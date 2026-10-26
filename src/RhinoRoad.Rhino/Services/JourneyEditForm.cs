using Eto.Forms;
using Rhino;
using Rhino.DocObjects;
using Rhino.Geometry;
using Rhino.Input;
using Rhino.Input.Custom;
using Rhino.UI;
using RhinoRoad.Core;
using RhinoRoad.Rhino.Commands;
using RhinoRoad.Rhino.UI;
using CommandResult = global::Rhino.Commands.Result;
using EtoSize = Eto.Drawing.Size;
using EtoPoint = Eto.Drawing.Point;

namespace RhinoRoad.Rhino.Services;

/// <summary>
/// The modeless surface of an <c>RREditRoad</c> session. Rhino will not let a running command take
/// grip drags, so the previous flow had to end its prompt and leave the user to remember
/// <c>RRUpdateRoad</c> — the edit had no visible beginning or end. A palette can stay open while the
/// grips are dragged, so the session now reads as one thing: it opens, the numbered controls and the
/// live sweep appear, driving intent is changed in place, and Save or Discard closes it.
/// </summary>
/// <remarks>
/// Every change the palette makes is the draft: it is written to the control line so the live
/// preview shows it, Save commits it through RRUpdateRoad, and Discard restores the road as opened.
/// The palette follows the document through its events rather than a timer, so undo, a grip drag
/// or a deleted yard reach it as they happen and an idle palette costs nothing.
/// </remarks>
internal sealed class JourneyEditForm : Form
{
    internal static readonly VehicleCatalog Catalog = VehicleCatalog.LoadEmbedded();

    private const int PaletteWidth = 360;
    private const int CompactHeight = 600;
    private const int SearchHeight = 420;

    private readonly RhinoDoc _document;
    private readonly RoadDocumentSession _session;
    private readonly Guid _sourceId;
    private readonly Curve _originalCurve;
    private readonly VehicleAccessDefinition _originalDefinition;
    // The text as opened, and as this version writes it: either means the definition is unchanged.
    private readonly string? _originalJson;
    private readonly string _originalSerialized;
    private readonly Label _status = new() { Wrap = WrapMode.Word };
    private readonly Label _controlsStatus = new() { Wrap = WrapMode.Word };
    private readonly Label _title = new();
    private readonly DropDown _startDirection = new();
    private readonly StackLayout _controls = new() { Spacing = 3 };
    private readonly JourneyEditLabelConduit _labels = new();
    private readonly AreaSearchPanel _areaSearch;
    private bool _saved;
    private bool _closing;
    private bool _abandoned;
    private bool _suppressDirectionEvent;
    private bool _draftApplied;
    private bool _refreshQueued;
    private bool _sourceDirty;
    private bool _siteDirty;
    // The saved text the rows were last built from, so an undo or another tool's change rebuilds them.
    private string? _shownJson;

    private JourneyEditForm(RhinoDoc document, RhinoObject source, VehicleAccessDefinition definition, Curve curve)
    {
        _document = document;
        _session = RoadDocumentSession.For(document);
        _sourceId = source.Id;
        StableId = definition.StableSourceId;
        _originalCurve = (Curve)curve.Duplicate();
        _originalDefinition = definition;
        _originalJson = source.Attributes.GetUserString(AccessDefinitionStore.DefinitionKey);
        _originalSerialized = AccessDefinitionSerializer.Serialize(definition);

        Title = "Edit Road";
        ClientSize = new EtoSize(PaletteWidth, CompactHeight);
        MinimumSize = new EtoSize(300, 420);
        Padding = 12;
        Resizable = true;
        Maximizable = false;
        Minimizable = false;
        ShowInTaskbar = false;
        // Without an owner the palette is an independent top-level window: clicking back into the
        // viewport sends it behind the Rhino frame, where it is effectively lost.
        Owner = RhinoEtoApp.MainWindowForDocument(document);
        this.UseRhinoStyle();

        _title.Text = string.IsNullOrWhiteSpace(source.Name)
            ? $"Road {definition.StableSourceId.ToString("N")[..8]}"
            : source.Name;

        _startDirection.Items.Add(new ListItem { Key = nameof(TravelDirection.Forward), Text = "Starts forward" });
        _startDirection.Items.Add(new ListItem { Key = nameof(TravelDirection.Reverse), Text = "Starts reversing" });
        _startDirection.SelectedKeyChanged += (_, _) =>
        {
            if (_suppressDirectionEvent || Read() is not { Manoeuvre: { } journey } saved) return;
            if (!Enum.TryParse(_startDirection.SelectedKey, out TravelDirection chosen) || chosen == journey.StartDirection) return;
            // Reversing the start flips every leg, so the reversal pattern the user drove is kept.
            WriteIntent(saved, ManoeuvreControlReconciler.WithStartDirection(journey, chosen), "RhinoRoad change start direction");
        };

        var heading = new Button { Text = "Set start heading…" };
        heading.Click += (_, _) => PickHeading();
        var settings = new Button { Text = "Settings…", ToolTip = "Vehicle, mode, clearance and checks for this road. The change is part of the draft until Save." };
        settings.Click += (_, _) => RunSettings();
        var save = new Button { Text = "Save" };
        save.Click += (_, _) => Save();
        var discard = new Button { Text = "Discard" };
        discard.Click += (_, _) => Discard();

        _areaSearch = new AreaSearchPanel(this, document, _sourceId, _labels, definition);
        _areaSearch.LayoutChanged += FitToScreen;

        Content = new StackLayout
        {
            Spacing = 10,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Items =
            {
                _title,
                new Label { Text = "Drag the numbered control points in the viewport. The blue sweep is the live preview.", Wrap = WrapMode.Word },
                _status,
                new GroupBox
                {
                    Text = "Driving intent",
                    Padding = 8,
                    Content = new StackLayout
                    {
                        Spacing = 6,
                        HorizontalContentAlignment = HorizontalAlignment.Stretch,
                        Items = { _startDirection, heading }
                    }
                },
                new StackLayoutItem(new GroupBox
                {
                    Text = "Controls",
                    Padding = 8,
                    Content = new Scrollable { Border = BorderType.None, ExpandContentWidth = true, Content = _controls }
                }, true),
                _controlsStatus,
                _areaSearch,
                new StackLayout
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 6,
                    Items = { save, discard, null, settings }
                }
            }
        };

