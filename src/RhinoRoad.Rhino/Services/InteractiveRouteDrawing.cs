using System.Drawing;
using Rhino;
using Rhino.Display;
using Rhino.Geometry;
using RhinoRoad.Core;

namespace RhinoRoad.Rhino.Services;

/// <summary>
/// A planned leg ready to draw: the leg, its sweep against the site, the trailer's fold at its end,
/// and the control that drove its last part, which says what a shortfall on it means.
/// </summary>
internal sealed record LegPreview(PlannedManoeuvreLeg Leg, SweepPreview Sweep, ArticulationChain? Chain, ManoeuvreControl Control);

/// <summary>
/// The committed part of the journey in model units, rebuilt only when a commit or undo changes it
/// rather than on every frame.
/// </summary>
internal sealed record CommittedDrawing(
    Polyline Path,
    IReadOnlyList<TravelDirection> PathDirections,
    IReadOnlyList<(IReadOnlyList<Polyline> Outlines, int SampleIndex, TravelDirection Direction)> Footprints);

/// <summary>What the click-to-drive preview draws, kept apart from the decisions the pick loop makes.</summary>
/// <remarks>
/// Forward and reverse travel are coloured apart everywhere -- blue forward, purple reverse -- the way
/// the plan-reverse review always drew them, because a manoeuvre with cusps in one colour reads as a
/// single line doubling back on itself.
/// </remarks>
internal static class InteractiveRouteDrawing
{
    public static readonly Color ForwardCommitted = Color.RoyalBlue;
    public static readonly Color ReverseCommitted = Color.Purple;
    public static readonly Color ForwardPlanned = Color.CornflowerBlue;
    public static readonly Color ReversePlanned = Color.MediumVioletRed;
    private static readonly Color Stale = Color.DarkGray;

    public static Color TravelColour(TravelDirection direction, bool committed) => direction == TravelDirection.Reverse
        ? committed ? ReverseCommitted : ReversePlanned
        : committed ? ForwardCommitted : ForwardPlanned;

    /// <summary>
    /// The colour key, and the modifiers a pick responds to. The modifiers live here rather than in
    /// the prompt, which they used to push past 200 characters.
    /// </summary>
    public static void Legend(ViewportMessages messages, bool showModifiers)
    {
        if (showModifiers)
        {
            messages.Legend(
            [
                ("Ctrl: full-lock turn", Color.DimGray),
                ("Shift: square the exit", Color.DimGray),
                ("Enter: done", Color.DimGray),
                ("Esc: keep or discard", Color.DimGray)
            ]);
        }

        messages.Legend(
        [
            ("Forward", ForwardPlanned),
            ("Reverse", ReversePlanned),
            ("Body sweep", Color.SteelBlue),
            ("Clearance", Color.SeaGreen),
            ("Clash", Color.Red),
            ("Steering limit", Color.OrangeRed),
            ("Dotted: reshaped tail", Color.DimGray)
        ]);
    }

