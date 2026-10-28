namespace RhinoRoad.Core;

/// <summary>
/// What the click-to-drive loop may offer a vehicle. Every such gate is here, and each one asks
/// <see cref="ReverseCapabilities"/> rather than counting trailers, so a vehicle that gains docking
/// there gains it in the pick loop without a hunt through it.
/// </summary>
public static class InteractiveCapabilities
{
    /// <summary>
    /// Whether the PlanReverse option is offered: the planner searches pull-ups and reverse approaches
    /// for any vehicle (see <see cref="ReverseCapabilities.CanPlanReverse"/>), and starts from forward
    /// travel.
    /// </summary>
    public static bool PlanReverseAvailable(VehicleDefinition vehicle, VehicleState state)
    {
        ArgumentNullException.ThrowIfNull(vehicle);
        ArgumentNullException.ThrowIfNull(state);
        return ReverseCapabilities.CanPlanReverse(vehicle) && state.Direction == TravelDirection.Forward;
    }

    /// <summary>
    /// Whether a bay is placed by the rear of the docking body and the direction it backs in, which
    /// is how a site plan dimensions one: the trailer's rear for a combination, the vehicle's own for
    /// a rigid one (see <see cref="ReverseBay"/>). False only for a chain that cannot be squared onto
    /// a bay, where there is no docked rear to speak of and the point stays the last trailer's axle.
    /// </summary>
    public static bool DockingAvailable(VehicleDefinition vehicle)
    {
        ArgumentNullException.ThrowIfNull(vehicle);
        return ReverseCapabilities.CanDock(vehicle);
    }

    /// <summary>
    /// Whether a point picked while travelling this way says where the trailer axle goes rather than
    /// the tractor's. Reversing a combination, the trailer is what is being placed. A rigid vehicle's
    /// point is its rear axle either way, so this stays articulated-only.
    /// </summary>
    public static bool PointPlacesTrailer(VehicleDefinition vehicle, TravelDirection direction)
    {
        ArgumentNullException.ThrowIfNull(vehicle);
        return vehicle.IsArticulated && direction == TravelDirection.Reverse;
    }

    /// <summary>
    /// Whether <paramref name="control"/> is a reverse into a bay that this vehicle is docked onto --
    /// on the point and square to it -- rather than aimed at. Mirrors the dispatch in
    /// <see cref="ManoeuvreReplayService.PlanControl"/>: a Turn and a Finish go the ordinary way.
    /// </summary>
    public static bool Docks(VehicleDefinition vehicle, ManoeuvreControl control)
    {
        ArgumentNullException.ThrowIfNull(vehicle);
        ArgumentNullException.ThrowIfNull(control);
        return control is { Direction: TravelDirection.Reverse, Kind: ManoeuvreControlKind.Aim, ExitHeadingRadians: not null }
            && ReverseCapabilities.CanDock(vehicle);
    }

    /// <summary>
    /// Whether <paramref name="control"/> places the docking body at its point rather than steering
    /// the rear axle towards it: any aimed reverse of a combination, and a rigid vehicle's reverse
    /// into a bay. A shortfall on such a leg is the body failing to get there, not the steering lock.
    /// </summary>
    public static bool PlacesDockingBody(VehicleDefinition vehicle, ManoeuvreControl control)
    {
        ArgumentNullException.ThrowIfNull(vehicle);
        ArgumentNullException.ThrowIfNull(control);
        return control is { Direction: TravelDirection.Reverse, Kind: ManoeuvreControlKind.Aim }
            && (vehicle.IsArticulated || Docks(vehicle, control));
    }
}

/// <summary>
/// The journey being driven click by click: the route so far, the controls that replay it, the undo
/// history, and the modifiers armed for the next click.
/// </summary>
/// <remarks>
/// <para>
/// Planning a leg is <see cref="ManoeuvreReplayService.PlanControl"/>'s job and drawing it is the
/// viewport's; this holds only what the pick loop decides between clicks, so the rules -- which
/// options exclude each other, what a commit splices and what undo restores -- can be tested without
/// a viewport.
/// </para>
/// <para>
/// The armed modifiers are spent by the caller rather than by <see cref="Commit"/>, because the ways
/// into a commit differ in what they spend: a typed sweep leaves Stop armed, a plan-reverse review
/// spends it even when the proposal is declined.
/// </para>
/// </remarks>
public sealed class InteractiveJourney
{
    private sealed record HistoryEntry(
        VehicleState State,
        RouteSample[] Route,
        ManoeuvreControl[] Controls,
        int LegStartIndex);

