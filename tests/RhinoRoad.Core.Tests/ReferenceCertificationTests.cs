using System.Text.Json;
using RhinoRoad.Core;

namespace RhinoRoad.Core.Tests;

public sealed class ReferenceCertificationTests
{
    private static ReferenceCertificationReport LoadReport()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Reference", "reference-certification.json");
        return JsonSerializer.Deserialize<ReferenceCertificationReport>(
                   File.ReadAllText(path),
                   new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
               ?? throw new InvalidDataException("The reference certification report is invalid.");
    }

    [Fact]
    public void ReportContainsTheCompleteMvpCertificationMatrix()
    {
        var report = LoadReport();

        Assert.Equal("1.0", report.SchemaVersion);
        Assert.Equal(0.10, report.Thresholds.MaximumEnvelopeDeviationMetres, 6);
        Assert.Equal(0.05, report.Thresholds.MaximumInwardUnderpredictionMetres, 6);
        Assert.Equal(18, report.Cases.Count);
        foreach (var vehicleId in ReferenceCertificationReport.RequiredVehicleIds)
        {
            Assert.True(report.IsCompleteFor(vehicleId));
            foreach (var modeId in ReferenceCertificationReport.RequiredModeIds)
            {
                foreach (var angle in ReferenceCertificationReport.RequiredAnglesGon)
                {
                    var item = Assert.Single(report.Cases, candidate =>
                        candidate.VehicleId == vehicleId && candidate.ModeId == modeId && candidate.AngleGon == angle);
                    Assert.Equal(64, item.SourceSha256.Length);
                    Assert.EndsWith(".dwg", item.SourceFile, StringComparison.OrdinalIgnoreCase);
                }
            }
        }
    }

    [Fact]
    public void PresetValidationStatusCannotOutrunTheStoredReport()
    {
        var report = LoadReport();
        var catalog = VehicleCatalog.LoadEmbedded();

        foreach (var vehicleId in ReferenceCertificationReport.RequiredVehicleIds)
        {
            var expected = report.IsValidated(vehicleId)
                ? ValidationStatus.ReferenceValidated
                : ValidationStatus.SourceTranscribed;
            Assert.Equal(expected, catalog.Get(vehicleId).ValidationStatus);
        }
    }

    [Fact]
    public void EveryPassingCaseHasMeasuredValuesInsideBothTolerances()
    {
        var report = LoadReport();

        foreach (var item in report.Cases.Where(item => item.Passed))
        {
            Assert.NotNull(item.MaximumEnvelopeDeviationMetres);
            Assert.NotNull(item.MaximumInwardUnderpredictionMetres);
            Assert.True(item.MaximumEnvelopeDeviationMetres <= report.Thresholds.MaximumEnvelopeDeviationMetres);
            Assert.True(item.MaximumInwardUnderpredictionMetres <= report.Thresholds.MaximumInwardUnderpredictionMetres);
        }
    }
}
