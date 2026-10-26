namespace RhinoRoad.Core;

/// <summary>
/// Something that sticks out past a unit's body outline: a mirror, a step, a lamp.
/// </summary>
/// <param name="Outline">Closed polygon in the owning unit's frame (axle at the origin, heading +X), metres.</param>
/// <param name="MinimumHeightMetres">Underside above the road, if known. Recorded for later vertical checks.</param>
/// <param name="MaximumHeightMetres">Top above the road, if known. Recorded for later vertical checks.</param>
/// <remarks>
/// The plan-view sweep treats every protrusion as occupying its outline whatever its height: the
/// height band is data for a future low-obstacle check, not yet a filter.
/// </remarks>
public sealed record ProtrusionDefinition(
    string Id,
    string Name,
    IReadOnlyList<Point2> Outline,
    double? MinimumHeightMetres = null,
    double? MaximumHeightMetres = null)
{
    public void Validate(string owner)
    {
        if (string.IsNullOrWhiteSpace(Id)) throw new InvalidDataException($"{owner}: a protrusion is missing its id.");
        if (Outline.Count < 3) throw new InvalidDataException($"{owner}/{Id}: a protrusion outline needs at least three points.");
        if (MinimumHeightMetres is { } low && MaximumHeightMetres is { } high && high < low)
            throw new InvalidDataException($"{owner}/{Id}: the maximum height is below the minimum height.");
    }
}

/// <summary>
/// The optional mirror allowance: a conservative stand-in for mirrors, which the presets do not model.
/// </summary>
/// <remarks>
/// What is published is the mirror width: the body widths exclude mirrors, and mirrors can be up to
/// 0.30 m wide (Grundlag for udformning af trafikarealer 2023, p.37 and p.44). Where along the
/// body a mirror sits, and how high, is not published. The allowance therefore widens the lead
/// unit's front <see cref="FrontLengthMetres"/> by <see cref="WidthMetres"/> on each side. The 0.30 m
/// is the published width; the 1.0 m position is a RhinoRoad assumption, chosen so the allowance
/// covers any plausible mirror position near the cab rather than to describe one.
/// </remarks>
public static class MirrorAllowance
{
    public const double WidthMetres = 0.30;
    public const double FrontLengthMetres = 1.0;

    /// <summary>One line stating what the allowance is and which part is a RhinoRoad assumption.</summary>
    public const string Basis =
        "Mirror allowance: lead body widened 0.30 m each side over its front 1.0 m. The 0.30 m width is " +
        "published (Grundlag for udformning af trafikarealer 2023, p.37); the position is a RhinoRoad assumption.";

    /// <summary>The vehicle with the allowance added to its lead unit; the vehicle itself is not changed.</summary>
    public static VehicleDefinition Apply(VehicleDefinition vehicle)
    {
        ArgumentNullException.ThrowIfNull(vehicle);
        if (vehicle.BodyOutline.Count == 0) return vehicle;
        var front = vehicle.BodyOutline.Max(point => point.X);
        var back = front - FrontLengthMetres;
        var half = vehicle.BodyOutline.Max(point => Math.Abs(point.Y));
        // The inner edge sits inside the body so the union with the body has no coincident edge.
        var inner = half - 0.05;
        var outer = half + WidthMetres;
        ProtrusionDefinition Mirror(string id, string name, double sign) => new(
            id,
            name,
            [
                new Point2(back, sign * inner),
                new Point2(front, sign * inner),
                new Point2(front, sign * outer),
                new Point2(back, sign * outer)
            ]);

        return vehicle with
        {
            Protrusions =
            [
                .. vehicle.Protrusions,
                Mirror("mirror-allowance-left", "Mirror allowance (left)", 1.0),
                Mirror("mirror-allowance-right", "Mirror allowance (right)", -1.0)
            ]
        };
    }
}

/// <summary>One wheel-edge trace: the path of the outer or inner edge of a wheel on one side of an axle.</summary>
public sealed record WheelEdgeTrace(string Name, IReadOnlyList<Point2> PointsMetres);

/// <summary>
/// Wheel-edge traces from the poses of a result: what kerb-overrun and bollard checks are made against.
/// </summary>
/// <remarks>
/// The lead unit gets the outer edge of its front wheels (at the front axle) and the inner edge of
/// its rear wheels; each towed unit gets both edges of its single axle line. The offsets are the
/// track plus or minus the tyre width over two. Front wheels are placed across the vehicle axis, not
/// rotated with the steering: the edge is at most a few centimetres off in a lock turn.
/// </remarks>
public static class WheelEdgeTracks
{
    public static IReadOnlyList<WheelEdgeTrace> Compute(VehicleAccessResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        var vehicle = result.Vehicle;
        var traces = new List<WheelEdgeTrace>();

        void Add(string name, double offset, Func<VehiclePose, (Point2 Centre, double Heading)> axle) =>
            traces.Add(new WheelEdgeTrace(name, result.Poses.Select(pose =>
            {
                var (centre, heading) = axle(pose);
                return Geometry2D.Transform(new Point2(0.0, offset), centre, heading);
            }).ToArray()));

        var outer = vehicle.WheelOuterEdgeOffsetMetres;
        var inner = vehicle.RearWheelInnerEdgeOffsetMetres;
        (Point2, double) Front(VehiclePose pose) => (pose.FrontAxleCentreMetres, pose.VehicleHeadingRadians);
        (Point2, double) Rear(VehiclePose pose) => (pose.RearAxleCentreMetres.XY, pose.VehicleHeadingRadians);
        Add("Front wheel outer edge (left)", outer, Front);
        Add("Front wheel outer edge (right)", -outer, Front);
        Add("Rear wheel inner edge (left)", inner, Rear);
        Add("Rear wheel inner edge (right)", -inner, Rear);

        for (var unit = 0; unit < vehicle.TowedUnits.Count; unit++)
        {
            var towed = vehicle.TowedUnits[unit];
            var captured = unit;
            (Point2, double) Axle(VehiclePose pose) =>
                (pose.TowedUnits[captured].AxleCentreMetres, pose.TowedUnits[captured].HeadingRadians);
            Add($"{towed.Name} wheel outer edge (left)", towed.WheelOuterEdgeOffsetMetres, Axle);
            Add($"{towed.Name} wheel outer edge (right)", -towed.WheelOuterEdgeOffsetMetres, Axle);
            Add($"{towed.Name} wheel inner edge (left)", towed.RearWheelInnerEdgeOffsetMetres, Axle);
            Add($"{towed.Name} wheel inner edge (right)", -towed.RearWheelInnerEdgeOffsetMetres, Axle);
        }

        return traces;
    }
}
