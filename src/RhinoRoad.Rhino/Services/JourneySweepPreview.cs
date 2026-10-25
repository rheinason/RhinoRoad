using System.Drawing;
using Rhino;
using Rhino.Display;
using Rhino.Geometry;
using RhinoRoad.Core;

namespace RhinoRoad.Rhino.Services;

/// <summary>A previewed sweep and where it runs into the site.</summary>
internal sealed record SweepPreview(JourneySweep Sweep, SiteClash Clash);

/// <summary>Cache the retained journey; rebuild it whenever easing changes the retained endpoint.</summary>
/// <remarks>
/// Built from a snapshot of the route, never the live list, because a background plan may still be
/// sweeping it after the click that changes the route. The builder's cache is shared between the
/// frame and that background plan, so building is serialised; drawing reads only finished results.
/// </remarks>
internal sealed class JourneySweepPreview
{
    private static readonly Color ClashColour = Color.Red;
    private static readonly Color StaleClashColour = Color.LightCoral;

    private readonly JourneySweepBuilder _builder;
    private readonly SiteConstraints _site;
    private readonly double _scale;
    private readonly double _elevationModel;

    public JourneySweepPreview(IReadOnlyList<RouteSample> route, VehicleDefinition vehicle,
        double clearance, UnitSystem units, SiteConstraints site)
    {
        _builder = new JourneySweepBuilder(route, vehicle, clearance);
        _site = site;
        _scale = RhinoMath.UnitScale(UnitSystem.Meters, units);
        _elevationModel = route[0].PositionMetres.Z * _scale;
    }

    public SweepPreview Build(PlannedManoeuvreLeg planned)
    {
        JourneySweep sweep;
        lock (_builder) sweep = _builder.Build(planned);
        var clash = sweep.Clearance.IsSuccess
            ? SiteClashPreview.Find(sweep.Clearance.Region!, _site)
            : SiteClash.None;
        return new SweepPreview(sweep, clash);
    }

    /// <summary>
    /// Draws the sweep, and the clash in red over it. A stale preview -- the last one completed while
    /// a newer one is computed -- is drawn faded, so it reads as where the vehicle was rather than
    /// where it is.
    /// </summary>
    public void Draw(DisplayPipeline display, SweepPreview preview, ViewportMessages messages, bool current)
    {
        var sweep = preview.Sweep;
        if (!sweep.Body.IsSuccess || !sweep.Clearance.IsSuccess)
        {
            messages.Add("Sweep preview unavailable", Color.OrangeRed);
            return;
        }
        DrawRegion(display, sweep.Clearance.Region!, current ? Color.SeaGreen : Color.DarkGray, 2);
        DrawRegion(display, sweep.Body.Region!, current ? Color.SteelBlue : Color.Gray, 2);
        if (sweep.Retained is not null) DrawRegion(display, sweep.Retained, current ? Color.RoyalBlue : Color.Gray, 1);

        var clash = preview.Clash;
        if (!clash.Any) return;
        var colour = current ? ClashColour : StaleClashColour;
        foreach (var loop in clash.ObstacleOverlaps.Concat(clash.OutsideAllowedArea)) DrawLoop(display, loop, colour, 3, closed: true);
        foreach (var run in clash.ObstacleCrossings) DrawLoop(display, run, colour, 4, closed: false);

        var findings = new List<string>();
        if (clash.ObstaclesHit > 0)
            findings.Add($"clashes with {clash.ObstaclesHit} obstacle{(clash.ObstaclesHit == 1 ? string.Empty : "s")}");
        if (clash.LeavesAllowedArea) findings.Add("leaves the allowed area");
        messages.Add($"Clearance {string.Join(" and ", findings)}", colour);
    }

    private void DrawRegion(DisplayPipeline display, SweptRegion region, Color colour, int width)
    {
        foreach (var loop in new[] { region.OuterBoundary }.Concat(region.Holes)) DrawLoop(display, loop, colour, width, closed: true);
    }

    private void DrawLoop(DisplayPipeline display, IReadOnlyList<Point2> loop, Color colour, int width, bool closed)
    {
        if (loop.Count < 2) return;
        var points = loop.Select(p => new Point3d(p.X * _scale, p.Y * _scale, _elevationModel)).ToList();
        if (closed) points.Add(points[0]);
        display.DrawPolyline(points, colour, width);
    }
}
