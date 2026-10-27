using System.Drawing;
using Rhino;
using Rhino.Commands;
using Rhino.Geometry;
using Rhino.Input;
using Rhino.Input.Custom;
using RhinoRoad.Core;
using static RhinoRoad.Rhino.Services.InteractiveRouteDrawing;

namespace RhinoRoad.Rhino.Services;

/// <summary>
/// Drives the vehicle leg by leg, each leg starting from the state the vehicle is actually in.
/// </summary>
/// <remarks>
/// <para>
/// The leg driven by a click is an arc through the picked point, which ends the vehicle turned by
/// twice the bearing of that point. On its own that is an overshoot to be corrected, and correcting
/// it after the fact is what puts an S through the exit of every corner.
/// </para>
/// <para>
/// So it is not corrected after the fact. The <em>next</em> point picked says which way the vehicle
/// should be travelling when it leaves the corner it is in, and that is enough to know when the
/// wheel should have started coming back. The route is rewound to that moment and the exit re-driven
/// as one continuous ease-out, the way a driver opens the wheel through the second half of a bend
/// rather than arriving at the exit still turning and having to unwind against it.
/// </para>
/// <para>
/// The decisions between clicks -- armed modifiers, commit and undo -- belong to
/// <see cref="InteractiveJourney"/>, and the drawing to <see cref="InteractiveRouteDrawing"/>; this
/// runs the Rhino picks and connects the two.
/// </para>
/// </remarks>
internal sealed class InteractiveRouteBuilder
{
    private enum EscapeChoice
    {
        Keep,
        Discard,
        Continue
    }

    private readonly RhinoDoc _document;
    private readonly VehicleDefinition _vehicle;
    private readonly DrivingModeDefinition _mode;
    private readonly double _clearanceMetres;
    private readonly SiteConstraints _site;
    private readonly UnitSystem _units;
    private readonly double _metresPerModelUnit;
    private readonly double _turnRangeMetres;
    private readonly InteractiveJourney _journey;

    private CommittedDrawing _committed;
    private int _committedLegCount = -1;

    private InteractiveRouteBuilder(
        RhinoDoc document,
        VehicleDefinition vehicle,
        DrivingModeDefinition mode,
        double clearanceMetres,
        SiteConstraints site,
        VehicleState start)
    {
        _document = document;
        _vehicle = vehicle;
        _mode = mode;
        _clearanceMetres = clearanceMetres;
        _site = site;
        _units = document.ModelUnitSystem;
        _metresPerModelUnit = RhinoMath.UnitScale(_units, UnitSystem.Meters);
        _turnRangeMetres = Math.Max(
            2.0 * TurningGeometryCalculator.AtFullLock(vehicle, mode).RearAxleRadiusMetres, 5.0);
        _journey = new InteractiveJourney(vehicle, start);
        _committed = InteractiveRouteDrawing.Committed(_journey.Route, vehicle, _units);
    }

    public static Result TryBuild(
        RhinoDoc document,
        VehicleDefinition vehicle,
        DrivingModeDefinition mode,
        TravelDirection initialDirection,
        double clearanceMetres,
        SiteConstraints site,
        out IReadOnlyList<RouteSample> samples,
        out ManoeuvreDefinition? manoeuvre,
        out Curve? controlCurve)
    {
        samples = [];
        manoeuvre = null;
        controlCurve = null;

        var start = InteractiveRouteStart.Pick(document, initialDirection, out var startResult);
        if (start is null) return startResult;

        // The modifiers are said once here rather than in every prompt, which they pushed past 200
        // characters; the viewport legend repeats the keys for as long as the pick runs.
        RhinoApp.WriteLine(
            "Driving: hold Ctrl for a full-lock turn, Shift to square the exit to ortho. Stop starts the next "
            + "leg from a standstill, Direction swings the exit after placing the point, Speed slows the legs that "
            + "follow, Undo takes a leg back. "
            + "Enter when done; Esc asks before discarding the legs driven.");

        var builder = new InteractiveRouteBuilder(document, vehicle, mode, clearanceMetres, site, start);
        var built = builder.Drive();
        if (built != Result.Success) return built;

        var journey = builder._journey;
        samples = journey.Route.ToArray();
        manoeuvre = journey.Definition();
        controlCurve = new PolylineCurve(
            new[] { manoeuvre.StartPositionMetres }
                .Concat(manoeuvre.Controls.Select(control => control.PositionMetres))
                .Select(point => ToModelPoint(point, document.ModelUnitSystem)));
        return Result.Success;
    }