    /// <summary>
    /// Everything a planned leg shows: its sweep and clash, the committed route it keeps, the tail it
    /// gives back, the leg itself, and the vehicle where the leg leaves it.
    /// </summary>
    public static void DrawLeg(
        DisplayPipeline display,
        ViewportMessages messages,
        VehicleDefinition vehicle,
        IReadOnlyList<RouteSample> route,
        CommittedDrawing committed,
        JourneySweepPreview sweepPreview,
        LegPreview preview,
        bool current,
        UnitSystem modelUnits)
    {
        var planned = preview.Leg;
        sweepPreview.Draw(display, preview.Sweep, messages, current);

        // What the ease-out hands back, drawn faintly, so rewinding into the corner reads as the exit
        // being reshaped rather than as geometry that silently disappeared.
        if (planned.FromIndex < route.Count - 1)
        {
            var discarded = new Polyline(route.Skip(planned.FromIndex)
                .Select(sample => ToModelPoint(sample.PositionMetres, modelUnits)));
            if (discarded.Count > 1) display.DrawDottedPolyline(discarded, Color.Gainsboro, false);
        }

        var kept = Math.Min(planned.FromIndex + 1, committed.Path.Count);
        DrawTravel(display, committed.Path.Take(kept).ToArray(), committed.PathDirections, committed: true, current, 2);

        // Only the stamps the ease-out keeps. Drawing all of them leaves the discarded tail's body
        // outlines sitting in the viewport, so a corner whose overshoot has just been rewound away
        // still looks as though it overshoots.
        foreach (var stamp in committed.Footprints)
        {
            if (stamp.SampleIndex > planned.FromIndex) break;
            var colour = !current ? Color.Gainsboro
                : stamp.Direction == TravelDirection.Reverse ? Color.Plum : Color.LightSteelBlue;
            foreach (var outline in stamp.Outlines) display.DrawPolyline(outline, colour, 1);
        }

        DrawTravel(display, planned.Samples, modelUnits, committed: false, current, 3);
        DrawVehicle(display, vehicle, planned.EndState, modelUnits,
            !current ? Stale : planned.RequestedAngleExceeded ? Color.OrangeRed : Color.DarkBlue,
            preview.Chain);
        DrawExitDirection(display, planned.EndState, modelUnits);

        if (planned.RequestedAngleExceeded)
        {
            // Reversing a combination, the point means where the trailer should end up, and reversing
            // into a bay any vehicle has to arrive on it square, so "steering limit" is the wrong thing
            // to say when it does not get there: the wheel may be nowhere near its lock.
            messages.Add(ReverseWording.ShortfallWarning(vehicle, preview.Control), Color.OrangeRed);
        }
        if (planned.ExitHeadingUnavailable)
            messages.Add(ReverseWording.ExitHeadingUnavailable, Color.Goldenrod);
        if (planned.AimedBehindTheBeam)
            messages.Add(
                "That point is behind the vehicle — aiming stops at a half turn. Reverse, or hold Ctrl to turn further.",
                Color.OrangeRed);
    }

    /// <summary>Samples drawn as runs of one travel direction each, coloured by it.</summary>
    public static void DrawTravel(
        DisplayPipeline display,
        IReadOnlyList<RouteSample> samples,
        UnitSystem modelUnits,
        bool committed,
        bool current,
        int width) =>
        DrawTravel(
            display,
            samples.Select(sample => ToModelPoint(sample.PositionMetres, modelUnits)).ToArray(),
            samples.Select(sample => sample.Direction).ToArray(),
            committed,
            current,
            width);

    private static void DrawTravel(
        DisplayPipeline display,
        IReadOnlyList<Point3d> points,
        IReadOnlyList<TravelDirection> directions,
        bool committed,
        bool current,
        int width)
    {
        if (points.Count < 2) return;
        var start = 0;
        var direction = directions[0];
        for (var index = 1; index <= points.Count; index++)
        {
            // A run ends where the next sample changes gear. The cusp sample is shared by both runs,
            // so the line stays continuous through it.
            if (index < points.Count && directions[index] == direction) continue;
            if (index - start > 1)
            {
                display.DrawPolyline(
                    new Polyline(points.Skip(start).Take(index - start)),
                    current ? TravelColour(direction, committed) : Stale,
                    width);
            }

            if (index == points.Count) break;
            start = index - 1;
            direction = directions[index];
        }
    }

    public static CommittedDrawing Committed(
        IReadOnlyList<RouteSample> route, VehicleDefinition vehicle, UnitSystem modelUnits) => new(
        new Polyline(route.Select(sample => ToModelPoint(sample.PositionMetres, modelUnits))),
        route.Select(sample => sample.Direction).ToArray(),
        CommittedFootprints(route, vehicle, modelUnits));

