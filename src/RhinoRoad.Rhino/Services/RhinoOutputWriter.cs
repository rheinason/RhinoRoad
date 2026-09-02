using System.Drawing;
using Rhino;
using Rhino.DocObjects;
using Rhino.Geometry;
using RhinoRoad.Core;

namespace RhinoRoad.Rhino.Services;

internal static class RhinoOutputWriter
{
    private const string RootLayerName = "RhinoRoad";

    public static IReadOnlyList<Guid> Bake(
        RhinoDoc document,
        RhinoAnalysisGeometry geometry,
        Manoeuvre? manoeuvre,
        VehicleAccessResult result,
        IReadOnlyList<AnalysisViolation> allViolations,
        Guid sourceId,
        string analysisId,
        double clearanceMetres,
        double leftWidthMetres,
        double rightWidthMetres,
        bool replaceExisting)
    {
        if (replaceExisting) DeleteMatching(document, sourceId);
        var ids = new List<Guid>();
        if (manoeuvre is not null)
        {
            // The editable half of the output. Its grips are the waypoints, and the manoeuvre
            // travels with it, so re-running the command against this curve rebuilds everything
            // else from it.
            var attributes = Attributes(document, "Control", ManoeuvreStore.ControlCurveName, Color.DodgerBlue, result, sourceId, analysisId, clearanceMetres, leftWidthMetres, rightWidthMetres);
            attributes.SetUserString(ManoeuvreStore.ManoeuvreKey, ManoeuvreSerializer.ToJson(manoeuvre));
            var controlId = document.Objects.AddCurve(ManoeuvreStore.ControlCurve(manoeuvre, document.ModelUnitSystem), attributes);
            if (controlId != Guid.Empty) ids.Add(controlId);
        }
        ids.Add(AddCurve(document, geometry.RearAxleTrack, "Paths", "Rear axle track", Color.Blue, result, sourceId, analysisId, clearanceMetres, leftWidthMetres, rightWidthMetres));
        ids.Add(AddCurve(document, geometry.FrontAxleTrack, "WheelTracks", "Front axle track", Color.CornflowerBlue, result, sourceId, analysisId, clearanceMetres, leftWidthMetres, rightWidthMetres));
        if (geometry.BodyEnvelope is not null)
        {
            ids.Add(AddCurve(document, geometry.BodyEnvelope, "Swept", "Body swept envelope", Color.DarkOrange, result, sourceId, analysisId, clearanceMetres, leftWidthMetres, rightWidthMetres));
        }
        if (geometry.ClearanceEnvelope is not null)
        {
            ids.Add(AddCurve(document, geometry.ClearanceEnvelope, "Clearance", "Clearance envelope", Color.OrangeRed, result, sourceId, analysisId, clearanceMetres, leftWidthMetres, rightWidthMetres));
        }
        foreach (var hole in geometry.BodyEnvelopeHoles)
        {
            ids.Add(AddCurve(document, hole, "Swept", "Body swept envelope hole", Color.DarkOrange, result, sourceId, analysisId, clearanceMetres, leftWidthMetres, rightWidthMetres));
        }
        foreach (var hole in geometry.ClearanceEnvelopeHoles)
        {
            ids.Add(AddCurve(document, hole, "Clearance", "Clearance envelope hole", Color.OrangeRed, result, sourceId, analysisId, clearanceMetres, leftWidthMetres, rightWidthMetres));
        }
        foreach (var footprint in geometry.Footprints)
        {
            ids.Add(AddCurve(document, footprint, "Footprints", "Vehicle footprint", Color.SlateGray, result, sourceId, analysisId, clearanceMetres, leftWidthMetres, rightWidthMetres));
        }
        foreach (var edge in geometry.FixedRoadEdges)
        {
            ids.Add(AddCurve(document, edge, "RoadEdges", "Fixed-width preliminary road edge", Color.ForestGreen, result, sourceId, analysisId, clearanceMetres, leftWidthMetres, rightWidthMetres));
        }

        var modelUnitsPerMetre = RhinoMath.UnitScale(UnitSystem.Meters, document.ModelUnitSystem);
        foreach (var violation in allViolations)
        {
            var attributes = Attributes(document, "Warnings", violation.Kind.ToString(), Color.Red, result, sourceId, analysisId, clearanceMetres, leftWidthMetres, rightWidthMetres);
            attributes.SetUserString("RhinoRoad.Message", violation.Message);
            var id = document.Objects.AddPoint(new Point3d(
                violation.PositionMetres.X * modelUnitsPerMetre,
                violation.PositionMetres.Y * modelUnitsPerMetre,
                violation.PositionMetres.Z * modelUnitsPerMetre), attributes);
            if (id != Guid.Empty) ids.Add(id);
        }

        var validIds = ids.Where(id => id != Guid.Empty).ToArray();
        if (validIds.Length > 0)
        {
            var groupIndex = document.Groups.Add($"RhinoRoad {result.Vehicle.Id} {result.DrivingMode.Id} {analysisId[..8]}");
            document.Groups.AddToGroup(groupIndex, validIds);
        }
        document.Views.Redraw();
        return validIds;
    }

