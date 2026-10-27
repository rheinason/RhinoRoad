using Eto.Forms;
using Rhino;
using Rhino.Geometry;
using RhinoRoad.Core;

namespace RhinoRoad.Rhino.Services;

/// <summary>
/// The clear-area search in the Edit Road palette: pick the site and the bay, search for a route
/// that clears less of the yard, and try the suggestions as the draft. It is collapsed until asked
/// for and hidden entirely for a vehicle the search cannot plan, so the palette is the road's
/// controls first; the live line under it is measured only when a new preview arrives, not polled.
/// </summary>
internal sealed class AreaSearchPanel : Panel
{
    private const string CurrentRouteKey = "current";
    private const string SearchText = "Find less clear area…";

    private readonly JourneyEditForm _host;
    private readonly RhinoDoc _document;
    private readonly Guid _sourceId;
    private readonly JourneyEditLabelConduit _labels;
    private readonly Expander _expander;
    private readonly Label _areaStatus = new() { Wrap = WrapMode.Word };
    private readonly Label _liveArea = new() { Wrap = WrapMode.Word };
    private readonly Label _yardLabel = new() { Wrap = WrapMode.Word };
    private readonly Label _obstaclesLabel = new() { Wrap = WrapMode.Word };
    private readonly Label _bayLabel = new() { Wrap = WrapMode.Word };
    private readonly DropDown _adjustFrom = new();
    private readonly DropDown _searchTime = new();
    private readonly DropDown _suggestions = new();
    private readonly Button _searchButton = new() { Text = SearchText };
    private readonly Button _bayButton = new() { Text = "Set bay…" };
    private VehicleDefinition? _vehicle;
    private bool _closed;
    private bool _suppressSuggestionEvent;
    // The road as it stood when the search started. Every suggestion is applied on top of this, so
    // moving between suggestions -- or back to the current route -- never compounds one onto another.
    private Curve? _baselineCurve;
    private VehicleAccessDefinition? _baselineDefinition;
    // The control line as the applied suggestion left it; once it differs, the grips have moved.
    private Curve? _appliedCurve;
    private CancellationTokenSource? _searchCancellation;
    private ManoeuvringAreaSearch.Result? _searchResult;
    private IReadOnlyList<Point2>? _searchYard;
    private IReadOnlyList<IReadOnlyList<Point2>> _searchObstacles = [];
    // The preview the live line was last measured for; a new one is measured once, off the UI thread.
    private JourneyEditPreviewResult? _measured;
    // The site the search measures against, picked in the palette. They start from the references
    // the road was saved with, so a road that already checks a yard and buildings needs no picking.
    private Guid? _yardId;
    private List<Guid> _obstacleIds = [];

    public AreaSearchPanel(JourneyEditForm host, RhinoDoc document, Guid sourceId,
        JourneyEditLabelConduit labels, VehicleAccessDefinition definition)
    {
        _host = host;
        _document = document;
        _sourceId = sourceId;
        _labels = labels;

        _searchButton.Click += (_, _) =>
        {
            if (Searching)
            {
                _searchCancellation?.Cancel();
                _areaStatus.Text = "Stopping search…";
            }
            else StartAreaSearch();
        };
        _bayButton.Click += (_, _) =>
        {
            if (!Searching && !CommandGuard.Busy(message => _areaStatus.Text = message))
                _host.PickBay(message => _areaStatus.Text = message);
        };
        var pickYard = new Button { Text = "Pick yard…", ToolTip = "One closed curve around the area the vehicle may use." };
        pickYard.Click += (_, _) => PickYard();
        var pickObstacles = new Button { Text = "Pick obstacles…", ToolTip = "Closed curves around buildings, storage and anything else the vehicle must clear. Enter with nothing selected clears the list." };
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

        _expander = new Expander
        {
            Header = new Label { Text = "Clear-area search", Font = Eto.Drawing.Fonts.Sans(9, Eto.Drawing.FontStyle.Bold) },
            Expanded = false,
            Content = new StackLayout
            {
                Padding = new Eto.Drawing.Padding(8, 4, 0, 0),
                Spacing = 5,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Items =
                {
                    Heading("1. Site"),
                    _yardLabel, pickYard,
                    _obstaclesLabel, pickObstacles,
                    Heading("2. Bay"),
                    _bayLabel, _bayButton,
                    Heading("3. Search"),
                    new Label { Text = "Keep the route as drawn up to, and let the search move from:", Wrap = WrapMode.Word },
                    _adjustFrom,
                    _searchTime, _searchButton,
                    Heading("4. Suggestions"),
                    _suggestions, _areaStatus, _liveArea
                }
            }
        };
        _expander.ExpandedChanged += (_, _) =>
        {
            _measured = null;
            UpdateReadout();
            LayoutChanged?.Invoke();
        };
        Content = _expander;

        if (definition.CheckAllowedArea &&
            definition.AllowedBoundaryObjectIds.FirstOrDefault(id => ClosedCurve(id) is not null) is var savedYard &&
            savedYard != Guid.Empty)
            _yardId = savedYard;
        if (definition.CheckObstacles)
            _obstacleIds = definition.ObstacleObjectIds.Where(id => ClosedCurve(id) is not null).ToList();
        RefreshSite();
    }

