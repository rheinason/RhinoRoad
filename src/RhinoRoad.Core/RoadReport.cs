using System.Globalization;
using System.Net;
using System.Text;

namespace RhinoRoad.Core;

/// <summary>Everything the report says about one saved road, already measured; the builders only format it.</summary>
/// <param name="OutOfDate">True when the road's source or referenced curves no longer match the saved fingerprints.</param>
/// <param name="MaximumWindowedGrade">Largest grade over the moving window, as a fraction; null when not measured.</param>
/// <param name="MaximumFoldAnglesRadians">Largest fold at each articulation joint; empty for a rigid vehicle.</param>
public sealed record RoadReportRoad(
    string Name,
    Guid StableId,
    string VehicleId,
    string VehicleName,
    string PresetVersion,
    ValidationStatus ValidationStatus,
    string ValidationNotes,
    string ModeId,
    string ModeName,
    double SpeedKilometresPerHour,
    double LockToLockSeconds,
    double ClearanceMetres,
    PathSourceKind SourceKind,
    VehicleAccessReview Review,
    double? MaximumWindowedGrade,
    IReadOnlyList<double> MaximumFoldAnglesRadians,
    IReadOnlyList<SiteConflict> Conflicts,
    bool OutOfDate,
    IReadOnlyList<SweptRegion> BodyRegions,
    IReadOnlyList<SweptRegion> ClearanceRegions)
{
    /// <summary>False when the route is flat (an interactive route without terrain), so grade figures are not meaningful.</summary>
    public bool HasElevation { get; init; } = true;

    /// <summary>True when the route's heights come from a terrain surface.</summary>
    public bool TerrainDraped { get; init; }
}

public sealed record RoadReportDocument(
    string DocumentName,
    DateTimeOffset Generated,
    string RhinoRoadVersion,
    IReadOnlyList<RoadReportRoad> Roads);

/// <summary>
/// Formats road reviews as a self-contained, print-friendly HTML page and a CSV of the per-station
/// samples. Pure text building: no file or Rhino access.
/// </summary>
public static class RoadReportBuilder
{
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    public static string Number(double value, int decimals = 2) =>
        double.IsFinite(value) ? value.ToString("F" + decimals, Invariant) : string.Empty;

    private static double Degrees(double radians) => radians * 180.0 / Math.PI;

    public static string Html(string? text) => WebUtility.HtmlEncode(text ?? string.Empty);

    /// <summary>One CSV field, quoted when it holds a comma, quote or line break.</summary>
    public static string CsvField(string? text)
    {
        text ??= string.Empty;
        return text.IndexOfAny([',', '"', '\r', '\n']) < 0 ? text : "\"" + text.Replace("\"", "\"\"") + "\"";
    }

    public static string CheckName(ReviewCheckKind kind) => kind switch
    {
        ReviewCheckKind.EnvelopeGeneration => "Swept envelope generation",
        ReviewCheckKind.SteeringAngle => "Steering angle",
        ReviewCheckKind.SteeringRate => "Steering rate",
        ReviewCheckKind.TangentContinuity => "Tangent continuity",
        ReviewCheckKind.Grade => "Grade",
        ReviewCheckKind.FixedWidthRoadFit => "Fixed-width road fit",
        ReviewCheckKind.AllowedAreaFit => "Allowed-area fit",
        ReviewCheckKind.ObstacleClearance => "Obstacle clearance",
        ReviewCheckKind.ArticulationAngle => "Articulation fold",
        _ => kind.ToString()
    };

    /// <summary>The unit a check's actual and limit are stated in; the review stores degrees, percent or metres.</summary>
    private static string CheckUnit(ReviewCheckKind kind) => kind switch
    {
        ReviewCheckKind.SteeringAngle or ReviewCheckKind.ArticulationAngle => "deg",
        ReviewCheckKind.SteeringRate => "deg/s",
        ReviewCheckKind.Grade => "%",
        ReviewCheckKind.FixedWidthRoadFit or ReviewCheckKind.AllowedAreaFit or ReviewCheckKind.ObstacleClearance => "m",
        _ => string.Empty
    };

    public static string StationRange(double start, double end) =>
        end - start < 0.005 ? $"{Number(start, 1)} m" : $"{Number(start, 1)} to {Number(end, 1)} m";

