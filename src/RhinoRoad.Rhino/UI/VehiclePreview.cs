using Eto.Drawing;
using Eto.Forms;
using RhinoRoad.Core;

namespace RhinoRoad.Rhino.UI;

/// <summary>
/// Scaled plan of the selected vehicle: body outline, both axles, and the wheelbase it turns on.
/// </summary>
/// <remarks>
/// A dimensioned picture answers "is this the vehicle I meant?" faster than a row of numbers, and
/// makes a mistranscribed preset visible — a wheelbase in the wrong place looks wrong immediately.
/// Drawn in plan because that is the view every output of this tool is in.
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
        var minimumX = vehicle.BodyOutline.Min(point => point.X);
        var maximumX = vehicle.BodyOutline.Max(point => point.X);
        var minimumY = vehicle.BodyOutline.Min(point => point.Y);
        var maximumY = vehicle.BodyOutline.Max(point => point.Y);
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
        var body = vehicle.BodyOutline.Select(point => ToScreen(point.X, point.Y)).ToArray();
        graphics.FillPolygon(new Color(ink, 0.08f), body);
        graphics.DrawPolygon(ink, body);

        // Axles: rear sits at the origin of the vehicle frame, front one wheelbase ahead.
        var halfTrack = vehicle.AxleTrackMetres * 0.5;
        graphics.DrawLine(ink, ToScreen(0.0, -halfTrack), ToScreen(0.0, halfTrack));
        graphics.DrawLine(ink, ToScreen(vehicle.WheelbaseMetres, -halfTrack), ToScreen(vehicle.WheelbaseMetres, halfTrack));

        // Wheelbase dimension line between them.
        var dimensionY = minimumY - (widthMetres * 0.18);
        var from = ToScreen(0.0, dimensionY);
        var to = ToScreen(vehicle.WheelbaseMetres, dimensionY);
        var accent = new Color(ink, 0.55f);
        graphics.DrawLine(accent, from, to);
        graphics.DrawLine(accent, from.X, from.Y - 3f, from.X, from.Y + 3f);
        graphics.DrawLine(accent, to.X, to.Y - 3f, to.X, to.Y + 3f);

        var font = SystemFonts.Label(7f);
        graphics.DrawText(font, accent, (from.X + to.X) / 2f - 14f, from.Y + 2f, $"{vehicle.WheelbaseMetres:0.00} m");
        graphics.DrawText(font, accent, bounds.X + 2f, bounds.Y + 2f, $"{lengthMetres:0.00} m overall");
        graphics.DrawText(font, accent, bounds.X + 2f, bounds.Bottom - 12f, $"{vehicle.WidthMetres:0.00} m wide");
    }
}
