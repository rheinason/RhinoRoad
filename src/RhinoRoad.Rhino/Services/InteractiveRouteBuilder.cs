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
/// Draws the intended line by clicking through points, with the vehicle previewed along it.
/// </summary>
/// <remarks>
/// <para>
/// Shaped like Rhino's <c>InterpCrv</c>: click the points the line should pass through, Enter to
/// finish, with snaps, typed coordinates, and ortho all working because they are ordinary picked
/// points. What is produced is a curve — the same kind that could have been drawn beforehand and
/// selected — so there is only one thing downstream and only one thing to edit afterwards.
/// </para>
/// <para>
/// <c>Reverse</c> ends the current leg and starts the next one going the other way. A three-point
/// turn is three legs, and the cusp between them is where the vehicle stops and changes direction,
/// so the new leg starts from exactly where the last one ended.
/// </para>
/// </remarks>
internal static class InteractiveRouteBuilder
{
    public static Result TryBuild(
        RhinoDoc document,
        VehicleDefinition vehicle,
        DrivingModeDefinition mode,
        TravelDirection startingDirection,
        out IReadOnlyList<IntentLeg> legs)
    {
        var completed = new List<IntentLeg>();
        var picked = new List<Point3d>();
        var direction = startingDirection;
        legs = completed;

        while (true)
        {
            using var getter = new GetPoint();
            getter.SetCommandPrompt(Prompt(completed.Count, picked.Count, direction));
            var canFinish = picked.Count >= 2 || (completed.Count > 0 && picked.Count == 0);
            getter.AcceptNothing(canFinish);
            var reverseOption = picked.Count >= 2 ? getter.AddOption("Reverse") : -1;
            var undoOption = picked.Count > 0 ? getter.AddOption("Undo") : -1;
            if (picked.Count > 0) getter.DrawLineFromPoint(picked[^1], true);

            getter.DynamicDraw += (_, args) =>
                Draw(args.Display, completed, picked, args.CurrentPoint, direction, vehicle, mode, document);

            var result = getter.Get();
            if (result == GetResult.Cancel) return Result.Cancel;
            if (result == GetResult.Nothing) break;

            if (result == GetResult.Option)
            {
                if (getter.OptionIndex() == undoOption && picked.Count > 0)
                {
                    picked.RemoveAt(picked.Count - 1);
                }
                else if (getter.OptionIndex() == reverseOption)
                {
                    var leg = IntentPathFactory.InterpolateThrough(picked);
                    if (leg is null) continue;
                    completed.Add(new IntentLeg(leg, direction));
                    direction = direction == TravelDirection.Forward
                        ? TravelDirection.Reverse
                        : TravelDirection.Forward;

                    // The next leg starts at the cusp, which is where this one ended.
                    picked = [picked[^1]];
                    RhinoApp.WriteLine($"Leg {completed.Count} finished. Now driving {direction}.");
                }

                continue;
            }

            if (result != GetResult.Point) continue;
            picked.Add(getter.Point());
        }

        if (picked.Count >= 2)
        {
            var last = IntentPathFactory.InterpolateThrough(picked);
            if (last is not null) completed.Add(new IntentLeg(last, direction));
        }

        if (completed.Count == 0)
        {
            RhinoApp.WriteLine("An intended line needs at least two points.");
            return Result.Cancel;
        }

        return Result.Success;
    }

    private static string Prompt(int completedLegs, int pickedCount, TravelDirection direction)
    {
        if (pickedCount == 0)
        {
            return completedLegs == 0
                ? "Start of the intended rear-axle line"
                : $"First point of leg {completedLegs + 1} ({direction})";
        }

        return $"Next point ({direction}, {pickedCount} placed); Enter to finish";
    }