    /// <summary>The per-station samples of every road, one row each, road name first.</summary>
    public static string Csv(RoadReportDocument document)
    {
        var builder = new StringBuilder();
        builder.Append("road,station_m,x_m,y_m,steering_angle_deg,steering_rate_deg_per_s,grade_percent,margin_m,fold_deg\r\n");
        foreach (var road in document.Roads)
            foreach (var sample in road.Review.Samples)
                builder.Append(CsvField(road.Name)).Append(',')
                    .Append(Number(sample.StationMetres, 3)).Append(',')
                    .Append(Number(sample.PositionMetres.X, 3)).Append(',')
                    .Append(Number(sample.PositionMetres.Y, 3)).Append(',')
                    .Append(Number(Degrees(sample.SteeringAngleRadians), 3)).Append(',')
                    .Append(Number(Degrees(sample.SteeringRateRadiansPerSecond), 3)).Append(',')
                    .Append(Number(sample.Grade * 100.0, 3)).Append(',')
                    .Append(sample.ClearanceOrRoadMarginMetres.HasValue ? Number(sample.ClearanceOrRoadMarginMetres.Value, 3) : string.Empty).Append(',')
                    .Append(sample.FoldAngleRadians.HasValue ? Number(Degrees(sample.FoldAngleRadians.Value), 3) : string.Empty)
                    .Append("\r\n");
        return builder.ToString();
    }

    /// <summary>An SVG path "d" for the loops, each closed, with points mapped by <paramref name="map"/>.</summary>
    public static string PathData(IEnumerable<IReadOnlyList<Point2>> loops, Func<Point2, Point2> map, bool close = true)
    {
        var builder = new StringBuilder();
        foreach (var loop in loops)
        {
            if (loop.Count < 2) continue;
            for (var index = 0; index < loop.Count; index++)
            {
                var point = map(loop[index]);
                builder.Append(index == 0 ? 'M' : 'L').Append(Number(point.X, 2)).Append(' ').Append(Number(point.Y, 2));
            }
            if (close) builder.Append('Z');
        }
        return builder.ToString();
    }

    private static IEnumerable<IReadOnlyList<Point2>> Loops(IEnumerable<SweptRegion> regions) =>
        regions.SelectMany(region => new[] { region.OuterBoundary }.Concat(region.Holes));

    /// <summary>
    /// A plan of the clearance and body envelopes, the route and the conflict areas, scaled to fit
    /// <paramref name="width"/> by the height its extents need. Empty when there is nothing to draw.
    /// </summary>
    public static string PlanSvg(RoadReportRoad road, double width = 720.0)
    {
        var routeLoop = (IReadOnlyList<Point2>)road.Review.Samples.Select(sample => new Point2(sample.PositionMetres.X, sample.PositionMetres.Y)).ToArray();
        var conflictRegions = road.Conflicts.SelectMany(conflict => conflict.Regions).ToArray();
        var all = Loops(road.ClearanceRegions).Concat(Loops(road.BodyRegions)).Concat(Loops(conflictRegions)).Append(routeLoop)
            .SelectMany(loop => loop).ToArray();
        if (all.Length == 0) return string.Empty;
        const double margin = 12.0;
        var minX = all.Min(p => p.X);
        var maxX = all.Max(p => p.X);
        var minY = all.Min(p => p.Y);
        var maxY = all.Max(p => p.Y);
        var spanX = Math.Max(maxX - minX, 1e-6);
        var spanY = Math.Max(maxY - minY, 1e-6);
        var scale = (width - 2 * margin) / spanX;
        // A very tall plan would print off the page; cap the height and let the width follow.
        const double maxHeight = 900.0;
        if (spanY * scale + 2 * margin > maxHeight) scale = (maxHeight - 2 * margin) / spanY;
        var drawnWidth = spanX * scale + 2 * margin;
        var height = spanY * scale + 2 * margin;
        Point2 Map(Point2 p) => new(margin + (p.X - minX) * scale, margin + (maxY - p.Y) * scale);

        var svg = new StringBuilder();
        svg.Append($"<svg xmlns=\"http://www.w3.org/2000/svg\" class=\"plan\" viewBox=\"0 0 {Number(drawnWidth, 1)} {Number(height, 1)}\" ")
            .Append($"width=\"{Number(drawnWidth, 0)}\" height=\"{Number(height, 0)}\" role=\"img\" aria-label=\"Plan of the swept envelopes\">");
        AppendPath(svg, Loops(road.ClearanceRegions), Map, "clearance", "fill=\"#cfe3f7\" fill-opacity=\"0.8\" stroke=\"#2b6cb0\" stroke-width=\"0.8\" fill-rule=\"evenodd\"");
        AppendPath(svg, Loops(road.BodyRegions), Map, "body", "fill=\"#8fb8e0\" fill-opacity=\"0.8\" stroke=\"#1a4a85\" stroke-width=\"0.8\" fill-rule=\"evenodd\"");
        AppendPath(svg, Loops(conflictRegions), Map, "conflict", "fill=\"#e53e3e\" fill-opacity=\"0.55\" stroke=\"#9b2c2c\" stroke-width=\"0.8\" fill-rule=\"evenodd\"");
        var pieces = PathData(road.Conflicts.SelectMany(conflict => conflict.OpenPieces), Map, close: false);
        if (pieces.Length > 0) svg.Append($"<path class=\"conflict-line\" d=\"{pieces}\" fill=\"none\" stroke=\"#9b2c2c\" stroke-width=\"2\"/>");
        var route = PathData([routeLoop], Map, close: false);
        if (route.Length > 0) svg.Append($"<path class=\"route\" d=\"{route}\" fill=\"none\" stroke=\"#111\" stroke-width=\"1.2\"/>");
        svg.Append("</svg>");
        return svg.ToString();
    }

