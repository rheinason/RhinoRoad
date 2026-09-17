using System.Diagnostics;

namespace RhinoRoad.Core;

/// <summary>Finds low-area, drivable SVT approaches within a selected planning yard.</summary>
public static class ManoeuvringAreaSearch
{
    public sealed record Candidate(
        ManoeuvreDefinition Manoeuvre,
        IReadOnlyList<RouteSample> Route,
        SweptRegion Clearance,
        double ClearAreaSquareMetres,
        double PositionErrorMetres,
        double HeadingErrorDegrees,
        int Corrections,
        double TravelMetres,
        Point2 LimitingPointMetres);

    public sealed record Result(IReadOnlyList<Candidate> Suggestions, Candidate? Current,
        int Tried, int OutsideYard, int ObstacleConflicts, int VehicleFailures,
        bool TimeLimitReached);

    public static Result Find(VehicleDefinition vehicle, DrivingModeDefinition mode,
        ManoeuvreDefinition saved, int firstAdjustableControl,
        IReadOnlyList<Point2> yard, IReadOnlyList<IReadOnlyList<Point2>> fixedObstacles,
        double clearanceMetres, TimeSpan budget, CancellationToken cancellationToken = default)
    {
        if (vehicle.Id != "SVT" || vehicle.TowedUnits.Count != 1)
            throw new ArgumentException("Area search currently supports SVT.", nameof(vehicle));
        if (firstAdjustableControl < 0 || firstAdjustableControl >= saved.Controls.Count)
            throw new ArgumentOutOfRangeException(nameof(firstAdjustableControl));
        if (saved.Controls[firstAdjustableControl].Direction != TravelDirection.Forward)
            throw new ArgumentException("The first adjustable control must be forward.", nameof(firstAdjustableControl));
        if (saved.Controls[^1] is not { Direction: TravelDirection.Reverse,
            Kind: ManoeuvreControlKind.Aim, ExitHeadingRadians: not null })
            throw new ArgumentException("The final control must reverse the trailer to a point and heading.", nameof(saved));
        if (yard.Count < 3) throw new ArgumentException("Select a closed yard boundary.", nameof(yard));
        if (budget <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(budget));

        var prefixControls = saved.Controls.Take(firstAdjustableControl).ToArray();
        var prefix = saved with { Controls = prefixControls };
        var prefixRoute = prefixControls.Length == 0
            ? new[] { ManoeuvreReplayService.StateSample(saved.StartState, vehicle) }
            : ManoeuvreReplayService.Replay(vehicle, mode, prefix).Samples.ToArray();
        var prefixState = prefixControls.Length == 0 ? saved.StartState
            : ManoeuvreReplayService.Replay(vehicle, mode, prefix).EndState;
        if (prefixState.Direction != TravelDirection.Forward)
            throw new ArgumentException("The adjustable section must start moving forward.", nameof(saved));

        var target = saved.Controls[^1];
        var original = saved.Controls[firstAdjustableControl].PositionMetres;
        var heading = prefixState.VehicleHeadingRadians;
        var forward = new Point2(Math.Cos(heading), Math.Sin(heading));
        var left = new Point2(-forward.Y, forward.X);
        var watch = Stopwatch.StartNew();
        var candidates = new List<(Candidate Candidate, double X, double Y)>();
        var tried = 0;
        var outside = 0;
        var blocked = 0;
        var vehicleFailures = 0;
        Candidate? current = Evaluate(saved, out _);
        if (current is not null) candidates.Add((current, 0.0, 0.0));

        // Coarse pass explores the turn-away point around its saved position. The reverse planner
        // tests direct backing, pull-ups, and one correction from each point. Refinement tests small
        // moves around the best areas, so a fixed grid does not dictate the final answer.
        foreach (var x in new[] { -15.0, -10.0, -5.0, 0.0, 5.0, 10.0, 15.0 })
        foreach (var y in new[] { -8.0, -4.0, 0.0, 4.0, 8.0 })
        {
            if (Expired()) break;
            SearchPoint(x, y);
        }
        foreach (var seed in candidates.OrderBy(item => item.Candidate.ClearAreaSquareMetres).Take(6).ToArray())
        foreach (var dx in new[] { -2.5, 0.0, 2.5 })
        foreach (var dy in new[] { -2.0, 0.0, 2.0 })
        {
            if (Expired()) break;
            SearchPoint(seed.X + dx, seed.Y + dy);
        }

        var suggestions = candidates.OrderBy(item => item.Candidate.ClearAreaSquareMetres)
            .ThenBy(item => item.Candidate.Corrections)
            .ThenBy(item => item.Candidate.TravelMetres)
            .Select(item => item.Candidate).ToArray();
        var distinct = new List<Candidate>();
        foreach (var candidate in suggestions)
        {
            if (distinct.Any(previous =>
                previous.Manoeuvre.Controls[firstAdjustableControl].PositionMetres.XY.DistanceTo(
                    candidate.Manoeuvre.Controls[firstAdjustableControl].PositionMetres.XY) < 2.0
                && previous.Corrections == candidate.Corrections)) continue;
            distinct.Add(candidate);
            if (distinct.Count == 3) break;
        }
        var suggestionsWithLimits = distinct.Select(candidate => candidate with
        {
            LimitingPointMetres = LimitingPoint(candidate.Clearance, yard, fixedObstacles)
        }).ToArray();
        return new Result(suggestionsWithLimits, current, tried, outside, blocked, vehicleFailures,
            watch.Elapsed >= budget);

        bool Expired() => cancellationToken.IsCancellationRequested || watch.Elapsed >= budget;

        void SearchPoint(double x, double y)
        {
            if (Expired()) return;
            var point = original.XY + (forward * x) + (left * y);
            var turn = saved.Controls[firstAdjustableControl] with
            {
                PositionMetres = new Point3(point.X, point.Y, original.Z)
            };
            try
            {
                var turnRoute = prefixRoute.ToList();
                var planned = ManoeuvreReplayService.PlanControl(vehicle, mode, turnRoute,
                    turnRoute.Count - 1, prefixState, turn);
                if (planned.RequestedAngleExceeded || planned.AimedBehindTheBeam)
                {
                    vehicleFailures++;
                    return;
                }
                if (planned.FromIndex < turnRoute.Count - 1)
                    turnRoute.RemoveRange(planned.FromIndex + 1, turnRoute.Count - planned.FromIndex - 1);
                turnRoute.AddRange(planned.Samples.Skip(1));
                var reverse = TrailerReversePlanner.Find(vehicle, mode, turnRoute,
                    prefixRoute.Length - 1, planned.EndState, target.PositionMetres,
                    target.ExitHeadingRadians);
                var definition = saved with
                {
                    Controls = prefixControls.Concat(new[] { turn }).Concat(reverse.Controls).ToArray()
                };
                var candidate = Evaluate(definition, out var reason);
                tried++;
                if (candidate is not null) candidates.Add((candidate, x, y));
                else Count(reason);
            }
            catch (ArgumentException) { vehicleFailures++; }
            catch (InvalidOperationException) { vehicleFailures++; }
        }

        void Count(string reason)
        {
            if (reason == "yard") outside++;
            else if (reason == "obstacle") blocked++;
            else vehicleFailures++;
        }

        Candidate? Evaluate(ManoeuvreDefinition definition, out string reason)
        {
            reason = "vehicle";
            var replay = ManoeuvreReplayService.Replay(vehicle, mode, definition);
            if (replay.RequestedAngleExceeded) return null;
            var analysis = new VehicleAccessAnalyzer().Analyze(vehicle, mode, replay.Samples);
            var suffixStart = Math.Min(prefixRoute.Length - 1, analysis.Poses.Count - 1);
            var firstStation = replay.Samples[suffixStart].StationMetres;
            if (analysis.Violations.Any(item => item.StationMetres >= firstStation - 1e-6)) return null;
            if (analysis.MaximumArticulationAnglesRadians.Any(fold => fold >= 65.0 * Math.PI / 180.0)) return null;
            var chain = ArticulationTrace.AtEndOf(vehicle, replay.Samples)!;
            var trailer = chain.Poses(replay.EndState.RearAxleCentreMetres.XY,
                replay.EndState.VehicleHeadingRadians)[^1];
            var positionError = trailer.AxleCentreMetres.DistanceTo(target.PositionMetres.XY);
            var headingError = Math.Abs(Geometry2D.NormalizeAngle(
                trailer.HeadingRadians - target.ExitHeadingRadians!.Value - Math.PI)) * 180.0 / Math.PI;
            if (positionError > TrailerReversePlanner.ArrivalToleranceMetres
                || headingError > TrailerReversePlanner.ArrivalToleranceDegrees) return null;
            var body = SweptRegionBuilder.FromPoses(analysis.Poses);
            if (!body.IsSuccess) return null;
            var clearance = SweptRegionBuilder.Inflate(body.Region!, clearanceMetres);
            if (!clearance.IsSuccess) return null;
            if (AccessFootprint.Outside(clearance.Region!, new[] { yard }) is not null)
            {
                reason = "yard";
                return null;
            }
            if (fixedObstacles.Any(obstacle => AccessFootprint.ConflictPoint(
                clearance.Region!, obstacle, true) is not null))
            {
                reason = "obstacle";
                return null;
            }
            var area = AccessFootprint.IntersectionArea(clearance.Region!, yard);
            var adjusted = definition.Controls.Skip(firstAdjustableControl).ToArray();
            var corrections = Enumerable.Range(1, adjusted.Length - 1)
                .Count(index => adjusted[index - 1].Direction == TravelDirection.Reverse
                    && adjusted[index].Direction == TravelDirection.Forward);
            var travel = replay.EndState.StationMetres - firstStation;
            return new Candidate(definition, replay.Samples, clearance.Region!, area, positionError,
                headingError, corrections, travel, clearance.Region!.OuterBoundary[0]);
        }
    }

    private static Point2 LimitingPoint(SweptRegion clearance, IReadOnlyList<Point2> yard,
        IReadOnlyList<IReadOnlyList<Point2>> obstacles)
    {
        var best = clearance.OuterBoundary[0];
        var distance = double.PositiveInfinity;
        foreach (var point in clearance.OuterBoundary)
        foreach (var polygon in new[] { yard }.Concat(obstacles))
        for (var i = 0; i < polygon.Count; i++)
        {
            var gap = Geometry2D.DistancePointToSegment(point, polygon[i], polygon[(i + 1) % polygon.Count]);
            if (gap < distance) { distance = gap; best = point; }
        }
        return best;
    }
}
