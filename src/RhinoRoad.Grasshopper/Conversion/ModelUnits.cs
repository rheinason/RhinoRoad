using Rhino;

namespace RhinoRoad.GrasshopperPlugin.Conversion;

/// <summary>
/// Model units of the active document against metres. Core works in SI metres throughout, so every
/// length crossing the component boundary is scaled here and nowhere else.
/// </summary>
public static class ModelUnits
{
    private static UnitSystem Current => RhinoDoc.ActiveDoc?.ModelUnitSystem ?? UnitSystem.Meters;

    public static double MetresPerModelUnit => RhinoMath.UnitScale(Current, UnitSystem.Meters);

    public static double ModelUnitsPerMetre => RhinoMath.UnitScale(UnitSystem.Meters, Current);
}
