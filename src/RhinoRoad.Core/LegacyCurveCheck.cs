namespace RhinoRoad.Core;

/// <summary>A run of a reference boundary that is circular to within a stated tolerance.</summary>
public sealed record SustainedArc(
    Point2 CentreMetres,
    double RadiusMetres,
    double FitDeviationMetres,
    int PointCount);

/// <summary>
/// The annulus a reference curve sheet holds where the vehicle is turning steadily: the tightest
/// circular run on each of its two boundaries.
/// </summary>
/// <remarks>
/// The inner boundary is found first, because it is the one with an unmistakable apex: it is the
/// tightest circular run on the boundary that rides the inside of the corner. The outer boundary is
/// then the run that is <em>concentric with it</em> — selected by shared centre rather than by
/// radius, because an outer boundary is mostly straight legs and gentle transitions, and asking for
/// the tightest run on one of those finds whatever ten-point wobble curls hardest and calls it the
/// turn.
/// <para>
/// <see cref="CentreGapMetres"/> is what that selection achieved, not an independent check on it.
/// The independent check is that both radii imply the same rear-axle radius; see
/// <see cref="ImpliedRearAxleRadiusMetres"/>.
/// </para>
/// </remarks>
public sealed record ReferenceAnnulus(SustainedArc Inner, SustainedArc Outer)
{
    public double CentreGapMetres => Inner.CentreMetres.DistanceTo(Outer.CentreMetres);

    public bool IsSteady(double maximumCentreGapMetres = 0.10) => CentreGapMetres <= maximumCentreGapMetres;

    /// <summary>
    /// Rear-axle radius each boundary implies, given the vehicle it was drawn for. The outer
    /// boundary is swept by the leading unit's outer front corner, a distance
    /// <c>wheelbase + front overhang</c> ahead of the axle and half a body width to the side; the
    /// inner boundary, on every one of these presets, is the body's inner flank at half a width in.
    /// Two numbers, one turn — they have to agree, and they are measured from opposite sides of the
    /// vehicle by different parts of it, so agreement is not something a mis-fit can fake.
    /// </summary>
    public (double FromOuter, double FromInner) ImpliedRearAxleRadiusMetres(VehicleDefinition vehicle)
    {
        ArgumentNullException.ThrowIfNull(vehicle);
        var reach = vehicle.WheelbaseMetres + vehicle.FrontOverhangMetres;
        var half = vehicle.WidthMetres / 2.0;
        var squared = (Outer.RadiusMetres * Outer.RadiusMetres) - (reach * reach);
        return (squared > 0.0 ? Math.Sqrt(squared) - half : double.NaN, Inner.RadiusMetres + half);
    }
}

/// <summary>
/// What is wrong with one reference curve sheet, if anything.
/// </summary>
/// <param name="Defects">
/// Problems that change what the sheet means — a third boundary chain, a boundary crossing itself, a
/// start that is not on the approach tangent. A number measured from these is measuring something
/// other than the vehicle.
/// </param>
/// <param name="Blemishes">
/// Problems that do not change the curve, such as a vertex stored twice. Worth recording so the
/// conversion quality is visible, but not a reason to throw the sheet away.
/// </param>
public sealed record ReferenceCurveHealth(IReadOnlyList<string> Defects, IReadOnlyList<string> Blemishes)
{
    public bool IsUsable => Defects.Count == 0;
}

/// <summary>
/// Structural checks on the legacy Vejdirektoratet curve sheets, and the steady-turn annulus they
/// hold.
/// </summary>
/// <remarks>
/// The library is a GIS conversion of the official drawings and parts of it did not survive: some
/// sheets carry a third boundary chain, some self-intersect, some repeat a point. Geometry like that
/// still measures — it just measures the wrong thing, and a comparison run over it reports the
/// preset as wrong when the reference is. Everything here exists to separate those cases out before
/// any number is taken from them.
///
/// <para>
/// The frame is the one the source plugin placed these in: the origin at the intersection of the two
/// travel tangents, the approach running along −X, and the turn to the right.
/// </para>
/// </remarks>
public static class LegacyCurveCheck
{
    /// <summary>Longest segment allowed before a chain counts as having a gap in it.</summary>
    private const double MaximumSegmentMetres = 4.0;

    /// <summary>How far the two boundaries may disagree about where the approach tangent starts.</summary>
    private const double StationToleranceMetres = 0.05;

    /// <summary>How far the approach band may differ from the body width.</summary>
    private const double WidthToleranceMetres = 0.03;

