using Eto.Drawing;
using Eto.Forms;
using Rhino;
using Rhino.UI;
using RhinoRoad.Core;

namespace RhinoRoad.Rhino.UI;

/// <summary>
/// Configuration for <c>Road</c>.
/// </summary>
/// <remarks>
/// The command-line form asks thirteen questions on one line and answers none. The thing a designer
/// most needs before drawing — how tightly this vehicle can actually turn — was never shown at all,
/// so a corner could only be found infeasible after the fact. This dialog groups the options and
/// puts the turning geometry on screen, live, as the vehicle and mode change.
/// </remarks>
internal sealed class VehicleAccessDialog : Dialog<bool>
{
    private readonly VehicleCatalog _catalog;
    private readonly DropDown _vehicle = new();
    private readonly DropDown _mode = new();
    private readonly Label _vehicleFacts = new() { Wrap = WrapMode.Word };
    private readonly Label _turningFacts = new() { Wrap = WrapMode.Word };
    private readonly VehiclePreview _preview = new();
    private readonly DropDown _footprintMode = new();

    private readonly DropDown _source = new();
    private readonly DropDown _direction = new();
    private readonly DropDown _edgeMethod = new();
    private readonly NumericStepper _clearance = Metres(0.0, 5.0);
    private readonly NumericStepper _leftWidth = Metres(0.0, 50.0);
    private readonly NumericStepper _rightWidth = Metres(0.0, 50.0);
    private readonly NumericStepper _footprintInterval = Metres(0.0, 100.0);
    private readonly NumericStepper _maximumGrade = new()
    {
        MinValue = 0.0, MaxValue = 100.0, DecimalPlaces = 1, Increment = 0.5
    };

    private readonly CheckBox _checkObstacles = new() { Text = "Obstacle curves" };
    private readonly CheckBox _checkAllowedArea = new() { Text = "Allowed-area boundaries" };
    private readonly CheckBox _checkGrade = new() { Text = "Maximum grade" };
    private readonly CheckBox _reselectReferences = new() { Text = "Reselect obstacle and boundary curves" };
    private readonly CheckBox _replaceExisting = new() { Text = "Replace previous result for the same path" };
    private readonly CheckBox _previewBeforeBaking = new() { Text = "Preview and confirm before baking" };

    // Narrow is the whole point of the resize, so the width survives to the next run.
    private static Size? _lastClientSize;

