using System.Text.Json;

namespace RhinoRoad.Core;

/// <summary>
/// The geometry extracted from one official DWG case, captured as data.
/// </summary>
/// <remarks>
/// This is the seam between DWG extraction — Rhino-only, and governed by per-drawing conventions
/// that vary across the published catalogue — and route reconstruction, which is pure and vehicle
/// agnostic. Extraction is where adding a vehicle actually risks failing, so capturing its output
/// makes that risk testable offline: a fixture per case, replayable in milliseconds, diffable in
/// review, and covering any vehicle added to the plan without new test code.
/// </remarks>
public sealed record CertificationFixture(
    string SchemaVersion,
    string VehicleId,
    string ModeId,
    int AngleGon,
    string SourceFile,
    string SourceSha256,
    double WheelbaseMetres,
    double WheelOuterEdgeOffsetMetres,
    double[][] ReferenceEnvelope,
    double[][] ChosenFrontWheelTrack,
    double[][] ChosenRearWheelTrack,
    double[][][] WheelTraceCandidates,
    double[][][] AllWheelChains)
{
    public const string CurrentSchemaVersion = "1.1";

    public string CaseId => $"{VehicleId}/{ModeId}/{AngleGon}";

    public static IReadOnlyList<Point2> ToPoints(double[][] vertices) => vertices
        .Where(vertex => vertex.Length >= 2)
        .Select(vertex => new Point2(vertex[0], vertex[1]))
        .ToArray();

    public IReadOnlyList<Point2> FrontPoints => ToPoints(ChosenFrontWheelTrack);
    public IReadOnlyList<Point2> RearPoints => ToPoints(ChosenRearWheelTrack);
    public IReadOnlyList<IReadOnlyList<Point2>> CandidatePoints => WheelTraceCandidates
        .Select(ToPoints)
        .ToArray();

    /// <summary>
    /// Every joined wheel chain in the case, before the length filter that picks candidates.
    /// Captured so a partner trace discarded by that filter is visible offline.
    /// </summary>
    public IReadOnlyList<IReadOnlyList<Point2>> AllChainPoints => (AllWheelChains ?? [])
        .Select(ToPoints)
        .ToArray();

    /// <summary>The chord the rigid-body geometry predicts between the two traces.</summary>
    public double ExpectedChordMetres => Math.Sqrt(
        (WheelbaseMetres * WheelbaseMetres) +
        (4.0 * WheelOuterEdgeOffsetMetres * WheelOuterEdgeOffsetMetres));

    public static CertificationFixture Load(string path)
    {
        using var stream = File.OpenRead(path);
        return JsonSerializer.Deserialize<CertificationFixture>(
                   stream,
                   new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
               ?? throw new InvalidDataException($"Fixture '{path}' is empty.");
    }

    public static IReadOnlyList<CertificationFixture> LoadDirectory(string directory) =>
        !Directory.Exists(directory)
            ? []
            : Directory.EnumerateFiles(directory, "*.json")
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .Select(Load)
                .ToArray();
}
