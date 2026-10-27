namespace RhinoRoad.Core;

/// <summary>A stretch of the alignment holding one curvature, and what it has to fit.</summary>
public sealed record AlignmentRun(
    double StartStationMetres,
    double EndStationMetres,
    double CurvaturePerMetre,
    double RequiredTransitionMetres)
{
    public double LengthMetres => EndStationMetres - StartStationMetres;

    /// <summary>Whether the wheel can be moved into and out of this run within its length.</summary>
    public bool Fits => LengthMetres >= RequiredTransitionMetres;

    public double RadiusMetres => Math.Abs(CurvaturePerMetre) <= Geometry2D.Epsilon
        ? double.PositiveInfinity
        : 1.0 / Math.Abs(CurvaturePerMetre);
}

/// <summary>A place where the drawn curvature jumps, and the transition a vehicle would need there.</summary>
public sealed record CurvatureJoin(double StationMetres, double RequiredTransitionMetres, int SteeringRateViolations);

/// <summary>
/// What a drawn alignment costs the steering wheel, and whether it has room for it.
/// </summary>
/// <remarks>
/// <para>
/// An alignment drawn as arcs joined straight onto tangents asks the wheel to change angle
/// instantly at every join, which no vehicle can do. Reporting that as a violation at each join is
/// true but useless: it says the drawing is wrong without saying what would make it right.
/// </para>
/// <para>
/// What makes it right is a transition — a stretch where the wheel slews from one angle to the
/// next — and its length is not a matter of taste. Turning a radius R needs the wheel at
/// atan(wheelbase / R), the mode fixes how fast the wheel moves and how fast the vehicle does, so
/// the distance follows:
/// </para>
/// <code>
///     length = speed · |atan(wheelbase / R2) − atan(wheelbase / R1)| / slewRate
/// </code>
/// <para>
/// Each stretch of constant curvature has to be long enough for the transitions at both its ends,
/// because the wheel has to arrive at that curvature and then leave it. A tangent between two bends
/// is the usual place this fails: it must hold the run-out of one and the run-in of the next.
/// </para>
/// </remarks>
public static class AlignmentTransitions
{
    /// <summary>A curvature change smaller than this is not a join, it is sampling noise.</summary>
    private const double CurvatureStepPerMetre = 1e-4;

    /// <summary>The distance needed to slew the wheel between two curvatures.</summary>
    public static double RequiredLengthMetres(
        VehicleDefinition vehicle,
        DrivingModeDefinition mode,
        double fromCurvaturePerMetre,
        double toCurvaturePerMetre)
    {
        ArgumentNullException.ThrowIfNull(vehicle);
        ArgumentNullException.ThrowIfNull(mode);
        var from = Math.Atan(vehicle.WheelbaseMetres * fromCurvaturePerMetre);
        var to = Math.Atan(vehicle.WheelbaseMetres * toCurvaturePerMetre);
        return Math.Abs(to - from) * mode.SpeedMetresPerSecond / mode.MaximumSteeringRateRadiansPerSecond;
    }