    private static Guid AddCurve(
        RhinoDoc document,
        Curve curve,
        string childLayer,
        string name,
        Color color,
        VehicleAccessResult result,
        Guid sourceId,
        string analysisId,
        double clearance,
        double leftWidth,
        double rightWidth)
    {
        var attributes = Attributes(document, childLayer, name, color, result, sourceId, analysisId, clearance, leftWidth, rightWidth);
        return document.Objects.AddCurve(curve, attributes);
    }

    private static ObjectAttributes Attributes(
        RhinoDoc document,
        string childLayer,
        string name,
        Color color,
        VehicleAccessResult result,
        Guid sourceId,
        string analysisId,
        double clearance,
        double leftWidth,
        double rightWidth)
    {
        var attributes = document.CreateDefaultAttributes();
        attributes.LayerIndex = EnsureLayer(document, childLayer, color);
        attributes.Name = name;
        attributes.ColorSource = ObjectColorSource.ColorFromLayer;
        attributes.SetUserString("RhinoRoad.AnalysisId", analysisId);
        attributes.SetUserString("RhinoRoad.SourceId", sourceId.ToString("D"));
        attributes.SetUserString("RhinoRoad.Vehicle", result.Vehicle.Id);
        attributes.SetUserString("RhinoRoad.VehicleVersion", result.Vehicle.Version);
        attributes.SetUserString("RhinoRoad.ValidationStatus", result.Vehicle.ValidationStatus.ToString());
        attributes.SetUserString("RhinoRoad.Mode", result.DrivingMode.Id);
        attributes.SetUserString("RhinoRoad.ClearanceMetres", clearance.ToString("R", System.Globalization.CultureInfo.InvariantCulture));
        attributes.SetUserString("RhinoRoad.LeftWidthMetres", leftWidth.ToString("R", System.Globalization.CultureInfo.InvariantCulture));
        attributes.SetUserString("RhinoRoad.RightWidthMetres", rightWidth.ToString("R", System.Globalization.CultureInfo.InvariantCulture));
        return attributes;
    }

    private static int EnsureLayer(RhinoDoc document, string childName, Color color)
    {
        var rootIndex = document.Layers.FindByFullPath(RootLayerName, -1);
        Guid rootId;
        if (rootIndex < 0)
        {
            rootIndex = document.Layers.Add(new Layer { Name = RootLayerName, Color = Color.DimGray });
        }
        rootId = document.Layers[rootIndex].Id;
        var fullPath = $"{RootLayerName}{ModelComponent.NamePathSeparator}{childName}";
        var childIndex = document.Layers.FindByFullPath(fullPath, -1);
        if (childIndex >= 0) return childIndex;
        return document.Layers.Add(new Layer { Name = childName, ParentLayerId = rootId, Color = color });
    }

    private static void DeleteMatching(RhinoDoc document, Guid sourceId)
    {
        var sourceText = sourceId.ToString("D");
        var matches = document.Objects.GetObjectList(ObjectType.AnyObject)
            .Where(obj => string.Equals(obj.Attributes.GetUserString("RhinoRoad.SourceId"), sourceText, StringComparison.OrdinalIgnoreCase))
            .Select(obj => obj.Id)
            .ToArray();
        foreach (var id in matches) document.Objects.Delete(id, quiet: true);
    }
}