    private readonly List<RouteSample> _route;
    private readonly List<ManoeuvreControl> _controls = [];
    private readonly Stack<HistoryEntry> _history = new();
    private bool _planReverseNext;

    public InteractiveJourney(VehicleDefinition vehicle, VehicleState start)
    {
        ArgumentNullException.ThrowIfNull(vehicle);
        ArgumentNullException.ThrowIfNull(start);
        Vehicle = vehicle;
        StartState = start;
        State = start;
        _route = [ManoeuvreReplayService.StateSample(start, vehicle)];
    }

    public VehicleDefinition Vehicle { get; }

    public VehicleState StartState { get; }

    public VehicleState State { get; private set; }

    public IReadOnlyList<RouteSample> Route => _route;

    public IReadOnlyList<ManoeuvreControl> Controls => _controls;

    /// <summary>
    /// How far back an ease-out may rewind: never past the start of the leg being driven, so the
    /// corner in hand can be reshaped but an earlier decision cannot be silently undone.
    /// </summary>
    public int LegStartIndex { get; private set; }

    /// <summary>Clicks committed and not undone. A planned reverse is one, however many controls it holds.</summary>
    public int LegCount => _history.Count;

    /// <summary>Whether there is a route to keep: at least one leg that went somewhere.</summary>
    public bool HasRoute => _route.Count >= 2;

    /// <summary>
    /// Set once a Finish leg is committed. Finish says how the journey ends, so nothing is driven
    /// after it: carrying on would leave a Finish part-way through a route, which neither replay nor
    /// the edit palette treats as anything but the last control.
    /// </summary>
    public bool IsComplete { get; private set; }

    /// <summary>The next aimed click ends the journey travelling in the direction picked.</summary>
    public bool Finishing { get; private set; }

    /// <summary>
    /// Latched by the Turn option; Ctrl holds it momentarily. Either puts the next click into a
    /// locked turn, and they are one flag in effect so the two routes into it cannot disagree.
    /// </summary>
    public bool LockedTurn { get; private set; }

    /// <summary>
    /// Armed by Stop and spent on the next click, because a stop is a thing that happens once rather
    /// than a state to be in. A change of direction is a stop already and needs no arming -- this is
    /// for the standstill a route would not otherwise have had.
    /// </summary>
    public bool StopFirst { get; private set; }

    /// <summary>
    /// Armed by Direction and spent on the next click. The click places the point, and the pick then
    /// continues with that point fixed while the cursor swings the direction about it, so the leg is
    /// previewed against the direction being chosen rather than chosen blind.
    /// </summary>
    public bool PointExitNext { get; private set; }

    /// <summary>
    /// Latched by Speed: the speed the legs from here on are driven at, put on every control they
    /// make, until it is changed again. Null is the mode's speed. Unlike Stop it is a state rather
    /// than an event -- a driver who slows to walking pace for a shunt stays at it until they speed
    /// up -- and undo leaves it alone, as it leaves Turn.
    /// </summary>
    public double? SpeedKilometresPerHour { get; private set; }

    /// <summary>Armed by PlanReverse, and only in effect while the vehicle can still plan one.</summary>
    public bool PlanReverseNext => _planReverseNext && InteractiveCapabilities.PlanReverseAvailable(Vehicle, State);

    /// <summary>
    /// Which way the last previewed turn swung, so a sweep typed on the command line -- where there is
    /// no cursor to read a side from -- goes the way the cursor was last pointing.
    /// </summary>
    public double LastSweepSign { get; private set; } = 1.0;

    /// <summary>
    /// Whether this click places the rear of the docking body -- the trailer, or a rigid vehicle
    /// itself -- at a bay. That is the point a site plan dimensions once a direction follows; without
    /// one there is no rear to speak of, and the pick stays the docking axle.
    /// </summary>
    public bool PickingBayRear => InteractiveCapabilities.DockingAvailable(Vehicle)
        && (PlanReverseNext || (PointExitNext && State.Direction == TravelDirection.Reverse));

    /// <summary>The kind of control the next click makes.</summary>
    public ManoeuvreControlKind KindFor(bool locked) =>
        locked ? ManoeuvreControlKind.Turn
        : Finishing ? ManoeuvreControlKind.Finish
        : ManoeuvreControlKind.Aim;

