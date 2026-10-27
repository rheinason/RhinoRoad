using RhinoRoad.Core;

namespace RhinoRoad.Core.Tests;

public sealed class WindowedGradeTests
{
    private readonly VehicleDefinition _vehicle = VehicleCatalog.LoadEmbedded().Get("PV");
    private readonly VehicleAccessAnalyzer _analyzer = new();

    private static IReadOnlyList<RouteSample> Line(double length, Func<double, double> height)
    {
        var samples = new List<RouteSample>();
        for (var x = 0.0; x <= length + 1e-9; x += 0.1)
            samples.Add(new RouteSample(x, new Point3(x, 0.0, height(x)), 0.0, 0.0, TravelDirection.Forward));
        return samples;
    }

    [Fact]
    public void Noisy_flat_line_passes_the_limit_though_neighbouring_samples_spike()
    {
        var random = new Random(7);
        var noise = new double[400];
        for (var i = 0; i < noise.Length; i++) noise[i] = (random.NextDouble() - 0.5) * 0.02;
        var samples = Line(30.0, x => noise[(int)Math.Round(x / 0.1)]);

        var result = _analyzer.Analyze(_vehicle, _vehicle.DrivingModes["A"], samples, maximumAbsoluteGrade: 0.08);

        Assert.DoesNotContain(result.Violations, violation => violation.Kind == ViolationKind.Grade);
        Assert.True(result.MaximumAbsoluteGrade > 0.08);
        Assert.True(result.MaximumWindowedGrade < 0.01);
    }

    [Fact]
    public void Genuine_ramp_fails_the_limit()
    {
        var result = _analyzer.Analyze(
            _vehicle, _vehicle.DrivingModes["A"], Line(30.0, x => x * 0.10), maximumAbsoluteGrade: 0.08);

        Assert.Contains(result.Violations, violation => violation.Kind == ViolationKind.Grade);
        Assert.Equal(0.10, result.MaximumWindowedGrade, 6);
    }

    [Fact]
    public void Window_is_at_least_five_metres_and_at_least_the_wheelbase()
    {
        Assert.Equal(5.0, WindowedGrade.WindowMetres(3.0));
        Assert.Equal(7.5, WindowedGrade.WindowMetres(7.5));
    }

    [Fact]
    public void Route_shorter_than_the_window_is_judged_end_to_end()
    {
        var grades = WindowedGrade.Compute(Line(3.0, x => x * 0.2), 5.0);

        var only = Assert.Single(grades);
        Assert.Equal(0.2, only.Grade, 6);
    }

    /// <summary>Up a 10 % ramp for <paramref name="up"/> metres, then reversing back down it for <paramref name="back"/>.</summary>
    private static List<RouteSample> UpAndBack(double up, double back)
    {
        var samples = new List<RouteSample>();
        var station = 0.0;
        for (var x = 0.0; x <= up + 1e-9; x += 0.1, station += 0.1)
            samples.Add(new RouteSample(station, new Point3(x, 0.0, x * 0.10), 0.0, 0.0, TravelDirection.Forward));
        var top = samples[^1].PositionMetres.X;
        var first = true;
        for (var x = top - 0.1; x >= top - back - 1e-9; x -= 0.1, station += 0.1)
        {
            samples.Add(new RouteSample(station, new Point3(x, 0.0, x * 0.10), 0.0, 0.0, TravelDirection.Reverse,
                StartsFromStandstill: first));
            first = false;
        }

        return samples;
    }

    [Fact]
    public void A_cusp_does_not_let_the_up_and_back_legs_cancel()
    {
        // 3 m up a 10 % ramp and 3 m back: a window across the cusp measured the net rise, 2 %.
        var samples = UpAndBack(3.0, 3.0);

        var grades = WindowedGrade.Compute(samples, 5.0);

        Assert.All(grades, grade => Assert.Equal(0.10, Math.Abs(grade.Grade), 6));
        Assert.Equal(2, grades.Count);
        var result = _analyzer.Analyze(_vehicle, _vehicle.DrivingModes["B"], samples, maximumAbsoluteGrade: 0.08);
        Assert.Equal(0.10, result.MaximumWindowedGrade, 6);
        Assert.Contains(result.Violations, violation => violation.Kind == ViolationKind.Grade);
    }

    [Fact]
    public void A_long_leg_after_a_cusp_is_windowed_from_the_cusp()
    {
        var samples = UpAndBack(20.0, 12.0);
        var cusp = samples.FindIndex(sample => sample.Direction == TravelDirection.Reverse) - 1;

        var grades = WindowedGrade.Compute(samples, 5.0);

        Assert.All(grades, grade => Assert.Equal(0.10, Math.Abs(grade.Grade), 6));
        // The first window of the reverse leg ends a full window after the cusp, not before.
        var firstReverse = grades.First(grade => grade.Index > cusp).Index;
        Assert.True(samples[firstReverse].StationMetres - samples[cusp].StationMetres >= 5.0 - 1e-6);
    }

    [Fact]
    public void An_armed_stop_also_restarts_the_window()
    {
        var samples = Line(8.0, x => x <= 4.0 ? x * 0.10 : 0.4 - ((x - 4.0) * 0.10))
            .Select(sample => Math.Abs(sample.PositionMetres.X - 4.1) < 1e-6 ? sample with { StartsFromStandstill = true } : sample)
            .ToArray();

        var grades = WindowedGrade.Compute(samples, 5.0);

        // Two legs of about 4 m, each shorter than the window, each judged end to end.
        Assert.Equal(2, grades.Count);
        Assert.All(grades, grade => Assert.Equal(0.10, Math.Abs(grade.Grade), 6));
    }
}
