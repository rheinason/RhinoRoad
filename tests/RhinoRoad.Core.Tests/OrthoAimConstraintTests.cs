using RhinoRoad.Core;

namespace RhinoRoad.Core.Tests;

/// <summary>
/// Pins the ortho constraint that squares up where a leg <em>leaves</em>, rather than where it is
/// aimed.
/// </summary>
/// <remarks>
/// Rhino's own ortho constrains the direction to the picked point, which is the wrong quantity here:
/// the vehicle drives an arc through that point and comes out turned by twice the bearing, so an
/// ortho band pointing north leaves it heading east. Constraining the departure heading instead is
/// the same projection applied to the quantity the designer means.
/// </remarks>
public sealed class OrthoAimConstraintTests
{
    private static (VehicleDefinition Vehicle, DrivingModeDefinition Mode) Preset(
        string id = "PV",
        string mode = "B")
    {
        var vehicle = VehicleCatalog.LoadEmbedded().Get(id);
        return (vehicle, vehicle.DrivingModes[mode]);
    }

    private static VehicleState At(double headingDegrees, TravelDirection direction = TravelDirection.Forward) =>
        new(new Point3(0, 0, 0), headingDegrees * Math.PI / 180.0, 0.0, direction, 0.0);

    private static double Degrees(double radians) => radians * 180.0 / Math.PI;

    /// <summary>The departure heading the aimed arc through a point asks for: twice its bearing.</summary>
    private static double ExitDegrees(VehicleState state, Point2 cursor)
    {
        var movementHeading = state.VehicleHeadingRadians +
            (state.Direction == TravelDirection.Reverse ? Math.PI : 0.0);
        var offset = cursor - state.RearAxleCentreMetres.XY;
        return Degrees(Geometry2D.NormalizeAngle(movementHeading + (2.0 * Geometry2D.NormalizeAngle(
            Math.Atan2(offset.Y, offset.X) - movementHeading))));
    }

    private static Point2 Cursor(double bearingDegrees, double range)
    {
        var radians = bearingDegrees * Math.PI / 180.0;
        return new Point2(Math.Cos(radians) * range, Math.Sin(radians) * range);
    }

    private const double Right = Math.PI / 2.0;

    [Theory]
    [InlineData(20.0, 0.0)]
    [InlineData(35.0, 90.0)]
    [InlineData(60.0, 90.0)]
    [InlineData(80.0, 180.0)]
    [InlineData(-35.0, -90.0)]
    [InlineData(-80.0, 180.0)]
    public void TheLegIsSquaredUpToWhereItLeaves(double bearingDegrees, double expectedExitDegrees)
    {
        var state = At(0.0);
        var snapped = OrthoAimConstraint.Snap(state, Cursor(bearingDegrees, 30.0), Right, 0.0);
        Assert.Equal(
            Geometry2D.NormalizeAngle(expectedExitDegrees * Math.PI / 180.0),
            Geometry2D.NormalizeAngle(ExitDegrees(state, snapped) * Math.PI / 180.0),
            9);
    }

    /// <summary>
    /// Ortho says which way to leave; the cursor keeps saying how much room the manoeuvre is given,
    /// because that is what decides whether the vehicle can make the turn at all.
    /// </summary>
    [Theory]
    [InlineData(5.0)]
    [InlineData(30.0)]
    [InlineData(250.0)]
    public void RangeSurvivesTheSnap(double range)
    {
        var state = At(0.0);
        var snapped = OrthoAimConstraint.Snap(state, Cursor(37.0, range), Right, 0.0);
        Assert.Equal(range, snapped.Length, 9);
    }

    /// <summary>
    /// Whatever the ortho angle, the departure heading lands on a multiple of it — from any starting
    /// heading, forwards or reversing, and behind the beam as well as ahead of it.
    /// </summary>
    [Theory]
    [InlineData(90.0)]
    [InlineData(45.0)]
    [InlineData(30.0)]
    [InlineData(15.0)]
    public void EveryAngleLandsOnAMultipleOfTheOrthoSetting(double orthoDegrees)
    {
        var ortho = orthoDegrees * Math.PI / 180.0;
        foreach (var heading in new[] { 0.0, 37.0, -110.0, 179.0 })
        foreach (var direction in new[] { TravelDirection.Forward, TravelDirection.Reverse })
        foreach (var bearing in new[] { 5.0, 44.0, 91.0, 137.0, -68.0, -152.0 })
        {
            var state = At(heading, direction);
            var exit = ExitDegrees(state, OrthoAimConstraint.Snap(state, Cursor(bearing, 25.0), ortho, 0.0));
            var remainder = Math.IEEERemainder(exit, orthoDegrees);
            Assert.Equal(0.0, remainder, 6);
        }
    }

    /// <summary>
    /// Angles are counted from the construction plane's X axis, not the world's. A site drawn on a
    /// rotated CPlane squares up to the site; squaring it to the world would be square to nothing the
    /// designer can see.
    /// </summary>
    [Fact]
    public void AnglesAreCountedFromTheConstructionPlane()
    {
        var state = At(0.0);
        var reference = 20.0 * Math.PI / 180.0;

        // 70 degrees of departure is nearest 90 in the world, but nearest 110 on a plane rotated 20.
        var cursor = Cursor(35.0, 30.0);
        Assert.Equal(90.0, ExitDegrees(state, OrthoAimConstraint.Snap(state, cursor, Right, 0.0)), 6);
        Assert.Equal(110.0, ExitDegrees(state, OrthoAimConstraint.Snap(state, cursor, Right, reference)), 6);
    }

