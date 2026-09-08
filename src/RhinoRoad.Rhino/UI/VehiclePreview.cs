using Eto.Drawing;
using Eto.Forms;
using RhinoRoad.Core;

namespace RhinoRoad.Rhino.UI;

/// <summary>
/// Scaled plan of the selected vehicle: body outlines, every axle, and the wheelbase it turns on.
/// </summary>
/// <remarks>
/// A dimensioned picture answers "is this the vehicle I meant?" faster than a row of numbers, and
/// makes a mistranscribed preset visible — a wheelbase in the wrong place looks wrong immediately.
/// Drawn in plan because that is the view every output of this tool is in. A combination is drawn
/// standing straight, with its coupling marked, because that is the one configuration its published
/// dimensions describe.
/// </remarks>
internal sealed class VehiclePreview : Drawable
{
    private VehicleDefinition? _vehicle;

    public VehiclePreview()
    {
        Height = 96;
        Paint += OnPaint;
    }

    public void Show(VehicleDefinition vehicle)
    {
        _vehicle = vehicle;
        Invalidate();
    }

    private void OnPaint(object? sender, PaintEventArgs args)
    {
        var vehicle = _vehicle;
        if (vehicle is null || vehicle.BodyOutline.Count < 3) return;

        var graphics = args.Graphics;
        var bounds = args.ClipRectangle;

        // Every unit drawn in one frame: the lead rear axle at the origin, each towed axle at the
        // offset its hitch and wheelbase put it at when the combination stands straight.
        var offsets = vehicle.StraightAxleOffsetsMetres;
        var units = new List<(double Offset, IReadOnlyList<Point2> Outline, double AxleTrack, double? Hitch)>
        {
            (offsets[0], vehicle.BodyOutline, vehicle.AxleTrackMetres, null)
        };
        for (var index = 0; index < vehicle.TowedUnits.Count; index++)
        {
            var unit = vehicle.TowedUnits[index];
            units.Add((offsets[index + 1], unit.BodyOutline, unit.AxleTrackMetres,
                offsets[index] + unit.HitchOffsetMetres));
        }

        var drawn = units.Where(unit => unit.Outline.Count >= 3).ToArray();
        var minimumX = drawn.Min(unit => unit.Offset + unit.Outline.Min(point => point.X));
        var maximumX = drawn.Max(unit => unit.Offset + unit.Outline.Max(point => point.X));
        var minimumY = drawn.Min(unit => unit.Outline.Min(point => point.Y));
        var maximumY = drawn.Max(unit => unit.Outline.Max(point => point.Y));
        var lengthMetres = maximumX - minimumX;
        var widthMetres = maximumY - minimumY;
        if (lengthMetres <= 0.0 || widthMetres <= 0.0) return;

        const float margin = 14f;
        var scale = Math.Min(
            (bounds.Width - (2 * margin)) / (float)lengthMetres,
            (bounds.Height - (2 * margin)) / (float)widthMetres);
        var originX = bounds.X + margin - ((float)minimumX * scale);
        var originY = bounds.Y + (bounds.Height / 2f);

        PointF ToScreen(double x, double y) => new(originX + ((float)x * scale), originY - ((float)y * scale));

        var ink = SystemColors.ControlText;
        var accent = new Color(ink, 0.55f);
        foreach (var unit in units)
        {
            if (unit.Outline.Count >= 3)
            {
                var outline = unit.Outline.Select(point => ToScreen(unit.Offset + point.X, point.Y)).ToArray();
                graphics.FillPolygon(new Color(ink, 0.08f), outline);
                graphics.DrawPolygon(ink, outline);
            }

            var halfTrack = unit.AxleTrack * 0.5;
            graphics.DrawLine(ink, ToScreen(unit.Offset, -halfTrack), ToScreen(unit.Offset, halfTrack));

            // The coupling, drawn as the drawbar or fifth wheel it is: a line from the towing axle
            // forward or back to the hitch, then on to the axle it drags.
            if (unit.Hitch is not double hitch) continue;
            var hitchPoint = ToScreen(hitch, 0.0);
            graphics.DrawLine(accent, hitchPoint, ToScreen(unit.Offset, 0.0));
            graphics.DrawEllipse(accent, hitchPoint.X - 2.5f, hitchPoint.Y - 2.5f, 5f, 5f);
        }

        // Front axle of the lead unit, one wheelbase ahead of the origin.
        var leadHalfTrack = vehicle.AxleTrackMetres * 0.5;
        graphics.DrawLine(ink, ToScreen(vehicle.WheelbaseMetres, -leadHalfTrack), ToScreen(vehicle.WheelbaseMetres, leadHalfTrack));

        // Wheelbase dimension line between the lead unit's axles.
        var dimensionY = minimumY - (widthMetres * 0.18);
        var from = ToScreen(0.0, dimensionY);
        var to = ToScreen(vehicle.WheelbaseMetres, dimensionY);
        graphics.DrawLine(accent, from, to);
        graphics.DrawLine(accent, from.X, from.Y - 3f, from.X, from.Y + 3f);
        graphics.DrawLine(accent, to.X, to.Y - 3f, to.X, to.Y + 3f);

        var font = SystemFonts.Label(7f);
        graphics.DrawText(font, accent, (from.X + to.X) / 2f - 14f, from.Y + 2f, $"{vehicle.WheelbaseMetres:0.00} m");
        graphics.DrawText(font, accent, bounds.X + 2f, bounds.Y + 2f, $"{lengthMetres:0.00} m overall");
        graphics.DrawText(font, accent, bounds.X + 2f, bounds.Bottom - 12f, $"{vehicle.WidthMetres:0.00} m wide");
    }
}
