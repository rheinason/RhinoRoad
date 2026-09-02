using System.Drawing;
using Rhino;
using Rhino.Commands;
using Rhino.Geometry;
using Rhino.Input;
using Rhino.Input.Custom;
using RhinoRoad.Core;

namespace RhinoRoad.Rhino.Services;

internal static class InteractiveRouteBuilder
{
    private sealed record HistoryEntry(VehicleState State, int SampleCount);

    public static Result TryBuild(
        RhinoDoc document,
        VehicleDefinition vehicle,
        DrivingModeDefinition mode,
        out IReadOnlyList<RouteSample> samples,
        out Curve? pathCurve)
    {
        samples = [];
        pathCurve = null;
        var metresPerModelUnit = RhinoMath.UnitScale(document.ModelUnitSystem, UnitSystem.Meters);

        using var startGetter = new GetPoint();
        startGetter.SetCommandPrompt("Rear-axle midpoint start");
        if (startGetter.Get() != GetResult.Point) return Result.Cancel;
        var startModel = startGetter.Point();

        using var headingGetter = new GetPoint();
        headingGetter.SetCommandPrompt("Vehicle forward heading");
        headingGetter.SetBasePoint(startModel, true);
        headingGetter.DrawLineFromPoint(startModel, true);
        if (headingGetter.Get() != GetResult.Point) return Result.Cancel;
        var headingPoint = headingGetter.Point();
        var headingVector = headingPoint - startModel;
        if (Math.Sqrt((headingVector.X * headingVector.X) + (headingVector.Y * headingVector.Y)) <= document.ModelAbsoluteTolerance)
        {
            RhinoApp.WriteLine("Heading point is too close to the start point.");
            return Result.Failure;
        }

        var state = new VehicleState(
            new Point3(startModel.X * metresPerModelUnit, startModel.Y * metresPerModelUnit, startModel.Z * metresPerModelUnit),
            Math.Atan2(headingVector.Y, headingVector.X),
            0.0,
            TravelDirection.Forward,
            0.0);
        var route = new List<RouteSample>
        {
            StateSample(state, vehicle)
        };
        var history = new Stack<HistoryEntry>();
        var generator = new RateLimitedTrajectoryGenerator();

        // Everything driven so far. Committed legs are not in the document until the command ends,
        // so without redrawing them each frame the viewport shows only the leg being aimed and the
        // manoeuvre appears to vanish behind the cursor.
        var committedPath = CommittedPolyline(route, document.ModelUnitSystem);
        var committedFootprints = CommittedFootprints(route, vehicle, document.ModelUnitSystem);

        var straighten = false;
        while (true)
        {
            GeneratedTrajectory? preview = null;
            var requestedExceeded = false;
            using var getter = new GetPoint();
            var wheelNow = state.SteeringAngleRadians * 180.0 / Math.PI;
            getter.SetCommandPrompt(straighten
                ? $"Straightening from {wheelNow:0.#}° of lock: pick the direction to end up travelling in; Enter to finish"
                : $"Pick next target ({state.Direction}, wheel {wheelNow:0.#}°); Enter to finish");
            getter.AcceptNothing(true);
            var reverseOption = getter.AddOption("Reverse");
            var undoOption = getter.AddOption("Undo");
            var straightenOption = getter.AddOption(straighten ? "Aim" : "Straighten");
            getter.DynamicDraw += (_, args) =>
            {
                if (committedPath.Count > 1) args.Display.DrawPolyline(committedPath, Color.RoyalBlue, 2);
                foreach (var footprint in committedFootprints)
                {
                    args.Display.DrawPolyline(footprint, Color.LightSteelBlue, 1);
                }

                var cursor = new Point2(args.CurrentPoint.X * metresPerModelUnit, args.CurrentPoint.Y * metresPerModelUnit);
                if (straighten)
                {
                    // The cursor picks a direction, not a distance. The vehicle holds its lock until
                    // unwinding the wheel would land exactly on that direction, then runs the wheel
                    // back to centre -- so the leg ends travelling the way you pointed, wheel
                    // straight, with no overshoot to correct back.
                    var bearing = Math.Atan2(
                        cursor.Y - state.RearAxleCentreMetres.Y,
                        cursor.X - state.RearAxleCentreMetres.X);
                    preview = HeadingLegGenerator.ToHeading(vehicle, mode, state, bearing, 0.10);
                    requestedExceeded = false;
                }
                else
                {
                    var controls = RateLimitedTrajectoryGenerator.ControlsFromCursor(vehicle, mode, state, cursor);
                    requestedExceeded = controls.RequestedAngleExceeded;
                    preview = generator.GenerateLeg(
                        vehicle, mode, state, controls.TargetSteeringRadians, controls.TravelDistanceMetres, 0.10);
                }

                var previewPolyline = new Polyline(preview.Samples.Select(sample => ToModelPoint(sample.PositionMetres, document.ModelUnitSystem)));
                args.Display.DrawPolyline(previewPolyline, requestedExceeded ? Color.OrangeRed : Color.CornflowerBlue, 3);
                DrawVehicle(args.Display, vehicle, preview.EndState, document.ModelUnitSystem, requestedExceeded ? Color.OrangeRed : Color.DarkBlue);
                DrawExitDirection(args.Display, preview.EndState, document.ModelUnitSystem);
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
                    RhinoApp.WriteLine($"Travel direction: {state.Direction}");
                }
                else if (getter.OptionIndex() == straightenOption)
                {
                    straighten = !straighten;
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
                        route.RemoveRange(entry.SampleCount, route.Count - entry.SampleCount);
                        committedPath = CommittedPolyline(route, document.ModelUnitSystem);
                        committedFootprints = CommittedFootprints(route, vehicle, document.ModelUnitSystem);
                    }
                }
                continue;
            }
            if (getResult != GetResult.Point || preview is null) continue;

            history.Push(new HistoryEntry(state, route.Count));
            state = preview.EndState;
            route.AddRange(preview.Samples.Skip(1));
            committedPath = CommittedPolyline(route, document.ModelUnitSystem);
            committedFootprints = CommittedFootprints(route, vehicle, document.ModelUnitSystem);
            if (requestedExceeded)
            {
                RhinoApp.WriteLine("Requested turn exceeded wheel lock; the committed leg was clamped to the selected driving mode.");
            }
        }

        samples = route;
        pathCurve = new PolylineCurve(route.Select(sample => ToModelPoint(sample.PositionMetres, document.ModelUnitSystem)));
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

    /// <summary>Body outlines stamped along the committed route, at the footprint reading interval.</summary>
    private static IReadOnlyList<Polyline> CommittedFootprints(
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
        var stamps = new List<Polyline>();
        var nextStation = 0.0;
        foreach (var sample in route)
        {
            if (sample.StationMetres < nextStation) continue;
            nextStation = sample.StationMetres + intervalMetres;
            var heading = Geometry2D.NormalizeAngle(
                sample.PathHeadingRadians + (sample.Direction == TravelDirection.Reverse ? Math.PI : 0.0));
            var points = vehicle.BodyOutline
                .Select(point => Geometry2D.Transform(point, sample.PositionMetres.XY, heading))
                .Select(point => new Point3d(point.X * scale, point.Y * scale, sample.PositionMetres.Z * scale))
                .ToList();
            points.Add(points[0]);
            stamps.Add(new Polyline(points));
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
