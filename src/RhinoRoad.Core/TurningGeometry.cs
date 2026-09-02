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
    /// </remarks>
    public static TurningGeometry AtFullLock(VehicleDefinition vehicle, DrivingModeDefinition mode)
    {
        ArgumentNullException.ThrowIfNull(vehicle);
        ArgumentNullException.ThrowIfNull(mode);

        var rearAxleRadius = vehicle.WheelbaseMetres / Math.Tan(mode.MaximumWheelAngleRadians);
        var outer = 0.0;
        var inner = double.PositiveInfinity;
        foreach (var point in vehicle.BodyOutline)
        {
            var offset = rearAxleRadius - point.Y;
            var radius = Math.Sqrt((point.X * point.X) + (offset * offset));
            outer = Math.Max(outer, radius);
            inner = Math.Min(inner, radius);
        }

        return new TurningGeometry(rearAxleRadius, outer, inner);
    }
}
