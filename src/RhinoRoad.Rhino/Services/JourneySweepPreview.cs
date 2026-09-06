using System.Drawing;
using Rhino;
using Rhino.Display;
using Rhino.Geometry;
using RhinoRoad.Core;

namespace RhinoRoad.Rhino.Services;

/// <summary>Cache the retained journey; rebuild it whenever easing changes the retained endpoint.</summary>
internal sealed class JourneySweepPreview
{
    private readonly IReadOnlyList<RouteSample> _route;
    private readonly JourneySweepBuilder _builder;
    private readonly double _scale;

    public JourneySweepPreview(IReadOnlyList<RouteSample> route, VehicleDefinition vehicle,
        double clearance, UnitSystem units)
    {
        _route = route;
        _builder = new JourneySweepBuilder(route, vehicle, clearance);
        _scale = RhinoMath.UnitScale(UnitSystem.Meters, units);
    }

    public void Draw(DisplayPipeline display, PlannedManoeuvreLeg planned)
    {
        display.Draw2dText("Blue: body sweep   Green: clearance   Dotted: reshaped tail", Color.DimGray,
            new Point2d(24, 35), false, 14);
        var sweep = _builder.Build(planned);
        if (!sweep.Body.IsSuccess || !sweep.Clearance.IsSuccess)
        {
            display.Draw2dText("Sweep preview unavailable", Color.OrangeRed, new Point2d(24, 85), false, 16);
            return;
        }
        DrawRegion(display, sweep.Clearance.Region!, Color.SeaGreen, 2);
        DrawRegion(display, sweep.Body.Region!, Color.SteelBlue, 2);
        if (sweep.Retained is not null) DrawRegion(display, sweep.Retained, Color.RoyalBlue, 1);
    }

    private void DrawRegion(DisplayPipeline display, SweptRegion region, Color colour, int width)
    {
        foreach (var loop in new[] { region.OuterBoundary }.Concat(region.Holes))
        {
            var points = loop.Select(p => new Point3d(p.X * _scale, p.Y * _scale,
                _route[0].PositionMetres.Z * _scale)).ToList();
            points.Add(points[0]);
            display.DrawPolyline(points, colour, width);
        }
    }
}
