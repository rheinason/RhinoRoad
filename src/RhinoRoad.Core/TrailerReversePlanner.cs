namespace RhinoRoad.Core;

/// <summary>
/// Searches a short forward pull-up followed by a trailer-led reverse. The result consists entirely
/// of ordinary controls, so it can be saved, replayed, and edited like a manually authored route.
/// </summary>
public static class TrailerReversePlanner
{
    /// <summary>How close the trailer axle must stop to the bay for the approach to count as reached.</summary>
    public const double ArrivalToleranceMetres = 0.25;

    /// <summary>How square, in degrees, the trailer must stop to a requested bay direction.</summary>
    public const double ArrivalToleranceDegrees = 2.0;

    public sealed record Proposal(
        IReadOnlyList<ManoeuvreControl> Controls,
        PlannedManoeuvreLeg Leg,
        double TrailerMissMetres,
        double? HeadingMissRadians,
        double MaximumFoldRadians,
        double PullUpMetres,
        bool ReachedTarget);

    public static Proposal Find(
        VehicleDefinition vehicle,
        DrivingModeDefinition mode,
        IReadOnlyList<RouteSample> route,
        int legStartIndex,
        VehicleState state,
        Point3 trailerTarget,
        double? exitMovementHeadingRadians = null)
    {
        ArgumentNullException.ThrowIfNull(vehicle);
        ArgumentNullException.ThrowIfNull(mode);
        ArgumentNullException.ThrowIfNull(route);
        if (vehicle.TowedUnits.Count != 1) throw new ArgumentException("A single semitrailer is required.", nameof(vehicle));
        if (route.Count == 0) throw new ArgumentException("A route is required.", nameof(route));
        if (state.Direction != TravelDirection.Forward) throw new ArgumentException("Start before changing to reverse.", nameof(state));

        Proposal? best = null;
        var heading = state.VehicleHeadingRadians;
        var forward = new Point2(Math.Cos(heading), Math.Sin(heading));
        var left = new Point2(-forward.Y, forward.X);

        // Include the current position: the search must never replace a good direct reverse with a
        // worse pull-up. Distances span the space needed to establish and then remove trailer fold.
        foreach (var distance in new[] { 0.0, 5.0, 10.0, 15.0, 20.0, 30.0 })
        foreach (var offset in distance == 0.0 ? new[] { 0.0 } : new[] { -5.0, 0.0, 5.0 })
        {
            ManoeuvreControl? pullControl = null;
            PlannedManoeuvreLeg? pull = null;
            IReadOnlyList<RouteSample> prefix = route;
            var pullEnd = state;
            var fromIndex = route.Count - 1;

            if (distance > 0.0)
            {
                var point = state.RearAxleCentreMetres.XY + (forward * distance) + (left * offset);
                pullControl = new ManoeuvreControl(
                    new Point3(point.X, point.Y, state.RearAxleCentreMetres.Z), TravelDirection.Forward);
                pull = ManoeuvreReplayService.PlanControl(vehicle, mode, route, legStartIndex, state, pullControl);
                if (pull.RequestedAngleExceeded || pull.AimedBehindTheBeam) continue;
                fromIndex = pull.FromIndex;
                prefix = route.Take(fromIndex + 1).Concat(pull.Samples.Skip(1)).ToArray();
                pullEnd = pull.EndState;
            }

            var reverseControl = new ManoeuvreControl(
                trailerTarget, TravelDirection.Reverse, ManoeuvreControlKind.Aim, exitMovementHeadingRadians);
            var reverse = ManoeuvreReplayService.PlanControl(
                vehicle, mode, prefix, prefix.Count - 1,
                pullEnd with { Direction = TravelDirection.Reverse }, reverseControl);
            var combined = pull is null
                ? reverse.Samples
                : pull.Samples.Concat(reverse.Samples.Skip(1)).ToArray();
            var candidateRoute = route.Take(fromIndex + 1).Concat(combined.Skip(1)).ToArray();
            var chain = ArticulationTrace.AtEndOf(vehicle, candidateRoute)!;
            var finalPose = chain.Poses(reverse.EndState.RearAxleCentreMetres.XY,
                reverse.EndState.VehicleHeadingRadians)[^1];
            var miss = finalPose.AxleCentreMetres.DistanceTo(trailerTarget.XY);
            var headingMiss = exitMovementHeadingRadians is double exit
                ? Math.Abs(Geometry2D.NormalizeAngle(finalPose.HeadingRadians - exit - Math.PI))
                : (double?)null;
            var maximumFold = 0.0;
            foreach (var step in ArticulationTrace.Follow(vehicle, candidateRoute))
                maximumFold = Math.Max(maximumFold,
                    Math.Abs(step.Chain!.ArticulationAngleRadians(0, step.VehicleHeadingRadians)));
            if (maximumFold >= 65.0 * Math.PI / 180.0) continue;

            var controls = pullControl is null
                ? new[] { reverseControl }
                : new[] { pullControl, reverseControl };
            var leg = new PlannedManoeuvreLeg(fromIndex, combined, reverse.EndState,
                reverse.RequestedAngleExceeded);
            var proposal = new Proposal(controls, leg, miss, headingMiss, maximumFold,
                pullEnd.StationMetres - state.StationMetres,
                Arrived(reverse.RequestedAngleExceeded, miss, headingMiss));
            if (best is null || Score(proposal) < Score(best)) best = proposal;
        }

        if (best is null) throw new InvalidOperationException("No drivable reverse candidate was found.");
        if (best.ReachedTarget) return best;

        // A short forward correction can unfold the trailer after the first reverse has stopped
        // short. Search only from the best initial approach: this is a bounded second pass, not an
        // unbounded chain of shunts that might wander across the site.
        // Corrections extend the first approach, never an earlier correction that has since become best.
        var initial = best;
        var firstRoute = route.Take(initial.Leg.FromIndex + 1).Concat(initial.Leg.Samples.Skip(1)).ToArray();
        var correctionStart = initial.Leg.EndState with { Direction = TravelDirection.Forward };
        var correctionForward = new Point2(Math.Cos(correctionStart.VehicleHeadingRadians),
            Math.Sin(correctionStart.VehicleHeadingRadians));
        var correctionLeft = new Point2(-correctionForward.Y, correctionForward.X);
        foreach (var distance in new[] { 5.0, 10.0, 15.0 })
        foreach (var offset in new[] { -4.0, 0.0, 4.0 })
        {
            var point = correctionStart.RearAxleCentreMetres.XY
                + (correctionForward * distance) + (correctionLeft * offset);
            var correctionControl = new ManoeuvreControl(
                new Point3(point.X, point.Y, correctionStart.RearAxleCentreMetres.Z),
                TravelDirection.Forward);
            var correction = ManoeuvreReplayService.PlanControl(vehicle, mode, firstRoute,
                firstRoute.Length - 1, correctionStart, correctionControl);
            if (correction.RequestedAngleExceeded || correction.AimedBehindTheBeam) continue;
            var correctedRoute = firstRoute.Take(correction.FromIndex + 1)
                .Concat(correction.Samples.Skip(1)).ToArray();
            var finalControl = new ManoeuvreControl(trailerTarget, TravelDirection.Reverse,
                ManoeuvreControlKind.Aim, exitMovementHeadingRadians);
            var final = ManoeuvreReplayService.PlanControl(vehicle, mode, correctedRoute,
                correctedRoute.Length - 1,
                correction.EndState with { Direction = TravelDirection.Reverse }, finalControl);
            var completeRoute = correctedRoute.Concat(final.Samples.Skip(1)).ToArray();
            var chain = ArticulationTrace.AtEndOf(vehicle, completeRoute)!;
            var pose = chain.Poses(final.EndState.RearAxleCentreMetres.XY,
                final.EndState.VehicleHeadingRadians)[^1];
            var miss = pose.AxleCentreMetres.DistanceTo(trailerTarget.XY);
            var headingMiss = exitMovementHeadingRadians is double exit
                ? Math.Abs(Geometry2D.NormalizeAngle(pose.HeadingRadians - exit - Math.PI))
                : (double?)null;
            var maximumFold = 0.0;
            foreach (var step in ArticulationTrace.Follow(vehicle, completeRoute))
                maximumFold = Math.Max(maximumFold,
                    Math.Abs(step.Chain!.ArticulationAngleRadians(0, step.VehicleHeadingRadians)));
            if (maximumFold >= 65.0 * Math.PI / 180.0) continue;

            var combined = initial.Leg.Samples.Concat(correction.Samples.Skip(1))
                .Concat(final.Samples.Skip(1)).ToArray();
            var leg = new PlannedManoeuvreLeg(initial.Leg.FromIndex, combined,
                final.EndState, final.RequestedAngleExceeded);
            var proposal = new Proposal(
                initial.Controls.Concat(new[] { correctionControl, finalControl }).ToArray(),
                leg, miss, headingMiss, maximumFold,
                initial.PullUpMetres + correction.EndState.StationMetres - correctionStart.StationMetres,
                Arrived(final.RequestedAngleExceeded, miss, headingMiss));
            if (Score(proposal) < Score(best)) best = proposal;
        }

        return best;
    }

    // The same tolerance the area search accepts, so a proposal reported as reaching the bay is
    // one that search also treats as a passing journey.
    private static bool Arrived(bool angleExceeded, double missMetres, double? headingMissRadians) =>
        !angleExceeded && missMetres <= ArrivalToleranceMetres
        && (headingMissRadians ?? 0.0) <= ArrivalToleranceDegrees * Math.PI / 180.0;

    private static double Score(Proposal proposal) =>
        (proposal.ReachedTarget ? 0.0 : 1000.0)
        + (proposal.TrailerMissMetres * 20.0)
        + ((proposal.HeadingMissRadians ?? 0.0) * 40.0)
        + (proposal.MaximumFoldRadians * 2.0)
        + (proposal.PullUpMetres * 0.1)
        + (proposal.Controls.Count > 2 ? 10.0 : 0.0);
}