    /// <summary>The pick loop: one click, option or typed sweep per pass, until the journey is done.</summary>
    private Result Drive()
    {
        while (!_journey.IsComplete)
        {
            RefreshCommitted();

            // A snapshot, because a background plan may still be reading it after this pass's click
            // has changed the journey.
            var route = _journey.Route.ToArray();
            var state = _journey.State;
            var legStartIndex = _journey.LegStartIndex;
            var sweepPreview = new JourneySweepPreview(route, _vehicle, _clearanceMetres, _units, _site);
            LegPreview Plan(ManoeuvreControl control) => PlanPreview(route, legStartIndex, state, sweepPreview, control);

            // Ctrl held during the pick. Rhino normally spends Ctrl on elevator mode, which is
            // meaningless for a plan-view manoeuvre, so it is given up below and the key is free.
            var controlKeyDown = false;
            var shiftKeyDown = false;
            using var getter = new GetPoint();
            using var preview = new LatestPreview<ManoeuvreControl, LegPreview>(_document, Plan);
            getter.SetCommandPrompt(Prompt(state));
            getter.AcceptNothing(true);

            // Snapping, and ortho taken over. The base point is the vehicle, which is what a
            // designer expects to measure from and what Rhino needs for its distance readout and its
            // from-point snaps. Rhino's own ortho is switched off deliberately: it constrains the
            // direction to the picked point, and the vehicle leaves on twice that bearing, so its
            // rubber band points somewhere the vehicle will not go. Shift keeps its usual meaning —
            // it toggles ortho rather than only enabling it — but the constraint it applies is
            // OrthoAimConstraint, which snaps where the leg ends up pointing. Object snap is left
            // alone and Alt stays Rhino's snap suppressor; only elevator mode is taken, freeing Ctrl.
            getter.SetBasePoint(ToModelPoint(state.RearAxleCentreMetres, _units), true);
            getter.PermitObjectSnap(true);
            getter.PermitOrthoSnap(false);
            getter.PermitElevatorMode(0);

            // A sweep in degrees, for the check that has to land on a round number rather than near
            // one. Negative is accepted: it turns the other way.
            getter.AcceptNumber(true, true);

            var reverseOption = getter.AddOption("Reverse");
            var undoOption = getter.AddOption("Undo");
            var turnOption = getter.AddOption(_journey.LockedTurn ? "Aim" : "Turn");
            var finishOption = getter.AddOption(_journey.Finishing ? "Aim" : "Finish");
            var stopOption = getter.AddOption(_journey.StopFirst ? "Rolling" : "Stop");
            var directionOption = getter.AddOption("Direction");
            var speedOption = getter.AddOption("Speed");
            int? planReverseOption = InteractiveCapabilities.PlanReverseAvailable(_vehicle, state)
                ? getter.AddOption(_journey.PlanReverseNext ? "Manual" : "PlanReverse")
                : null;
            getter.MouseMove += (_, args) =>
            {
                controlKeyDown = args.ControlKeyDown;
                shiftKeyDown = args.ShiftKeyDown;
            };
            getter.MouseDown += (_, args) =>
            {
                controlKeyDown = args.ControlKeyDown;
                shiftKeyDown = args.ShiftKeyDown;
            };
            getter.DynamicDraw += (_, args) =>
            {
                var messages = new ViewportMessages(args.Display);
                try
                {
                    DrawPick(args, messages, route, state, sweepPreview, preview, controlKeyDown, shiftKeyDown);
                }
                finally
                {
                    Legend(messages, showModifiers: true);
                }
            };

            var getResult = getter.Get();
            if (getResult == GetResult.Cancel)
            {
                // One stray Esc after ten careful clicks used to throw the journey away.
                if (_journey.LegCount == 0) return Result.Cancel;
                var choice = AskOnEscape();
                if (choice == EscapeChoice.Discard) return Result.Cancel;
                if (choice == EscapeChoice.Continue) continue;
                if (_journey.HasRoute) break;
                RhinoApp.WriteLine("Create at least one trajectory leg.");
                continue;
            }

            if (getResult == GetResult.Nothing)
            {
                if (!_journey.HasRoute)
                {
                    RhinoApp.WriteLine("Create at least one trajectory leg.");
                    continue;
                }

                break;
            }

            if (getResult == GetResult.Option)
            {
                var option = getter.OptionIndex();
                if (option == reverseOption) ToggleDirection();
                else if (option == finishOption) _journey.ToggleFinish();
                else if (option == turnOption) _journey.ToggleTurn();
                else if (option == stopOption) ToggleStop();
                else if (option == directionOption) TogglePointExit();
                else if (option == speedOption) ChooseSpeed();
                else if (planReverseOption is not null && option == planReverseOption) TogglePlanReverse();
                else if (option == undoOption && !_journey.Undo()) RhinoApp.WriteLine("Nothing to undo.");
                continue;
            }

            // A sweep typed rather than clicked. It is stored as the control point that asks for
            // exactly that sweep, so a typed turn and a clicked one are the same thing on disk and
            // replay through one path.
            if (getResult == GetResult.Number)
            {
                if (!_journey.LockedTurn && !controlKeyDown)
                {
                    RhinoApp.WriteLine("A number sets the sweep of a full-lock turn; choose Turn first.");
                    continue;
                }

                var typedDegrees = getter.Number();
                if (Math.Abs(typedDegrees) < 1e-6 || Math.Abs(typedDegrees) > 360.0)
                {
                    RhinoApp.WriteLine("Enter a sweep between -360 and 360 degrees.");
                    continue;
                }

                var typedSweep = typedDegrees * Math.PI / 180.0;
                if (typedDegrees > 0.0) typedSweep *= _journey.LastSweepSign;
                Commit(new ManoeuvreControl(
                    LockedTurnGenerator.CursorForSweep(state, typedSweep, _turnRangeMetres),
                    state.Direction,
                    ManoeuvreControlKind.Turn,
                    SpeedKilometresPerHour: _journey.SpeedKilometresPerHour));
                continue;
            }

            if (getResult != GetResult.Point) continue;
            CommitPick(getter, route, state, sweepPreview, shiftKeyDown, controlKeyDown);
        }

        return Result.Success;
    }

