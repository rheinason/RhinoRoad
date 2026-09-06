using System.Drawing;
using Rhino;
using Rhino.Commands;
using Rhino.Geometry;
using Rhino.Input;
using Rhino.Input.Custom;
using RhinoRoad.Core;

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
/// </remarks>
internal static class InteractiveRouteBuilder
{
    private sealed record HistoryEntry(
        VehicleState State,
        RouteSample[] Route,
        ManoeuvreControl[] Controls,
        int LegStartIndex);

    public static Result TryBuild(
        RhinoDoc document,
        VehicleDefinition vehicle,
        DrivingModeDefinition mode,
        TravelDirection initialDirection,
        double clearanceMetres,
        out IReadOnlyList<RouteSample> samples,
        out ManoeuvreDefinition? manoeuvre,
        out Curve? controlCurve)
    {
        samples = [];
        manoeuvre = null;
        controlCurve = null;
        var metresPerModelUnit = RhinoMath.UnitScale(document.ModelUnitSystem, UnitSystem.Meters);

        using var startGetter = new GetPoint();
        startGetter.SetCommandPrompt("Place vehicle (rear axle centre)");
        if (startGetter.Get() != GetResult.Point) return Result.Cancel;
        var startModel = startGetter.Point();

        using var headingGetter = new GetPoint();
        headingGetter.SetCommandPrompt("Vehicle forward heading");
        headingGetter.SetBasePoint(startModel, true);
        headingGetter.DrawLineFromPoint(startModel, true);
        if (headingGetter.Get() != GetResult.Point) return Result.Cancel;
        var headingVector = headingGetter.Point() - startModel;
        if (Math.Sqrt((headingVector.X * headingVector.X) + (headingVector.Y * headingVector.Y)) <= document.ModelAbsoluteTolerance)
        {
            RhinoApp.WriteLine("Heading point is too close to the start point.");
            return Result.Failure;
        }

        var state = new VehicleState(
            new Point3(startModel.X * metresPerModelUnit, startModel.Y * metresPerModelUnit, startModel.Z * metresPerModelUnit),
            Math.Atan2(headingVector.Y, headingVector.X),
            0.0,
            initialDirection,
            0.0);
        var route = new List<RouteSample> { StateSample(state, vehicle) };
        var controls = new List<ManoeuvreControl>();
        var history = new Stack<HistoryEntry>();

        // How far back an ease-out may rewind: never past the start of the leg being driven, so the
        // corner in hand can be reshaped but an earlier decision cannot be silently undone.
        var legStartIndex = 0;

        var committedPath = CommittedPolyline(route, document.ModelUnitSystem);
        var committedFootprints = CommittedFootprints(route, vehicle, document.ModelUnitSystem);

        var finishing = false;

        // Latched by the Turn option; held momentarily by Ctrl. Either one puts the next click into
        // a locked turn, and they are one flag so the two routes into it cannot disagree.
        var lockedTurn = false;

        // Which way the last previewed turn swung, so a sweep typed on the command line — where
        // there is no cursor to read a side from — goes the way the cursor was last pointing.
        var lastSweepSign = 1.0;
        var turnRangeMetres = Math.Max(
            2.0 * TurningGeometryCalculator.AtFullLock(vehicle, mode).RearAxleRadiusMetres, 5.0);

        while (true)
        {
            var sweepPreview = new JourneySweepPreview(route, vehicle, clearanceMetres, document.ModelUnitSystem);
            PlannedManoeuvreLeg? planned = null;
            ManoeuvreControl? plannedControl = null;
            Point3d? lastPreviewPoint = null;
            var lastPreviewLocked = false;
            var lastPreviewSquared = false;

            // Ctrl held during the pick. Rhino normally spends Ctrl on elevator mode, which is
            // meaningless for a plan-view manoeuvre, so it is given up below and the key is free.
            var controlKeyDown = false;
            var shiftKeyDown = false;
            using var getter = new GetPoint();
            var wheelNow = state.SteeringAngleRadians * 180.0 / Math.PI;
            getter.SetCommandPrompt(finishing
                ? $"Finish: pick the direction to end up travelling in (wheel {wheelNow:0.#} deg); Enter to finish"
                : lockedTurn
                    ? $"Turn at full lock: pick the side and heading to swing to, or type degrees ({state.Direction}); Enter to finish"
                    : $"Pick next point ({state.Direction}, wheel {wheelNow:0.#} deg; Ctrl for full lock, Shift to square the exit); Enter to finish");
            getter.AcceptNothing(true);

            // Snapping, and ortho taken over. The base point is the vehicle, which is what a
            // designer expects to measure from and what Rhino needs for its distance readout and its
            // from-point snaps. Rhino's own ortho is switched off deliberately: it constrains the
            // direction to the picked point, and the vehicle leaves on twice that bearing, so its
            // rubber band points somewhere the vehicle will not go. Shift keeps its usual meaning —
            // it toggles ortho rather than only enabling it — but the constraint it applies is
            // OrthoAimConstraint, which snaps where the leg ends up pointing. Object snap is left
            // alone and Alt stays Rhino's snap suppressor; only elevator mode is taken, freeing Ctrl.
            var basePoint = ToModelPoint(state.RearAxleCentreMetres, document.ModelUnitSystem);
            getter.SetBasePoint(basePoint, true);
            getter.PermitObjectSnap(true);
            getter.PermitOrthoSnap(false);
            getter.PermitElevatorMode(0);


            // A sweep in degrees, for the check that has to land on a round number rather than near
            // one. Negative is accepted: it turns the other way.
            getter.AcceptNumber(true, true);

            var reverseOption = getter.AddOption("Reverse");
            var undoOption = getter.AddOption("Undo");
            var turnOption = getter.AddOption(lockedTurn ? "Aim" : "Turn");
            var finishOption = getter.AddOption(finishing ? "Aim" : "Finish");
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
                var locked = lockedTurn || controlKeyDown;
                var squared = OrthoInEffect(shiftKeyDown);

                // Ortho angles are counted from the construction plane's X axis, and the plane that
                // matters is the one belonging to the viewport the cursor is actually in — not the
                // active view, which is a different thing the moment a pick is made in a viewport
                // without clicking into it first.
                var orthoReference = ConstructionPlaneHeading(args.Viewport);
                var cursor = Cursor(
                    args.CurrentPoint, state, squared && locked, document.ModelUnitSystem, orthoReference);
                var exitHeading = squared
                    ? SnappedExitHeading(args.CurrentPoint, state, document.ModelUnitSystem, orthoReference)
                    : null;
                var control = new ManoeuvreControl(
                    new Point3(cursor.X, cursor.Y, args.CurrentPoint.Z * metresPerModelUnit),
                    state.Direction,
                    locked ? ManoeuvreControlKind.Turn
                        : finishing ? ManoeuvreControlKind.Finish
                        : ManoeuvreControlKind.Aim,
                    exitHeading);
                if (planned is null
                    || lastPreviewPoint != args.CurrentPoint
                    || lastPreviewLocked != locked
                    || lastPreviewSquared != squared
                    || plannedControl != control)
                {
                    planned = ManoeuvreReplayService.PlanControl(vehicle, mode, route, legStartIndex, state, control);
                    lastPreviewPoint = args.CurrentPoint;
                    lastPreviewLocked = locked;
                    lastPreviewSquared = squared;
                    plannedControl = control;
                }

                // The snapped aim, drawn from the vehicle, so it is visible that the pick has been
                // squared up and to what. Rhino's own ortho band is off here and would have pointed
                // the wrong way anyway.
                if (squared)
                {
                    args.Display.DrawDottedLine(
                        ToModelPoint(state.RearAxleCentreMetres, document.ModelUnitSystem),
                        ToModelPoint(new Point3(cursor.X, cursor.Y, state.RearAxleCentreMetres.Z), document.ModelUnitSystem),
                        Color.DarkSeaGreen);
                    // What it snapped to as well as where it landed. Rhino shows the ortho angle
                    // nowhere during a pick, and a construction plane rotated to suit a site — which
                    // is exactly why ortho is counted from the plane and not the world — otherwise
                    // looks like the constraint misbehaving rather than obeying the plane it is on.
                    args.Display.Draw2dText(
                        $"Ortho: leaving on {Degrees(exitHeading ?? 0.0):0.#} deg "
                        + $"(every {OrthoStepDegrees():0.#} deg from CPlane {Degrees(orthoReference):0.#} deg)",
                        Color.DarkSeaGreen, new Point2d(24, 108), false, 16);
                }
                if (locked)
                {
                    var sweep = LockedTurnGenerator.SweepFromCursor(state, cursor);
                    if (Math.Abs(sweep) > 1e-9) lastSweepSign = Math.Sign(sweep);
                    DrawLockCircles(args.Display, vehicle, mode, state, document.ModelUnitSystem);
                    args.Display.Draw2dText(
                        $"Turn {Math.Abs(sweep) * 180.0 / Math.PI:0.#} deg {(sweep >= 0.0 ? "left" : "right")} at full lock",
                        Color.MediumVioletRed, new Point2d(24, 84), false, 16);
                }
                sweepPreview.Draw(args.Display, planned);

                // What the ease-out hands back, drawn faintly, so rewinding into the corner reads as
                // the exit being reshaped rather than as geometry that silently disappeared.
                if (planned.FromIndex < route.Count - 1)
                {
                    var discarded = new Polyline(route.Skip(planned.FromIndex)
                        .Select(sample => ToModelPoint(sample.PositionMetres, document.ModelUnitSystem)));
                    if (discarded.Count > 1) args.Display.DrawDottedPolyline(discarded, Color.Gainsboro, false);
                }

                var kept = Math.Min(planned.FromIndex + 1, committedPath.Count);
                if (kept > 1)
                {
                    args.Display.DrawPolyline(new Polyline(committedPath.Take(kept)), Color.RoyalBlue, 2);
                }

                // Only the stamps the ease-out keeps. Drawing all of them leaves the discarded
                // tail's body outlines sitting in the viewport, so a corner whose overshoot has
                // just been rewound away still looks as though it overshoots.
                foreach (var stamp in committedFootprints)
                {
                    if (stamp.SampleIndex > planned.FromIndex) break;
                    args.Display.DrawPolyline(stamp.Outline, Color.LightSteelBlue, 1);
                }

                var colour = planned.RequestedAngleExceeded ? Color.OrangeRed : Color.CornflowerBlue;
                if (planned.Samples.Count > 1)
                {
                    args.Display.DrawPolyline(
                        new Polyline(planned.Samples.Select(sample => ToModelPoint(sample.PositionMetres, document.ModelUnitSystem))),
                        colour,
                        3);
                }

                DrawVehicle(args.Display, vehicle, planned.EndState, document.ModelUnitSystem,
                    planned.RequestedAngleExceeded ? Color.OrangeRed : Color.DarkBlue);
                DrawExitDirection(args.Display, planned.EndState, document.ModelUnitSystem);
                if (planned.RequestedAngleExceeded)
                    args.Display.Draw2dText("Steering limit reached — adjust your aim", Color.OrangeRed,
                        new Point2d(24, 60), false, 16);
                if (planned.AimedBehindTheBeam)
                    args.Display.Draw2dText(
                        "That point is behind the vehicle — aiming stops at a half turn. Reverse, or hold Ctrl to turn further.",
                        Color.OrangeRed, new Point2d(24, 36), false, 16);
            };

            var getResult = getter.Get();
            if (getResult == GetResult.Cancel) return Result.Cancel;
            if (getResult == GetResult.Nothing)
            {
                if (route.Count < 2)
                {
                    RhinoApp.WriteLine("Create at least one trajectory leg.");
                    continue;
                }

                break;
            }

            if (getResult == GetResult.Option)
            {
                if (getter.OptionIndex() == reverseOption)
                {
                    state = state with
                    {
                        Direction = state.Direction == TravelDirection.Forward ? TravelDirection.Reverse : TravelDirection.Forward
                    };

                    // A cusp. The ease-out must not reach back through it into the other direction.
                    legStartIndex = route.Count - 1;
                    RhinoApp.WriteLine($"Travel direction: {state.Direction}");
                }
                else if (getter.OptionIndex() == finishOption)
                {
                    finishing = !finishing;
                    if (finishing) lockedTurn = false;
                }
                else if (getter.OptionIndex() == turnOption)
                {
                    lockedTurn = !lockedTurn;
                    if (lockedTurn) finishing = false;
                }
                else if (getter.OptionIndex() == undoOption)
                {
                    if (history.Count == 0)
                    {
                        RhinoApp.WriteLine("Nothing to undo.");
                    }
                    else
                    {
                        var entry = history.Pop();
                        state = entry.State;
                        legStartIndex = entry.LegStartIndex;
                        route.Clear();
                        route.AddRange(entry.Route);
                        controls.Clear();
                        controls.AddRange(entry.Controls);
                        committedPath = CommittedPolyline(route, document.ModelUnitSystem);
                        committedFootprints = CommittedFootprints(route, vehicle, document.ModelUnitSystem);
                    }
                }

                continue;
            }

            // A sweep typed rather than clicked. It is stored as the control point that asks for
            // exactly that sweep, so a typed turn and a clicked one are the same thing on disk and
            // replay through one path.
            if (getResult == GetResult.Number)
            {
                if (!lockedTurn && !controlKeyDown)
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
                if (typedDegrees > 0.0) typedSweep *= lastSweepSign;
                Commit(new ManoeuvreControl(
                    LockedTurnGenerator.CursorForSweep(state, typedSweep, turnRangeMetres),
                    state.Direction,
                    ManoeuvreControlKind.Turn));
                continue;
            }

            if (getResult != GetResult.Point) continue;

            // Re-plan from the committed Rhino point rather than trusting the last mouse-move
            // callback. That makes the stored control and the baked samples an exact pair even if
            // snapping adjusts the click after the final dynamic-draw event.
            var picked = getter.Point();
            var pickedSquared = OrthoInEffect(shiftKeyDown);
            var pickedLocked = lockedTurn || controlKeyDown;
            var pickedReference = ConstructionPlaneHeading(
                getter.View()?.ActiveViewport ?? document.Views.ActiveView?.ActiveViewport);
            var pickedCursor = Cursor(
                picked, state, pickedSquared && pickedLocked, document.ModelUnitSystem, pickedReference);
            Commit(new ManoeuvreControl(
                new Point3(pickedCursor.X, pickedCursor.Y, picked.Z * metresPerModelUnit),
                state.Direction,
                pickedLocked ? ManoeuvreControlKind.Turn
                    : finishing ? ManoeuvreControlKind.Finish
                    : ManoeuvreControlKind.Aim,
                pickedSquared
                    ? SnappedExitHeading(picked, state, document.ModelUnitSystem, pickedReference)
                    : null));
            continue;

            void Commit(ManoeuvreControl pickedControl)
            {
                var leg = ManoeuvreReplayService.PlanControl(vehicle, mode, route, legStartIndex, state, pickedControl);
                history.Push(new HistoryEntry(state, route.ToArray(), controls.ToArray(), legStartIndex));
                if (leg.FromIndex < route.Count - 1)
                {
                    route.RemoveRange(leg.FromIndex + 1, route.Count - leg.FromIndex - 1);
                }

                legStartIndex = route.Count - 1;
                route.AddRange(leg.Samples.Skip(1));
                state = leg.EndState;
                controls.Add(pickedControl);
                committedPath = CommittedPolyline(route, document.ModelUnitSystem);
                committedFootprints = CommittedFootprints(route, vehicle, document.ModelUnitSystem);
                if (leg.RequestedAngleExceeded)
                {
                    RhinoApp.WriteLine("Requested turn exceeded wheel lock; the committed leg was clamped to the selected driving mode. Hold Ctrl to drive the tightest turn the mode allows.");
                }

                if (leg.AimedBehindTheBeam)
                {
                    RhinoApp.WriteLine("That point was behind the vehicle, so the leg stopped at a half turn rather than looping round to it. Reverse to back towards it, or hold Ctrl to turn at full lock.");
                }
            }
        }