    /// <summary>
    /// Whether the clear-area search can plan for <paramref name="vehicle"/>: any vehicle that can be
    /// docked, rigid or articulated. The single place the palette asks, and the same capability
    /// <see cref="ManoeuvringAreaSearch.Find"/> enforces.
    /// </summary>
    public static bool AreaSearchSupported(VehicleDefinition vehicle) => ReverseCapabilities.CanSearchArea(vehicle);

    /// <summary>Raised when the panel opens, closes, appears or disappears, so the palette can resize.</summary>
    public event Action? LayoutChanged;

    public bool Searching { get; private set; }

    public bool Expanded => _expander.Expanded;

    /// <summary>Whether <paramref name="objectId"/> is part of the site the search measures against.</summary>
    public bool UsesSite(Guid objectId) => _yardId == objectId || _obstacleIds.Contains(objectId);

    private static Label Heading(string text) =>
        new() { Text = text, Font = Eto.Drawing.Fonts.Sans(9, Eto.Drawing.FontStyle.Bold) };

    /// <summary>Follows the road's saved intent: which vehicle, which controls the search may move, the bay.</summary>
    public void Refresh(VehicleAccessDefinition definition)
    {
        // The vehicle the road is swept with, mirror allowance included, so the live readout measures
        // the envelope that is baked. A vehicle that is no longer loaded hides the search.
        _vehicle = RhinoRoadCatalog.TryRoadVehicle(definition, out var vehicle, out _) ? vehicle : null;
        if (_vehicle is not null)
            _bayButton.ToolTip = $"Pick {ReverseWording.BayRear(_vehicle)} at the bay, then point the way it reverses in.";
        var supported = _vehicle is not null && AreaSearchSupported(_vehicle);
        if (Visible != supported)
        {
            if (!supported)
            {
                StopSearch();
                ClearSuggestions();
            }
            Visible = supported;
            _labels.YardId = supported ? _yardId : null;
            _labels.ObstacleIds = supported ? _obstacleIds.ToArray() : [];
            LayoutChanged?.Invoke();
        }

        var journey = definition.Manoeuvre;
        var selectedStart = _adjustFrom.SelectedKey;
        _adjustFrom.Items.Clear();
        if (journey is not null)
            for (var index = 0; index < journey.Controls.Count - 1; index++)
                if (journey.Controls[index].Direction == TravelDirection.Forward)
                    _adjustFrom.Items.Add(new ListItem
                    {
                        Key = index.ToString(),
                        Text = index == 0 ? "control 1 (search the whole route)"
                            : index == 1 ? "control 2 (keep control 1 as drawn)"
                            : $"control {index + 1} (keep controls 1–{index} as drawn)"
                    });
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
        if (!RhinoRoadCatalog.TryRoadVehicle(definition, out var vehicle, out var vehicleError))
        {
            _bayLabel.Text = vehicleError;
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
        var rear = ReverseBay.RearFromAxle(vehicle, axle, heading);
        var body = ReverseWording.DockingBody(vehicle);
        _bayLabel.Text = $"Rear of the {body} at ({rear.X:0.##}, {rear.Y:0.##}) m, reversing in at {JourneyEditPicks.Degrees(heading):0.#}° (purple).";
    }

    /// <summary>
    /// The control line changed. A suggestion's clearance outline and tightest point describe it as
    /// suggested; once its controls are dragged they describe something else, and the live sweep
    /// takes over. Comparing the geometry once per change replaces a fingerprint taken every tick.
    /// </summary>
    public void SourceChanged(VehicleAccessDefinition? definition)
    {
        if (_labels.SearchCandidate is not null && _appliedCurve is not null &&
            !(_document.Objects.FindId(_sourceId)?.Geometry is Curve now && GeometryBase.GeometryEquals(now, _appliedCurve)))
        {
            _labels.SearchCandidate = null;
            _appliedCurve = null;
            _document.Views.Redraw();
        }
        if (definition is not null) RefreshBay(definition);
    }

    /// <summary>
    /// Measures the live line for the current preview, once per new preview and only while the
    /// panel is open; the Clipper area and the articulation walk run off the UI thread.
    /// </summary>
    public void UpdateReadout()
    {
        if (_closed) return;
        var preview = LiveJourneyEditConduit.CurrentResult(_document, _host.StableId);
        if (!Visible || !_expander.Expanded || Searching || _searchYard is not { } yard || _vehicle is not { } vehicle ||
            preview is not { Clearance.IsSuccess: true })
        {
            _measured = null;
            if (!string.IsNullOrEmpty(_liveArea.Text)) _liveArea.Text = string.Empty;
            return;
        }
        if (ReferenceEquals(preview, _measured)) return;
        _measured = preview;
        var obstacles = _searchObstacles;
        var current = _searchResult?.Current;
        _ = Task.Run(() => ClearAreaReadout.Measure(vehicle, preview, yard, obstacles))
            .ContinueWith(work =>
            {
                // Observed here, before the early return below, so a fault in a readout that is no
                // longer wanted is never left unobserved.
                _ = work.Exception;
                Application.Instance.AsyncInvoke(() =>
                {
                    if (_closed || !ReferenceEquals(_measured, preview)) return;
                    _liveArea.Text = work.IsCompletedSuccessfully
                        ? Describe(work.Result, current)
                        : $"Live clear area unavailable: {work.Exception?.GetBaseException().Message}";
                });
            }, TaskScheduler.Default);
    }

    private static string Describe(ClearAreaReadout readout, ManoeuvringAreaSearch.Candidate? current)
    {
        var difference = current is not null
            ? $" ({readout.ClearAreaSquareMetres - current.ClearAreaSquareMetres:+0.0;-0.0;0.0} m² vs current)" : string.Empty;
        var bay = readout.Bay is { } arrival
            ? $"; bay error {arrival.PositionErrorMetres:0.00} m / {arrival.HeadingErrorDegrees:0.0}°" : string.Empty;
        return $"Live clear area {readout.ClearAreaSquareMetres:0.0} m²{difference}{bay}. "
            + (readout.OutsideYard ? "Outside yard. " : string.Empty)
            + (readout.ObstacleConflict ? "Fixed-object conflict. " : string.Empty)
            + (readout.FoldLimitReached ? "Fold limit. " : string.Empty)
            + (readout.SteeringLimitReached ? "Steering limit. " : string.Empty)
            + (!readout.Passes ? "Adjust the controls before saving."
                : readout.Bay is not null ? "Position, heading and site clear." : "Site clear.");
    }

    private Curve? ClosedCurve(Guid id) =>
        id != _sourceId && _document.Objects.FindId(id)?.Geometry is Curve curve &&
        JourneyEditPicks.IsClosedEnough(_document, curve) ? curve : null;

    /// <summary>
    /// Re-reads the picked site from the document: labels, the polygons the live readout measures
    /// and what the viewport highlights. Curves deleted or opened since the pick drop out.
    /// </summary>
    public void RefreshSite()
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
        _labels.YardId = Visible ? _yardId : null;
        _labels.ObstacleIds = Visible ? _obstacleIds.ToArray() : [];
        _measured = null;
        UpdateReadout();
        _document.Views.Redraw();
    }

