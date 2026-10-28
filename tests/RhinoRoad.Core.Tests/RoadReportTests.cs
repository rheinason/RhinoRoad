using System.Globalization;
using RhinoRoad.Core;

namespace RhinoRoad.Core.Tests;

public sealed class RoadReportTests
{
    private static RoadReportRoad Road(
        string name = "Road A",
        ValidationStatus status = ValidationStatus.SourceTranscribed,
        bool outOfDate = false,
        IReadOnlyList<SiteConflict>? conflicts = null)
    {
        var samples = new[]
        {
            new VehicleAccessMetricSample(0.0, new Point3(0, 0, 0), 0.0, 0.0, 0.0, 1.5, TravelDirection.Forward),
            new VehicleAccessMetricSample(1.5, new Point3(1.5, 0.25, 0.03), Math.PI / 18, 0.1, 0.02, null, TravelDirection.Forward)
                { FoldAngleRadians = Math.PI / 36 }
        };
        var review = new VehicleAccessReview(
            [new VehicleAccessReviewCheck(ReviewCheckKind.SteeringAngle, ReviewCheckState.Fail, 12.5, 10.0, "Maximum wheel angle")],
            samples,
            [new VehicleAccessProblemEvent(ReviewCheckKind.SteeringAngle, 0.5, 1.5, 1.0, new Point3(1, 0, 0), 12.5, 10.0, "Too <sharp> & tight")],
            1.5, 0.2, 0.1, 0.02, 1.5, 0, "Test <truck>", "Mode A", status, false);
        var square = new SweptRegion([new Point2(0, 0), new Point2(2, 0), new Point2(2, 1), new Point2(0, 1)], []);
        return new RoadReportRoad(
            name, Guid.Empty, "PV", "Test <truck>", "1.2", status, "Notes & more", "A", "Mode A", 10.0, 4.0, 0.5,
            PathSourceKind.ExistingCurve, review, 0.015, [Math.PI / 36], conflicts ?? [], outOfDate, [square], [square]);
    }

    private static RoadReportDocument Document(params RoadReportRoad[] roads) =>
        new("Site <1>", new DateTimeOffset(2026, 3, 4, 5, 6, 0, TimeSpan.Zero), "0.5.0-beta", roads);

    [Fact]
    public void Html_escapes_names_and_messages()
    {
        var page = RoadReportBuilder.Page(Document(Road()));
        Assert.Contains("Site &lt;1&gt;", page);
        Assert.Contains("Test &lt;truck&gt;", page);
        Assert.Contains("Too &lt;sharp&gt; &amp; tight", page);
        Assert.DoesNotContain("<truck>", page);
    }

    [Fact]
    public void Html_states_validation_prominently_and_the_transcribed_caveat()
    {
        var transcribed = RoadReportBuilder.Page(Document(Road()));
        Assert.Contains("SourceTranscribed", transcribed);
        Assert.Contains("not validated against official curves", transcribed);
        var validated = RoadReportBuilder.Page(Document(Road(status: ValidationStatus.ReferenceValidated)));
        Assert.DoesNotContain("not validated against official curves", validated);
    }

    [Fact]
    public void Html_flags_an_out_of_date_road_only_when_it_is()
    {
        Assert.DoesNotContain("Out of date", RoadReportBuilder.Page(Document(Road())));
        Assert.Contains("Out of date", RoadReportBuilder.Page(Document(Road(outOfDate: true))));
    }

