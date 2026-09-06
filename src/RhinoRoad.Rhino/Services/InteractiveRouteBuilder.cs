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
        while (true)
        {
            var sweepPreview = new JourneySweepPreview(route, vehicle, clearanceMetres, document.ModelUnitSystem);
            PlannedManoeuvreLeg? planned = null;
            Point3d? lastPreviewPoint = null;
            using var getter = new GetPoint();
            var wheelNow = state.SteeringAngleRadians * 180.0 / Math.PI;
            getter.SetCommandPrompt(finishing
                ? $"Finish: pick the direction to end up travelling in (wheel {wheelNow:0.#} deg); Enter to finish"
                : $"Pick next point ({state.Direction}, wheel {wheelNow:0.#} deg); Enter to finish");
            getter.AcceptNothing(true);
            var reverseOption = getter.AddOption("Reverse");
            var undoOption = getter.AddOption("Undo");
            var finishOption = getter.AddOption(finishing ? "Aim" : "Finish");
            getter.DynamicDraw += (_, args) =>
            {
                var cursor = new Point2(args.CurrentPoint.X * metresPerModelUnit, args.CurrentPoint.Y * metresPerModelUnit);
                var control = new ManoeuvreControl(
                    new Point3(cursor.X, cursor.Y, args.CurrentPoint.Z * metresPerModelUnit),
                    state.Direction,
                    finishing ? ManoeuvreControlKind.Finish : ManoeuvreControlKind.Aim);
                if (planned is null || lastPreviewPoint != args.CurrentPoint)
                {
                    planned = ManoeuvreReplayService.PlanControl(vehicle, mode, route, legStartIndex, state, control);
                    lastPreviewPoint = args.CurrentPoint;
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

            if (getResult != GetResult.Point) continue;

            // Re-plan from the committed Rhino point rather than trusting the last mouse-move
            // callback. That makes the stored control and the baked samples an exact pair even if
            // snapping adjusts the click after the final dynamic-draw event.
            var picked = getter.Point();
            var pickedControl = new ManoeuvreControl(
                new Point3(picked.X * metresPerModelUnit, picked.Y * metresPerModelUnit, picked.Z * metresPerModelUnit),
                state.Direction,
                finishing ? ManoeuvreControlKind.Finish : ManoeuvreControlKind.Aim);
            planned = ManoeuvreReplayService.PlanControl(vehicle, mode, route, legStartIndex, state, pickedControl);

            history.Push(new HistoryEntry(state, route.ToArray(), controls.ToArray(), legStartIndex));
            if (planned.FromIndex < route.Count - 1)
            {
                route.RemoveRange(planned.FromIndex + 1, route.Count - planned.FromIndex - 1);
            }

            legStartIndex = route.Count - 1;
            route.AddRange(planned.Samples.Skip(1));
            state = planned.EndState;
            controls.Add(pickedControl);
            committedPath = CommittedPolyline(route, document.ModelUnitSystem);
            committedFootprints = CommittedFootprints(route, vehicle, document.ModelUnitSystem);
            if (planned.RequestedAngleExceeded)
            {
                RhinoApp.WriteLine("Requested turn exceeded wheel lock; the committed leg was clamped to the selected driving mode.");
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
