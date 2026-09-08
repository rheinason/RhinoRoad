using RhinoRoad.Core;

namespace RhinoRoad.Core.Tests;

public sealed class VejReferenceTemplateCatalogTests
{
    [Fact]
    public void LegacyLibraryCoversPresetVehiclesModesAndAngles()
    {
        var sourcePath = Path.Combine(AppContext.BaseDirectory, "Reference", "VehicleTurn_VD_v12.py");
        var catalog = VejReferenceTemplateCatalog.LoadFromLegacyPython(File.ReadAllText(sourcePath));
        var typeIds = new Dictionary<string, (int A, int B)>
        {
            ["PV"] = (1, 101),
            ["REN"] = (2, 102),
            ["BUS12"] = (4, 104),
            ["BUS13"] = (5, 105),
            ["BUS15"] = (6, 106),
            // The articulated presets have official envelopes here too. Nothing compares against
            // them yet — see the articulated gap in the README — but they are the reference a
            // comparison would use, so their presence is fenced rather than left to be rediscovered.
            ["PVT"] = (8, 108),
            ["SVT"] = (9, 109)
        };

        foreach (var pair in typeIds)
        {
            foreach (var typeId in new[] { pair.Value.A, pair.Value.B })
            {
                // 100 and 160 gon are the only angles every sheet in the library draws: BUS 13,7
                // mode A skips 60, BUS 15 mode A starts at 100, and BUS 25 mode B starts at 60.
                foreach (var angle in new[] { 100, 160 })
                {
                    var template = catalog.Get(typeId, angle);
                    Assert.NotEmpty(template.DesignEnvelopeLines);
                    Assert.NotEmpty(template.WheelTrackLines);
                    Assert.InRange(template.AngleDegrees, (angle * 0.9) - 0.01, (angle * 0.9) + 0.01);
                }
            }
        }

        Assert.DoesNotContain("104:180", catalog.Keys);
        Assert.DoesNotContain("6:40", catalog.Keys);
        Assert.DoesNotContain("6:60", catalog.Keys);
        Assert.DoesNotContain("5:60", catalog.Keys);
    }

    [Fact]
    public void ReferenceWidthsMatchTranscribedPresetWidthsWithinLegacyTolerance()
    {
        var sourcePath = Path.Combine(AppContext.BaseDirectory, "Reference", "VehicleTurn_VD_v12.py");
        var references = VejReferenceTemplateCatalog.LoadFromLegacyPython(File.ReadAllText(sourcePath));
        var vehicles = VehicleCatalog.LoadEmbedded();

        Assert.InRange(Math.Abs(references.Get(1, 100).SourceWidthMetres - vehicles.Get("PV").WidthMetres), 0.0, 0.03);
        Assert.InRange(Math.Abs(references.Get(2, 100).SourceWidthMetres - vehicles.Get("REN").WidthMetres), 0.0, 0.03);
        // LV 12 and BUS 12 carry a wider body width in their metadata than their drawn geometry
        // holds -- 2.657 m for LV, which is the refrigerated-body width the source allows, against
        // the 2.55 m band the sheets are actually drawn to. The drawn width is what
        // LegacyCurveCheck.Inspect verifies, and it agrees; this pins the metadata gap so it stays
        // a known quirk rather than resurfacing as a mystery.
        var lorryWidthDifference = Math.Abs(references.Get(3, 100).SourceWidthMetres - vehicles.Get("LV12").WidthMetres);
        Assert.InRange(lorryWidthDifference, 0.10, 0.11);
        Assert.InRange(Math.Abs(references.Get(5, 100).SourceWidthMetres - vehicles.Get("BUS13").WidthMetres), 0.0, 0.03);
        Assert.InRange(Math.Abs(references.Get(6, 100).SourceWidthMetres - vehicles.Get("BUS15").WidthMetres), 0.0, 0.03);
        Assert.InRange(Math.Abs(references.Get(8, 100).SourceWidthMetres - vehicles.Get("PVT").WidthMetres), 0.0, 0.03);
        Assert.InRange(Math.Abs(references.Get(9, 100).SourceWidthMetres - vehicles.Get("SVT").WidthMetres), 0.0, 0.03);
        var busWidthDifference = Math.Abs(references.Get(4, 100).SourceWidthMetres - vehicles.Get("BUS12").WidthMetres);
        Assert.InRange(busWidthDifference, 0.06, 0.07);
        Assert.Equal(ValidationStatus.SourceTranscribed, vehicles.Get("BUS12").ValidationStatus);
    }
}
