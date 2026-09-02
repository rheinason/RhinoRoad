using System.Drawing;
using Rhino;
using Rhino.Commands;
using Rhino.Display;
using Rhino.Geometry;
using Rhino.Input;
using Rhino.Input.Custom;
using RhinoRoad.Core;

namespace RhinoRoad.Rhino.Services;

/// <summary>
/// Drives the vehicle along a route the designer clicks out, one waypoint at a time.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately shaped like Rhino's own <c>Polyline</c>: pick points, watch the next leg preview
/// under the cursor, Enter to finish. Every waypoint therefore goes through Rhino's point input —
/// object snaps, typed coordinates, ortho — which is both the familiar way to work and the only
/// way to place a waypoint accurately enough to matter on a tight stretch.
/// </para>
/// <para>
/// What is previewed is what gets committed. The preview runs the same drive the stored waypoint
/// will run when the route is rebuilt later, so nothing shifts between aiming, baking, and editing.
/// </para>
/// </remarks>
internal static class InteractiveRouteBuilder
{
    public static Result TryBuild(
        RhinoDoc document,
        VehicleDefinition vehicle,
        DrivingModeDefinition mode,
        out IReadOnlyList<RouteSample> samples,
        out Curve? pathCurve,
        out Manoeuvre? manoeuvre)
    {
        samples = [];
        pathCurve = null;
        manoeuvre = null;
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
        var headingVector = headingGetter.Point() - startModel;
        if (Math.Sqrt((headingVector.X * headingVector.X) + (headingVector.Y * headingVector.Y))
            <= document.ModelAbsoluteTolerance)
        {
            RhinoApp.WriteLine("Heading point is too close to the start point.");
            return Result.Failure;
        }

        var recorder = new RouteRecorder(
            vehicle,
            mode,
            new Point3(
                startModel.X * metresPerModelUnit,
                startModel.Y * metresPerModelUnit,
                startModel.Z * metresPerModelUnit),
            Math.Atan2(headingVector.Y, headingVector.X));

        while (true)
        {
            using var getter = new GetPoint();
            var wheelDegrees = recorder.State.SteeringAngleRadians * 180.0 / Math.PI;
            getter.SetCommandPrompt(
                $"Next waypoint ({recorder.Direction}, wheel {wheelDegrees:0.#}°); Enter to finish");
            getter.AcceptNothing(true);
            var reverseOption = getter.AddOption("Reverse");
            var undoOption = getter.AddOption("Undo");

            DrivenSegment? preview = null;
            getter.DynamicDraw += (_, args) =>
            {
                Draw(args.Display, recorder, document.ModelUnitSystem, vehicle);
                var cursor = new Point2(
                    args.CurrentPoint.X * metresPerModelUnit,
                    args.CurrentPoint.Y * metresPerModelUnit);
                preview = recorder.Preview(cursor);
                DrawSegment(args.Display, preview, recorder.State, document.ModelUnitSystem, vehicle);
            };

            var result = getter.Get();
            if (result == GetResult.Cancel) return Result.Cancel;
            if (result == GetResult.Nothing)
            {
                if (recorder.Waypoints.Count == 0)
                {
                    RhinoApp.WriteLine("Place at least one waypoint.");
                    continue;
                }

                break;
            }

            if (result == GetResult.Option)
            {
                if (getter.OptionIndex() == reverseOption)
                {
                    recorder.Direction = recorder.Direction == TravelDirection.Forward
                        ? TravelDirection.Reverse
                        : TravelDirection.Forward;
                    RhinoApp.WriteLine($"Travel direction: {recorder.Direction}");
                }
                else if (getter.OptionIndex() == undoOption && !recorder.Undo())
                {
                    RhinoApp.WriteLine("Nothing to undo.");
                }

                continue;
            }

            if (result != GetResult.Point) continue;

            var point = getter.Point();
            var committed = recorder.Commit(new Point2(
                point.X * metresPerModelUnit,
                point.Y * metresPerModelUnit));
            if (committed.Samples.Count == 0)
            {
                RhinoApp.WriteLine("That waypoint is where the vehicle already is.");
            }
            else if (!committed.Arrived)
            {
                RhinoApp.WriteLine(
                    "The vehicle cannot reach that point from here without turning through more " +
                    "than a full circle. Place a nearer waypoint, or reverse.");
                recorder.Undo();
            }
        }

        samples = recorder.Samples;
        manoeuvre = recorder.ToManoeuvre();
        pathCurve = new PolylineCurve(
            recorder.Samples.Select(sample => ToModelPoint(sample.PositionMetres, document.ModelUnitSystem)));
        return Result.Success;
    }

