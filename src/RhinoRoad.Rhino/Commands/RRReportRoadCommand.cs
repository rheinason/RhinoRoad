using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using Rhino;
using Rhino.Commands;
using Rhino.DocObjects;
using Rhino.Input;
using Rhino.Input.Custom;
using RhinoRoad.Core;
using RhinoRoad.Rhino.Services;

namespace RhinoRoad.Rhino.Commands;

/// <summary>
/// Writes a review report for one or more saved roads: a print-friendly HTML page and a CSV of the
/// per-station samples beside it. The formatting is in Core (<see cref="RoadReportBuilder"/>); this
/// only gathers each road's review and writes the files. <c>-RRReportRoad</c> takes the .html path
/// on the command line instead of opening a dialog.
/// </summary>
[Guid("8C6A1D52-4E7B-4F39-9A0D-5B2E7C13F864")]
public sealed class RRReportRoadCommand : Command
{
    public override string EnglishName => "RRReportRoad";

    protected override Result RunCommand(RhinoDoc document, RunMode mode)
    {
        var selected = Selected(document);
        if (selected.Length == 0)
        {
            using var pick = new GetObject();
            pick.SetCommandPrompt("Select saved roads to report");
            pick.GeometryFilter = ObjectType.AnyObject;
            pick.SubObjectSelect = false;
            pick.GetMultiple(1, 0);
            if (pick.CommandResult() != Result.Success) return pick.CommandResult();
            selected = pick.Objects().Select(item => item.Object()).Where(item => item is not null).ToArray()!;
        }

        RoadReportDocument report;
        try
        {
            report = Gather(document, selected);
        }
        catch (Exception error)
        {
            RhinoApp.WriteLine($"Report stopped: {error.Message}");
            return Result.Failure;
        }

        var path = mode == RunMode.Scripted ? ScriptedPath(document) : DialogPath(document);
        if (string.IsNullOrWhiteSpace(path)) return Result.Cancel;
        try
        {
            path = Path.ChangeExtension(Path.GetFullPath(path), ".html");
            var csvPath = Path.ChangeExtension(path, ".csv");
            // UTF-8 with a byte-order mark so a spreadsheet opens the CSV and a browser the page correctly.
            File.WriteAllText(path, RoadReportBuilder.Page(report), new UTF8Encoding(true));
            File.WriteAllText(csvPath, RoadReportBuilder.Csv(report), new UTF8Encoding(true));
            RhinoApp.WriteLine($"Report for {report.Roads.Count} road{(report.Roads.Count == 1 ? string.Empty : "s")} written to {path} and {csvPath}.");
            return Result.Success;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            RhinoApp.WriteLine($"The report could not be written: {error.Message}");
            return Result.Failure;
        }
    }

    private static RhinoObject[] Selected(RhinoDoc document) =>
        document.Objects.GetSelectedObjects(includeLights: false, includeGrips: false).ToArray();

    private static string? ScriptedPath(RhinoDoc document)
    {
        using var getter = new GetString();
        getter.SetCommandPrompt("Report file (.html)");
        getter.SetDefaultString(DefaultFileName(document));
        return getter.Get() == GetResult.String ? getter.StringResult().Trim().Trim('"') : null;
    }

    private static string? DialogPath(RhinoDoc document)
    {
        var dialog = new global::Rhino.UI.SaveFileDialog
        {
            Title = "Save road report",
            Filter = "HTML report (*.html)|*.html",
            DefaultExt = "html",
            FileName = DefaultFileName(document)
        };
        if (!string.IsNullOrEmpty(document.Path)) dialog.InitialDirectory = Path.GetDirectoryName(document.Path);
        return dialog.ShowSaveDialog() ? dialog.FileName : null;
    }

    private static string DefaultFileName(RhinoDoc document)
    {
        var name = string.IsNullOrEmpty(document.Path) ? "RhinoRoad" : Path.GetFileNameWithoutExtension(document.Path);
        return $"{name} road report.html";
    }

