using Rhino;

namespace RhinoRoad.GrasshopperPlugin.Conversion;

/// <summary>
/// Model units of a Rhino document against metres. Core works in SI metres throughout, so every
/// length crossing the component boundary is scaled here and nowhere else.
/// </summary>
/// <remarks>
/// The document is the one the Grasshopper definition belongs to, passed by the component; the active
/// document is only the fallback, since with several documents open it need not be the definition's.
/// </remarks>
public static class ModelUnits
{
    public static UnitSystem Of(RhinoDoc? document) =>
        (document ?? RhinoDoc.ActiveDoc)?.ModelUnitSystem ?? UnitSystem.Meters;

    public static double MetresPerModelUnit(RhinoDoc? document) => RhinoMath.UnitScale(Of(document), UnitSystem.Meters);

    public static double ModelUnitsPerMetre(RhinoDoc? document) => RhinoMath.UnitScale(UnitSystem.Meters, Of(document));
}