        Closing += (_, args) =>
        {
            // Closing the palette is the same decision as Discard; without this a half-finished drag
            // would be left on the curve with nothing on screen saying the road is now out of date.
            if (_saved || _closing) return;
            args.Cancel = true;
            Discard();
        };
        Closed += (_, _) => Teardown();
        Shown += (_, _) =>
        {
            try
            {
                _labels.SourceId = _sourceId;
                _labels.Enabled = true;
                _session.ObjectChanged += OnObjectChanged;
                _session.PreviewChanged += QueueRefresh;
                LiveJourneyEditConduit.Begin(_document, StableId);
            }
            catch (Exception error)
            {
                RhinoApp.WriteLine($"RhinoRoad could not start the edit preview: {error.Message}");
            }
        };

        Rebuild(definition);
        _shownJson = _originalJson;
        _status.Text = "No change yet. The saved result is what you see.";
    }

    public Guid StableId { get; }

    /// <summary>Opens, or brings forward, the edit session for one saved journey.</summary>
    public static CommandResult Start(RhinoDoc document, RhinoObject source, VehicleAccessDefinition definition, Curve curve)
    {
        var session = RoadDocumentSession.For(document);
        if (session.EditForm is { } existing)
        {
            if (existing._sourceId == source.Id)
            {
                existing.BringToFront();
                return CommandResult.Success;
            }
            existing.Discard();
        }
        if (source.IsLocked)
        {
            RhinoApp.WriteLine("Unlock the control line before editing it.");
            return CommandResult.Failure;
        }

        var form = new JourneyEditForm(document, source, definition, curve);
        session.EditForm = form;
        document.Objects.UnselectAll();
        document.Objects.Select(source.Id);
        source.GripsOn = true;
        document.Views.Redraw();
        RhinoApp.WriteLine("Editing road. Drag the control points; Save in the Edit Road palette keeps the result and reruns the checks.");
        Application.Instance.AsyncInvoke(() =>
        {
            try
            {
                form.Show();
                form.PlaceOnScreen();
            }
            catch (Exception error)
            {
                RhinoApp.WriteLine($"RhinoRoad could not open Edit Road: {error.Message}");
                if (session.EditForm == form) session.EditForm = null;
                form.Teardown();
            }
        });
        return CommandResult.Success;
    }

    public static bool IsEditing(RhinoDoc document, Guid sourceObjectId) =>
        RoadDocumentSession.Find(document)?.EditForm?._sourceId == sourceObjectId;

    /// <summary>
    /// Starts beside the active view, as before, but kept inside the screen's working area: the
    /// palette was 860 px tall, more than many laptop screens, and opened with its buttons off-screen.
    /// </summary>
    private void PlaceOnScreen()
    {
        if (_document.Views.ActiveView is { } view)
        {
            var rect = view.ScreenRectangle;
            Location = new EtoPoint(rect.Left + 18, rect.Top + 12);
        }
        FitToScreen();
    }

    /// <summary>Sizes the palette for what it shows -- taller while the search is open -- within the screen.</summary>
    private void FitToScreen()
    {
        if (_closing || !Visible) return;
        var area = (Screen.FromPoint(new Eto.Drawing.PointF(Location.X, Location.Y)) ?? Screen.PrimaryScreen).WorkingArea;
        const int margin = 8;
        var wanted = CompactHeight + (_areaSearch.Visible && _areaSearch.Expanded ? SearchHeight : 0);
        var width = (int)Math.Max(MinimumSize.Width, Math.Min(Math.Max(Width, PaletteWidth), area.Width - (2 * margin)));
        var height = (int)Math.Max(MinimumSize.Height, Math.Min(wanted, area.Height - (2 * margin)));
        var x = (int)Math.Max(area.Left + margin, Math.Min(Location.X, area.Right - width - margin));
        var y = (int)Math.Max(area.Top + margin, Math.Min(Location.Y, area.Bottom - height - margin));
        Size = new EtoSize(width, height);
        Location = new EtoPoint(x, y);
    }

    internal VehicleAccessDefinition? Read() =>
        _document.Objects.FindId(_sourceId) is { } source &&
        AccessDefinitionStore.TryRead(source, out var definition, out _) ? definition : null;

    /// <summary>The control line for a journey: its start, then each control, in model units.</summary>
    internal Curve ControlLine(ManoeuvreDefinition journey)
    {
        var scale = RhinoMath.UnitScale(UnitSystem.Meters, _document.ModelUnitSystem);
        return new PolylineCurve(new[] { journey.StartPositionMetres }
            .Concat(journey.Controls.Select(control => control.PositionMetres))
            .Select(point => new Point3d(point.X * scale, point.Y * scale, point.Z * scale)));
    }

    /// <summary>
    /// Writes changed intent back to the source without rebaking. Only Direction, Kind and heading
    /// move: the stored positions stay exactly as saved, so the live preview keeps comparing the
    /// dragged grips against the saved journey rather than quietly adopting them. A pick that moves
    /// the bay passes the moved control line with it, so the two land in one undo step.
    /// </summary>
    private bool WriteIntent(VehicleAccessDefinition definition, ManoeuvreDefinition journey, string undoDescription,
        Curve? controlLine = null) =>
        WriteDraft(controlLine, definition with { Manoeuvre = journey }, undoDescription);

    /// <summary>
    /// Puts a change on the control line as the unsaved edit, as one undo step: a new line when
    /// <paramref name="curve"/> is given, and the definition. Save commits it like any other edit and
    /// Discard still restores the road as it was opened.
    /// </summary>
    internal bool WriteDraft(Curve? curve, VehicleAccessDefinition definition, string undoDescription)
    {
        if (_document.Objects.FindId(_sourceId) is not { Geometry: Curve previousCurve } source || Read() is not { } previous)
        {
            _status.Text = "The road can no longer be read. Close Edit Road and open it again.";
            return false;
        }
        var previousCopy = (Curve)previousCurve.Duplicate();
        var written = DocumentUndo.Record(_document, undoDescription, () =>
        {
            if (curve is not null)
            {
                source.GripsOn = false;
                if (!_document.Objects.Replace(_sourceId, curve)) return false;
            }
            if (AccessDefinitionStore.Write(_document, _sourceId, definition)) return true;
            if (curve is not null) _document.Objects.Replace(_sourceId, previousCopy);
            AccessDefinitionStore.Write(_document, _sourceId, previous);
            return false;
        });
        if (!written)
        {
            RestoreEditSelection();
            _status.Text = "That change could not be applied; the previous edit remains.";
            return false;
        }
        Rebuild(definition);
        UpdateDraft();
        RestoreEditSelection();
        return true;
    }

    /// <summary>
    /// Previews the draft whenever it differs from the road as opened, even with every grip where the
    /// saved controls are -- as after a suggestion, a settings change or an inserted control.
    /// </summary>
    private void UpdateDraft()
    {
        if (_document.Objects.FindId(_sourceId) is not { Geometry: Curve current } source) return;
        var json = source.Attributes.GetUserString(AccessDefinitionStore.DefinitionKey);
        var draft = !GeometryBase.GeometryEquals(current, _originalCurve) ||
            !(string.Equals(json, _originalJson, StringComparison.Ordinal) ||
              string.Equals(json, _originalSerialized, StringComparison.Ordinal));
        if (draft == _draftApplied) return;
        _draftApplied = draft;
        LiveJourneyEditConduit.ForceDraft(_document, StableId, draft);
    }

    private void Rebuild(VehicleAccessDefinition definition)
    {
        _shownJson = AccessDefinitionSerializer.Serialize(definition);
        var journey = definition.Manoeuvre;
        _suppressDirectionEvent = true;
        _startDirection.SelectedKey = (journey?.StartDirection ?? TravelDirection.Forward).ToString();
        _suppressDirectionEvent = false;

        _controls.Items.Clear();
        if (journey is not null)
            for (var index = 0; index < journey.Controls.Count; index++)
                _controls.Items.Add(Row(definition, journey, index));
        _areaSearch.Refresh(definition);
    }

    private void OnObjectChanged(Guid objectId)
    {
        if (objectId == _sourceId) _sourceDirty = true;
        else if (_areaSearch.UsesSite(objectId)) _siteDirty = true;
        else return;
        QueueRefresh();
    }

    /// <summary>
    /// Document and preview events arrive mid-change, often several for one edit, so they only ask
    /// for a refresh; it runs once, after the change is complete.
    /// </summary>
    private void QueueRefresh()
    {
        if (_refreshQueued || _closing) return;
        _refreshQueued = true;
        Application.Instance.AsyncInvoke(() =>
        {
            _refreshQueued = false;
            if (!_closing) Refresh();
        });
    }

    private void Refresh()
    {
        if (_document.Objects.FindId(_sourceId) is not { } source)
        {
            _status.Text = "The control line has been deleted.";
            return;
        }
        if (_siteDirty)
        {
            _siteDirty = false;
            _areaSearch.RefreshSite();
        }
        if (_sourceDirty)
        {
            _sourceDirty = false;
            AccessDefinitionStore.TryReadCached(source, out var definition, out var json);
            // An undo, or another tool, changed the road under the palette; show what it is now.
            if (definition is not null && !string.Equals(json, _shownJson, StringComparison.Ordinal)) Rebuild(definition);
            UpdateDraft();
            _areaSearch.SourceChanged(definition);
        }
        // This runs for each frame of a drag, and setting a label's text relays the palette out.
        var status = LiveJourneyEditConduit.IsPreviewing(_document, StableId)
            ? "Unsaved edit — Save reruns the checks and refreshes the sizing results."
            : "No change yet. The saved result is what you see.";
        if (_status.Text != status) _status.Text = status;
        _areaSearch.UpdateReadout();
    }

    private enum ControlEdit { InsertBefore, InsertAfter, Delete }

    private Control Row(VehicleAccessDefinition definition, ManoeuvreDefinition journey, int index)
    {
        var control = journey.Controls[index];
        var last = index == journey.Controls.Count - 1;
        var number = new Label { Text = $"{index + 1}.", Width = 24, VerticalAlignment = VerticalAlignment.Center };

        var direction = new DropDown { Width = 88 };
        direction.Items.Add(new ListItem { Key = nameof(TravelDirection.Forward), Text = "Forward" });
        direction.Items.Add(new ListItem { Key = nameof(TravelDirection.Reverse), Text = "Reverse" });
        direction.SelectedKey = control.Direction.ToString();
        direction.SelectedKeyChanged += (_, _) =>
        {
            if (!Enum.TryParse(direction.SelectedKey, out TravelDirection chosen) || chosen == control.Direction) return;
            Replace(definition, journey, index, control with { Direction = chosen }, "RhinoRoad change control direction");
        };

        // Turn is legal anywhere in the route, so the whole row is no longer last-only; Finish still
        // is, and is the one entry withheld from the middle of a journey.
        var kind = new DropDown { Width = 74 };
        kind.Items.Add(new ListItem { Key = nameof(ManoeuvreControlKind.Aim), Text = "Aim" });
        kind.Items.Add(new ListItem { Key = nameof(ManoeuvreControlKind.Turn), Text = "Turn" });
        if (last) kind.Items.Add(new ListItem { Key = nameof(ManoeuvreControlKind.Finish), Text = "Finish" });
        kind.SelectedKey = control.Kind.ToString();
        kind.ToolTip = last
            ? "Aim drives towards this point; Turn swings at full lock by twice its bearing; Finish stops the vehicle on it."
            : "Aim drives towards this point; Turn swings at full lock by twice its bearing. Only the last control can be a Finish.";
        kind.SelectedKeyChanged += (_, _) =>
        {
            if (!Enum.TryParse(kind.SelectedKey, out ManoeuvreControlKind chosen) || chosen == control.Kind) return;
            Replace(definition, journey, index, control with { Kind = chosen }, "RhinoRoad change control kind");
        };

        var heading = new Button
        {
            Width = 64,
            Text = control.ExitHeadingRadians is double exit ? $"{JourneyEditPicks.Degrees(exit):0.#}°" : "Dir…",
            Enabled = control.Kind != ManoeuvreControlKind.Turn,
            ToolTip = control.Kind == ManoeuvreControlKind.Turn
                ? "A full-lock Turn has no direction to leave along."
                : control.ExitHeadingRadians is null
                    ? "Pick the direction to leave this control along (ortho and snaps apply)."
                    : "Change the direction this control leaves along; Enter while picking clears it."
        };
        heading.Click += (_, _) => PickControlDirection(index);

        // Grips can only move a control, so adding or removing one is offered here.
        var more = new Button { Text = "…", Width = 28, ToolTip = "Insert a control before or after this one, or delete it." };
        var menu = new ContextMenu();
        var before = new ButtonMenuItem { Text = "Insert control before" };
        before.Click += (_, _) => EditControl(index, ControlEdit.InsertBefore);
        var after = new ButtonMenuItem { Text = "Insert control after" };
        after.Click += (_, _) => EditControl(index, ControlEdit.InsertAfter);
        var delete = new ButtonMenuItem { Text = "Delete control", Enabled = journey.Controls.Count > 1 };
        delete.Click += (_, _) => EditControl(index, ControlEdit.Delete);
        menu.Items.Add(before);
        menu.Items.Add(after);
        menu.Items.Add(delete);
        more.Click += (_, _) => menu.Show(more);

        var row = new StackLayout
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            VerticalContentAlignment = VerticalAlignment.Center,
            Items = { number, direction, kind, heading, more }
        };
        row.MouseEnter += (_, _) => Highlight(index);
        row.MouseLeave += (_, _) => Highlight(null);
        return row;
    }

    private void Replace(VehicleAccessDefinition definition, ManoeuvreDefinition journey, int index,
        ManoeuvreControl replacement, string undoDescription)
    {
        var controls = journey.Controls.ToArray();
        controls[index] = replacement;
        WriteIntent(definition, journey with { Controls = controls }, undoDescription);
    }

    /// <summary>
    /// Inserts or deletes a control. The control line and the stored controls change together, one
    /// vertex per control, so the reconciler and the preview read them as the user sees them. The
    /// grips' current positions are taken first, so a drag made before the edit is kept.
    /// </summary>
    private void EditControl(int index, ControlEdit edit)
    {
        if (_areaSearch.Searching) return;
        if (Read() is not { Manoeuvre: { } stored } definition ||
            _document.Objects.FindId(_sourceId)?.Geometry is not Curve controlCurve ||
            index < 0 || index >= stored.Controls.Count)
        {
            _controlsStatus.Text = "The saved road could not be read. Close Edit Road and open it again.";
            return;
        }
        var vertices = AccessDefinitionStore.PolylineVertices(controlCurve, _document.ModelUnitSystem);
        if (vertices.Count != stored.Controls.Count + 1)
        {
            _controlsStatus.Text = "The control line no longer matches its controls. Discard and open Edit Road again.";
            return;
        }
        if (edit == ControlEdit.Delete && stored.Controls.Count == 1)
        {
            _controlsStatus.Text = "A road needs at least one control.";
            return;
        }
        var current = ManoeuvreControlReconciler.Reconcile(stored, vertices);
        var edited = edit switch
        {
            ControlEdit.InsertBefore => ManoeuvreControlEdits.Insert(current, index),
            ControlEdit.InsertAfter => ManoeuvreControlEdits.Insert(current, index + 1),
            _ => ManoeuvreControlEdits.Delete(current, index)
        };
        _areaSearch.ClearSuggestions();
        Highlight(null);
        var description = edit == ControlEdit.Delete ? "RhinoRoad delete control" : "RhinoRoad insert control";
        if (!WriteDraft(ControlLine(edited), definition with { Manoeuvre = edited }, description)) return;
        var inserted = edit == ControlEdit.InsertBefore ? index + 1 : index + 2;
        _controlsStatus.Text = edit == ControlEdit.Delete
            ? $"Control {index + 1} deleted. The blue sweep previews the road without it; Save keeps it."
            : $"Control {inserted} inserted as a plain aim. Drag it into place; Save keeps it.";
    }

    private void Highlight(int? index)
    {
        if (_labels.Highlighted == index) return;
        _labels.Highlighted = index;
        _document.Views.Redraw();
    }

    private void PickHeading()
    {
        if (Read() is not { Manoeuvre: { } journey } definition) return;
        if (_document.Objects.FindId(_sourceId)?.Geometry is not Curve curve) return;
        var start = curve.PointAtStart;
        using var aim = new GetPoint();
        aim.SetCommandPrompt("Pick the vehicle's starting forward heading");
        aim.SetBasePoint(start, true);
        aim.DrawLineFromPoint(start, true);
        if (aim.Get() != GetResult.Point) return;
        var delta = aim.Point() - start;
        if (new Vector2d(delta.X, delta.Y).Length <= _document.ModelAbsoluteTolerance)
        {
            RhinoApp.WriteLine("That point is on the start of the route, so it gives no heading.");
            return;
        }
        WriteIntent(definition, journey with { StartHeadingRadians = Math.Atan2(delta.Y, delta.X) }, "RhinoRoad set start heading");
    }

    /// <summary>
    /// Places the bay the way it is dimensioned on a site plan: the rear of the trailer, then the way
    /// it reverses in. The journey stores the trailer axle, which follows from those two, and the
    /// control line's last vertex moves to it as though its grip had been dragged. The direction is a
    /// real pick from the rear, so Rhino's ortho and object snaps constrain it as they would a line.
    /// </summary>
    internal void PickTrailerBay(Action<string> report)
    {
        if (Read() is not { Manoeuvre: { } journey } definition ||
            _document.Objects.FindId(_sourceId)?.Geometry is not Curve controlCurve)
        {
            report("The saved road could not be read. Close Edit Road and open it again.");
            return;
        }
        if (journey.Controls.Count == 0 || journey.Controls[^1] is not { Direction: TravelDirection.Reverse } final)
        {
            report("The road must end reversing into the bay. Set the last control to Reverse, or redraw it with PlanReverse.");
            return;
        }
        var vehicle = Catalog.Get(definition.VehicleId);
        if (!vehicle.IsArticulated)
        {
            report("Bay placement is for articulated vehicles.");
            return;
        }
        var scale = RhinoMath.UnitScale(UnitSystem.Meters, _document.ModelUnitSystem);
        var vertices = AccessDefinitionStore.PolylineVertices(controlCurve, _document.ModelUnitSystem);
        if (vertices.Count != journey.Controls.Count + 1)
        {
            report("The control line no longer matches its controls. Discard and open Edit Road again.");
            return;
        }
        var axleNow = vertices[^1];
        Point3? rearNow = final.ExitHeadingRadians is double headingNow
            ? TrailerBay.RearFromAxle(vehicle, axleNow, headingNow) : null;

        report("In the viewport: click the rear of the trailer at the bay"
            + (rearNow is null ? "." : " (Enter keeps it).")
            + " Then point the way it reverses in; ortho and snaps apply, or type an angle.");
        _document.Objects.UnselectAll();
        Point3 rear;
        using (var rearPicker = new GetPoint())
        {
            rearPicker.SetCommandPrompt(rearNow is null
                ? "Pick the rear of the trailer at the bay"
                : "Pick the rear of the trailer at the bay; Enter keeps the current position");
            rearPicker.AcceptNothing(rearNow is not null);
            if (rearNow is { } current)
                rearPicker.SetBasePoint(new Point3d(current.X * scale, current.Y * scale, current.Z * scale), true);
            var rearResult = rearPicker.Get();
            if (rearResult == GetResult.Nothing && rearNow is { } kept) rear = kept;
            else if (rearResult == GetResult.Point)
            {
                var point = rearPicker.Point();
                rear = new Point3(point.X / scale, point.Y / scale, point.Z / scale);
            }
            else
            {
                RestoreEditSelection();
                report("Trailer bay unchanged.");
                return;
            }
        }

        var pivot = new Point3d(rear.X * scale, rear.Y * scale, rear.Z * scale);
        var picked = JourneyEditPicks.PickDirection(_document, pivot,
            "Point the way the trailer reverses into the bay, or type an angle from the CPlane",
            allowClear: false,
            (display, heading) => JourneyEditPicks.DrawDockedTrailer(_document, display, vehicle,
                TrailerBay.AxleFromRear(vehicle, rear, heading), heading),
            out var bayHeading);
        if (picked != DirectionPick.Picked)
        {
            RestoreEditSelection();
            report("Trailer bay unchanged.");
            return;
        }

        var axle = TrailerBay.AxleFromRear(vehicle, rear, bayHeading);
        var controls = journey.Controls.ToArray();
        controls[^1] = final with { ExitHeadingRadians = bayHeading, Kind = ManoeuvreControlKind.Aim };
        _areaSearch.ClearSuggestions();
        var points = vertices.Take(vertices.Count - 1).Append(axle)
            .Select(point => new Point3d(point.X * scale, point.Y * scale, point.Z * scale));
        if (WriteIntent(definition, journey with { Controls = controls }, "RhinoRoad set trailer bay", new PolylineCurve(points)))
            report("Trailer bay set. Search to compare approaches, or Save.");
    }

    /// <summary>
    /// Changes the direction a control leaves along. The final reverse into a bay turns about the
    /// trailer's rear, which stays where it is, so the bay is re-angled rather than moved; any other
    /// control turns about its own point. Enter clears the direction, returning the leg to a plain aim.
    /// </summary>
    private void PickControlDirection(int index)
    {
        if (_areaSearch.Searching) return;
        if (Read() is not { Manoeuvre: { } journey } definition ||
            _document.Objects.FindId(_sourceId)?.Geometry is not Curve controlCurve ||
            index < 0 || index >= journey.Controls.Count)
        {
            _controlsStatus.Text = "The saved road could not be read. Close Edit Road and open it again.";
            return;
        }
        var control = journey.Controls[index];
        if (control.Kind == ManoeuvreControlKind.Turn)
        {
            _controlsStatus.Text = $"Control {index + 1} is a full-lock Turn, which has no direction to leave along. Set it to Aim first.";
            return;
        }
        var vertices = AccessDefinitionStore.PolylineVertices(controlCurve, _document.ModelUnitSystem);
        if (vertices.Count != journey.Controls.Count + 1)
        {
            _controlsStatus.Text = "The control line no longer matches its controls. Discard and open Edit Road again.";
            return;
        }
        var vehicle = Catalog.Get(definition.VehicleId);
        var bay = index == journey.Controls.Count - 1 && control.Direction == TravelDirection.Reverse && vehicle.IsArticulated;
        if (bay && control.ExitHeadingRadians is null)
        {
            // Without a direction the stored point is the axle, and the rear is not yet known.
            PickTrailerBay(message => _controlsStatus.Text = message);
            return;
        }

        var scale = RhinoMath.UnitScale(UnitSystem.Meters, _document.ModelUnitSystem);
        var point = vertices[index + 1];
        var rear = bay ? TrailerBay.RearFromAxle(vehicle, point, control.ExitHeadingRadians!.Value) : point;
        _controlsStatus.Text = bay
            ? "In the viewport: point the way the trailer reverses in; it turns about its rear. Ortho and snaps apply, or type an angle. Enter clears it."
            : $"In the viewport: point the way to leave control {index + 1}. Ortho and snaps apply, or type an angle. Enter clears it.";
        _document.Objects.UnselectAll();
        var picked = JourneyEditPicks.PickDirection(_document, new Point3d(rear.X * scale, rear.Y * scale, rear.Z * scale),
            bay
                ? "Point the way the trailer reverses into the bay, or type an angle from the CPlane; Enter clears it"
                : $"Point the way to leave control {index + 1}, or type an angle from the CPlane; Enter clears it",
            allowClear: true,
            bay ? (display, heading) => JourneyEditPicks.DrawDockedTrailer(_document, display, vehicle,
                    TrailerBay.AxleFromRear(vehicle, rear, heading), heading)
                : null,
            out var headingRadians);
        if (picked == DirectionPick.Cancelled)
        {
            RestoreEditSelection();
            _controlsStatus.Text = $"Control {index + 1} unchanged.";
            return;
        }

        var controls = journey.Controls.ToArray();
        controls[index] = control with { ExitHeadingRadians = picked == DirectionPick.Picked ? headingRadians : null };
        Curve? moved = null;
        if (bay && picked == DirectionPick.Picked)
        {
            var axle = TrailerBay.AxleFromRear(vehicle, rear, headingRadians);
            moved = new PolylineCurve(vertices.Select((vertex, at) => at == vertices.Count - 1 ? axle : vertex)
                .Select(vertex => new Point3d(vertex.X * scale, vertex.Y * scale, vertex.Z * scale)));
        }
        _areaSearch.ClearSuggestions();
        if (!WriteIntent(definition, journey with { Controls = controls }, "RhinoRoad change control direction", moved)) return;
        _controlsStatus.Text = picked == DirectionPick.Picked
            ? $"Control {index + 1} now leaves along {JourneyEditPicks.Degrees(headingRadians):0.#}°. The blue sweep previews it; Save keeps it."
            : $"Control {index + 1} direction cleared.";
    }

    private void RestoreEditSelection() => JourneyEditPicks.RestoreEditSelection(_document, _sourceId);

    /// <summary>
    /// The Road settings for the draft. Running <c>RRRoad</c> for this, as the palette once did,
    /// took its configure path, which committed the dragged draft while the palette still said it was
    /// unsaved, and a later Discard left the rebaked output of the discarded draft behind. The dialog
    /// is shown here instead and its answers join the draft, so Save and Discard keep their meaning.
    /// </summary>
    private void RunSettings()
    {
        if (_areaSearch.Searching) return;
        if (Read() is not { } draft)
        {
            _status.Text = "The road can no longer be read. Close Edit Road and open it again.";
            return;
        }
        var settings = new VehicleAccessSettings();
        AccessDefinitionSettings.Apply(draft, settings);
        settings.Reconcile(Catalog);
        if (!VehicleAccessDialog.Show(_document, Catalog, settings, configuringSavedSource: true)) return;
        if (settings.Source != draft.SourceKind)
            RhinoApp.WriteLine("Edit Road keeps this road driven interactively; the Source setting was not changed.");

        var configured = AccessDefinitionSettings.WithSettings(draft, settings);
        if (settings.CheckObstacles && (settings.ReselectReferences || configured.ObstacleObjectIds.Count == 0 ||
            configured.ObstacleObjectIds.Any(id => _document.Objects.FindId(id) is null)))
        {
            var obstacles = JourneyEditPicks.PickCurves(_document, _sourceId,
                "Select replacement obstacle curves; Enter for none", multiple: true, closedOnly: false);
            if (obstacles is null)
            {
                _status.Text = "Settings unchanged.";
                return;
            }
            configured = configured with { ObstacleObjectIds = obstacles };
        }
        if (settings.CheckAllowedArea && (settings.ReselectReferences || configured.AllowedBoundaryObjectIds.Count == 0 ||
            configured.AllowedBoundaryObjectIds.Any(id => _document.Objects.FindId(id) is null)))
        {
            var boundaries = JourneyEditPicks.PickCurves(_document, _sourceId,
                "Select replacement closed allowed-area boundaries; Enter for none", multiple: true, closedOnly: true);
            if (boundaries is null)
            {
                _status.Text = "Settings unchanged.";
                return;
            }
            configured = configured with { AllowedBoundaryObjectIds = boundaries };
        }
        if (settings.DrapeTerrain && (settings.ReselectReferences || configured.TerrainObjectId is not { } storedTerrain ||
                _document.Objects.FindId(storedTerrain) is null))
        {
            var terrain = TerrainDraper.Pick(_document);
            JourneyEditPicks.RestoreEditSelection(_document, _sourceId);
            if (terrain is null)
            {
                _status.Text = "Settings unchanged.";
                return;
            }
            configured = configured with { TerrainObjectId = terrain };
        }
        if (AccessDefinitionSerializer.Serialize(configured) == AccessDefinitionSerializer.Serialize(draft)) return;
        if (configured.VehicleId != draft.VehicleId) _areaSearch.ClearSuggestions();
        if (WriteDraft(null, configured, "RhinoRoad change road settings"))
            _controlsStatus.Text = "Settings changed. The blue sweep previews them; Save reruns the checks with them, Discard restores the road as opened.";
    }

    private void Save()
    {
        if (_document.Objects.FindId(_sourceId) is not { } source)
        {
            RhinoApp.WriteLine("The control line has been deleted; there is nothing to save.");
            Discard();
            return;
        }
        // End the preview first: the review conduit and the baked objects both hide themselves while
        // a preview is live, so committing underneath one leaves the new result invisible.
        LiveJourneyEditConduit.End(_document);
        LiveJourneyEditConduit.ForceDraft(_document, StableId, false);
        // Update records its own undo step here, outside any command, so Ctrl+Z brings back the
        // previous result.
        var result = RRUpdateVehicleAccessCommand.Update(_document, source);
        if (result != CommandResult.Success)
        {
            LiveJourneyEditConduit.Begin(_document, StableId);
            if (_draftApplied) LiveJourneyEditConduit.ForceDraft(_document, StableId, true);
            _status.Text = "Save failed — see the command line. The edit is still open.";
            return;
        }
        _saved = true;
        VehicleAccessInspectorService.Refresh(_document, StableId);
        Close();
    }

    private void Discard()
    {
        if (_closing) return;
        _closing = true;
        _areaSearch.Close();
        LiveJourneyEditConduit.ForceDraft(_document, StableId, false);
        _labels.SearchCandidate = null;
        if (_document.Objects.FindId(_sourceId) is { } editable) editable.GripsOn = false;
        DocumentUndo.Record(_document, "RhinoRoad discard road edit", () =>
        {
            if (_document.Objects.FindId(_sourceId) is { Geometry: Curve current } &&
                !GeometryBase.GeometryEquals(_originalCurve, current))
                _document.Objects.Replace(_sourceId, _originalCurve);
            if (_document.Objects.FindId(_sourceId) is { } source &&
                source.Attributes.GetUserString(AccessDefinitionStore.DefinitionKey) is var json &&
                !string.Equals(json, _originalJson, StringComparison.Ordinal))
                AccessDefinitionStore.Write(_document, _sourceId, _originalDefinition);
        });
        Close();
    }

    /// <summary>
    /// The document is closing. Nothing may be written to it or redrawn in it, so the palette shuts
    /// without the discard it would otherwise make.
    /// </summary>
    internal void Abandon()
    {
        _abandoned = true;
        _closing = true;
        _areaSearch.Close();
        Close();
    }

    private void Teardown()
    {
        _closing = true;
        _areaSearch.Close();
        _labels.Enabled = false;
        _session.ObjectChanged -= OnObjectChanged;
        _session.PreviewChanged -= QueueRefresh;
        if (_session.EditForm == this) _session.EditForm = null;
        if (_abandoned) return;
        LiveJourneyEditConduit.ForceDraft(_document, StableId, false);
        LiveJourneyEditConduit.End(_document);
        if (_document.Objects.FindId(_sourceId) is { } source) source.GripsOn = false;
        _document.Views.Redraw();
    }
}