    /// <summary>
    /// The main pick's frame: the aim the cursor asks for, planned in the frame or in the background,
    /// and drawn with what it would do to the route.
    /// </summary>
    private void DrawPick(
        GetPointDrawEventArgs args,
        ViewportMessages messages,
        IReadOnlyList<RouteSample> route,
        VehicleState state,
        JourneySweepPreview sweepPreview,
        LatestPreview<ManoeuvreControl, LegPreview> preview,
        bool controlKeyDown,
        bool shiftKeyDown)
    {
        var locked = _journey.LockedTurn || controlKeyDown;
        var squared = OrthoInEffect(shiftKeyDown);

        // Ortho angles are counted from the construction plane's X axis, and the plane that matters
        // is the one belonging to the viewport the cursor is actually in — not the active view, which
        // is a different thing the moment a pick is made in a viewport without clicking into it first.
        var orthoReference = ConstructionPlaneHeading(args.Viewport);
        var cursor = Cursor(args.CurrentPoint, state, squared && locked, _units, orthoReference);
        var exitHeading = squared
            ? SnappedExitHeading(args.CurrentPoint, state, _units, orthoReference)
            : null;
        if (_journey.PickingBayRear)
        {
            // Where the body's rear stops says nothing yet about where its axle goes, so no leg is
            // planned until the direction has been swung.
            var rear = ReverseWording.BayRear(_vehicle);
            messages.Add(_journey.PlanReverseNext
                    ? $"Pick {rear} at the bay; its direction comes next, then the planner searches approaches"
                    : $"Pick {rear} at the bay; its reversing direction comes next",
                Color.DarkBlue);
            return;
        }
        if (_journey.PlanReverseNext)
        {
            // A chain that cannot be docked is planned to a point, which is its last trailer's axle;
            // previewing a forward leg to that point would show something the planner will not do.
            messages.Add($"Pick where the {ReverseWording.DockingAxle(_vehicle)} should stop; then the planner searches approaches", Color.DarkBlue);
            return;
        }

        var control = new ManoeuvreControl(
            new Point3(cursor.X, cursor.Y, args.CurrentPoint.Z * _metresPerModelUnit),
            state.Direction,
            _journey.KindFor(locked),
            exitHeading,
            SpeedKilometresPerHour: _journey.SpeedKilometresPerHour);

        // The snapped aim, drawn from the vehicle, so it is visible that the pick has been squared up
        // and to what. Rhino's own ortho band is off here and would have pointed the wrong way anyway.
        if (squared)
        {
            args.Display.DrawDottedLine(
                ToModelPoint(state.RearAxleCentreMetres, _units),
                ToModelPoint(new Point3(cursor.X, cursor.Y, state.RearAxleCentreMetres.Z), _units),
                Color.DarkSeaGreen);
            // What it snapped to as well as where it landed. Rhino shows the ortho angle nowhere
            // during a pick, and a construction plane rotated to suit a site — which is exactly why
            // ortho is counted from the plane and not the world — otherwise looks like the
            // constraint misbehaving rather than obeying the plane it is on.
            messages.Add(
                $"Ortho: leaving on {Degrees(exitHeading ?? 0.0):0.#} deg "
                + $"(every {OrthoStepDegrees():0.#} deg from CPlane {Degrees(orthoReference):0.#} deg)",
                Color.DarkSeaGreen);
        }
        if (locked)
        {
            var sweep = LockedTurnGenerator.SweepFromCursor(state, cursor);
            _journey.RecordSweepSide(sweep);
            DrawLockCircles(args.Display, _vehicle, _mode, state, _units);
            messages.Add(
                $"Turn {Math.Abs(sweep) * 180.0 / Math.PI:0.#} deg {(sweep >= 0.0 ? "left" : "right")} at full lock",
                Color.MediumVioletRed);
        }

        DrawPreview(args.Display, messages, route, sweepPreview, preview, control);
    }

