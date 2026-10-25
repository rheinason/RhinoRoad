using System.Drawing;
using System.Globalization;
using Rhino;
using Rhino.DocObjects;
using Rhino.Geometry;
using RhinoRoad.Core;

namespace RhinoRoad.Rhino.Services;

internal static class RhinoOutputWriter
{
    /// <summary>Clearance fill: a light tint, so the site drawing still reads through the envelope.</summary>
    private static readonly Color ClearanceFill = Color.FromArgb(253, 224, 208);

    /// <summary>Body fill: darker than the clearance, so the body reads as the core of the sweep.</summary>
    private static readonly Color BodyFill = Color.FromArgb(244, 176, 112);

    private static readonly Color ConflictFill = Color.FromArgb(226, 81, 66);

    /// <summary>Text height of the title block at the route start, in metres.</summary>
    private const double NoteHeightMetres = 0.5;

    public static IReadOnlyList<Guid> Bake(
        RhinoDoc document,
        RhinoAnalysisGeometry geometry,
        VehicleAccessResult result,
        IReadOnlyList<AnalysisViolation> allViolations,
        Guid sourceId,
        string analysisId,
        double clearanceMetres,
        double leftWidthMetres,
        double rightWidthMetres,
        bool replaceExisting)
    {
        var ids = new List<Guid>();
        var output = new Output(document, result, sourceId, analysisId, clearanceMetres, leftWidthMetres, rightWidthMetres);
        try
        {
            ids.Add(AddCurve(output, geometry.RearAxleTrack, "Paths", "Rear axle track", Color.Blue));
            ids.Add(AddCurve(output, geometry.FrontAxleTrack, "WheelTracks", "Front axle track", Color.CornflowerBlue));
            foreach (var track in geometry.TowedAxleTracks)
                ids.Add(AddCurve(output, track, "WheelTracks", "Towed axle track", Color.SteelBlue));
            if (geometry.BodyEnvelope is not null)
                ids.Add(AddCurve(output, geometry.BodyEnvelope, "Swept", "Body swept envelope", Color.DarkOrange));
            if (geometry.ClearanceEnvelope is not null)
                ids.Add(AddCurve(output, geometry.ClearanceEnvelope, "Clearance", "Clearance envelope", Color.OrangeRed));
            foreach (var hole in geometry.BodyEnvelopeHoles)
                ids.Add(AddCurve(output, hole, "Swept", "Body swept envelope hole", Color.DarkOrange));
            foreach (var hole in geometry.ClearanceEnvelopeHoles)
                ids.Add(AddCurve(output, hole, "Clearance", "Clearance envelope hole", Color.OrangeRed));

            // Fills sit behind everything else, clearance furthest back, so the curves and the site
            // drawing stay legible on top: display order below zero draws before normal objects.
            if (geometry.ClearanceEnvelope is not null)
                AddHatches(output, ids, [geometry.ClearanceEnvelope, .. geometry.ClearanceEnvelopeHoles],
                    "Clearance", "Clearance envelope fill", Color.OrangeRed, ClearanceFill, -2);
            if (geometry.BodyEnvelope is not null)
                AddHatches(output, ids, [geometry.BodyEnvelope, .. geometry.BodyEnvelopeHoles],
                    "Swept", "Body swept envelope fill", Color.DarkOrange, BodyFill, -1);

            foreach (var footprint in geometry.Footprints)
                ids.Add(AddCurve(output, footprint, "Footprints", "Vehicle footprint", Color.SlateGray));
            foreach (var edge in geometry.FixedRoadEdges)
                ids.Add(AddCurve(output, edge, "RoadEdges", "Fixed-width preliminary road edge", Color.ForestGreen));

            if (geometry.SiteFit is not null)
                AddConflicts(output, ids, geometry.SiteFit.Conflicts, geometry.RearAxleTrack.PointAtStart.Z);
            AddMarkers(output, ids, allViolations);
            ids.Add(AddNote(output, geometry));

            var validIds = ids.Where(id => id != Guid.Empty).ToArray();
            if (validIds.Length == 0) throw new InvalidOperationException("Rhino did not create any output objects.");
            var groupIndex = document.Groups.Add($"RhinoRoad {result.Vehicle.Id} {result.DrivingMode.Id} {analysisId[..8]}");
            document.Groups.AddToGroup(groupIndex, validIds);
            if (replaceExisting) DeleteMatching(document, sourceId, analysisId);
            document.Views.Redraw();
            return validIds;
        }
        catch
        {
            foreach (var id in ids.Where(id => id != Guid.Empty)) DocumentObjects.Delete(document, id);
            throw;
        }
    }

