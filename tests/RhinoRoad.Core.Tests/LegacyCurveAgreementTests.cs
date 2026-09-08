using RhinoRoad.Core;

namespace RhinoRoad.Core.Tests;

/// <summary>
/// Checks the presets against the legacy Vejdirektoratet curve library, which carries official
/// design envelopes for every preset in both modes and needs no Rhino to read.
/// </summary>
/// <remarks>
/// <para>
/// What is compared is the <em>steady-turn annulus</em>, not the whole envelope. Comparing whole
/// envelopes needs the manoeuvre the sheets were drawn to — where the wheel starts moving and how
/// fast — and that convention is not published. Reconstructing it as "straight in, full lock, straight
/// out" reproduces the sheets only loosely: against PV, the one preset independently validated
/// against the official DWGs to 0.052 m, that reconstruction disagrees by up to 1.2 m at 100 gon.
/// The disagreement is the assumed manoeuvre, not the preset, and a test built on it would report
/// every preset as broken.
/// </para>
/// <para>
/// Where the vehicle is turning steadily there is no such freedom: the radius is fixed by the
/// wheelbase and the wheel angle alone, whatever route led into it. The quantity compared is the
/// rear-axle radius, because both boundaries are derived from it — the outer by the leading unit's
/// front corner, the inner by the body's flank — so the two are one measurement made twice, from
/// opposite sides of the vehicle, and their agreement is the check that the arcs were found
/// correctly.
/// </para>
/// </remarks>
public sealed class LegacyCurveAgreementTests
{
    /// <summary>The tolerance the official-DWG certification uses, applied here too.</summary>
    private const double ToleranceMetres = 0.10;

    private static readonly int[] Angles = [40, 60, 80, 100, 120, 140, 160, 180];

    private static readonly (string VehicleId, int TypeId, string ModeId)[] Sheets =
    [
        ("PV", 1, "A"), ("PV", 101, "B"),
        ("REN", 2, "A"), ("REN", 102, "B"),
        ("LV12", 3, "A"), ("LV12", 103, "B"),
        ("BUS12", 4, "A"), ("BUS12", 104, "B"),
        ("BUS13", 5, "A"), ("BUS13", 105, "B"),
        ("BUS15", 6, "A"), ("BUS15", 106, "B"),
        ("SVT", 9, "A"), ("SVT", 109, "B"),
        ("PVT", 8, "A"), ("PVT", 108, "B")
    ];

