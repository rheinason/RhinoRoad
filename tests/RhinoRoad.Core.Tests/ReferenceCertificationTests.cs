using System.Text.Json;
using RhinoRoad.Core;

namespace RhinoRoad.Core.Tests;

public sealed class ReferenceCertificationTests
{
    private static CertificationPlan LoadPlan() =>
        CertificationPlan.Load(Path.Combine(AppContext.BaseDirectory, "Reference", CertificationPlan.FileName));

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
        var plan = LoadPlan();

        Assert.Equal(ReferenceCertificationReport.CurrentSchemaVersion, report.SchemaVersion);
        Assert.Equal(0.10, report.Thresholds.MaximumEnvelopeDeviationMetres, 6);
        Assert.Equal(0.05, report.Thresholds.MaximumInwardUnderpredictionMetres, 6);
        Assert.Equal(plan.ExpectedCaseCount, report.Cases.Count);

        // A verdict is only reproducible if the geometry context it was measured in is recorded.
        Assert.NotNull(report.Environment);
        Assert.Equal("Meters", report.Environment.ModelUnitSystem);
        Assert.True(report.Environment.GeometryToleranceMetres > 0.0);
        foreach (var vehicleId in plan.RequiredVehicleIds)
        {
            Assert.True(report.IsCompleteFor(plan, vehicleId));
            foreach (var modeId in plan.ModeIdsFor(vehicleId))
            {
                foreach (var angle in plan.AnglesGon)
                {
                    var item = Assert.Single(report.Cases, candidate =>
                        candidate.VehicleId == vehicleId && candidate.ModeId == modeId && candidate.AngleGon == angle);
                    Assert.Equal(64, item.SourceSha256.Length);
                    Assert.EndsWith(".dwg", item.SourceFile, StringComparison.OrdinalIgnoreCase);
                    Assert.True(item.ExpectedChordMetres > 0.0, $"{vehicleId}/{modeId}/{angle}: expected chord");
                }
            }
        }
    }

    [Fact]
    public void PresetValidationStatusCannotOutrunTheStoredReport()
    {
        var report = LoadReport();
        var plan = LoadPlan();
        var catalog = VehicleCatalog.LoadEmbedded();

        foreach (var vehicleId in plan.RequiredVehicleIds)
        {
            var expected = report.IsValidated(plan, vehicleId)
                ? ValidationStatus.ReferenceValidated
                : ValidationStatus.SourceTranscribed;
            Assert.Equal(expected, catalog.Get(vehicleId).ValidationStatus);
        }
    }

    /// <summary>
    /// The chord between the two wheel traces is fixed by the vehicle, so the drawing and the preset
    /// must agree on it. Before schema 1.3 this comparison used the preset's own value on both
    /// sides and could not fail; measuring the drawing is what makes it evidence.
    /// </summary>
    [Fact]
    public void MeasuredChordAgreesWithThePresetWithinDraftingTolerance()
    {
        var report = LoadReport();
        var measured = report.Cases.Where(item => item.MeasuredChordMetres.HasValue).ToArray();

        Assert.NotEmpty(measured);
        foreach (var item in measured)
        {
            var delta = item.MeasuredChordMetres!.Value - item.ExpectedChordMetres;
            Assert.True(
                Math.Abs(delta) <= 0.10,
                $"{item.VehicleId}/{item.ModeId}/{item.AngleGon}: the drawing holds a " +
                $"{item.MeasuredChordMetres.Value:0.000} m chord against the {item.ExpectedChordMetres:0.000} m " +
                $"the preset predicts ({delta:+0.000;-0.000} m). Either the preset is mistranscribed or the " +
                "traces are not this vehicle.");
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
