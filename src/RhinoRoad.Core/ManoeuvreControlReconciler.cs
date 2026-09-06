namespace RhinoRoad.Core;

/// <summary>Maps edited polyline vertices back to ordered controls without losing their intent.</summary>
public static class ManoeuvreControlReconciler
{
    /// <summary>
    /// Changes the manoeuvre's initial travel direction while preserving its reversal pattern.
    /// Every control is flipped together; changing only the header would be overwritten by the
    /// first control as soon as replay begins.
    /// </summary>
    public static ManoeuvreDefinition WithStartDirection(
        ManoeuvreDefinition stored,
        TravelDirection direction)
    {
        ArgumentNullException.ThrowIfNull(stored);
        if (stored.StartDirection == direction) return stored;
        return stored with
        {
            StartDirection = direction,
            Controls = stored.Controls.Select(control => control with
            {
                Direction = control.Direction == TravelDirection.Forward
                    ? TravelDirection.Reverse
                    : TravelDirection.Forward
            }).ToArray()
        };
    }

    public static ManoeuvreDefinition Reconcile(
        ManoeuvreDefinition stored,
        IReadOnlyList<Point3> editedVertices)
    {
        ArgumentNullException.ThrowIfNull(stored);
        ArgumentNullException.ThrowIfNull(editedVertices);
        if (editedVertices.Count < 2) throw new InvalidDataException("The control route needs a start and at least one control.");

        var oldControls = stored.Controls;
        var points = editedVertices.Skip(1).ToArray();
        var matches = MonotonicMatches(oldControls.Select(item => item.PositionMetres).ToArray(), points);
        var byNewIndex = matches.ToDictionary(pair => pair.NewIndex, pair => pair.OldIndex);
        var reconciled = new List<ManoeuvreControl>(points.Length);
        for (var index = 0; index < points.Length; index++)
        {
            if (byNewIndex.TryGetValue(index, out var oldIndex))
            {
                var old = oldControls[oldIndex];
                reconciled.Add(old with { PositionMetres = points[index] });
            }
            else
            {
                var direction = reconciled.Count > 0 ? reconciled[^1].Direction : stored.StartDirection;
                reconciled.Add(new ManoeuvreControl(points[index], direction, ManoeuvreControlKind.Aim));
            }
        }

        // Finish is meaningful only at the route end. A formerly final control displaced by an
        // inserted vertex becomes an ordinary aim; a matched final Finish remains Finish.
        for (var index = 0; index < reconciled.Count - 1; index++)
            if (reconciled[index].Kind == ManoeuvreControlKind.Finish)
                reconciled[index] = reconciled[index] with { Kind = ManoeuvreControlKind.Aim };

        return stored with
        {
            StartPositionMetres = editedVertices[0],
            Controls = reconciled
        };
    }

    private static IReadOnlyList<(int OldIndex, int NewIndex)> MonotonicMatches(
        IReadOnlyList<Point3> oldPoints,
        IReadOnlyList<Point3> newPoints)
    {
        var oldCount = oldPoints.Count;
        var newCount = newPoints.Count;
        var cost = new double[oldCount + 1, newCount + 1];
        var step = new byte[oldCount + 1, newCount + 1];
        // Always preserve as many ordered controls as the two polylines have in common. Geometry
        // decides *which* vertices are insertions/deletions, but a large grip move must not turn a
        // known Reverse or Finish into a brand-new Aim merely because it travelled several metres.
        var greatestDistance = oldPoints.SelectMany(oldPoint => newPoints.Select(newPoint => Distance(oldPoint, newPoint)))
            .DefaultIfEmpty(0.0).Max();
        var gap = greatestDistance + 1.0;
        for (var i = 1; i <= oldCount; i++) { cost[i, 0] = i * gap; step[i, 0] = 1; }
        for (var j = 1; j <= newCount; j++) { cost[0, j] = j * gap; step[0, j] = 2; }
        for (var i = 1; i <= oldCount; i++)
        for (var j = 1; j <= newCount; j++)
        {
            var match = cost[i - 1, j - 1] + Distance(oldPoints[i - 1], newPoints[j - 1]);
            var remove = cost[i - 1, j] + gap;
            var insert = cost[i, j - 1] + gap;
            if (match <= remove && match <= insert) { cost[i, j] = match; step[i, j] = 0; }
            else if (remove <= insert) { cost[i, j] = remove; step[i, j] = 1; }
            else { cost[i, j] = insert; step[i, j] = 2; }
        }

        var matches = new List<(int, int)>();
        var oi = oldCount;
        var nj = newCount;
        while (oi > 0 || nj > 0)
        {
            var action = step[oi, nj];
            if (oi > 0 && nj > 0 && action == 0) { matches.Add((oi - 1, nj - 1)); oi--; nj--; }
            else if (oi > 0 && (nj == 0 || action == 1)) oi--;
            else nj--;
        }
        matches.Reverse();
        return matches;
    }

    private static double Distance(Point3 first, Point3 second)
    {
        var dx = first.X - second.X;
        var dy = first.Y - second.Y;
        var dz = first.Z - second.Z;
        return Math.Sqrt((dx * dx) + (dy * dy) + (dz * dz));
    }
}
