namespace RhinoRoad.Core;

/// <summary>
/// The heading of every towed unit, carried forward along a route.
/// </summary>
/// <remarks>
/// A trailer's position is not a function of where the tractor is now — it is a function of where
/// the tractor has been. Two routes that arrive at the same pose from different directions leave the
/// trailer folded differently, so a towed unit cannot be stamped from a single route sample the way
/// a rigid body can. This carries that history as one heading per unit and advances it with the
/// tractor.
///
/// <para>
/// For a unit hitched a distance <c>h</c> from its tower's axle and trailing its own axle a distance
/// <c>L</c> behind that hitch, the no-slip condition at the towed axle gives, per unit of the
/// tower's travel:
/// </para>
/// <code>
/// dθ  = (ds·sin γ + dφ·h·cos γ) / L
/// ds' =  ds·cos γ - dφ·h·sin γ
/// </code>
/// <para>
/// where <c>γ</c> is the fold between tower and unit, <c>dφ</c> the tower's heading change and
/// <c>ds</c> its signed travel. The second line is what makes a chain work: each unit hands the next
/// one the travel measured at its own axle, so a dolly and the body it carries are the same
/// recursion applied twice.
/// </para>
/// </remarks>
public sealed class ArticulationChain
{
    /// <summary>
    /// Integration step. The relation above is exact only in the limit, and a trailer folding hard
    /// at low speed is where a coarse step drifts. Two centimetres is well inside the drafting
    /// tolerance everything downstream works to, and costs a few arithmetic operations per step.
    /// </summary>
    private const double SubStepMetres = 0.02;

    /// <summary>
    /// Cap on sub-steps for one route interval, so a route sampled sparsely down a long straight
    /// cannot turn a single interval into an unbounded loop. On a straight the fold decays anyway.
    /// </summary>
    private const int MaximumSubSteps = 512;

    private readonly IReadOnlyList<TowedUnitDefinition> _units;
    private readonly double[] _headings;

    private ArticulationChain(IReadOnlyList<TowedUnitDefinition> units, double[] headings)
    {
        _units = units;
        _headings = headings;
    }

    /// <summary>
    /// A chain standing straight behind the lead unit. This is how the official curve sheets start
    /// every manoeuvre, and it is the only starting fold a route can state without being told one.
    /// </summary>
    public static ArticulationChain StartAligned(VehicleDefinition vehicle, double leadHeadingRadians)
    {
        ArgumentNullException.ThrowIfNull(vehicle);
        var headings = new double[vehicle.TowedUnits.Count];
        Array.Fill(headings, Geometry2D.NormalizeAngle(leadHeadingRadians));
        return new ArticulationChain(vehicle.TowedUnits, headings);
    }

    public ArticulationChain Clone() => new(_units, (double[])_headings.Clone());

    public int Count => _units.Count;

    public IReadOnlyList<double> HeadingsRadians => _headings;

    /// <summary>
    /// Advances every unit over one route interval.
    /// </summary>
    /// <param name="leadHeadingBeforeRadians">Lead-unit heading at the start of the interval.</param>
    /// <param name="leadHeadingChangeRadians">
    /// Its change over the interval, as a raw difference rather than a difference of two normalised
    /// headings — the sub-stepping below has to be able to walk through a half turn.
    /// </param>
    /// <param name="signedDistanceMetres">
    /// Distance the lead axle travelled, negative when reversing. The sign is the whole reason a
    /// reversing trailer folds the way it does.
    /// </param>
    public void Advance(double leadHeadingBeforeRadians, double leadHeadingChangeRadians, double signedDistanceMetres)
    {
        if (_units.Count == 0) return;
        if (Math.Abs(signedDistanceMetres) <= Geometry2D.Epsilon &&
            Math.Abs(leadHeadingChangeRadians) <= Geometry2D.Epsilon)
        {
            return;
        }

        var steps = (int)Math.Ceiling(Math.Abs(signedDistanceMetres) / SubStepMetres);
        steps = Math.Clamp(Math.Max(steps, 1), 1, MaximumSubSteps);
        var leadStepDistance = signedDistanceMetres / steps;
        var leadStepHeading = leadHeadingChangeRadians / steps;
        var leadHeading = leadHeadingBeforeRadians;

        for (var step = 0; step < steps; step++)
        {
            var towerHeading = leadHeading;
            var headingChange = leadStepHeading;
            var distance = leadStepDistance;

            for (var index = 0; index < _units.Count; index++)
            {
                var unit = _units[index];
                var fold = Geometry2D.NormalizeAngle(towerHeading - _headings[index]);
                var sine = Math.Sin(fold);
                var cosine = Math.Cos(fold);
                var nextHeadingChange =
                    ((distance * sine) + (headingChange * unit.HitchOffsetMetres * cosine)) / unit.WheelbaseMetres;
                var nextDistance = (distance * cosine) - (headingChange * unit.HitchOffsetMetres * sine);

                towerHeading = _headings[index];
                _headings[index] = Geometry2D.NormalizeAngle(_headings[index] + nextHeadingChange);
                headingChange = nextHeadingChange;
                distance = nextDistance;
            }

            leadHeading += leadStepHeading;
        }
    }

    /// <summary>Fold between unit <paramref name="index"/> and whatever tows it.</summary>
    public double ArticulationAngleRadians(int index, double leadHeadingRadians) =>
        Geometry2D.NormalizeAngle((index == 0 ? leadHeadingRadians : _headings[index - 1]) - _headings[index]);

    /// <summary>Places every towed unit in the world, given where the lead unit's axle sits.</summary>
    public IReadOnlyList<TowedUnitPose> Poses(Point2 leadAxleCentreMetres, double leadHeadingRadians)
    {
        if (_units.Count == 0) return [];

        var poses = new TowedUnitPose[_units.Count];
        var towerAxle = leadAxleCentreMetres;
        var towerHeading = leadHeadingRadians;
        for (var index = 0; index < _units.Count; index++)
        {
            var unit = _units[index];
            var heading = _headings[index];
            var hitch = Geometry2D.Transform(new Point2(unit.HitchOffsetMetres, 0.0), towerAxle, towerHeading);
            var axle = Geometry2D.Transform(new Point2(-unit.WheelbaseMetres, 0.0), hitch, heading);
            var outline = unit.BodyOutline.Count == 0
                ? []
                : unit.BodyOutline.Select(point => Geometry2D.Transform(point, axle, heading)).ToArray();

            poses[index] = new TowedUnitPose(
                unit.Id,
                hitch,
                axle,
                heading,
                Geometry2D.NormalizeAngle(towerHeading - heading),
                outline);

            towerAxle = axle;
            towerHeading = heading;
        }

        return poses;
    }
}
