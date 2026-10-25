using Rhino;
using Rhino.DocObjects;
using RhinoRoad.Core;

namespace RhinoRoad.Rhino.Services;

/// <summary>
/// Everything RhinoRoad keeps for one open document: the edit palette and inspector open on it, the
/// live preview session, and the lookups that save rescanning or reparsing the document. These were
/// static dictionaries spread over several classes, each with its own idea of when a document went
/// away — the forced-draft set was not keyed by document at all — so one closing document could
/// leave state behind or be cleaned up in the wrong order. Now there is one owner, dropped whole
/// when the document closes.
/// </summary>
internal sealed class RoadDocumentSession
{
    private static readonly Dictionary<uint, RoadDocumentSession> Sessions = new();

    // A copied control line carries the same stable id as its original, so which of the two
    // resolves is the first one the document lists, exactly as the scan this index replaces chose.
    private Dictionary<string, Guid>? _sourceIndex;
    private readonly Dictionary<uint, CachedDefinition> _definitions = new();

    private sealed record CachedDefinition(string? Json, VehicleAccessDefinition? Definition, string? Error);

    static RoadDocumentSession()
    {
        RhinoDoc.AddRhinoObject += (_, e) => Changed(e.TheObject.Document, e.TheObject, null);
        RhinoDoc.UndeleteRhinoObject += (_, e) => Changed(e.TheObject.Document, e.TheObject, null);
        RhinoDoc.DeleteRhinoObject += (_, e) => Changed(e.TheObject.Document, e.TheObject, e.TheObject);
        RhinoDoc.ReplaceRhinoObject += (_, e) => Changed(e.Document, e.NewRhinoObject, e.OldRhinoObject);
        RhinoDoc.ModifyObjectAttributes += (_, e) =>
        {
            if (Find(e.Document) is not { } session) return;
            if (CarriesDefinition(e.OldAttributes) || CarriesDefinition(e.NewAttributes)) session._sourceIndex = null;
            session._definitions.Remove(e.RhinoObject.RuntimeSerialNumber);
            session.Touched(e.RhinoObject.Id);
        };
        RhinoDoc.EndOpenDocument += (_, e) =>
        {
            if (Find(e.Document) is not { } session) return;
            session._sourceIndex = null;
            session._definitions.Clear();
            if (session.LivePreview is { } preview) preview.Dirty = true;
        };
        RhinoDoc.CloseDocument += (_, e) =>
        {
            if (!Sessions.Remove(e.Document.RuntimeSerialNumber, out var session)) return;
            session.Close();
        };
    }

    private RoadDocumentSession(uint serial) => DocumentSerial = serial;

    public uint DocumentSerial { get; }

    /// <summary>The Edit Road palette open on this document, if any. One journey is edited at a time.</summary>
    public JourneyEditForm? EditForm { get; set; }

    /// <summary>The Inspect Road palette open on this document, if any.</summary>
    public VehicleAccessInspectorForm? InspectorForm { get; set; }

    /// <summary>The stable id of the journey whose edits are previewed live.</summary>
    public Guid? EditingStableId { get; set; }

    /// <summary>The live preview's working state, rebuilt from scratch whenever a session starts.</summary>
    public LiveJourneyEditConduit.PreviewState? LivePreview { get; set; }

    /// <summary>Journeys previewed even though their grips match the saved controls: an unsaved draft.</summary>
    public HashSet<Guid> ForcedDrafts { get; } = new();

    /// <summary>Raised for each object added, deleted, replaced or given new attributes in this document.</summary>
    public event Action<Guid>? ObjectChanged;

    /// <summary>Raised when the live preview starts, stops, goes stale or finishes a new result.</summary>
    public event Action? PreviewChanged;

    /// <summary>Makes sure the document events are being listened to before any session exists.</summary>
    public static void Initialize()
    {
    }

    public static RoadDocumentSession For(RhinoDoc document)
    {
        if (!Sessions.TryGetValue(document.RuntimeSerialNumber, out var session))
            Sessions[document.RuntimeSerialNumber] = session = new RoadDocumentSession(document.RuntimeSerialNumber);
        return session;
    }

    public static RoadDocumentSession? Find(RhinoDoc? document) =>
        document is not null && Sessions.TryGetValue(document.RuntimeSerialNumber, out var session) ? session : null;

