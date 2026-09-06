using System.Globalization;
using Eto.Drawing;
using Eto.Forms;
using RhinoRoad.Core;
using RhinoRoad.Rhino.Services;

namespace RhinoRoad.Rhino.UI;

/// <summary>A compact read-only station profile; moving across it scrubs the vehicle in Rhino.</summary>
internal sealed class VehicleAccessProfileControl : Drawable
{
    private const int Left = 46;
    private const int Right = 12;
    private const int Top = 12;
    private const int Bottom = 28;
    private VehicleAccessReview? _review;
    private double? _hoverStation;

    public VehicleAccessProfileControl()
    {
        Height = 172;
        MinimumSize = new Size(300, 150);
        BackgroundColor = Color.FromArgb(43, 45, 49);
        Cursor = Cursors.Crosshair;
        ToolTip = "Move along the profile to show that vehicle pose in the viewport.";
    }

    public ReviewMetricKind Metric { get; set; } = ReviewMetricKind.SteeringAngle;
    public event EventHandler<double?>? HoverStationChanged;

    public void SetReview(VehicleAccessReview? review)
    {
        _review = review;
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var graphics = e.Graphics;
        graphics.FillRectangle(BackgroundColor, 0, 0, Width, Height);
        if (_review is null || _review.Samples.Count < 2)
        {
            DrawCentered(graphics, "Pick an access analysis", Colors.Gray);
            return;
        }

        var values = _review.Samples.Select(Value).Where(double.IsFinite).ToArray();
        if (values.Length == 0) return;
        var minimum = Math.Min(values.Min(), 0.0);
        var maximum = Math.Max(values.Max(), 0.0);
        if (Math.Abs(maximum - minimum) < 1e-9) { minimum -= 1.0; maximum += 1.0; }
        var font = SystemFonts.Default(SystemFonts.Default().Size - 1f);
        for (var index = 0; index <= 4; index++)
        {
            var y = Top + PlotHeight * index / 4f;
            graphics.DrawLine(Color.FromArgb(69, 72, 77), Left, y, Left + PlotWidth, y);
            var label = maximum - (maximum - minimum) * index / 4.0;
            graphics.DrawText(font, Color.FromArgb(170, 174, 181), 3, y - 7, label.ToString("0.##", CultureInfo.InvariantCulture));
        }

        foreach (var problem in _review.ProblemEvents)
        {
            var x0 = X(problem.StartStationMetres);
            var x1 = X(problem.EndStationMetres);
            graphics.FillRectangle(new Color(0.9f, 0.25f, 0.20f, 0.14f), x0, Top, Math.Max(2, x1 - x0), PlotHeight);
        }

        for (var index = 0; index < _review.Samples.Count - 1; index++)
        {
            var a = _review.Samples[index];
            var b = _review.Samples[index + 1];
            var va = Value(a);
            var vb = Value(b);
            if (!double.IsFinite(va) || !double.IsFinite(vb)) continue;
            var colourValue = Metric == ReviewMetricKind.ClearanceOrRoadMargin ? Math.Min(va, vb) : (va + vb) * 0.5;
            using var pen = new Pen(ToEto(VehicleAccessMetricPalette.For(Metric, colourValue, _review)), 2);
            graphics.DrawLine(pen, X(a.StationMetres), Y(va, minimum, maximum), X(b.StationMetres), Y(vb, minimum, maximum));
        }

        graphics.DrawRectangle(Color.FromArgb(88, 91, 96), Left, Top, PlotWidth, PlotHeight);
        graphics.DrawText(font, Color.FromArgb(170, 174, 181), Left, Top + PlotHeight + 5,
            $"Station 0 — {_review.RouteLengthMetres:0.#} m");
        if (_hoverStation.HasValue)
        {
            var sample = _review.Samples.MinBy(item => Math.Abs(item.StationMetres - _hoverStation.Value))!;
            var x = X(sample.StationMetres);
            graphics.DrawLine(Colors.White, x, Top, x, Top + PlotHeight);
            graphics.DrawText(font, Colors.White, Math.Clamp(x - 38, Left, Left + PlotWidth - 76), Top + 4,
                $"{sample.StationMetres:0.0} m  {Value(sample):0.##}");
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (_review is not null && e.Location.X >= Left && e.Location.X <= Left + PlotWidth &&
            e.Location.Y >= Top && e.Location.Y <= Top + PlotHeight)
        {
            _hoverStation = (e.Location.X - Left) / Math.Max(1.0, PlotWidth) * _review.RouteLengthMetres;
            HoverStationChanged?.Invoke(this, _hoverStation);
            Invalidate();
        }
        base.OnMouseMove(e);
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        _hoverStation = null;
        HoverStationChanged?.Invoke(this, null);
        Invalidate();
        base.OnMouseLeave(e);
    }

    private float X(double station) => Left + (float)(station / Math.Max(_review?.RouteLengthMetres ?? 1.0, 1e-9) * PlotWidth);
    private float Y(double value, double minimum, double maximum) => Top + (float)((maximum - value) / (maximum - minimum) * PlotHeight);
    private int PlotWidth => Math.Max(0, Width - Left - Right);
    private int PlotHeight => Math.Max(0, Height - Top - Bottom);

    private double Value(VehicleAccessMetricSample sample) => VehicleAccessMetricPalette.Value(Metric, sample);

    private static Color ToEto(System.Drawing.Color color) => Color.FromArgb(color.R, color.G, color.B);

    private void DrawCentered(Graphics graphics, string text, Color color)
    {
        var font = SystemFonts.Default();
        var size = graphics.MeasureString(font, text);
        graphics.DrawText(font, color, (Width - size.Width) / 2, (Height - size.Height) / 2, text);
    }
}
