using System.Drawing;
using Eto.Forms;
using Rhino;
using Rhino.Display;
using Rhino.DocObjects;
using Rhino.Geometry;
using Rhino.Input;
using Rhino.Input.Custom;
using Rhino.UI;
using RhinoRoad.Core;
using RhinoRoad.Rhino.Commands;
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
internal sealed class JourneyEditForm : Form
{
    private static readonly Dictionary<uint, JourneyEditForm> OpenForms = new();

    private readonly RhinoDoc _document;
    private readonly Guid _sourceId;
    private readonly Guid _stableId;
    private readonly Curve _originalCurve;
    private readonly Label _status = new() { Wrap = WrapMode.Word };
    private readonly Label _title = new();
    private readonly DropDown _startDirection = new();
    private readonly StackLayout _controls = new() { Spacing = 3 };
    private readonly ControlLabelConduit _labels = new();
    private readonly UITimer _timer = new() { Interval = 0.4 };
    private bool _saved;
    private bool _closing;
    private bool _suppressDirectionEvent;

    private JourneyEditForm(RhinoDoc document, RhinoObject source, VehicleAccessDefinition definition, Curve curve)
    {
        _document = document;
        _sourceId = source.Id;
        _stableId = definition.StableSourceId;
        _originalCurve = (Curve)curve.Duplicate();

        Title = "Edit Road";
        ClientSize = new EtoSize(320, 460);
        MinimumSize = new EtoSize(280, 320);
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
            WriteIntent(saved, ManoeuvreControlReconciler.WithStartDirection(journey, chosen));
        };

