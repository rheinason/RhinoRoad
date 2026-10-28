using System.Diagnostics;
using RhinoRoad.Core;

namespace RhinoRoad.Core.Tests;

/// <summary>
/// The reverse planner and the docking solvers can be abandoned, so the pick loop can run them off
/// the UI thread with Esc to stop them. Cancelling only ever stops a search; one that runs to the end
/// is the same with or without a token, because a proposal becomes saved controls and replay has to
/// reproduce it.
/// </summary>
public sealed class ReverseCancellationTests
{
    private static readonly VehicleCatalog Catalog = VehicleCatalog.LoadEmbedded();

    /// <summary>
    /// A bay square across the line just behind and to the side: the planner tries every candidate
    /// in its budget, which for MVT in mode A is the slowest search any preset makes.
    /// </summary>
    private static (VehicleDefinition Vehicle, DrivingModeDefinition Mode, RouteSample[] Route, VehicleState Start, Point3 Bay)
        SlowRequest()
    {
        var vehicle = Catalog.Get("MVT");
        var mode = vehicle.DrivingModes["A"];
        var scale = ReversePlanner.ManoeuvreScaleMetres(vehicle, mode);
        var start = new VehicleState(new(0, 0, 0), 0.0, 0.0, TravelDirection.Forward, 0.0);
        return (vehicle, mode, [ManoeuvreReplayService.StateSample(start, vehicle)], start,
            new Point3(-1.0 * scale, 1.0 * scale, 0));
    }

    [Fact]
    public void ACancelledPlannerStopsBeforeItStarts()
    {
        var (vehicle, mode, route, start, bay) = SlowRequest();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.ThrowsAny<OperationCanceledException>(() =>
            ReversePlanner.Find(vehicle, mode, route, 0, start, bay, Math.PI / 2, cancellationToken: cancellation.Token));
    }

    /// <summary>
    /// Cancelled part-way, the search stops within a docking plan rather than at the end of its
    /// budget, which runs to well over a second here.
    /// </summary>
    [Fact]
    public void APlannerCancelledPartWayStopsPromptly()
    {
        var (vehicle, mode, route, start, bay) = SlowRequest();
        using var cancellation = new CancellationTokenSource();
        cancellation.CancelAfter(TimeSpan.FromMilliseconds(50));
        var watch = Stopwatch.StartNew();

        Assert.ThrowsAny<OperationCanceledException>(() =>
            ReversePlanner.Find(vehicle, mode, route, 0, start, bay, Math.PI / 2, cancellationToken: cancellation.Token));
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(1), $"stopped after {watch.ElapsedMilliseconds} ms");
    }

    [Theory]
    [InlineData("PV", "B")]
    [InlineData("SVT", "A")]
    [InlineData("PVT", "B")]
    public void ATokenThatIsNeverCancelledChangesNothing(string vehicleId, string modeId)
    {
        var vehicle = Catalog.Get(vehicleId);
        var mode = vehicle.DrivingModes[modeId];
        var scale = ReversePlanner.ManoeuvreScaleMetres(vehicle, mode);
        var start = new VehicleState(new(-2.0 * scale, 0, 0), 0.0, 0.0, TravelDirection.Forward, 0.0);
        var route = new[] { ManoeuvreReplayService.StateSample(start, vehicle) };
        var bay = ReverseBay.AxleFromRear(vehicle, new Point3(-2.0 * scale, -0.6 * scale, 0), Math.PI);
        using var cancellation = new CancellationTokenSource();

        var without = ReversePlanner.Find(vehicle, mode, route, 0, start, bay, Math.PI);
        var with = ReversePlanner.Find(vehicle, mode, route, 0, start, bay, Math.PI, cancellationToken: cancellation.Token);

        Assert.Equal(without.Controls, with.Controls);
        Assert.Equal(without.Leg.Samples, with.Leg.Samples);
        Assert.Equal(without.MissMetres, with.MissMetres);
    }

    [Fact]
    public void ADockingPlanCanBeCancelled()
    {
        var vehicle = Catalog.Get("PVT");
        var start = new VehicleState(new(0, 0, 0), 0.0, 0.0, TravelDirection.Reverse, 0.0);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var control = new ManoeuvreControl(new Point3(-30, 4, 0), TravelDirection.Reverse, ManoeuvreControlKind.Aim, Math.PI);

        Assert.ThrowsAny<OperationCanceledException>(() => ManoeuvreReplayService.PlanControl(vehicle,
            vehicle.DrivingModes["B"], [ManoeuvreReplayService.StateSample(start, vehicle)], 0, start, control,
            cancellation.Token));
        Assert.ThrowsAny<OperationCanceledException>(() => MultiJointReverseDocking.Dock(vehicle,
            vehicle.DrivingModes["B"], start, ArticulationChain.StartAligned(vehicle, 0.0), new Point2(-30, 4),
            Math.PI, cancellationToken: cancellation.Token));
    }
}
