using Eto.Drawing;
using Eto.Forms;
using Rhino;
using Rhino.DocObjects;
using Rhino.Geometry;
using Rhino.UI;
using RhinoRoad.Core;
using RhinoRoad.Rhino.Commands;
using RhinoRoad.Rhino.UI;

namespace RhinoRoad.Rhino.Services;

internal sealed class VehicleAccessInspectorForm : Form
{
    private readonly RhinoDoc _document;
    private readonly Label _sourceName = new() { Text = "No source", Wrap = WrapMode.Word };
    private readonly Label _status = new() { Text = "Unavailable", Wrap = WrapMode.Word };
    private readonly Button _update = new() { Text = "Update" };
    private readonly StackLayout _checks = new() { Spacing = 4 };
    private readonly StackLayout _measurements = new() { Spacing = 3 };
    private readonly StackLayout _events = new() { Spacing = 4 };
    private readonly DropDown _metric = new();
    private readonly VehicleAccessProfileControl _profile = new();
    private readonly VehicleAccessReviewConduit _conduit = new();
    private readonly Label _readout = new() { Text = "Move across the graph to locate it on the road." };
    private readonly UITimer _timer = new() { Interval = 0.5 };
    private RhinoObject? _source;
    private VehicleAccessDefinition? _definition;
    private VehicleAccessReviewSnapshot? _snapshot;
    private uint _sourceRuntimeSerial;
    private readonly Dictionary<Guid, uint> _referenceRuntimeSerials = new();
    private bool _outOfDate;

    public VehicleAccessInspectorForm(RhinoDoc document, Guid objectId)
    {
        _document = document;
        Title = "Inspect Road";
        ClientSize = new Size(430, 700);
        MinimumSize = new Size(350, 460);
        Padding = 12;
        Resizable = true;
        Maximizable = false;
        Minimizable = false;
        ShowInTaskbar = false;
        // An ownerless top-level window drops behind the Rhino frame the moment the viewport takes
        // focus -- which is every time the inspector is actually used. Owning it to the document's
        // main window keeps it above Rhino without making it topmost over everything else.
        Owner = RhinoEtoApp.MainWindowForDocument(document);
        this.UseRhinoStyle();
        _conduit.ModelUnits = document.ModelUnitSystem;

        foreach (var metric in Enum.GetValues<ReviewMetricKind>())
            _metric.Items.Add(new ListItem { Key = metric.ToString(), Text = MetricName(metric) });
        _metric.SelectedKey = ReviewMetricKind.SteeringAngle.ToString();
        _metric.SelectedKeyChanged += (_, _) =>
        {
            if (!Enum.TryParse(_metric.SelectedKey, out ReviewMetricKind selected)) return;
            _profile.Metric = selected;
            _conduit.Metric = selected;
            _profile.Invalidate();
            _document.Views.Redraw();
        };
        _profile.HoverStationChanged += (_, station) =>
        {
            _conduit.HoveredStation = station;
            _readout.Text = Readout(station);
            _document.Views.Redraw();
        };

        var pick = new Button { Text = "Pick" };
        pick.Click += (_, _) => Pick();
        _update.Click += (_, _) => Update();
        var configure = new Button { Text = "Settings" };
        configure.Click += (_, _) => Configure();
        var close = new Button { Text = "Close" };
        close.Click += (_, _) => Close();
        var edit = new Button { Text = "Edit road" };
        edit.ToolTip = "Open the edit session: drag the control points with a live sweep preview.";
        edit.Click += (_, _) => RunForSource("_RREditRoad");
        var measure = new Button { Text = "Measure section" };
        measure.Click += (_, _) => RunForSource("_RRMeasureRoad");

        var routeToggle = Toggle("Metric route", VehicleAccessOverlayParts.MetricRoute, true);
        var envelopeToggle = Toggle("Envelopes", VehicleAccessOverlayParts.Envelopes, true);
        var footprintToggle = Toggle("Footprints", VehicleAccessOverlayParts.Footprints, true);
        var roadToggle = Toggle("Road references", VehicleAccessOverlayParts.RoadReferences, true);
        var eventToggle = Toggle("Event markers", VehicleAccessOverlayParts.Events, true);
        var controlToggle = Toggle("Control intent", VehicleAccessOverlayParts.ControlIntent, true);

        var body = new StackLayout
        {
            Spacing = 10,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Items =
            {
                new StackLayout
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 6,
                    VerticalContentAlignment = VerticalAlignment.Center,
                    Items = { pick, new StackLayoutItem(_sourceName, true), _update, configure, close }
                },
                _status,
                // The graph leads. It is the one part of this panel that is read continuously rather
                // than glanced at, and scrubbing it is how the numbers below get their location.
                Group("Driving detail", new StackLayout
                {
                    Spacing = 5,
                    HorizontalContentAlignment = HorizontalAlignment.Stretch,
                    Items = { _metric, _profile, _readout }
                }),
                new StackLayout { Orientation = Orientation.Horizontal, Spacing = 6, Items = { edit, measure } },
                Group("Problem areas", _events),
                Group("Checks", _checks),
                Group("Measurements", _measurements),
                Group("Display", new TableLayout
                {
                    Spacing = new Size(10, 4),
                    Rows =
                    {
                        new TableRow(routeToggle, envelopeToggle, footprintToggle),
                        new TableRow(roadToggle, eventToggle, controlToggle)
                    }
                })
            }
        };
        Content = new Scrollable { Border = BorderType.None, ExpandContentWidth = true, Content = body };

