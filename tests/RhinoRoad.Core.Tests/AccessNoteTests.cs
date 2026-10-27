using System.Text.Json;
using RhinoRoad.Core;

namespace RhinoRoad.Core.Tests;

/// <summary>
/// Pins the baked annotation's wording and placement, the report's explanation of curvature jumps,
/// and that the computed members of the point types stay out of saved JSON.
/// </summary>
public sealed class AccessNoteTests
{
    private static VehicleDefinition Svt() => VehicleCatalog.LoadEmbedded().Get("SVT");

    [Fact]
    public void Version_is_shown_as_a_preset_without_a_v_prefix()
    {
        Assert.Equal("preset VD-GUT-2023.09", AccessNote.VersionText("vVD-GUT-2023.09"));
        Assert.Equal("preset 2.1", AccessNote.VersionText("2.1"));
    }

    [Theory]
    [InlineData(ValidationStatus.ReferenceValidated, "validated against the official curves")]
    [InlineData(ValidationStatus.SourceTranscribed, "source-transcribed, not validated")]
    [InlineData(ValidationStatus.UserDefined, "user-defined, not validated")]
    public void Status_is_plain_language_not_an_enum_name(ValidationStatus status, string expected)
    {
        var vehicle = Svt() with { ValidationStatus = status };
        var lines = AccessNote.Lines(vehicle, vehicle.DrivingModes.Values.First(), 0.3, false, new DateOnly(2026, 1, 2));

        Assert.Contains($"Validation: {expected}", lines);
        Assert.DoesNotContain(lines, line => line.Contains(status.ToString()));
        Assert.Contains("Date: 2026-01-02", lines);
        Assert.DoesNotContain(lines, line => line.Contains("vVD"));
        Assert.Contains(lines, line => line.Contains("preset "));
    }

    [Fact]
    public void Only_a_validated_preset_has_no_capitals_warning()
    {
        var mode = Svt().DrivingModes.Values.First();
        var validated = AccessNote.Lines(Svt() with { ValidationStatus = ValidationStatus.ReferenceValidated }, mode, 0.3, false, default);
        var transcribed = AccessNote.Lines(Svt() with { ValidationStatus = ValidationStatus.SourceTranscribed }, mode, 0.3, false, default);

        Assert.DoesNotContain(validated, line => line == line.ToUpperInvariant() && line.Any(char.IsLetter));
        Assert.Equal(transcribed[0].ToUpperInvariant(), transcribed[0]);
    }

    [Fact]
    public void Note_sits_below_the_lower_left_corner_by_a_gap_scaled_to_the_model()
    {
        var anchor = AccessNote.Anchor(new Point2(10.0, 20.0), 1000.0);
        Assert.Equal(10.0, anchor.X);
        Assert.Equal(20.0 - (AccessNote.GapMetres * 1000.0), anchor.Y, 9);
        Assert.True(anchor.Y < 20.0);
    }

    private static (VehicleDefinition Vehicle, DrivingModeDefinition Mode) Pv()
    {
        var vehicle = VehicleCatalog.LoadEmbedded().Get("PV");
        return (vehicle, vehicle.DrivingModes["A"]);
    }

    private static IReadOnlyList<AlignmentRun> ThreeRuns() =>
    [
        new AlignmentRun(0, 30, 0.0, 5),
        new AlignmentRun(30, 60, 0.04, 5),
        new AlignmentRun(60, 90, 0.0, 5)
    ];