    private static Guid AddCurve(Output output, Curve curve, string childLayer, string name, Color color, Action<ObjectAttributes>? decorate = null)
    {
        var attributes = output.Attributes(childLayer, name, color);
        decorate?.Invoke(attributes);
        var id = output.Document.Objects.AddCurve(curve, attributes);
        if (id == Guid.Empty) throw new InvalidOperationException($"Rhino could not create {name}.");
        return id;
    }

    /// <summary>
    /// Solid fills of a region given as its outer curve and hole curves together, so Rhino builds the
    /// holes into the hatch: a roundabout island stays unfilled.
    /// </summary>
    private static void AddHatches(
        Output output,
        List<Guid> ids,
        IReadOnlyList<Curve> loops,
        string childLayer,
        string name,
        Color layerColor,
        Color fill,
        int displayOrder,
        Action<ObjectAttributes>? decorate = null)
    {
        var document = output.Document;
        var hatches = Hatch.Create(loops, SolidPatternIndex(document), 0.0, 1.0, document.ModelAbsoluteTolerance);
        if (hatches is null || hatches.Length == 0)
        {
            // A fill is presentation; the curves already carry the result, so a region Rhino cannot
            // hatch is reported rather than allowed to fail the bake.
            RhinoApp.WriteLine($"RhinoRoad could not fill {name}; the outline is unaffected.");
            return;
        }

        foreach (var hatch in hatches)
        {
            var attributes = output.Attributes(childLayer, name, layerColor);
            attributes.ColorSource = ObjectColorSource.ColorFromObject;
            attributes.ObjectColor = fill;
            attributes.DisplayOrder = displayOrder;
            decorate?.Invoke(attributes);
            var id = document.Objects.AddHatch(hatch, attributes);
            if (id == Guid.Empty) throw new InvalidOperationException($"Rhino could not create {name}.");
            ids.Add(id);
        }
    }

    /// <summary>
    /// Each site conflict as filled area with its outline, named with how much there is: "how much
    /// is the overlap" is the question a designer can act on, where "it clashes" is not.
    /// </summary>
    private static void AddConflicts(Output output, List<Guid> ids, IReadOnlyList<SiteConflict> conflicts, double elevationModel)
    {
        var scale = RhinoMath.UnitScale(UnitSystem.Meters, output.Document.ModelUnitSystem);
        foreach (var conflict in conflicts)
        {
            var amount = conflict.Regions.Count > 0
                ? $"{conflict.AreaSquareMetres:0.00} m²"
                : $"{conflict.LengthMetres:0.00} m";
            var name = $"{ConflictName(conflict)} {amount}";
            var stations = string.Join(", ", conflict.Ranges.Select(range =>
                range.EndStationMetres - range.StartStationMetres < 0.005
                    ? $"{range.StartStationMetres:0.0} m"
                    : $"{range.StartStationMetres:0.0}–{range.EndStationMetres:0.0} m"));
            var note = $"{name} at station {stations}.";
            void Tag(ObjectAttributes attributes)
            {
                attributes.SetUserString("RhinoRoad.Message", note);
                attributes.SetUserString("RhinoRoad.ConflictKind", conflict.Kind.ToString());
                attributes.SetUserString("RhinoRoad.ConflictAreaSquareMetres",
                    conflict.AreaSquareMetres.ToString("0.###", CultureInfo.InvariantCulture));
                if (conflict.OpenPieces.Count > 0)
                    attributes.SetUserString("RhinoRoad.ConflictLengthMetres",
                        conflict.LengthMetres.ToString("0.###", CultureInfo.InvariantCulture));
            }

            foreach (var region in conflict.Regions)
            {
                var loops = new[] { region.OuterBoundary }
                    .Concat(region.Holes)
                    .Select(loop => RhinoGeometryBuilder.LoopCurve(loop, elevationModel, scale))
                    .ToArray();
                AddHatches(output, ids, loops, "Conflicts", name, ConflictFill, ConflictFill, 0, Tag);
                foreach (var loop in loops) ids.Add(AddCurve(output, loop, "Conflicts", name, ConflictFill, Tag));
            }

            foreach (var piece in conflict.OpenPieces)
            {
                var curve = RhinoGeometryBuilder.OpenCurve(piece, elevationModel, scale);
                ids.Add(AddCurve(output, curve, "Conflicts", name, ConflictFill, Tag));
            }
        }
    }

