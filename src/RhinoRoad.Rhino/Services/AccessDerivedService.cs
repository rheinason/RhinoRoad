using System.Drawing;
using System.Text.Json;
using Rhino;
using Rhino.DocObjects;
using Rhino.Geometry;
using RhinoRoad.Core;

namespace RhinoRoad.Rhino.Services;

internal sealed record AccessDerivedDefinition(int Version, Guid Id, Guid[] Sources,
    Point2? SectionStart, Point2? SectionEnd, Dictionary<Guid, string> Signatures);

/// <summary>Persistent, explicitly refreshed sections and combined footprints, independent of baked journeys.</summary>
internal static class AccessDerivedService
{
    private const string Key = "RhinoRoad.DerivedDefinition";
    private const string IdKey = "RhinoRoad.DerivedId";
    private static readonly HashSet<RhinoDoc> Pending = new();
    private static bool _initialized;
    private static bool _marking;

    public static void Initialize()
    {
        if (_initialized) return;
        _initialized = true;
        RhinoDoc.ReplaceRhinoObject += (_, e) => Queue(e.Document);
        RhinoDoc.DeleteRhinoObject += (_, e) => Queue(e.TheObject.Document);
        RhinoDoc.AddRhinoObject += (_, e) => Queue(e.TheObject.Document);
        RhinoDoc.ModifyObjectAttributes += (_, e) => Queue(e.Document);
        RhinoDoc.EndOpenDocument += (_, e) => Queue(e.Document);
        RhinoDoc.CloseDocument += (_, e) => Pending.Remove(e.Document);
        RhinoApp.Idle += (_, _) =>
        {
            var documents = Pending.ToArray();
            Pending.Clear();
            foreach (var document in documents) MarkStale(document);
        };
    }

    private static void Queue(RhinoDoc? document)
    {
        if (!_marking && document is not null) Pending.Add(document);
    }

    public static RhinoObject? Anchor(RhinoDoc document, RhinoObject selected)
    {
        if (selected.Attributes.GetUserString(Key) is not null) return selected;
        var id = selected.Attributes.GetUserString(IdKey);
        return id is null ? null : Anchors(document).FirstOrDefault(o => o.Attributes.GetUserString(IdKey) == id);
    }

    public static Guid[] SourceIds(RhinoDoc document, IEnumerable<RhinoObject> selected) => selected.SelectMany(o =>
    {
        var anchor = Anchor(document, o);
        if (anchor is not null) return Read(anchor).Sources;
        var source = AccessDefinitionStore.ResolveSource(document, o);
        if (source is not null && AccessDefinitionStore.TryRead(source, out var definition, out _) && definition is not null)
            return new[] { definition.StableSourceId };
        throw new InvalidOperationException("Select saved vehicle journeys or a combined access footprint.");
    }).Distinct().ToArray();

    public static void Create(RhinoDoc document, Guid[] sources, Point2? start = null, Point2? end = null)
    {
        var definition = new AccessDerivedDefinition(1, Guid.NewGuid(), sources, start, end, new());
        Rebuild(document, null, definition);
    }

    public static void Update(RhinoDoc document, RhinoObject anchor)
    {
        var definition = Read(anchor);
        if (definition.SectionStart is not null && anchor.Geometry is Curve section)
        {
            if (!section.IsLinear(document.ModelAbsoluteTolerance))
                throw new InvalidOperationException("A measurement section must remain a straight line.");
            var metres = RhinoMath.UnitScale(document.ModelUnitSystem, UnitSystem.Meters);
            definition = definition with
            {
                SectionStart = new Point2(section.PointAtStart.X * metres, section.PointAtStart.Y * metres),
                SectionEnd = new Point2(section.PointAtEnd.X * metres, section.PointAtEnd.Y * metres)
            };
        }
        Rebuild(document, anchor, definition);
    }

