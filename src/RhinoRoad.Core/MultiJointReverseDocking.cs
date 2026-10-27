namespace RhinoRoad.Core;

/// <summary>
/// Reverses a combination with more than one articulation joint — PVT's lorry, drawbar dolly and
/// trailer body — onto a point, or onto a dock square to a heading, by planning the whole leg in
/// simulation rather than by steering it with a feedback law alone.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="TrailerReverseGenerator"/> docks a semitrailer with a cascade: the approach line sets a
/// curvature for the trailer, the curvature a fold, the fold a wheel angle. On two joints the outer
/// heading loop and the folds it commands cannot be separated in speed, and every gain pairing either
/// crawled, wandered off or ran into the fold stop. What the cascade does well is the inner part —
/// given a curvature for the last unit, it holds every fold to the value that delivers it — and that
/// part is kept. What is replaced is the outer loop: instead of feeding back where the trailer is,
/// the curvature asked of it is planned in advance as a profile over its own travel, and the profile
/// is chosen by driving the combination through it in simulation and correcting it until the trailer
/// arrives.
/// </para>
/// <para>
/// The simulation is the route model itself: every step goes through
/// <see cref="RateLimitedTrajectoryGenerator.Advance"/>, so lock and slew rate are honoured, and the
/// chain is advanced exactly as <see cref="ArticulationTrace.Follow"/> advances it from the samples
/// produced. That second point is not a nicety. Reversing, a trailer's response to a given tractor
/// path is unstable, so two integrations of the same samples that differ in the last bit — distance
/// taken from the step length rather than from the sample positions, say, which sometimes rounds to a
/// different number of sub-steps — part company exponentially: measured on PVT, by metres and to a
/// jackknife within 40 m. Advanced the way the analyzer and replay advance it, the trailer the planner
/// steers is the trailer everything downstream sees.
/// </para>
/// <para>
/// The profile is piecewise linear in a handful of knots. It starts from the curvature of a cubic
/// Hermite curve between the trailer's pose and the dock's, and is corrected by Levenberg–Marquardt
/// on the arrival errors with a barrier on fold. Everything is fixed — the starting curves, the
/// iteration cap and a rollout budget — so the same request plans the same leg every time, which
/// replay depends on, and no clock is consulted.
/// </para>
/// <para>
/// Nothing in the planning depends on the number of joints, and a semitrailer is planned here too
/// when <see cref="TrailerReverseGenerator.DockTowedUnitInAvailableSpace"/>'s cascade misses: in
/// mode A the wheel takes 12.5 m to reach its lock, the cascade's fold loop closes over about three,
/// and a plan that knows the lag in advance docks where feedback chasing it folds into the stop.
/// <see cref="Handles"/> still names only the chains that come here first.
/// </para>
/// </remarks>
public static class MultiJointReverseDocking
{
    /// <summary>
    /// The largest fold the inner cascade will ask any joint for, as a share of that unit's fold
    /// limit — 55 degrees against the default 65, as in the single-joint law.
    /// </summary>
    private const double CommandedShareOfFoldLimit = 55.0 / 65.0;

    // A leg stops, as for a semitrailer, at each unit's own fold limit
    // (TowedUnitDefinition.MaximumArticulationDegrees): short of the right angle, at the fold a
    // driver would stop and pull forward from. The leg returned never contains a sample past it.

    /// <summary>Fold from which the search starts paying for it — gently, because tight docks need it.</summary>
    private const double SoftFoldRadians = 50.0 * Math.PI / 180.0;

    /// <summary>Fold from which the barrier steepens a hundredfold, keeping plans clear of the stop.</summary>
    private const double HardFoldRadians = 60.0 * Math.PI / 180.0;

    /// <summary>
    /// The search's own stop, further out than the leg's. Stopping candidates at the leg's stop would
    /// give the search a cliff with no slope to descend; letting them run on, penalised, shows it the
    /// way back. Still short of the right angle past which the model means nothing.
    /// </summary>
    private const double SearchFoldStopRadians = 85.0 * Math.PI / 180.0;

    /// <summary>Shortest step taken while closing on the target station, as in the single-joint law.</summary>
    private const double MinimumStepMetres = 0.05;