    /// <summary>Changes gear. The cusp is a leg boundary, so no ease-out reaches back through it.</summary>
    public void ToggleDirection()
    {
        _planReverseNext = false;
        State = State with
        {
            Direction = State.Direction == TravelDirection.Forward ? TravelDirection.Reverse : TravelDirection.Forward
        };
        LegStartIndex = _route.Count - 1;
    }

    public void ToggleFinish()
    {
        Finishing = !Finishing;
        if (Finishing) LockedTurn = false;
    }

    public void ToggleTurn()
    {
        LockedTurn = !LockedTurn;
        if (LockedTurn) Finishing = false;
    }

    public void ToggleStop() => StopFirst = !StopFirst;

    public void TogglePointExit() => PointExitNext = !PointExitNext;

    public void TogglePlanReverse()
    {
        _planReverseNext = !_planReverseNext;
        if (!_planReverseNext) return;
        LockedTurn = false;
        Finishing = false;
    }

    /// <summary>
    /// Sets the speed for the legs that follow; null returns them to the mode's speed. A speed at or
    /// above the mode's is the mode's, and is kept as null so the saved control says nothing it does
    /// not mean. Returns the speed that will be driven at.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The speed is not a positive number.</exception>
    public double SetSpeed(DrivingModeDefinition mode, double? kilometresPerHour)
    {
        ArgumentNullException.ThrowIfNull(mode);
        var driven = mode.LegSpeedKilometresPerHour(kilometresPerHour);
        SpeedKilometresPerHour = driven < mode.SpeedKilometresPerHour ? driven : null;
        return driven;
    }

    public void SpendStop() => StopFirst = false;

    public void SpendPointExit() => PointExitNext = false;

    public void SpendPlanReverse() => _planReverseNext = false;

    public void RecordSweepSide(double sweepRadians)
    {
        if (Math.Abs(sweepRadians) > 1e-9) LastSweepSign = Math.Sign(sweepRadians);
    }

    /// <summary>
    /// Splices a planned leg onto the route: the tail the ease-out rewound is given back, the leg's
    /// samples follow, and the vehicle is where the leg left it.
    /// </summary>
    /// <remarks>
    /// When the last control seals its leg (<see cref="ManoeuvreControl.SealsLeg"/>, as a planned
    /// reverse's does) the next leg may not rewind into it. The seal is read from the control rather
    /// than passed in, so the boundary drawn here and the one replay of the saved controls finds are
    /// one and the same.
    /// </remarks>
    public void Commit(PlannedManoeuvreLeg leg, IReadOnlyList<ManoeuvreControl> controls)
    {
        ArgumentNullException.ThrowIfNull(leg);
        ArgumentNullException.ThrowIfNull(controls);
        _history.Push(new HistoryEntry(State, _route.ToArray(), _controls.ToArray(), LegStartIndex));
        if (leg.FromIndex < _route.Count - 1)
        {
            _route.RemoveRange(leg.FromIndex + 1, _route.Count - leg.FromIndex - 1);
        }

        LegStartIndex = _route.Count - 1;
        _route.AddRange(leg.Samples.Skip(1));
        if (controls.Count > 0 && controls[^1].SealsLeg) LegStartIndex = _route.Count - 1;
        State = leg.EndState;
        _controls.AddRange(controls);
        if (controls.Any(control => control.Kind == ManoeuvreControlKind.Finish))
        {
            IsComplete = true;
            Finishing = false;
        }
    }

    /// <summary>Takes back the last commit. Returns false when there is nothing to take back.</summary>
    public bool Undo()
    {
        if (_history.Count == 0) return false;
        var entry = _history.Pop();
        State = entry.State;
        LegStartIndex = entry.LegStartIndex;
        _route.Clear();
        _route.AddRange(entry.Route);
        _controls.Clear();
        _controls.AddRange(entry.Controls);
        IsComplete = false;
        return true;
    }

    /// <summary>The saved form of the journey, which replays to exactly <see cref="Route"/>.</summary>
    public ManoeuvreDefinition Definition() => new(
        ManoeuvreDefinition.CurrentSchemaVersion,
        StartState.RearAxleCentreMetres,
        StartState.VehicleHeadingRadians,
        StartState.Direction,
        _controls.ToArray());
}