    /// <summary>
    /// One marker per problem range rather than one per violation: a long over-lock turn used to
    /// bake a dotted line of identical points, one every quarter metre.
    /// </summary>
    private static void AddMarkers(Output output, List<Guid> ids, IReadOnlyList<AnalysisViolation> violations)
    {
        var document = output.Document;
        var scale = RhinoMath.UnitScale(UnitSystem.Meters, document.ModelUnitSystem);
        var stations = output.Result.Poses.Select(pose => pose.StationMetres).ToArray();
        foreach (var range in ViolationRanges.Group(violations, stations))
        {
            var attributes = output.Attributes("Warnings", range.Kind.ToString(), Color.Red);
            attributes.SetUserString("RhinoRoad.Message", range.Message);
            attributes.SetUserString("RhinoRoad.StartStationMetres", range.StartStationMetres.ToString("R", CultureInfo.InvariantCulture));
            attributes.SetUserString("RhinoRoad.EndStationMetres", range.EndStationMetres.ToString("R", CultureInfo.InvariantCulture));
            var position = new Point3d(
                range.PositionMetres.X * scale,
                range.PositionMetres.Y * scale,
                range.PositionMetres.Z * scale);
            var id = document.Objects.AddTextDot(new TextDot(ViolationRanges.Label(range), position), attributes);
            if (id == Guid.Empty) throw new InvalidOperationException("Rhino could not create a warning marker.");
            ids.Add(id);
        }
    }

    /// <summary>
    /// The block a drawing sheet needs at the route start: what was run, with what allowance, how
    /// far the preset can be trusted, and when. A preset that is not reference-validated says so
    /// first, in capitals, because that is the line a reader must not skip.
    /// </summary>
    private static Guid AddNote(Output output, RhinoAnalysisGeometry geometry)
    {
        var document = output.Document;
        var result = output.Result;
        var scale = RhinoMath.UnitScale(UnitSystem.Meters, document.ModelUnitSystem);
        var lines = new List<string>();
        var status = result.Vehicle.ValidationStatus;
        if (status != ValidationStatus.ReferenceValidated)
            lines.Add(status == ValidationStatus.ValidationFailed
                ? "PRESET FAILED VALIDATION — DO NOT RELY ON THIS RESULT"
                : "PRESET NOT REFERENCE-VALIDATED — SCREENING ONLY");
        lines.Add($"Vehicle: {result.Vehicle.Name} ({result.Vehicle.Id} v{result.Vehicle.Version})");
        lines.Add($"Mode: {result.DrivingMode.Name}");
        lines.Add($"Clearance: {output.ClearanceMetres:0.00} m");
        lines.Add($"Validation: {status}");
        lines.Add($"Date: {DateTime.Now:yyyy-MM-dd}");

        var start = geometry.RearAxleTrack.PointAtStart;
        var plane = new Plane(start + new Vector3d(0.0, -NoteHeightMetres * 2.0 * scale, 0.0), Vector3d.ZAxis);
        var text = TextEntity.Create(string.Join(System.Environment.NewLine, lines), plane, document.DimStyles.Current, false, 0.0, 0.0)
            ?? throw new InvalidOperationException("Rhino could not create the vehicle access note.");
        text.TextHeight = NoteHeightMetres * scale;
        var attributes = output.Attributes("Annotation", "Vehicle access note", Color.DimGray);
        var id = document.Objects.AddText(text, attributes);
        if (id == Guid.Empty) throw new InvalidOperationException("Rhino could not create the vehicle access note.");
        return id;
    }

