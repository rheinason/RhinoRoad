using RhinoRoad.Core;

namespace RhinoRoad.Core.Tests;

public sealed class ReferenceCertificationThresholdTests
{
    private static readonly ReferenceCertificationThresholds Thresholds = new(0.10, 0.05);

    [Theory]
    [InlineData(0.00, 0.00, true)]
    [InlineData(0.10, 0.05, true)]
    [InlineData(0.10, 0.00, true)]
    [InlineData(0.10000001, 0.05, false)]
    [InlineData(0.10, 0.05000001, false)]
    [InlineData(0.11, 0.00, false)]
    [InlineData(0.00, 0.06, false)]
    [InlineData(0.20, 0.20, false)]
    public void AcceptsOnlyWhenBothMeasuresSitInsideTolerance(
        double deviation,
        double inward,
        bool expected) =>
        Assert.Equal(expected, Thresholds.Accepts(deviation, inward));

    [Fact]
    public void InwardUnderpredictionIsTheStricterGate()
    {
        // A sweep can overshoot the official envelope by more than it may fall short of it.
        Assert.True(Thresholds.Accepts(0.09, 0.04));
        Assert.False(Thresholds.Accepts(0.09, 0.06));
    }

    [Fact]
    public void StoredReportThresholdsAgreeWithEveryRecordedVerdict()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Reference", "reference-certification.json");
        var report = System.Text.Json.JsonSerializer.Deserialize<ReferenceCertificationReport>(
                         File.ReadAllText(path),
                         new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                     ?? throw new InvalidDataException("The reference certification report is invalid.");

        foreach (var item in report.Cases)
        {
            var recomputed = item.MaximumEnvelopeDeviationMetres.HasValue &&
                             item.MaximumInwardUnderpredictionMetres.HasValue &&
                             report.Thresholds.Accepts(
                                 item.MaximumEnvelopeDeviationMetres.Value,
                                 item.MaximumInwardUnderpredictionMetres.Value);

            Assert.Equal(item.Passed, recomputed);
        }
    }
}