    public static ReferenceCurveHealth Inspect(VejReferenceTemplate template, double bodyWidthMetres)
    {
        ArgumentNullException.ThrowIfNull(template);
        var defects = new List<string>();
        var blemishes = new List<string>();
        var chains = template.DesignEnvelopeLines;
        if (chains.Count != 2)
        {
            defects.Add($"the sheet carries {chains.Count} envelope boundaries rather than 2");
            return new ReferenceCurveHealth(defects, blemishes);
        }

        for (var index = 0; index < chains.Count; index++)
        {
            var chain = chains[index];
            if (chain.Count < 20)
            {
                defects.Add($"boundary {index} has only {chain.Count} points");
                continue;
            }

            for (var i = 0; i + 1 < chain.Count; i++)
            {
                var step = chain[i].DistanceTo(chain[i + 1]);
                if (step <= Geometry2D.Epsilon) blemishes.Add($"boundary {index} repeats a point at index {i}");
                if (step > MaximumSegmentMetres) defects.Add($"boundary {index} jumps {step:0.00} m at index {i}");
            }

            var crossings = SelfIntersections(chain);
            if (crossings > 0) defects.Add($"boundary {index} self-intersects {crossings} time(s)");
        }

        // Both boundaries must begin on the approach tangent, at the same station, one body width
        // apart, and heading -X. A sheet that fails this is not in the frame it is assumed to be in.
        var starts = chains.Select(Oriented).ToArray();
        foreach (var (chain, index) in starts.Select((c, i) => (c, i)))
        {
            var heading = Math.Atan2(chain[1].Y - chain[0].Y, chain[1].X - chain[0].X);
            var offAxis = Math.Abs(Geometry2D.NormalizeAngle(heading - Math.PI));
            if (offAxis > 0.02) defects.Add($"boundary {index} does not start along the approach tangent");
        }

        if (Math.Abs(starts[0][0].X - starts[1][0].X) > StationToleranceMetres)
        {
            defects.Add("the two boundaries start at different stations on the approach");
        }

        var band = Math.Abs(starts[0][0].Y - starts[1][0].Y);
        if (Math.Abs(band - bodyWidthMetres) > WidthToleranceMetres)
        {
            defects.Add($"the approach band is {band:0.000} m against a {bodyWidthMetres:0.000} m body");
        }

        return new ReferenceCurveHealth(defects, blemishes);
    }

    /// <summary>
    /// The steady-turn annulus: the apex of the inner boundary, and the run of the outer boundary
    /// concentric with it. Null when either cannot be resolved.
    /// </summary>
    public static ReferenceAnnulus? Annulus(
        VejReferenceTemplate template,
        double toleranceMetres = 0.015,
        int minimumPoints = 10)
    {
        ArgumentNullException.ThrowIfNull(template);
        var boundaries = Boundaries(template);
        if (boundaries is null) return null;
        var inner = TightestCircularRun(boundaries.Value.Inner, toleranceMetres, minimumPoints);
        if (inner is null) return null;
        var outer = MostConcentricRun(boundaries.Value.Outer, inner.CentreMetres, toleranceMetres, minimumPoints);
        return outer is null ? null : new ReferenceAnnulus(inner, outer);
    }

    /// <summary>
    /// The two boundaries, told apart by which side of the approach tangent they run on. The source
    /// is a right turn about an origin ahead of the vehicle, so the boundary sitting at positive Y on
    /// the approach is the one on the inside of the corner. Ordering in the file is not reliable —
    /// some sheets store the outer boundary first — so it is never used.
    /// </summary>
    public static (IReadOnlyList<Point2> Inner, IReadOnlyList<Point2> Outer)? Boundaries(VejReferenceTemplate template)
    {
        ArgumentNullException.ThrowIfNull(template);
        if (template.DesignEnvelopeLines.Count != 2) return null;
        var oriented = template.DesignEnvelopeLines.Select(Oriented).ToArray();
        if (oriented[0].Count < 2 || oriented[1].Count < 2) return null;
        return oriented[0][0].Y > oriented[1][0].Y
            ? (oriented[0], oriented[1])
            : (oriented[1], oriented[0]);
    }

