using RhinoRoad.Core;

namespace RhinoRoad.Core.Tests;

public sealed class ManoeuvreReplayTests
{
    [Fact]
    public void Recorded_preview_and_replay_are_sample_for_sample_identical_across_rewind_reverse_finish_and_wrap()
    {
        var vehicle = VehicleCatalog.LoadEmbedded().Get("PV");
        var mode = vehicle.DrivingModes["B"];
        var definition = new ManoeuvreDefinition(
            1,
            new Point3(0, 0, 0),
            Math.PI - 0.03,
            TravelDirection.Forward,
            [
                new ManoeuvreControl(new Point3(-10, -7, 0), TravelDirection.Forward),
                new ManoeuvreControl(new Point3(-18, -13, 0), TravelDirection.Forward),
                new ManoeuvreControl(new Point3(-28, -13, 0), TravelDirection.Forward),
                new ManoeuvreControl(new Point3(-23, -4, 0), TravelDirection.Reverse),
                new ManoeuvreControl(new Point3(-15, 0, 0), TravelDirection.Reverse, ManoeuvreControlKind.Finish)
            ]);

        var recorded = Record(vehicle, mode, definition);
        var replayed = ManoeuvreReplayService.Replay(vehicle, mode, definition);

        Assert.Equal(recorded.Count, replayed.Samples.Count);
        for (var index = 0; index < recorded.Count; index++) Assert.Equal(recorded[index], replayed.Samples[index]);
        var analysis = new VehicleAccessAnalyzer().Analyze(vehicle, mode, replayed.Samples);
        Assert.True(analysis.MaximumSteeringAngleRadians <= mode.MaximumWheelAngleRadians + 1e-8);
        Assert.True(analysis.MaximumSteeringRateRadiansPerSecond <= mode.MaximumSteeringRateRadiansPerSecond + 1e-8);
    }

    private static IReadOnlyList<RouteSample> Record(
        VehicleDefinition vehicle,
        DrivingModeDefinition mode,
        ManoeuvreDefinition definition)
    {
        var state = definition.StartState;
        var route = new List<RouteSample> { ManoeuvreReplayService.StateSample(state, vehicle) };
        var legStart = 0;
        foreach (var control in definition.Controls)
        {
            if (state.Direction != control.Direction)
            {
                state = state with { Direction = control.Direction };
                legStart = route.Count - 1;
            }
            var planned = ManoeuvreReplayService.PlanControl(vehicle, mode, route, legStart, state, control);
            if (planned.FromIndex < route.Count - 1)
                route.RemoveRange(planned.FromIndex + 1, route.Count - planned.FromIndex - 1);
            legStart = route.Count - 1;
            route.AddRange(planned.Samples.Skip(1));
            state = planned.EndState;
        }
        return route;
    }
}
