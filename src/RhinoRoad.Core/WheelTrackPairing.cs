namespace RhinoRoad.Core;

public sealed record WheelTrackPairingResult(
    bool IsSuccess,
    IReadOnlyList<Point2> PairedFrontVertices,
    double FrontLengthConsumedFraction,
    double WorstChordErrorMetres,
    string Message);

/// <summary>
/// Resolves the correspondence between the two outer wheel-edge traces when the source drawing
/// tessellates them at different densities.
/// </summary>
/// <remarks>
/// The traces are captured simultaneously, not at equal arc length — in a turn the front edge
/// travels measurably farther than the rear — so pairing by normalised arc length is wrong.
/// What does hold at every instant is the rigid-body chord: the front point lies exactly
/// <c>sqrt(L^2 + 4e^2)</c> from the rear point (see <see cref="WheelTrackRouteReconstructor"/>).
/// For each rear vertex this class therefore intersects a circle of that radius with the front
/// polyline, advancing monotonically so the correspondence cannot double back.
///
/// Because the chord is imposed at each paired vertex, this path weakens the separation check
/// that guards vertex-paired input — it still binds between vertices, where the reconstructor
/// interpolates, but no longer at the vertices themselves. It is validated instead by existence
/// (every rear vertex must admit a forward solution) and by coverage (the walk must consume most
/// of the front trace); traces belonging to different cases fail both.
/// </remarks>
public static class WheelTrackPairing
{
    /// <summary>A pairing that leaves much of the front trace unvisited is not a correspondence.</summary>
    public const double MinimumFrontCoverageFraction = 0.60;

    /// <summary>
    /// How far into the front trace the first pairing may sit. Two traces of the same pass begin at
    /// the same instant, so the first rear vertex pairs at the front trace's start. A correspondence
    /// that only begins a third of the way along is chord-consistent by coincidence, not a pair.
    /// </summary>
    public const double MaximumLeadInFraction = 0.05;

    /// <summary>Slack admitted on segment parameters so exact vertex solutions are not lost to rounding.</summary>
    private const double ParameterEpsilon = 1e-9;

    public static WheelTrackPairingResult Resolve(
        IReadOnlyList<Point2> frontVertices,
        IReadOnlyList<Point2> rearVertices,
        double separationMetres,
        double chordToleranceMetres = 0.15)
    {
        ArgumentNullException.ThrowIfNull(frontVertices);
        ArgumentNullException.ThrowIfNull(rearVertices);

        if (frontVertices.Count < 2 || rearVertices.Count < 2)
        {
            return Failed("Both traces need at least two vertices to pair.");
        }

        if (separationMetres <= 0.0)
        {
            return Failed($"The expected chord ({separationMetres:0.000} m) must be positive.");
        }

        var paired = new List<Point2>(rearVertices.Count);
        var segment = 0;
        var parameter = 0.0;
        var worstChordError = 0.0;

        for (var index = 0; index < rearVertices.Count; index++)
        {
            if (!TryAdvance(
                    frontVertices,
                    rearVertices[index],
                    separationMetres,
                    chordToleranceMetres,
                    ref segment,
                    ref parameter,
                    out var point,
                    out var chordError))
            {
                return Failed(
                    $"No point on the front trace lies within {chordToleranceMetres:0.000} m of the " +
                    $"{separationMetres:0.000} m chord from rear vertex {index} of {rearVertices.Count} " +
                    "while advancing along the trace. The two traces do not describe the same pass of " +
                    "the same vehicle.");
            }

            if (index == 0)
            {
                var leadIn = ConsumedFraction(frontVertices, segment, parameter);
                if (leadIn > MaximumLeadInFraction)
                {
                    return Failed(
                        $"The first rear vertex pairs {leadIn * 100.0:0.0}% of the way along the front " +
                        $"trace, past the {MaximumLeadInFraction * 100.0:0.0}% a shared start allows. " +
                        "The traces are chord-consistent but begin at different points of the " +
                        "manoeuvre, so they are not a matched pair.");
                }
            }

            worstChordError = Math.Max(worstChordError, chordError);
            paired.Add(point);
        }

        var consumed = ConsumedFraction(frontVertices, segment, parameter);
        if (consumed < MinimumFrontCoverageFraction)
        {
            return new WheelTrackPairingResult(
                false,
                [],
                consumed,
                worstChordError,
                $"Pairing consumed only {consumed * 100.0:0.0}% of the front trace, below the " +
                $"{MinimumFrontCoverageFraction * 100.0:0.0}% required; the traces are unlikely to be a matched pair.");
        }

        return new WheelTrackPairingResult(
            true,
            paired,
            consumed,
            worstChordError,
            $"Paired {rearVertices.Count} rear vertices against a {frontVertices.Count}-vertex front trace, " +
            $"consuming {consumed * 100.0:0.0}% of it; worst chord error {worstChordError:0.000} m.");
    }