    /// <summary>Asks the preview for a control and draws whatever it has, saying when it is behind.</summary>
    private void DrawPreview(
        global::Rhino.Display.DisplayPipeline display,
        ViewportMessages messages,
        IReadOnlyList<RouteSample> route,
        JourneySweepPreview sweepPreview,
        LatestPreview<ManoeuvreControl, LegPreview> preview,
        ManoeuvreControl control)
    {
        var (result, current) = preview.Get(control, PlansSlowly(control));
        if (preview.Error is { } error) messages.Add($"Preview unavailable: {error}", Color.OrangeRed);
        else if (!current) messages.Add(result is null ? "Planning…" : "Updating… the faded preview is the last position planned", Color.DimGray);
        if (result is null) return;
        DrawLeg(display, messages, _vehicle, route, _committed, sweepPreview, result, current, _units);
    }

    /// <summary>
    /// Whether planning this control is known to be too slow for a frame: a docking reverse tries
    /// several correction strengths, whatever the vehicle. Anything else is timed, and moves to the
    /// background if it turns out slow too.
    /// </summary>
    private bool PlansSlowly(ManoeuvreControl control) => InteractiveCapabilities.Docks(_vehicle, control);

    /// <summary>
    /// Plans a control and everything drawn from it. Pure apart from the sweep cache, which
    /// serialises itself, so it is safe on a background thread against a route snapshot.
    /// </summary>
    private LegPreview PlanPreview(
        IReadOnlyList<RouteSample> route,
        int legStartIndex,
        VehicleState state,
        JourneySweepPreview sweepPreview,
        ManoeuvreControl control)
    {
        var leg = ManoeuvreReplayService.PlanControl(_vehicle, _mode, route, legStartIndex, state, control);
        return new LegPreview(leg, sweepPreview.Build(leg), ArticulationTrace.At(_vehicle, route, leg), control);
    }

    /// <summary>
    /// Commits a click. It is re-planned from the committed Rhino point rather than taken from the
    /// last preview, which makes the stored control and the baked samples an exact pair even if
    /// snapping adjusts the click after the final dynamic-draw event or the preview was still behind.
    /// </summary>
    private void CommitPick(
        GetPoint getter,
        IReadOnlyList<RouteSample> route,
        VehicleState state,
        JourneySweepPreview sweepPreview,
        bool shiftKeyDown,
        bool controlKeyDown)
    {
        var picked = getter.Point();
        var pickedSquared = OrthoInEffect(shiftKeyDown);
        var pickedLocked = _journey.LockedTurn || controlKeyDown;
        var viewport = getter.View()?.ActiveViewport ?? _document.Views.ActiveView?.ActiveViewport;
        var pickedReference = ConstructionPlaneHeading(viewport);
        var pickedCursor = Cursor(picked, state, pickedSquared && pickedLocked, _units, pickedReference);
        var pickedTarget = new Point3(pickedCursor.X, pickedCursor.Y, picked.Z * _metresPerModelUnit);
        var pickedKind = _journey.KindFor(pickedLocked);
        var pickedExit = pickedSquared
            ? SnappedExitHeading(picked, state, _units, pickedReference)
            : null;

        if (_journey.PickingBayRear)
        {
            // The rear of the docking body at the bay -- the trailer's, or a rigid vehicle's own --
            // then the way it backs in; the journey stores the axle that puts the rear there. Plan
            // reverse always needs the direction.
            pickedKind = ManoeuvreControlKind.Aim;
            var swung = SwingExitDirection(pickedTarget, pickedKind, viewport, route, state, sweepPreview, bayRear: true);
            _journey.SpendPointExit();
            if (swung is not double bayHeading) return;
            pickedExit = bayHeading;
            pickedTarget = ReverseBay.AxleFromRear(_vehicle, pickedTarget, bayHeading);
        }
        // A locked turn ends still turning by definition, so there is no exit direction to swing.
        else if (_journey.PointExitNext && !pickedLocked)
        {
            var swung = SwingExitDirection(pickedTarget, pickedKind, viewport, route, state, sweepPreview);
            _journey.SpendPointExit();
            if (swung is not double exit) return;
            pickedExit = exit;
        }

        if (_journey.PlanReverseNext)
        {
            ReviewPlannedReverse(route, state, sweepPreview, pickedTarget, pickedExit);
            _journey.SpendPlanReverse();
            _journey.SpendStop();
            return;
        }

        Commit(new ManoeuvreControl(
            pickedTarget, state.Direction, pickedKind, pickedExit, FromStandstill: _journey.StopFirst,
            SpeedKilometresPerHour: _journey.SpeedKilometresPerHour));
        _journey.SpendStop();
    }