    [Fact]
    public void Steering_rate_violations_at_run_boundaries_are_matched_to_their_joins()
    {
        var (vehicle, mode) = Pv();
        var violations = new[]
        {
            new AnalysisViolation(ViolationKind.SteeringRate, 30.08, default, "rate"),
            new AnalysisViolation(ViolationKind.SteeringRate, 59.96, default, "rate"),
            new AnalysisViolation(ViolationKind.SteeringRate, 45.0, default, "rate elsewhere"),
            new AnalysisViolation(ViolationKind.Grade, 30.0, default, "not a rate")
        };

        var joins = AlignmentTransitions.JoinsWithRateViolations(vehicle, mode, ThreeRuns(), violations);

        Assert.Equal([30.0, 60.0], joins.Select(join => join.StationMetres));
        Assert.All(joins, join => Assert.Equal(1, join.SteeringRateViolations));
        Assert.All(joins, join => Assert.True(join.RequiredTransitionMetres > 0.0));
        Assert.Empty(AlignmentTransitions.JoinsWithRateViolations(vehicle, mode, ThreeRuns(), []));
    }

    [Fact]
    public void Join_description_explains_instead_of_contradicting()
    {
        var joins = new[] { new CurvatureJoin(30.0, 4.0, 1), new CurvatureJoin(60.0, 4.0, 1) };

        var fits = AlignmentTransitions.DescribeJoins(joins, everyStretchFits: true);
        Assert.Contains("changes curvature instantly at 2 join(s)", fits);
        Assert.Contains("every stretch is long enough", fits);

        var tight = AlignmentTransitions.DescribeJoins(joins, everyStretchFits: false);
        Assert.DoesNotContain("every stretch is long enough", tight);
    }

    [Fact]
    public void Computed_point_members_are_not_serialised_but_old_json_still_reads()
    {
        var json = JsonSerializer.Serialize(new Point3(25, 0, 0));
        Assert.DoesNotContain("XY", json);
        Assert.DoesNotContain("Length", JsonSerializer.Serialize(new Point2(3, 4)));

        var old = JsonSerializer.Deserialize<Point3>("{\"X\":25,\"Y\":0,\"Z\":0,\"XY\":{\"X\":25,\"Y\":0,\"Length\":25}}");
        Assert.Equal(new Point3(25, 0, 0), old);
    }

    [Theory]
    [InlineData("vejregler-1", "preset vejregler-1")]
    [InlineData("v2.1", "preset 2.1")]
    [InlineData("vVD-GUT-2023.09", "preset VD-GUT-2023.09")]
    [InlineData("v", "preset v")]
    [InlineData("  example-1 ", "preset example-1")]
    public void Only_a_v_prefix_is_stripped_not_a_word_beginning_with_v(string version, string expected) =>
        Assert.Equal(expected, AccessNote.VersionText(version));

    [Fact]
    public void A_violation_between_two_close_joins_is_counted_once_at_the_nearer()
    {
        // Joins 1.5 m apart, both within the 1 m tolerance of a violation 0.9 m past the first.
        var (vehicle, mode) = Pv();
        IReadOnlyList<AlignmentRun> runs =
        [
            new AlignmentRun(0, 30, 0.0, 5),
            new AlignmentRun(30, 31.5, 0.04, 5),
            new AlignmentRun(31.5, 60, 0.0, 5)
        ];
        var violations = new[] { new AnalysisViolation(ViolationKind.SteeringRate, 30.9, default, "rate") };

        var joins = AlignmentTransitions.JoinsWithRateViolations(vehicle, mode, runs, violations);

        var join = Assert.Single(joins);
        Assert.Equal(31.5, join.StationMetres);
        Assert.Equal(1, join.SteeringRateViolations);
    }

    [Fact]
    public void Join_stations_are_listed_unambiguously_whatever_the_culture()
    {
        var previous = System.Globalization.CultureInfo.CurrentCulture;
        System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("da-DK");
        try
        {
            var text = AlignmentTransitions.DescribeJoins([new CurvatureJoin(12.5, 4.0, 1), new CurvatureJoin(30.0, 5.5, 1)], true);

            Assert.Contains("(stations 12.5 m, 30.0 m)", text);
            Assert.Contains("4.0 to 5.5 m", text);
            Assert.DoesNotContain("12,5", text);
        }
        finally { System.Globalization.CultureInfo.CurrentCulture = previous; }
    }
}