    private VehicleAccessDialog(VehicleCatalog catalog, VehicleAccessSettings settings, bool configuringSavedSource)
    {
        _catalog = catalog;
        Title = configuringSavedSource ? "Road settings" : "Road";
        Padding = 12;
        Resizable = true;
        this.UseRhinoStyle();

        foreach (var vehicle in catalog.Vehicles)
        {
            _vehicle.Items.Add(new ListItem { Text = $"{vehicle.Name} ({vehicle.Id})", Key = vehicle.Id });
        }

        _footprintMode.Items.Add(new ListItem { Text = "None", Key = nameof(FootprintMode.None) });
        _footprintMode.Items.Add(new ListItem { Text = "First and last only", Key = nameof(FootprintMode.EndsOnly) });
        _footprintMode.Items.Add(new ListItem { Text = "Every interval", Key = nameof(FootprintMode.AtInterval) });

        _source.Items.Add(new ListItem { Text = "Existing rear-axle path (advanced)", Key = nameof(PathSourceKind.ExistingCurve) });
        _source.Items.Add(new ListItem { Text = "Drive the vehicle interactively", Key = nameof(PathSourceKind.Interactive) });
        _direction.Items.Add(new ListItem { Text = "Forward", Key = nameof(TravelDirection.Forward) });
        _direction.Items.Add(new ListItem { Text = "Reverse", Key = nameof(TravelDirection.Reverse) });
        foreach (var method in Enum.GetNames<RoadEdgeMethod>())
        {
            _edgeMethod.Items.Add(new ListItem { Text = method, Key = method });
        }

        Apply(settings);
        _vehicle.SelectedKeyChanged += (_, _) => { RefreshModes(); RefreshFacts(); };
        _mode.SelectedKeyChanged += (_, _) => RefreshFacts();
        _checkGrade.CheckedChanged += (_, _) => _maximumGrade.Enabled = _checkGrade.Checked == true;
        _footprintMode.SelectedKeyChanged += (_, _) =>
            _footprintInterval.Enabled = _footprintMode.SelectedKey == nameof(FootprintMode.AtInterval);
        _checkObstacles.CheckedChanged += (_, _) => RefreshReferenceChoice();
        _checkAllowedArea.CheckedChanged += (_, _) => RefreshReferenceChoice();
        _reselectReferences.Visible = configuringSavedSource;

        DefaultButton = new Button { Text = "Continue" };
        DefaultButton.Click += (_, _) => Close(true);
        AbortButton = new Button { Text = "Cancel" };
        AbortButton.Click += (_, _) => Close(false);

        if (_lastClientSize is { } remembered) ClientSize = remembered;
        Closing += (_, _) => _lastClientSize = ClientSize;

        var advanced = new StackLayout
        {
            Visible = false,
            Spacing = 8,
            Items =
            {
                Group("Fixed-width corridor", Fields(("Road edges", _edgeMethod),
                    ("Left width (m)", _leftWidth), ("Right width (m)", _rightWidth))),
                Group("Output", Stretched(Fields(("Vehicle footprints", _footprintMode),
                    ("Interval (m)", _footprintInterval)), _previewBeforeBaking, _replaceExisting))
            }
        };
        var showAdvanced = new CheckBox { Text = "Advanced geometry and output" };
        showAdvanced.CheckedChanged += (_, _) => advanced.Visible = showAdvanced.Checked == true;
        var body = new StackLayout
        {
            Spacing = 10,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Items =
            {
                    Group("Vehicle", Stretched(
                        Fields(("Vehicle", _vehicle), ("Driving mode", _mode)),
                        _preview,
                        _vehicleFacts,
                        _turningFacts)),
                    Group("Path", Fields(
                        ("Source", _source),
                        ("Travel direction", _direction))),
                    Group("Clearance", Fields(("Allowance (m)", _clearance))),
                    Group("Site constraints", Stretched(
                        _checkObstacles,
                        _checkAllowedArea,
                        _reselectReferences,
                        new StackLayout
                        {
                            Orientation = Orientation.Horizontal,
                            Spacing = 8,
                            VerticalContentAlignment = VerticalAlignment.Center,
                            Items = { _checkGrade, new Label { Text = "max %" }, _maximumGrade }
                        })),
                    showAdvanced,
                    advanced,
            }
        };

        // The groups scroll; the buttons do not. Narrowing the window makes the wrapped text taller,
        // and without this the Continue button is the first thing pushed off the bottom -- which is
        // the one control the dialog cannot do without.
        Content = new TableLayout
        {
            Spacing = new Size(0, 10),
            Rows =
            {
                new TableRow(new TableCell(
                    new Scrollable
                    {
                        Border = BorderType.None,
                        ExpandContentWidth = true,
                        ExpandContentHeight = false,
                        Content = body
                    },
                    true))
                {
                    ScaleHeight = true
                },
                new TableRow(new TableCell(
                    new StackLayout
                    {
                        Orientation = Orientation.Horizontal,
                        Spacing = 8,
                        Items = { null, DefaultButton, AbortButton }
                    },
                    true))
            }
        };

        // A wrapping label has no width of its own to wrap at: asked how wide it wants to be, it
        // answers with the whole sentence on one line, and that answer becomes the dialog's minimum
        // width. Handing it the width the window actually has, every time the window changes, is
        // what makes the text wrap and the dialog narrowable.
        SizeChanged += (_, _) => FitWrappingText();

        // After layout, not during it. On first show the client size is not settled when Shown
        // fires, so measuring then gives the wrapping text the wrong width and the dialog opens
        // with both scrollbars up until the first resize nudges it right.
        Shown += (_, _) => Application.Instance.AsyncInvoke(FitWrappingText);

        RefreshFacts();
    }

    private void FitWrappingText()
    {
        // Less the vertical scrollbar, which appears exactly when the text is tall enough to matter.
        var available = ClientSize.Width - Padding.Horizontal - 34;
        if (available < 80) return;
        _vehicleFacts.Width = available;
        _turningFacts.Width = available;
    }

    private static NumericStepper Metres(double minimum, double maximum) => new()
    {
        MinValue = minimum, MaxValue = maximum, DecimalPlaces = 2, Increment = 0.05
    };

    /// <summary>
    /// A label/field grid whose field column absorbs whatever width the window has. Fixed-width
    /// fields in an auto-sized layout set the dialog's floor; letting them stretch instead means
    /// the window can be dragged as narrow as the longest caption.
    /// </summary>
    private static TableLayout Fields(params (string Caption, Control Field)[] rows)
    {
        var table = new TableLayout { Spacing = new Size(8, 6) };
        foreach (var (caption, field) in rows)
        {
            table.Rows.Add(new TableRow(
                new TableCell(new Label { Text = caption, VerticalAlignment = VerticalAlignment.Center }),
                new TableCell(field, true)));
        }

        return table;
    }

    private static StackLayout Stretched(params Control[] items)
    {
        var stack = new StackLayout { Spacing = 6, HorizontalContentAlignment = HorizontalAlignment.Stretch };
        foreach (var item in items) stack.Items.Add(item);
        return stack;
    }

    private static GroupBox Group(string title, Control content) =>
        new() { Text = title, Padding = 8, Content = content };

    private VehicleDefinition SelectedVehicle => _catalog.Get(_vehicle.SelectedKey);

    private void RefreshModes()
    {
        var previous = _mode.SelectedKey;
        _mode.Items.Clear();
        foreach (var mode in SelectedVehicle.DrivingModes.Values.OrderBy(item => item.Id))
        {
            _mode.Items.Add(new ListItem { Text = $"{mode.Id} — {mode.Name}", Key = mode.Id });
        }

        _mode.SelectedKey = _mode.Items.Any(item => item.Key == previous) ? previous : _mode.Items[0].Key;
    }