    /// <summary>The route committed so far, redrawn every frame because none of it is in the document yet.</summary>
    private static void Draw(
        DisplayPipeline display,
        RouteRecorder recorder,
        UnitSystem modelUnits,
        VehicleDefinition vehicle)
    {
        var scale = RhinoMath.UnitScale(UnitSystem.Meters, modelUnits);
        if (recorder.Samples.Count > 1)
        {
            display.DrawPolyline(
                new Polyline(recorder.Samples.Select(sample => ToModelPoint(sample.PositionMetres, modelUnits))),
                Color.RoyalBlue,
                2);
        }

        foreach (var footprint in Footprints(recorder.Samples, vehicle, modelUnits))
        {
            display.DrawPolyline(footprint, Color.LightSteelBlue, 1);
        }

        // The waypoints, so the route reads as the short editable thing it is stored as rather than
        // as an undifferentiated trail.
        foreach (var waypoint in recorder.Waypoints)
        {
            display.DrawPoint(
                new Point3d(waypoint.TargetMetres.X * scale, waypoint.TargetMetres.Y * scale, 0.0),
                PointStyle.RoundControlPoint,
                4,
                Color.RoyalBlue);
        }
    }

    private static void DrawSegment(
        DisplayPipeline display,
        DrivenSegment segment,
        VehicleState from,
        UnitSystem modelUnits,
        VehicleDefinition vehicle)
    {
        // A segment that could not reach its target is drawn in the warning colour rather than
        // silently shown as if it were an ordinary leg — it will be refused on commit.
        var colour = segment.Arrived ? Color.CornflowerBlue : Color.OrangeRed;
        if (segment.Samples.Count > 1)
        {
            display.DrawPolyline(
                new Polyline(segment.Samples.Select(sample => ToModelPoint(sample.PositionMetres, modelUnits))),
                colour,
                3);
        }

        var end = segment.Samples.Count > 0 ? segment.EndState : from;
        DrawVehicle(display, vehicle, end, modelUnits, segment.Arrived ? Color.DarkBlue : Color.OrangeRed);
        DrawExitDirection(display, end, modelUnits);
    }

    /// <summary>A short ray along the heading the leg ends on.</summary>
    private static void DrawExitDirection(DisplayPipeline display, VehicleState state, UnitSystem modelUnits)
    {
        var scale = RhinoMath.UnitScale(UnitSystem.Meters, modelUnits);
        var heading = state.VehicleHeadingRadians +
            (state.Direction == TravelDirection.Reverse ? Math.PI : 0.0);
        var from = state.RearAxleCentreMetres.XY;
        var to = from + (new Point2(Math.Cos(heading), Math.Sin(heading)) * 6.0);
        display.DrawDottedLine(
            new Point3d(from.X * scale, from.Y * scale, state.RearAxleCentreMetres.Z * scale),
            new Point3d(to.X * scale, to.Y * scale, state.RearAxleCentreMetres.Z * scale),
            Color.SlateGray);
    }

    /// <summary>Body outlines stamped along the committed route, at the readable spacing.</summary>
    private static IReadOnlyList<Polyline> Footprints(
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

    private static void DrawVehicle(
        DisplayPipeline display,
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
