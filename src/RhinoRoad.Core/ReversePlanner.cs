namespace RhinoRoad.Core;

/// <summary>
/// Searches forward pull-ups followed by a reverse into a bay, for any vehicle. The result consists
/// entirely of ordinary controls, so it can be saved, replayed, and edited like a manually authored
/// route.
/// </summary>
/// <remarks>
/// <para>
/// The bay is the docking body's axle — the last trailer's for a combination, the rear axle for a
/// rigid vehicle (see <see cref="ReverseBay"/>) — and the reverse into it is whatever
/// <see cref="ReverseDocking"/> drives for the vehicle. A vehicle that cannot be docked is aimed at
/// the point instead; the proposal then says the heading was not honoured, and judges arrival by
/// position alone.
/// </para>
/// <para>
/// The search is bounded by a count of candidates, never by time: a proposal becomes saved controls,
/// and the same request has to produce the same proposal on every machine.
/// </para>
/// </remarks>
public static class ReversePlanner
{
    /// <summary>How close the docking axle must stop to the bay for the approach to count as reached.</summary>
    public const double ArrivalToleranceMetres = 0.25;

    /// <summary>How square, in degrees, the docking body must stop to a requested bay direction.</summary>
    public const double ArrivalToleranceDegrees = 2.0;

    /// <summary>
    /// The most reverse legs one search plans, pull-ups and corrections together. A deterministic
    /// budget rather than a time limit, so the proposal never depends on how fast the machine is.
    /// </summary>
    public const int MaximumCandidates = 60;

    /// <param name="MissMetres">How far the docking axle stopped from the bay.</param>
    /// <param name="HeadingMissRadians">How far off square it stopped; null when no heading was asked for.</param>
    /// <param name="MaximumFoldRadians">The largest fold at any joint along the route; zero for a rigid vehicle.</param>
    public sealed record Proposal(
        IReadOnlyList<ManoeuvreControl> Controls,
        PlannedManoeuvreLeg Leg,
        double MissMetres,
        double? HeadingMissRadians,
        double MaximumFoldRadians,
        double PullUpMetres,
        bool ReachedTarget)
    {
        /// <summary>
        /// False when a heading was asked for but this vehicle cannot be docked, so the reverse was
        /// aimed at the point and <see cref="ReachedTarget"/> judges position alone. Say so rather
        /// than presenting the heading miss as a near dock.
        /// </summary>
        public bool HeadingHonoured { get; init; } = true;
    }

    /// <summary>
    /// The length the pull-up search is laid out in for this vehicle: the larger of its wheelbase,
    /// its rear-axle radius at full lock and, for a combination, the tightest radius it can hold
    /// continuously. Pull-ups are there to give the reverse room to establish a turn and take it out
    /// again, and that room scales with how tightly the vehicle turns — a car needs a few metres, a
    /// semitrailer a trailer's length.
    /// </summary>
    public static double ManoeuvreScaleMetres(VehicleDefinition vehicle, DrivingModeDefinition mode)
    {
        var turning = TurningGeometryCalculator.AtFullLock(vehicle, mode);
        return Math.Max(vehicle.WheelbaseMetres,
            Math.Max(turning.RearAxleRadiusMetres, turning.MinimumSustainableRearAxleRadiusMetres));
    }