        var heading = new Button { Text = "Set start heading…" };
        heading.Click += (_, _) => PickHeading();
        var settings = new Button { Text = "Settings…" };
        settings.Click += (_, _) => RunSettings();
        var save = new Button { Text = "Save" };
        save.Click += (_, _) => Save();
        var discard = new Button { Text = "Discard" };
        discard.Click += (_, _) => Discard();

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
                new StackLayout
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 6,
                    Items = { save, discard, null, settings }
                }
            }
        };

        _timer.Elapsed += (_, _) => Poll();
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
                LiveJourneyEditConduit.Begin(_document, _stableId);
                _timer.Start();
            }
            catch (Exception error)
            {
                RhinoApp.WriteLine($"RhinoRoad could not start the edit preview: {error.Message}");
            }
        };

        Rebuild(definition);
    }

    /// <summary>Opens, or brings forward, the edit session for one saved journey.</summary>
    public static CommandResult Start(RhinoDoc document, RhinoObject source, VehicleAccessDefinition definition, Curve curve)
    {
        if (OpenForms.TryGetValue(document.RuntimeSerialNumber, out var existing))
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
        OpenForms[document.RuntimeSerialNumber] = form;
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
                form.PositionOverActiveView();
            }
            catch (Exception error)
            {
                RhinoApp.WriteLine($"RhinoRoad could not open Edit Road: {error.Message}");
                OpenForms.Remove(document.RuntimeSerialNumber);
                form.Teardown();
            }
        });
        return CommandResult.Success;
    }

    public static bool IsEditing(RhinoDoc document, Guid sourceObjectId) =>
        OpenForms.TryGetValue(document.RuntimeSerialNumber, out var form) && form._sourceId == sourceObjectId;

    private void PositionOverActiveView()
    {
        if (_document.Views.ActiveView is not { } view) return;
        var rect = view.ScreenRectangle;
        Location = new EtoPoint(rect.Left + 18, rect.Top + 12);
    }

    private VehicleAccessDefinition? Read() =>
        _document.Objects.FindId(_sourceId) is { } source &&
        AccessDefinitionStore.TryRead(source, out var definition, out _) ? definition : null;

    /// <summary>
    /// Writes changed intent back to the source without rebaking. Only Direction, Kind and heading
    /// move: the stored positions stay exactly as saved, so the live preview keeps comparing the
    /// dragged grips against the saved journey rather than quietly adopting them.
    /// </summary>
    private void WriteIntent(VehicleAccessDefinition definition, ManoeuvreDefinition journey)
    {
        AccessDefinitionStore.Write(_document, _sourceId, definition with { Manoeuvre = journey });
        Rebuild(definition with { Manoeuvre = journey });
        _document.Views.Redraw();
    }

    private void Rebuild(VehicleAccessDefinition definition)
    {
        var journey = definition.Manoeuvre;
        _suppressDirectionEvent = true;
        _startDirection.SelectedKey = (journey?.StartDirection ?? TravelDirection.Forward).ToString();
        _suppressDirectionEvent = false;

        _controls.Items.Clear();
        if (journey is null) return;
        for (var index = 0; index < journey.Controls.Count; index++)
            _controls.Items.Add(Row(definition, journey, index));
    }

    private Control Row(VehicleAccessDefinition definition, ManoeuvreDefinition journey, int index)
    {
        var control = journey.Controls[index];
        var last = index == journey.Controls.Count - 1;
        var number = new Label { Text = $"{index + 1}.", Width = 24, VerticalAlignment = VerticalAlignment.Center };

        var direction = new DropDown { Width = 96 };
        direction.Items.Add(new ListItem { Key = nameof(TravelDirection.Forward), Text = "Forward" });
        direction.Items.Add(new ListItem { Key = nameof(TravelDirection.Reverse), Text = "Reverse" });
        direction.SelectedKey = control.Direction.ToString();
        direction.SelectedKeyChanged += (_, _) =>
        {
            if (!Enum.TryParse(direction.SelectedKey, out TravelDirection chosen) || chosen == control.Direction) return;
            Replace(definition, journey, index, control with { Direction = chosen });
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
            Replace(definition, journey, index, control with { Kind = chosen });
        };

        var row = new StackLayout
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            VerticalContentAlignment = VerticalAlignment.Center,
            Items = { number, direction, kind }
        };
        row.MouseEnter += (_, _) => Highlight(index);
        row.MouseLeave += (_, _) => Highlight(null);
        return row;
    }

    private void Replace(VehicleAccessDefinition definition, ManoeuvreDefinition journey, int index, ManoeuvreControl replacement)
    {
        var controls = journey.Controls.ToArray();
        controls[index] = replacement;
        WriteIntent(definition, journey with { Controls = controls });
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
        WriteIntent(definition, journey with { StartHeadingRadians = Math.Atan2(delta.Y, delta.X) });
    }

    private void RunSettings()
    {
        if (_document.Objects.FindId(_sourceId) is null) return;
        _document.Objects.UnselectAll();
        _document.Objects.Select(_sourceId);
        RhinoApp.RunScript(_document.RuntimeSerialNumber, "_RRRoad", false);
        if (Read() is { } refreshed) Rebuild(refreshed);
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
        var result = RRUpdateVehicleAccessCommand.Update(_document, source);
        if (result != CommandResult.Success)
        {
            LiveJourneyEditConduit.Begin(_document, _stableId);
            _status.Text = "Save failed — see the command line. The edit is still open.";
            return;
        }
        _saved = true;
        VehicleAccessInspectorService.Refresh(_document, _stableId);
        Close();
    }

    private void Discard()
    {
        if (_closing) return;
        _closing = true;
        if (_document.Objects.FindId(_sourceId) is { Geometry: Curve current } &&
            !GeometryBase.GeometryEquals(_originalCurve, current))
            _document.Objects.Replace(_sourceId, _originalCurve);
        Close();
    }

    private void Poll()
    {
        if (_document.Objects.FindId(_sourceId) is null)
        {
            _status.Text = "The control line has been deleted.";
            return;
        }
        _status.Text = LiveJourneyEditConduit.IsPreviewing(_document, _stableId)
            ? "Unsaved edit — Save reruns the checks and refreshes the sizing results."
            : "No change yet. The saved result is what you see.";
    }

    private void Teardown()
    {
        _timer.Stop();
        _labels.Enabled = false;
        LiveJourneyEditConduit.End(_document);
        if (_document.Objects.FindId(_sourceId) is { } source) source.GripsOn = false;
        OpenForms.Remove(_document.RuntimeSerialNumber);
        _document.Views.Redraw();
    }

    /// <summary>Numbers the controls in the viewport so a row in the palette names something visible.</summary>
    private sealed class ControlLabelConduit : DisplayConduit
    {
        public Guid SourceId { get; set; }
        public int? Highlighted { get; set; }

        protected override void DrawForeground(DrawEventArgs e)
        {
            if (e.RhinoDoc is not { } document || document.Objects.FindId(SourceId) is not { } source) return;
            if (!AccessDefinitionStore.TryRead(source, out var definition, out _) || definition?.Manoeuvre is null) return;
            if (source.Geometry is not Curve curve) return;
            var vertices = AccessDefinitionStore.PolylineVertices(curve, document.ModelUnitSystem);
            var scale = RhinoMath.UnitScale(UnitSystem.Meters, document.ModelUnitSystem);
            var controls = definition.Manoeuvre.Controls;
            // Grips move the vertices but never add or remove one, so the live vertex list still
            // lines up with the stored controls; a mismatch means the curve was edited another way.
            var count = Math.Min(controls.Count, Math.Max(0, vertices.Count - 1));
            for (var index = 0; index < count; index++)
            {
                var control = controls[index];
                var vertex = vertices[index + 1];
                var point = new Point3d(vertex.X * scale, vertex.Y * scale, vertex.Z * scale);
                var highlighted = Highlighted == index;
                var colour = highlighted ? Color.Gold
                    : control.Kind == ManoeuvreControlKind.Finish ? Color.DarkOrange
                    : control.Direction == TravelDirection.Forward ? Color.RoyalBlue : Color.MediumPurple;
                e.Display.DrawDot(point, $"{index + 1}", colour, Color.White);
            }
        }
    }
}