    /// <summary>
    /// The run whose fitted centre sits closest to <paramref name="centre"/>. Ties on centre go to
    /// the longer run, which is the one carrying more evidence.
    /// </summary>
    public static SustainedArc? MostConcentricRun(
        IReadOnlyList<Point2> chain,
        Point2 centre,
        double toleranceMetres = 0.015,
        int minimumPoints = 10)
    {
        ArgumentNullException.ThrowIfNull(chain);
        SustainedArc? best = null;
        var bestGap = double.PositiveInfinity;
        for (var start = 0; start + minimumPoints <= chain.Count; start++)
        {
            for (var end = start + minimumPoints; end <= chain.Count; end++)
            {
                var fit = FitCircle(chain, start, end);
                if (fit is null || fit.FitDeviationMetres > toleranceMetres) break;
                var gap = centre.DistanceTo(fit.CentreMetres);
                if (gap > bestGap + 1e-9) continue;
                if (gap < bestGap - 1e-9 || best is null || fit.PointCount > best.PointCount)
                {
                    best = fit;
                    bestGap = gap;
                }
            }
        }

        return best;
    }

    /// <summary>
    /// The smallest-radius run of consecutive points that fits a circle within
    /// <paramref name="toleranceMetres"/>. Smallest rather than longest: a boundary is mostly
    /// straight legs and gentle transitions, all of which fit a large circle beautifully, so
    /// preferring the longest run finds a tangent and calls it a turn.
    /// </summary>
    public static SustainedArc? TightestCircularRun(
        IReadOnlyList<Point2> chain,
        double toleranceMetres = 0.015,
        int minimumPoints = 10)
    {
        ArgumentNullException.ThrowIfNull(chain);
        SustainedArc? best = null;
        for (var start = 0; start + minimumPoints <= chain.Count; start++)
        {
            for (var end = start + minimumPoints; end <= chain.Count; end++)
            {
                var fit = FitCircle(chain, start, end);
                if (fit is null || fit.FitDeviationMetres > toleranceMetres) break;
                if (best is null || fit.RadiusMetres < best.RadiusMetres) best = fit;
            }
        }

        return best;
    }

    /// <summary>Least-squares circle through <c>chain[start..end)</c>, by the algebraic method.</summary>
    private static SustainedArc? FitCircle(IReadOnlyList<Point2> chain, int start, int end)
    {
        var count = end - start;
        double sx = 0, sy = 0, sxx = 0, syy = 0, sxy = 0, sxxx = 0, syyy = 0, sxyy = 0, sxxy = 0;
        for (var i = start; i < end; i++)
        {
            var (x, y) = (chain[i].X, chain[i].Y);
            sx += x; sy += y; sxx += x * x; syy += y * y; sxy += x * y;
            sxxx += x * x * x; syyy += y * y * y; sxyy += x * y * y; sxxy += x * x * y;
        }

        var c = (count * sxx) - (sx * sx);
        var d = (count * sxy) - (sx * sy);
        var e = (count * sxxx) + (count * sxyy) - ((sxx + syy) * sx);
        var g = (count * syy) - (sy * sy);
        var h = (count * sxxy) + (count * syyy) - ((sxx + syy) * sy);
        var denominator = 2.0 * ((c * g) - (d * d));
        if (Math.Abs(denominator) < 1e-12) return null;

        var centre = new Point2(((e * g) - (d * h)) / denominator, ((c * h) - (d * e)) / denominator);
        var radius = 0.0;
        for (var i = start; i < end; i++) radius += centre.DistanceTo(chain[i]);
        radius /= count;
        var deviation = 0.0;
        for (var i = start; i < end; i++) deviation = Math.Max(deviation, Math.Abs(centre.DistanceTo(chain[i]) - radius));
        return new SustainedArc(centre, radius, deviation, count);
    }

    /// <summary>Runs a boundary approach-first, whichever way round the library happens to store it.</summary>
    private static IReadOnlyList<Point2> Oriented(IReadOnlyList<Point2> chain)
    {
        if (chain.Count < 2) return chain;
        var forward = Math.Abs(Geometry2D.NormalizeAngle(
            Math.Atan2(chain[1].Y - chain[0].Y, chain[1].X - chain[0].X) - Math.PI));
        var backward = Math.Abs(Geometry2D.NormalizeAngle(
            Math.Atan2(chain[^2].Y - chain[^1].Y, chain[^2].X - chain[^1].X) - Math.PI));
        return backward < forward ? chain.Reverse().ToArray() : chain;
    }

    private static int SelfIntersections(IReadOnlyList<Point2> chain)
    {
        var count = 0;
        for (var i = 0; i + 1 < chain.Count; i++)
        {
            for (var j = i + 2; j + 1 < chain.Count; j++)
            {
                if (Geometry2D.SegmentsIntersect(chain[i], chain[i + 1], chain[j], chain[j + 1])) count++;
            }
        }

        return count;
    }
}
