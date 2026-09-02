using System.Text.Json;

namespace RhinoRoad.Core;

/// <summary>One official drawing: the mode it covers and the block the cases live in.</summary>
public sealed record CertificationDrawing(string ModeId, string Path, string BlockName);

/// <summary>
/// A vehicle in the certification matrix. <paramref name="Required"/> separates presets that must
/// certify before release from ones being trialled, so a new vehicle can be added and measured
/// without failing the build.
/// </summary>
public sealed record CertificationVehicle(
    string VehicleId,
    bool Required,
    IReadOnlyList<CertificationDrawing> Drawings);

/// <summary>
/// The certification matrix as data. Adding a vehicle is an edit to
/// <c>reference/certification/certification-plan.json</c> plus its preset — no code change, and the
/// fixture and report tests pick it up automatically.
/// </summary>
public sealed record CertificationPlan(
    IReadOnlyList<int> AnglesGon,
    IReadOnlyList<CertificationVehicle> Vehicles)
{
    public const string FileName = "certification-plan.json";

    public IReadOnlyList<string> RequiredVehicleIds => Vehicles
        .Where(vehicle => vehicle.Required)
        .Select(vehicle => vehicle.VehicleId)
        .ToArray();

    /// <summary>Total cases a complete run must report, across every vehicle, mode and angle.</summary>
    public int ExpectedCaseCount => Vehicles.Sum(vehicle => vehicle.Drawings.Count) * AnglesGon.Count;

    public CertificationVehicle? Find(string vehicleId) => Vehicles
        .FirstOrDefault(vehicle => vehicle.VehicleId.Equals(vehicleId, StringComparison.OrdinalIgnoreCase));

    public IReadOnlyList<string> ModeIdsFor(string vehicleId) => Find(vehicleId)?.Drawings
        .Select(drawing => drawing.ModeId)
        .ToArray() ?? [];

    public void Validate()
    {
        if (AnglesGon.Count == 0) throw new InvalidDataException("The plan lists no angles.");
        if (Vehicles.Count == 0) throw new InvalidDataException("The plan lists no vehicles.");
        foreach (var vehicle in Vehicles)
        {
            if (string.IsNullOrWhiteSpace(vehicle.VehicleId))
                throw new InvalidDataException("A plan vehicle is missing its id.");
            if (vehicle.Drawings.Count == 0)
                throw new InvalidDataException($"{vehicle.VehicleId}: no drawings listed.");
            var duplicate = vehicle.Drawings
                .GroupBy(drawing => drawing.ModeId, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault(group => group.Count() > 1);
            if (duplicate is not null)
                throw new InvalidDataException($"{vehicle.VehicleId}: mode '{duplicate.Key}' is listed twice.");
        }
    }

    public static CertificationPlan Load(string path)
    {
        using var stream = File.OpenRead(path);
        return Load(stream);
    }

    public static CertificationPlan Load(Stream stream)
    {
        var plan = JsonSerializer.Deserialize<CertificationPlan>(
                       stream,
                       new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                   ?? throw new InvalidDataException("The certification plan is empty.");
        plan.Validate();
        return plan;
    }
}