    /// <summary>
    /// Second stage of the pick: the point is placed, and the direction swings about it. Null when
    /// the swing was abandoned, which abandons the click rather than committing a leg whose
    /// direction was never settled.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The direction is swung about the point just placed rather than picked from the vehicle,
    /// because it is a property of where the leg ends. The picked point is a real Rhino pick from
    /// that base point, so Rhino's own ortho (F8 or Shift, at its ortho angle and on the CPlane) and
    /// object snaps constrain it exactly as they would drawing a line, and the rubber band shows the
    /// direction that will be used. Some directions are on no step -- a skewed arm, a bay set at
    /// whatever angle the building is -- and those can be snapped to geometry or typed.
    /// </para>
    /// <para>
    /// For a bay the point placed is the rear of the docking body -- the trailer, or a rigid vehicle
    /// itself -- and the axle the journey stores follows from it and the direction. The planned
    /// reverse is not previewed while swinging: the planner tries dozens of approaches, and running
    /// it on every mouse move made the pick lag behind the cursor. The docked body is drawn instead,
    /// and the proposal is reviewed once the direction is picked. A plain docking reverse is
    /// previewed, in the background when it is slow.
    /// </para>
    /// </remarks>
    private double? SwingExitDirection(
        Point3 targetMetres,
        ManoeuvreControlKind kind,
        global::Rhino.Display.RhinoViewport? viewport,
        IReadOnlyList<RouteSample> route,
        VehicleState state,
        JourneySweepPreview sweepPreview,
        bool bayRear = false)
    {
        var scale = RhinoMath.UnitScale(UnitSystem.Meters, _units);
        var pivot = new Point3d(targetMetres.X * scale, targetMetres.Y * scale, targetMetres.Z * scale);
        var reference = ConstructionPlaneHeading(viewport);
        var planReverse = _journey.PlanReverseNext;
        var stopFirst = _journey.StopFirst;
        var speed = _journey.SpeedKilometresPerHour;
        var legStartIndex = _journey.LegStartIndex;

        using var swing = new GetPoint();
        using var preview = new LatestPreview<ManoeuvreControl, LegPreview>(
            _document, control => PlanPreview(route, legStartIndex, state, sweepPreview, control));
        var body = ReverseWording.DockingBody(_vehicle);
        swing.SetCommandPrompt(bayRear
            ? $"Point the way the {body} reverses into the bay, or type an angle from the CPlane"
            : "Swing the direction to leave along, or type an angle from the CPlane");
        swing.SetBasePoint(pivot, true);
        swing.DrawLineFromPoint(pivot, true);
        swing.AcceptNumber(true, true);
        swing.PermitObjectSnap(true);
        swing.PermitOrthoSnap(true);
        swing.PermitElevatorMode(0);

        swing.DynamicDraw += (_, drawn) =>
        {
            var messages = new ViewportMessages(drawn.Display);
            try
            {
                if (SwungHeading(pivot, drawn.CurrentPoint) is not double exit) return;
                var axleMetres = bayRear ? ReverseBay.AxleFromRear(_vehicle, targetMetres, exit) : targetMetres;
                if (bayRear)
                {
                    DrawDockedBody(drawn.Display, _vehicle, axleMetres, exit, _units);
                    messages.Add(
                        $"The {body} reverses in at {Degrees(exit - reference):0.#} deg from the CPlane; rear at the picked point",
                        Color.MediumPurple);
                }

                // The planner runs once the direction is picked, and a leg previewed here would be
                // the forward travel the vehicle is in, not the reverse it will plan.
                if (planReverse) return;

                DrawPreview(drawn.Display, messages, route, sweepPreview, preview,
                    new ManoeuvreControl(axleMetres, state.Direction, kind, exit, FromStandstill: stopFirst,
                        SpeedKilometresPerHour: speed));
            }
            finally
            {
                Legend(messages, showModifiers: false);
            }
        };

        var swingResult = swing.Get();
        if (swingResult == GetResult.Number)
        {
            return Geometry2D.NormalizeAngle(reference + (swing.Number() * Math.PI / 180.0));
        }

        if (swingResult != GetResult.Point) return null;
        if (SwungHeading(pivot, swing.Point()) is not double picked)
        {
            RhinoApp.WriteLine("That point is on top of the one just placed; swing further out.");
            return null;
        }

        return picked;
    }

