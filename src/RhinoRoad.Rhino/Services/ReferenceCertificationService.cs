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
    private const double RouteSpacingMetres = 0.025;
    private const double BoundarySpacingMetres = 0.025;
    private const double JoinToleranceMetres = 0.02;
    private static readonly int[] AnglesGon = [40, 100, 180];
    private static readonly ReferenceCertificationThresholds Thresholds = new(0.10, 0.05);

    private sealed record ReferenceFile(string VehicleId, string ModeId, string RelativePath, string BlockName);
    private sealed record IndexedCurve(int Index, Curve Curve, System.Drawing.Color Color);
    private sealed record ExtractedCase(int AngleGon, Curve ReferenceEnvelope, Curve InnerRearWheelTrack, double TrackWidthMetres);

    private static readonly ReferenceFile[] Files =
    [
        new("PV", "A", @"reference\vejdirektoratet-koerekurver\01-koeremaade-a\PV_A.dwg", "PV A"),
        new("PV", "B", @"reference\vejdirektoratet-koerekurver\02-koeremaade-b\PV_B.dwg", "PV B"),
        new("REN", "A", @"reference\vejdirektoratet-koerekurver\01-koeremaade-a\REN_A.dwg", "REN A"),
        new("REN", "B", @"reference\vejdirektoratet-koerekurver\02-koeremaade-b\REN_B.dwg", "REN_B"),
        new("BUS12", "A", @"reference\vejdirektoratet-koerekurver\01-koeremaade-a\BUS_12_A.dwg", "BUS 12 A"),
        new("BUS12", "B", @"reference\vejdirektoratet-koerekurver\02-koeremaade-b\BUS_12_B.dwg", "BUS 12 B")
    ];

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

    public static ReferenceCertificationReport Run(RhinoDoc document, string repositoryRoot)
    {
        var catalog = VehicleCatalog.LoadEmbedded();
        var cases = new List<ReferenceCertificationCase>();
        foreach (var referenceFile in Files)
        {
            var path = Path.Combine(repositoryRoot, referenceFile.RelativePath);
            if (!File.Exists(path)) throw new FileNotFoundException("Reference DWG was not found.", path);
            var sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
            try
            {
                var extracted = ImportAndExtract(document, path, referenceFile.BlockName);
                var vehicle = catalog.Get(referenceFile.VehicleId);
                var drivingMode = vehicle.DrivingModes[referenceFile.ModeId];
                foreach (var angle in AnglesGon)
                {
                    if (!extracted.TryGetValue(angle, out var sourceCase))
                    {
                        cases.Add(new ReferenceCertificationCase(
                            referenceFile.VehicleId, referenceFile.ModeId, angle, referenceFile.RelativePath, sha256,
                            null, null, null, 0, false,
                            "The official DWG did not contain extractable geometry for the requested annotated case."));
                        continue;
                    }

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
                foreach (var angle in AnglesGon)
                {
                    cases.Add(new ReferenceCertificationCase(
                        referenceFile.VehicleId, referenceFile.ModeId, angle, referenceFile.RelativePath, sha256,
                        null, null, null, 0, false, $"DWG extraction failed: {exception.Message}"));
                }
            }
        }

        return new ReferenceCertificationReport(
            "1.0",
            DateTimeOffset.UtcNow.ToString("O"),
            RhinoApp.Version.ToString(),
            "Official DWG red envelope compared to RhinoRoad sweep reconstructed from the DWG inner rear-wheel trace.",
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

    private static Dictionary<int, ExtractedCase> ImportAndExtract(RhinoDoc document, string path, string blockName)
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
            return ExtractCases(definition, transform);
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

    private static Dictionary<int, ExtractedCase> ExtractCases(InstanceDefinition definition, Transform transform)
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
            if (!AnglesGon.Contains(annotations[groupIndex].Angle)) continue;
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
            var centreY = (lowerRed.PointAtStart.Y + upperRed.PointAtStart.Y) * 0.5;
            var innerWheelSource = yellowChains.OrderBy(chain => MinimumEndpointDistance(chain, incomingRedUpper)).First();
            var innerWheel = OrientFromPoint(innerWheelSource, incomingRedUpper);
            var trackHalf = Math.Abs(innerWheel.PointAtStart.Y - centreY);
            var envelope = CloseEnvelope(lowerRed, upperRed);
            result[annotations[groupIndex].Angle] = new ExtractedCase(annotations[groupIndex].Angle, envelope, innerWheel, trackHalf * 2.0);
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
        var route = RouteFromInnerRearTrack(sourceCase.InnerRearWheelTrack, sourceCase.TrackWidthMetres * 0.5);
        var selfIntersections = Intersection.CurveSelf(sourceCase.ReferenceEnvelope, 0.001);
        if (sourceCase.TrackWidthMetres <= 0.0 || sourceCase.TrackWidthMetres > vehicle.WidthMetres + 0.10 ||
            route.Count < 500 || selfIntersections.Count > 0)
        {
            return new ReferenceCertificationCase(
                referenceFile.VehicleId, referenceFile.ModeId, sourceCase.AngleGon, referenceFile.RelativePath, sha256,
                null, null, sourceCase.TrackWidthMetres, route.Count, false,
                "DWG path extraction failed sanity checks; no deviation is reported for this case.");
        }
        var analysis = new VehicleAccessAnalyzer().Analyze(vehicle, mode, route);
        var generated = RhinoGeometryBuilder.Build(analysis, route, document, 0.0, 0.0, 0.0, false).BodyEnvelope;
        if (generated is null)
        {
            return new ReferenceCertificationCase(
                referenceFile.VehicleId, referenceFile.ModeId, sourceCase.AngleGon, referenceFile.RelativePath, sha256,
                null, null, sourceCase.TrackWidthMetres, route.Count, false,
                "Rhino could not construct the generated swept envelope.");
        }

        var maximumDeviation = Math.Max(
            MaximumDistance(sourceCase.ReferenceEnvelope, generated),
            MaximumDistance(generated, sourceCase.ReferenceEnvelope));
        var inward = MaximumOutsideDistance(sourceCase.ReferenceEnvelope, generated);
        var passed = maximumDeviation <= Thresholds.MaximumEnvelopeDeviationMetres + 1e-9 &&
                     inward <= Thresholds.MaximumInwardUnderpredictionMetres + 1e-9;
        var notes = passed
            ? "Meets both reference tolerances."
            : "Exceeds one or both reference tolerances; the preset remains unvalidated.";
        var sourceBounds = sourceCase.ReferenceEnvelope.GetBoundingBox(true);
        var generatedBounds = generated.GetBoundingBox(true);
        RhinoApp.WriteLine(
            $"    track={sourceCase.TrackWidthMetres:0.000} m samples={route.Count}; " +
            $"ref=({sourceBounds.Min.X:0.00},{sourceBounds.Min.Y:0.00})..({sourceBounds.Max.X:0.00},{sourceBounds.Max.Y:0.00}); " +
            $"gen=({generatedBounds.Min.X:0.00},{generatedBounds.Min.Y:0.00})..({generatedBounds.Max.X:0.00},{generatedBounds.Max.Y:0.00})");
        return new ReferenceCertificationCase(
            referenceFile.VehicleId, referenceFile.ModeId, sourceCase.AngleGon, referenceFile.RelativePath, sha256,
            maximumDeviation, inward, sourceCase.TrackWidthMetres, route.Count, passed, notes);
    }

    private static IReadOnlyList<RouteSample> RouteFromInnerRearTrack(Curve source, double trackHalfMetres)
    {
        var curve = source.DuplicateCurve();
        var startScore = Math.Abs(curve.TangentAtStart.Y) + Math.Max(0.0, curve.TangentAtStart.X);
        var endScore = Math.Abs(curve.TangentAtEnd.Y) + Math.Max(0.0, curve.TangentAtEnd.X);
        if (endScore < startScore) curve.Reverse();
        var parameters = curve.DivideByLength(RouteSpacingMetres, true) ?? [curve.Domain.T0, curve.Domain.T1];
        if (parameters[^1] < curve.Domain.T1 - 1e-9) parameters = [.. parameters, curve.Domain.T1];
        var raw = new List<(Point3 Point, double Heading)>(parameters.Length);
        foreach (var parameter in parameters)
        {
            var wheel = curve.PointAt(parameter);
            var tangent = curve.TangentAt(parameter);
            tangent.Z = 0.0;
            if (!tangent.Unitize()) continue;
            var centre = new Point3d(
                wheel.X - (tangent.Y * trackHalfMetres),
                wheel.Y + (tangent.X * trackHalfMetres),
                0.0);
            raw.Add((new Point3(centre.X, centre.Y, 0.0), Math.Atan2(tangent.Y, tangent.X)));
        }

        var stations = new double[raw.Count];
        for (var index = 1; index < raw.Count; index++)
        {
            stations[index] = stations[index - 1] + raw[index].Point.XY.DistanceTo(raw[index - 1].Point.XY);
        }

        var samples = new List<RouteSample>(raw.Count);
        for (var index = 0; index < raw.Count; index++)
        {
            var before = Math.Max(0, index - 1);
            var after = Math.Min(raw.Count - 1, index + 1);
            var span = Math.Max(stations[after] - stations[before], 1e-9);
            var curvature = Geometry2D.NormalizeAngle(raw[after].Heading - raw[before].Heading) / span;
            samples.Add(new RouteSample(stations[index], raw[index].Point, raw[index].Heading, curvature, TravelDirection.Forward));
        }

        return samples;
    }

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
    {
        var parameters = source.DivideByLength(BoundarySpacingMetres, true) ?? [source.Domain.T0, source.Domain.T1];
        var maximum = 0.0;
        foreach (var parameter in parameters)
        {
            var point = source.PointAt(parameter);
            if (target.ClosestPoint(point, out var targetParameter))
            {
                maximum = Math.Max(maximum, point.DistanceTo(target.PointAt(targetParameter)));
            }
        }

        return maximum;
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
