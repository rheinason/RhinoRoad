using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Rhino;
using Rhino.DocObjects;
using Rhino.Geometry;
using Rhino.Geometry.Intersect;
using RhinoRoad.Core;

namespace RhinoRoad.Rhino.Services;

internal static class ReferenceCertificationService
{
    private const double BoundarySpacingMetres = 0.025;
    private const double JoinToleranceMetres = 0.02;
    private static readonly ReferenceCertificationThresholds Thresholds = new(0.10, 0.05);

    /// <summary>
    /// Certification builds its swept envelopes at a fixed tolerance rather than inheriting the
    /// document's. The envelope is a Boolean union of ~1500 footprints whose success is
    /// tolerance-dependent, so an ambient value makes verdicts depend on document setup: at 0.001 m
    /// the union silently fails and every case reports a ~10 m deviation against a fragment.
    /// </summary>
    private const double GeometryToleranceMetres = 0.01;

    /// <summary>Chord the rigid-body geometry predicts between the two outer wheel-edge traces.</summary>
    private static double ExpectedChord(VehicleDefinition vehicle) => Math.Sqrt(
        (vehicle.WheelbaseMetres * vehicle.WheelbaseMetres) +
        (4.0 * vehicle.WheelOuterEdgeOffsetMetres * vehicle.WheelOuterEdgeOffsetMetres));
    private static readonly WheelTrackReconstructionOptions ReconstructionOptions = new()
    {
        SampleSpacingMetres = 0.025,
        MaximumSeparationErrorMetres = 0.15,
        MinimumSampleCount = 500
    };

    private sealed record ReferenceFile(string VehicleId, string ModeId, string RelativePath, string BlockName);
    private sealed record IndexedCurve(int Index, Curve Curve, System.Drawing.Color Color);
    private sealed record ExtractedCase(
        int AngleGon,
        Curve ReferenceEnvelope,
        Curve OuterFrontWheelTrack,
        Curve OuterRearWheelTrack,
        double WheelOuterEdgeOffsetMetres,
        IReadOnlyList<Curve> WheelTraceCandidates,
        IReadOnlyList<Curve> AllWheelChains,
        Point3d IncomingLower,
        Point3d IncomingUpper);
    private sealed record DistanceDetail(
        double DistanceMetres,
        Point3d SourcePoint,
        Point3d TargetPoint,
        IReadOnlyList<double> AllDistances);

    public static CertificationPlan LoadPlan(string repositoryRoot) =>
        CertificationPlan.Load(Path.Combine(repositoryRoot, "reference", "certification", CertificationPlan.FileName));