    /// <summary>
    /// Runs the reverse planner for a bay and shows its proposal, which is committed only if
    /// accepted.
    /// </summary>
    private void ReviewPlannedReverse(
        IReadOnlyList<RouteSample> route,
        VehicleState state,
        JourneySweepPreview sweepPreview,
        Point3 target,
        double? exitHeading)
    {
        ReversePlanner.Proposal proposal;
        try
        {
            proposal = ReversePlanner.Find(
                _vehicle, _mode, route, _journey.LegStartIndex, state, target, exitHeading,
                _journey.SpeedKilometresPerHour);
        }
        catch (InvalidOperationException error)
        {
            RhinoApp.WriteLine(error.Message);
            return;
        }

        // Planned once, not per frame: the review's cursor changes nothing about the proposal.
        var reviewed = new LegPreview(proposal.Leg, sweepPreview.Build(proposal.Leg),
            ArticulationTrace.At(_vehicle, route, proposal.Leg), proposal.Controls[^1]);
        using var review = new GetPoint();
        review.SetCommandPrompt(ReverseWording.ProposalSummary(_vehicle, proposal)
            + "; Enter to accept, Esc to choose another destination");
        review.AcceptNothing(true);
        review.PermitObjectSnap(false);
        review.DynamicDraw += (_, args) =>
        {
            var messages = new ViewportMessages(args.Display);
            DrawLeg(args.Display, messages, _vehicle, route, _committed, sweepPreview, reviewed, true, _units);
            Legend(messages, showModifiers: false);
        };
        if (review.Get() != GetResult.Nothing) return;

        _journey.Commit(proposal.Leg, proposal.Controls, sealLeg: true);
        RhinoApp.WriteLine(ReverseWording.ProposalCommitted(_vehicle, proposal));
        ReportCompletion();
    }

    private void Commit(ManoeuvreControl pickedControl)
    {
        var leg = ManoeuvreReplayService.PlanControl(
            _vehicle, _mode, _journey.Route, _journey.LegStartIndex, _journey.State, pickedControl);
        _journey.Commit(leg, [pickedControl]);
        if (leg.RequestedAngleExceeded)
        {
            // Measured where the docking body actually stands, the trailer's axle or a rigid
            // vehicle's rear axle, by the same reckoning the planner judges arrival with.
            var miss = ReverseBay.Miss(_vehicle, _journey.Route, _journey.State,
                pickedControl.PositionMetres.XY, null).PositionMetres;
            RhinoApp.WriteLine(ReverseWording.CommittedShortfall(_vehicle, pickedControl, miss)
                ?? "Requested turn exceeded wheel lock; the committed leg was clamped to the selected driving mode. Hold Ctrl to drive the tightest turn the mode allows.");
        }

        if (leg.AimedBehindTheBeam)
        {
            RhinoApp.WriteLine("That point was behind the vehicle, so the leg stopped at a half turn rather than looping round to it. Reverse to back towards it, or hold Ctrl to turn at full lock.");
        }

        ReportCompletion();
    }

    private void ReportCompletion()
    {
        if (_journey.IsComplete)
            RhinoApp.WriteLine("Journey finished: Finish sets how it ends, so the route is complete.");
    }

    private void ToggleDirection()
    {
        _journey.ToggleDirection();
        RhinoApp.WriteLine($"Travel direction: {_journey.State.Direction}");
        if (ReverseWording.ReverseGuidance(_vehicle, _journey.State.Direction) is { } guidance)
            RhinoApp.WriteLine(guidance);
    }

    private void ToggleStop()
    {
        _journey.ToggleStop();
        RhinoApp.WriteLine(_journey.StopFirst
            ? "The next leg starts from a standstill: the wheel is turned before the vehicle moves."
            : "The next leg starts rolling.");
    }

    private void TogglePointExit()
    {
        _journey.TogglePointExit();
        RhinoApp.WriteLine(_journey.PointExitNext
            ? InteractiveCapabilities.DockingAvailable(_vehicle)
              && (_journey.State.Direction == TravelDirection.Reverse || _journey.PlanReverseNext)
                ? $"Pick {ReverseWording.BayRear(_vehicle)} at the bay, then point the way it reverses in."
                : "Place the point, then swing the direction to leave along."
            : "Exit direction released.");
    }

    /// <summary>
    /// Sets the speed the legs from here on are driven at, until it is changed again. Only the
    /// wheel's slew per metre follows from it, so the lock is the mode's whatever is chosen, and a
    /// speed above the mode's is driven at the mode's own.
    /// </summary>
    private void ChooseSpeed()
    {
        using var ask = new GetOption();
        var modeSpeed = _mode.SpeedKilometresPerHour;
        ask.SetCommandPrompt(
            $"Speed for the legs that follow, now {SpeedText(_journey.SpeedKilometresPerHour)}. "
            + $"ModeSpeed is {modeSpeed:0.#} km/h; Walk 5, Crawl 3, Inch 1, or type km/h");
        ask.AcceptNumber(true, false);
        ask.AcceptNothing(true);
        var modeOption = ask.AddOption("ModeSpeed");
        var presets = new Dictionary<int, double>
        {
            [ask.AddOption("Walk")] = 5.0,
            [ask.AddOption("Crawl")] = 3.0,
            [ask.AddOption("Inch")] = 1.0
        };
        var answer = ask.Get();
        double? requested;
        if (answer == GetResult.Number) requested = ask.Number();
        else if (answer == GetResult.Option && ask.OptionIndex() == modeOption) requested = null;
        else if (answer == GetResult.Option && presets.TryGetValue(ask.OptionIndex(), out var preset)) requested = preset;
        else return;

        if (requested is double typed && !(double.IsFinite(typed) && typed > 0.0))
        {
            RhinoApp.WriteLine("A speed must be above zero; the speed is unchanged.");
            return;
        }

        var driven = _journey.SetSpeed(_mode, requested);
        if (requested > modeSpeed)
        {
            RhinoApp.WriteLine(
                $"{_mode.Name} is set out for {modeSpeed:0.#} km/h, and a faster leg would turn the wheel in less distance "
                + "than the mode allows, which understates the swept path; the legs that follow are driven at the mode's speed.");
            return;
        }

        RhinoApp.WriteLine(
            $"The legs that follow are driven at {SpeedText(_journey.SpeedKilometresPerHour)}: the wheel goes from centre "
            + $"to lock in {_mode.CentreToLockMetres(driven):0.#} m of travel, and the lock is still the mode's "
            + $"{_mode.MaximumWheelAngleDegrees:0.#} deg.");
    }

