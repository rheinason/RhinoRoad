using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Rhino;
using Rhino.DocObjects;
using Rhino.Geometry;
using RhinoRoad.Core;
using System.Drawing;

namespace RhinoRoad.Rhino.Services;

internal static class AccessDefinitionStore
{
    public const string DefinitionKey = "RhinoRoad.AccessDefinition";
    public const string SourceIdKey = "RhinoRoad.SourceId";
    public const string AnalysisIdKey = "RhinoRoad.AnalysisId";
    public const string RoleKey = "RhinoRoad.Role";
    public const string FingerprintKey = "RhinoRoad.Fingerprint";
    public const string SourceControlRole = "SourceControl";
    public const string SourceCurveRole = "SourceCurve";
    public const string GeneratedRole = "Generated";

    public static RhinoObject? ResolveSource(RhinoDoc document, RhinoObject selected)
    {
        if (selected.Attributes.GetUserString(DefinitionKey) is not null) return selected;
        var sourceId = selected.Attributes.GetUserString(SourceIdKey);
        if (string.IsNullOrWhiteSpace(sourceId)) return null;
        return document.Objects.GetObjectList(ObjectType.AnyObject).FirstOrDefault(candidate =>
            candidate.Attributes.GetUserString(DefinitionKey) is not null &&
            string.Equals(candidate.Attributes.GetUserString(SourceIdKey), sourceId, StringComparison.OrdinalIgnoreCase));
    }

    public static bool TryRead(RhinoObject source, out VehicleAccessDefinition? definition, out string? error) =>
        AccessDefinitionSerializer.TryDeserialize(source.Attributes.GetUserString(DefinitionKey), out definition, out error);

    public static bool Write(RhinoDoc document, Guid objectId, VehicleAccessDefinition definition)
    {
        var source = document.Objects.FindId(objectId);
        if (source is null) return false;
        var attributes = source.Attributes.Duplicate();
        attributes.SetUserString(DefinitionKey, AccessDefinitionSerializer.Serialize(definition));
        attributes.SetUserString(SourceIdKey, definition.StableSourceId.ToString("D"));
        attributes.SetUserString(AnalysisIdKey, definition.AnalysisId);
        attributes.SetUserString(RoleKey, definition.SourceKind == PathSourceKind.Interactive ? SourceControlRole : SourceCurveRole);
        attributes.SetUserString(FingerprintKey, definition.SourceFingerprint);
        return document.Objects.ModifyAttributes(source, attributes, quiet: true);
    }

    public static Guid AddInteractiveSource(RhinoDoc document, Curve controls, Guid stableSourceId)
    {
        var attributes = document.CreateDefaultAttributes();
        attributes.Name = "Vehicle access controls";
        attributes.LayerIndex = EnsureChildLayer(document, "Controls", Color.MediumPurple);
        attributes.ColorSource = ObjectColorSource.ColorFromLayer;
        attributes.SetUserString(SourceIdKey, stableSourceId.ToString("D"));
        attributes.SetUserString(RoleKey, SourceControlRole);
        return document.Objects.AddCurve(controls, attributes);
    }

    public static Guid AddSampleSource(RhinoDoc document, Curve sourceCurve)
    {
        var attributes = document.CreateDefaultAttributes();
        attributes.Name = "Sample rear-axle path";
        attributes.LayerIndex = EnsureChildLayer(document, "Paths", Color.DodgerBlue);
        attributes.ColorSource = ObjectColorSource.ColorFromLayer;
        return document.Objects.AddCurve(sourceCurve, attributes);
    }

    public static string Fingerprint(RhinoObject source) => Fingerprint(source.Geometry);

    public static string Fingerprint(GeometryBase geometry)
    {
        var builder = new StringBuilder();
        var box = geometry.GetBoundingBox(accurate: true);
        Add(builder, box.Min.X); Add(builder, box.Min.Y); Add(builder, box.Min.Z);
        Add(builder, box.Max.X); Add(builder, box.Max.Y); Add(builder, box.Max.Z);
        if (geometry is Curve curve)
        {
            Add(builder, curve.GetLength());
            var divisions = curve.DivideByCount(64, includeEnds: true) ?? [curve.Domain.T0, curve.Domain.T1];
            foreach (var parameter in divisions)
            {
                var point = curve.PointAt(parameter);
                Add(builder, point.X); Add(builder, point.Y); Add(builder, point.Z);
            }
        }
        else
        {
            builder.Append(geometry.ObjectType).Append('|');
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())));
    }

    public static IReadOnlyList<Point3> PolylineVertices(Curve curve, UnitSystem modelUnits)
    {
        if (!curve.TryGetPolyline(out var polyline))
            throw new InvalidDataException("The editable control route must remain a polyline.");
        var metres = RhinoMath.UnitScale(modelUnits, UnitSystem.Meters);
        return polyline.Select(point => new Point3(point.X * metres, point.Y * metres, point.Z * metres)).ToArray();
    }

    private static void Add(StringBuilder builder, double value) =>
        builder.Append(value.ToString("R", CultureInfo.InvariantCulture)).Append('|');

    internal static int EnsureChildLayer(RhinoDoc document, string childName, Color color)
    {
        const string rootName = "RhinoRoad";
        var rootIndex = document.Layers.FindByFullPath(rootName, -1);
        if (rootIndex < 0) rootIndex = document.Layers.Add(new Layer { Name = rootName, Color = Color.DimGray });
        var rootId = document.Layers[rootIndex].Id;
        var path = $"{rootName}{ModelComponent.NamePathSeparator}{childName}";
        var existing = document.Layers.FindByFullPath(path, -1);
        return existing >= 0
            ? existing
            : document.Layers.Add(new Layer { Name = childName, ParentLayerId = rootId, Color = color });
    }
}