    /// <summary>
    /// Plans a forward pull-up (or none), a reverse to <paramref name="axleTarget"/>, and when that
    /// does not arrive, one forward correction and a second reverse.
    /// </summary>
    /// <param name="axleTarget">
    /// Where the docking body's axle should stop. A bay picked by the rear of the body converts with
    /// <see cref="ReverseBay.AxleFromRear"/>.
    /// </param>
    /// <param name="exitMovementHeadingRadians">The direction of travel into the bay, or null for a point.</param>
    /// <param name="speedKilometresPerHour">
    /// The speed every planned leg is driven at, carried on each control it proposes; null for the
    /// mode's speed (see <see cref="ManoeuvreControl.SpeedKilometresPerHour"/>).
    /// </param>
    /// <param name="cancellationToken">
    /// Abandons the search with <see cref="OperationCanceledException"/>, checked between candidates
    /// and inside each docking plan. It only ever stops the search: a search that is not cancelled
    /// tries the same candidates in the same order and proposes the same approach.
    /// </param>
    /// <returns>
    /// The best approach found. Its last control seals its leg
    /// (<see cref="ManoeuvreControl.SealsLeg"/>), so the control after it neither rewinds into the
    /// planned arrival while it is driven nor when it is replayed.
    /// </returns>
    public static Proposal Find(
        VehicleDefinition vehicle,
        DrivingModeDefinition mode,
        IReadOnlyList<RouteSample> route,
        int legStartIndex,
        VehicleState state,
        Point3 axleTarget,
        double? exitMovementHeadingRadians = null,
        double? speedKilometresPerHour = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(vehicle);
        ArgumentNullException.ThrowIfNull(mode);
        ArgumentNullException.ThrowIfNull(route);
        ArgumentNullException.ThrowIfNull(state);
        if (route.Count == 0) throw new ArgumentException("A route is required.", nameof(route));
        if (state.Direction != TravelDirection.Forward) throw new ArgumentException("Start before changing to reverse.", nameof(state));
        // Checked here, so a bad speed is refused as the request it is rather than from inside a leg.
        var swing = mode.CentreToLockMetres(speedKilometresPerHour);

        var scale = ManoeuvreScaleMetres(vehicle, mode);
        var honoured = exitMovementHeadingRadians is null || ReverseDocking.CanDock(vehicle);
        var budget = MaximumCandidates;
        var heading = state.VehicleHeadingRadians;
        var forward = new Point2(Math.Cos(heading), Math.Sin(heading));
        var left = new Point2(-forward.Y, forward.X);

        // Pull-ups are tried on a grid laid out in the vehicle's own manoeuvre scale, then refined
        // twice around the best -- coarse first, so a fixed grid does not dictate the answer, and
        // bounded, so the refinement cannot wander. Distances span the space needed to establish and
        // then remove a turn; the current position is included, so the search never replaces a good
        // direct reverse with a worse pull-up.
        Proposal? best = null;
        (double Distance, double Offset) bestAt = (0.0, 0.0);
        Consider(0.0, 0.0);
        foreach (var distance in new[] { 0.5, 1.0, 1.5, 2.0, 2.5, 3.5 })
        foreach (var offset in new[] { -0.5, 0.0, 0.5 })
            Consider(distance * scale, offset * scale);
        // The scale is the size of the turn, but a turn also takes the distance the wheel needs to
        // get to its lock and back, and that is set by the slew rate and the speed rather than by
        // the vehicle's shape. In mode B it is small against the scale (4.2 m from centre to lock at
        // 5 km/h); in mode A it is not (12.5 m at 15 km/h), and MVT in mode A, whose reverse has to
        // swing two joints round with the wheel taking three times as far to follow, does not
        // arrive from any pull-up within 3.5 scales -- measured, the nearest missed by 2.4 m and 48
        // degrees -- and arrives from 5. So the grid is carried on past the turn by one and two
        // wheel swings, which reaches that far in mode A and adds little in mode B, or in either
        // mode at a crawl. The swing is measured at the speed asked for (the mode's when none is),
        // as it was before a reverse defaulted to the manoeuvring speed; keeping it keeps the grid.
        foreach (var extra in new[] { 1.0, 2.0 })
        foreach (var offset in new[] { -0.5, 0.0, 0.5 })
            Consider((3.5 * scale) + (extra * swing), offset * scale);
        foreach (var step in new[] { 0.25, 0.125 })
        {
            var centre = bestAt;
            foreach (var dd in new[] { -step, 0.0, step })
            foreach (var dy in new[] { -step, 0.0, step })
            {
                if (dd == 0.0 && dy == 0.0) continue;
                var distance = centre.Distance + (dd * scale);
                // A pull-up of no length is the direct reverse, which has no sideways freedom.
                if (distance < (0.5 * step * scale)) continue;
                Consider(distance, centre.Offset + (dy * scale));
            }
        }

        if (best is null) throw new InvalidOperationException("No drivable reverse candidate was found.");
        if (best.ReachedTarget) return Sealed(best);

        // A short forward correction can straighten the vehicle out after the first reverse has
        // stopped short. Search only from the best initial approach: this is a bounded second pass,
        // not an unbounded chain of shunts that might wander across the site. Corrections extend the
        // first approach, never an earlier correction that has since become best.
        var initial = best;
        var firstRoute = route.Take(initial.Leg.FromIndex + 1).Concat(initial.Leg.Samples.Skip(1)).ToArray();
        var correctionStart = initial.Leg.EndState with { Direction = TravelDirection.Forward };
        var correctionForward = new Point2(Math.Cos(correctionStart.VehicleHeadingRadians),
            Math.Sin(correctionStart.VehicleHeadingRadians));
        var correctionLeft = new Point2(-correctionForward.Y, correctionForward.X);
        Proposal? bestCorrection = null;
        (double Distance, double Offset) bestCorrectionAt = (0.0, 0.0);
        foreach (var distance in new[] { 0.5, 1.0, 1.5 })
        foreach (var offset in new[] { -0.4, 0.0, 0.4 })
            Correct(distance * scale, offset * scale);
        if (bestCorrection is not null && !best.ReachedTarget)
        {
            var around = bestCorrectionAt;
            foreach (var dd in new[] { -0.25, 0.0, 0.25 })
            foreach (var dy in new[] { -0.25, 0.0, 0.25 })
            {
                if (dd == 0.0 && dy == 0.0) continue;
                var distance = around.Distance + (dd * scale);
                if (distance < 0.1 * scale) continue;
                Correct(distance, around.Offset + (dy * scale));
            }
        }

        return Sealed(best);

        void Consider(double distance, double offset)
        {
            if (budget <= 0) return;
            cancellationToken.ThrowIfCancellationRequested();
            ManoeuvreControl? pullControl = null;
            PlannedManoeuvreLeg? pull = null;
            IReadOnlyList<RouteSample> prefix = route;
            var pullEnd = state;
            var fromIndex = route.Count - 1;

            if (distance > 0.0)
            {
                var point = state.RearAxleCentreMetres.XY + (forward * distance) + (left * offset);
                pullControl = new ManoeuvreControl(
                    new Point3(point.X, point.Y, state.RearAxleCentreMetres.Z), TravelDirection.Forward,
                    SpeedKilometresPerHour: speedKilometresPerHour);
                pull = ManoeuvreReplayService.PlanControl(vehicle, mode, route, legStartIndex, state, pullControl,
                    cancellationToken);
                if (pull.RequestedAngleExceeded || pull.AimedBehindTheBeam) return;
                fromIndex = pull.FromIndex;
                prefix = route.Take(fromIndex + 1).Concat(pull.Samples.Skip(1)).ToArray();
                pullEnd = pull.EndState;
            }

            budget--;
            var reverseControl = new ManoeuvreControl(
                axleTarget, TravelDirection.Reverse, ManoeuvreControlKind.Aim, exitMovementHeadingRadians,
                SpeedKilometresPerHour: speedKilometresPerHour);
            var reverse = ManoeuvreReplayService.PlanControl(
                vehicle, mode, prefix, prefix.Count - 1,
                pullEnd with { Direction = TravelDirection.Reverse }, reverseControl, cancellationToken);
            var combined = pull is null
                ? reverse.Samples
                : pull.Samples.Concat(reverse.Samples.Skip(1)).ToArray();
            var candidateRoute = route.Take(fromIndex + 1).Concat(combined.Skip(1)).ToArray();
            var fold = FoldLimit.Measure(vehicle, candidateRoute);
            if (fold.LimitReached) return;
            var miss = ReverseBay.Miss(vehicle, candidateRoute, reverse.EndState, axleTarget.XY,
                exitMovementHeadingRadians);

            var controls = pullControl is null
                ? new[] { reverseControl }
                : new[] { pullControl, reverseControl };
            var leg = new PlannedManoeuvreLeg(fromIndex, combined, reverse.EndState,
                reverse.RequestedAngleExceeded, ExitHeadingUnavailable: reverse.ExitHeadingUnavailable);
            var proposal = new Proposal(controls, leg, miss.PositionMetres, miss.HeadingRadians,
                fold.MaximumRadians, pullEnd.StationMetres - state.StationMetres,
                Arrived(reverse.RequestedAngleExceeded, miss.PositionMetres, honoured ? miss.HeadingRadians : null))
            {
                HeadingHonoured = honoured
            };
            if (best is null || Score(proposal) < Score(best))
            {
                best = proposal;
                bestAt = (distance, offset);
            }
        }

        void Correct(double distance, double offset)
        {
            if (budget <= 0) return;
            cancellationToken.ThrowIfCancellationRequested();
            var point = correctionStart.RearAxleCentreMetres.XY
                + (correctionForward * distance) + (correctionLeft * offset);
            var correctionControl = new ManoeuvreControl(
                new Point3(point.X, point.Y, correctionStart.RearAxleCentreMetres.Z),
                TravelDirection.Forward, SpeedKilometresPerHour: speedKilometresPerHour);
            var correction = ManoeuvreReplayService.PlanControl(vehicle, mode, firstRoute,
                firstRoute.Length - 1, correctionStart, correctionControl, cancellationToken);
            if (correction.RequestedAngleExceeded || correction.AimedBehindTheBeam) return;
            budget--;
            var correctedRoute = firstRoute.Take(correction.FromIndex + 1)
                .Concat(correction.Samples.Skip(1)).ToArray();
            var finalControl = new ManoeuvreControl(axleTarget, TravelDirection.Reverse,
                ManoeuvreControlKind.Aim, exitMovementHeadingRadians,
                SpeedKilometresPerHour: speedKilometresPerHour);
            var final = ManoeuvreReplayService.PlanControl(vehicle, mode, correctedRoute,
                correctedRoute.Length - 1,
                correction.EndState with { Direction = TravelDirection.Reverse }, finalControl,
                cancellationToken);
            var completeRoute = correctedRoute.Concat(final.Samples.Skip(1)).ToArray();
            var fold = FoldLimit.Measure(vehicle, completeRoute);
            if (fold.LimitReached) return;
            var miss = ReverseBay.Miss(vehicle, completeRoute, final.EndState, axleTarget.XY,
                exitMovementHeadingRadians);

            var combined = initial.Leg.Samples.Concat(correction.Samples.Skip(1))
                .Concat(final.Samples.Skip(1)).ToArray();
            var leg = new PlannedManoeuvreLeg(initial.Leg.FromIndex, combined,
                final.EndState, final.RequestedAngleExceeded,
                ExitHeadingUnavailable: final.ExitHeadingUnavailable);
            var proposal = new Proposal(
                initial.Controls.Concat(new[] { correctionControl, finalControl }).ToArray(),
                leg, miss.PositionMetres, miss.HeadingRadians, fold.MaximumRadians,
                initial.PullUpMetres + correction.EndState.StationMetres - correctionStart.StationMetres,
                Arrived(final.RequestedAngleExceeded, miss.PositionMetres, honoured ? miss.HeadingRadians : null))
            {
                HeadingHonoured = honoured
            };
            if (bestCorrection is null || Score(proposal) < Score(bestCorrection))
            {
                bestCorrection = proposal;
                bestCorrectionAt = (distance, offset);
            }

            if (Score(proposal) < Score(best!)) best = proposal;
        }
    }

    /// <summary>
    /// The proposal with its last control sealed. Sealing changes nothing about the proposal's own
    /// route -- it says only what the control after it may do -- so it is applied once the search is
    /// over rather than to every candidate.
    /// </summary>
    private static Proposal Sealed(Proposal proposal)
    {
        var controls = proposal.Controls.ToArray();
        controls[^1] = controls[^1] with { SealsLeg = true };
        return proposal with { Controls = controls };
    }

    // The same tolerance the area search accepts, so a proposal reported as reaching the bay is
    // one that search also treats as a passing journey.
    private static bool Arrived(bool angleExceeded, double missMetres, double? headingMissRadians) =>
        !angleExceeded && missMetres <= ArrivalToleranceMetres
        && (headingMissRadians ?? 0.0) <= ArrivalToleranceDegrees * Math.PI / 180.0;

    private static double Score(Proposal proposal) =>
        (proposal.ReachedTarget ? 0.0 : 1000.0)
        + (proposal.MissMetres * 20.0)
        + ((proposal.HeadingMissRadians ?? 0.0) * 40.0)
        + (proposal.MaximumFoldRadians * 2.0)
        + (proposal.PullUpMetres * 0.1)
        + (proposal.Controls.Count > 2 ? 10.0 : 0.0);
}