    private string SpeedText(double? kilometresPerHour) => kilometresPerHour is double speed
        ? $"{speed:0.#} km/h"
        : $"the mode's {_mode.SpeedKilometresPerHour:0.#} km/h";

    private void TogglePlanReverse()
    {
        _journey.TogglePlanReverse();
        if (!_journey.PlanReverseNext) return;
        RhinoApp.WriteLine((InteractiveCapabilities.DockingAvailable(_vehicle)
                ? $"Pick {ReverseWording.BayRear(_vehicle)} at the bay, then point the way it reverses in."
                : $"Pick where the {ReverseWording.DockingAxle(_vehicle)} should stop; this combination cannot be squared onto a bay, so it is aimed at the point.")
            + " The planner will try forward pull-ups and reverse approaches, then show its proposal before you accept it.");
    }

    /// <summary>
    /// Esc with legs driven. Enter keeps them, as if the journey had been finished there; Esc again
    /// goes back to driving, because cancelling a question should not be the way work is lost.
    /// </summary>
    private EscapeChoice AskOnEscape()
    {
        using var ask = new GetOption();
        var legs = _journey.LegCount;
        ask.SetCommandPrompt($"Keep the {legs} leg{(legs == 1 ? string.Empty : "s")} driven so far? Enter keeps them");
        ask.AcceptNothing(true);
        var keep = ask.AddOption("Keep");
        var discard = ask.AddOption("Discard");
        var carryOn = ask.AddOption("Continue");
        var answer = ask.Get();
        if (answer == GetResult.Nothing) return EscapeChoice.Keep;
        if (answer != GetResult.Option) return EscapeChoice.Continue;
        var option = ask.OptionIndex();
        return option == keep ? EscapeChoice.Keep
            : option == discard ? EscapeChoice.Discard
            : option == carryOn ? EscapeChoice.Continue
            : EscapeChoice.Continue;
    }

    private string Prompt(VehicleState state)
    {
        if (_journey.PickingBayRear)
            return _journey.PlanReverseNext
                ? $"Plan reverse: pick {ReverseWording.BayRear(_vehicle)} at the bay"
                : $"Pick {ReverseWording.BayRear(_vehicle)} at the bay ({state.Direction})";
        if (_journey.PlanReverseNext) return $"Plan reverse: pick where the {ReverseWording.DockingAxle(_vehicle)} should stop";
        var wheel = state.SteeringAngleRadians * 180.0 / Math.PI;
        if (_journey.Finishing) return $"Finish: pick the direction to end up travelling in (wheel {wheel:0.#} deg)";
        if (_journey.LockedTurn) return $"Full-lock turn: pick the side and heading, or type degrees ({state.Direction})";
        var target = InteractiveCapabilities.PointPlacesTrailer(_vehicle, state.Direction) ? "Trailer axle destination" : "Next point";
        return $"{target} ({state.Direction}, wheel {wheel:0.#} deg"
            + (_journey.SpeedKilometresPerHour is double speed ? $", {speed:0.#} km/h" : string.Empty)
            + (_journey.StopFirst ? ", stop first" : string.Empty)
            + (_journey.PointExitNext ? ", then the exit" : string.Empty)
            + "); Enter when done";
    }

    private void RefreshCommitted()
    {
        // Rebuilt on commit and undo only. Every commit or undo changes the leg count, and nothing
        // else changes the route.
        if (_committedLegCount == _journey.LegCount) return;
        _committed = InteractiveRouteDrawing.Committed(_journey.Route, _vehicle, _units);
        _committedLegCount = _journey.LegCount;
    }

    /// <summary>
    /// Whether ortho applies to this pick, following Rhino's convention that Shift toggles the
    /// document's ortho setting rather than only turning it on.
    /// </summary>
    private static bool OrthoInEffect(bool shiftKeyDown) =>
        global::Rhino.ApplicationSettings.ModelAidSettings.Ortho != shiftKeyDown;

