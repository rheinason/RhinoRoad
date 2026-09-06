using System.Drawing;
using Rhino;
using Rhino.Display;
using Rhino.DocObjects;
using Rhino.Geometry;
using RhinoRoad.Core;

namespace RhinoRoad.Rhino.Services;

/// <summary>
/// The live sweep preview belongs to an explicit edit session, not to the document. Previewing
/// whenever a stored control curve happened to move meant that nudging a curve for any other reason
/// silently replaced the baked result on screen with a preview the user never asked for. A session
/// is opened by <c>RREditRoad</c> and closed when the edit is saved or discarded; outside one this
/// conduit is switched off entirely, so an idle document pays nothing for it either.
/// </summary>
internal sealed class LiveJourneyEditConduit : DisplayConduit
{
    private sealed class Preview
    {
        public required VehicleAccessDefinition Saved;
        public required Point3[] Vertices;
        public Task<JourneyEditPreviewResult>? Work;
        public Point3[]? WorkVertices;
        public JourneyEditPreviewResult? Result;
        public Point3[]? ResultVertices;
        public string? Error;
    }

    private sealed class DocumentState
    {
        public bool Dirty = true;
        public Guid[] Sources = [];
        public Dictionary<Guid, Preview> Previews = new();
    }

    private static readonly LiveJourneyEditConduit Instance = new();
    private static readonly Dictionary<uint, DocumentState> Documents = new();
    private static readonly Dictionary<uint, Guid> Sessions = new();
    private static readonly VehicleCatalog Catalog = VehicleCatalog.LoadEmbedded();
    private static bool _initialized;

    public static void Initialize()
    {
        if (_initialized) return;
        _initialized = true;
        RhinoDoc.AddRhinoObject += (_, e) => Dirty(e.TheObject.Document);
        RhinoDoc.DeleteRhinoObject += (_, e) => Dirty(e.TheObject.Document);
        RhinoDoc.ReplaceRhinoObject += (_, e) => Dirty(e.Document);
        RhinoDoc.ModifyObjectAttributes += (_, e) => Dirty(e.Document);
        RhinoDoc.EndOpenDocument += (_, e) => Dirty(e.Document);
        RhinoDoc.CloseDocument += (_, e) =>
        {
            // The document is going away, so drop the session without asking it to redraw.
            Sessions.Remove(e.Document.RuntimeSerialNumber);
            Documents.Remove(e.Document.RuntimeSerialNumber);
            Instance.Enabled = Sessions.Count > 0;
        };
    }

    /// <summary>Starts previewing edits to one saved journey. Only one journey is edited at a time.</summary>
    public static void Begin(RhinoDoc document, Guid stableSourceId)
    {
        Sessions[document.RuntimeSerialNumber] = stableSourceId;
        Documents.Remove(document.RuntimeSerialNumber);
        Instance.Enabled = true;
        document.Views.Redraw();
    }

    public static void End(RhinoDoc document)
    {
        Sessions.Remove(document.RuntimeSerialNumber);
        Documents.Remove(document.RuntimeSerialNumber);
        Instance.Enabled = Sessions.Count > 0;
        document.Views.Redraw();
    }

    public static bool IsEditing(RhinoDoc document, Guid stableSourceId) =>
        Sessions.TryGetValue(document.RuntimeSerialNumber, out var editing) && editing == stableSourceId;

    private static void Dirty(RhinoDoc? document)
    {
        if (document is not null && Documents.TryGetValue(document.RuntimeSerialNumber, out var state)) state.Dirty = true;
    }

    public static bool IsPreviewing(RhinoDoc document, Guid stableId) =>
        Documents.TryGetValue(document.RuntimeSerialNumber, out var state) && state.Previews.ContainsKey(stableId);