        samples = route;
        manoeuvre = new ManoeuvreDefinition(
            ManoeuvreDefinition.CurrentSchemaVersion,
            new Point3(startModel.X * metresPerModelUnit, startModel.Y * metresPerModelUnit, startModel.Z * metresPerModelUnit),
            Math.Atan2(headingVector.Y, headingVector.X),
            initialDirection,
            controls);
        controlCurve = new PolylineCurve(
            new[] { manoeuvre.StartPositionMetres }
                .Concat(controls.Select(control => control.PositionMetres))
                .Select(point => ToModelPoint(point, document.ModelUnitSystem)));
        return Result.Success;
    }

    /// <summary>
    /// A short ray along the heading the leg ends on. Aiming at a point rotates the vehicle by twice
    /// the bearing picked, so the exit direction is rarely the one the cursor suggests; drawing it
    /// makes that visible while aiming rather than after committing.
    /// </summary>
    private static void DrawExitDirection(
        global::Rhino.Display.DisplayPipeline display,
        VehicleState state,
        UnitSystem modelUnits)
    {
        var scale = RhinoMath.UnitScale(UnitSystem.Meters, modelUnits);
        var heading = state.VehicleHeadingRadians + (state.Direction == TravelDirection.Reverse ? Math.PI : 0.0);
        var from = state.RearAxleCentreMetres.XY;
        var to = from + (new Point2(Math.Cos(heading), Math.Sin(heading)) * 6.0);
        display.DrawDottedLine(
            new Point3d(from.X * scale, from.Y * scale, state.RearAxleCentreMetres.Z * scale),
            new Point3d(to.X * scale, to.Y * scale, state.RearAxleCentreMetres.Z * scale),
            Color.SlateGray);
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

    private static double Degrees(double radians)
    {
        var degrees = radians * 180.0 / Math.PI;
        return degrees < 0.0 ? degrees + 360.0 : degrees;
    }

    /// <summary>
    /// The two circles the rear axle would trace at full lock, left and right of where the vehicle
    /// stands.
    /// </summary>
    /// <remarks>
    /// Drawn only while a locked turn is being aimed, because they answer the question that mode
    /// exists for: how tight the turn can actually be. Without them the tightest available radius is
    /// invisible, and the only way to discover it is to click and see.
    /// </remarks>
    private static void DrawLockCircles(
        global::Rhino.Display.DisplayPipeline display,
        VehicleDefinition vehicle,
        DrivingModeDefinition mode,
        VehicleState state,
        UnitSystem modelUnits)
    {
        var scale = RhinoMath.UnitScale(UnitSystem.Meters, modelUnits);
        var radius = TurningGeometryCalculator.AtFullLock(vehicle, mode).RearAxleRadiusMetres;
        var heading = state.VehicleHeadingRadians + (state.Direction == TravelDirection.Reverse ? Math.PI : 0.0);
        foreach (var side in new[] { 1.0, -1.0 })
        {
            var centre = state.RearAxleCentreMetres.XY + new Point2(
                Math.Cos(heading + (side * Math.PI * 0.5)) * radius,
                Math.Sin(heading + (side * Math.PI * 0.5)) * radius);
            var points = new List<Point3d>();
            for (var step = 0; step <= 72; step++)
            {
                var angle = step * Math.PI * 2.0 / 72.0;
                points.Add(new Point3d(
                    (centre.X + (Math.Cos(angle) * radius)) * scale,
                    (centre.Y + (Math.Sin(angle) * radius)) * scale,
                    state.RearAxleCentreMetres.Z * scale));
            }

            display.DrawDottedPolyline(new Polyline(points), Color.Thistle, false);
        }
    }

    private static Polyline CommittedPolyline(IReadOnlyList<RouteSample> route, UnitSystem modelUnits) =>
        new(route.Select(sample => ToModelPoint(sample.PositionMetres, modelUnits)));

    /// <summary>
    /// Body outlines stamped along the committed route, at the footprint reading interval.
    /// </summary>
    /// <remarks>
    /// Each stamp remembers which sample it was taken at, so a preview that rewinds part of the
    /// route can drop the stamps belonging to the part being given back.
    /// </remarks>
    private static IReadOnlyList<(Polyline Outline, int SampleIndex)> CommittedFootprints(
        IReadOnlyList<RouteSample> route,
        VehicleDefinition vehicle,
        UnitSystem modelUnits)
    {
        // Stamps are a reading aid, and they are redrawn on every mouse move. Two metres is the
        // readable spacing, but a long manoeuvre must not turn each frame into hundreds of
        // polylines, so the interval opens up once the count would exceed what anyone can read.
        const int maximumStamps = 120;
        var routeLength = route.Count == 0 ? 0.0 : route[^1].StationMetres;
        var intervalMetres = Math.Max(2.0, routeLength / maximumStamps);
        var scale = RhinoMath.UnitScale(UnitSystem.Meters, modelUnits);
        var stamps = new List<(Polyline Outline, int SampleIndex)>();
        var nextStation = 0.0;
        for (var index = 0; index < route.Count; index++)
        {
            var sample = route[index];
            if (sample.StationMetres < nextStation) continue;
            nextStation = sample.StationMetres + intervalMetres;
            var heading = Geometry2D.NormalizeAngle(
                sample.PathHeadingRadians + (sample.Direction == TravelDirection.Reverse ? Math.PI : 0.0));
            var points = vehicle.BodyOutline
                .Select(point => Geometry2D.Transform(point, sample.PositionMetres.XY, heading))
                .Select(point => new Point3d(point.X * scale, point.Y * scale, sample.PositionMetres.Z * scale))
                .ToList();
            points.Add(points[0]);
            stamps.Add((new Polyline(points), index));
        }

        return stamps;
    }

    private static RouteSample StateSample(VehicleState state, VehicleDefinition vehicle)
    {
        var pathHeading = Geometry2D.NormalizeAngle(
            state.VehicleHeadingRadians + (state.Direction == TravelDirection.Reverse ? Math.PI : 0.0));
        var curvature = (double)state.Direction * Math.Tan(state.SteeringAngleRadians) / vehicle.WheelbaseMetres;
        return new RouteSample(state.StationMetres, state.RearAxleCentreMetres, pathHeading, curvature, state.Direction);
    }

    private static void DrawVehicle(
        global::Rhino.Display.DisplayPipeline display,
        VehicleDefinition vehicle,
        VehicleState state,
        UnitSystem modelUnits,
        Color color)
    {
        var scale = RhinoMath.UnitScale(UnitSystem.Meters, modelUnits);
        var points = vehicle.BodyOutline
            .Select(point => Geometry2D.Transform(point, state.RearAxleCentreMetres.XY, state.VehicleHeadingRadians))
            .Select(point => new Point3d(point.X * scale, point.Y * scale, state.RearAxleCentreMetres.Z * scale))
            .ToList();
        points.Add(points[0]);
        display.DrawPolyline(new Polyline(points), color, 2);
    }

    private static Point3d ToModelPoint(Point3 pointMetres, UnitSystem modelUnits)
    {
        var scale = RhinoMath.UnitScale(UnitSystem.Meters, modelUnits);
        return new Point3d(pointMetres.X * scale, pointMetres.Y * scale, pointMetres.Z * scale);
    }
}
