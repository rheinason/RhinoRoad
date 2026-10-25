using Rhino;
using Rhino.DocObjects;
using Rhino.Geometry;
using Rhino.Geometry.Intersect;
using RhinoRoad.Core;

namespace RhinoRoad.Rhino.Services;

/// <summary>
/// Sets a route's elevations to the height of a terrain surface, mesh or SubD below each sample.
/// </summary>
/// <remarks>
/// Both the first run and every update go through <see cref="Drape"/>, so a driven route and a
/// sampled curve are draped the same way and the saved result matches what the command reported.
/// What happens to stations that miss the terrain is decided in <see cref="TerrainDrape"/>.
/// </remarks>
internal static class TerrainDraper
{
    /// <summary>Whether the object is something a vertical projection can land on.</summary>
    public static bool IsTerrain(GeometryBase? geometry) =>
        geometry is Mesh or Brep or Extrusion or SubD;

    /// <summary>
    /// Asks for one terrain object. Null when cancelled. Preselection is ignored so a highlighted
    /// path or control line is never taken for the terrain.
    /// </summary>
    public static Guid? Pick(RhinoDoc document, string prompt = "Select terrain surface, polysurface, SubD or mesh to drape the route onto")
    {
        document.Objects.UnselectAll();
        using var picker = new global::Rhino.Input.Custom.GetObject();
        picker.SetCommandPrompt(prompt);
        picker.GeometryFilter = ObjectType.Surface | ObjectType.PolysrfFilter | ObjectType.Extrusion | ObjectType.SubD | ObjectType.Mesh;
        picker.SubObjectSelect = false;
        picker.EnablePreSelect(false, true);
        return picker.Get() == global::Rhino.Input.GetResult.Object ? picker.Object(0).ObjectId : null;
    }

    /// <summary>
    /// The route with Z taken from the terrain, and the number of stations that found none.
    /// </summary>
    public static (IReadOnlyList<RouteSample> Route, int MissedStations) Drape(
        RhinoDoc document,
        Guid terrainId,
        IReadOnlyList<RouteSample> route)
    {
        var terrain = document.Objects.FindId(terrainId)?.Geometry
            ?? throw new InvalidOperationException("The terrain object is missing.");
        if (!IsTerrain(terrain)) throw new InvalidOperationException("The terrain object is not a surface, polysurface, SubD or mesh.");

        var toModel = RhinoMath.UnitScale(UnitSystem.Meters, document.ModelUnitSystem);
        var toMetres = RhinoMath.UnitScale(document.ModelUnitSystem, UnitSystem.Meters);
        var tolerance = document.ModelAbsoluteTolerance;

        // Start above the terrain and project down, so every surface under the point is found
        // whichever way the projection treats a hit behind the start.
        var box = terrain.GetBoundingBox(accurate: false);
        var startHeight = box.Max.Z + Math.Max(1.0, box.Diagonal.Length * 0.01);
        var points = route
            .Select(sample => new Point3d(sample.PositionMetres.X * toModel, sample.PositionMetres.Y * toModel, startHeight))
            .ToArray();

        Point3d[] hits;
        int[] indices;
        switch (terrain)
        {
            case Mesh mesh:
                hits = Intersection.ProjectPointsToMeshesEx([mesh], points, -Vector3d.ZAxis, tolerance, out indices);
                break;
            default:
                var brep = terrain switch
                {
                    Brep value => value,
                    Extrusion extrusion => extrusion.ToBrep(),
                    SubD subd => subd.ToBrep(),
                    _ => null
                } ?? throw new InvalidOperationException("The terrain could not be converted for projection.");
                hits = Intersection.ProjectPointsToBrepsEx([brep], points, -Vector3d.ZAxis, tolerance, out indices);
                break;
        }

        var heights = new double?[route.Count];
        for (var hit = 0; hit < hits.Length && hit < indices.Length; hit++)
        {
            var index = indices[hit];
            if (index < 0 || index >= heights.Length) continue;
            var height = hits[hit].Z * toMetres;
            if (!heights[index].HasValue || height > heights[index]!.Value) heights[index] = height;
        }

        return TerrainDrape.Apply(route, heights);
    }

    /// <summary>
    /// Replaces the baked rear-axle track with one that follows the draped route, so the vertical
    /// profile can be seen. Every other output stays planar at the start elevation.
    /// </summary>
    public static RhinoAnalysisGeometry WithDrapedRearTrack(
        RhinoAnalysisGeometry geometry,
        IReadOnlyList<RouteSample> route,
        UnitSystem modelUnits)
    {
        var toModel = RhinoMath.UnitScale(UnitSystem.Meters, modelUnits);
        return geometry with
        {
            RearAxleTrack = new PolylineCurve(route.Select(sample => new Point3d(
                sample.PositionMetres.X * toModel, sample.PositionMetres.Y * toModel, sample.PositionMetres.Z * toModel)))
        };
    }
}
