namespace RhinoRoad.Core;

/// <summary>
/// Adds and removes controls of a saved journey. Grips can only move a vertex, so before these a
/// control could be added or dropped only by redrawing the road. Each edit returns a journey whose
/// start and control positions are the new control line's vertices, one for one, so the polyline
/// written back and the stored controls stay in the step <see cref="ManoeuvreControlReconciler"/>
/// and <see cref="JourneyEditPreview"/> expect to find them in.
/// </summary>
public static class ManoeuvreControlEdits
{
    /// <summary>How far past the end an appended control is placed, along the last leg.</summary>
    public const double AppendedControlMetres = 5.0;

    /// <summary>
    /// Inserts a plain aim so that it becomes control <paramref name="index"/>: zero puts it before
    /// the first control, <c>Controls.Count</c> after the last.
    /// </summary>
    /// <remarks>
    /// Between two points the new control halves the leg it splits and is driven the same way, in
    /// the same direction and at the same speed, so the reversal pattern the user drove is kept;
    /// past the end it continues the last leg, at its speed. Finish
    /// only means something on the last control, so appending after a Finish moves it to the new end.
    /// </remarks>
    public static ManoeuvreDefinition Insert(ManoeuvreDefinition journey, int index)
    {
        ArgumentNullException.ThrowIfNull(journey);
        var controls = journey.Controls.ToList();
        if (index < 0 || index > controls.Count) throw new ArgumentOutOfRangeException(nameof(index));

        var from = index == 0 ? journey.StartPositionMetres : controls[index - 1].PositionMetres;
        ManoeuvreControl inserted;
        if (index < controls.Count)
        {
            var to = controls[index].PositionMetres;
            inserted = new ManoeuvreControl(
                new Point3((from.X + to.X) / 2.0, (from.Y + to.Y) / 2.0, (from.Z + to.Z) / 2.0),
                controls[index].Direction,
                SpeedKilometresPerHour: controls[index].SpeedKilometresPerHour);
        }
        else
        {
            var last = controls.Count == 0 ? null : controls[^1];
            inserted = new ManoeuvreControl(
                Beyond(journey, from),
                last?.Direction ?? journey.StartDirection,
                last?.Kind == ManoeuvreControlKind.Finish ? ManoeuvreControlKind.Finish : ManoeuvreControlKind.Aim,
                SpeedKilometresPerHour: last?.SpeedKilometresPerHour);
            if (last?.Kind == ManoeuvreControlKind.Finish)
                controls[^1] = last with { Kind = ManoeuvreControlKind.Aim };
        }
        controls.Insert(index, inserted);
        return journey with { Controls = controls };
    }

    /// <summary>
    /// Removes control <paramref name="index"/>. A road keeps at least one control, and a Finish
    /// removed from the end passes to the control that becomes last, when that one is a plain aim.
    /// </summary>
    public static ManoeuvreDefinition Delete(ManoeuvreDefinition journey, int index)
    {
        ArgumentNullException.ThrowIfNull(journey);
        var controls = journey.Controls.ToList();
        if (index < 0 || index >= controls.Count) throw new ArgumentOutOfRangeException(nameof(index));
        if (controls.Count == 1) throw new InvalidOperationException("A road needs at least one control.");

        var removed = controls[index];
        controls.RemoveAt(index);
        if (index == controls.Count && removed.Kind == ManoeuvreControlKind.Finish &&
            controls[^1].Kind == ManoeuvreControlKind.Aim)
            controls[^1] = controls[^1] with { Kind = ManoeuvreControlKind.Finish };
        return journey with { Controls = controls };
    }

    /// <summary>The crawl speeds offered for a leg, in km/h, wherever they are slower than the mode.</summary>
    public static readonly IReadOnlyList<double> CrawlSpeedsKilometresPerHour = [5.0, 3.0, 1.0];

    /// <summary>
    /// The speeds a control can be given in <paramref name="mode"/>, fastest first: null for the
    /// mode's own, then each crawl speed slower than it, and the control's own speed when it is none
    /// of those -- set from Grasshopper or typed in the pick loop -- so a choice list can show what
    /// the control holds rather than silently replacing it.
    /// </summary>
    public static IReadOnlyList<double?> SpeedChoices(DrivingModeDefinition mode, double? current)
    {
        ArgumentNullException.ThrowIfNull(mode);
        var speeds = CrawlSpeedsKilometresPerHour.Where(speed => speed < mode.SpeedKilometresPerHour).ToList();
        if (current is double own && !speeds.Contains(own)) speeds.Add(own);
        return new double?[] { null }.Concat(speeds.OrderByDescending(speed => speed).Select(speed => (double?)speed)).ToArray();
    }

    /// <summary>
    /// A point a few metres past <paramref name="end"/>, continuing the line into it. A road with a
    /// single zero-length leg has no line to continue, so it follows the start heading instead.
    /// </summary>
    private static Point3 Beyond(ManoeuvreDefinition journey, Point3 end)
    {
        var previous = journey.Controls.Count >= 2 ? journey.Controls[^2].PositionMetres : journey.StartPositionMetres;
        var dx = end.X - previous.X;
        var dy = end.Y - previous.Y;
        var length = Math.Sqrt((dx * dx) + (dy * dy));
        if (journey.Controls.Count == 0 || length <= 1e-6)
        {
            dx = Math.Cos(journey.StartHeadingRadians);
            dy = Math.Sin(journey.StartHeadingRadians);
            length = 1.0;
        }
        var scale = AppendedControlMetres / length;
        return new Point3(end.X + (dx * scale), end.Y + (dy * scale), end.Z);
    }
}