    /// <summary>
    /// Breaks a sampled alignment into its constant-curvature stretches and prices each one.
    /// </summary>
    /// <remarks>
    /// The two ends of the alignment are treated as straight, because the vehicle arrives at the
    /// first stretch and leaves the last one with the wheel wherever it already was — there is no
    /// alignment left to hold a transition.
    /// </remarks>
    public static IReadOnlyList<AlignmentRun> Runs(
        VehicleDefinition vehicle,
        DrivingModeDefinition mode,
        IReadOnlyList<RouteSample> samples)
    {
        ArgumentNullException.ThrowIfNull(mode);
        ArgumentNullException.ThrowIfNull(samples);
        if (samples.Count < 2) return [];

        var boundaries = new List<int> { 0 };
        for (var index = 1; index < samples.Count; index++)
        {
            if (Math.Abs(samples[index].SignedCurvaturePerMetre - samples[index - 1].SignedCurvaturePerMetre)
                > CurvatureStepPerMetre)
            {
                boundaries.Add(index);
            }
        }

        boundaries.Add(samples.Count - 1);

        var runs = new List<AlignmentRun>();
        for (var segment = 0; segment < boundaries.Count - 1; segment++)
        {
            var from = boundaries[segment];
            var to = boundaries[segment + 1];
            if (to <= from) continue;

            // The curvature held through the stretch, read just inside it so a boundary sample
            // cannot be mistaken for the value the stretch actually carries.
            var curvature = samples[Math.Min(from + 1, to)].SignedCurvaturePerMetre;
            var before = segment == 0 ? 0.0 : samples[Math.Max(from - 1, 0)].SignedCurvaturePerMetre;
            var after = segment == boundaries.Count - 2
                ? 0.0
                : samples[Math.Min(to + 1, samples.Count - 1)].SignedCurvaturePerMetre;

            // Priced at the speed the stretch was driven at, which is the mode's for any curve and
            // for a driven leg with no speed of its own.
            var stretchMode = mode.AtSpeed(samples[Math.Min(from + 1, to)].SpeedKilometresPerHour);
            var required =
                RequiredLengthMetres(vehicle, stretchMode, before, curvature) +
                RequiredLengthMetres(vehicle, stretchMode, curvature, after);

            runs.Add(new AlignmentRun(
                samples[from].StationMetres,
                samples[to].StationMetres,
                curvature,
                required));
        }

        return runs;
    }

    /// <summary>How far from a join a steering-rate violation may sit and still be that join's.</summary>
    public const double JoinToleranceMetres = 1.0;

    /// <summary>
    /// The joins between adjacent constant-curvature runs that carry a steering-rate violation.
    /// </summary>
    /// <remarks>
    /// A drawn arc meeting a tangent changes curvature in no distance at all, so the analysis
    /// reports the wheel moving at an impossible rate exactly there. That violation is inherent to
    /// the drawing, not a sign the vehicle cannot make the turn, and the report should say so
    /// rather than list it beside a claim that every stretch has room.
    /// </remarks>
    public static IReadOnlyList<CurvatureJoin> JoinsWithRateViolations(
        VehicleDefinition vehicle,
        DrivingModeDefinition mode,
        IReadOnlyList<AlignmentRun> runs,
        IReadOnlyList<AnalysisViolation> violations)
    {
        ArgumentNullException.ThrowIfNull(runs);
        ArgumentNullException.ThrowIfNull(violations);
        var joins = new List<CurvatureJoin>();
        for (var index = 1; index < runs.Count; index++)
        {
            var station = runs[index].StartStationMetres;
            var count = violations.Count(violation =>
                violation.Kind == ViolationKind.SteeringRate &&
                Math.Abs(violation.StationMetres - station) <= JoinToleranceMetres);
            if (count == 0) continue;
            joins.Add(new CurvatureJoin(
                station,
                RequiredLengthMetres(vehicle, mode, runs[index - 1].CurvaturePerMetre, runs[index].CurvaturePerMetre),
                count));
        }

        return joins;
    }

    /// <summary>
    /// The sentence that explains those joins to a reader, in the current culture's number format.
    /// </summary>
    /// <param name="joins">The result of <see cref="JoinsWithRateViolations"/>; must not be empty.</param>
    /// <param name="everyStretchFits">Whether every run is long enough to hold the transitions at its ends.</param>
    public static string DescribeJoins(IReadOnlyList<CurvatureJoin> joins, bool everyStretchFits)
    {
        ArgumentNullException.ThrowIfNull(joins);
        if (joins.Count == 0) throw new ArgumentException("There are no joins to describe.", nameof(joins));
        var stations = string.Join(", ", joins.Select(join => $"{join.StationMetres:0.0}"));
        var shortest = joins.Min(join => join.RequiredTransitionMetres);
        var longest = joins.Max(join => join.RequiredTransitionMetres);
        var needed = Math.Abs(longest - shortest) < 0.05
            ? $"{longest:0.0} m"
            : $"{shortest:0.0} to {longest:0.0} m";
        var lead =
            $"The drawn line changes curvature instantly at {joins.Count} join(s) (stations {stations} m); " +
            $"a vehicle needs a transition there of {needed}";
        return everyStretchFits
            ? lead + " — every stretch is long enough to fit one, so easing the joins with transitions would remove these steering-rate violations."
            : lead + ", but some stretches are too short to hold one (listed below).";
    }
}
