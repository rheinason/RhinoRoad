using System.Text.Json;
using RhinoRoad.Core;

namespace RhinoRoad.Core.Tests;

/// <summary>
/// Pins the two things a control's stored direction has to survive: the gear it was driven in, and
/// the departure heading it was squared to.
/// </summary>
public sealed class DirectionConstraintRegressionTests
{
    private static (VehicleDefinition Vehicle, DrivingModeDefinition Mode) Preset(string id, string mode)
    {
        var vehicle = VehicleCatalog.LoadEmbedded().Get(id);
        return (vehicle, vehicle.DrivingModes[mode]);
    }

    /// <summary>
    /// A control carrying an exit heading is driven onto that heading exactly, whichever way the
    /// vehicle is facing and whichever gear it is in — and the heading survives being saved.
    /// </summary>
    /// <remarks>
    /// This is what makes squaring a leg up mean something. Moving the cursor onto an ortho direction
    /// only asks for it, and the wheel takes metres to reach the lock the arc needs, so a BUS 12 in
    /// mode A squared to a 90 degree departure makes 55 of them over 15 m. Carrying the heading on the
    /// control instead lets the leg ease onto it and then run straight, which arrives on the axis.
    /// </remarks>
    [Theory]
    [InlineData(0.0, TravelDirection.Forward)]
    [InlineData(30.0, TravelDirection.Forward)]
    [InlineData(30.0, TravelDirection.Reverse)]
    public void AStoredExitHeadingIsDrivenOntoExactly(double startHeadingDegrees, TravelDirection direction)
    {
        var (vehicle, mode) = Preset("BUS12", "A");
        var state = new VehicleState(
            new Point3(0, 0, 0), startHeadingDegrees * Math.PI / 180.0, 0.0, direction, 0.0);
        var wanted = (startHeadingDegrees + 90.0) * Math.PI / 180.0;

        var control = new ManoeuvreControl(new Point3(2, 2, 0), direction, ExitHeadingRadians: wanted);
        control = JsonSerializer.Deserialize<ManoeuvreControl>(JsonSerializer.Serialize(control))!;

        var leg = ManoeuvreReplayService.PlanControl(
            vehicle, mode, [ManoeuvreReplayService.StateSample(state, vehicle)], 0, state, control);

        var arrived = Geometry2D.NormalizeAngle(leg.Samples[^1].PathHeadingRadians - wanted);
        Assert.InRange(Math.Abs(arrived), 0.0, 2e-4);
        Assert.Equal(direction, leg.EndState.Direction);
    }

    /// <summary>
    /// A leg driven straight after a change of gear stays in the new gear, and starts where the
    /// vehicle actually stands.
    /// </summary>
    /// <remarks>
    /// The ease-out rewinds into the corner by reconstructing the vehicle from a stored route sample,
    /// and a sample carries the gear it was driven in. At a cusp the last stored sample still
    /// describes the <em>arriving</em> gear, so rewinding into it drove the ease in the direction the
    /// vehicle had just stopped travelling in — the reversing leg silently turned back into a forward
    /// one. Past a cusp the ease is therefore driven from the end rather than rewound into.
    /// </remarks>
    [Theory]
    [InlineData(ManoeuvreControlKind.Aim)]
    [InlineData(ManoeuvreControlKind.Finish)]
    public void AGearChangeIsNotUndoneByTheEaseThatFollowsIt(ManoeuvreControlKind kind)
    {
        var (vehicle, mode) = Preset("PV", "B");
        var start = new VehicleState(new Point3(0, 0, 0), 0.0, 0.0, TravelDirection.Forward, 0.0);
        var cornered = LockedTurnGenerator.Turn(vehicle, mode, start, Math.PI / 3.0);

        var state = cornered.EndState with { Direction = TravelDirection.Reverse };
        var travelling = state.VehicleHeadingRadians + Math.PI;
        var target = new Point3(
            state.RearAxleCentreMetres.X + (20.0 * Math.Cos(travelling)),
            state.RearAxleCentreMetres.Y + (20.0 * Math.Sin(travelling)),
            state.RearAxleCentreMetres.Z);

        var leg = ManoeuvreReplayService.PlanControl(
            vehicle, mode, cornered.Samples, cornered.Samples.Count - 1, state,
            new ManoeuvreControl(target, state.Direction, kind));

        // Nothing before the cusp is given back, and every sample of the new leg is in the new gear.
        Assert.Equal(cornered.Samples.Count - 1, leg.FromIndex);
        Assert.Equal(state.RearAxleCentreMetres, leg.Samples[0].PositionMetres);
        Assert.All(leg.Samples, sample => Assert.Equal(TravelDirection.Reverse, sample.Direction));
        Assert.Equal(TravelDirection.Reverse, leg.EndState.Direction);
    }
}