    // Residual scales: each error is measured in units small against the arrival tolerance, so the
    // search weighs a centimetre of offset and a fraction of a degree of yaw about equally.
    private const double CrossScaleMetres = 0.05;
    private const double HeadingScaleRadians = 0.5 * Math.PI / 180.0;

    /// <summary>
    /// When only a point is asked for the arrival heading is free, and is held only loosely to the
    /// natural one so the search has something to settle on among the many that arrive.
    /// </summary>
    private const double AimHeadingScaleRadians = 15.0 * Math.PI / 180.0;

    private const double FoldScaleRadians = 2.0 * Math.PI / 180.0;

    /// <summary>
    /// What counts as done for the search: well inside the arrival tolerance, so a plan reported as
    /// reached is not one that only scrapes in.
    /// </summary>
    private const double ConvergedCrossMetres = 0.08;
    private const double ConvergedHeadingRadians = 0.8 * Math.PI / 180.0;

    /// <summary>
    /// Knots in the curvature profile. Eight spread over the trailer's run is room for the shapes a
    /// dock needs — establish a fold, swing, take it out, square up — and few enough that a correction
    /// step costs nine rollouts. Ten and twelve were measured and did no better.
    /// </summary>
    private const int KnotCount = 8;

    private const int MaximumIterationsPerStart = 40;
    private const int DampingAttempts = 10;
    private const double FiniteDifference = 1e-4;

    /// <summary>
    /// Rollouts allowed for one plan, across every start. A rollout of a 40 m leg is a few tenths of
    /// a millisecond, so this bounds an impossible request — the only kind that spends it — to around
    /// a tenth of a second, and a request that succeeds typically stops after a few dozen.
    /// </summary>
    private const int RolloutBudget = 900;

    /// <summary>
    /// Where the search starts, tried in order until one converges: the inner fold gain, and how far
    /// the Hermite curve's tangents reach. The gain matters more than its size suggests. At the
    /// single-joint law's 0.35 the tightest docks fold too fast to recover; at 0.30 every measured
    /// case from 18 m back docked; 0.25 rescues a few that 0.30 does not.
    /// </summary>
    private static readonly (double FoldGain, double TangentScale)[] Starts =
    [
        (0.30, 1.0), (0.30, 0.6), (0.30, 1.5), (0.25, 1.0), (0.35, 1.0), (0.25, 0.6), (0.25, 1.5)
    ];

    /// <summary>
    /// Whether this planner is the one to use first: any chain of more than one joint. A single
    /// joint comes here only as the fallback for a missed cascade.
    /// </summary>
    public static bool Handles(VehicleDefinition vehicle)
    {
        ArgumentNullException.ThrowIfNull(vehicle);
        return vehicle.TowedUnits.Count > 1;
    }

    /// <summary>
    /// Reverses until the last towed unit reaches <paramref name="targetMetres"/> square to
    /// <paramref name="exitMovementHeadingRadians"/> — the same request, and the same result shape,
    /// as <see cref="TrailerReverseGenerator.DockTowedUnitInAvailableSpace"/>.
    /// </summary>
    /// <param name="exitMovementHeadingRadians">
    /// The direction of travel at the end; reversing, the trailer faces the opposite way.
    /// </param>
    /// <returns>
    /// The leg and the chain it ends in. <see cref="ReverseLeg.ReachedTarget"/> is true only when the
    /// trailer's axle arrives within <see cref="TrailerReversePlanner.ArrivalToleranceMetres"/> of the
    /// approach line at the target's station and within
    /// <see cref="TrailerReversePlanner.ArrivalToleranceDegrees"/> of square. Otherwise the leg is the
    /// best drivable attempt found, ending at the target's station or where a fold reached the stop.
    /// </returns>
    public static ReverseLeg Dock(
        VehicleDefinition vehicle,
        DrivingModeDefinition mode,
        VehicleState start,
        ArticulationChain chain,
        Point2 targetMetres,
        double exitMovementHeadingRadians,
        double maximumStepMetres = 0.10,
        bool wheelSetAtStandstill = false)
    {
        Validate(vehicle, mode, chain, maximumStepMetres);
        return Plan(vehicle, mode, start, chain, targetMetres, exitMovementHeadingRadians, docking: true,
            maximumStepMetres, wheelSetAtStandstill);
    }