    protected override void PreDrawObjects(DrawEventArgs e)
    {
        var document = e.RhinoDoc;
        if (document is null || !Sessions.TryGetValue(document.RuntimeSerialNumber, out var editing)) return;
        if (!Documents.TryGetValue(document.RuntimeSerialNumber, out var state))
            Documents[document.RuntimeSerialNumber] = state = new DocumentState();
        if (state.Dirty)
        {
            state.Sources = document.Objects.GetObjectList(ObjectType.Curve)
                .Where(o => o.Attributes.GetUserString(AccessDefinitionStore.DefinitionKey) is not null)
                .Select(o => o.Id).ToArray();
            state.Dirty = false;
        }
        var active = new HashSet<Guid>();
        foreach (var id in state.Sources)
        {
            var source = document.Objects.FindId(id);
            if (source is null || !source.Visible || source.Geometry is not Curve curve ||
                !AccessDefinitionStore.TryRead(source, out var saved, out _) || saved?.Manoeuvre is null ||
                saved.SourceKind != PathSourceKind.Interactive || saved.StableSourceId != editing) continue;
            try
            {
                var vertices = Positions(source, curve, document.ModelUnitSystem);
                if (JourneyEditPreview.PositionsMatch(saved.Manoeuvre, vertices)) continue;
                active.Add(saved.StableSourceId);
                if (!state.Previews.TryGetValue(saved.StableSourceId, out var preview) ||
                    AccessDefinitionSerializer.Serialize(preview.Saved) != AccessDefinitionSerializer.Serialize(saved))
                    state.Previews[saved.StableSourceId] = preview = new Preview { Saved = saved, Vertices = vertices };
                if (!preview.Vertices.SequenceEqual(vertices))
                {
                    preview.Vertices = vertices;
                    preview.Error = null;
                }
                if (preview.Work?.IsCompleted == true)
                {
                    // Keep the last completed sweep while a newer position computes. Requiring an
                    // exact cursor match here would starve the display throughout a continuous drag.
                    if (preview.Work.IsCompletedSuccessfully)
                    {
                        preview.Result = preview.Work.Result;
                        preview.ResultVertices = preview.WorkVertices;
                    }
                    else
                    {
                        var error = preview.Work.Exception?.GetBaseException().Message ?? "Preview cancelled";
                        if (preview.WorkVertices!.SequenceEqual(vertices)) preview.Error = error;
                    }
                    preview.Work = null;
                }
                if (preview.Work is null && preview.Error is null &&
                    (preview.ResultVertices is null || !preview.ResultVertices.SequenceEqual(vertices)))
                {
                    var captured = vertices.ToArray();
                    var vehicle = Catalog.Get(saved.VehicleId);
                    var mode = vehicle.DrivingModes[saved.ModeId];
                    preview.WorkVertices = captured;
                    var previousRoute = preview.Result?.PreviousRoute;
                    preview.Work = Task.Run(() => JourneyEditPreview.Build(vehicle, mode, saved.Manoeuvre,
                        captured, saved.ClearanceMetres, previousRoute));
                    var serial = document.RuntimeSerialNumber;
                    var sourceId = saved.StableSourceId;
                    var work = preview.Work;
                    _ = work.ContinueWith(completed =>
                    {
                        // Observe abandoned faults too. A document can close or an edit can be
                        // cancelled before its pure computation finishes.
                        _ = completed.Exception;
                        RhinoApp.InvokeOnUiThread((Action)(() =>
                        {
                            if (Documents.TryGetValue(serial, out var current) &&
                                current.Previews.TryGetValue(sourceId, out var currentPreview) && currentPreview.Work == work)
                                RhinoDoc.FromRuntimeSerialNumber(serial)?.Views.Redraw();
                        }));
                    }, TaskScheduler.Default);
                }
            }
            catch (Exception error)
            {
                active.Add(saved.StableSourceId);
                state.Previews[saved.StableSourceId] = new Preview { Saved = saved, Vertices = [], Error = error.Message };
            }
        }
        foreach (var id in state.Previews.Keys.Where(id => !active.Contains(id)).ToArray()) state.Previews.Remove(id);
    }