    private static string ConflictName(SiteConflict conflict) => conflict.Kind switch
    {
        ViolationKind.ObstacleClearance => conflict.BodyConflict ? "Obstacle overlap (body)" : "Obstacle overlap (clearance)",
        ViolationKind.OutsideAllowedArea => "Outside allowed area",
        ViolationKind.FixedWidthRoad => "Outside fixed-width road",
        _ => conflict.Kind.ToString()
    };

    private static int SolidPatternIndex(RhinoDoc document)
    {
        var solid = document.HatchPatterns.FindName(HatchPattern.Defaults.Solid.Name);
        return solid?.Index ?? document.HatchPatterns.Add(HatchPattern.Defaults.Solid);
    }

    /// <summary>
    /// What every baked object of one run carries. Replacement, deletion and the live-edit culling
    /// all find a run's output by these user strings, so nothing may be baked without them.
    /// </summary>
    private sealed record Output(
        RhinoDoc Document,
        VehicleAccessResult Result,
        Guid SourceId,
        string AnalysisId,
        double ClearanceMetres,
        double LeftWidthMetres,
        double RightWidthMetres)
    {
        public ObjectAttributes Attributes(string childLayer, string name, Color color)
        {
            var attributes = Document.CreateDefaultAttributes();
            attributes.LayerIndex = AccessDefinitionStore.EnsureChildLayer(Document, childLayer, color);
            attributes.Name = name;
            attributes.ColorSource = ObjectColorSource.ColorFromLayer;
            attributes.SetUserString(AccessDefinitionStore.AnalysisIdKey, AnalysisId);
            attributes.SetUserString(AccessDefinitionStore.SourceIdKey, SourceId.ToString("D"));
            attributes.SetUserString(AccessDefinitionStore.RoleKey, AccessDefinitionStore.GeneratedRole);
            attributes.SetUserString("RhinoRoad.Vehicle", Result.Vehicle.Id);
            attributes.SetUserString("RhinoRoad.VehicleVersion", Result.Vehicle.Version);
            attributes.SetUserString("RhinoRoad.ValidationStatus", Result.Vehicle.ValidationStatus.ToString());
            attributes.SetUserString("RhinoRoad.Mode", Result.DrivingMode.Id);
            attributes.SetUserString("RhinoRoad.ClearanceMetres", ClearanceMetres.ToString("R", CultureInfo.InvariantCulture));
            attributes.SetUserString("RhinoRoad.LeftWidthMetres", LeftWidthMetres.ToString("R", CultureInfo.InvariantCulture));
            attributes.SetUserString("RhinoRoad.RightWidthMetres", RightWidthMetres.ToString("R", CultureInfo.InvariantCulture));
            return attributes;
        }
    }

    private static void DeleteMatching(RhinoDoc document, Guid sourceId, string keepAnalysisId)
    {
        var sourceText = sourceId.ToString("D");
        var matches = DocumentObjects.All(document)
            .Where(obj => string.Equals(obj.Attributes.GetUserString(AccessDefinitionStore.SourceIdKey), sourceText, StringComparison.OrdinalIgnoreCase))
            .Where(obj => !string.Equals(obj.Attributes.GetUserString(AccessDefinitionStore.AnalysisIdKey), keepAnalysisId, StringComparison.OrdinalIgnoreCase))
            .Where(obj => !string.Equals(obj.Attributes.GetUserString(AccessDefinitionStore.RoleKey), AccessDefinitionStore.SourceControlRole, StringComparison.OrdinalIgnoreCase))
            .Where(obj => !string.Equals(obj.Attributes.GetUserString(AccessDefinitionStore.RoleKey), AccessDefinitionStore.SourceCurveRole, StringComparison.OrdinalIgnoreCase))
            .Select(obj => obj.Id)
            .ToArray();
        foreach (var id in matches) DocumentObjects.Delete(document, id);
    }
}