        _timer.Elapsed += (_, _) => PollFreshness();
        Shown += (_, _) =>
        {
            // The conduit touches Rhino's display pipeline, so it is switched on once the native
            // window exists rather than from inside the picking command that opened this form.
            try
            {
                _conduit.Enabled = true;
                _timer.Start();
                _document.Views.Redraw();
            }
            catch (Exception error)
            {
                RhinoApp.WriteLine($"RhinoRoad inspector overlay disabled: {error.Message}");
            }
        };
        Closed += (_, _) =>
        {
            _timer.Stop();
            _conduit.Enabled = false;
            _document.Views.Redraw();
        };
        SetObject(objectId);
    }

    /// <summary>Opens against the right-hand edge of the active viewport rather than the screen centre.</summary>
    public void PositionOverActiveView()
    {
        if (_document.Views.ActiveView is not { } view) return;
        var rect = view.ScreenRectangle;
        Location = new Eto.Drawing.Point(rect.Right - Width - 18, rect.Top + 12);
    }

    public void SetObject(Guid objectId)
    {
        var selected = _document.Objects.FindId(objectId);
        _source = selected is null ? null : AccessDefinitionStore.ResolveSource(_document, selected);
        string? error = null;
        if (_source is null || !AccessDefinitionStore.TryRead(_source, out _definition, out error) || _definition is null)
        {
            _sourceName.Text = error ?? "No saved analysis";
            SetSnapshot(null);
            return;
        }

        _sourceName.Text = string.IsNullOrWhiteSpace(_source.Name)
            ? $"Access {_definition.StableSourceId.ToString("N")[..8]}"
            : _source.Name;
        _outOfDate = !DefinitionMatchesFingerprints();
        CaptureRuntimeSerials();
        var snapshot = VehicleAccessReviewService.Find(_document, _definition.StableSourceId);
        if (snapshot is null && !_outOfDate &&
            VehicleAccessRunService.TryPrepareUpdate(_document, _source, out var prepared, out _, _definition) && prepared is not null)
        {
            VehicleAccessReviewService.Remember(_document, prepared);
            snapshot = VehicleAccessReviewService.Find(_document, _definition.StableSourceId);
        }
        SetSnapshot(snapshot);
        PollFreshness();
    }

    public void RefreshSource(Guid sourceId)
    {
        if (_definition?.StableSourceId != sourceId) return;
        if (_source is not null) SetObject(_source.Id);
    }

    private void SetSnapshot(VehicleAccessReviewSnapshot? snapshot)
    {
        _snapshot = snapshot;
        _conduit.SetSnapshot(snapshot);
        _profile.SetReview(snapshot?.Review);
        _conduit.HoveredStation = null;
        _readout.Text = snapshot is null
            ? "No review data."
            : "Move across the graph to locate it on the road.";
        RebuildChecks();
        RebuildMeasurements();
        RebuildEvents();
        _document.Views.Redraw();
    }

    private void RebuildChecks()
    {
        _checks.Items.Clear();
        if (_snapshot is null) { _checks.Items.Add(new Label { Text = "No review data" }); return; }
        foreach (var check in _snapshot.Review.Checks)
        {
            var text = $"{StateGlyph(check.State)}  {CheckName(check.Kind)}    {Reading(check)}";
            if (check.State != ReviewCheckState.Fail)
            {
                _checks.Items.Add(new Label { Text = text, VerticalAlignment = VerticalAlignment.Center });
                continue;
            }
            var button = new Button { Text = text };
            button.Click += (_, _) => ZoomTo(_snapshot.Review.ProblemEvents
                .Where(item => item.Check == check.Kind)
                .MaxBy(item => Math.Abs((item.ActualValue ?? 0.0) - (item.Limit ?? 0.0))));
            _checks.Items.Add(button);
        }
    }

    private void RebuildMeasurements()
    {
        _measurements.Items.Clear();
        if (_snapshot is null) return;
        var review = _snapshot.Review;
        AddMeasurement("Route length", $"{review.RouteLengthMetres:0.00} m");
        AddMeasurement("Maximum wheel angle", $"{Degrees(review.MaximumSteeringAngleRadians):0.0}°");
        AddMeasurement("Maximum steering rate", $"{Degrees(review.MaximumSteeringRateRadiansPerSecond):0.0}°/s");
        var gradeState = review.Checks.First(check => check.Kind == ReviewCheckKind.Grade).State;
        AddMeasurement("Maximum grade", gradeState == ReviewCheckState.Unavailable
            ? "— (World XY route)"
            : $"{review.MaximumAbsoluteGrade * 100.0:0.00}%");
        AddMeasurement("Minimum clearance", review.MinimumClearanceMetres is { } clearance ? $"{clearance:0.00} m" : "—");
        AddMeasurement("Reversals", review.ReversalCount.ToString());
        AddMeasurement("Vehicle / mode", $"{review.VehicleName} / {review.ModeName}");
        AddMeasurement("Preset validation", review.ValidationStatus.ToString());
    }

    private void RebuildEvents()
    {
        _events.Items.Clear();
        if (_snapshot is null || _snapshot.Review.ProblemEvents.Count == 0)
        {
            _events.Items.Add(new Label { Text = "No problem areas" });
            return;
        }
        foreach (var item in _snapshot.Review.ProblemEvents)
        {
            var range = Math.Abs(item.EndStationMetres - item.StartStationMetres) < 0.01
                ? $"Sta {item.WorstStationMetres:0.0} m"
                : $"Sta {item.StartStationMetres:0.0}–{item.EndStationMetres:0.0} m";
            var button = new Button { Text = $"●  {range}  {item.Message}" };
            button.Click += (_, _) => ZoomTo(item);
            _events.Items.Add(button);
        }
    }

    private void PollFreshness()
    {
        if (_source is not null) _source = _document.Objects.FindId(_source.Id);
        if (_source is null || _definition is null)
        {
            _status.Text = "Unavailable";
            _update.Enabled = false;
            return;
        }
        if (!_outOfDate && _source.RuntimeSerialNumber != _sourceRuntimeSerial)
        {
            _outOfDate = !string.Equals(
                AccessDefinitionStore.Fingerprint(_source),
                _definition.SourceFingerprint,
                StringComparison.Ordinal);
            _sourceRuntimeSerial = _source.RuntimeSerialNumber;
        }
        if (!_outOfDate)
        {
            foreach (var pair in _definition.ReferenceFingerprints)
            {
                var reference = _document.Objects.FindId(pair.Key);
                if (reference is null)
                {
                    _outOfDate = true;
                    break;
                }
                _referenceRuntimeSerials.TryGetValue(pair.Key, out var previousSerial);
                if (reference.RuntimeSerialNumber == previousSerial) continue;
                if (!string.Equals(AccessDefinitionStore.Fingerprint(reference), pair.Value, StringComparison.Ordinal))
                {
                    _outOfDate = true;
                    break;
                }
                _referenceRuntimeSerials[pair.Key] = reference.RuntimeSerialNumber;
            }
        }
        _status.Text = LiveJourneyEditConduit.IsPreviewing(_document, _definition.StableSourceId)
            ? "Live edit preview — Update to refresh checks and save" : _outOfDate ? "Out of date" : _snapshot is null ? "Unavailable" :
            MovementFit.Describe(_snapshot.Run.Geometry.BodyEnvelopeMerged &&
                _snapshot.Run.Geometry.ClearanceEnvelope is not null && _snapshot.Run.Geometry.Warnings.Count == 0,
                _snapshot.Review.IsFeasible,
                (_definition.CheckObstacles && _definition.ObstacleObjectIds.Count > 0) ||
                (_definition.CheckAllowedArea && _definition.AllowedBoundaryObjectIds.Count > 0));
        _update.Enabled = true;
    }

    private bool DefinitionMatchesFingerprints()
    {
        if (_source is null || _definition is null) return false;
        if (!string.Equals(AccessDefinitionStore.Fingerprint(_source), _definition.SourceFingerprint, StringComparison.Ordinal))
            return false;
        foreach (var pair in _definition.ReferenceFingerprints)
        {
            var reference = _document.Objects.FindId(pair.Key);
            if (reference is null || !string.Equals(AccessDefinitionStore.Fingerprint(reference), pair.Value, StringComparison.Ordinal))
                return false;
        }
        return true;
    }

    private void CaptureRuntimeSerials()
    {
        _sourceRuntimeSerial = _source?.RuntimeSerialNumber ?? 0;
        _referenceRuntimeSerials.Clear();
        if (_definition is null) return;
        foreach (var id in _definition.ReferenceFingerprints.Keys)
            _referenceRuntimeSerials[id] = _document.Objects.FindId(id)?.RuntimeSerialNumber ?? 0;
    }

    private void Pick()
    {
        using var getter = new global::Rhino.Input.Custom.GetObject();
        getter.SetCommandPrompt("Select road to inspect");
        getter.GeometryFilter = ObjectType.AnyObject;
        if (getter.Get() == global::Rhino.Input.GetResult.Object) SetObject(getter.Object(0).ObjectId);
    }

    private void Update()
    {
        if (_source is null) return;
        RRUpdateVehicleAccessCommand.Update(_document, _source);
        SetObject(_source.Id);
    }

    private void Configure()
        => RunForSource("_RRRoad");

    private void RunForSource(string command)
    {
        if (_source is null) return;
        _document.Objects.UnselectAll();
        _document.Objects.Select(_source.Id);
        _document.Views.Redraw();
        RhinoApp.RunScript(_document.RuntimeSerialNumber, command, false);
    }

    private void ZoomTo(VehicleAccessProblemEvent? item)
    {
        if (item is null || _snapshot is null) return;
        _conduit.SelectedEvent = item;
        var scale = RhinoMath.UnitScale(UnitSystem.Meters, _document.ModelUnitSystem);
        var poses = _snapshot.Run.Analysis.Poses.Where(pose =>
            pose.StationMetres >= item.StartStationMetres - 0.1 && pose.StationMetres <= item.EndStationMetres + 0.1).ToArray();
        if (poses.Length == 0) poses = [_snapshot.Run.Analysis.Poses.MinBy(pose => Math.Abs(pose.StationMetres - item.WorstStationMetres))!];
        var points = poses.SelectMany(pose => pose.OccupiedOutlinesWorldMetres.SelectMany(outline =>
            outline.Select(point => new Point3d(point.X * scale, point.Y * scale, pose.RearAxleCentreMetres.Z * scale))));
        var box = new BoundingBox(points);
        if (box.IsValid)
        {
            box.Inflate(Math.Max(box.Diagonal.Length * 0.35, _document.ModelAbsoluteTolerance * 20));
            _document.Views.ActiveView?.ActiveViewport.ZoomBoundingBox(box);
        }
        _document.Views.Redraw();
    }

    /// <summary>Says in words what the viewport marker is pointing at, so the graph and the road agree.</summary>
    private string Readout(double? station)
    {
        if (_snapshot is null) return "No review data.";
        if (station is null) return "Move across the graph to locate it on the road.";
        var review = _snapshot.Review;
        var sample = review.Samples.MinBy(item => Math.Abs(item.StationMetres - station.Value));
        if (sample is null) return "Move across the graph to locate it on the road.";
        var direction = sample.Direction == TravelDirection.Reverse ? "reversing" : "forward";
        if (!Enum.TryParse(_metric.SelectedKey, out ReviewMetricKind metric)) metric = ReviewMetricKind.SteeringAngle;
        var value = VehicleAccessMetricPalette.Value(metric, sample);
        return $"Station {sample.StationMetres:0.0} m · {MetricName(metric)} {value:0.##} · {direction}";
    }

    private CheckBox Toggle(string text, VehicleAccessOverlayParts part, bool initial)
    {
        var toggle = new CheckBox { Text = text, Checked = initial };
        toggle.CheckedChanged += (_, _) =>
        {
            if (toggle.Checked == true) _conduit.Parts |= part;
            else _conduit.Parts &= ~part;
            _document.Views.Redraw();
        };
        return toggle;
    }

    private void AddMeasurement(string name, string value) => _measurements.Items.Add(new StackLayout
    {
        Orientation = Orientation.Horizontal,
        Items = { new StackLayoutItem(new Label { Text = name }, true), new Label { Text = value } }
    });

    private static GroupBox Group(string name, Control content) => new() { Text = name, Padding = 8, Content = content };
    private static double Degrees(double radians) => radians * 180.0 / Math.PI;
    private static string Reading(VehicleAccessReviewCheck check)
    {
        if (check.ActualValue.HasValue)
            return check.Limit.HasValue ? $"{check.ActualValue:0.##} / {check.Limit:0.##}" : $"{check.ActualValue:0.##}";
        return check.State switch
        {
            ReviewCheckState.Pass => "Pass",
            ReviewCheckState.Off => "Off",
            ReviewCheckState.Unavailable => check.Message,
            _ => check.Message
        };
    }
    private static string StateGlyph(ReviewCheckState state) => state switch
    {
        ReviewCheckState.Pass => "✓",
        ReviewCheckState.Fail => "!",
        ReviewCheckState.Off => "○",
        _ => "—"
    };
    private static string CheckName(ReviewCheckKind kind) => kind switch
    {
        ReviewCheckKind.EnvelopeGeneration => "Envelope generation",
        ReviewCheckKind.SteeringAngle => "Steering angle",
        ReviewCheckKind.SteeringRate => "Steering rate",
        ReviewCheckKind.TangentContinuity => "Tangent continuity",
        ReviewCheckKind.Grade => "Grade",
        ReviewCheckKind.FixedWidthRoadFit => "Fixed-width road fit",
        ReviewCheckKind.AllowedAreaFit => "Allowed-area fit",
        ReviewCheckKind.ObstacleClearance => "Obstacle clearance",
        _ => kind.ToString()
    };
    private static string MetricName(ReviewMetricKind kind) => kind switch
    {
        ReviewMetricKind.SteeringAngle => "Steering angle (°)",
        ReviewMetricKind.SteeringRate => "Steering rate (°/s)",
        ReviewMetricKind.ClearanceOrRoadMargin => "Clearance / road margin (m)",
        ReviewMetricKind.Grade => "Grade (%)",
        _ => kind.ToString()
    };
}
