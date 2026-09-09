using RhinoRoad.Core;

namespace RhinoRoad.Core.Tests;

/// <summary>
/// Pins the turn a vehicle makes at its wheel's lock, and the three-point turn built out of them.
/// </summary>
/// <remarks>
/// <para>
/// The property being defended is that a turn asked for by size arrives at that size, on the
/// tightest radius the mode allows. Aiming at a point cannot do this: the arc through the point has
/// to pay for its own lock-up out of its own length, so at minimum radius a PV finishes a
/// half-circle only 102 degrees round, and more turn is available only by clicking further out on a
/// slacker radius. <see cref="TightestAimedTurnFallsWellShortOfAUTurn"/> keeps that measurement
/// honest, because it is the reason this generator exists.
/// </para>
/// </remarks>
public sealed class LockedTurnTests
{
    private static (VehicleDefinition Vehicle, DrivingModeDefinition Mode) Preset(
        string id = "PV",
        string mode = "A")
    {
        var vehicle = VehicleCatalog.LoadEmbedded().Get(id);
        return (vehicle, vehicle.DrivingModes[mode]);
    }

    private static VehicleState Start(
        double headingDegrees = 0.0,
        TravelDirection direction = TravelDirection.Forward) =>
        new(new Point3(0, 0, 0), headingDegrees * Math.PI / 180.0, 0.0, direction, 0.0);

    private static double Degrees(double radians) => radians * 180.0 / Math.PI;

    /// <summary>Movement heading swung over a leg, unwrapped so a turn past half a circle counts.</summary>
    private static double SweptDegrees(IReadOnlyList<RouteSample> samples)
    {
        var total = 0.0;
        for (var index = 1; index < samples.Count; index++)
            total += Geometry2D.NormalizeAngle(
                samples[index].PathHeadingRadians - samples[index - 1].PathHeadingRadians);
        return Degrees(total);
    }

    /// <summary>
    /// Heading swung by the vehicle body, unwrapped. Unlike the path heading this does not jump at a
    /// cusp, so it is the measure of what a manoeuvre containing reversals actually turned through.
    /// </summary>
    private static double VehicleSweptDegrees(IReadOnlyList<RouteSample> samples)
    {
        static double Body(RouteSample sample) => sample.PathHeadingRadians
            - (sample.Direction == TravelDirection.Reverse ? Math.PI : 0.0);

        var total = 0.0;
        for (var index = 1; index < samples.Count; index++)
            total += Geometry2D.NormalizeAngle(Body(samples[index]) - Body(samples[index - 1]));
        return Degrees(total);
    }

    [Theory]
    [InlineData("PV", "A")]
    [InlineData("PV", "B")]
    [InlineData("REN", "A")]
    [InlineData("REN", "B")]
    [InlineData("BUS12", "A")]
    [InlineData("BUS12", "B")]
    public void ATurnArrivesAtTheSizeItWasAskedFor(string id, string modeId)
    {
        var (vehicle, mode) = Preset(id, modeId);
        foreach (var wanted in new[] { 30.0, 90.0, 180.0, 270.0, -90.0, -180.0 })
        {
            var leg = LockedTurnGenerator.Turn(vehicle, mode, Start(), wanted * Math.PI / 180.0);
            Assert.Equal(wanted, SweptDegrees(leg.Samples), 2);
        }
    }

    /// <summary>
    /// The wheel is still out at the end, because straightening belongs to the control that follows.
    /// Unwinding here would open out the very turn that was asked to be tight.
    /// </summary>
    [Fact]
    public void ATurnEndsStillAtLock()
    {
        var (vehicle, mode) = Preset();
        var leg = LockedTurnGenerator.Turn(vehicle, mode, Start(), Math.PI);
        Assert.Equal(Degrees(mode.MaximumWheelAngleRadians), Degrees(leg.EndState.SteeringAngleRadians), 3);
    }

