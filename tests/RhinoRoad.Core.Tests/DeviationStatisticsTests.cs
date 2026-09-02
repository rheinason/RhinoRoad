using RhinoRoad.Core;

namespace RhinoRoad.Core.Tests;

public sealed class DeviationStatisticsTests
{
    private const double Tolerance = 0.10;

    /// <summary>A boundary that agrees everywhere except a handful of vertices.</summary>
    private static double[] LocalisedArtefact(int count, int spikes, double spikeSize) =>
        [.. Enumerable.Repeat(0.03, count - spikes), .. Enumerable.Repeat(spikeSize, spikes)];

    [Fact]
    public void DistinguishesAFewBadVerticesFromAWhollyWrongShape()
    {
        var localised = DeviationStatistics.From(LocalisedArtefact(1000, 2, 1.536), Tolerance);
        var wrongShape = DeviationStatistics.From([.. Enumerable.Repeat(3.0, 960), .. Enumerable.Repeat(0.03, 40)], Tolerance);

        // No verdict is derived from these numbers; the distribution is what distinguishes them.
        Assert.True(localised.ShareOverToleranceFraction < 0.01);
        Assert.True(wrongShape.ShareOverToleranceFraction > 0.90);

        // Both report a large maximum; only the distribution tells them apart.
        Assert.True(localised.MaximumMetres > Tolerance);
        Assert.True(wrongShape.MaximumMetres > Tolerance);
        Assert.True(localised.MedianMetres < Tolerance);
        Assert.True(wrongShape.MedianMetres > Tolerance);
    }

    [Fact]
    public void ReportsTheShareOfBoundaryOutsideTolerance()
    {
        var stats = DeviationStatistics.From(LocalisedArtefact(1000, 5, 0.5), Tolerance);

        Assert.Equal(0.005, stats.ShareOverToleranceFraction, 6);
        Assert.Equal(0.5, stats.MaximumMetres, 6);
        Assert.Equal(0.03, stats.MedianMetres, 6);
    }

    [Fact]
    public void ACleanCaseHasNothingOutsideTolerance()
    {
        // PV measures like this: everything well inside, nothing over the line.
        var stats = DeviationStatistics.From([.. Enumerable.Range(0, 500).Select(i => 0.01 + (i * 0.00008))], Tolerance);

        Assert.Equal(0.0, stats.ShareOverToleranceFraction, 6);
        Assert.True(stats.MaximumMetres <= Tolerance);
        Assert.True(stats.Percentile99Metres <= stats.MaximumMetres);
        Assert.True(stats.MedianMetres <= stats.Percentile90Metres);
    }

    [Fact]
    public void PercentilesAreOrdered()
    {
        var stats = DeviationStatistics.From([.. Enumerable.Range(0, 1000).Select(i => (double)i / 1000.0)], Tolerance);

        Assert.True(stats.MedianMetres <= stats.Percentile90Metres);
        Assert.True(stats.Percentile90Metres <= stats.Percentile99Metres);
        Assert.True(stats.Percentile99Metres <= stats.MaximumMetres);
    }

    [Fact]
    public void AnEmptySampleIsZeroedRatherThanUndefined()
    {
        var stats = DeviationStatistics.From([], Tolerance);

        Assert.Equal(0.0, stats.MaximumMetres);
        Assert.Equal(0.0, stats.ShareOverToleranceFraction);
    }
}