    /// <summary>
    /// Held with a locked turn, the departure heading is not merely asked for but reached: a locked
    /// turn drives its sweep exactly, so the vehicle really does leave on the axis.
    /// </summary>
    /// <remarks>
    /// An aimed leg only asks. The wheel takes metres to reach the lock the arc needs, so a leg given
    /// too little room falls short of its snapped heading — which is the model being honest, not the
    /// constraint failing, and it is why <see cref="AnAimedLegAsksForTheAxisAndReportsWhatItReached"/>
    /// measures the gap rather than asserting it away.
    /// </remarks>
    [Theory]
    [InlineData("PV", "A")]
    [InlineData("REN", "B")]
    [InlineData("BUS12", "A")]
    public void ALockedTurnActuallyArrivesOnTheAxis(string id, string modeId)
    {
        var (vehicle, mode) = Preset(id, modeId);
        foreach (var heading in new[] { 0.0, 37.0, -110.0 })
        foreach (var bearing in new[] { 30.0, 62.0, 100.0, -48.0 })
        {
            var state = At(heading);
            var snapped = OrthoAimConstraint.Snap(state, Cursor(bearing, 20.0), Right, 0.0);
            var leg = LockedTurnGenerator.Turn(
                vehicle, mode, state, LockedTurnGenerator.SweepFromCursor(state, snapped));

            var exit = Degrees(Geometry2D.NormalizeAngle(leg.EndState.VehicleHeadingRadians));
            Assert.Equal(0.0, Math.IEEERemainder(exit, 90.0), 2);
        }
    }

    /// <summary>
    /// The whole squared-up pick, end to end: the direction the cursor names is carried on the control
    /// and the leg is driven onto it, so a squared leg departs on the axis whatever room it is given.
    /// </summary>
    /// <remarks>
    /// This is what the command builds. Moving the cursor onto the direction is only half of it —
    /// <see cref="AnAimedLegAlonePlacesTheArcButCannotHoldIt"/> measures what that half is worth — and
    /// the heading rides on the control so the leg can ease onto it and then run straight.
    /// </remarks>
    [Theory]
    [InlineData("BUS12", "A")]
    [InlineData("PV", "A")]
    [InlineData("PV", "B")]
    public void ASquaredPickDepartsOnTheAxis(string id, string modeId)
    {
        var (vehicle, mode) = Preset(id, modeId);
        const double ortho = Math.PI / 2.0;

        foreach (var reference in new[] { 0.0, 20.0 * Math.PI / 180.0 })
        foreach (var startHeading in new[] { 0.0, 37.0 })
        foreach (var range in new[] { 15.0, 60.0 })
        foreach (var bearing in new[] { 12.0, 38.0, 70.0, -38.0 })
        {
            var state = At(startHeading);
            var route = new List<RouteSample> { ManoeuvreReplayService.StateSample(state, vehicle) };
            var picked = Cursor(startHeading + bearing, range);

            var wanted = OrthoAimConstraint.SnapExitHeading(state, picked, ortho, reference);
            Assert.NotNull(wanted);

            var leg = ManoeuvreReplayService.PlanControl(
                vehicle, mode, route, 0, state,
                new ManoeuvreControl(
                    new Point3(picked.X, picked.Y, 0.0),
                    TravelDirection.Forward,
                    ManoeuvreControlKind.Aim,
                    wanted));

            var arrived = Geometry2D.NormalizeAngle(leg.Samples[^1].PathHeadingRadians - wanted.Value);
            Assert.InRange(Math.Abs(Degrees(arrived)), 0.0, 0.01);
        }
    }

    /// <summary>
    /// Moving the cursor alone asks for the axis and gets as close as the room allows. Recorded,
    /// because it is the measure of what carrying the heading on the control is worth.
    /// </summary>
    [Fact]
    public void AnAimedLegAlonePlacesTheArcButCannotHoldIt()
    {
        var (vehicle, mode) = Preset("BUS12", "A");
        var generator = new RateLimitedTrajectoryGenerator();

        double Reached(double range)
        {
            var state = At(0.0);
            var snapped = OrthoAimConstraint.Snap(state, Cursor(38.0, range), Right, 0.0);
            Assert.Equal(90.0, ExitDegrees(state, snapped), 6);

            var aimed = RateLimitedTrajectoryGenerator.ControlsFromCursor(vehicle, mode, state, snapped);
            var leg = generator.GenerateLeg(
                vehicle, mode, state, aimed.TargetSteeringRadians, aimed.TravelDistanceMetres);
            return Degrees(Geometry2D.NormalizeAngle(leg.EndState.VehicleHeadingRadians));
        }

        // Too little room and the wheel never reaches the lock the arc needs: 15 m of it buys a
        // BUS12 in mode A 55 degrees of the 90 it was squared to.
        Assert.InRange(Reached(15.0), 50.0, 60.0);

        // Given room, the aimed leg converges on the axis it was squared to.
        Assert.InRange(Reached(60.0), 86.0, 90.0);
    }
}
