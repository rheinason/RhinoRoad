using System.Globalization;
using System.Text;
using RhinoRoad.Core;

namespace RhinoRoad.Core.Tests;

/// <summary>
/// One fold limit, per towed unit, used everywhere: the generator stops at it, the planner and the
/// search refuse it, and the analyzer reports a route past it — so nothing one of them refuses can be
/// baked as feasible by another.
/// </summary>
public sealed class FoldLimitTests
{
    private static readonly VehicleCatalog Catalog = VehicleCatalog.LoadEmbedded();
    private static readonly VehicleAccessAnalyzer Analyzer = new();

    private static VehicleDefinition WithLimit(VehicleDefinition vehicle, double degrees) => vehicle with
    {
        TowedUnits = vehicle.TowedUnits.Select(unit => unit with { MaximumArticulationDegrees = degrees }).ToArray()
    };

    private static ReverseLeg CurvedReverse(VehicleDefinition vehicle)
    {
        var start = new VehicleState(new(0, 0, 0), 0.0, 0.0, TravelDirection.Reverse, 0.0);
        var target = new Point2(vehicle.StraightAxleOffsetsMetres[^1] - 40.0, 12.0);
        return TrailerReverseGenerator.AimTowedUnit(vehicle, vehicle.DrivingModes["B"], start,
            ArticulationChain.StartAligned(vehicle, 0.0), target);
    }

    [Fact]
    public void EveryPresetUnitTakesTheDocumentedDefault()
    {
        var units = Catalog.Vehicles.SelectMany(vehicle => vehicle.TowedUnits).ToArray();

        Assert.NotEmpty(units);
        Assert.All(units, unit => Assert.Equal(TowedUnitDefinition.DefaultMaximumArticulationDegrees,
            unit.MaximumArticulationDegrees));
        Assert.Equal(65.0, TowedUnitDefinition.DefaultMaximumArticulationDegrees);
    }

    [Fact]
    public void ACatalogueMayStateAUnitsLimitAndMayLeaveItOut()
    {
        var catalog = VehicleCatalog.Load(Json("\"maximumArticulationDegrees\": 40.0,"));
        var units = catalog.Get("TEST").TowedUnits;

        Assert.Equal(40.0, units[0].MaximumArticulationDegrees);
        Assert.Equal(TowedUnitDefinition.DefaultMaximumArticulationDegrees, units[1].MaximumArticulationDegrees);
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(95.0)]
    public void ALimitPastTheJackknifeFloorOrAtZeroIsRejected(double degrees)
    {
        Assert.Throws<InvalidDataException>(() =>
            VehicleCatalog.Load(Json(
            $"\"maximumArticulationDegrees\": {degrees.ToString(CultureInfo.InvariantCulture)},")));
    }

    /// <summary>
    /// The analyzer judges a fold past the unit's limit as a violation of its own, separate from the
    /// jackknife floor, and only for the unit whose limit it is.
    /// </summary>
    [Fact]
    public void AFoldPastTheAssumedLimitIsAViolationButNotAJackknife()
    {
        var svt = Catalog.Get("SVT");
        var mode = svt.DrivingModes["B"];
        var leg = CurvedReverse(svt);

        var asPublished = Analyzer.Analyze(svt, mode, leg.Samples);
        var tighter = Analyzer.Analyze(WithLimit(svt, 6.0), mode, leg.Samples);

        Assert.True(asPublished.MaximumArticulationAnglesRadians[0] > 6.0 * Math.PI / 180.0,
            $"{asPublished.MaximumArticulationAnglesRadians[0] * 180 / Math.PI:0.0} deg reached={leg.ReachedTarget} s={leg.EndState.StationMetres:0.0} n={leg.Samples.Count}");
        Assert.True(asPublished.IsFeasible, string.Join("; ", asPublished.Violations.Select(item => item.Message)));
        Assert.False(tighter.IsFeasible);
        var violation = Assert.Single(tighter.Violations, item => item.Kind == ViolationKind.ArticulationAngle);
        Assert.Contains("assumed fold limit of 6", violation.Message);
        Assert.DoesNotContain("jackknifed", violation.Message);
    }

    /// <summary>The generator stops at the unit's limit, whatever it is, rather than at a fixed 65.</summary>
    [Fact]
    public void TheGeneratorStopsShortOfTheUnitsOwnLimit()
    {
        var svt = WithLimit(Catalog.Get("SVT"), 6.0);
        var leg = CurvedReverse(svt);
        var analysis = Analyzer.Analyze(svt, svt.DrivingModes["B"], leg.Samples);

        Assert.True(analysis.MaximumArticulationAnglesRadians[0] < 6.0 * Math.PI / 180.0);
        Assert.DoesNotContain(analysis.Violations, item => item.Kind == ViolationKind.ArticulationAngle);
        Assert.True(FoldLimit.Measure(svt, leg.Samples).MaximumRadians < 6.0 * Math.PI / 180.0);
        Assert.False(FoldLimit.IsReached(svt, leg.Samples));
    }

    [Fact]
    public void ARigidVehicleHasNothingToFold()
    {
        var pv = Catalog.Get("PV");
        var route = new[]
        {
            ManoeuvreReplayService.StateSample(new VehicleState(new(0, 0, 0), 0, 0.3, TravelDirection.Forward, 0), pv),
            ManoeuvreReplayService.StateSample(new VehicleState(new(1, 0.1, 0), 0.1, 0.3, TravelDirection.Forward, 1), pv)
        };

        Assert.Equal((0.0, false), FoldLimit.Measure(pv, route));
        Assert.False(FoldLimit.IsReached(pv, Array.Empty<double>()));
    }

    private static MemoryStream Json(string firstUnitLimit) => new(Encoding.UTF8.GetBytes($$"""
        {
          "vehicles": [
            {
              "id": "TEST",
              "name": "Test combination",
              "version": "test",
              "widthMetres": 2.55,
              "wheelbaseMetres": 5.5,
              "frontOverhangMetres": 1.4,
              "rearOverhangMetres": 2.15,
              "axleTrackMetres": 2.195,
              "tyreWidthMetres": 0.254,
              "bodyOutline": [[-2.15,-1.275],[6.90,-1.275],[6.90,1.275],[-2.15,1.275]],
              "towedUnits": [
                {
                  "id": "DOLLY", "name": "Dolly", "hitchOffsetMetres": -2.0, "wheelbaseMetres": 2.9,
                  {{firstUnitLimit}}
                  "widthMetres": 2.55, "axleTrackMetres": 2.195, "tyreWidthMetres": 0.254, "bodyOutline": []
                },
                {
                  "id": "BODY", "name": "Body", "hitchOffsetMetres": 0.0, "wheelbaseMetres": 5.8,
                  "widthMetres": 2.55, "axleTrackMetres": 2.195, "tyreWidthMetres": 0.254,
                  "bodyOutline": [[-1.15,-1.275],[7.00,-1.275],[7.00,1.275],[-1.15,1.275]]
                }
              ],
              "drivingModes": {
                "B": { "name": "B", "speedKilometresPerHour": 5.0, "maximumWheelAngleDegrees": 38.7, "lockToLockSeconds": 6.0, "defaultClearanceMetres": 0.30 }
              },
              "validationStatus": "SourceTranscribed",
              "validationNotes": "test"
            }
          ]
        }
        """));
}