    private static void Draw(
        DisplayPipeline display,
        IReadOnlyList<IntentLeg> completed,
        IReadOnlyList<Point3d> picked,
        Point3d cursor,
        TravelDirection direction,
        VehicleDefinition vehicle,
        DrivingModeDefinition mode,
        RhinoDoc document)
    {
        var trial = new List<Point3d>(picked) { cursor };
        var pending = IntentPathFactory.InterpolateThrough(trial);

        foreach (var leg in completed) display.DrawCurve(leg.Curve, Color.MediumPurple, 2);
        if (pending is not null) display.DrawCurve(pending, Color.RoyalBlue, 2);
        foreach (var point in picked) display.DrawPoint(point, PointStyle.RoundControlPoint, 4, Color.RoyalBlue);

        var legs = new List<IntentLeg>(completed);
        if (pending is not null) legs.Add(new IntentLeg(pending, direction));
        if (legs.Count == 0) return;

        FollowedRoute route;
        try
        {
            route = PathFollower.FollowLegs(vehicle, mode, legs
                .Select(leg => new RouteLeg(
                    IntentPathFactory.FromCurve(leg.Curve, document.ModelUnitSystem),
                    leg.Direction))
                .ToArray());
        }
        catch (Exception)
        {
            // A degenerate line under the cursor is not worth a message on every mouse move.
            return;
        }

        var scale = RhinoMath.UnitScale(UnitSystem.Meters, document.ModelUnitSystem);
        var samples = route.Samples;

        // Red once the vehicle is further from the drawn line than its own width: past that the
        // line has stopped describing where the vehicle goes, which is worth seeing while drawing.
        var strayed = route.MaximumDeviationMetres > vehicle.WidthMetres;
        if (samples.Count > 1)
        {
            display.DrawPolyline(
                new Polyline(samples.Select(sample => ToModel(sample.PositionMetres, scale))),
                strayed ? Color.OrangeRed : Color.SteelBlue,
                2);
        }

        foreach (var stamp in Footprints(samples, vehicle, scale))
        {
            display.DrawPolyline(stamp, Color.LightSteelBlue, 1);
        }

        if (samples.Count > 0) DrawVehicle(display, vehicle, samples[^1], scale, Color.DarkBlue);
    }

    private static IReadOnlyList<Polyline> Footprints(
        IReadOnlyList<RouteSample> route,
        VehicleDefinition vehicle,
        double scale)
    {
        // Redrawn on every mouse move, so the spacing opens up on a long line rather than turning
        // each frame into hundreds of polylines.
        const int maximumStamps = 120;
        var routeLength = route.Count == 0 ? 0.0 : route[^1].StationMetres;
        var interval = Math.Max(2.0, routeLength / maximumStamps);
        var stamps = new List<Polyline>();
        var nextStation = 0.0;
        foreach (var sample in route)
        {
            if (sample.StationMetres < nextStation) continue;
            nextStation = sample.StationMetres + interval;
            stamps.Add(Outline(vehicle, sample, scale));
        }

        return stamps;
    }

    private static Polyline Outline(VehicleDefinition vehicle, RouteSample sample, double scale)
    {
        var heading = Geometry2D.NormalizeAngle(
            sample.PathHeadingRadians + (sample.Direction == TravelDirection.Reverse ? Math.PI : 0.0));
        var points = vehicle.BodyOutline
            .Select(point => Geometry2D.Transform(point, sample.PositionMetres.XY, heading))
            .Select(point => new Point3d(point.X * scale, point.Y * scale, sample.PositionMetres.Z * scale))
            .ToList();
        points.Add(points[0]);
        return new Polyline(points);
    }

    private static void DrawVehicle(
        DisplayPipeline display,
        VehicleDefinition vehicle,
        RouteSample sample,
        double scale,
        Color color) =>
        display.DrawPolyline(Outline(vehicle, sample, scale), color, 2);

    private static Point3d ToModel(Point3 pointMetres, double scale) =>
        new(pointMetres.X * scale, pointMetres.Y * scale, pointMetres.Z * scale);
}