    public static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(Path.GetDirectoryName(typeof(ReferenceCertificationService).Assembly.Location)!);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "RhinoRoad.sln"))) return directory.FullName;
            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not find RhinoRoad.sln above the loaded plugin.");
    }

    public static ReferenceCertificationReport Run(RhinoDoc document, string repositoryRoot, CertificationPlan plan)
    {
        // The reference geometry arrives in document units, so a non-metric document would compare
        // it against a metre-built sweep. Fail loudly rather than emit meaningless verdicts.
        if (document.ModelUnitSystem != UnitSystem.Meters)
        {
            throw new InvalidOperationException(
                $"Reference certification requires a document in meters; this document is in {document.ModelUnitSystem}.");
        }

        var catalog = VehicleCatalog.LoadEmbedded();
        var cases = new List<ReferenceCertificationCase>();
        var fixtureDirectory = Path.Combine(repositoryRoot, "reference", "certification", "fixtures");
        Directory.CreateDirectory(fixtureDirectory);
        var referenceFiles = plan.Vehicles
            .SelectMany(vehicle => vehicle.Drawings.Select(drawing =>
                new ReferenceFile(vehicle.VehicleId, drawing.ModeId, drawing.Path, drawing.BlockName)))
            .ToArray();
        foreach (var referenceFile in referenceFiles)
        {
            var path = Path.Combine(repositoryRoot, referenceFile.RelativePath);
            if (!File.Exists(path)) throw new FileNotFoundException("Reference DWG was not found.", path);
            var sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
            try
            {
                var vehicle = catalog.Get(referenceFile.VehicleId);
                var extracted = ImportAndExtract(document, path, referenceFile.BlockName, vehicle, plan.AnglesGon);
                var drivingMode = vehicle.DrivingModes[referenceFile.ModeId];
                foreach (var angle in plan.AnglesGon)
                {
                    if (!extracted.TryGetValue(angle, out var sourceCase))
                    {
                        cases.Add(new ReferenceCertificationCase(
                            referenceFile.VehicleId, referenceFile.ModeId, angle, referenceFile.RelativePath, sha256,
                            null, null, null, null, null, ExpectedChord(vehicle), 0, false,
                            "The official DWG did not contain extractable geometry for the requested annotated case."));
                        continue;
                    }

                    WriteFixture(fixtureDirectory, referenceFile, sha256, vehicle, sourceCase);
                    var evaluated = EvaluateCase(document, vehicle, drivingMode, referenceFile, sha256, sourceCase);
                    cases.Add(evaluated);
                    RhinoApp.WriteLine(
                        $"  {evaluated.VehicleId} {evaluated.ModeId} {evaluated.AngleGon} gon: " +
                        $"dev={evaluated.MaximumEnvelopeDeviationMetres:0.000} m, " +
                        $"inward={evaluated.MaximumInwardUnderpredictionMetres:0.000} m " +
                        $"{(evaluated.Passed ? "PASS" : "FAIL")}");
                }
            }
            catch (Exception exception)
            {
                RhinoApp.WriteLine($"  {referenceFile.VehicleId} {referenceFile.ModeId}: extraction failed: {exception.Message}");
                foreach (var angle in plan.AnglesGon)
                {
                    cases.Add(new ReferenceCertificationCase(
                        referenceFile.VehicleId, referenceFile.ModeId, angle, referenceFile.RelativePath, sha256,
                            null, null, null, null, null, ExpectedChord(catalog.Get(referenceFile.VehicleId)), 0, false, $"DWG extraction failed: {exception.Message}"));
                }
            }
        }

        return new ReferenceCertificationReport(
            ReferenceCertificationReport.CurrentSchemaVersion,
            DateTimeOffset.UtcNow.ToString("O"),
            RhinoApp.Version.ToString(),
            "Official DWG red envelope compared to RhinoRoad sweep reconstructed from paired outer front/rear wheel-area boundaries. Vehicle heading and rear-axle midpoint are solved from the rigid-body chord between the two traces using the preset wheelbase, axle track and tyre width; the chord the drawing's own traces hold is measured and reported alongside the chord the preset predicts, so a disagreement between drawing and preset is visible. Traces tessellated at differing densities are reconciled against that chord. Swept envelopes are built at a fixed geometry tolerance so verdicts do not depend on document setup.",
            new ReferenceCertificationEnvironment(
                document.ModelUnitSystem.ToString(),
                document.ModelAbsoluteTolerance,
                GeometryToleranceMetres),
            Thresholds,
            cases);
    }

    public static string WriteReport(string repositoryRoot, ReferenceCertificationReport report)
    {
        var directory = Path.Combine(repositoryRoot, "reference", "certification");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "reference-certification.json");
        var options = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            Converters = { new JsonStringEnumConverter() }
        };
        File.WriteAllText(path, JsonSerializer.Serialize(report, options) + System.Environment.NewLine);
        return path;
    }

    private static Dictionary<int, ExtractedCase> ImportAndExtract(RhinoDoc document, string path, string blockName, VehicleDefinition vehicle, IReadOnlyList<int> angles)
    {
        var priorObjects = document.Objects
            .Where(item => !item.IsInstanceDefinitionGeometry)
            .Select(item => item.Id)
            .ToHashSet();
        var priorDefinitions = document.InstanceDefinitions.Where(item => item is not null).Select(item => item.Id).ToHashSet();
        var escapedPath = path.Replace("\"", "\"\"");
        var imported = RhinoApp.RunScript(document.RuntimeSerialNumber, $"_-Import \"{escapedPath}\" _Enter", false);
        if (!imported) throw new InvalidOperationException($"Rhino could not import '{path}'.");

        try
        {
            var topLevel = document.Objects
                .Where(item => !item.IsInstanceDefinitionGeometry && !priorObjects.Contains(item.Id))
                .Select(item => (Object: item, Reference: item.Geometry as InstanceReferenceGeometry))
                .Where(item => item.Reference is not null)
                .Select(item => (item.Object, Reference: item.Reference!, Definition: document.InstanceDefinitions.FindId(item.Reference!.ParentIdefId)))
                .FirstOrDefault(item => item.Definition is not null &&
                    item.Definition.Name.Equals(blockName, StringComparison.OrdinalIgnoreCase));
            var definition = topLevel.Definition ?? document.InstanceDefinitions
                .FirstOrDefault(item => !priorDefinitions.Contains(item.Id) &&
                    item.Name.Equals(blockName, StringComparison.OrdinalIgnoreCase));
            if (definition is null)
            {
                var available = string.Join(", ", document.InstanceDefinitions
                    .Where(item => item is not null && !priorDefinitions.Contains(item.Id))
                    .Select(item => item.Name));
                throw new InvalidDataException($"Block '{blockName}' was not found in '{path}'. Imported blocks: {available}");
            }

            var transform = topLevel.Definition is null ? Transform.Identity : topLevel.Reference.Xform;
            return ExtractCases(definition, transform, vehicle, angles);
        }
        finally
        {
            foreach (var item in document.Objects.Where(item =>
                         !item.IsInstanceDefinitionGeometry && !priorObjects.Contains(item.Id)).ToArray())
            {
                document.Objects.Delete(item.Id, true);
            }

            var newDefinitionIds = document.InstanceDefinitions
                .Where(item => item is not null && !priorDefinitions.Contains(item.Id))
                .Select(item => item.Id)
                .ToArray();
            for (var pass = 0; pass < 4; pass++)
            {
                var remaining = newDefinitionIds
                    .Select(id => document.InstanceDefinitions.FindId(id))
                    .Where(item => item is not null)
                    .OrderByDescending(item => item.Index)
                    .ToArray();
                if (remaining.Length == 0) break;
                foreach (var definition in remaining)
                {
                    document.InstanceDefinitions.Delete(definition.Index, true, true);
                    var current = document.InstanceDefinitions.FindId(definition.Id);
                    if (current is not null) document.InstanceDefinitions.Purge(current.Index);
                }
            }
        }
    }

    private static Dictionary<int, ExtractedCase> ExtractCases(InstanceDefinition definition, Transform transform, VehicleDefinition vehicle, IReadOnlyList<int> angles)
    {
        var curves = new List<IndexedCurve>();
        var annotations = new List<(int Index, int Angle)>();
        var objects = definition.GetObjects();
        for (var index = 0; index < objects.Length; index++)
        {
            if (objects[index].Geometry is Curve sourceCurve)
            {
                var curve = sourceCurve.DuplicateCurve();
                curve.Transform(transform);
                curves.Add(new IndexedCurve(index, curve, objects[index].Attributes.ObjectColor));
            }
            else if (objects[index].Geometry is TextEntity text)
            {
                var firstLine = text.PlainText.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
                if (firstLine is not null)
                {
                    var digits = new string(firstLine.TakeWhile(char.IsDigit).ToArray());
                    if (int.TryParse(digits, out var angle)) annotations.Add((index, angle));
                }
            }
        }

        var red = curves.Where(item => IsRed(item.Color)).ToArray();
        var yellow = curves.Where(item => IsYellow(item.Color)).ToArray();
        if (red.Length < 4 || yellow.Length < 4 || annotations.Count == 0)
        {
            throw new InvalidDataException($"Block '{definition.Name}' does not contain the expected red/yellow reference geometry.");
        }

        var redEndpoints = red.SelectMany(item => Endpoints(item.Curve)).ToArray();
        var endpointFrequencies = redEndpoints
            .Select(point => (Point: point, Count: redEndpoints.Count(other => point.DistanceTo(other) <= 0.05)))
            .ToArray();
        var maximumFrequency = endpointFrequencies.Max(item => item.Count);
        var repeatedIncomingEndpoints = endpointFrequencies
            .Where(item => item.Count == maximumFrequency)
            .Select(item => item.Point)
            .ToArray();
        var incomingRed = repeatedIncomingEndpoints.OrderBy(point => point.Y).ThenBy(point => point.X).First();
        var incomingRedUpper = repeatedIncomingEndpoints.OrderByDescending(point => point.Y).ThenBy(point => point.X).First();
        var firstAnnotationIndex = annotations.Min(item => item.Index);
        var groupStarts = red
            .Where(item => item.Index < firstAnnotationIndex &&
                item.Curve.GetBoundingBox(true).Diagonal.Y > 0.50 &&
                Endpoints(item.Curve).Any(point => point.DistanceTo(incomingRed) <= 0.05))
            .OrderBy(item => item.Index)
            .ToArray();
        if (groupStarts.Length < 8 || groupStarts.Length > annotations.Count)
        {
            throw new InvalidDataException($"Block '{definition.Name}' yielded {groupStarts.Length} geometry groups for {annotations.Count} annotations.");
        }

        var result = new Dictionary<int, ExtractedCase>();
        for (var groupIndex = 0; groupIndex < groupStarts.Length; groupIndex++)
        {
            if (!angles.Contains(annotations[groupIndex].Angle)) continue;
            var startIndex = groupIndex == 0 ? 0 : groupStarts[groupIndex].Index - 2;
            var endIndex = groupIndex + 1 < groupStarts.Length ? groupStarts[groupIndex + 1].Index - 2 : firstAnnotationIndex;
            var groupRed = red.Where(item => item.Index >= startIndex && item.Index < endIndex).Select(item => item.Curve).ToArray();
            var groupYellow = yellow.Where(item => item.Index >= startIndex && item.Index < endIndex).Select(item => item.Curve).ToArray();
            var redChains = Curve.JoinCurves(groupRed, JoinToleranceMetres, preserveDirection: false) ?? [];
            var yellowChains = Curve.JoinCurves(groupYellow, JoinToleranceMetres, preserveDirection: false) ?? [];
            if (redChains.Length < 2 || yellowChains.Length < 2)
            {
                throw new InvalidDataException($"Block '{definition.Name}' case {annotations[groupIndex].Angle} gon could not be joined into two envelope and wheel chains.");
            }

            var lowerSource = redChains.OrderBy(chain => MinimumEndpointDistance(chain, incomingRed)).First();
            var upperSource = redChains
                .Where(chain => !ReferenceEquals(chain, lowerSource))
                .OrderBy(chain => MinimumEndpointDistance(chain, incomingRedUpper))
                .First();
            var lowerRed = OrientFromPoint(lowerSource, incomingRed);
            var upperRed = OrientFromPoint(upperSource, incomingRedUpper);
            var viableYellow = yellowChains
                .Where(chain => chain.GetLength() >= 10.0)
                .OrderBy(chain => chain.GetLength())
                .ToArray();
            if (viableYellow.Length < 2)
            {
                throw new InvalidDataException(
                    $"Block '{definition.Name}' case {annotations[groupIndex].Angle} gon did not contain both viable front and rear wheel traces.");
            }

            var outerRearWheel = OrientFromPoint(viableYellow[0], incomingRedUpper);
            var outerFrontWheel = OrientFromPoint(viableYellow[^1], incomingRed);
            var envelope = CloseEnvelope(lowerRed, upperRed);
            result[annotations[groupIndex].Angle] = new ExtractedCase(
                annotations[groupIndex].Angle,
                envelope,
                outerFrontWheel,
                outerRearWheel,
                vehicle.WheelOuterEdgeOffsetMetres,
                viableYellow,
                yellowChains,
                incomingRed,
                incomingRedUpper);
        }

        return result;
    }

    private static ReferenceCertificationCase EvaluateCase(
        RhinoDoc document,
        VehicleDefinition vehicle,
        DrivingModeDefinition mode,
        ReferenceFile referenceFile,
        string sha256,
        ExtractedCase sourceCase)
    {
        var reconstruction = ReconstructRoute(vehicle, sourceCase);
        var route = reconstruction.Samples;
        var selfIntersections = Intersection.CurveSelf(sourceCase.ReferenceEnvelope, 0.001);
        if (!reconstruction.IsSuccess || selfIntersections.Count > 0)
        {
            var reason = reconstruction.IsSuccess
                ? "The extracted reference envelope self-intersects."
                : reconstruction.Message;
            return new ReferenceCertificationCase(
                referenceFile.VehicleId, referenceFile.ModeId, sourceCase.AngleGon, referenceFile.RelativePath, sha256,
                null, null, null, null, null, ExpectedChord(vehicle), route.Count, false,
                $"DWG path extraction failed sanity checks; no deviation is reported for this case. {reason}");
        }
        var analysis = new VehicleAccessAnalyzer().Analyze(vehicle, mode, route);
        var built = RhinoGeometryBuilder.Build(
            analysis, route, UnitSystem.Meters, GeometryToleranceMetres, 0.0, 0.0, 0.0, false);
        var generated = built.BodyEnvelope;
        if (generated is null || !built.BodyEnvelopeMerged)
        {
            var reason = generated is null
                ? "Rhino could not construct the generated swept envelope."
                : string.Join(" ", built.Warnings);
            return new ReferenceCertificationCase(
                referenceFile.VehicleId, referenceFile.ModeId, sourceCase.AngleGon, referenceFile.RelativePath, sha256,
                null, null, null, reconstruction.MeasuredChordMetres, reconstruction.MeasuredChordSpreadMetres, ExpectedChord(vehicle), route.Count, false,
                $"The generated swept envelope is not trustworthy, so no deviation is reported. {reason}");
        }

        var referenceToGenerated = MaximumDistanceDetail(sourceCase.ReferenceEnvelope, generated);
        var generatedToReference = MaximumDistanceDetail(generated, sourceCase.ReferenceEnvelope);
        var maximumDeviation = Math.Max(referenceToGenerated.DistanceMetres, generatedToReference.DistanceMetres);
        var deviation = DeviationStatistics.From(
            [.. referenceToGenerated.AllDistances, .. generatedToReference.AllDistances],
            Thresholds.MaximumEnvelopeDeviationMetres);
        var inward = MaximumOutsideDistance(sourceCase.ReferenceEnvelope, generated);
        var passed = Thresholds.Accepts(maximumDeviation, inward);
        var notes = passed
            ? "Meets both reference tolerances."
            : $"Exceeds tolerance on {deviation.ShareOverToleranceFraction * 100.0:0.0}% of the compared " +
              $"boundary length (median {deviation.MedianMetres:0.000} m, 90th percentile " +
              $"{deviation.Percentile90Metres:0.000} m, maximum {deviation.MaximumMetres:0.000} m). " +
              "The preset remains unvalidated.";

        var sourceBounds = sourceCase.ReferenceEnvelope.GetBoundingBox(true);
        var generatedBounds = generated.GetBoundingBox(true);
        RhinoApp.WriteLine(
            $"    chord measured={reconstruction.MeasuredChordMetres:0.000} m " +
            $"expected={ExpectedChord(vehicle):0.000} m (spread {reconstruction.MeasuredChordSpreadMetres:0.000} m) samples={route.Count}; " +
            $"ref=({sourceBounds.Min.X:0.00},{sourceBounds.Min.Y:0.00})..({sourceBounds.Max.X:0.00},{sourceBounds.Max.Y:0.00}); " +
            $"gen=({generatedBounds.Min.X:0.00},{generatedBounds.Min.Y:0.00})..({generatedBounds.Max.X:0.00},{generatedBounds.Max.Y:0.00})");
        RhinoApp.WriteLine(
            $"    ref->gen={referenceToGenerated.DistanceMetres:0.000} m " +
            $"at ({referenceToGenerated.SourcePoint.X:0.000},{referenceToGenerated.SourcePoint.Y:0.000}) -> " +
            $"({referenceToGenerated.TargetPoint.X:0.000},{referenceToGenerated.TargetPoint.Y:0.000}); " +
            $"gen->ref={generatedToReference.DistanceMetres:0.000} m " +
            $"at ({generatedToReference.SourcePoint.X:0.000},{generatedToReference.SourcePoint.Y:0.000}) -> " +
            $"({generatedToReference.TargetPoint.X:0.000},{generatedToReference.TargetPoint.Y:0.000})");
        return new ReferenceCertificationCase(
            referenceFile.VehicleId, referenceFile.ModeId, sourceCase.AngleGon, referenceFile.RelativePath, sha256,
            maximumDeviation, inward, deviation, reconstruction.MeasuredChordMetres, reconstruction.MeasuredChordSpreadMetres,
            ExpectedChord(vehicle), route.Count, passed, notes);
    }

    /// <summary>
    /// Writes the extracted geometry for one case as data, at the boundary between DWG extraction
    /// (Rhino-only, per-drawing conventions) and route reconstruction (pure, tested). Extraction is
    /// where per-vehicle fragility lives; dumping it here lets Core replay and diagnose every
    /// vehicle offline in milliseconds instead of through a Rhino round trip.
    /// </summary>
    private static void WriteFixture(
        string directory,
        ReferenceFile referenceFile,
        string sha256,
        VehicleDefinition vehicle,
        ExtractedCase sourceCase)
    {
        try
        {
            var fixture = new CertificationFixture(
                CertificationFixture.CurrentSchemaVersion,
                referenceFile.VehicleId,
                referenceFile.ModeId,
                sourceCase.AngleGon,
                referenceFile.RelativePath,
                sha256,
                vehicle.WheelbaseMetres,
                sourceCase.WheelOuterEdgeOffsetMetres,
                Vertices(sourceCase.ReferenceEnvelope),
                Vertices(sourceCase.OuterFrontWheelTrack),
                Vertices(sourceCase.OuterRearWheelTrack),
                sourceCase.WheelTraceCandidates.Select(Vertices).ToArray(),
                sourceCase.AllWheelChains.Select(Vertices).ToArray());

            var name = $"{referenceFile.VehicleId}_{referenceFile.ModeId}_{sourceCase.AngleGon}.json";
            File.WriteAllText(
                Path.Combine(directory, name),
                JsonSerializer.Serialize(fixture, FixtureJsonOptions) + System.Environment.NewLine);
        }
        catch (Exception exception)
        {
            RhinoApp.WriteLine($"    fixture dump failed: {exception.Message}");
        }
    }

    private static readonly JsonSerializerOptions FixtureJsonOptions = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    /// <summary>Plan vertices of a curve; non-polyline curves are divided at the sampling spacing.</summary>
    private static double[][] Vertices(Curve curve)
    {
        if (curve.TryGetPolyline(out var polyline))
        {
            return polyline.Select(point => new[] { point.X, point.Y }).ToArray();
        }

        var parameters = curve.DivideByLength(BoundarySpacingMetres, true)
                         ?? [curve.Domain.T0, curve.Domain.T1];
        return parameters
            .Select(parameter => curve.PointAt(parameter))
            .Select(point => new[] { point.X, point.Y })
            .ToArray();
    }

    /// <summary>
    /// Reconstructs the route from the wheel traces the extractor chose by chain length. That
    /// heuristic mis-picks when a case carries more than two long yellow chains, so on failure the
    /// remaining candidates are searched for a pair that actually reconciles against the rigid-body
    /// chord. The heuristic pair is tried first, leaving already-reconciling cases untouched.
    /// </summary>
    private static WheelTrackReconstruction ReconstructRoute(VehicleDefinition vehicle, ExtractedCase sourceCase)
    {
        var chosen = WheelTrackRouteReconstructor.Reconstruct(
            PlanVertices(sourceCase.OuterFrontWheelTrack),
            PlanVertices(sourceCase.OuterRearWheelTrack),
            vehicle.WheelbaseMetres,
            sourceCase.WheelOuterEdgeOffsetMetres,
            ReconstructionOptions);
        if (chosen.IsSuccess) return chosen;

        var anchors = new[] { sourceCase.IncomingLower, sourceCase.IncomingUpper };
        foreach (var frontSource in sourceCase.WheelTraceCandidates)
        {
            foreach (var rearSource in sourceCase.WheelTraceCandidates)
            {
                if (ReferenceEquals(frontSource, rearSource)) continue;
                foreach (var frontAnchor in anchors)
                {
                    foreach (var rearAnchor in anchors)
                    {
                        var candidate = WheelTrackRouteReconstructor.Reconstruct(
                            PlanVertices(OrientFromPoint(frontSource, frontAnchor)),
                            PlanVertices(OrientFromPoint(rearSource, rearAnchor)),
                            vehicle.WheelbaseMetres,
                            sourceCase.WheelOuterEdgeOffsetMetres,
                            ReconstructionOptions);
                        if (candidate.IsSuccess)
                        {
                            RhinoApp.WriteLine(
                                $"    recovered a chord-consistent wheel-trace pair after the length heuristic failed: {candidate.Message}");
                            return candidate;
                        }
                    }
                }
            }
        }

        return chosen;
    }

    /// <summary>
    /// Projects an extracted wheel-edge polyline onto the plan for
    /// <see cref="WheelTrackRouteReconstructor"/>; a non-polyline curve yields no vertices,
    /// which the reconstructor reports as an insufficient-vertex failure.
    /// </summary>
    private static IReadOnlyList<Point2> PlanVertices(Curve curve) =>
        curve.TryGetPolyline(out var polyline)
            ? polyline.Select(point => new Point2(point.X, point.Y)).ToArray()
            : [];

    private static Curve CloseEnvelope(Curve first, Curve second)
    {
        var secondReversed = second.DuplicateCurve();
        secondReversed.Reverse();
        var curves = new Curve[]
        {
            first.DuplicateCurve(),
            new LineCurve(first.PointAtEnd, secondReversed.PointAtStart),
            secondReversed,
            new LineCurve(secondReversed.PointAtEnd, first.PointAtStart)
        };
        return Curve.JoinCurves(curves, JoinToleranceMetres, true)?
                   .Where(item => item.IsClosed)
                   .OrderByDescending(item => Math.Abs(AreaMassProperties.Compute(item)?.Area ?? 0.0))
                   .FirstOrDefault()
               ?? throw new InvalidDataException("The reference envelope could not be closed.");
    }

    private static Curve OrientFromPoint(Curve source, Point3d incomingPoint)
    {
        var curve = source.DuplicateCurve();
        if (curve.PointAtEnd.DistanceTo(incomingPoint) < curve.PointAtStart.DistanceTo(incomingPoint)) curve.Reverse();
        return curve;
    }

    private static double MinimumEndpointDistance(Curve curve, Point3d point) =>
        Math.Min(curve.PointAtStart.DistanceTo(point), curve.PointAtEnd.DistanceTo(point));

    private static IEnumerable<Point3d> Endpoints(Curve curve)
    {
        yield return curve.PointAtStart;
        yield return curve.PointAtEnd;
    }

    private static double MaximumDistance(Curve source, Curve target)
        => MaximumDistanceDetail(source, target).DistanceMetres;

    private static DistanceDetail MaximumDistanceDetail(Curve source, Curve target)
    {
        var parameters = source.DivideByLength(BoundarySpacingMetres, true) ?? [source.Domain.T0, source.Domain.T1];
        var maximum = 0.0;
        var maximumSource = source.PointAtStart;
        var maximumTarget = target.PointAtStart;
        var distances = new List<double>(parameters.Length);
        foreach (var parameter in parameters)
        {
            var point = source.PointAt(parameter);
            if (target.ClosestPoint(point, out var targetParameter))
            {
                var targetPoint = target.PointAt(targetParameter);
                var distance = point.DistanceTo(targetPoint);
                distances.Add(distance);
                if (distance > maximum)
                {
                    maximum = distance;
                    maximumSource = point;
                    maximumTarget = targetPoint;
                }
            }
        }

        return new DistanceDetail(maximum, maximumSource, maximumTarget, distances);
    }

    private static double MaximumOutsideDistance(Curve reference, Curve generated)
    {
        var parameters = reference.DivideByLength(BoundarySpacingMetres, true) ?? [reference.Domain.T0, reference.Domain.T1];
        var maximum = 0.0;
        foreach (var parameter in parameters)
        {
            var point = reference.PointAt(parameter);
            if (generated.Contains(point, Plane.WorldXY, 0.001) == PointContainment.Outside &&
                generated.ClosestPoint(point, out var generatedParameter))
            {
                maximum = Math.Max(maximum, point.DistanceTo(generated.PointAt(generatedParameter)));
            }
        }

        return maximum;
    }

    private static bool IsRed(System.Drawing.Color color) => color.R > 240 && color.G < 20 && color.B < 20;
    private static bool IsYellow(System.Drawing.Color color) => color.R > 240 && color.G > 240 && color.B < 20;
}