    public static bool AnyEditing => Sessions.Values.Any(session => session.EditingStableId is not null);

    public void RaisePreviewChanged() => PreviewChanged?.Invoke();

    /// <summary>The saved source carrying <paramref name="stableSourceId"/>, from an index kept per document.</summary>
    public RhinoObject? FindSource(RhinoDoc document, string stableSourceId)
    {
        _sourceIndex ??= BuildSourceIndex(document);
        if (!_sourceIndex.TryGetValue(stableSourceId, out var objectId)) return null;
        if (document.Objects.FindId(objectId) is { } found && IsSource(found, stableSourceId)) return found;
        // Every change to a source drops the index, so a stale entry means an event was missed;
        // one rescan answers correctly rather than trusting it.
        _sourceIndex = BuildSourceIndex(document);
        return _sourceIndex.TryGetValue(stableSourceId, out objectId) ? document.Objects.FindId(objectId) : null;
    }

    /// <summary>
    /// The definition saved on <paramref name="source"/>, parsed once per object. The live preview
    /// and the control labels read it on every redraw, and parsing the JSON each frame was most of
    /// their cost. The saved text is still compared, which is cheap, so a change that reached the
    /// object without an event can never be served stale.
    /// </summary>
    public bool TryReadDefinition(RhinoObject source, out VehicleAccessDefinition? definition, out string? json)
    {
        json = source.Attributes.GetUserString(AccessDefinitionStore.DefinitionKey);
        if (!_definitions.TryGetValue(source.RuntimeSerialNumber, out var cached) ||
            !string.Equals(cached.Json, json, StringComparison.Ordinal))
        {
            var read = AccessDefinitionSerializer.TryDeserialize(json, out var parsed, out var error);
            _definitions[source.RuntimeSerialNumber] = cached = new CachedDefinition(json, read ? parsed : null, error);
        }
        definition = cached.Definition;
        return definition is not null;
    }

    private static void Changed(RhinoDoc? document, RhinoObject? current, RhinoObject? previous)
    {
        if (Find(document) is not { } session) return;
        if ((current is not null && CarriesDefinition(current.Attributes)) ||
            (previous is not null && CarriesDefinition(previous.Attributes)))
            session._sourceIndex = null;
        if (previous is not null) session._definitions.Remove(previous.RuntimeSerialNumber);
        if ((current ?? previous) is { } changed) session.Touched(changed.Id);
    }

    private void Touched(Guid objectId)
    {
        if (LivePreview is { } preview) preview.Dirty = true;
        ObjectChanged?.Invoke(objectId);
    }

    private static bool CarriesDefinition(ObjectAttributes? attributes) =>
        attributes?.GetUserString(AccessDefinitionStore.DefinitionKey) is not null;

    private static bool IsSource(RhinoObject candidate, string stableSourceId) =>
        CarriesDefinition(candidate.Attributes) &&
        string.Equals(candidate.Attributes.GetUserString(AccessDefinitionStore.SourceIdKey), stableSourceId,
            StringComparison.OrdinalIgnoreCase);

    private static Dictionary<string, Guid> BuildSourceIndex(RhinoDoc document)
    {
        var index = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);
        foreach (var candidate in DocumentObjects.All(document))
        {
            if (!CarriesDefinition(candidate.Attributes)) continue;
            var stableId = candidate.Attributes.GetUserString(AccessDefinitionStore.SourceIdKey);
            if (!string.IsNullOrWhiteSpace(stableId)) index.TryAdd(stableId, candidate.Id);
        }
        return index;
    }

    /// <summary>
    /// The document is going away: nothing here may touch it or ask it to redraw, so the palettes
    /// are closed without discarding into it.
    /// </summary>
    private void Close()
    {
        EditingStableId = null;
        LivePreview = null;
        ForcedDrafts.Clear();
        _definitions.Clear();
        _sourceIndex = null;
        ObjectChanged = null;
        PreviewChanged = null;
        var editor = EditForm;
        EditForm = null;
        editor?.Abandon();
        var inspector = InspectorForm;
        InspectorForm = null;
        if (inspector?.Visible == true) inspector.Close();
        LiveJourneyEditConduit.RefreshEnabled();
    }
}