    /// <summary>Restates the selection in the terms a corner is laid out in.</summary>
    private void RefreshFacts()
    {
        var vehicle = SelectedVehicle;
        _preview.Show(vehicle);
        if (!vehicle.DrivingModes.TryGetValue(_mode.SelectedKey ?? string.Empty, out var mode)) return;

        var length = vehicle.BodyOutline.Max(point => point.X) - vehicle.BodyOutline.Min(point => point.X);
        _vehicleFacts.Text =
            $"{length:0.00} m long, {vehicle.WidthMetres:0.00} m wide, {vehicle.WheelbaseMetres:0.00} m wheelbase. " +
            $"Preset {vehicle.Version}, {Describe(vehicle.ValidationStatus)}.";

        var turn = TurningGeometryCalculator.AtFullLock(vehicle, mode);
        _turningFacts.Text =
            $"Mode {mode.Id}: {mode.SpeedKilometresPerHour:0.#} km/h, {mode.MaximumWheelAngleDegrees:0.#}° wheel lock. " +
            $"Tightest turn {turn.RearAxleRadiusMetres:0.00} m at the rear axle, sweeping {turn.SweptWidthMetres:0.00} m wide " +
            $"between radii {turn.InnerRadiusMetres:0.00} m and {turn.OuterRadiusMetres:0.00} m.";
    }

    private static string Describe(ValidationStatus status) => status switch
    {
        ValidationStatus.ReferenceValidated => "validated against the official curves",
        ValidationStatus.ValidationFailed => "FAILED validation — do not rely on this preset",
        _ => "source-transcribed, not yet validated"
    };

    private void Apply(VehicleAccessSettings settings)
    {
        _vehicle.SelectedKey = _catalog.Vehicles.Any(item => item.Id == settings.VehicleId)
            ? settings.VehicleId
            : _catalog.Vehicles.First().Id;
        RefreshModes();
        if (_mode.Items.Any(item => item.Key == settings.ModeId)) _mode.SelectedKey = settings.ModeId;

        _source.SelectedKey = settings.Source.ToString();
        _direction.SelectedKey = settings.Direction.ToString();
        _edgeMethod.SelectedKey = settings.EdgeMethod.ToString();
        _clearance.Value = settings.ClearanceMetres;
        _leftWidth.Value = settings.LeftWidthMetres;
        _rightWidth.Value = settings.RightWidthMetres;
        _footprintInterval.Value = settings.FootprintIntervalMetres;
        _footprintMode.SelectedKey = settings.Footprints.ToString();
        _footprintInterval.Enabled = settings.Footprints == FootprintMode.AtInterval;
        _maximumGrade.Value = settings.MaximumGradePercent;
        _checkObstacles.Checked = settings.CheckObstacles;
        _checkAllowedArea.Checked = settings.CheckAllowedArea;
        _reselectReferences.Checked = settings.ReselectReferences;
        _checkGrade.Checked = settings.CheckMaximumGrade;
        _replaceExisting.Checked = settings.ReplaceExisting;
        _previewBeforeBaking.Checked = settings.PreviewBeforeBaking;
        _maximumGrade.Enabled = settings.CheckMaximumGrade;
        RefreshReferenceChoice();
    }

    private void Harvest(VehicleAccessSettings settings)
    {
        settings.VehicleId = _vehicle.SelectedKey;
        settings.ModeId = _mode.SelectedKey;
        settings.Source = Enum.Parse<PathSourceKind>(_source.SelectedKey);
        settings.Direction = Enum.Parse<TravelDirection>(_direction.SelectedKey);
        settings.EdgeMethod = Enum.Parse<RoadEdgeMethod>(_edgeMethod.SelectedKey);
        settings.ClearanceMetres = _clearance.Value;
        settings.LeftWidthMetres = _leftWidth.Value;
        settings.RightWidthMetres = _rightWidth.Value;
        settings.FootprintIntervalMetres = _footprintInterval.Value;
        settings.Footprints = Enum.Parse<FootprintMode>(_footprintMode.SelectedKey);
        settings.MaximumGradePercent = _maximumGrade.Value;
        settings.CheckObstacles = _checkObstacles.Checked == true;
        settings.CheckAllowedArea = _checkAllowedArea.Checked == true;
        settings.ReselectReferences = _reselectReferences.Checked == true;
        settings.CheckMaximumGrade = _checkGrade.Checked == true;
        settings.ReplaceExisting = _replaceExisting.Checked == true;
        settings.PreviewBeforeBaking = _previewBeforeBaking.Checked == true;
    }

    /// <summary>Shows the dialog, writing the chosen values back into <paramref name="settings"/>.</summary>
    public static bool Show(
        RhinoDoc document,
        VehicleCatalog catalog,
        VehicleAccessSettings settings,
        bool configuringSavedSource = false)
    {
        var dialog = new VehicleAccessDialog(catalog, settings, configuringSavedSource);
        if (!dialog.ShowModal(RhinoEtoApp.MainWindowForDocument(document))) return false;
        dialog.Harvest(settings);
        return true;
    }

    private void RefreshReferenceChoice() =>
        _reselectReferences.Enabled = _checkObstacles.Checked == true || _checkAllowedArea.Checked == true;
}