    /// <summary>
    /// A short ray along the heading the leg ends on. Aiming at a point rotates the vehicle by twice
    /// the bearing picked, so the exit direction is rarely the one the cursor suggests; drawing it
    /// makes that visible while aiming rather than after committing.
    /// </summary>
    public static void DrawExitDirection(DisplayPipeline display, VehicleState state, UnitSystem modelUnits)
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
    /// The two circles the rear axle would trace at full lock, left and right of where the vehicle
    /// stands.
    /// </summary>
    /// <remarks>
    /// Drawn only while a locked turn is being aimed, because they answer the question that mode
    /// exists for: how tight the turn can actually be. Without them the tightest available radius is
    /// invisible, and the only way to discover it is to click and see.
    /// </remarks>
    public static void DrawLockCircles(
        DisplayPipeline display,
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

    /// <summary>
    /// The docking body -- the trailer, or a rigid vehicle itself -- as it would stand in the bay
    /// being picked, and its axle.
    /// </summary>
    public static void DrawDockedBody(
        DisplayPipeline display, VehicleDefinition vehicle, Point3 axleMetres, double travelHeadingRadians, UnitSystem modelUnits)
    {
        var scale = RhinoMath.UnitScale(UnitSystem.Meters, modelUnits);
        var outline = ReverseBay.DockedOutline(vehicle, axleMetres, travelHeadingRadians)
            .Select(point => new Point3d(point.X * scale, point.Y * scale, axleMetres.Z * scale))
            .ToList();
        if (outline.Count < 3) return;
        outline.Add(outline[0]);
        display.DrawPolyline(new Polyline(outline), Color.MediumPurple, 2);
        display.DrawPoint(ToModelPoint(axleMetres, modelUnits), PointStyle.X, 4, Color.MediumPurple);
    }

    public static void DrawVehicle(
        DisplayPipeline display,
        VehicleDefinition vehicle,
        VehicleState state,
        UnitSystem modelUnits,
        Color color,
        ArticulationChain? chain = null)
    {
        var scale = RhinoMath.UnitScale(UnitSystem.Meters, modelUnits);
        var origin = state.RearAxleCentreMetres.XY;
        var outlines = new List<IReadOnlyList<Point2>> { vehicle.BodyOutline
            .Select(point => Geometry2D.Transform(point, origin, state.VehicleHeadingRadians)).ToArray() };
        foreach (var towed in chain?.Poses(origin, state.VehicleHeadingRadians) ?? [])
        {
            if (towed.BodyOutlineWorldMetres.Count >= 3) outlines.Add(towed.BodyOutlineWorldMetres);
        }

        foreach (var outline in outlines)
        {
            var points = outline
                .Select(point => new Point3d(point.X * scale, point.Y * scale, state.RearAxleCentreMetres.Z * scale))
                .ToList();
            points.Add(points[0]);
            display.DrawPolyline(new Polyline(points), color, 2);
        }
    }

    public static Point3d ToModelPoint(Point3 pointMetres, UnitSystem modelUnits)
    {
        var scale = RhinoMath.UnitScale(UnitSystem.Meters, modelUnits);
        return new Point3d(pointMetres.X * scale, pointMetres.Y * scale, pointMetres.Z * scale);
    }

    /// <summary>
    /// Body outlines stamped along the committed route, at the footprint reading interval.
    /// </summary>
    /// <remarks>
    /// Each stamp remembers which sample it was taken at, so a preview that rewinds part of the
    /// route can drop the stamps belonging to the part being given back.
    /// </remarks>
    private static IReadOnlyList<(IReadOnlyList<Polyline> Outlines, int SampleIndex, TravelDirection Direction)> CommittedFootprints(
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
        var stamps = new List<(IReadOnlyList<Polyline> Outlines, int SampleIndex, TravelDirection Direction)>();
        var nextStation = 0.0;
        var chain = ArticulationTrace.Along(vehicle, route);
        for (var index = 0; index < route.Count; index++)
        {
            var sample = route[index];
            if (sample.StationMetres < nextStation) continue;
            nextStation = sample.StationMetres + intervalMetres;
            var heading = Geometry2D.NormalizeAngle(
                sample.PathHeadingRadians + (sample.Direction == TravelDirection.Reverse ? Math.PI : 0.0));
            var outlines = new List<IReadOnlyList<Point2>> { vehicle.BodyOutline
                .Select(point => Geometry2D.Transform(point, sample.PositionMetres.XY, heading)).ToArray() };
            foreach (var towed in chain[index]?.Poses(sample.PositionMetres.XY, heading) ?? [])
            {
                if (towed.BodyOutlineWorldMetres.Count >= 3) outlines.Add(towed.BodyOutlineWorldMetres);
            }

            stamps.Add((outlines.Select(outline =>
            {
                var points = outline
                    .Select(point => new Point3d(point.X * scale, point.Y * scale, sample.PositionMetres.Z * scale))
                    .ToList();
                points.Add(points[0]);
                return new Polyline(points);
            }).ToArray(), index, sample.Direction));
        }

        return stamps;
    }
}