    public static string Describe(RhinoDoc document, RhinoObject anchor)
    {
        MarkStale(document);
        anchor = document.Objects.FindId(anchor.Id) ?? anchor;
        return $"{anchor.Name}\n{anchor.Attributes.GetUserString("RhinoRoad.SizingStatus")}\n" +
            anchor.Attributes.GetUserString("RhinoRoad.MovementResults") +
            "\nSpace required by these journeys; separate movements, not simultaneous passing. Use RRUpdateRoad to refresh.";
    }

    public static void RefreshForSource(RhinoDoc document, Guid sourceId)
    {
        foreach (var anchor in Anchors(document).ToArray())
        {
            try
            {
                if (Read(anchor).Sources.Contains(sourceId)) Update(document, anchor);
            }
            catch (Exception error)
            {
                RhinoApp.WriteLine($"Linked sizing result retained: {error.Message}");
            }
        }
        Queue(document);
    }

    private static void Rebuild(RhinoDoc document, RhinoObject? anchor, AccessDerivedDefinition definition)
    {
        if (definition.Version != 1 || definition.Sources.Length == 0)
            throw new InvalidDataException("Unsupported or empty sizing definition.");
        var regions = new List<SweptRegion>();
        var signatures = new Dictionary<Guid, string>();
        var movements = new List<string>();
        foreach (var id in definition.Sources)
        {
            var source = FindSource(document, id) ?? throw new InvalidOperationException($"Journey {id} is missing.");
            if (!AccessDefinitionStore.TryRead(source, out var saved, out _) || saved is null)
                throw new InvalidDataException("A journey definition cannot be read.");
            if (AccessDefinitionStore.Fingerprint(source) != saved.SourceFingerprint || saved.ReferenceFingerprints.Any(pair =>
                    document.Objects.FindId(pair.Key) is not { } reference || AccessDefinitionStore.Fingerprint(reference) != pair.Value))
                throw new InvalidOperationException("A journey is out of date. Update that journey first, then update this sizing result.");
            if (!VehicleAccessRunService.TryPrepareUpdate(document, source, out var run, out var error) || run is null)
                throw new InvalidOperationException(error);
            if (!run.Geometry.BodyEnvelopeMerged || run.Geometry.ClearanceRegion is null || run.Geometry.Warnings.Count > 0)
                throw new InvalidOperationException("A journey has no complete clearance envelope.");
            regions.Add(run.Geometry.ClearanceRegion);
            signatures[id] = Signature(document, source);
            movements.Add($"{run.Analysis.Vehicle.Name}: {MovementFit.Describe(true, run.Violations.Count == 0,
                run.Obstacles.Count + run.AllowedBoundaries.Count > 0)}; {run.Analysis.Vehicle.ValidationStatus}");
        }
        var combined = AccessFootprint.Combine(regions);
        if (combined.Count == 0) throw new InvalidOperationException("No combined access area was generated.");
        definition = definition with { Signatures = signatures };
        var scale = RhinoMath.UnitScale(UnitSystem.Meters, document.ModelUnitSystem);
        var geometries = new List<GeometryBase>();
        var section = definition.SectionStart is not null;
        GeometryBase anchorGeometry;
        var label = section ? "Access section" : "Combined access footprint — separate movements";
        if (definition.SectionStart is { } start && definition.SectionEnd is { } end)
        {
            var intervals = AccessFootprint.Measure(combined, start, end);
            // Both ends must be beyond the footprint so a clipped interval is never labelled as its full width.
            if (intervals.Any(i => i.Start.DistanceTo(start) < 1e-5 || i.End.DistanceTo(end) < 1e-5))
                throw new InvalidOperationException("Extend both section endpoints beyond the clearance footprint.");
            anchorGeometry = new LineCurve(Model(start), Model(end));
            var axis = Model(end) - Model(start);
            axis.Unitize();
            var plane = new Plane(Model(start), axis, Vector3d.CrossProduct(Vector3d.ZAxis, axis));
            foreach (var interval in intervals)
            {
                plane.ClosestParameter(Model(interval.Start), out var ax, out var ay);
                plane.ClosestParameter(Model(interval.End), out var bx, out var by);
                var dimension = new LinearDimension(plane, new Point2d(ax, ay), new Point2d(bx, by),
                    new Point2d((ax + bx) / 2, 1.0 * scale));
                dimension.DimensionStyleId = document.DimStyles.Current.Id;
                geometries.Add(dimension);
            }
            if (intervals.Count == 0) label += " — no occupied interval";
            RhinoApp.WriteLine($"Section: {intervals.Count} occupied interval(s), " +
                string.Join(", ", intervals.Select(i => $"{i.WidthMetres:0.00} m")));
        }
        else
        {
            foreach (var region in combined)
            foreach (var loop in new[] { region.OuterBoundary }.Concat(region.Holes))
                geometries.Add(new PolylineCurve(loop.Append(loop[0]).Select(Model)));
            anchorGeometry = new TextDot("Combined access\nSeparate movements", Model(combined[0].OuterBoundary[0]));
        }
        // Build all replacement objects before touching previous output or source metadata.
        var created = new List<Guid>();
        var oldOutputs = document.Objects.GetObjectList(ObjectType.AnyObject).Where(o =>
            o.Attributes.GetUserString(IdKey) == definition.Id.ToString("D") && o.Id != anchor?.Id).Select(o => o.Id).ToArray();
        if (anchor?.IsLocked == true || oldOutputs.Any(id => document.Objects.FindId(id)?.IsLocked == true))
            throw new InvalidOperationException("Unlock the sizing result before updating it.");
        var previousAttributes = anchor?.Attributes.Duplicate();
        try
        {
            foreach (var geometry in geometries)
            {
                var id = document.Objects.Add(geometry, Attributes(document, definition, label));
                if (id == Guid.Empty) throw new InvalidOperationException("Rhino could not create the sizing output.");
                created.Add(id);
            }
            var attributes = Attributes(document, definition, label);
            attributes.SetUserString(Key, JsonSerializer.Serialize(definition));
            attributes.SetUserString("RhinoRoad.MovementResults", string.Join("\n", movements));
            if (anchor is null)
            {
                var id = document.Objects.Add(anchorGeometry, attributes);
                if (id == Guid.Empty) throw new InvalidOperationException("Rhino could not save the sizing definition.");
                created.Add(id);
            }
            var groupName = $"RhinoRoad sizing {definition.Id:D}";
            var group = document.Groups.FindName(groupName)?.Index ?? document.Groups.Add(groupName);
            if (group < 0 || !document.Groups.AddToGroup(group, created))
                throw new InvalidOperationException("Rhino could not group the sizing output.");
            if (anchor is not null)
            {
                attributes.AddToGroup(group);
                if (!document.Objects.ModifyAttributes(anchor.Id, attributes, true))
                    throw new InvalidOperationException("Rhino could not update the sizing definition.");
            }
            foreach (var id in oldOutputs) document.Objects.Delete(id, true);
        }
        catch
        {
            foreach (var id in created) document.Objects.Delete(id, true);
            if (anchor is not null && previousAttributes is not null)
                document.Objects.ModifyAttributes(anchor.Id, previousAttributes, true);
            throw;
        }
        finally
        {
            foreach (var geometry in geometries) geometry.Dispose();
            anchorGeometry.Dispose();
        }
        foreach (var movement in movements) RhinoApp.WriteLine(movement);
        document.Views.Redraw();
        Point3d Model(Point2 p) => new(p.X * scale, p.Y * scale, 0);
    }

    private static ObjectAttributes Attributes(RhinoDoc document, AccessDerivedDefinition definition, string name)
    {
        var attributes = document.CreateDefaultAttributes();
        attributes.Name = name;
        attributes.ColorSource = ObjectColorSource.ColorFromLayer;
        attributes.LayerIndex = AccessDefinitionStore.EnsureChildLayer(document, "Sizing", Color.SeaGreen);
        attributes.SetUserString(IdKey, definition.Id.ToString("D"));
        attributes.SetUserString("RhinoRoad.SizingStatus", "Current");
        return attributes;
    }

    private static IEnumerable<RhinoObject> Anchors(RhinoDoc document) => document.Objects.GetObjectList(ObjectType.AnyObject)
        .Where(o => o.Attributes.GetUserString(Key) is not null);

    private static AccessDerivedDefinition Read(RhinoObject anchor)
    {
        var definition = JsonSerializer.Deserialize<AccessDerivedDefinition>(anchor.Attributes.GetUserString(Key)!);
        if (definition is null || definition.Version != 1 || definition.Id == Guid.Empty ||
            definition.Sources is null || definition.Sources.Length == 0 || definition.Signatures is null ||
            definition.SectionStart.HasValue != definition.SectionEnd.HasValue)
            throw new InvalidDataException("The sizing definition is invalid or uses an unsupported version.");
        return definition;
    }

    private static RhinoObject? FindSource(RhinoDoc document, Guid id) => document.Objects.GetObjectList(ObjectType.AnyObject)
        .FirstOrDefault(o => o.Attributes.GetUserString(AccessDefinitionStore.DefinitionKey) is not null &&
            o.Attributes.GetUserString(AccessDefinitionStore.SourceIdKey) == id.ToString("D"));

    private static string Signature(RhinoDoc document, RhinoObject source)
    {
        if (!AccessDefinitionStore.TryRead(source, out var definition, out _) || definition is null) return "Unreadable";
        return source.Attributes.GetUserString(AccessDefinitionStore.DefinitionKey) + "|" + AccessDefinitionStore.Fingerprint(source) + "|" +
            string.Join("|", definition.ReferenceFingerprints.Keys.Order().Select(id =>
                document.Objects.FindId(id) is { } reference ? AccessDefinitionStore.Fingerprint(reference) : "Missing"));
    }

    private static void MarkStale(RhinoDoc document)
    {
        _marking = true;
        try
        {
            foreach (var anchor in Anchors(document).ToArray())
            {
                AccessDerivedDefinition definition;
                try { definition = Read(anchor); } catch { continue; }
                var sectionChanged = false;
                if (definition.SectionStart is { } start && definition.SectionEnd is { } end && anchor.Geometry is Curve line)
                {
                    var metres = RhinoMath.UnitScale(document.ModelUnitSystem, UnitSystem.Meters);
                    sectionChanged = !line.IsLinear(document.ModelAbsoluteTolerance) ||
                        start.DistanceTo(new Point2(line.PointAtStart.X * metres, line.PointAtStart.Y * metres)) > 1e-6 ||
                        end.DistanceTo(new Point2(line.PointAtEnd.X * metres, line.PointAtEnd.Y * metres)) > 1e-6;
                }
                var stale = sectionChanged || definition.Sources.Any(id => FindSource(document, id) is not { } source ||
                    !definition.Signatures.TryGetValue(id, out var saved) || saved != Signature(document, source));
                if (!stale) continue;
                foreach (var item in document.Objects.GetObjectList(ObjectType.AnyObject).Where(o =>
                             o.Attributes.GetUserString(IdKey) == definition.Id.ToString("D")).ToArray())
                {
                    if (item.Attributes.GetUserString("RhinoRoad.SizingStatus") == "Out of date") continue;
                    var attributes = item.Attributes.Duplicate();
                    attributes.Name = "Out of date — " + attributes.Name;
                    attributes.ObjectColor = Color.DarkOrange;
                    attributes.ColorSource = ObjectColorSource.ColorFromObject;
                    attributes.SetUserString("RhinoRoad.SizingStatus", "Out of date");
                    document.Objects.ModifyAttributes(item.Id, attributes, true);
                }
            }
        }
        finally { _marking = false; }
    }
}