    private string CurveName(Guid id) =>
        _document.Objects.FindId(id) is { Name.Length: > 0 } named ? $"“{named.Name}”" : "closed curve";

    private Point2[] SampleClosed(Curve curve) => CurveOutline.Points(curve, _document.ModelUnitSystem);

    private void PickYard()
    {
        if (Searching || CommandGuard.Busy(message => _areaStatus.Text = message)) return;
        _areaStatus.Text = "Click one closed curve around the planning yard in the viewport. Open curves cannot be picked. Esc keeps the current yard.";
        var picked = JourneyEditPicks.PickCurves(_document, _sourceId,
            "Select one closed curve around the planning yard", multiple: false, closedOnly: true);
        if (picked is null)
        {
            _areaStatus.Text = "Yard unchanged.";
            return;
        }
        _yardId = picked[0];
        _obstacleIds.Remove(picked[0]);
        ClearSuggestions();
        RefreshSite();
        _areaStatus.Text = "Yard picked. Pick obstacles, check the bay, then search.";
    }

    private void PickObstacles()
    {
        if (Searching || CommandGuard.Busy(message => _areaStatus.Text = message)) return;
        _areaStatus.Text = "Click closed curves around buildings and storage in the viewport, then press Enter. Enter with nothing selected clears the obstacles; Esc keeps them.";
        var picked = JourneyEditPicks.PickCurves(_document, _sourceId,
            "Select closed building and storage outlines", multiple: true, closedOnly: true);
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

    public void ClearSuggestions()
    {
        _searchResult = null;
        _suggestions.Items.Clear();
        _suggestions.Enabled = false;
        _labels.SearchCandidate = null;
        _baselineCurve = null;
        _baselineDefinition = null;
        _appliedCurve = null;
    }

    /// <summary>Stops any search; nothing it finds afterwards is applied.</summary>
    public void Close()
    {
        _closed = true;
        _searchCancellation?.Cancel();
    }

    /// <summary>
    /// Abandons a running search: it is cancelled and forgotten, so whatever it returns is discarded
    /// rather than filled into a panel that is now hidden. The Stop button cancels without
    /// forgetting, so a stopped search still shows what it found.
    /// </summary>
    private void StopSearch()
    {
        _searchCancellation?.Cancel();
        _searchCancellation = null;
        if (!Searching) return;
        Searching = false;
        _searchButton.Text = SearchText;
        _areaStatus.Text = string.Empty;
    }

    private void StartAreaSearch()
    {
        if (Searching) return;
        if (_host.Read() is not { Manoeuvre: { } stored } definition ||
            _document.Objects.FindId(_sourceId)?.Geometry is not Curve sourceCurve)
        {
            _areaStatus.Text = "The saved road could not be read. Close Edit Road and open it again.";
            return;
        }
        if (!RhinoRoadCatalog.TryRoadVehicle(definition, out var vehicle, out var vehicleError))
        {
            _areaStatus.Text = vehicleError;
            return;
        }
        if (!AreaSearchSupported(vehicle))
        {
            _areaStatus.Text = $"{vehicle.Name} cannot be squared onto a bay in reverse, so the clear-area search cannot hold the bay's direction for it.";
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
            _areaStatus.Text = "Set the bay first (step 2): the road must end reversing into a bay with a direction.";
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
        Searching = true;
        _liveArea.Text = string.Empty;
        _searchButton.Text = "Stop search";
        _areaStatus.Text = "Searching drivable manoeuvres and measuring their clear yard area…";
        _searchCancellation?.Cancel();
        var cancellation = _searchCancellation = new CancellationTokenSource();
        var mode = vehicle.DrivingModes[definition.ModeId];
        var seconds = _searchTime.SelectedKey == "120" ? 120 : 30;
        _ = Task.Run(() => ManoeuvringAreaSearch.Find(vehicle, mode, journey, first,
                yard, obstacles, definition.ClearanceMetres, TimeSpan.FromSeconds(seconds), cancellation.Token))
            .ContinueWith(work =>
            {
                // Observed before the early returns below: a search abandoned by Close, or by the
                // vehicle becoming unsupported, must not leave its fault unobserved.
                _ = work.Exception;
                Application.Instance.AsyncInvoke(() => SearchFinished(work, cancellation));
            }, TaskScheduler.Default);
    }

    private void SearchFinished(Task<ManoeuvringAreaSearch.Result> work, CancellationTokenSource cancellation)
    {
        if (_closed || cancellation != _searchCancellation) return;
        Searching = false;
        _searchButton.Text = SearchText;
        if (!work.IsCompletedSuccessfully)
        {
            _areaStatus.Text = $"Search stopped: {work.Exception?.GetBaseException().Message ?? "cancelled"}";
            UpdateReadout();
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
        _measured = null;
        UpdateReadout();
        _document.Views.Redraw();
    }

    private static string PreviewText(ManoeuvringAreaSearch.Candidate candidate) =>
        $"Preview {candidate.ClearAreaSquareMetres:0.0} m²; bay error {candidate.PositionErrorMetres:0.00} m / {candidate.HeadingErrorDegrees:0.0}°; {candidate.Corrections} correction(s). Orange dot marks the tightest location. Save keeps it, or adjust its controls first.";

    private bool SourceMatchesBaseline() =>
        _baselineCurve is not null && _baselineDefinition is not null &&
        _document.Objects.FindId(_sourceId)?.Geometry is Curve now && GeometryBase.GeometryEquals(now, _baselineCurve) &&
        _host.Read() is { } definition &&
        AccessDefinitionSerializer.Serialize(definition) == AccessDefinitionSerializer.Serialize(_baselineDefinition);

    private void SelectSuggestion(string? key, string prefix = "")
    {
        if (_searchResult is null || _baselineCurve is null || _baselineDefinition is null) return;
        if (key == CurrentRouteKey)
        {
            if (!_host.WriteDraft((Curve)_baselineCurve.Duplicate(), _baselineDefinition, "RhinoRoad restore route before search"))
            {
                _areaStatus.Text = "That route could not be applied; the previous edit remains.";
                return;
            }
            _labels.SearchCandidate = null;
            _appliedCurve = null;
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
        if (!_host.WriteDraft(_host.ControlLine(suggestion.Manoeuvre), draft, "RhinoRoad apply area-search suggestion"))
        {
            _areaStatus.Text = "That route could not be applied; the previous edit remains.";
            return;
        }
        _labels.SearchCandidate = suggestion;
        _appliedCurve = _document.Objects.FindId(_sourceId)?.Geometry is Curve applied ? (Curve)applied.Duplicate() : null;
        _areaStatus.Text = prefix + $"Suggestion {index + 1} is the draft. " + PreviewText(suggestion);
    }
}