    /// <summary>
    /// A U-turn ends the vehicle offset by a diameter — wider than the ideal one, because the wheel
    /// spends the first metres of the turn getting to lock and the vehicle runs wide while it does.
    /// The gap is the lock-up cost, and it is bounded: never more than the lock-up travel itself.
    /// </summary>
    [Theory]
    [InlineData("PV", "A")]
    [InlineData("PV", "B")]
    [InlineData("BUS12", "A")]
    [InlineData("BUS12", "B")]
    public void AUTurnClearsAboutTheVehiclesTurningDiameter(string id, string modeId)
    {
        var (vehicle, mode) = Preset(id, modeId);
        var ideal = 2.0 * TurningGeometryCalculator.AtFullLock(vehicle, mode).RearAxleRadiusMetres;
        var lockUpTravel = mode.MaximumWheelAngleRadians / mode.MaximumSteeringRateRadiansPerSecond
            * mode.SpeedMetresPerSecond;

        var leg = LockedTurnGenerator.Turn(vehicle, mode, Start(), Math.PI);
        var offset = leg.EndState.RearAxleCentreMetres.XY.Length;

        Assert.True(offset >= ideal, $"{id}/{modeId}: {offset:0.00} m is tighter than the {ideal:0.00} m minimum.");
        Assert.True(
            offset <= ideal + lockUpTravel,
            $"{id}/{modeId}: {offset:0.00} m exceeds {ideal:0.00} m by more than the {lockUpTravel:0.00} m lock-up travel.");
    }

    /// <summary>
    /// The measurement that motivates the whole control: the tightest turn a single aimed click can
    /// produce is nowhere near a U-turn, and clicking further out only widens the radius.
    /// </summary>
    [Fact]
    public void TightestAimedTurnFallsWellShortOfAUTurn()
    {
        var (vehicle, mode) = Preset();
        var radius = TurningGeometryCalculator.AtFullLock(vehicle, mode).RearAxleRadiusMetres;
        var generator = new RateLimitedTrajectoryGenerator();

        var abeam = new Point2(0.0, 2.0 * radius);
        var aimed = RateLimitedTrajectoryGenerator.ControlsFromCursor(vehicle, mode, Start(), abeam);
        var leg = generator.GenerateLeg(
            vehicle, mode, Start(), aimed.TargetSteeringRadians, aimed.TravelDistanceMetres);

        Assert.InRange(SweptDegrees(leg.Samples), 95.0, 110.0);
        Assert.Equal(180.0, SweptDegrees(LockedTurnGenerator.Turn(vehicle, mode, Start(), Math.PI).Samples), 2);
    }

