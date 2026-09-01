using RhinoRoad.Core;

namespace RhinoRoad.Core.Tests;

public sealed class VejReferenceTemplateCatalogTests
{
    [Fact]
    public void LegacyLibraryCoversMvpVehiclesModesAndAngles()
    {
        var sourcePath = Path.Combine(AppContext.BaseDirectory, "Reference", "VehicleTurn_VD_v12.py");
        var catalog = VejReferenceTemplateCatalog.LoadFromLegacyPython(File.ReadAllText(sourcePath));
        var typeIds = new Dictionary<string, (int A, int B)>
        {
            ["PV"] = (1, 101),
            ["REN"] = (2, 102),
            ["BUS12"] = (4, 104)
        };

        foreach (var pair in typeIds)
        {
            foreach (var typeId in new[] { pair.Value.A, pair.Value.B })
            {
                foreach (var angle in new[] { 40, 100, 160 })
                {
                    var template = catalog.Get(typeId, angle);
                    Assert.NotEmpty(template.DesignEnvelopeLines);
                    Assert.NotEmpty(template.WheelTrackLines);
                    Assert.InRange(template.AngleDegrees, (angle * 0.9) - 0.01, (angle * 0.9) + 0.01);
                }
            }
        }

        Assert.DoesNotContain("104:180", catalog.Keys);
    }

    [Fact]
    public void ReferenceWidthsMatchTranscribedPresetWidthsWithinLegacyTolerance()
    {
        var sourcePath = Path.Combine(AppContext.BaseDirectory, "Reference", "VehicleTurn_VD_v12.py");
        var references = VejReferenceTemplateCatalog.LoadFromLegacyPython(File.ReadAllText(sourcePath));
        var vehicles = VehicleCatalog.LoadEmbedded();

        Assert.InRange(Math.Abs(references.Get(1, 100).SourceWidthMetres - vehicles.Get("PV").WidthMetres), 0.0, 0.03);
        Assert.InRange(Math.Abs(references.Get(2, 100).SourceWidthMetres - vehicles.Get("REN").WidthMetres), 0.0, 0.03);
        var busWidthDifference = Math.Abs(references.Get(4, 100).SourceWidthMetres - vehicles.Get("BUS12").WidthMetres);
        Assert.InRange(busWidthDifference, 0.06, 0.07);
        Assert.Equal(ValidationStatus.SourceTranscribed, vehicles.Get("BUS12").ValidationStatus);
    }
}
