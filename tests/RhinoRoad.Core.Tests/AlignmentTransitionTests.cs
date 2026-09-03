using RhinoRoad.Core;

namespace RhinoRoad.Core.Tests;

/// <summary>
/// Pins how much room a drawn alignment has to give the steering wheel.
/// </summary>
/// <remarks>
/// An alignment of arcs joined onto tangents asks for instant changes of wheel angle, and saying so
/// at every join tells the designer nothing they can act on. The length of the transition each join
/// needs is a number, and these tests keep it honest.
/// </remarks>
public sealed class AlignmentTransitionTests
{
    private static (VehicleDefinition Vehicle, DrivingModeDefinition Mode) Preset(
        string id = "PV",
        string mode = "A")
    {
        var vehicle = VehicleCatalog.LoadEmbedded().Get(id);
        return (vehicle, vehicle.DrivingModes[mode]);
    }

    /// <summary>Samples an alignment given as alternating straights and arcs, in metres.</summary>
    private static IReadOnlyList<RouteSample> Sample(params (double Length, double Radius)[] segments)
    {
        var samples = new List<RouteSample>();
        var station = 0.0;
        var heading = 0.0;
        var x = 0.0;
        var y = 0.0;
        const double step = 0.05;

        foreach (var (length, radius) in segments)
        {
            var curvature = Math.Abs(radius) <= 0.0 ? 0.0 : 1.0 / radius;
            for (var travelled = 0.0; travelled < length; travelled += step)
            {
                x += Math.Cos(heading) * step;
                y += Math.Sin(heading) * step;
                heading += curvature * step;
                station += step;
                samples.Add(new RouteSample(
                    station,
                    new Point3(x, y, 0),
                    Geometry2D.NormalizeAngle(heading),
                    curvature,
                    TravelDirection.Forward));
            }
        }

        return samples;
    }

    /// <summary>
    /// The formula the whole check rests on: wheel angle for the radius, divided by how fast the
    /// wheel moves, multiplied by how fast the vehicle does.
    /// </summary>
    [Theory]
    [InlineData("PV", 25.0, 2.64)]
    [InlineData("REN", 25.0, 4.37)]
    [InlineData("BUS12", 25.0, 5.68)]
    public void TheTransitionLengthMatchesTheWheelSlew(string vehicleId, double radius, double expected)
    {
        var (vehicle, mode) = Preset(vehicleId);

        var length = AlignmentTransitions.RequiredLengthMetres(vehicle, mode, 0.0, 1.0 / radius);

        Assert.Equal(expected, length, 2);
    }

    [Fact]
    public void AStraightAlignmentNeedsNoTransition()
    {
        var (vehicle, mode) = Preset();

        var runs = AlignmentTransitions.Runs(vehicle, mode, Sample((60.0, 0.0)));

        Assert.All(runs, run => Assert.Equal(0.0, run.RequiredTransitionMetres, 9));
        Assert.All(runs, run => Assert.True(run.Fits));
    }

    [Fact]
    public void EachStretchIsPricedForBothOfItsEnds()
    {
        var (vehicle, mode) = Preset();
        var perEnd = AlignmentTransitions.RequiredLengthMetres(vehicle, mode, 0.0, 1.0 / 25.0);

        var runs = AlignmentTransitions.Runs(vehicle, mode, Sample((40.0, 0.0), (30.0, 25.0), (40.0, 0.0)));

        var arc = runs.Single(run => run.CurvaturePerMetre > 1e-6);
        Assert.Equal(perEnd * 2.0, arc.RequiredTransitionMetres, 6);

        // The opening straight only has to hand the wheel over at its far end.
        Assert.Equal(perEnd, runs[0].RequiredTransitionMetres, 6);
    }

    /// <summary>
    /// The customer's road: 25 m corners with a 9.15 m tangent between two of them. Everything fits
    /// but that tangent, and only for the longest vehicle at the higher speed.
    /// </summary>
    [Theory]
    [InlineData("PV", "A", true)]
    [InlineData("REN", "A", true)]
    [InlineData("BUS12", "A", false)]
    [InlineData("BUS12", "B", true)]
    public void AShortTangentBetweenTwoBendsIsWhereItFails(string vehicleId, string modeId, bool expectFits)
    {
        var (vehicle, mode) = Preset(vehicleId, modeId);
        var samples = Sample(
            (123.12, 0.0), (39.27, 25.0), (9.15, 0.0), (29.78, 25.0), (88.50, 0.0));

        var runs = AlignmentTransitions.Runs(vehicle, mode, samples);
        var shortTangent = runs.Single(run =>
            Math.Abs(run.CurvaturePerMetre) < 1e-6 && run.LengthMetres is > 8.0 and < 11.0);

        Assert.Equal(expectFits, shortTangent.Fits);
    }

    [Fact]
    public void TheRunsCoverTheAlignmentInOrder()
    {
        var (vehicle, mode) = Preset();
        var samples = Sample((40.0, 0.0), (30.0, 25.0), (20.0, 0.0));

        var runs = AlignmentTransitions.Runs(vehicle, mode, samples);

        Assert.Equal(3, runs.Count);
        Assert.Equal(samples[0].StationMetres, runs[0].StartStationMetres, 6);
        Assert.Equal(samples[^1].StationMetres, runs[^1].EndStationMetres, 6);
        for (var index = 1; index < runs.Count; index++)
        {
            Assert.Equal(runs[index - 1].EndStationMetres, runs[index].StartStationMetres, 6);
        }
    }

    [Fact]
    public void ARadiusIsReportedForABendAndInfinityForAStraight()
    {
        var (vehicle, mode) = Preset();

        var runs = AlignmentTransitions.Runs(vehicle, mode, Sample((40.0, 0.0), (30.0, 25.0)));

        Assert.True(double.IsPositiveInfinity(runs[0].RadiusMetres));
        Assert.Equal(25.0, runs[1].RadiusMetres, 1);
    }

    [Fact]
    public void TooFewSamplesGiveNoRuns()
    {
        var (vehicle, mode) = Preset();

        Assert.Empty(AlignmentTransitions.Runs(vehicle, mode, []));
    }
}