    /// <summary>
    /// The picked point in metres, moved onto the ortho direction where the leg cannot carry the
    /// direction itself.
    /// </summary>
    /// <remarks>
    /// Only a locked turn needs this. It has no exit-heading field, but it drives its sweep rather
    /// than aiming at it, so a moved cursor lands it on the direction exactly. An aimed leg keeps the
    /// point where it was clicked, because there the point says how far to run once the heading has
    /// been reached, and the heading itself travels on the control.
    /// </remarks>
    private static Point2 Cursor(
        Point3d picked,
        VehicleState state,
        bool squared,
        UnitSystem modelUnits,
        double orthoReferenceRadians)
    {
        var scale = RhinoMath.UnitScale(modelUnits, UnitSystem.Meters);
        var cursor = new Point2(picked.X * scale, picked.Y * scale);
        if (!squared) return cursor;
        return OrthoAimConstraint.Snap(
            state, cursor, OrthoStepDegrees() * Math.PI / 180.0, orthoReferenceRadians);
    }

    /// <summary>
    /// The ortho increment, in degrees.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Rhino's setting is what a designer expects this to follow, but a value it cannot mean is worse
    /// than following it blindly. Zero — which is what this reads on the machine it was built against
    /// — would snap every direction onto one and silently do nothing at all, which is exactly how it
    /// first presented: the readout moved freely and looked as though ortho were being ignored.
    /// </para>
    /// <para>
    /// The accepted band is 5 to 90 degrees and everything else falls back to right angles. Five is
    /// the floor because an increment below it is not a constraint anyone means — snapping every few
    /// degrees is indistinguishable from not snapping — and because it makes the reading safe against
    /// the one thing the RhinoCommon documentation does not state, which is whether this property is
    /// in degrees or radians. A ninety degree setting read as radians is 1.571, which is inside no
    /// sane band and falls back to ninety; a real setting of 45, 30 or 15 is honoured either way.
    /// </para>
    /// </remarks>
    private static double OrthoStepDegrees()
    {
        var configured = global::Rhino.ApplicationSettings.ModelAidSettings.OrthoAngle;
        return configured is >= 5.0 and <= 90.0 ? configured : 90.0;
    }

    /// <summary>
    /// Direction of a viewport's construction plane X axis in the model plan, which is what Rhino
    /// counts ortho angles from. A tilted plane contributes only its rotation about the vertical,
    /// which is the part a plan-view manoeuvre can act on; a plane edge-on to the plan leaves the
    /// reference at zero rather than producing a meaningless axis.
    /// </summary>
    private static double ConstructionPlaneHeading(global::Rhino.Display.RhinoViewport? viewport)
    {
        if (viewport is null) return 0.0;
        var axis = viewport.GetConstructionPlane().Plane.XAxis;
        return Math.Abs(axis.X) + Math.Abs(axis.Y) <= RhinoMath.ZeroTolerance
            ? 0.0
            : Math.Atan2(axis.Y, axis.X);
    }

    /// <summary>
    /// The ortho direction a pick asks the vehicle to leave on, carried on the control so the leg can
    /// be driven onto it exactly rather than merely aimed at it.
    /// </summary>
    /// <remarks>
    /// Moving the cursor onto the direction only <em>asks</em> for it, and the wheel takes metres to
    /// reach the lock the arc needs: a BUS 12 in mode A squared to a 90 degree departure made 55 of
    /// them over 15 m. Carrying the heading lets the leg ease onto it and then run straight to the
    /// click, which arrives on the axis to a thousandth of a degree and takes however much room the
    /// turn actually needs.
    /// </remarks>
    private static double? SnappedExitHeading(
        Point3d picked,
        VehicleState state,
        UnitSystem modelUnits,
        double orthoReferenceRadians)
    {
        var scale = RhinoMath.UnitScale(modelUnits, UnitSystem.Meters);
        return OrthoAimConstraint.SnapExitHeading(
            state,
            new Point2(picked.X * scale, picked.Y * scale),
            OrthoStepDegrees() * Math.PI / 180.0,
            orthoReferenceRadians);
    }

    /// <summary>
    /// Heading from the placed point to the cursor. Rhino has already applied ortho and object snaps
    /// to the cursor, so this only measures it. Null while the cursor is still on top of the point,
    /// where there is no direction yet.
    /// </summary>
    private static double? SwungHeading(Point3d pivotModel, Point3d cursorModel)
    {
        var deltaX = cursorModel.X - pivotModel.X;
        var deltaY = cursorModel.Y - pivotModel.Y;
        if (Math.Sqrt((deltaX * deltaX) + (deltaY * deltaY)) <= RhinoMath.ZeroTolerance) return null;
        return Geometry2D.NormalizeAngle(Math.Atan2(deltaY, deltaX));
    }

    private static double Degrees(double radians)
    {
        var degrees = radians * 180.0 / Math.PI;
        return degrees < 0.0 ? degrees + 360.0 : degrees;
    }
}
