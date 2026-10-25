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
}