    private static Point3[] Positions(RhinoObject source, Curve curve, UnitSystem units)
    {
        var grips = source.GetGrips();
        if (grips is null || grips.Length == 0) return AccessDefinitionStore.PolylineVertices(curve, units).ToArray();
        var scale = RhinoMath.UnitScale(units, UnitSystem.Meters);
        return grips.OrderBy(g => g.Index).Select(grip =>
        {
            var point = grip.CurrentLocation;
            if (grip.GetDynamicTransform(out var transform))
            {
                point = grip.OriginalLocation;
                point.Transform(transform);
            }
            return new Point3(point.X * scale, point.Y * scale, point.Z * scale);
        }).ToArray();
    }

    protected override void ObjectCulling(CullObjectEventArgs e)
    {
        var item = e.RhinoObject;
        if (item?.Document is not { } document ||
            item.Attributes.GetUserString(AccessDefinitionStore.RoleKey) != AccessDefinitionStore.GeneratedRole) return;
        if (Guid.TryParse(item.Attributes.GetUserString(AccessDefinitionStore.SourceIdKey), out var id) && IsPreviewing(document, id))
            e.CullObject = true;
    }

    protected override void DrawForeground(DrawEventArgs e)
    {
        if (e.RhinoDoc is not { } document || !Documents.TryGetValue(document.RuntimeSerialNumber, out var state)) return;
        var scale = RhinoMath.UnitScale(UnitSystem.Meters, document.ModelUnitSystem);
        var row = 0;
        foreach (var preview in state.Previews.Values)
        {
            var result = preview.Result;
            var current = preview.ResultVertices?.SequenceEqual(preview.Vertices) == true && preview.Error is null;
            var message = preview.Error is not null ? $"Edit preview unavailable: {preview.Error}" : result is null ?
                "Updating edit preview…" : !current ? "Updating preview — faded sweep is the previous position" :
                "Live edit preview — Save in the Edit Road palette to keep it and rerun the checks";
            e.Display.Draw2dText(message, Color.DimGray, new Point2d(24, 35 + row++ * 24), false, 14);
            if (result is null) continue;
            if (!result.Body.IsSuccess || !result.Clearance.IsSuccess)
            {
                e.Display.Draw2dText("Sweep unavailable for this edit", Color.OrangeRed, new Point2d(24, 35 + row++ * 24), false, 14);
                continue;
            }
            var firstChanged = Math.Max(0, result.UnchangedSampleCount - 1);
            var previous = result.PreviousRoute.Skip(firstChanged).Select(s => Model(s.PositionMetres)).ToArray();
            if (previous.Length > 1) e.Display.DrawDottedPolyline(previous, Color.Gray, false);
            var route = result.Journey.Samples.Select(s => Model(s.PositionMetres)).ToArray();
            e.Display.DrawPolyline(route, current ? Color.RoyalBlue : Color.Gray, 2);
            var changed = route.Skip(firstChanged).ToArray();
            if (changed.Length > 1) e.Display.DrawPolyline(changed, current ? Color.DarkCyan : Color.Gray, 3);
            DrawRegion(result.Body.Region!, current ? Color.SteelBlue : Color.Gray);
            DrawRegion(result.Clearance.Region!, current ? Color.SeaGreen : Color.Gray);
            if (current && result.Journey.RequestedAngleExceeded)
                e.Display.Draw2dText("Steering limit reached — one or more aims cannot be followed exactly", Color.OrangeRed,
                    new Point2d(24, 35 + row++ * 24), false, 14);

            void DrawRegion(SweptRegion region, Color colour)
            {
                foreach (var loop in new[] { region.OuterBoundary }.Concat(region.Holes))
                    e.Display.DrawPolyline(loop.Append(loop[0]).Select(p =>
                        new Point3d(p.X * scale, p.Y * scale, result.Journey.Samples[0].PositionMetres.Z * scale)), colour, 2);
            }
        }
        Point3d Model(Point3 p) => new(p.X * scale, p.Y * scale, p.Z * scale);
    }
}
