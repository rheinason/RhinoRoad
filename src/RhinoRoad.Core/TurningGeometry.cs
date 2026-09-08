namespace RhinoRoad.Core;

/// <summary>
/// What a vehicle can do at full lock, in the terms a designer lays out a corner in.
/// </summary>
/// <param name="RearAxleRadiusMetres">Radius traced by the rear-axle midpoint — the path you draw.</param>
/// <param name="OuterRadiusMetres">Radius of the body corner that swings widest.</param>
/// <param name="InnerRadiusMetres">Radius of the body point that cuts closest to the centre.</param>
public sealed record TurningGeometry(
    double RearAxleRadiusMetres,
    double OuterRadiusMetres,
    double InnerRadiusMetres)
{
    /// <summary>
    /// Width of the annulus the body occupies at full lock. This is the kerb-to-kerb width a turn
    /// must provide, and it is always wider than the vehicle: the front corner swings out while the
    /// rear cuts in.
    /// </summary>
    public double SweptWidthMetres => OuterRadiusMetres - InnerRadiusMetres;

    /// <summary>Radius each towed axle settles on in the steady turn. Empty for a rigid vehicle.</summary>
    public IReadOnlyList<double> TowedAxleRadiiMetres { get; init; } = [];

    /// <summary>
    /// False when the combination has no steady state at this radius — the tractor can hold the
    /// lock, but the trailer cannot follow it round and folds until it jackknifes. The radii above
    /// then describe the lead unit alone, and
    /// <see cref="MinimumSustainableRearAxleRadiusMetres"/> is the number the corner has to respect.
    /// </summary>
    public bool SteadyStateAttainable { get; init; } = true;

    /// <summary>
    /// Tightest rear-axle radius the whole combination can hold continuously. Zero for a rigid
    /// vehicle, which has no fold to run out of.
    /// </summary>
    public double MinimumSustainableRearAxleRadiusMetres { get; init; }
}

public static class TurningGeometryCalculator
{
    /// <summary>
    /// Tightest turn the mode permits, evaluated over the actual body outline.
    /// </summary>
    /// <remarks>
    /// At full lock the rear axle traces a circle of radius <c>L / tan(δ_max)</c>. Placing that
    /// circle's centre at <c>(0, R)</c> in the vehicle frame — rear axle at the origin, heading +X —
    /// every body point <c>(x, y)</c> rides a radius of <c>sqrt(x² + (R − y)²)</c>. Taking the
    /// extremes over the outline gives the swept annulus exactly, including the corner swing that a
    /// centreline radius alone would miss.
    ///
    /// <para>
    /// A towed unit in a steady turn circles the same centre, and no-slip at its axle puts its own
    /// axis tangent to that circle — so the same construction applies to it, once its radius is
    /// known. Its hitch rides at <c>sqrt(R² + h²)</c>, and its axle trails one wheelbase behind on
    /// the same circle, giving <c>R' = sqrt(R² + h² − L²)</c>. When that is negative the unit has no
    /// steady state at this radius: it is being asked to fold past a right angle and keep folding.
    /// </para>
    /// </remarks>
    public static TurningGeometry AtFullLock(VehicleDefinition vehicle, DrivingModeDefinition mode)
    {
        ArgumentNullException.ThrowIfNull(vehicle);
        ArgumentNullException.ThrowIfNull(mode);

        var rearAxleRadius = vehicle.WheelbaseMetres / Math.Tan(mode.MaximumWheelAngleRadians);
        var outer = 0.0;
        var inner = double.PositiveInfinity;
        Extend(vehicle.BodyOutline, rearAxleRadius, ref outer, ref inner);

        var radii = new List<double>(vehicle.TowedUnits.Count);
        var attainable = true;
        var radius = rearAxleRadius;
        foreach (var unit in vehicle.TowedUnits)
        {
            var squared = (radius * radius) + (unit.HitchOffsetMetres * unit.HitchOffsetMetres)
                - (unit.WheelbaseMetres * unit.WheelbaseMetres);
            if (squared < 0.0)
            {
                attainable = false;
                break;
            }

            radius = Math.Sqrt(squared);
            radii.Add(radius);
            Extend(unit.BodyOutline, radius, ref outer, ref inner);
        }

        return new TurningGeometry(rearAxleRadius, outer, double.IsPositiveInfinity(inner) ? 0.0 : inner)
        {
            TowedAxleRadiiMetres = radii,
            SteadyStateAttainable = attainable,
            MinimumSustainableRearAxleRadiusMetres = MinimumSustainableRadius(vehicle)
        };
    }

    /// <summary>
    /// Smallest lead rear-axle radius at which every unit still has a steady state. Walking the
    /// chain backwards, the last unit needs its own radius to reach zero at worst, and each unit
    /// hands its tower the radius that requirement implies.
    /// </summary>
    private static double MinimumSustainableRadius(VehicleDefinition vehicle)
    {
        var squared = 0.0;
        for (var index = vehicle.TowedUnits.Count - 1; index >= 0; index--)
        {
            var unit = vehicle.TowedUnits[index];
            squared = Math.Max(0.0, squared
                + (unit.WheelbaseMetres * unit.WheelbaseMetres)
                - (unit.HitchOffsetMetres * unit.HitchOffsetMetres));
        }

        return Math.Sqrt(squared);
    }

    /// <summary>
    /// Widens the annulus to contain one unit's outline, riding a circle of <paramref name="radius"/>.
    /// </summary>
    /// <remarks>
    /// The far extreme is always a corner, so the outer radius is a maximum over vertices. The near
    /// extreme is not: on a body whose inner flank spans the axle station — every one of these
    /// presets — the closest point to the turn centre lies partway along that flank, not at either
    /// end of it. Taking the minimum over vertices alone reports the inner radius nearly a metre too
    /// large for REN, and a too-large inner radius understates the swept width, which is the unsafe
    /// direction to be wrong in. The minimum is therefore taken over the edges.
    /// </remarks>
    private static void Extend(IReadOnlyList<Point2> outline, double radius, ref double outer, ref double inner)
    {
        if (outline.Count == 0) return;
        var centre = new Point2(0.0, radius);
        foreach (var point in outline) outer = Math.Max(outer, centre.DistanceTo(point));
        if (outline.Count < 2)
        {
            inner = Math.Min(inner, centre.DistanceTo(outline[0]));
            return;
        }

        for (var index = 0; index < outline.Count; index++)
        {
            inner = Math.Min(
                inner,
                Geometry2D.DistancePointToSegment(centre, outline[index], outline[(index + 1) % outline.Count]));
        }
    }
}