    /// <summary>
    /// Reverses until the last towed unit's axle reaches <paramref name="targetMetres"/>, arriving at
    /// whatever heading suits — the same request, and the same result shape, as
    /// <see cref="TrailerReverseGenerator.AimTowedUnit"/>.
    /// </summary>
    public static ReverseLeg Aim(
        VehicleDefinition vehicle,
        DrivingModeDefinition mode,
        VehicleState start,
        ArticulationChain chain,
        Point2 targetMetres,
        double maximumStepMetres = 0.10,
        bool wheelSetAtStandstill = false)
    {
        Validate(vehicle, mode, chain, maximumStepMetres);
        var trailer = TrailerAxle(vehicle, start.RearAxleCentreMetres.XY, start.VehicleHeadingRadians,
            chain.HeadingsRadians, out var heading);

        // The arrival heading is free, so one is chosen for the search to hold loosely: that of the
        // single arc leaving the trailer along its own axis and passing through the point, which is
        // the turn with the least steering in it. An arc turns through twice the bearing it is aimed
        // at; beyond a quarter turn it is clamped, since a point that far round is not reversed to.
        var travel = Geometry2D.NormalizeAngle(heading + Math.PI);
        var bearing = Geometry2D.NormalizeAngle(
            Math.Atan2(targetMetres.Y - trailer.Y, targetMetres.X - trailer.X) - travel);
        var arrival = travel + Math.Clamp(2.0 * bearing, -0.5 * Math.PI, 0.5 * Math.PI);
        return Plan(vehicle, mode, start, chain, targetMetres, arrival, docking: false,
            maximumStepMetres, wheelSetAtStandstill);
    }

    private static void Validate(
        VehicleDefinition vehicle, DrivingModeDefinition mode, ArticulationChain chain, double maximumStepMetres)
    {
        ArgumentNullException.ThrowIfNull(vehicle);
        ArgumentNullException.ThrowIfNull(mode);
        ArgumentNullException.ThrowIfNull(chain);
        if (!vehicle.IsArticulated) throw new ArgumentException("The vehicle tows nothing.", nameof(vehicle));
        if (maximumStepMetres <= 0.0) throw new ArgumentOutOfRangeException(nameof(maximumStepMetres));
    }

    private static ReverseLeg Plan(
        VehicleDefinition vehicle,
        DrivingModeDefinition mode,
        VehicleState start,
        ArticulationChain chain,
        Point2 target,
        double arrivalTravelHeading,
        bool docking,
        double maximumStep,
        bool standstill)
    {
        var problem = new Problem(vehicle, mode, start, chain, target,
            Geometry2D.NormalizeAngle(arrivalTravelHeading + Math.PI), docking, maximumStep, standstill);
        var trailer = TrailerAxle(vehicle, start.RearAxleCentreMetres.XY, start.VehicleHeadingRadians,
            chain.HeadingsRadians, out var trailerHeading);

        double[]? best = null;
        Outcome? bestOutcome = null;
        var bestSpacing = 0.0;
        var bestGain = 0.0;
        foreach (var (gain, scale) in Starts)
        {
            var (knots, spacing) = WarmStart(trailer, trailerHeading + Math.PI, target, arrivalTravelHeading, scale);
            problem.KnotSpacingMetres = spacing;
            problem.FoldGain = gain;
            var (solved, outcome) = Solve(problem, knots);
            if (bestOutcome is null || Better(outcome, bestOutcome))
            {
                best = solved;
                bestOutcome = outcome;
                bestSpacing = spacing;
                bestGain = gain;
            }

            if (bestOutcome.Converged || problem.Rollouts >= RolloutBudget) break;
        }

        problem.KnotSpacingMetres = bestSpacing;
        problem.FoldGain = bestGain;
        return problem.Rollout(best!, record: true).Leg!;
    }

    private static bool Better(Outcome candidate, Outcome incumbent) =>
        (candidate.Converged && !incumbent.Converged)
        || (candidate.Converged == incumbent.Converged && Cost(candidate.Residuals) < Cost(incumbent.Residuals));

