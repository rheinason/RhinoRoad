using System.Buffers.Binary;
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
        // Every output carries its source's id, so this ran for each click on one; scanning the whole
        // document each time was the cost on a large site. The session keeps the answer per document.
        return RoadDocumentSession.For(document).FindSource(document, sourceId);
    }

    public static bool TryRead(RhinoObject source, out VehicleAccessDefinition? definition, out string? error) =>
        AccessDefinitionSerializer.TryDeserialize(source.Attributes.GetUserString(DefinitionKey), out definition, out error);

    /// <summary>
    /// <see cref="TryRead"/> for code that runs on every redraw: the parsed definition is kept per
    /// object, and <paramref name="json"/> is the saved text, for cheap comparisons. The instance is
    /// shared, which a record makes harmless.
    /// </summary>
    public static bool TryReadCached(RhinoObject source, out VehicleAccessDefinition? definition, out string? json)
    {
        if (source.Document is { } document)
            return RoadDocumentSession.For(document).TryReadDefinition(source, out definition, out json);
        json = source.Attributes.GetUserString(DefinitionKey);
        return AccessDefinitionSerializer.TryDeserialize(json, out definition, out _) && definition is not null;
    }

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
            // A terrain is a tracked reference too, and editing its interior (a vertex dragged, a
            // control point lifted) leaves the bounding box unchanged, so the box alone left a road
            // "current" with stale grades. Hash the content that defines the shape. Curves keep their
            // original sampled form above so that saved curve fingerprints still match.
            if (ContentHash(geometry) is { } content) builder.Append(content).Append('|');
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())));
    }

    /// <summary>
    /// A hash of the vertices or control points of a non-curve reference, or null for a kind with no
    /// defined content hash. Explicit rather than <see cref="GeometryBase.DataCRC"/>, whose inputs are
    /// not documented as stable across sessions or versions; every value hashed here is saved in the
    /// file, so the fingerprint survives a reload. A whole mesh is hashed: binary SHA-256 over a
    /// million vertices costs tens of milliseconds, and it runs on change events, not per frame.
    /// </summary>
    private static string? ContentHash(GeometryBase geometry)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        switch (geometry)
        {
            case Mesh mesh:
                AddCount(hash, mesh.Vertices.Count);
                foreach (var point in mesh.Vertices.ToPoint3dArray()) AddPoint(hash, point);
                var faces = mesh.Faces.ToIntArray(asTriangles: false);
                AddCount(hash, faces.Length);
                foreach (var index in faces) AddCount(hash, index);
                break;
            case Brep brep:
                AddBrep(hash, brep);
                break;
            case Extrusion extrusion:
                using (var brep = extrusion.ToBrep(splitKinkyFaces: false))
                {
                    if (brep is null) return null;
                    AddBrep(hash, brep);
                }
                break;
            case Surface surface:
                if (!AddSurface(hash, surface)) return null;
                break;
            case SubD subd:
                // Hashed by persistent component id rather than list order, which a reload need not keep.
                var vertices = new List<(uint Id, Point3d Point)>();
                for (var vertex = subd.Vertices.First; vertex is not null; vertex = vertex.Next)
                    vertices.Add((vertex.Id, vertex.ControlNetPoint));
                AddCount(hash, vertices.Count);
                AddCount(hash, subd.Edges.Count);
                AddCount(hash, subd.Faces.Count);
                foreach (var (id, point) in vertices.OrderBy(v => v.Id))
                {
                    AddCount(hash, unchecked((int)id));
                    AddPoint(hash, point);
                }
                break;
            default:
                return null;
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static void AddBrep(IncrementalHash hash, Brep brep)
    {
        AddCount(hash, brep.Vertices.Count);
        foreach (var vertex in brep.Vertices) AddPoint(hash, vertex.Location);
        AddCount(hash, brep.Surfaces.Count);
        for (var i = 0; i < brep.Surfaces.Count; i++) AddSurface(hash, brep.Surfaces[i]);
        AddCount(hash, brep.Curves3D.Count);
        for (var i = 0; i < brep.Curves3D.Count; i++)
        {
            using var nurbs = brep.Curves3D[i].ToNurbsCurve();
            if (nurbs is null) { AddCount(hash, -1); continue; }
            AddCount(hash, nurbs.Points.Count);
            for (var j = 0; j < nurbs.Points.Count; j++)
            {
                var point = nurbs.Points[j];
                AddPoint(hash, point.Location);
                AddDouble(hash, point.Weight);
            }
        }
    }

    private static bool AddSurface(IncrementalHash hash, Surface surface)
    {
        using var nurbs = surface.ToNurbsSurface();
        if (nurbs is null) { AddCount(hash, -1); return false; }
        AddCount(hash, nurbs.Points.CountU);
        AddCount(hash, nurbs.Points.CountV);
        for (var u = 0; u < nurbs.Points.CountU; u++)
        for (var v = 0; v < nurbs.Points.CountV; v++)
        {
            nurbs.Points.GetPoint(u, v, out Point4d point);
            AddDouble(hash, point.X); AddDouble(hash, point.Y); AddDouble(hash, point.Z); AddDouble(hash, point.W);
        }
        for (var i = 0; i < nurbs.KnotsU.Count; i++) AddDouble(hash, nurbs.KnotsU[i]);
        for (var i = 0; i < nurbs.KnotsV.Count; i++) AddDouble(hash, nurbs.KnotsV[i]);
        return true;
    }

    private static void AddPoint(IncrementalHash hash, Point3d point)
    {
        AddDouble(hash, point.X); AddDouble(hash, point.Y); AddDouble(hash, point.Z);
    }

    private static void AddDouble(IncrementalHash hash, double value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(double)];
        BinaryPrimitives.WriteDoubleLittleEndian(bytes, value);
        hash.AppendData(bytes);
    }

    private static void AddCount(IncrementalHash hash, int value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, value);
        hash.AppendData(bytes);
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
