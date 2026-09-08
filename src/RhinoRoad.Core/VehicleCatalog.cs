using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RhinoRoad.Core;

public sealed class VehicleCatalog
{
    private readonly Dictionary<string, VehicleDefinition> _vehicles;

    private VehicleCatalog(IEnumerable<VehicleDefinition> vehicles)
    {
        _vehicles = vehicles.ToDictionary(vehicle => vehicle.Id, StringComparer.OrdinalIgnoreCase);
        foreach (var vehicle in _vehicles.Values) vehicle.Validate();
    }

    public IReadOnlyCollection<VehicleDefinition> Vehicles => _vehicles.Values.OrderBy(vehicle => vehicle.Id).ToArray();

    public VehicleDefinition Get(string id) => _vehicles.TryGetValue(id, out var vehicle)
        ? vehicle
        : throw new KeyNotFoundException($"Vehicle preset '{id}' was not found.");

    public static VehicleCatalog LoadEmbedded()
    {
        var assembly = typeof(VehicleCatalog).Assembly;
        var resource = assembly.GetManifestResourceNames().Single(name => name.EndsWith("vehicles.json", StringComparison.OrdinalIgnoreCase));
        using var stream = assembly.GetManifestResourceStream(resource) ?? throw new InvalidDataException("Embedded vehicle catalog is missing.");
        return Load(stream);
    }

    public static VehicleCatalog Load(Stream stream)
    {
        var options = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            Converters = { new JsonStringEnumConverter() }
        };
        var document = JsonSerializer.Deserialize<CatalogDocument>(stream, options) ?? throw new InvalidDataException("Vehicle catalog is empty.");
        var vehicles = document.Vehicles.Select(dto => dto.ToDefinition()).ToArray();
        return new VehicleCatalog(vehicles);
    }

    private sealed class CatalogDocument
    {
        public List<VehicleDto> Vehicles { get; init; } = [];
    }

    private sealed class VehicleDto
    {
        public string Id { get; init; } = string.Empty;
        public string Name { get; init; } = string.Empty;
        public string Version { get; init; } = string.Empty;
        public double WidthMetres { get; init; }
        public double WheelbaseMetres { get; init; }
        public double FrontOverhangMetres { get; init; }
        public double RearOverhangMetres { get; init; }
        public double AxleTrackMetres { get; init; }
        public double TyreWidthMetres { get; init; }
        public List<double[]> BodyOutline { get; init; } = [];
        public Dictionary<string, ModeDto> DrivingModes { get; init; } = new(StringComparer.OrdinalIgnoreCase);
        public List<TowedUnitDto> TowedUnits { get; init; } = [];
        public VehicleSource Source { get; init; } = new(string.Empty, string.Empty, string.Empty, string.Empty, string.Empty);
        public ValidationStatus ValidationStatus { get; init; }
        public string ValidationNotes { get; init; } = string.Empty;

        public VehicleDefinition ToDefinition() => new(
            Id,
            Name,
            Version,
            WidthMetres,
            WheelbaseMetres,
            FrontOverhangMetres,
            RearOverhangMetres,
            AxleTrackMetres,
            TyreWidthMetres,
            BodyOutline.Select(point => new Point2(point[0], point[1])).ToArray(),
            DrivingModes.ToDictionary(pair => pair.Key, pair => pair.Value.ToDefinition(pair.Key), StringComparer.OrdinalIgnoreCase),
            Source,
            ValidationStatus,
            ValidationNotes)
        {
            TowedUnits = TowedUnits.Select(unit => unit.ToDefinition()).ToArray()
        };
    }

    private sealed class TowedUnitDto
    {
        public string Id { get; init; } = string.Empty;
        public string Name { get; init; } = string.Empty;
        public double HitchOffsetMetres { get; init; }
        public double WheelbaseMetres { get; init; }
        public double WidthMetres { get; init; }
        public double AxleTrackMetres { get; init; }
        public double TyreWidthMetres { get; init; }
        public List<double[]> BodyOutline { get; init; } = [];

        public TowedUnitDefinition ToDefinition() => new(
            Id,
            Name,
            HitchOffsetMetres,
            WheelbaseMetres,
            WidthMetres,
            AxleTrackMetres,
            TyreWidthMetres,
            BodyOutline.Select(point => new Point2(point[0], point[1])).ToArray());
    }

    private sealed class ModeDto
    {
        public string Name { get; init; } = string.Empty;
        public double SpeedKilometresPerHour { get; init; }
        public double MaximumWheelAngleDegrees { get; init; }
        public double LockToLockSeconds { get; init; }
        public double DefaultClearanceMetres { get; init; }

        public DrivingModeDefinition ToDefinition(string id) => new(
            id,
            Name,
            SpeedKilometresPerHour,
            MaximumWheelAngleDegrees,
            LockToLockSeconds,
            DefaultClearanceMetres);
    }
}