    /// <summary>
    /// A first guess at the profile: the curvature, against arc length, of a cubic Hermite curve
    /// from the trailer's axle to the target, leaving and arriving along the directions of travel.
    /// It ignores the lag between asking a trailer for a curvature and getting it, which is exactly
    /// what the correction that follows is for.
    /// </summary>
    private static (double[] Knots, double Spacing) WarmStart(
        Point2 from, double fromTravel, Point2 to, double toTravel, double tangentScale)
    {
        var reach = from.DistanceTo(to) * tangentScale;
        var t0 = new Point2(Math.Cos(fromTravel), Math.Sin(fromTravel)) * reach;
        var t1 = new Point2(Math.Cos(toTravel), Math.Sin(toTravel)) * reach;
        const int Samples = 400;
        var arc = new double[Samples + 1];
        var curvature = new double[Samples + 1];
        var previous = from;
        for (var index = 0; index <= Samples; index++)
        {
            var t = (double)index / Samples;
            var point = (from * ((2 * t * t * t) - (3 * t * t) + 1)) + (t0 * ((t * t * t) - (2 * t * t) + t))
                + (to * ((-2 * t * t * t) + (3 * t * t))) + (t1 * ((t * t * t) - (t * t)));
            var d1 = (from * ((6 * t * t) - (6 * t))) + (t0 * ((3 * t * t) - (4 * t) + 1))
                + (to * ((-6 * t * t) + (6 * t))) + (t1 * ((3 * t * t) - (2 * t)));
            var d2 = (from * ((12 * t) - 6)) + (t0 * ((6 * t) - 4)) + (to * ((-12 * t) + 6)) + (t1 * ((6 * t) - 2));
            var speed = d1.Length;
            curvature[index] = speed < 1e-9 ? 0.0 : ((d1.X * d2.Y) - (d1.Y * d2.X)) / (speed * speed * speed);
            arc[index] = index == 0 ? 0.0 : arc[index - 1] + point.DistanceTo(previous);
            previous = point;
        }

        var knots = new double[KnotCount];
        var spacing = Math.Max(arc[^1] / (KnotCount - 1), 0.5);
        var cursor = 0;
        for (var index = 0; index < KnotCount; index++)
        {
            var station = index * spacing;
            while (cursor < Samples && arc[cursor + 1] < station) cursor++;
            // The cascade takes curvature per unit of travel along the unit's own axis. Reversing,
            // the unit travels against that axis, so the curve's travel curvature changes sign.
            knots[index] = -curvature[cursor];
        }

        return (knots, spacing);
    }

    /// <summary>
    /// Levenberg–Marquardt with forward-difference Jacobians. A step is kept only if it lowers the
    /// cost, so a rollout that hits a discontinuity — a fold stop, a crossing that moves — can only
    /// be rejected, never followed.
    /// </summary>
    private static (double[] Parameters, Outcome Outcome) Solve(Problem problem, double[] initial)
    {
        var parameters = (double[])initial.Clone();
        var current = problem.Rollout(parameters, record: false);
        var cost = Cost(current.Residuals);
        var damping = 1e-2;
        var n = parameters.Length;
        for (var iteration = 0; iteration < MaximumIterationsPerStart; iteration++)
        {
            if (current.Converged || problem.Rollouts >= RolloutBudget) break;
            var m = current.Residuals.Length;
            var jacobian = new double[m, n];
            for (var j = 0; j < n; j++)
            {
                var probe = (double[])parameters.Clone();
                probe[j] += FiniteDifference;
                var perturbed = problem.Rollout(probe, record: false).Residuals;
                for (var i = 0; i < m; i++) jacobian[i, j] = (perturbed[i] - current.Residuals[i]) / FiniteDifference;
            }

            var normal = new double[n, n];
            var gradient = new double[n];
            for (var a = 0; a < n; a++)
            {
                for (var i = 0; i < m; i++) gradient[a] -= jacobian[i, a] * current.Residuals[i];
                for (var b = 0; b < n; b++)
                {
                    var sum = 0.0;
                    for (var i = 0; i < m; i++) sum += jacobian[i, a] * jacobian[i, b];
                    normal[a, b] = sum;
                }
            }

            var improved = false;
            for (var attempt = 0; attempt < DampingAttempts; attempt++)
            {
                var system = (double[,])normal.Clone();
                for (var a = 0; a < n; a++) system[a, a] += damping * (normal[a, a] + 1.0);
                var step = SolveLinear(system, gradient);
                var candidate = new double[n];
                for (var a = 0; a < n; a++) candidate[a] = parameters[a] + step[a];
                var outcome = problem.Rollout(candidate, record: false);
                var candidateCost = Cost(outcome.Residuals);
                if (candidateCost < cost)
                {
                    parameters = candidate;
                    current = outcome;
                    cost = candidateCost;
                    damping = Math.Max(damping / 3.0, 1e-7);
                    improved = true;
                    break;
                }

                damping *= 4.0;
            }

            if (!improved) break;
        }

        return (parameters, current);
    }