    private static RoadReportDocument Gather(RhinoDoc document, RhinoObject[] selected)
    {
        var roads = new List<RoadReportRoad>();
        foreach (var id in AccessDerivedService.SourceIds(document, selected))
        {
            var source = RoadDocumentSession.For(document).FindSource(document, id.ToString("D"))
                ?? throw new InvalidOperationException($"Road {id:D} is missing from the document.");
            if (!AccessDefinitionStore.TryRead(source, out var saved, out var readError) || saved is null)
                throw new InvalidDataException($"The saved definition of road {id:D} cannot be read: {readError}");
            roads.Add(Road(document, source, saved));
        }
        var version = typeof(RRReportRoadCommand).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? typeof(RRReportRoadCommand).Assembly.GetName().Version?.ToString() ?? "unknown";
        // Strip a "+commit" build suffix; the release number is what a reader needs.
        var plus = version.IndexOf('+');
        if (plus > 0) version = version[..plus];
        return new RoadReportDocument(
            string.IsNullOrEmpty(document.Name) ? "Untitled" : document.Name,
            DateTimeOffset.Now,
            version,
            roads);
    }

    private static RoadReportRoad Road(RhinoDoc document, RhinoObject source, VehicleAccessDefinition saved)
    {
        var outOfDate = !MatchesFingerprints(document, source, saved);
        // A cached review is only the saved result while nothing it was built from has moved.
        var snapshot = outOfDate ? null : VehicleAccessReviewService.Find(document, saved.StableSourceId);
        PreparedVehicleAccess run;
        VehicleAccessReview review;
        if (snapshot is not null)
        {
            run = snapshot.Run;
            review = snapshot.Review;
        }
        else
        {
            if (!VehicleAccessRunService.TryPrepareUpdate(document, source, out var prepared, out var error) || prepared is null)
                throw new InvalidOperationException(error);
            run = prepared;
            review = VehicleAccessReviewService.Build(document, run);
        }

        var vehicle = run.Analysis.Vehicle;
        var drivingMode = run.Analysis.DrivingMode;
        var name = !string.IsNullOrWhiteSpace(source.Name)
            ? source.Name
            : $"{vehicle.Name} {saved.StableSourceId.ToString("D")[..8]}";
        return new RoadReportRoad(
            name,
            saved.StableSourceId,
            vehicle.Id,
            vehicle.Name,
            vehicle.Version,
            vehicle.ValidationStatus,
            vehicle.ValidationNotes,
            drivingMode.Id,
            drivingMode.Name,
            drivingMode.SpeedKilometresPerHour,
            drivingMode.LockToLockSeconds,
            run.Definition.ClearanceMetres,
            run.Definition.SourceKind,
            review,
            run.Analysis.MaximumWindowedGrade,
            run.Analysis.MaximumArticulationAnglesRadians,
            run.Geometry.SiteFit?.Conflicts ?? [],
            outOfDate,
            run.Geometry.BodyRegion is { } body ? [body] : [],
            run.Geometry.ClearanceRegion is { } clearance ? [clearance] : [])
        {
            HasElevation = run.HasElevation,
            TerrainDraped = run.TerrainDraped
        };
    }

    /// <summary>The inspector's "Out of date" test: the source and every referenced curve still match the saved fingerprints.</summary>
    private static bool MatchesFingerprints(RhinoDoc document, RhinoObject source, VehicleAccessDefinition saved)
    {
        if (!string.Equals(AccessDefinitionStore.Fingerprint(source), saved.SourceFingerprint, StringComparison.Ordinal)) return false;
        foreach (var pair in saved.ReferenceFingerprints)
        {
            var reference = document.Objects.FindId(pair.Key);
            if (reference is null || !string.Equals(AccessDefinitionStore.Fingerprint(reference), pair.Value, StringComparison.Ordinal))
                return false;
        }
        return true;
    }
}