    private static VejReferenceTemplateCatalog Templates() =>
        VejReferenceTemplateCatalog.LoadFromLegacyPython(File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory, "Reference", "VehicleTurn_VD_v12.py")));

    private static IEnumerable<(string VehicleId, string ModeId, int Gon, VejReferenceTemplate Template)> All()
    {
        var templates = Templates();
        foreach (var (vehicleId, typeId, modeId) in Sheets)
        {
            foreach (var gon in Angles)
            {
                VejReferenceTemplate template;
                try { template = templates.Get(typeId, gon); }
                catch (KeyNotFoundException) { continue; }
                yield return (vehicleId, modeId, gon, template);
            }
        }
    }

    /// <summary>Every sheet that is sound and resolves to a genuine steady turn.</summary>
    private static IEnumerable<(string VehicleId, string ModeId, int Gon, VehicleDefinition Vehicle, ReferenceAnnulus Annulus)>
        SteadyTurns()
    {
        var catalog = VehicleCatalog.LoadEmbedded();
        foreach (var (vehicleId, modeId, gon, template) in All())
        {
            var vehicle = catalog.Get(vehicleId);
            if (!LegacyCurveCheck.Inspect(template, vehicle.WidthMetres).IsUsable) continue;
            var annulus = LegacyCurveCheck.Annulus(template);
            if (annulus is null || !annulus.IsSteady()) continue;
            yield return (vehicleId, modeId, gon, vehicle, annulus);
        }
    }

    private static double MeasuredRearAxleRadius(VehicleDefinition vehicle, ReferenceAnnulus annulus)
    {
        var (fromOuter, fromInner) = annulus.ImpliedRearAxleRadiusMetres(vehicle);
        return (fromOuter + fromInner) / 2.0;
    }

    /// <summary>
    /// The library is a GIS conversion and parts of it did not survive it. This pins exactly which
    /// sheets are unusable, so a comparison can never quietly run over one — and so that if the
    /// library is ever re-converted, the improvement or regression shows up here rather than as a
    /// preset that suddenly looks wrong.
    /// </summary>
    [Fact]
    public void TheBrokenSheetsInTheLegacyLibraryAreTheOnesWeKnowAbout()
    {
        var catalog = VehicleCatalog.LoadEmbedded();
        var unusable = new List<string>();
        var total = 0;
        foreach (var (vehicleId, modeId, gon, template) in All())
        {
            total++;
            if (!LegacyCurveCheck.Inspect(template, catalog.Get(vehicleId).WidthMetres).IsUsable)
            {
                unusable.Add($"{vehicleId} {modeId} {gon}");
            }
        }

        Assert.Equal(121, total);
        Assert.Equal(
            [
                "REN A 180", "REN B 120",
                // Every one of LV 12's mode-A sheets carries three to five boundary chains.
                "LV12 A 40", "LV12 A 60", "LV12 A 80", "LV12 A 100",
                "LV12 A 120", "LV12 A 140", "LV12 A 160", "LV12 A 180",
                // Seven of BUS 12's eight mode-A sheets carry a stray third boundary chain.
                "BUS12 A 40", "BUS12 A 80", "BUS12 A 100", "BUS12 A 120",
                "BUS12 A 140", "BUS12 A 160", "BUS12 A 180",
                // SVT's two tightest mode-B sheets have a boundary that crosses itself.
                "SVT B 140", "SVT B 160"
            ],
            unusable);
    }

    /// <summary>
    /// Each steady turn is measured twice — once off each boundary — and the two must agree. This
    /// is what says the arcs were found on the sustained part of the turn rather than on a
    /// transition that happens to fit a circle, and it depends on nothing but the sheet.
    /// </summary>
    [Fact]
    public void EverySteadyTurnIsInternallyConsistent()
    {
        var checkedAny = false;
        foreach (var (vehicleId, modeId, gon, vehicle, annulus) in SteadyTurns())
        {
            checkedAny = true;
            var (fromOuter, fromInner) = annulus.ImpliedRearAxleRadiusMetres(vehicle);
            Assert.True(
                Math.Abs(fromOuter - fromInner) <= ToleranceMetres,
                $"{vehicleId} {modeId} {gon} gon: the outer boundary implies a {fromOuter:0.000} m " +
                $"rear-axle radius and the inner implies {fromInner:0.000} m.");
        }

        Assert.True(checkedAny);
    }

    /// <summary>
    /// Mode B is drawn at the maximum wheel angle the source publishes per vehicle, so a steady
    /// mode-B turn is directly predictable from the preset. This is the comparison that validates
    /// the transcribed dimensions.
    /// </summary>
    [Fact]
    public void ModeBSteadyTurnsAgreeWithThePresetTurningGeometry()
    {
        var compared = new List<string>();
        foreach (var (vehicleId, modeId, gon, vehicle, annulus) in SteadyTurns())
        {
            if (modeId != "B") continue;
            var predicted = TurningGeometryCalculator.AtFullLock(vehicle, vehicle.DrivingModes[modeId]);
            var measured = MeasuredRearAxleRadius(vehicle, annulus);
            var name = $"{vehicleId} {gon} gon";
            compared.Add(name);

            Assert.True(
                Math.Abs(measured - predicted.RearAxleRadiusMetres) <= ToleranceMetres,
                $"{name}: the sheet turns on a {measured:0.000} m rear-axle radius against " +
                $"{predicted.RearAxleRadiusMetres:0.000} m predicted.");
            Assert.True(
                Math.Abs(annulus.Outer.RadiusMetres - predicted.OuterRadiusMetres) <= ToleranceMetres,
                $"{name}: the sheet holds a {annulus.Outer.RadiusMetres:0.000} m outer radius against " +
                $"{predicted.OuterRadiusMetres:0.000} m predicted.");
        }

        Assert.Equal(
            [
                "REN 140 gon", "REN 160 gon", "REN 180 gon",
                "LV12 120 gon", "LV12 140 gon", "LV12 160 gon",
                "BUS12 160 gon",
                "BUS13 140 gon", "BUS13 160 gon", "BUS13 180 gon"
            ],
            compared);
    }

    /// <summary>
    /// A finding, pinned so it cannot be lost: the mode-A sheets for the large vehicles are drawn at
    /// 30.0°, not the 29.7° that converting the published 33 gon exactly gives — and the source says
    /// so itself, in the caption to Figure 6.1: "hjuldrejning på 30 grader (svarende til 33 gon)".
    /// The presets hold 29.7°, which costs about 0.15 m on the inner radius.
    /// </summary>
    /// <remarks>
    /// It is left as a finding rather than applied, because mode A is shared with PV, whose
    /// <c>ReferenceValidated</c> status rests on a stored certification run computed at 29.7°.
    /// Changing the angle would make that report describe a preset that no longer exists, and
    /// re-running it needs Rhino. When it is re-run, change both together and this test with them.
    /// </remarks>
    [Fact]
    public void ModeASheetsForLargeVehiclesAreDrawnAtThirtyDegrees()
    {
        var implied = new List<double>();
        foreach (var (vehicleId, modeId, gon, vehicle, annulus) in SteadyTurns())
        {
            if (modeId != "A" || vehicleId is not ("REN" or "BUS13" or "BUS15")) continue;
            var degrees = Math.Atan(vehicle.WheelbaseMetres / MeasuredRearAxleRadius(vehicle, annulus))
                * 180.0 / Math.PI;
            implied.Add(degrees);
            Assert.True(
                Math.Abs(degrees - 30.0) <= 0.5,
                $"{vehicleId} {modeId} {gon} gon implies a {degrees:0.00}° lock.");
            Assert.Equal(29.7, vehicle.DrivingModes["A"].MaximumWheelAngleDegrees, 6);
        }

        Assert.True(implied.Count >= 10);
        Assert.True(implied.Average() > 29.7, "the sheets sit above the 29.7 degrees the presets hold");
    }

    /// <summary>
    /// PV is the exception: its mode-A sheets are not drawn at any lock the preset knows about. The
    /// source runs personbiler through junctions at 20 km/h against 15 for large vehicles, and at
    /// that speed the curve is set by comfort rather than by the steering lock — the sheets imply
    /// about 18°, against a 29.7° cap. Recorded so the gap is never mistaken for a bad preset.
    /// </summary>
    [Fact]
    public void PvModeASheetsAreNotDrawnAtTheSteeringLock()
    {
        var seen = 0;
        foreach (var (vehicleId, modeId, _, vehicle, annulus) in SteadyTurns())
        {
            if (vehicleId != "PV" || modeId != "A") continue;
            seen++;
            Assert.InRange(
                Math.Atan(vehicle.WheelbaseMetres / MeasuredRearAxleRadius(vehicle, annulus)) * 180.0 / Math.PI,
                15.0,
                22.0);
        }

        Assert.True(seen >= 3);
    }

    /// <summary>
    /// The articulated presets' own validation. The whole combination never settles, but the lorry
    /// does, and the outer boundary is the front corner of a rigid unit on a circle — so the sheet's
    /// outer radius measures the lorry's wheelbase, front overhang, width and mode-B wheel angle
    /// together.
    /// </summary>
    /// <remarks>
    /// This is also what settled 43 gon as 38.7 degrees rather than the 38 the published table
    /// prints beside it: at 38 the prediction misses the sheet by 0.169 m, at 38.7 by 0.036 m.
    /// </remarks>
    [Fact]
    public void ThePvtLorryHoldsThePublishedFullLockArc()
    {
        var catalog = VehicleCatalog.LoadEmbedded();
        var templates = Templates();
        var vehicle = catalog.Get("PVT");
        var predicted = TurningGeometryCalculator.AtFullLock(vehicle, vehicle.DrivingModes["B"]);

        var radii = new List<double>();
        foreach (var gon in new[] { 140, 160, 180 })
        {
            var template = templates.Get(108, gon);
            Assert.True(LegacyCurveCheck.Inspect(template, vehicle.WidthMetres).IsUsable);
            var arc = LegacyCurveCheck.TightestCircularRun(LegacyCurveCheck.Boundaries(template)!.Value.Outer)!;

            // A sustained arc, not a transition that happens to fit: it is circular to well under a
            // millimetre, and it is the same radius however far round the sheet goes.
            Assert.True(arc.FitDeviationMetres <= 0.002, $"{gon} gon: fit deviation {arc.FitDeviationMetres:0.0000} m");
            radii.Add(arc.RadiusMetres);
            Assert.True(
                Math.Abs(arc.RadiusMetres - predicted.OuterRadiusMetres) <= ToleranceMetres,
                $"PVT B {gon} gon: the sheet holds a {arc.RadiusMetres:0.000} m outer radius against " +
                $"{predicted.OuterRadiusMetres:0.000} m predicted.");
        }

        Assert.True(radii.Max() - radii.Min() <= 0.005, "the lorry's arc should not depend on how far round it goes");
    }

    /// <summary>
    /// Why the trailers are not validated here, recorded as a fact rather than left as a gap: neither
    /// combination ever reaches a steady turn on these sheets. For SVT that is what the model itself
    /// predicts — its semitrailer has no steady state at any lock either mode allows — and for PVT
    /// the trailer is still folding at 180 gon, the longest turn the library draws.
    /// </summary>
    [Fact]
    public void NoArticulatedSheetReachesASteadyTurnSoTheTrailersStayUnvalidated()
    {
        foreach (var (vehicleId, modeId, gon, _, _) in SteadyTurns())
        {
            Assert.False(
                vehicleId is "SVT" or "PVT",
                $"{vehicleId} {modeId} {gon} gon now resolves to a steady annulus — the trailer can be " +
                "compared against it, and this test should become that comparison.");
        }

        // SVT's half of that is a property of the vehicle, not of the drawing.
        var svt = VehicleCatalog.LoadEmbedded().Get("SVT");
        Assert.All(svt.DrivingModes.Values, mode =>
            Assert.False(TurningGeometryCalculator.AtFullLock(svt, mode).SteadyStateAttainable));
    }
}