    private static double Cost(double[] residuals)
    {
        var sum = 0.0;
        foreach (var value in residuals) sum += value * value;
        return sum;
    }

    /// <summary>Gaussian elimination with partial pivoting; the systems are eight by eight.</summary>
    private static double[] SolveLinear(double[,] matrix, double[] rhs)
    {
        var n = rhs.Length;
        var a = (double[,])matrix.Clone();
        var b = (double[])rhs.Clone();
        for (var column = 0; column < n; column++)
        {
            var pivot = column;
            for (var row = column + 1; row < n; row++)
            {
                if (Math.Abs(a[row, column]) > Math.Abs(a[pivot, column])) pivot = row;
            }

            if (pivot != column)
            {
                for (var k = 0; k < n; k++) (a[column, k], a[pivot, k]) = (a[pivot, k], a[column, k]);
                (b[column], b[pivot]) = (b[pivot], b[column]);
            }

            var diagonal = a[column, column];
            if (Math.Abs(diagonal) < 1e-300) return new double[n];
            for (var row = column + 1; row < n; row++)
            {
                var factor = a[row, column] / diagonal;
                if (factor == 0.0) continue;
                for (var k = column; k < n; k++) a[row, k] -= factor * a[column, k];
                b[row] -= factor * b[column];
            }
        }

        var x = new double[n];
        for (var row = n - 1; row >= 0; row--)
        {
            var sum = b[row];
            for (var k = row + 1; k < n; k++) sum -= a[row, k] * x[k];
            x[row] = sum / a[row, row];
        }

        return x;
    }

    /// <summary>
    /// The last unit's axle and heading, by the same construction as
    /// <see cref="ArticulationChain.Poses"/> without the body outlines it would allocate every step.
    /// </summary>
    private static Point2 TrailerAxle(
        VehicleDefinition vehicle, Point2 leadAxle, double leadHeading, IReadOnlyList<double> headings, out double heading)
    {
        var axle = leadAxle;
        var towerHeading = leadHeading;
        for (var index = 0; index < vehicle.TowedUnits.Count; index++)
        {
            var unit = vehicle.TowedUnits[index];
            var hitch = Geometry2D.Transform(new Point2(unit.HitchOffsetMetres, 0.0), axle, towerHeading);
            towerHeading = headings[index];
            axle = Geometry2D.Transform(new Point2(-unit.WheelbaseMetres, 0.0), hitch, towerHeading);
        }

        heading = towerHeading;
        return axle;
    }

    private sealed record Outcome(double[] Residuals, bool Converged, ReverseLeg? Leg);