    /// <summary>
    /// Aiming inside the turning circle asks for a radius the vehicle cannot hold, and what it gets
    /// back is the same amount of turn on the tightest circle it can — the same leg, wherever inside
    /// the circle the cursor sits.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Computing the leg's length from the <em>requested</em> curvature while steering at the
    /// clamped one made this a cliff instead of a plateau. A PV in mode A aimed abeam at a fifth of
    /// its turning diameter drove 4 degrees where aiming at the full diameter drove 102, so pulling
    /// the cursor in — the natural reflex when a corner comes out too wide — opened the corner
    /// further rather than tightening it.
    /// </para>
    /// <para>
    /// Outside the circle the turn still grows with range, and always will: an abeam arc is a
    /// half-circle at any radius, but the wheel has to reach lock out of the arc's own length, so a
    /// longer arc recovers more of the 180 degrees. That is the limit this generator exists to
    /// escape, not a defect to be tuned away.
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData("PV", "A")]
    [InlineData("REN", "A")]
    [InlineData("BUS12", "B")]
    public void AimingInsideTheTurningCircleGivesTheTightestTurnAvailable(string id, string modeId)
    {
        var (vehicle, mode) = Preset(id, modeId);
        var radius = TurningGeometryCalculator.AtFullLock(vehicle, mode).RearAxleRadiusMetres;
        var generator = new RateLimitedTrajectoryGenerator();

        double Abeam(double diameters)
        {
            var cursor = new Point2(0.0, 2.0 * radius * diameters);
            var aimed = RateLimitedTrajectoryGenerator.ControlsFromCursor(vehicle, mode, Start(), cursor);
            return SweptDegrees(generator
                .GenerateLeg(vehicle, mode, Start(), aimed.TargetSteeringRadians, aimed.TravelDistanceMetres)
                .Samples);
        }

        var onTheCircle = Abeam(1.0);
        for (var diameters = 0.2; diameters < 1.0; diameters += 0.1)
            Assert.Equal(onTheCircle, Abeam(diameters), 3);

        var previous = 0.0;
        for (var diameters = 0.2; diameters <= 3.0; diameters += 0.1)
        {
            var swept = Abeam(diameters);

            // A thousandth of a degree of slack. Two clicks on the plateau drive the same leg but
            // subdivide it into a different number of integration steps, so they agree to about a
            // millidegree rather than to the bit. The cliff this guards against was tens of degrees.
            Assert.True(
                swept >= previous - 1e-3,
                $"{id}/{modeId}: aiming abeam at {diameters:0.0} diameters turned {swept:0.0} deg, "
                + $"less than the tighter click's {previous:0.0} deg.");
            previous = swept;
        }
    }

    [Fact]
    public void AbeamAsksForAUTurnAndBehindAsksForMore()
    {
        var state = Start();
        Assert.Equal(180.0, Degrees(LockedTurnGenerator.SweepFromCursor(state, new Point2(0.0, 9.0))), 6);
        Assert.Equal(-180.0, Degrees(LockedTurnGenerator.SweepFromCursor(state, new Point2(0.0, -9.0))), 6);
        Assert.Equal(90.0, Degrees(LockedTurnGenerator.SweepFromCursor(state, new Point2(9.0, 9.0))), 6);
        Assert.Equal(270.0, Degrees(LockedTurnGenerator.SweepFromCursor(state, new Point2(-9.0, 9.0))), 6);
    }

    /// <summary>
    /// Range must not enter into it. An aimed leg grows without bound as the cursor recedes; a
    /// locked turn is the same turn whether the cursor is five metres away or five hundred.
    /// </summary>
    [Fact]
    public void RangeDoesNotChangeALockedTurn()
    {
        var state = Start();
        var near = LockedTurnGenerator.SweepFromCursor(state, new Point2(1.0, 3.0));
        var far = LockedTurnGenerator.SweepFromCursor(state, new Point2(100.0, 300.0));
        Assert.Equal(near, far, 9);
    }

    /// <summary>A sweep typed on the command line survives being stored as a control point.</summary>
    [Theory]
    [InlineData(45.0)]
    [InlineData(180.0)]
    [InlineData(-180.0)]
    [InlineData(300.0)]
    public void ATypedSweepRoundTripsThroughItsControlPoint(double degrees)
    {
        var state = Start(headingDegrees: 37.0);
        var point = LockedTurnGenerator.CursorForSweep(state, degrees * Math.PI / 180.0, 8.0);
        Assert.Equal(degrees, Degrees(LockedTurnGenerator.SweepFromCursor(state, point.XY)), 6);
    }

    /// <summary>
    /// Reversing turns the vehicle the other way for the same wheel angle, so the sweep is stated
    /// against the direction of travel and the caller does not have to think about it. This is what
    /// lets a three-point turn be three locked turns with the travel direction flipped between them.
    /// </summary>
    [Theory]
    [InlineData(90.0)]
    [InlineData(-90.0)]
    [InlineData(180.0)]
    public void ReversingSwingsTheDirectionOfTravelTheWayAsked(double degrees)
    {
        var (vehicle, mode) = Preset();
        var leg = LockedTurnGenerator.Turn(
            vehicle, mode, Start(direction: TravelDirection.Reverse), degrees * Math.PI / 180.0);
        Assert.Equal(degrees, SweptDegrees(leg.Samples), 2);
    }

    /// <summary>
    /// A three-point turn, driven the way the command drives one: swing forward at lock, reverse at
    /// lock, swing forward again. It has to end reversed in heading and it has to fit in less width
    /// than a U-turn, which is the entire reason for making one.
    /// </summary>
    /// <summary>
    /// A three-point turn, driven the way the command drives one: swing forward at lock, reverse at
    /// lock, swing forward again. Whatever the split, it has to end the vehicle exactly reversed.
    /// </summary>
    /// <remarks>
    /// This is the case that catches a locked turn crediting itself with the wrong-way rotation it
    /// makes while the wheel crosses centre at a cusp. Measuring progress by size rather than by
    /// direction, a PV asked for 55 degrees on the reverse leg finished 3.6 degrees the other way,
    /// and the manoeuvre came out 71 degrees round instead of 180. Every forward-only test passes
    /// with that fault present, because a leg starting from a centred wheel never turns the wrong
    /// way at all.
    /// </remarks>
    [Theory]
    [InlineData("PV", "A")]
    [InlineData("PV", "B")]
    [InlineData("REN", "A")]
    [InlineData("REN", "B")]
    [InlineData("BUS12", "A")]
    [InlineData("BUS12", "B")]
    public void AThreePointTurnReversesTheHeadingExactly(string id, string modeId)
    {
        var (vehicle, mode) = Preset(id, modeId);
        foreach (var split in new[]
        {
            new[] { 100.0, 55.0, 25.0 },
            new[] { 90.0, 45.0, 45.0 },
            new[] { 120.0, 30.0, 30.0 }
        })
        {
            var journey = Compose(vehicle, mode,
            [
                (TravelDirection.Forward, split[0]),
                (TravelDirection.Reverse, split[1]),
                (TravelDirection.Forward, split[2])
            ]);

            // Measured on the vehicle's own heading, not on its direction of travel: the two differ
            // by half a turn while reversing, so a path heading would count each cusp as 180 degrees
            // of turn that the vehicle never made.
            var replayed = ManoeuvreReplayService.Replay(vehicle, mode, journey);
            Assert.Equal(180.0, VehicleSweptDegrees(replayed.Samples), 1);
        }
    }

    /// <summary>
    /// Shuffling beats turning round, for every preset in both modes. Records the measurement,
    /// because it decides when the manoeuvre is worth offering at all — and because it used to be
    /// the other way about.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A three-point turn saves width by trading it for length, but only if the wheel can reach its
    /// lock inside a leg. Until the wheel could be turned at a standstill it usually could not: the
    /// wheel needs 12.5 m of travel to reach full lock in mode A and no leg of a shuffle is that
    /// long, so every leg was driven on a radius far wider than the minimum and the manoeuvre
    /// sprawled — 1.2 to 2.3 times the width of simply turning round. Only the heavy presets in mode
    /// B, where lock-up costs 4.2 m, came in under the U-turn.
    /// </para>
    /// <para>
    /// A change of direction is a standstill, so the wheel now arrives at each leg already at lock
    /// and the legs are driven on the minimum radius they were always meant to be. Every preset now
    /// gains, from 1.9 m on PV in mode B to 6.4 m on BUS 15 in mode A.
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData("PV", "A")]
    [InlineData("PV", "B")]
    [InlineData("REN", "A")]
    [InlineData("REN", "B")]
    [InlineData("BUS12", "A")]
    [InlineData("BUS12", "B")]
    [InlineData("BUS15", "A")]
    public void ShufflingBeatsAUTurnBecauseTheWheelIsTurnedAtEachStop(string id, string modeId)
    {
        var (vehicle, mode) = Preset(id, modeId);

        var uTurn = AcrossMetres(Compose(vehicle, mode, [(TravelDirection.Forward, 180.0)]), vehicle, mode);
        var shuffle = AcrossMetres(
            Compose(vehicle, mode,
            [
                (TravelDirection.Forward, 120.0),
                (TravelDirection.Reverse, 30.0),
                (TravelDirection.Forward, 30.0)
            ]),
            vehicle,
            mode);

        Assert.True(shuffle < uTurn, $"shuffle {shuffle:0.00} m against a {uTurn:0.00} m U-turn");
        Assert.True(uTurn - shuffle >= 1.5, $"only saved {uTurn - shuffle:0.00} m");
    }

    /// <summary>Width the manoeuvre needs across the road the vehicle started on.</summary>
    private static double AcrossMetres(
        ManoeuvreDefinition journey,
        VehicleDefinition vehicle,
        DrivingModeDefinition mode)
    {
        var samples = ManoeuvreReplayService.Replay(vehicle, mode, journey).Samples;
        return samples.Max(sample => sample.PositionMetres.Y) - samples.Min(sample => sample.PositionMetres.Y);
    }

    /// <summary>
    /// Turns a list of sweeps into a journey, placing each control against the state its predecessor
    /// left the vehicle in — the same order of events as clicking them one at a time.
    /// </summary>
    private static ManoeuvreDefinition Compose(
        VehicleDefinition vehicle,
        DrivingModeDefinition mode,
        IReadOnlyList<(TravelDirection Direction, double Degrees)> sweeps)
    {
        var state = Start();
        var route = new List<RouteSample> { ManoeuvreReplayService.StateSample(state, vehicle) };
        var controls = new List<ManoeuvreControl>();
        var legStartIndex = 0;
        foreach (var (direction, degrees) in sweeps)
        {
            if (state.Direction != direction)
            {
                state = state with { Direction = direction };
                legStartIndex = route.Count - 1;
            }

            var control = new ManoeuvreControl(
                LockedTurnGenerator.CursorForSweep(state, degrees * Math.PI / 180.0, 10.0),
                direction,
                ManoeuvreControlKind.Turn);
            var planned = ManoeuvreReplayService.PlanControl(vehicle, mode, route, legStartIndex, state, control);
            route.AddRange(planned.Samples.Skip(1));
            legStartIndex = route.Count - 1;
            state = planned.EndState;
            controls.Add(control);
        }

        return new ManoeuvreDefinition(
            ManoeuvreDefinition.CurrentSchemaVersion,
            new Point3(0, 0, 0),
            0.0,
            TravelDirection.Forward,
            controls);
    }

    /// <summary>
    /// A journey holding locked turns replays to the route it was recorded as. Replay is what a
    /// saved journey is re-driven through, so a control the interactive command can create but
    /// replay cannot reproduce would silently change shape on reopening.
    /// </summary>
    [Fact]
    public void LockedTurnsReplayIdentically()
    {
        var (vehicle, mode) = Preset();
        var journey = new ManoeuvreDefinition(
            ManoeuvreDefinition.CurrentSchemaVersion,
            new Point3(0, 0, 0),
            0.3,
            TravelDirection.Forward,
            [
                new ManoeuvreControl(new Point3(20, 4, 0), TravelDirection.Forward),
                new ManoeuvreControl(new Point3(20, 30, 0), TravelDirection.Forward, ManoeuvreControlKind.Turn),
                new ManoeuvreControl(new Point3(-10, 20, 0), TravelDirection.Forward)
            ]);

        var first = ManoeuvreReplayService.Replay(vehicle, mode, journey);
        var second = ManoeuvreReplayService.Replay(vehicle, mode, journey);

        Assert.Equal(first.Samples.Count, second.Samples.Count);
        Assert.Equal(first.EndState.RearAxleCentreMetres.X, second.EndState.RearAxleCentreMetres.X, 9);
        Assert.Equal(first.EndState.RearAxleCentreMetres.Y, second.EndState.RearAxleCentreMetres.Y, 9);
    }

    /// <summary>
    /// The control after a locked turn opens the turn out from inside it, like any other corner.
    /// </summary>
    /// <remarks>
    /// Barring the rewind was tried, on the argument that a turn asked for at a size should keep
    /// that size. It leaves the exit to be corrected from the end of the turn rather than opened out
    /// of its middle, which is the S the whole model exists to avoid, and it looked it: the same
    /// U-turn came back with a kinked, splayed exit where letting it rewind gives a clean parallel
    /// return.
    /// </remarks>
    [Fact]
    public void AFollowingControlEasesOutOfALockedTurn()
    {
        var (vehicle, mode) = Preset();
        var route = new List<RouteSample>
        {
            ManoeuvreReplayService.StateSample(Start(), vehicle)
        };

        var turn = ManoeuvreReplayService.PlanControl(
            vehicle, mode, route, 0, Start(),
            new ManoeuvreControl(new Point3(0, 10, 0), TravelDirection.Forward, ManoeuvreControlKind.Turn));
        route.AddRange(turn.Samples.Skip(1));

        var end = route.Count - 1;
        var next = ManoeuvreReplayService.PlanControl(
            vehicle, mode, route, 0, turn.EndState,
            new ManoeuvreControl(new Point3(-30, 20, 0), TravelDirection.Forward));

        Assert.True(
            next.FromIndex < end,
            $"the exit was driven from the end of the turn (sample {next.FromIndex} of {end}) instead of out of it.");
    }

    /// <summary>
    /// Aiming at a point behind the beam is brought round to the beam, not looped round to the point.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Two faults met here. The arc length came from <c>2·asin(chord·k/2)</c>, which cannot exceed
    /// half a turn, so a point behind the beam — needing a reflex arc — was driven as its minor angle
    /// and the leg stopped at the mirror position, metres away, with nothing reporting a miss. Driving
    /// the reflex arc instead is geometrically right and practically useless: a circle through a point
    /// nearly dead astern is nearly straight, so reaching it meant driving half of a kilometre-wide
    /// loop — 358 m to reach a point 2 m behind, and 10.7 km to reach one 60 m behind.
    /// </para>
    /// <para>
    /// The domain of aiming is the half-plane ahead of the beam, and a request outside it is projected
    /// onto the boundary: same range, brought round to the beam, which is the hardest turn aiming has
    /// ever been able to express. Reversing is what walks a designer into this without their noticing,
    /// because it puts "behind the direction of travel" directly in front of the nose.
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData("PV", "B")]
    [InlineData("REN", "B")]
    [InlineData("BUS12", "A")]
    public void AimingBehindTheBeamIsBroughtRoundToIt(string id, string modeId)
    {
        var (vehicle, mode) = Preset(id, modeId);
        var radius = TurningGeometryCalculator.AtFullLock(vehicle, mode).RearAxleRadiusMetres;

        foreach (var range in new[] { 0.5, 2.0, 15.0, 60.0 })
        {
            var beam = RateLimitedTrajectoryGenerator.ControlsFromCursor(
                vehicle, mode, Start(), Cursor(90.0, range));
            Assert.False(beam.AimedBehindTheBeam);

            foreach (var bearing in new[] { 100.0, 140.0, 179.0, -100.0, -179.0 })
            {
                var behind = RateLimitedTrajectoryGenerator.ControlsFromCursor(
                    vehicle, mode, Start(), Cursor(bearing, range));

                Assert.True(behind.AimedBehindTheBeam, $"{bearing:0} degrees was not reported as behind the beam.");
                Assert.Equal(beam.TravelDistanceMetres, behind.TravelDistanceMetres, 9);
                Assert.Equal(
                    Math.Abs(beam.TargetSteeringRadians),
                    Math.Abs(behind.TargetSteeringRadians),
                    9);

                // Half a turn, on whichever circle applies: the one through the point, or the
                // tightest the vehicle can hold when that one is tighter still. Nothing the cursor
                // can do makes a leg larger than that.
                Assert.True(
                    behind.TravelDistanceMetres <= (Math.PI * Math.Max(range / 2.0, radius)) + 1e-6,
                    $"{id}/{modeId}: a click {range:0.0} m away at {bearing:0} degrees drove "
                    + $"{behind.TravelDistanceMetres:0.0} m.");
            }
        }
    }

    /// <summary>
    /// Ahead of the beam a leg is always the scale of the click that made it: at most pi/2 times the
    /// distance to the point, which is the arc-to-chord ratio of a half turn and the worst an arc
    /// through a point ahead of the beam can be.
    /// </summary>
    [Theory]
    [InlineData("PV", "B")]
    [InlineData("BUS12", "A")]
    public void AheadOfTheBeamALegIsTheScaleOfItsClick(string id, string modeId)
    {
        var (vehicle, mode) = Preset(id, modeId);
        var radius = TurningGeometryCalculator.AtFullLock(vehicle, mode).RearAxleRadiusMetres;

        foreach (var range in new[] { 1.0, 5.0, 20.0, 80.0 })
        foreach (var bearing in new[] { 0.0, 15.0, 45.0, 75.0, 89.0, -45.0, -89.0 })
        {
            var aimed = RateLimitedTrajectoryGenerator.ControlsFromCursor(
                vehicle, mode, Start(), Cursor(bearing, range));

            Assert.False(aimed.AimedBehindTheBeam);
            Assert.True(
                aimed.TravelDistanceMetres <= (Math.PI * Math.Max(range / 2.0, radius)) + 1e-6,
                $"{id}/{modeId}: a click {range:0.0} m away at {bearing:0} degrees drove "
                + $"{aimed.TravelDistanceMetres:0.0} m.");
        }
    }

    /// <summary>
    /// The leg a designer draws straight after reversing. It stays bounded and says why it did not
    /// reach the point, rather than swinging off across the site.
    /// </summary>
    [Fact]
    public void AForwardLegAfterReversingStaysBounded()
    {
        var (vehicle, mode) = Preset("REN", "B");
        var radius = TurningGeometryCalculator.AtFullLock(vehicle, mode).RearAxleRadiusMetres;

        // Reversing leaves the vehicle travelling away from where the next leg is wanted, so the
        // natural next click lands behind the direction of travel.
        var reversing = new VehicleState(
            new Point3(0, 0, 0), 0.0, 23.0 * Math.PI / 180.0, TravelDirection.Reverse, 0.0);
        var aimed = RateLimitedTrajectoryGenerator.ControlsFromCursor(
            vehicle, mode, reversing, new Point2(12.0, 4.0));

        Assert.True(aimed.AimedBehindTheBeam);
        Assert.True(
            aimed.TravelDistanceMetres <= Math.PI * Math.Max(Math.Sqrt(160.0) / 2.0, radius),
            $"the leg drove {aimed.TravelDistanceMetres:0.0} m.");
    }

    private static Point2 Cursor(double bearingDegrees, double range)
    {
        var radians = bearingDegrees * Math.PI / 180.0;
        return new Point2(Math.Cos(radians) * range, Math.Sin(radians) * range);
    }
}