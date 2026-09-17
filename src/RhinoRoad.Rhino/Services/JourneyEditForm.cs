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
    private static readonly VehicleCatalog Catalog = VehicleCatalog.LoadEmbedded();

    private readonly RhinoDoc _document;
    private readonly Guid _sourceId;
    private readonly Guid _stableId;
    private readonly Curve _originalCurve;
    private readonly VehicleAccessDefinition _originalDefinition;
    private readonly Label _status = new() { Wrap = WrapMode.Word };
    private readonly Label _areaStatus = new() { Wrap = WrapMode.Word };
    private readonly Label _liveArea = new() { Wrap = WrapMode.Word };
    private readonly Label _controlsStatus = new() { Wrap = WrapMode.Word };
    private readonly Label _yardLabel = new() { Wrap = WrapMode.Word };
    private readonly Label _obstaclesLabel = new() { Wrap = WrapMode.Word };
    private readonly Label _bayLabel = new() { Wrap = WrapMode.Word };
    private readonly Label _title = new();
    private readonly DropDown _startDirection = new();
    private readonly DropDown _adjustFrom = new();
    private readonly DropDown _searchTime = new();
    private readonly DropDown _suggestions = new();
    private readonly Button _searchButton = new() { Text = "Find less clear area…" };
    private readonly StackLayout _controls = new() { Spacing = 3 };
    private readonly ControlLabelConduit _labels = new();
    private readonly UITimer _timer = new() { Interval = 0.4 };
    private bool _saved;
    private bool _closing;
    private bool _suppressDirectionEvent;
    private bool _searching;
    private bool _areaDraftApplied;
    private bool _suppressSuggestionEvent;
    // The road as it stood when the search started. Every suggestion is applied on top of this, so
    // moving between suggestions -- or back to the current route -- never compounds one onto another.
    private Curve? _baselineCurve;
    private VehicleAccessDefinition? _baselineDefinition;
    private string? _appliedFingerprint;
    private CancellationTokenSource? _searchCancellation;
    private ManoeuvringAreaSearch.Result? _searchResult;
    private IReadOnlyList<Point2>? _searchYard;
    private IReadOnlyList<IReadOnlyList<Point2>> _searchObstacles = [];
    // The site the search measures against, picked in the palette. They start from the references
    // the road was saved with, so a road that already checks a yard and buildings needs no picking.
    private Guid? _yardId;
    private List<Guid> _obstacleIds = [];

    private JourneyEditForm(RhinoDoc document, RhinoObject source, VehicleAccessDefinition definition, Curve curve)
    {
        _document = document;
        _sourceId = source.Id;
        _stableId = definition.StableSourceId;
        _originalCurve = (Curve)curve.Duplicate();
        _originalDefinition = definition;

        Title = "Edit Road";
        ClientSize = new EtoSize(360, 860);
        MinimumSize = new EtoSize(300, 560);
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
        _searchButton.Click += (_, _) =>
        {
            if (_searching)
            {
                _searchCancellation?.Cancel();
                _areaStatus.Text = "Stopping search…";
            }
            else StartAreaSearch();
        };
        var bay = new Button { Text = "Set trailer bay…", ToolTip = "Pick the rear of the trailer at the bay, then point the way it reverses in." };
        bay.Click += (_, _) => PickTrailerBay();
        var pickYard = new Button { Text = "Pick yard…", ToolTip = "One closed curve around the area the truck may use." };
        pickYard.Click += (_, _) => PickYard();
        var pickObstacles = new Button { Text = "Pick obstacles…", ToolTip = "Closed curves around buildings, storage and anything else the truck must clear. Enter with nothing selected clears the list." };
        pickObstacles.Click += (_, _) => PickObstacles();
        _adjustFrom.ToolTip = "The search keeps every control before this one exactly as drawn. It may move this control and replace everything after it with a pull-up and reverse to the same bay.";
        _suggestions.Enabled = false;
        _searchTime.Items.Add(new ListItem { Key = "30", Text = "30 seconds" });
        _searchTime.Items.Add(new ListItem { Key = "120", Text = "2 minutes" });
        _searchTime.SelectedKey = "30";
        _suggestions.SelectedKeyChanged += (_, _) =>
        {
            if (!_suppressSuggestionEvent) SelectSuggestion(_suggestions.SelectedKey);
        };

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
                new GroupBox
                {
                    Text = "SVT clear-area search",
                    Padding = 8,
                    Content = new StackLayout
                    {
                        Spacing = 5,
                        HorizontalContentAlignment = HorizontalAlignment.Stretch,
                        Items =
                        {
                            Heading("1. Site"),
                            _yardLabel, pickYard,
                            _obstaclesLabel, pickObstacles,
                            Heading("2. Trailer bay"),
                            _bayLabel, bay,
                            Heading("3. Search"),
                            new Label { Text = "Keep the route as drawn up to, and let the search move from:", Wrap = WrapMode.Word },
                            _adjustFrom,
                            _searchTime, _searchButton,
                            Heading("4. Suggestions"),
                            _suggestions, _areaStatus, _liveArea
                        }
                    }
                },
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

        if (definition.CheckAllowedArea &&
            definition.AllowedBoundaryObjectIds.FirstOrDefault(id => ClosedCurve(id) is not null) is var savedYard &&
            savedYard != Guid.Empty)
            _yardId = savedYard;
        if (definition.CheckObstacles)
            _obstacleIds = definition.ObstacleObjectIds.Where(id => ClosedCurve(id) is not null).ToList();
        RefreshSite();
        Rebuild(definition);
    }

    private static Label Heading(string text) =>
        new() { Text = text, Font = Eto.Drawing.Fonts.Sans(9, Eto.Drawing.FontStyle.Bold) };

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
        var selectedStart = _adjustFrom.SelectedKey;
        _adjustFrom.Items.Clear();
        if (journey is null) return;
        for (var index = 0; index < journey.Controls.Count; index++)
        {
            _controls.Items.Add(Row(definition, journey, index));
            if (index < journey.Controls.Count - 1 && journey.Controls[index].Direction == TravelDirection.Forward)
                _adjustFrom.Items.Add(new ListItem
                {
                    Key = index.ToString(),
                    Text = index == 0 ? "control 1 (search the whole route)"
                        : index == 1 ? "control 2 (keep control 1 as drawn)"
                        : $"control {index + 1} (keep controls 1–{index} as drawn)"
                });
        }
        // The first control gives the search the whole approach to work with; later ones are for
        // keeping a part of the route that is already settled.
        _adjustFrom.SelectedKey = _adjustFrom.Items.Any(item => item.Key == selectedStart)
            ? selectedStart : _adjustFrom.Items.FirstOrDefault()?.Key;
        RefreshBay(definition);
    }

    private void RefreshBay(VehicleAccessDefinition definition)
    {
        var journey = definition.Manoeuvre;
        if (journey is null || journey.Controls.Count == 0 ||
            journey.Controls[^1] is not { Direction: TravelDirection.Reverse } final)
        {
            _bayLabel.Text = "The road must end reversing into a bay. Set the last control to Reverse, or redraw it with PlanReverse.";
            return;
        }
        var vehicle = Catalog.Get(definition.VehicleId);
        if (!vehicle.IsArticulated)
        {
            _bayLabel.Text = "Bay placement is for articulated vehicles.";
            return;
        }
        if (final.ExitHeadingRadians is not double heading)
        {
            _bayLabel.Text = "No reversing direction yet — required for the search.";
            return;
        }
        // The control line's last vertex is where the axle is now, dragged or not.
        var axle = _document.Objects.FindId(_sourceId)?.Geometry is Curve line
            ? AccessDefinitionStore.PolylineVertices(line, _document.ModelUnitSystem)[^1]
            : final.PositionMetres;
        var rear = TrailerBay.RearFromAxle(vehicle, axle, heading);
        var degrees = heading * 180.0 / Math.PI;
        _bayLabel.Text = $"Trailer rear at ({rear.X:0.##}, {rear.Y:0.##}) m, reversing in at {(degrees < 0 ? degrees + 360 : degrees):0.#}° (purple).";
    }

    private Curve? ClosedCurve(Guid id) =>
        id != _sourceId && _document.Objects.FindId(id)?.Geometry is Curve curve && IsClosedEnough(curve) ? curve : null;

    private bool IsClosedEnough(Curve curve) =>
        curve.IsClosed || curve.IsClosable(_document.ModelAbsoluteTolerance);

    /// <summary>
    /// Re-reads the picked site from the document: labels, the polygons the live readout measures
    /// and what the viewport highlights. Curves deleted or opened since the pick drop out.
    /// </summary>
    private void RefreshSite()
    {
        if (_yardId is Guid staleYard && ClosedCurve(staleYard) is null) _yardId = null;
        _obstacleIds = _obstacleIds.Where(id => ClosedCurve(id) is not null).Distinct().ToList();
        _searchYard = _yardId is Guid yardId ? SampleClosed(ClosedCurve(yardId)!) : null;
        _searchObstacles = _obstacleIds
            .Select(id => (IReadOnlyList<Point2>)SampleClosed(ClosedCurve(id)!)).ToArray();
        _yardLabel.Text = _yardId is Guid picked
            ? $"Yard: {CurveName(picked)} (green)."
            : "Yard: not picked — required for the search.";
        _obstaclesLabel.Text = _obstacleIds.Count == 0
            ? "Obstacles: none picked."
            : $"Obstacles: {_obstacleIds.Count} closed curve{(_obstacleIds.Count == 1 ? string.Empty : "s")} (red).";
        _labels.YardId = _yardId;
        _labels.ObstacleIds = _obstacleIds.ToArray();
        _document.Views.Redraw();
    }

    private string CurveName(Guid id) =>
        _document.Objects.FindId(id) is { Name.Length: > 0 } named ? $"“{named.Name}”" : "closed curve";

    /// <summary>
    /// Picks closed curves in the viewport for the site. The road's own control line is selected while
    /// editing, and a picker that honoured that selection took it as the yard — an open curve — before
    /// anything could be clicked; so preselection is ignored and the selection restored afterwards.
    /// </summary>
    private List<Guid>? PickClosedCurves(string prompt, bool multiple)
    {
        _document.Objects.UnselectAll();
        try
        {
            using var picker = new GetObject();
            picker.SetCommandPrompt(prompt);
            picker.GeometryFilter = ObjectType.Curve;
            picker.SubObjectSelect = false;
            picker.EnablePreSelect(false, true);
            picker.SetCustomGeometryFilter((rhinoObject, geometry, _) =>
                rhinoObject?.Id != _sourceId && geometry is Curve curve && IsClosedEnough(curve));
            if (multiple)
            {
                picker.AcceptNothing(true);
                picker.GetMultiple(1, 0);
                if (picker.CommandResult() == CommandResult.Cancel) return null;
                if (picker.Result() == GetResult.Nothing) return [];
            }
            else if (picker.Get() != GetResult.Object) return null;
            return Enumerable.Range(0, picker.ObjectCount).Select(index => picker.Object(index).ObjectId).ToList();
        }
        finally
        {
            _document.Objects.UnselectAll();
            _document.Objects.Select(_sourceId);
            if (_document.Objects.FindId(_sourceId) is { } source) source.GripsOn = true;
            _document.Views.Redraw();
        }
    }

    private void PickYard()
    {
        if (_searching) return;
        _areaStatus.Text = "Click one closed curve around the planning yard in the viewport. Open curves cannot be picked. Esc keeps the current yard.";
        var picked = PickClosedCurves("Select one closed curve around the planning yard", multiple: false);
        if (picked is null)
        {
            _areaStatus.Text = "Yard unchanged.";
            return;
        }
        _yardId = picked[0];
        _obstacleIds.Remove(picked[0]);
        ClearSuggestions();
        RefreshSite();
        _areaStatus.Text = "Yard picked. Pick obstacles, check the trailer bay, then search.";
    }

    private void PickObstacles()
    {
        if (_searching) return;
        _areaStatus.Text = "Click closed curves around buildings and storage in the viewport, then press Enter. Enter with nothing selected clears the obstacles; Esc keeps them.";
        var picked = PickClosedCurves("Select closed building and storage outlines", multiple: true);
        if (picked is null)
        {
            _areaStatus.Text = "Obstacles unchanged.";
            return;
        }
        _obstacleIds = picked.Where(id => id != _yardId).ToList();
        ClearSuggestions();
        RefreshSite();
        _areaStatus.Text = _obstacleIds.Count == 0 ? "Obstacles cleared." : $"{_obstacleIds.Count} obstacle(s) picked.";
    }

    private void ClearSuggestions()
    {
        _searchResult = null;
        _suggestions.Items.Clear();
        _suggestions.Enabled = false;
        _labels.SearchCandidate = null;
        _baselineCurve = null;
        _baselineDefinition = null;
        _appliedFingerprint = null;
    }

    private void StartAreaSearch()
    {
        if (_searching) return;
        if (Read() is not { Manoeuvre: { } stored } definition ||
            _document.Objects.FindId(_sourceId)?.Geometry is not Curve sourceCurve)
        {
            _areaStatus.Text = "The saved road could not be read. Close Edit Road and open it again.";
            return;
        }
        if (definition.VehicleId != "SVT")
        {
            _areaStatus.Text = "The clear-area search currently supports SVT journeys only.";
            return;
        }
        if (!int.TryParse(_adjustFrom.SelectedKey, out var first))
        {
            _areaStatus.Text = "The route needs a forward control before the final reverse for the search to move.";
            return;
        }
        var journey = ManoeuvreControlReconciler.Reconcile(stored,
            AccessDefinitionStore.PolylineVertices(sourceCurve, _document.ModelUnitSystem));
        if (journey.Controls[^1] is not { Direction: TravelDirection.Reverse,
            Kind: ManoeuvreControlKind.Aim, ExitHeadingRadians: not null })
        {
            _areaStatus.Text = "Set the trailer bay first (step 2): the road must end reversing into a bay with a direction.";
            return;
        }
        RefreshSite();
        if (_searchYard is not { } yard)
        {
            _areaStatus.Text = "Pick the planning yard first (step 1).";
            return;
        }
        var obstacles = _searchObstacles;
        ClearSuggestions();
        _baselineCurve = (Curve)sourceCurve.Duplicate();
        _baselineDefinition = definition;
        _searching = true;
        _searchButton.Text = "Stop search";
        _areaStatus.Text = "Searching drivable manoeuvres and measuring their clear yard area…";
        _searchCancellation?.Cancel();
        var cancellation = _searchCancellation = new CancellationTokenSource();
        var vehicle = Catalog.Get("SVT");
        var mode = vehicle.DrivingModes[definition.ModeId];
        var seconds = _searchTime.SelectedKey == "120" ? 120 : 30;
        _ = Task.Run(() => ManoeuvringAreaSearch.Find(vehicle, mode, journey, first,
                yard, obstacles, definition.ClearanceMetres, TimeSpan.FromSeconds(seconds), cancellation.Token))
            .ContinueWith(work => Application.Instance.AsyncInvoke(() =>
            {
                if (_closing || cancellation != _searchCancellation) return;
                _searching = false;
                _searchButton.Text = "Find less clear area…";
                if (!work.IsCompletedSuccessfully)
                {
                    _areaStatus.Text = $"Search stopped: {work.Exception?.GetBaseException().Message ?? "cancelled"}";
                    return;
                }
                _searchResult = work.Result;
                _suppressSuggestionEvent = true;
                _suggestions.Items.Clear();
                for (var index = 0; index < work.Result.Suggestions.Count; index++)
                {
                    var candidate = work.Result.Suggestions[index];
                    var change = work.Result.Current is { } current
                        ? $" ({candidate.ClearAreaSquareMetres - current.ClearAreaSquareMetres:+0.0;-0.0;0.0} m²)"
                        : string.Empty;
                    _suggestions.Items.Add(new ListItem { Key = index.ToString(),
                        Text = $"{index + 1}. {candidate.ClearAreaSquareMetres:0.0} m²{change}" });
                }
                if (work.Result.Suggestions.Count > 0)
                    _suggestions.Items.Add(new ListItem { Key = CurrentRouteKey, Text = "Current route (unchanged)" });
                _suggestions.Enabled = work.Result.Suggestions.Count > 0;
                var summary = $"Found {work.Result.Suggestions.Count} suggestions from {work.Result.Tried} routes"
                    + (work.Result.TimeLimitReached ? " before the time limit" : string.Empty) + ". ";
                if (work.Result.Suggestions.Count == 0)
                {
                    _suppressSuggestionEvent = false;
                    _areaStatus.Text = $"No passing route found. Tried {work.Result.Tried}; outside yard {work.Result.OutsideYard}, fixed-object conflicts {work.Result.ObstacleConflicts}, vehicle/arrival failures {work.Result.VehicleFailures}.";
                }
                else if (!SourceMatchesBaseline())
                {
                    // Applying now would silently overwrite whatever was changed while it ran.
                    _suggestions.SelectedKey = CurrentRouteKey;
                    _suppressSuggestionEvent = false;
                    _areaStatus.Text = summary + "The controls changed during the search, so nothing was applied. Choosing a suggestion replaces those changes.";
                }
                else
                {
                    // The best suggestion becomes the draft straight away, so Save keeps it without a
                    // separate step; choosing another entry, or the current route, switches.
                    _suggestions.SelectedKey = "0";
                    _suppressSuggestionEvent = false;
                    SelectSuggestion("0", summary);
                }
                _document.Views.Redraw();
            }));
    }

    private static string PreviewText(ManoeuvringAreaSearch.Candidate candidate) =>
        $"Preview {candidate.ClearAreaSquareMetres:0.0} m²; bay error {candidate.PositionErrorMetres:0.00} m / {candidate.HeadingErrorDegrees:0.0}°; {candidate.Corrections} correction(s). Orange dot marks the tightest location. Save keeps it, or adjust its controls first.";

    private Point2[] SampleClosed(Curve curve) => CurveOutline.Points(curve, _document.ModelUnitSystem);

    private const string CurrentRouteKey = "current";

    private bool SourceMatchesBaseline() =>
        _baselineCurve is not null && _baselineDefinition is not null &&
        _document.Objects.FindId(_sourceId)?.Geometry is Curve now && GeometryBase.GeometryEquals(now, _baselineCurve) &&
        Read() is { } definition &&
        AccessDefinitionSerializer.Serialize(definition) == AccessDefinitionSerializer.Serialize(_baselineDefinition);

    private void SelectSuggestion(string? key, string prefix = "")
    {
        if (_searchResult is null || _baselineCurve is null || _baselineDefinition is null) return;
        if (key == CurrentRouteKey)
        {
            if (!WriteDraft((Curve)_baselineCurve.Duplicate(), _baselineDefinition)) return;
            _labels.SearchCandidate = null;
            _appliedFingerprint = null;
            _areaStatus.Text = prefix + "Showing the route as it was before the search. Choose a suggestion to try it.";
            return;
        }
        if (!int.TryParse(key, out var index) || index < 0 || index >= _searchResult.Suggestions.Count) return;
        if (_yardId is not Guid yardId)
        {
            _areaStatus.Text = "The yard can no longer be read. Pick the yard and search again.";
            return;
        }
        var suggestion = _searchResult.Suggestions[index];
        var scale = RhinoMath.UnitScale(UnitSystem.Meters, _document.ModelUnitSystem);
        var curve = new PolylineCurve(new[] { suggestion.Manoeuvre.StartPositionMetres }
            .Concat(suggestion.Manoeuvre.Controls.Select(control => control.PositionMetres))
            .Select(point => new Point3d(point.X * scale, point.Y * scale, point.Z * scale)));
        var draft = _baselineDefinition with
        {
            Manoeuvre = suggestion.Manoeuvre,
            CheckAllowedArea = true,
            AllowedBoundaryObjectIds = [yardId],
            // The palette's list started from the saved obstacles, so it replaces them: an obstacle
            // removed from the pick is no longer checked.
            CheckObstacles = _obstacleIds.Count > 0,
            ObstacleObjectIds = _obstacleIds.ToArray()
        };
        if (!WriteDraft(curve, draft)) return;
        _labels.SearchCandidate = suggestion;
        _appliedFingerprint = AccessDefinitionStore.Fingerprint(_document.Objects.FindId(_sourceId)!.Geometry);
        _areaStatus.Text = prefix + $"Suggestion {index + 1} is the draft. " + PreviewText(suggestion);
    }

    /// <summary>
    /// Puts a route on the control line as the unsaved edit. Save commits it like any other edit and
    /// Discard still restores the road as it was opened.
    /// </summary>
    private bool WriteDraft(Curve curve, VehicleAccessDefinition definition)
    {
        if (_document.Objects.FindId(_sourceId) is not { Geometry: Curve previousCurve } source || Read() is not { } previous)
        {
            _areaStatus.Text = "The road can no longer be read. Close Edit Road and open it again.";
            return false;
        }
        var previousCopy = (Curve)previousCurve.Duplicate();
        source.GripsOn = false;
        if (!_document.Objects.Replace(_sourceId, curve) || !AccessDefinitionStore.Write(_document, _sourceId, definition))
        {
            _document.Objects.Replace(_sourceId, previousCopy);
            AccessDefinitionStore.Write(_document, _sourceId, previous);
            RestoreEditSelection();
            _areaStatus.Text = "That route could not be applied; the previous edit remains.";
            return false;
        }
        _areaDraftApplied = !(GeometryBase.GeometryEquals(curve, _originalCurve) &&
            AccessDefinitionSerializer.Serialize(definition) == AccessDefinitionSerializer.Serialize(_originalDefinition));
        LiveJourneyEditConduit.ForceDraft(_document, _stableId, _areaDraftApplied);
        Rebuild(definition);
        RestoreEditSelection();
        return true;
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

        var heading = new Button
        {
            Width = 72,
            Text = control.ExitHeadingRadians is double exit ? $"{Degrees(exit):0.#}°" : "Dir…",
            Enabled = control.Kind != ManoeuvreControlKind.Turn,
            ToolTip = control.Kind == ManoeuvreControlKind.Turn
                ? "A full-lock Turn has no direction to leave along."
                : control.ExitHeadingRadians is null
                    ? "Pick the direction to leave this control along (ortho and snaps apply)."
                    : "Change the direction this control leaves along; Enter while picking clears it."
        };
        heading.Click += (_, _) => PickControlDirection(index);

        var row = new StackLayout
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            VerticalContentAlignment = VerticalAlignment.Center,
            Items = { number, direction, kind, heading }
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

    /// <summary>
    /// Places the bay the way it is dimensioned on a site plan: the rear of the trailer, then the way
    /// it reverses in. The journey stores the trailer axle, which follows from those two, and the
    /// control line's last vertex moves to it as though its grip had been dragged. The direction is a
    /// real pick from the rear, so Rhino's ortho and object snaps constrain it as they would a line.
    /// </summary>
    private void PickTrailerBay()
    {
        if (_searching) return;
        if (Read() is not { Manoeuvre: { } journey } definition ||
            _document.Objects.FindId(_sourceId)?.Geometry is not Curve controlCurve)
        {
            _areaStatus.Text = "The saved road could not be read. Close Edit Road and open it again.";
            return;
        }
        if (journey.Controls.Count == 0 || journey.Controls[^1] is not { Direction: TravelDirection.Reverse } final)
        {
            _areaStatus.Text = "The road must end reversing into the bay. Set the last control to Reverse, or redraw it with PlanReverse.";
            return;
        }
        var vehicle = Catalog.Get(definition.VehicleId);
        if (!vehicle.IsArticulated)
        {
            _areaStatus.Text = "Bay placement is for articulated vehicles.";
            return;
        }
        var scale = RhinoMath.UnitScale(UnitSystem.Meters, _document.ModelUnitSystem);
        var vertices = AccessDefinitionStore.PolylineVertices(controlCurve, _document.ModelUnitSystem);
        if (vertices.Count != journey.Controls.Count + 1)
        {
            _areaStatus.Text = "The control line no longer matches its controls. Discard and open Edit Road again.";
            return;
        }
        var axleNow = vertices[^1];
        Point3? rearNow = final.ExitHeadingRadians is double headingNow
            ? TrailerBay.RearFromAxle(vehicle, axleNow, headingNow) : null;

        _areaStatus.Text = "In the viewport: click the rear of the trailer at the bay"
            + (rearNow is null ? "." : " (Enter keeps it).")
            + " Then point the way it reverses in; ortho and snaps apply, or type an angle.";
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
                _areaStatus.Text = "Trailer bay unchanged.";
                return;
            }
        }

        var pivot = new Point3d(rear.X * scale, rear.Y * scale, rear.Z * scale);
        var picked = PickDirection(pivot,
            "Point the way the trailer reverses into the bay, or type an angle from the CPlane",
            allowClear: false,
            (display, heading) => DrawDockedTrailer(display, vehicle, TrailerBay.AxleFromRear(vehicle, rear, heading), heading),
            out var bayHeading);
        if (picked != DirectionPick.Picked)
        {
            RestoreEditSelection();
            _areaStatus.Text = "Trailer bay unchanged.";
            return;
        }

        var axle = TrailerBay.AxleFromRear(vehicle, rear, bayHeading);
        var controls = journey.Controls.ToArray();
        controls[^1] = final with { ExitHeadingRadians = bayHeading, Kind = ManoeuvreControlKind.Aim };
        ClearSuggestions();
        var points = vertices.Take(vertices.Count - 1).Append(axle)
            .Select(point => new Point3d(point.X * scale, point.Y * scale, point.Z * scale));
        _document.Objects.Replace(_sourceId, new PolylineCurve(points));
        WriteIntent(definition, journey with { Controls = controls });
        RestoreEditSelection();
        _areaStatus.Text = "Trailer bay set. Search to compare approaches, or Save.";
    }

    private enum DirectionPick { Picked, Cleared, Cancelled }

    /// <summary>
    /// Picks a direction from <paramref name="pivot"/>. It is a real point pick from that base point,
    /// so Rhino's ortho (F8 or Shift, at its ortho angle on the CPlane) and object snaps constrain it
    /// as they would drawing a line; an angle can also be typed, counted from the CPlane.
    /// </summary>
    private DirectionPick PickDirection(
        Point3d pivot, string prompt, bool allowClear,
        Action<DisplayPipeline, double>? draw, out double headingRadians)
    {
        headingRadians = 0.0;
        var reference = _document.Views.ActiveView?.ActiveViewport is { } viewport ? CPlaneHeading(viewport) : 0.0;
        using var picker = new GetPoint();
        picker.SetCommandPrompt(prompt);
        picker.SetBasePoint(pivot, true);
        picker.DrawLineFromPoint(pivot, true);
        picker.PermitObjectSnap(true);
        picker.PermitOrthoSnap(true);
        picker.AcceptNumber(true, true);
        picker.AcceptNothing(allowClear);
        picker.DynamicDraw += (_, args) =>
        {
            var delta = args.CurrentPoint - pivot;
            if (draw is null || new Vector2d(delta.X, delta.Y).Length <= _document.ModelAbsoluteTolerance) return;
            draw(args.Display, Math.Atan2(delta.Y, delta.X));
        };
        while (true)
        {
            var result = picker.Get();
            if (result == GetResult.Nothing && allowClear) return DirectionPick.Cleared;
            if (result == GetResult.Number)
            {
                headingRadians = Geometry2D.NormalizeAngle(reference + (picker.Number() * Math.PI / 180.0));
                return DirectionPick.Picked;
            }
            if (result != GetResult.Point) return DirectionPick.Cancelled;
            var delta = picker.Point() - pivot;
            if (new Vector2d(delta.X, delta.Y).Length > _document.ModelAbsoluteTolerance)
            {
                headingRadians = Math.Atan2(delta.Y, delta.X);
                return DirectionPick.Picked;
            }
            RhinoApp.WriteLine("That point is on the control itself, so it gives no direction; pick away from it.");
        }
    }

    private void DrawDockedTrailer(DisplayPipeline display, VehicleDefinition vehicle, Point3 axle, double heading)
    {
        var scale = RhinoMath.UnitScale(UnitSystem.Meters, _document.ModelUnitSystem);
        var outline = TrailerBay.DockedOutline(vehicle, axle, heading)
            .Select(point => new Point3d(point.X * scale, point.Y * scale, axle.Z * scale)).ToList();
        if (outline.Count == 0) return;
        outline.Add(outline[0]);
        display.DrawPolyline(new Polyline(outline), Color.MediumPurple, 2);
    }

    /// <summary>
    /// Changes the direction a control leaves along. The final reverse into a bay turns about the
    /// trailer's rear, which stays where it is, so the bay is re-angled rather than moved; any other
    /// control turns about its own point. Enter clears the direction, returning the leg to a plain aim.
    /// </summary>
    private void PickControlDirection(int index)
    {
        if (_searching) return;
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
            PickTrailerBay();
            return;
        }

        var scale = RhinoMath.UnitScale(UnitSystem.Meters, _document.ModelUnitSystem);
        var point = vertices[index + 1];
        var rear = bay ? TrailerBay.RearFromAxle(vehicle, point, control.ExitHeadingRadians!.Value) : point;
        _controlsStatus.Text = bay
            ? $"In the viewport: point the way the trailer reverses in; it turns about its rear. Ortho and snaps apply, or type an angle. Enter clears it."
            : $"In the viewport: point the way to leave control {index + 1}. Ortho and snaps apply, or type an angle. Enter clears it.";
        _document.Objects.UnselectAll();
        var picked = PickDirection(new Point3d(rear.X * scale, rear.Y * scale, rear.Z * scale),
            bay
                ? "Point the way the trailer reverses into the bay, or type an angle from the CPlane; Enter clears it"
                : $"Point the way to leave control {index + 1}, or type an angle from the CPlane; Enter clears it",
            allowClear: true,
            bay ? (display, heading) => DrawDockedTrailer(display, vehicle, TrailerBay.AxleFromRear(vehicle, rear, heading), heading)
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
        if (bay && picked == DirectionPick.Picked)
        {
            var axle = TrailerBay.AxleFromRear(vehicle, rear, headingRadians);
            var points = vertices.Select((vertex, at) => at == vertices.Count - 1 ? axle : vertex)
                .Select(vertex => new Point3d(vertex.X * scale, vertex.Y * scale, vertex.Z * scale));
            _document.Objects.Replace(_sourceId, new PolylineCurve(points));
        }
        ClearSuggestions();
        WriteIntent(definition, journey with { Controls = controls });
        RestoreEditSelection();
        _controlsStatus.Text = picked == DirectionPick.Picked
            ? $"Control {index + 1} now leaves along {Degrees(headingRadians):0.#}°. The blue sweep previews it; Save keeps it."
            : $"Control {index + 1} direction cleared.";
    }

    private static double Degrees(double radians)
    {
        var degrees = radians * 180.0 / Math.PI;
        return degrees < 0.0 ? degrees + 360.0 : degrees;
    }

    private void RestoreEditSelection()
    {
        _document.Objects.UnselectAll();
        _document.Objects.Select(_sourceId);
        if (_document.Objects.FindId(_sourceId) is { } source) source.GripsOn = true;
        _document.Views.Redraw();
    }

    private static double CPlaneHeading(global::Rhino.Display.RhinoViewport viewport)
    {
        var axis = viewport.GetConstructionPlane().Plane.XAxis;
        return Math.Abs(axis.X) + Math.Abs(axis.Y) <= RhinoMath.ZeroTolerance ? 0.0 : Math.Atan2(axis.Y, axis.X);
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
        LiveJourneyEditConduit.ForceDraft(_document, _stableId, false);
        var result = RRUpdateVehicleAccessCommand.Update(_document, source);
        if (result != CommandResult.Success)
        {
            LiveJourneyEditConduit.Begin(_document, _stableId);
            if (_areaDraftApplied) LiveJourneyEditConduit.ForceDraft(_document, _stableId, true);
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
        _searchCancellation?.Cancel();
        LiveJourneyEditConduit.ForceDraft(_document, _stableId, false);
        _labels.SearchCandidate = null;
        if (_document.Objects.FindId(_sourceId) is { } editable) editable.GripsOn = false;
        if (_document.Objects.FindId(_sourceId) is { Geometry: Curve current } &&
            !GeometryBase.GeometryEquals(_originalCurve, current))
            _document.Objects.Replace(_sourceId, _originalCurve);
        if (_document.Objects.FindId(_sourceId) is not null)
            AccessDefinitionStore.Write(_document, _sourceId, _originalDefinition);
        Close();
    }

    private void Poll()
    {
        if (_document.Objects.FindId(_sourceId) is null)
        {
            _status.Text = "The control line has been deleted.";
            return;
        }
        // The suggestion's clearance outline and tightest point describe it as suggested; once its
        // controls are dragged they describe something else, and the live sweep takes over.
        if (_labels.SearchCandidate is not null && _appliedFingerprint is not null &&
            AccessDefinitionStore.Fingerprint(_document.Objects.FindId(_sourceId)!.Geometry) != _appliedFingerprint)
        {
            _labels.SearchCandidate = null;
            _document.Views.Redraw();
        }
        _status.Text = LiveJourneyEditConduit.IsPreviewing(_document, _stableId)
            ? "Unsaved edit — Save reruns the checks and refreshes the sizing results."
            : "No change yet. The saved result is what you see.";
        if (_searching || _searchYard is not { } yard ||
            LiveJourneyEditConduit.CurrentResult(_document, _stableId) is not { Clearance.IsSuccess: true } preview)
        {
            _liveArea.Text = string.Empty;
            return;
        }
        var footprint = preview.Clearance.Region!;
        var area = AccessFootprint.IntersectionArea(footprint, yard);
        var outside = AccessFootprint.Outside(footprint, new[] { yard }) is not null;
        var obstacle = _searchObstacles.Any(polygon => AccessFootprint.ConflictPoint(footprint, polygon, true) is not null);
        var final = preview.Definition.Controls[^1];
        var vehicle = Catalog.Get("SVT");
        var end = preview.Journey.EndState;
        var trailer = ArticulationTrace.AtEndOf(vehicle, preview.Journey.Samples)!
            .Poses(end.RearAxleCentreMetres.XY, end.VehicleHeadingRadians)[^1];
        var positionError = trailer.AxleCentreMetres.DistanceTo(final.PositionMetres.XY);
        var headingError = final.ExitHeadingRadians is double heading
            ? Math.Abs(Geometry2D.NormalizeAngle(trailer.HeadingRadians - heading - Math.PI)) * 180.0 / Math.PI
            : double.PositiveInfinity;
        var maximumFold = ArticulationTrace.Follow(vehicle, preview.Journey.Samples)
            .Max(step => Math.Abs(step.Chain!.ArticulationAngleRadians(0, step.VehicleHeadingRadians)));
        var foldExceeded = maximumFold >= 65.0 * Math.PI / 180.0;
        var difference = _searchResult?.Current is { } current
            ? $" ({area - current.ClearAreaSquareMetres:+0.0;-0.0;0.0} m² vs current)" : string.Empty;
        _liveArea.Text = $"Live clear area {area:0.0} m²{difference}; bay error {positionError:0.00} m / {headingError:0.0}°. "
            + (outside ? "Outside yard. " : string.Empty)
            + (obstacle ? "Fixed-object conflict. " : string.Empty)
            + (foldExceeded ? "Trailer fold limit. " : string.Empty)
            + (preview.Journey.RequestedAngleExceeded ? "Steering limit. " : string.Empty)
            + (!outside && !obstacle && !foldExceeded
                && positionError <= TrailerReversePlanner.ArrivalToleranceMetres
                && headingError <= TrailerReversePlanner.ArrivalToleranceDegrees
                && !preview.Journey.RequestedAngleExceeded
                ? "Position, heading and site clear." : "Adjust the controls before saving.");
    }

    private void Teardown()
    {
        _closing = true;
        _searchCancellation?.Cancel();
        _timer.Stop();
        _labels.Enabled = false;
        LiveJourneyEditConduit.ForceDraft(_document, _stableId, false);
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
        public ManoeuvringAreaSearch.Candidate? SearchCandidate { get; set; }
        public Guid? YardId { get; set; }
        public Guid[] ObstacleIds { get; set; } = [];

        protected override void DrawForeground(DrawEventArgs e)
        {
            if (e.RhinoDoc is not { } document || document.Objects.FindId(SourceId) is not { } source) return;
            if (!AccessDefinitionStore.TryRead(source, out var definition, out _) || definition?.Manoeuvre is null) return;
            if (source.Geometry is not Curve curve) return;
            var vertices = AccessDefinitionStore.PolylineVertices(curve, document.ModelUnitSystem);
            var scale = RhinoMath.UnitScale(UnitSystem.Meters, document.ModelUnitSystem);

            // The site the search will use, so what was picked is visible rather than remembered.
            if (YardId is Guid yardId && document.Objects.FindId(yardId)?.Geometry is Curve yard)
                e.Display.DrawCurve(yard, Color.SeaGreen, 4);
            foreach (var obstacleId in ObstacleIds)
                if (document.Objects.FindId(obstacleId)?.Geometry is Curve obstacle)
                    e.Display.DrawCurve(obstacle, Color.IndianRed, 3);

            // The trailer as it stands in the bay at the control line's current end, rear marked.
            if (definition.Manoeuvre.Controls.Count > 0 && vertices.Count > 1 &&
                definition.Manoeuvre.Controls[^1] is { Direction: TravelDirection.Reverse, ExitHeadingRadians: double bayHeading } &&
                Catalog.Get(definition.VehicleId) is { IsArticulated: true } bayVehicle)
            {
                var axle = vertices[^1];
                var docked = TrailerBay.DockedOutline(bayVehicle, axle, bayHeading)
                    .Select(point => new Point3d(point.X * scale, point.Y * scale, axle.Z * scale)).ToList();
                if (docked.Count > 0)
                {
                    docked.Add(docked[0]);
                    e.Display.DrawDottedPolyline(docked, Color.MediumPurple, true);
                    var rear = TrailerBay.RearFromAxle(bayVehicle, axle, bayHeading);
                    e.Display.DrawDot(new Point3d(rear.X * scale, rear.Y * scale, rear.Z * scale),
                        "rear", Color.MediumPurple, Color.White);
                }
            }
            if (SearchCandidate is { } suggestion)
            {
                var height = suggestion.Route[0].PositionMetres.Z * scale;
                e.Display.DrawPolyline(suggestion.Route.Select(sample =>
                    new Point3d(sample.PositionMetres.X * scale,
                        sample.PositionMetres.Y * scale, sample.PositionMetres.Z * scale)),
                    Color.MediumVioletRed, 3);
                foreach (var loop in new[] { suggestion.Clearance.OuterBoundary }
                    .Concat(suggestion.Clearance.Holes))
                    e.Display.DrawPolyline(loop.Append(loop[0]).Select(point =>
                        new Point3d(point.X * scale, point.Y * scale, height)),
                        Color.SeaGreen, 2);
                e.Display.DrawDot(new Point3d(suggestion.LimitingPointMetres.X * scale,
                    suggestion.LimitingPointMetres.Y * scale, height),
                    "tightest", Color.DarkOrange, Color.White);
            }
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