    /// <summary>One planning request: everything a rollout needs except the profile being tried.</summary>
    private sealed class Problem(
        VehicleDefinition vehicle,
        DrivingModeDefinition mode,
        VehicleState start,
        ArticulationChain chain,
        Point2 target,
        double facing,
        bool docking,
        double maximumStep,
        bool standstill)
    {
        private readonly Point2 _tangent = new(Math.Cos(facing), Math.Sin(facing));
        private readonly Point2 _normal = new(-Math.Sin(facing), Math.Cos(facing));

        /// <summary>Trailer travel between knots.</summary>
        public double KnotSpacingMetres { get; set; } = 5.0;

        /// <summary>How hard each joint is driven to the fold its curvature asks for, per metre.</summary>
        public double FoldGain { get; set; } = 0.30;

        public int Rollouts { get; private set; }

        private double Profile(double[] knots, double arc)
        {
            var position = arc / KnotSpacingMetres;
            if (position <= 0.0) return knots[0];
            var index = (int)Math.Floor(position);
            if (index >= knots.Length - 1) return knots[^1];
            var fraction = position - index;
            return knots[index] + ((knots[index + 1] - knots[index]) * fraction);
        }

        /// <summary>
        /// Drives the combination through a profile until the trailer reaches the target's station
        /// on the approach line. <paramref name="record"/> distinguishes the leg handed back from the
        /// search's trial runs: only it keeps samples, and only it stops at the fold a driver would.
        /// </summary>
        public Outcome Rollout(double[] knots, bool record)
        {
            Rollouts++;
            var state = start;
            var working = chain.Clone();
            var seed = Seed(state);
            var samples = record ? new List<RouteSample> { seed } : null;

            // The chain is advanced from the samples, exactly as ArticulationTrace.Follow advances
            // it — see the remarks on the class for why nothing less will do.
            var traceHeading = ArticulationTrace.VehicleHeadingRadians(seed);
            var tracePosition = seed.PositionMetres.XY;
            var travelled = 0.0;
            var arc = 0.0;
            var foldPenalty = 0.0;
            var closest = double.PositiveInfinity;
            var limit = (3.0 * TrailerAxle(vehicle, tracePosition, traceHeading, working.HeadingsRadians, out _)
                .DistanceTo(target)) + 30.0;
            Point2 previousAxle = default;
            double previousAlong = 0.0, previousCross = 0.0, previousHeading = 0.0;
            var first = true;
            double cross = 0.0, headingError = 0.0, along = 0.0;
            var crossed = false;
            var folded = false;
            var largestFold = 0.0;
            while (travelled < limit)
            {
                var axle = TrailerAxle(vehicle, tracePosition, traceHeading, working.HeadingsRadians, out var trailerHeading);
                var offset = axle - target;
                along = (offset.X * _tangent.X) + (offset.Y * _tangent.Y);
                cross = (offset.X * _normal.X) + (offset.Y * _normal.Y);
                headingError = Geometry2D.NormalizeAngle(trailerHeading - facing);
                closest = Math.Min(closest, axle.DistanceTo(target));
                if (!first) arc += axle.DistanceTo(previousAxle);

                if (along <= 0.0)
                {
                    crossed = true;
                    if (!record && !first && previousAlong > 0.0)
                    {
                        // Measured where the station was crossed rather than at the step that
                        // crossed it, so the errors are continuous in the profile instead of jumping
                        // whenever a change moves the crossing by a step. The leg handed back reports
                        // where it really stopped.
                        var fraction = previousAlong / (previousAlong - along);
                        cross = previousCross + ((cross - previousCross) * fraction);
                        headingError = previousHeading
                            + (Geometry2D.NormalizeAngle(headingError - previousHeading) * fraction);
                    }

                    break;
                }

                var maximumFold = 0.0;
                for (var index = 0; index < working.Count; index++)
                {
                    var fold = Math.Abs(working.ArticulationAngleRadians(index, traceHeading));
                    maximumFold = Math.Max(maximumFold, fold);
                    var soft = Math.Max(0.0, fold - SoftFoldRadians);
                    var hard = Math.Max(0.0, fold - HardFoldRadians);
                    foldPenalty += (soft * soft) + (100.0 * hard * hard);
                }

                largestFold = Math.Max(largestFold, maximumFold);
                if (record ? FoldsPastLimit(working, traceHeading) : maximumFold >= SearchFoldStopRadians)
                {
                    folded = true;
                    break;
                }

                previousAxle = axle;
                previousAlong = along;
                previousCross = cross;
                previousHeading = headingError;
                first = false;

                var steering = SteeringFor(state, traceHeading, working, Profile(knots, arc));
                var step = Math.Min(maximumStep, Math.Max(along, MinimumStepMetres));
                var advanced = RateLimitedTrajectoryGenerator.Advance(
                    vehicle, mode, state, steering, step, standstill && travelled == 0.0);
                var sampleHeading = ArticulationTrace.VehicleHeadingRadians(advanced.Sample);
                var samplePosition = advanced.Sample.PositionMetres.XY;
                var before = record ? working.Clone() : null;
                working.Advance(
                    traceHeading,
                    Geometry2D.NormalizeAngle(sampleHeading - traceHeading),
                    samplePosition.DistanceTo(tracePosition) * (double)advanced.Sample.Direction);
                if (before is not null && FoldsPastLimit(working, sampleHeading))
                {
                    // The leg ends on the last sample short of the stop, not the first one past it.
                    working = before;
                    folded = true;
                    break;
                }

                traceHeading = sampleHeading;
                tracePosition = samplePosition;
                state = advanced.State;
                samples?.Add(advanced.Sample);
                travelled += step;
            }

            var residuals = new[]
            {
                cross / CrossScaleMetres,
                headingError / (docking ? HeadingScaleRadians : AimHeadingScaleRadians),
                crossed ? 0.0 : Math.Max(along, 0.0) / CrossScaleMetres,
                (Math.Sqrt(foldPenalty * maximumStep) / FoldScaleRadians) + (folded ? 100.0 : 0.0)
            };
            var converged = crossed && !folded && largestFold < HardFoldRadians
                && Math.Abs(cross) <= ConvergedCrossMetres
                && (!docking || Math.Abs(headingError) <= ConvergedHeadingRadians);
            ReverseLeg? leg = null;
            if (record)
            {
                var reached = crossed && !folded
                    && Math.Abs(cross) <= TrailerReversePlanner.ArrivalToleranceMetres
                    && (!docking || Math.Abs(headingError)
                        <= TrailerReversePlanner.ArrivalToleranceDegrees * Math.PI / 180.0);
                leg = new ReverseLeg(new GeneratedTrajectory(state, samples!), working, reached, closest)
                {
                    HeadingErrorRadians = docking ? headingError : null
                };
            }

            return new Outcome(residuals, converged, leg);
        }

        private bool FoldsPastLimit(ArticulationChain chain, double leadHeading)
        {
            for (var index = 0; index < chain.Count; index++)
            {
                if (Math.Abs(chain.ArticulationAngleRadians(index, leadHeading))
                    >= vehicle.TowedUnits[index].MaximumArticulationRadians) return true;
            }

            return false;
        }

        /// <summary>
        /// The single-joint law's cascade, unchanged but for its gain: the wheel angle that gives the
        /// last unit the curvature asked of it, worked back through every joint.
        /// </summary>
        private double SteeringFor(
            VehicleState state, double leadHeading, ArticulationChain working, double desiredCurvature)
        {
            var desired = desiredCurvature;
            var directionSign = (double)state.Direction;
            for (var index = vehicle.TowedUnits.Count - 1; index >= 0; index--)
            {
                var unit = vehicle.TowedUnits[index];
                var fold = working.ArticulationAngleRadians(index, leadHeading);
                var commandedLimit = unit.MaximumArticulationRadians * CommandedShareOfFoldLimit;
                var commandedFold = Math.Clamp(
                    Math.Atan(desired * unit.WheelbaseMetres),
                    -commandedLimit,
                    commandedLimit);
                var denominator = 1.0 - (unit.HitchOffsetMetres * Math.Cos(fold) / unit.WheelbaseMetres);
                desired = ((Math.Sin(fold) / unit.WheelbaseMetres)
                    + (directionSign * FoldGain * (commandedFold - fold))) / denominator;
            }

            return Math.Clamp(
                Math.Atan(vehicle.WheelbaseMetres * desired),
                -mode.MaximumWheelAngleRadians,
                mode.MaximumWheelAngleRadians);
        }

        private RouteSample Seed(VehicleState state)
        {
            var pathHeading = Geometry2D.NormalizeAngle(
                state.VehicleHeadingRadians + (state.Direction == TravelDirection.Reverse ? Math.PI : 0.0));
            var curvature = (double)state.Direction * Math.Tan(state.SteeringAngleRadians) / vehicle.WheelbaseMetres;
            return new RouteSample(state.StationMetres, state.RearAxleCentreMetres, pathHeading, curvature, state.Direction);
        }
    }
}
