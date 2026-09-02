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
/// points. What is produced is a curve, not a route — the same kind of curve that could have been
/// drawn beforehand and selected — so there is only one thing downstream and only one thing to
/// edit afterwards.
/// </para>
/// <para>
/// The vehicle is shown following that line as it is drawn, which is the useful feedback: the line
/// is the wish, the swept body is what the wish costs, and where they part company is where the
/// manoeuvre is in trouble.
/// </para>
/// </remarks>
internal static class InteractiveRouteBuilder
{
    public static Result TryBuild(
        RhinoDoc document,
        VehicleDefinition vehicle,
        DrivingModeDefinition mode,
        TravelDirection direction,
        out Curve? intentCurve)
    {
        intentCurve = null;
        var picked = new List<Point3d>();

        while (true)
        {
            using var getter = new GetPoint();
            getter.SetCommandPrompt(picked.Count == 0
                ? "Start of the intended rear-axle line"
                : $"Next point ({picked.Count} placed); Enter to finish");
            getter.AcceptNothing(picked.Count >= 2);
            var undoOption = picked.Count > 0 ? getter.AddOption("Undo") : -1;
            if (picked.Count > 0) getter.DrawLineFromPoint(picked[^1], true);

            getter.DynamicDraw += (_, args) =>
            {
                var trial = new List<Point3d>(picked) { args.CurrentPoint };
                var curve = IntentPathFactory.InterpolateThrough(trial);
                if (curve is null) return;
                args.Display.DrawCurve(curve, Color.RoyalBlue, 2);
                foreach (var point in picked) args.Display.DrawPoint(point, PointStyle.RoundControlPoint, 4, Color.RoyalBlue);
                DrawVehicleAlong(args.Display, curve, vehicle, mode, direction, document.ModelUnitSystem);
            };

            var result = getter.Get();
            if (result == GetResult.Cancel) return Result.Cancel;
            if (result == GetResult.Nothing) break;
            if (result == GetResult.Option)
            {
                if (getter.OptionIndex() == undoOption && picked.Count > 0) picked.RemoveAt(picked.Count - 1);
                continue;
            }

            if (result != GetResult.Point) continue;
            picked.Add(getter.Point());
        }

        if (picked.Count < 2)
        {
            RhinoApp.WriteLine("An intended line needs at least two points.");
            return Result.Cancel;
        }

        intentCurve = IntentPathFactory.InterpolateThrough(picked);
        return intentCurve is null ? Result.Failure : Result.Success;
    }

    /// <summary>
    /// Drives the line and draws what the vehicle does with it: the route, body stamps along it,
    /// and the vehicle where it ends up.
    /// </summary>
    private static void DrawVehicleAlong(
        DisplayPipeline display,
        Curve curve,
        VehicleDefinition vehicle,
        DrivingModeDefinition mode,
        TravelDirection direction,
        UnitSystem modelUnits)
    {
        FollowedRoute route;
        try
        {
            route = PathFollower.Follow(vehicle, mode, IntentPathFactory.FromCurve(curve, modelUnits), direction);
        }
        catch (Exception)
        {
            // A degenerate line under the cursor is not worth a message on every mouse move.
            return;
        }

        var scale = RhinoMath.UnitScale(UnitSystem.Meters, modelUnits);
        var samples = route.Samples;

        // Red once the vehicle is further from the drawn line than its own width: at that point the
        // line stops describing where the vehicle goes, which is worth seeing while drawing.
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
