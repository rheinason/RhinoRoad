using System.Drawing;
using Rhino.Display;
using Rhino.Geometry;

namespace RhinoRoad.Rhino.Services;

/// <summary>
/// Status text for one frame of a viewport, stacked from the top so concurrent messages never
/// overlap, with an optional legend along the bottom.
/// </summary>
/// <remarks>
/// Messages used to be placed at fixed pixel rows, and several states shared a row: a steering-limit
/// warning and a trailer prompt firing together printed over each other, and on a high-DPI screen
/// the rows were too close for the text they held. One instance per frame owns the layout, and every
/// distance is scaled by the pipeline's DPI factor, so a line is as tall on a 4K laptop as on a
/// 96 DPI monitor.
/// </remarks>
internal sealed class ViewportMessages
{
    private const double MarginPixels = 24.0;
    private const double TopPixels = 12.0;
    private const double GapPixels = 8.0;
    private const string FontFace = "Arial";

    private readonly DisplayPipeline _display;
    private readonly double _scale;
    private readonly HashSet<string> _shown = new();
    private double _nextTop;
    private double _nextBottom;

    public ViewportMessages(DisplayPipeline display)
    {
        _display = display;
        // DpiScale is one at 96 DPI; below one is not a screen anyone reads text on.
        _scale = Math.Max(1.0, display.DpiScale);
        _nextTop = TopPixels * _scale;
        _nextBottom = display.Viewport.Size.Height - (TopPixels * _scale);
    }

    /// <summary>
    /// Adds a line under the last one. A message already shown this frame is not repeated, because
    /// two parts of a preview can have the same thing to say about it.
    /// </summary>
    public void Add(string text, Color colour, int size = 16)
    {
        if (string.IsNullOrEmpty(text) || !_shown.Add(text)) return;
        var height = Pixels(size);
        _display.Draw2dText(text, colour, new Point2d(MarginPixels * _scale, _nextTop), false, height, FontFace);
        _nextTop += height + (GapPixels * _scale);
    }

    /// <summary>
    /// One row of coloured legend entries, laid along the bottom-left corner. Rows stack upwards, so
    /// the first row given sits lowest.
    /// </summary>
    public void Legend(IReadOnlyList<(string Text, Color Colour)> entries, int size = 13)
    {
        if (entries.Count == 0) return;
        var height = Pixels(size);
        var top = _nextBottom - height;
        var left = MarginPixels * _scale;
        foreach (var (text, colour) in entries)
        {
            var at = new Point2d(left, top);
            _display.Draw2dText(text, colour, at, false, height, FontFace);
            var extent = _display.Measure2dText(text, at, false, 0.0, height, FontFace);
            // An estimate stands in only if the pipeline cannot measure, so entries never stack.
            var width = extent.Width > 0 ? extent.Width : text.Length * height * 0.55;
            left += width + (2.0 * GapPixels * _scale);
        }

        _nextBottom = top - (GapPixels * _scale);
    }

    private int Pixels(int size) => (int)Math.Round(size * _scale);
}
