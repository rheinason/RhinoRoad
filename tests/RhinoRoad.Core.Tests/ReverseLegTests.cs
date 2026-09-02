using RhinoRoad.Core;

namespace RhinoRoad.Core.Tests;

/// <summary>
/// Pins manoeuvres that reverse partway through — the three-point turn.
/// </summary>
/// <remarks>
/// The cusp is the whole difficulty: the vehicle must carry its pose and its wheel across the
/// reversal rather than being replanted on the next line, and the reversal must not read as a fault
/// to the checks that look for discontinuities. A vehicle stopping and backing is not a vehicle
/// teleporting.
/// </remarks>
public sealed class ReverseLegTests
{
    private static (VehicleDefinition Vehicle, DrivingModeDefinition Mode) Preset(
        string id = "PV",
        string mode = "B")
    {
        var vehicle = VehicleCatalog.LoadEmbedded().Get(id);
        return (vehicle, vehicle.DrivingModes[mode]);
    }

    private static IntentPath Line(Point3 from, Point3 to, double spacing = 0.05)
    {
        var length = from.XY.DistanceTo(to.XY);
        var count = Math.Max(2, (int)(length / spacing));
        var points = new List<Point3>();
        for (var index = 0; index <= count; index++)
        {
            var t = (double)index / count;
            points.Add(new Point3(
                from.X + ((to.X - from.X) * t),
                from.Y + ((to.Y - from.Y) * t),
                from.Z + ((to.Z - from.Z) * t)));
        }

        return IntentPath.FromPoints(points);
    }

    /// <summary>Forward east, back up to the north-west, then forward north — a three-point turn.</summary>
    private static IReadOnlyList<RouteLeg> ThreePointTurn() =>
    [
        new RouteLeg(Line(new Point3(0, 0, 0), new Point3(12, 0, 0)), TravelDirection.Forward),
        new RouteLeg(Line(new Point3(12, 0, 0), new Point3(4, 6, 0)), TravelDirection.Reverse),
        new RouteLeg(Line(new Point3(4, 6, 0), new Point3(4, 20, 0)), TravelDirection.Forward)
    ];

    [Fact]
    public void AManoeuvreCanReversePartwayThrough()
    {
        var (vehicle, mode) = Preset();

        var route = PathFollower.FollowLegs(vehicle, mode, ThreePointTurn());

        Assert.Contains(route.Samples, sample => sample.Direction == TravelDirection.Forward);
        Assert.Contains(route.Samples, sample => sample.Direction == TravelDirection.Reverse);
    }

    /// <summary>The route must not jump at the cusp: the vehicle is where it stopped.</summary>
    [Fact]
    public void ThePositionIsContinuousAcrossACusp()
    {
        var (vehicle, mode) = Preset();

        var route = PathFollower.FollowLegs(vehicle, mode, ThreePointTurn());

        for (var index = 1; index < route.Samples.Count; index++)
        {
            var gap = route.Samples[index].PositionMetres.XY
                .DistanceTo(route.Samples[index - 1].PositionMetres.XY);
            Assert.True(gap <= PathFollower.StepMetres + 1e-9, $"jumped {gap:0.000} m at sample {index}");
        }
    }

    [Fact]
    public void StationsKeepIncreasingAcrossACusp()
    {
        var (vehicle, mode) = Preset();

        var route = PathFollower.FollowLegs(vehicle, mode, ThreePointTurn());

        var previous = -1.0;
        foreach (var sample in route.Samples)
        {
            Assert.True(sample.StationMetres >= previous);
            previous = sample.StationMetres;
        }
    }

    /// <summary>
    /// The wheel does not move while the vehicle changes direction, so the steering angle either
    /// side of a cusp is the same angle — described from the other end, which flips the curvature.
    /// A check that read that flip as wheel movement would fail every three-point turn.
    /// </summary>
    [Fact]
    public void ACuspIsNotSeenAsTheWheelJumping()
    {
        var (vehicle, mode) = Preset();
        var route = PathFollower.FollowLegs(vehicle, mode, ThreePointTurn());
        var secondsPerStep = PathFollower.StepMetres / mode.SpeedMetresPerSecond;
        var previous = 0.0;

        foreach (var sample in route.Samples)
        {
            var steering = Math.Atan(
                sample.SignedCurvaturePerMetre * vehicle.WheelbaseMetres * (double)sample.Direction);
            Assert.True(
                Math.Abs(steering - previous) / secondsPerStep <= mode.MaximumSteeringRateRadiansPerSecond + 1e-9,
                "the steering rate limit was breached across a cusp");
            previous = steering;
        }
    }

    [Fact]
    public void AReversingManoeuvreRaisesNoDiscontinuityViolation()
    {
        var (vehicle, mode) = Preset();
        var route = PathFollower.FollowLegs(vehicle, mode, ThreePointTurn());

        var analysis = new VehicleAccessAnalyzer().Analyze(vehicle, mode, route.Samples);

        Assert.DoesNotContain(
            analysis.Violations,
            violation => violation.Kind == ViolationKind.TangentDiscontinuity);
    }

    /// <summary>
    /// Given a leg long enough to converge on, the vehicle settles onto it after the cusp.
    /// </summary>
    /// <remarks>
    /// It cannot do so immediately: leaving a cusp the wheel is wherever the reversing leg left it,
    /// and centring it costs metres of travel. What matters is that the error is worked off rather
    /// than carried, so the test gives the last leg room and checks where it ends up.
    /// </remarks>
    [Fact]
    public void TheVehicleSettlesOntoTheLastLegOnceItHasRoom()
    {
        var (vehicle, mode) = Preset();
        IReadOnlyList<RouteLeg> turn =
        [
            new RouteLeg(Line(new Point3(0, 0, 0), new Point3(12, 0, 0)), TravelDirection.Forward),
            new RouteLeg(Line(new Point3(12, 0, 0), new Point3(4, 6, 0)), TravelDirection.Reverse),
            new RouteLeg(Line(new Point3(4, 6, 0), new Point3(4, 80, 0)), TravelDirection.Forward)
        ];

        var route = PathFollower.FollowLegs(vehicle, mode, turn);

        // The last leg runs due north, and the vehicle has 74 m to get onto it.
        var finalHeading = route.Samples[^1].PathHeadingRadians * 180.0 / Math.PI;
        Assert.InRange(finalHeading, 85.0, 95.0);

        var finish = route.Samples[^1].PositionMetres.XY;
        Assert.True(
            Math.Abs(finish.X - 4.0) < 0.25,
            $"finished {Math.Abs(finish.X - 4.0):0.000} m off the line it was following");
    }

    [Fact]
    public void ASingleLegMatchesFollowingThatLineOnItsOwn()
    {
        var (vehicle, mode) = Preset();
        var intent = Line(new Point3(0, 0, 0), new Point3(30, 0, 0));

        var single = PathFollower.Follow(vehicle, mode, intent);
        var asLeg = PathFollower.FollowLegs(vehicle, mode, [new RouteLeg(intent, TravelDirection.Forward)]);

        Assert.Equal(single.Samples, asLeg.Samples);
    }

    [Fact]
    public void AManoeuvreNeedsAtLeastOneLeg()
    {
        var (vehicle, mode) = Preset();

        Assert.Throws<ArgumentException>(() => PathFollower.FollowLegs(vehicle, mode, []));
    }
}
