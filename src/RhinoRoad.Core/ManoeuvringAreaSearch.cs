using System.Diagnostics;

namespace RhinoRoad.Core;

/// <summary>
/// Finds low-area, drivable approaches into a fixed bay within a selected planning yard, for any
/// vehicle that can be docked (see <see cref="ReverseCapabilities.CanSearchArea"/>).
/// </summary>
/// <remarks>
/// Only this search runs against the clock: it is an explicit, interactive exploration whose
/// suggestions become ordinary controls. Each candidate is planned and replayed with the
/// deterministic machinery a saved journey uses, so a suggestion replays exactly however long the
/// search that found it was allowed to run.
/// </remarks>
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
        ArgumentNullException.ThrowIfNull(vehicle);
        ArgumentNullException.ThrowIfNull(mode);
        ArgumentNullException.ThrowIfNull(saved);
        if (!ReverseCapabilities.CanSearchArea(vehicle))
            throw new ArgumentException(
                "Area search needs a vehicle that can be reversed square into a bay; this one can only be aimed.",
                nameof(vehicle));
        if (firstAdjustableControl < 0 || firstAdjustableControl >= saved.Controls.Count)
            throw new ArgumentOutOfRangeException(nameof(firstAdjustableControl));
        if (saved.Controls[firstAdjustableControl].Direction != TravelDirection.Forward)
            throw new ArgumentException("The first adjustable control must be forward.", nameof(firstAdjustableControl));
        if (saved.Controls[^1] is not { Direction: TravelDirection.Reverse,
            Kind: ManoeuvreControlKind.Aim, ExitHeadingRadians: not null })
            throw new ArgumentException("The final control must reverse into a bay: a point and a heading.", nameof(saved));
        if (yard.Count < 3) throw new ArgumentException("Select a closed yard boundary.", nameof(yard));
        if (budget <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(budget));

        var prefixControls = saved.Controls.Take(firstAdjustableControl).ToArray();
        // The prefix is replayed rather than reconstructed, and the turn is planned from replay's
        // own leg boundary: the turn may ease the exit of the leg before it, exactly as it will when
        // the suggestion is replayed. Planned from the end of the prefix instead, candidates were
        // judged from poses a replay of them never reaches.
        var prefixReplay = ManoeuvreReplayService.Replay(vehicle, mode, saved with { Controls = prefixControls });
        var prefixRoute = prefixReplay.Samples.ToArray();
        var prefixState = prefixReplay.EndState;
        if (prefixState.Direction != TravelDirection.Forward)
            throw new ArgumentException("The adjustable section must start moving forward.", nameof(saved));

        var target = saved.Controls[^1];
        var original = saved.Controls[firstAdjustableControl].PositionMetres;
        var heading = prefixState.VehicleHeadingRadians;
        // The grid is laid out in the vehicle's own manoeuvre scale -- about 9 m for SVT, under 4 m
        // for a car -- so a car's turn-away is not searched in steps longer than its turning circle.
        var scale = ReversePlanner.ManoeuvreScaleMetres(vehicle, mode);
        var forward = new Point2(Math.Cos(heading), Math.Sin(heading));
        var left = new Point2(-forward.Y, forward.X);
        var watch = Stopwatch.StartNew();
        var candidates = new List<(Candidate Candidate, double X, double Y)>();
        var tried = 0;
        var outside = 0;
        var blocked = 0;
        var vehicleFailures = 0;
        Candidate? current = Evaluate(saved, out _, CancellationToken.None);
        if (current is not null) candidates.Add((current, 0.0, 0.0));

        // Coarse pass explores the turn-away point around its saved position. The reverse planner
        // tests direct backing, pull-ups, and one correction from each point. Refinement tests small
        // moves around the best areas, so a fixed grid does not dictate the final answer.
        foreach (var x in new[] { -1.5, -1.0, -0.5, 0.0, 0.5, 1.0, 1.5 })
        foreach (var y in new[] { -0.9, -0.45, 0.0, 0.45, 0.9 })
        {
            if (Expired()) break;
            SearchPoint(x * scale, y * scale);
        }
        foreach (var seed in candidates.OrderBy(item => item.Candidate.ClearAreaSquareMetres).Take(6).ToArray())
        foreach (var dx in new[] { -0.28, 0.0, 0.28 })
        foreach (var dy in new[] { -0.225, 0.0, 0.225 })
        {
            if (Expired()) break;
            SearchPoint(seed.X + (dx * scale), seed.Y + (dy * scale));
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
                    candidate.Manoeuvre.Controls[firstAdjustableControl].PositionMetres.XY) < 0.225 * scale
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

        // Checked between grid points; the token also reaches into each planner run and replay, so a
        // cancelled search stops within a docking plan rather than after the grid point in hand.
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
                    prefixReplay.LegStartIndex, prefixState, turn, cancellationToken);
                if (planned.RequestedAngleExceeded || planned.AimedBehindTheBeam)
                {
                    vehicleFailures++;
                    return;
                }
                if (planned.FromIndex < turnRoute.Count - 1)
                    turnRoute.RemoveRange(planned.FromIndex + 1, turnRoute.Count - planned.FromIndex - 1);
                // Where replay starts the leg after the turn: at the turn's own start, so a pull-up
                // can reshape its exit, unless the turn is sealed.
                var turnStartIndex = turnRoute.Count - 1;
                turnRoute.AddRange(planned.Samples.Skip(1));
                if (turn.SealsLeg) turnStartIndex = turnRoute.Count - 1;
                // The bay keeps the speed it was reversed into at, so a reverse saved at walking pace
                // is searched at walking pace.
                var reverse = ReversePlanner.Find(vehicle, mode, turnRoute,
                    turnStartIndex, planned.EndState, target.PositionMetres,
                    target.ExitHeadingRadians, target.SpeedKilometresPerHour, cancellationToken);
                var definition = saved with
                {
                    Controls = prefixControls.Concat(new[] { turn }).Concat(reverse.Controls).ToArray()
                };
                var candidate = Evaluate(definition, out var reason, cancellationToken);
                tried++;
                if (candidate is not null) candidates.Add((candidate, x, y));
                else Count(reason);
            }
            // A cancelled search is over rather than failed: the candidates found so far stand.
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
            catch (ArgumentException) { vehicleFailures++; }
            catch (InvalidOperationException) { vehicleFailures++; }
        }

        void Count(string reason)
        {
            if (reason == "yard") outside++;
            else if (reason == "obstacle") blocked++;
            else vehicleFailures++;
        }

        Candidate? Evaluate(ManoeuvreDefinition definition, out string reason, CancellationToken cancellation)
        {
            reason = "vehicle";
            var replay = ManoeuvreReplayService.Replay(vehicle, mode, definition, cancellation);
            if (replay.RequestedAngleExceeded) return null;
            var analysis = new VehicleAccessAnalyzer().Analyze(vehicle, mode, replay.Samples);
            var suffixStart = Math.Min(prefixRoute.Length - 1, analysis.Poses.Count - 1);
            var firstStation = replay.Samples[suffixStart].StationMetres;
            if (analysis.Violations.Any(item => item.StationMetres >= firstStation - 1e-6)) return null;
            // A rigid vehicle has nothing to fold. The analyzer already reports a fold past the limit
            // as a violation; this says the same thing in the search's own terms.
            if (vehicle.IsArticulated && FoldLimit.IsReached(vehicle, analysis.MaximumArticulationAnglesRadians))
                return null;
            var miss = ReverseBay.Miss(vehicle, replay.Samples, replay.EndState,
                target.PositionMetres.XY, target.ExitHeadingRadians);
            var positionError = miss.PositionMetres;
            var headingError = miss.HeadingRadians!.Value * 180.0 / Math.PI;
            if (positionError > ReversePlanner.ArrivalToleranceMetres
                || headingError > ReversePlanner.ArrivalToleranceDegrees) return null;
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