    /// <summary>
    /// Finds the next point along the front polyline whose distance from <paramref name="centre"/>
    /// matches <paramref name="radius"/>, never stepping backwards from the current position.
    /// </summary>
    /// <remarks>
    /// An exact circle crossing is preferred. Where none exists in the forward span — which happens
    /// at a trace endpoint, where the published traces are trimmed slightly differently and the
    /// chord misses by millimetres — the closest approach is accepted if it lands inside
    /// <paramref name="chordTolerance"/>. That is the same slack vertex-paired input already gets,
    /// and the residual is reported so a genuine mismatch cannot hide inside it.
    /// </remarks>
    private static bool TryAdvance(
        IReadOnlyList<Point2> front,
        Point2 centre,
        double radius,
        double chordTolerance,
        ref int segment,
        ref double parameter,
        out Point2 point,
        out double chordError)
    {
        // Pass one: an exact crossing anywhere ahead always wins. Searching the whole remaining
        // trace first stops a near miss on an early segment from shadowing a true crossing later.
        for (var index = segment; index < front.Count - 1; index++)
        {
            var start = front[index];
            var direction = front[index + 1] - start;
            var offset = start - centre;
            var a = (direction.X * direction.X) + (direction.Y * direction.Y);
            if (a <= Geometry2D.Epsilon) continue;

            var b = 2.0 * ((direction.X * offset.X) + (direction.Y * offset.Y));
            var c = (offset.X * offset.X) + (offset.Y * offset.Y) - (radius * radius);
            var discriminant = (b * b) - (4.0 * a * c);
            if (discriminant < 0.0) continue;

            var root = Math.Sqrt(discriminant);
            var lower = (-b - root) / (2.0 * a);
            var upper = (-b + root) / (2.0 * a);
            var floor = index == segment ? parameter : 0.0;
            foreach (var candidate in lower <= upper ? new[] { lower, upper } : [upper, lower])
            {
                // Exact solutions land on a vertex, where rounding can put the root a few ulps
                // outside [0,1] or below the floor. Admit that slack, then clamp back onto the
                // segment so the walk stays monotonic.
                if (candidate < floor - ParameterEpsilon || candidate > 1.0 + ParameterEpsilon) continue;
                var resolved = Math.Max(Math.Clamp(candidate, 0.0, 1.0), floor);
                segment = index;
                parameter = resolved;
                point = start + (direction * resolved);
                chordError = Math.Abs(point.DistanceTo(centre) - radius);
                return true;
            }
        }

        // Pass two: no crossing exists ahead. Distance to a point varies convexly along a segment,
        // so the closest approach is at the extremum or at an end of the admissible span. Take the
        // earliest one that lands inside tolerance.
        for (var index = segment; index < front.Count - 1; index++)
        {
            var start = front[index];
            var direction = front[index + 1] - start;
            var offset = start - centre;
            var a = (direction.X * direction.X) + (direction.Y * direction.Y);
            if (a <= Geometry2D.Epsilon) continue;

            var b = 2.0 * ((direction.X * offset.X) + (direction.Y * offset.Y));
            var floor = index == segment ? parameter : 0.0;
            var extremum = Math.Max(Math.Clamp(-b / (2.0 * a), 0.0, 1.0), floor);
            var best = double.MaxValue;
            var bestParameter = floor;
            foreach (var candidate in new[] { floor, extremum, 1.0 })
            {
                var error = Math.Abs((start + (direction * candidate)).DistanceTo(centre) - radius);
                if (error >= best) continue;
                best = error;
                bestParameter = candidate;
            }

            if (best > chordTolerance) continue;
            segment = index;
            parameter = bestParameter;
            point = start + (direction * bestParameter);
            chordError = best;
            return true;
        }

        point = default;
        chordError = double.NaN;
        return false;
    }

    private static double ConsumedFraction(IReadOnlyList<Point2> front, int segment, double parameter)
    {
        var total = 0.0;
        var walked = 0.0;
        for (var index = 0; index < front.Count - 1; index++)
        {
            var length = front[index].DistanceTo(front[index + 1]);
            total += length;
            if (index < segment) walked += length;
            else if (index == segment) walked += length * parameter;
        }

        return total <= Geometry2D.Epsilon ? 0.0 : walked / total;
    }

    private static WheelTrackPairingResult Failed(string message) => new(false, [], 0.0, 0.0, message);
}