    [Fact]
    public void Html_says_a_road_read_from_an_older_schema_may_have_changed()
    {
        var warning = AccessDefinitionSerializer.Deserialize(
            File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "access-definition-v1.json"))).UpgradeWarning;

        Assert.NotNull(warning);
        Assert.DoesNotContain("RhinoRoad 0.5 or earlier", RoadReportBuilder.Page(Document(Road())));
        Assert.Contains("RhinoRoad 0.5 or earlier", RoadReportBuilder.Page(Document(Road() with { UpgradeWarning = warning })));
    }

    [Fact]
    public void Html_lists_checks_problems_and_measurements_with_invariant_numbers()
    {
        var previous = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = new CultureInfo("da-DK");
        try
        {
            var page = RoadReportBuilder.Page(Document(Road()));
            Assert.Contains("12.50 deg", page);
            Assert.Contains("0.5 to 1.5 m", page);
            Assert.Contains("1.50 %", page);
            Assert.Contains("2.00 %", page);
            Assert.DoesNotContain("12,50", page);
        }
        finally { CultureInfo.CurrentCulture = previous; }
    }

    [Fact]
    public void Html_includes_conflict_areas_only_when_present()
    {
        Assert.DoesNotContain("conflict areas", RoadReportBuilder.Page(Document(Road())));
        var region = new SweptRegion([new Point2(0, 0), new Point2(1, 0), new Point2(1, 1)], []);
        var conflict = new SiteConflict(ViolationKind.ObstacleClearance, 0, true, [region], [], 0.5, 0.0,
            [new SiteConflictRange(0.5, 1.0, 0.7, new Point3(1, 0, 0), -0.2)]);
        var page = RoadReportBuilder.Page(Document(Road(conflicts: [conflict])));
        Assert.Contains("Site-fit conflict areas", page);
        Assert.Contains("Obstacle 1", page);
        Assert.Contains("class=\"conflict\"", page);
    }

    [Fact]
    public void Csv_has_a_header_one_row_per_sample_and_quotes_names()
    {
        var csv = RoadReportBuilder.Csv(Document(Road("A, \"north\"")));
        var lines = csv.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(3, lines.Length);
        Assert.StartsWith("road,station_m,x_m,y_m,steering_angle_deg", lines[0]);
        Assert.Equal("\"A, \"\"north\"\"\",0.000,0.000,0.000,0.000,0.000,0.000,1.500,", lines[1]);
        Assert.Equal("\"A, \"\"north\"\"\",1.500,1.500,0.250,10.000,5.730,2.000,,5.000", lines[2]);
    }

    [Fact]
    public void Path_data_closes_loops_and_uses_invariant_numbers()
    {
        var loop = new Point2[] { new(0, 0), new(1.5, 0), new(1.5, 2) };
        Assert.Equal("M0.00 0.00L1.50 0.00L1.50 2.00Z", RoadReportBuilder.PathData([loop], p => p));
        Assert.Equal("M0.00 0.00L1.50 0.00L1.50 2.00", RoadReportBuilder.PathData([loop], p => p, close: false));
        Assert.Equal(string.Empty, RoadReportBuilder.PathData([new Point2[] { new(0, 0) }], p => p));
    }

    [Fact]
    public void Plan_svg_flips_y_and_fits_the_width()
    {
        var svg = RoadReportBuilder.PlanSvg(Road(), 112.0);
        Assert.Contains("viewBox=\"0 0 112.0 68.0\"", svg);
        // The envelope's south-west corner (0,0) lands at the bottom-left margin.
        Assert.Contains("M12.00 56.00", svg);
        Assert.Contains("class=\"route\"", svg);
    }

    [Fact]
    public void Plan_svg_is_empty_without_geometry()
    {
        var road = Road() with { BodyRegions = [], ClearanceRegions = [] };
        // The route alone is still drawn; with no samples there is nothing at all.
        var empty = road with { Review = road.Review with { Samples = [] } };
        Assert.Equal(string.Empty, RoadReportBuilder.PlanSvg(empty));
    }

    [Theory]
    [InlineData("=HYPERLINK(\"http://x\")")]
    [InlineData("+1")]
    [InlineData("-2+3")]
    [InlineData("@SUM(A1)")]
    [InlineData("\tcmd")]
    [InlineData("\rcmd")]
    public void Csv_field_that_a_spreadsheet_would_run_is_written_as_text(string name)
    {
        var field = RoadReportBuilder.CsvField(name);

        Assert.StartsWith(field.StartsWith('"') ? "\"'" : "'", field);
        var row = RoadReportBuilder.Csv(Document(Road(name))).Split("\r\n")[1];
        Assert.False(row.Length > 0 && row[0] is '=' or '+' or '-' or '@' or '\t' or '\r');
    }

    [Fact]
    public void Csv_field_without_a_formula_lead_is_unchanged()
    {
        Assert.Equal("Road A", RoadReportBuilder.CsvField("Road A"));
        Assert.Equal("A-1", RoadReportBuilder.CsvField("A-1"));
    }

    [Fact]
    public void Analyzer_messages_in_the_report_use_invariant_numbers_under_a_comma_culture()
    {
        var previous = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = new CultureInfo("da-DK");
        try
        {
            // A real ramp and a real kerb, so the messages are the analyzers' own wording.
            var vehicle = VehicleCatalog.LoadEmbedded().Get("PV");
            var route = Enumerable.Range(0, 301)
                .Select(index => new RouteSample(index * 0.1, new Point3(index * 0.1, 0.0, index * 0.01), 0.0, 0.0, TravelDirection.Forward))
                .ToArray();
            var result = new VehicleAccessAnalyzer().Analyze(vehicle, vehicle.DrivingModes["A"], route, maximumAbsoluteGrade: 0.08);
            var body = SweptRegionBuilder.FromPoses(result.Poses).Region!;
            var clearance = SweptRegionBuilder.Inflate(body, 0.30).Region!;
            var kerbY = (vehicle.WidthMetres / 2.0) + 0.15;
            var site = SiteFitAnalyzer.Check(
                result.Poses, body, clearance, [new SiteCurve([new(10.0, kerbY), new(20.0, kerbY)], false)], [], null, 0.30);
            var joins = AlignmentTransitions.DescribeJoins([new CurvatureJoin(12.5, 4.0, 1), new CurvatureJoin(30.0, 4.0, 1)], true);

            var messages = result.Violations.Where(violation => violation.Kind == ViolationKind.Grade).Take(1)
                .Concat(site.Violations.Take(1))
                .Select(violation => violation.Message)
                .Append(joins)
                .ToArray();
            Assert.Equal(3, messages.Length);
            Assert.All(messages, message => Assert.DoesNotMatch(@"\d,\d", message));
            Assert.Contains("exceeds 8.00%", messages[0]);
            Assert.Contains("0.30 m clearance allowance", messages[1]);
            Assert.Contains("stations 12.5 m, 30.0 m", messages[2]);

            var road = Road() with
            {
                Review = Road().Review with
                {
                    ProblemEvents = messages
                        .Select(message => new VehicleAccessProblemEvent(ReviewCheckKind.Grade, 1.0, 2.0, 1.5, new Point3(1, 0, 0), 10.0, 8.0, message))
                        .ToArray()
                }
            };
            var page = RoadReportBuilder.Page(Document(road));
            Assert.Contains(RoadReportBuilder.Html(messages[0]), page);
            Assert.Contains(RoadReportBuilder.Html(messages[1]), page);
            Assert.Contains("stations 12.5 m, 30.0 m", page);
        }
        finally { CultureInfo.CurrentCulture = previous; }
    }
}
