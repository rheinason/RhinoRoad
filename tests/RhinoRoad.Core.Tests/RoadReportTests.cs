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
}
