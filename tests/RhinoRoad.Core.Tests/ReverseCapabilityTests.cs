using RhinoRoad.Core;

namespace RhinoRoad.Core.Tests;

/// <summary>
/// What the reversing tools can do for each vehicle is decided by its shape, in one place, and the
/// planner and the search follow whatever that place says.
/// </summary>
public sealed class ReverseCapabilityTests
{
    private static readonly VehicleCatalog Catalog = VehicleCatalog.LoadEmbedded();

    /// <summary>A chain of three joints: nothing in the catalogue, and nothing that can be docked.</summary>
    private static VehicleDefinition ThreeJoints
    {
        get
        {
            var pvt = Catalog.Get("PVT");
            return pvt with
            {
                Id = "PVT3",
                TowedUnits = [.. pvt.TowedUnits, pvt.TowedUnits[^1] with { Id = "SECOND", HitchOffsetMetres = -1.15 }]
            };
        }
    }

    [Theory]
    [InlineData("PV")]
    [InlineData("REN")]
    [InlineData("LV12")]
    [InlineData("BUS12")]
    [InlineData("BUS13")]
    [InlineData("BUS15")]
    [InlineData("SVT")]
    [InlineData("PVT")]
    public void EveryPresetCanBeDockedPlannedAndSearched(string id)
    {
        var vehicle = Catalog.Get(id);

        Assert.True(ReverseCapabilities.CanDock(vehicle));
        Assert.True(ReverseCapabilities.CanPlanReverse(vehicle));
        Assert.True(ReverseCapabilities.CanSearchArea(vehicle));
        Assert.NotNull(ReverseDocking.SolverFor(vehicle));
    }

    [Fact]
    public void AChainThatCannotBeDockedCanStillBePlannedButNotSearched()
    {
        var vehicle = ThreeJoints;

        Assert.False(ReverseCapabilities.CanDock(vehicle));
        Assert.True(ReverseCapabilities.CanPlanReverse(vehicle));
        Assert.False(ReverseCapabilities.CanSearchArea(vehicle));
        Assert.Null(ReverseDocking.SolverFor(vehicle));
        Assert.Throws<ArgumentException>(() => ReverseDocking.Dock(vehicle, vehicle.DrivingModes["B"],
            new VehicleState(new(0, 0, 0), 0, 0, TravelDirection.Reverse, 0),
            ArticulationChain.StartAligned(vehicle, 0.0), new Point2(-40, 0), Math.PI));
    }

    /// <summary>
    /// Planning for a chain that can only be aimed still produces a drivable proposal, and says the
    /// heading was not honoured rather than presenting the miss as a near dock.
    /// </summary>
    [Fact]
    public void APlannedReverseForAnAimOnlyChainSaysTheHeadingWasNotHonoured()
    {
        var vehicle = ThreeJoints;
        var mode = vehicle.DrivingModes["B"];
        var state = new VehicleState(new(0, 0, 0), 0.0, 0.0, TravelDirection.Forward, 0.0);
        var route = new[] { ManoeuvreReplayService.StateSample(state, vehicle) };
        var target = new Point3(vehicle.StraightAxleOffsetsMetres[^1] - 30.0, 0.0, 0.0);

        var proposal = ReversePlanner.Find(vehicle, mode, route, 0, state, target, Math.PI);

        Assert.False(proposal.HeadingHonoured);
        Assert.True(proposal.Leg.ExitHeadingUnavailable);
        Assert.NotNull(proposal.HeadingMissRadians);
        Assert.Equal(TravelDirection.Reverse, proposal.Leg.EndState.Direction);
    }

    [Fact]
    public void TheAreaSearchRefusesAChainThatCanOnlyBeAimed()
    {
        var vehicle = ThreeJoints;
        var start = new VehicleState(new(0, 0, 0), 0, 0, TravelDirection.Forward, 0);
        var journey = new ManoeuvreDefinition(1, start.RearAxleCentreMetres, 0, TravelDirection.Forward,
        [
            new ManoeuvreControl(new(30, 5, 0), TravelDirection.Forward),
            new ManoeuvreControl(new(0, 0, 0), TravelDirection.Reverse, ManoeuvreControlKind.Aim, Math.PI)
        ]);
        Point2[] yard = [new(-100, -100), new(100, -100), new(100, 100), new(-100, 100)];

        Assert.Throws<ArgumentException>(() => ManoeuvringAreaSearch.Find(vehicle, vehicle.DrivingModes["B"],
            journey, 0, yard, [], 0.3, TimeSpan.FromSeconds(1)));
    }

    /// <summary>
    /// The pull-up grid is laid out in the vehicle's own scale: the turning radius for a rigid
    /// vehicle, the tightest radius the combination can hold for a semitrailer.
    /// </summary>
    [Fact]
    public void ThePlannerScaleFollowsHowTightlyTheVehicleTurns()
    {
        var pv = Catalog.Get("PV");
        var svt = Catalog.Get("SVT");
        var pvMode = pv.DrivingModes["B"];
        var svtMode = svt.DrivingModes["B"];

        Assert.Equal(TurningGeometryCalculator.AtFullLock(pv, pvMode).RearAxleRadiusMetres,
            ReversePlanner.ManoeuvreScaleMetres(pv, pvMode), 9);
        Assert.Equal(TurningGeometryCalculator.AtFullLock(svt, svtMode).MinimumSustainableRearAxleRadiusMetres,
            ReversePlanner.ManoeuvreScaleMetres(svt, svtMode), 9);
        Assert.True(ReversePlanner.ManoeuvreScaleMetres(pv, pvMode) < 4.0);
        Assert.True(ReversePlanner.ManoeuvreScaleMetres(svt, svtMode) > 8.0);
    }

    /// <summary>The old name still plans, and agrees with the new one.</summary>
    [Fact]
    public void TheTrailerPlannerNameIsTheSamePlanner()
    {
        var vehicle = Catalog.Get("SVT");
        var mode = vehicle.DrivingModes["B"];
        var state = new VehicleState(new(0, 0, 0), 0.0, 0.0, TravelDirection.Forward, 0.0);
        var route = new[] { ManoeuvreReplayService.StateSample(state, vehicle) };
        var target = new Point3(vehicle.StraightAxleOffsetsMetres[^1] - 30.0, 0.0, 0.0);

        var general = ReversePlanner.Find(vehicle, mode, route, 0, state, target, Math.PI);
        var trailer = TrailerReversePlanner.Find(vehicle, mode, route, 0, state, target, Math.PI);

        Assert.Equal(general.Controls, trailer.Controls);
        Assert.Equal(general.MissMetres, trailer.TrailerMissMetres);
        Assert.Equal(general.ReachedTarget, trailer.ReachedTarget);
    }
}