    private static void AppendPath(
        StringBuilder svg, IEnumerable<IReadOnlyList<Point2>> loops, Func<Point2, Point2> map, string cssClass, string style)
    {
        var d = PathData(loops, map);
        if (d.Length > 0) svg.Append($"<path class=\"{cssClass}\" d=\"{d}\" {style}/>");
    }

    public static string Page(RoadReportDocument document)
    {
        var html = new StringBuilder();
        html.Append("<!DOCTYPE html>\n<html lang=\"en\"><head><meta charset=\"utf-8\">")
            .Append("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">")
            .Append($"<title>RhinoRoad report - {Html(document.DocumentName)}</title><style>{Style}</style></head><body>");
        html.Append("<h1>Vehicle access report</h1>");
        html.Append("<p class=\"meta\">")
            .Append($"Document: <b>{Html(document.DocumentName)}</b> &middot; ")
            .Append($"Date: {Html(document.Generated.ToString("yyyy-MM-dd HH:mm", Invariant))} &middot; ")
            .Append($"RhinoRoad {Html(document.RhinoRoadVersion)} &middot; Roads: {document.Roads.Count}</p>");
        foreach (var road in document.Roads) AppendRoad(html, road);
        html.Append("</body></html>\n");
        return html.ToString();
    }

    private static void AppendRoad(StringBuilder html, RoadReportRoad road)
    {
        var review = road.Review;
        html.Append("<section class=\"road\">");
        html.Append($"<h2>{Html(road.Name)} <small>{Html(road.StableId.ToString("D"))}</small></h2>");
        if (road.OutOfDate)
            html.Append("<p class=\"banner stale\">Out of date: the road's source curve or a referenced site curve has changed since " +
                        "the last saved update. The figures below were recomputed from the current geometry and may differ from the saved result; " +
                        "run Update before relying on this report.</p>");
        var validated = road.ValidationStatus == ValidationStatus.ReferenceValidated;
        html.Append($"<p class=\"banner {(validated ? "ok" : "warn")}\">Vehicle validation: <b>{Html(road.ValidationStatus.ToString())}</b>");
        if (road.ValidationStatus == ValidationStatus.SourceTranscribed)
            html.Append(". SourceTranscribed presets are transcribed from the published source and are not validated against official curves.");
        if (!string.IsNullOrWhiteSpace(road.ValidationNotes)) html.Append($" {Html(road.ValidationNotes)}");
        html.Append("</p>");
        html.Append($"<p class=\"banner {(review.IsFeasible ? "ok" : "fail")}\">Overall: <b>{(review.IsFeasible ? "feasible, no problems found" : "problems found")}</b></p>");

        html.Append("<table class=\"kv\">");
        Row(html, "Vehicle", $"{road.VehicleName} ({road.VehicleId}), preset version {road.PresetVersion}");
        Row(html, "Driving mode", $"{road.ModeName} ({road.ModeId}), {Number(road.SpeedKilometresPerHour, 1)} km/h, lock-to-lock {Number(road.LockToLockSeconds, 1)} s");
        Row(html, "Clearance allowance", $"{Number(road.ClearanceMetres)} m");
        Row(html, "Source", (road.SourceKind == PathSourceKind.Interactive ? "Interactive route (editable controls)" : "Existing curve") +
            (road.TerrainDraped ? ", draped on terrain" : string.Empty));
        Row(html, "Travel length", $"{Number(review.RouteLengthMetres, 1)} m");
        Row(html, "Reversals", review.ReversalCount.ToString(Invariant));
        html.Append("</table>");

        html.Append("<h3>Checks</h3><table><tr><th>Check</th><th>State</th><th>Actual</th><th>Limit</th><th>Note</th></tr>");
        foreach (var check in review.Checks)
        {
            var unit = CheckUnit(check.Kind);
            html.Append($"<tr class=\"{check.State.ToString().ToLowerInvariant()}\"><td>{Html(CheckName(check.Kind))}</td><td>{check.State}</td>")
                .Append($"<td class=\"n\">{Html(WithUnit(check.ActualValue, unit))}</td><td class=\"n\">{Html(WithUnit(check.Limit, unit))}</td>")
                .Append($"<td>{Html(check.Message)}</td></tr>");
        }
        html.Append("</table>");

        html.Append("<h3>Measurements</h3><table class=\"kv\">");
        Row(html, "Maximum wheel angle", $"{Number(Degrees(review.MaximumSteeringAngleRadians), 1)} deg");
        Row(html, "Maximum steering rate", $"{Number(Degrees(review.MaximumSteeringRateRadiansPerSecond), 1)} deg/s");
        Row(html, "Maximum grade, point to point", road.HasElevation ? $"{Number(review.MaximumAbsoluteGrade * 100.0)} %" : "n/a (flat route)");
        Row(html, "Maximum grade, windowed", road.HasElevation && road.MaximumWindowedGrade.HasValue ? $"{Number(road.MaximumWindowedGrade.Value * 100.0)} %" : "n/a");
        Row(html, "Minimum clearance", review.MinimumClearanceMetres.HasValue ? $"{Number(review.MinimumClearanceMetres.Value)} m" : "n/a (no site curves checked)");
        for (var joint = 0; joint < road.MaximumFoldAnglesRadians.Count; joint++)
            Row(html, $"Maximum fold, joint {joint + 1}", $"{Number(Degrees(road.MaximumFoldAnglesRadians[joint]), 1)} deg");
        html.Append("</table>");

        html.Append("<h3>Problems</h3>");
        if (review.ProblemEvents.Count == 0) html.Append("<p>None.</p>");
        else
        {
            html.Append("<table><tr><th>Check</th><th>Stations</th><th>Worst at</th><th>Worst / limit</th><th>Detail</th></tr>");
            foreach (var group in review.ProblemEvents.GroupBy(item => item.Check).OrderBy(group => group.Min(item => item.StartStationMetres)))
                foreach (var item in group.OrderBy(item => item.StartStationMetres))
                {
                    var unit = CheckUnit(item.Check);
                    html.Append($"<tr class=\"fail\"><td>{Html(CheckName(item.Check))}</td><td>{Html(StationRange(item.StartStationMetres, item.EndStationMetres))}</td>")
                        .Append($"<td class=\"n\">{Html(Number(item.WorstStationMetres, 1))} m</td>")
                        .Append($"<td class=\"n\">{Html(WithUnit(item.ActualValue, unit))} / {Html(WithUnit(item.Limit, unit))}</td>")
                        .Append($"<td>{Html(item.Message)}</td></tr>");
                }
            html.Append("</table>");
        }

        if (road.Conflicts.Count > 0)
        {
            html.Append("<h3>Site-fit conflict areas</h3><table><tr><th>Constraint</th><th>Body in conflict</th><th>Area</th><th>Length</th><th>Stations</th></tr>");
            foreach (var conflict in road.Conflicts)
                html.Append($"<tr><td>{Html(ConflictName(conflict))}</td><td>{(conflict.BodyConflict ? "yes" : "no")}</td>")
                    .Append($"<td class=\"n\">{Number(conflict.AreaSquareMetres)} m&sup2;</td><td class=\"n\">{Number(conflict.LengthMetres)} m</td>")
                    .Append($"<td>{Html(string.Join("; ", conflict.Ranges.Select(range => StationRange(range.StartStationMetres, range.EndStationMetres))))}</td></tr>");
            html.Append("</table>");
        }

        var plan = PlanSvg(road);
        if (plan.Length > 0)
        {
            html.Append("<h3>Plan</h3><div class=\"planbox\">").Append(plan).Append("</div>")
                .Append("<p class=\"legend\"><span class=\"sw\" style=\"background:#cfe3f7\"></span>clearance envelope " +
                        "<span class=\"sw\" style=\"background:#8fb8e0\"></span>body envelope " +
                        "<span class=\"sw\" style=\"background:#e53e3e\"></span>conflict " +
                        "<span class=\"sw\" style=\"background:#111\"></span>route (north up)</p>");
        }
        html.Append("</section>");
    }

    private static string ConflictName(SiteConflict conflict) => conflict.Kind switch
    {
        ViolationKind.ObstacleClearance => conflict.SourceIndex >= 0 ? $"Obstacle {conflict.SourceIndex + 1}" : "Obstacle",
        ViolationKind.OutsideAllowedArea => "Allowed area",
        ViolationKind.FixedWidthRoad => "Fixed-width road",
        _ => conflict.Kind.ToString()
    };

    private static string WithUnit(double? value, string unit) =>
        !value.HasValue ? "n/a" : unit.Length == 0 ? Number(value.Value) : $"{Number(value.Value)} {unit}";

    private static void Row(StringBuilder html, string label, string value) =>
        html.Append($"<tr><th>{Html(label)}</th><td>{Html(value)}</td></tr>");

    private const string Style =
        "body{font:13px/1.45 Segoe UI,Helvetica,Arial,sans-serif;color:#111;background:#fff;margin:24px auto;max-width:900px;padding:0 16px}" +
        "h1{font-size:22px;margin:0 0 4px}h2{font-size:17px;margin:0 0 8px;border-bottom:2px solid #333;padding-bottom:3px}" +
        "h2 small{font-weight:normal;font-size:11px;color:#666}h3{font-size:14px;margin:16px 0 4px}" +
        ".meta{color:#444;margin:0 0 20px}.road{margin-bottom:32px;page-break-after:always;break-after:page}.road:last-child{page-break-after:auto;break-after:auto}" +
        "table{border-collapse:collapse;width:100%;margin:4px 0}th,td{border:1px solid #bbb;padding:3px 6px;text-align:left;vertical-align:top}" +
        "th{background:#f0f0f0}table.kv th{width:30%}td.n{text-align:right;white-space:nowrap}" +
        "tr.fail td{background:#fdecec}tr.pass td:nth-child(2){color:#276749;font-weight:600}tr.fail td:nth-child(2){color:#9b2c2c;font-weight:600}" +
        "tr.off td,tr.unavailable td{color:#666}" +
        ".banner{padding:6px 10px;border-left:5px solid;margin:6px 0}.banner.ok{background:#e6f4ea;border-color:#2f855a}" +
        ".banner.warn{background:#fff6dd;border-color:#b7791f}.banner.fail,.banner.stale{background:#fdecec;border-color:#c53030}" +
        ".planbox{border:1px solid #bbb;padding:4px;text-align:center;overflow:hidden}svg.plan{max-width:100%;height:auto}" +
        ".legend{font-size:11px;color:#444}.sw{display:inline-block;width:10px;height:10px;margin:0 3px 0 10px;border:1px solid #666}" +
        "@media print{body{margin:0;max-width:none}.road{margin-bottom:0}}";
}
