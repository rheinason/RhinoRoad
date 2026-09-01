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

        while (true)
        {
            GeneratedTrajectory? preview = null;
            var requestedExceeded = false;
            using var getter = new GetPoint();
            getter.SetCommandPrompt($"Pick next target ({state.Direction}); Enter to finish");
            getter.AcceptNothing(true);
            var reverseOption = getter.AddOption("Reverse");
            var undoOption = getter.AddOption("Undo");
            getter.DynamicDraw += (_, args) =>
            {
                var cursor = new Point2(args.CurrentPoint.X * metresPerModelUnit, args.CurrentPoint.Y * metresPerModelUnit);
                var controls = RateLimitedTrajectoryGenerator.ControlsFromCursor(vehicle, mode, state, cursor);
                requestedExceeded = controls.RequestedAngleExceeded;
                preview = generator.GenerateLeg(vehicle, mode, state, controls.TargetSteeringRadians, controls.TravelDistanceMetres, 0.10);
                var previewPolyline = new Polyline(preview.Samples.Select(sample => ToModelPoint(sample.PositionMetres, document.ModelUnitSystem)));
                args.Display.DrawPolyline(previewPolyline, requestedExceeded ? Color.OrangeRed : Color.CornflowerBlue, 3);
                DrawVehicle(args.Display, vehicle, preview.EndState, document.ModelUnitSystem, requestedExceeded ? Color.OrangeRed : Color.DarkBlue);
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
                    }
                }
                continue;
            }
            if (getResult != GetResult.Point || preview is null) continue;

            history.Push(new HistoryEntry(state, route.Count));
            state = preview.EndState;
            route.AddRange(preview.Samples.Skip(1));
            if (requestedExceeded)
            {
                RhinoApp.WriteLine("Requested turn exceeded wheel lock; the committed leg was clamped to the selected driving mode.");
            }
        }

        samples = route;
        pathCurve = new PolylineCurve(route.Select(sample => ToModelPoint(sample.PositionMetres, document.ModelUnitSystem)));
        return Result.Success;
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
