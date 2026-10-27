using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RhinoRoad.Core;

/// <summary>A catalog of built-ins plus user vehicles, and why any user vehicle or file was left out.</summary>
public sealed record UserCatalogLoad(VehicleCatalog Catalog, IReadOnlyList<string> Errors);

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

    /// <summary>
    /// The vehicle with <paramref name="id"/>, or false when there is none. A saved road can name a
    /// user vehicle whose file has since been removed, so code that opens a saved road asks this and
    /// reports <see cref="NotFoundMessage"/> instead of throwing.
    /// </summary>
    public bool TryGet(string? id, [NotNullWhen(true)] out VehicleDefinition? vehicle)
    {
        vehicle = null;
        return id is not null && _vehicles.TryGetValue(id, out vehicle);
    }

    /// <summary>What to tell the user when a saved road's vehicle is not in the catalogue.</summary>
    public static string NotFoundMessage(string? id) =>
        $"Vehicle {id} not found — import it again (RRImportVehicle) or change the road's vehicle.";

    public static VehicleCatalog LoadEmbedded()
    {
        var assembly = typeof(VehicleCatalog).Assembly;
        var resource = assembly.GetManifestResourceNames().Single(name => name.EndsWith("vehicles.json", StringComparison.OrdinalIgnoreCase));
        using var stream = assembly.GetManifestResourceStream(resource) ?? throw new InvalidDataException("Embedded vehicle catalog is missing.");
        return Load(stream);
    }

    /// <summary>
    /// Built-in presets plus the vehicles in <paramref name="userSources"/> (label, JSON text), each in the
    /// same schema as the embedded catalog. User vehicles are always <see cref="ValidationStatus.UserDefined"/>
    /// whatever the file says. A vehicle whose id collides with a built-in (or an earlier user vehicle) is
    /// rejected, not renamed; a malformed file or invalid vehicle is skipped. Nothing here throws on bad
    /// user input: the reasons are returned in <see cref="UserCatalogLoad.Errors"/>.
    /// </summary>
    public static UserCatalogLoad LoadWithUserVehicles(IEnumerable<KeyValuePair<string, string>> userSources)
    {
        var builtIn = LoadEmbedded();
        var accepted = new Dictionary<string, VehicleDefinition>(StringComparer.OrdinalIgnoreCase);
        foreach (var vehicle in builtIn.Vehicles) accepted[vehicle.Id] = vehicle;
        var errors = new List<string>();
        foreach (var (label, json) in userSources)
        {
            // One bad file must never break the catalog: every command reads it, and a load that
            // throws is retried, and throws again, on every one of them.
            try
            {
                AddUserFile(label, json, accepted, errors);
            }
            catch (Exception exception)
            {
                errors.Add($"{label}: not a valid vehicle file ({exception.Message})");
            }
        }

        return new UserCatalogLoad(new VehicleCatalog(accepted.Values), errors);
    }

    private static void AddUserFile(
        string label,
        string json,
        Dictionary<string, VehicleDefinition> accepted,
        List<string> errors)
    {
        CatalogDocument? document;
        try
        {
            document = JsonSerializer.Deserialize<CatalogDocument>(json, CreateOptions());
        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException or InvalidOperationException or ArgumentException)
        {
            errors.Add($"{label}: not a valid vehicle file ({exception.Message})");
            return;
        }

        if (document is null)
        {
            errors.Add($"{label}: the file is empty (null).");
            return;
        }

        if (document.Vehicles is null)
        {
            errors.Add($"{label}: \"vehicles\" is null; it must be an array of vehicles.");
            return;
        }

        if (document.Vehicles.Count == 0)
        {
            errors.Add($"{label}: contains no vehicles.");
            return;
        }

        for (var index = 0; index < document.Vehicles.Count; index++)
        {
            var dto = document.Vehicles[index];
            if (dto is null)
            {
                errors.Add($"{label}: vehicle {index + 1} is null and was skipped.");
                continue;
            }

            try
            {
                var vehicle = dto.ToDefinition() with { ValidationStatus = ValidationStatus.UserDefined };
                vehicle.Validate();
                if (accepted.TryGetValue(vehicle.Id, out var existing))
                {
                    errors.Add($"{label}: id '{vehicle.Id}' is already used by " +
                               (existing.ValidationStatus == ValidationStatus.UserDefined ? "another user vehicle" : "a built-in preset") +
                               "; choose a different id.");
                    continue;
                }

                accepted[vehicle.Id] = vehicle;
            }
            catch (Exception exception)
            {
                // Validation failures are InvalidDataException; anything else is a shape the DTO
                // checks did not anticipate, and still only this vehicle's problem.
                var name = string.IsNullOrWhiteSpace(dto.Id) ? $"{index + 1}" : $"'{dto.Id}'";
                errors.Add($"{label}: vehicle {name} skipped ({exception.Message})");
            }
        }
    }

    /// <summary>Loads built-ins plus every *.json in <paramref name="directory"/> (missing folder = none).</summary>
    public static UserCatalogLoad LoadWithUserDirectory(string? directory)
    {
        var sources = new List<KeyValuePair<string, string>>();
        var errors = new List<string>();
        string[] paths = [];
        if (!string.IsNullOrWhiteSpace(directory) && Directory.Exists(directory))
        {
            try
            {
                paths = Directory.EnumerateFiles(directory, "*.json").OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToArray();
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
            {
                errors.Add($"{directory}: the user vehicles folder could not be listed ({exception.Message})");
            }

            foreach (var path in paths)
            {
                try
                {
                    sources.Add(new KeyValuePair<string, string>(Path.GetFileName(path), File.ReadAllText(path)));
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    errors.Add($"{Path.GetFileName(path)}: could not be read ({exception.Message})");
                }
            }
        }

        var load = LoadWithUserVehicles(sources);
        return errors.Count == 0 ? load : new UserCatalogLoad(load.Catalog, [.. errors, .. load.Errors]);
    }

    private static JsonSerializerOptions CreateOptions() => new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        Converters = { new JsonStringEnumConverter() }
    };

    public static VehicleCatalog Load(Stream stream)
    {
        var options = CreateOptions();
        var document = JsonSerializer.Deserialize<CatalogDocument>(stream, options) ?? throw new InvalidDataException("Vehicle catalog is empty.");
        var vehicles = (document.Vehicles ?? throw new InvalidDataException("Vehicle catalog has no vehicles array."))
            .Select(dto => (dto ?? throw new InvalidDataException("Vehicle catalog contains a null vehicle.")).ToDefinition())
            .ToArray();
        return new VehicleCatalog(vehicles);
    }

    /// <summary>
    /// A file's list of [x, y] points, refusing what JSON allows but a polygon cannot be: a null list,
    /// a null point, or a point with fewer than two coordinates.
    /// </summary>
    private static Point2[] Points(List<double[]?>? points, string owner, string what)
    {
        if (points is null) throw new InvalidDataException($"{owner}: {what} is null; it must be a list of [x, y] points.");
        var result = new Point2[points.Count];
        for (var index = 0; index < points.Count; index++)
        {
            var point = points[index];
            if (point is null || point.Length < 2)
                throw new InvalidDataException($"{owner}: point {index + 1} of {what} must be [x, y].");
            result[index] = new Point2(point[0], point[1]);
        }

        return result;
    }

    /// <summary>
    /// An optional list (towed units, protrusions): null is read as none, but a null entry in it is
    /// an error, because it is a unit or protrusion the file meant to describe and did not.
    /// </summary>
    private static T[] Items<TDto, T>(List<TDto?>? items, string owner, string what, Func<TDto, T> convert)
        where TDto : class
    {
        if (items is null) return [];
        var result = new T[items.Count];
        for (var index = 0; index < items.Count; index++)
        {
            result[index] = convert(items[index] ?? throw new InvalidDataException($"{owner}: {what} {index + 1} is null."));
        }

        return result;
    }

    // Collections and their entries are nullable here because a file can say null, and
    // System.Text.Json honours it whatever the initialiser says; each is checked on conversion.
    private sealed class CatalogDocument
    {
        public List<VehicleDto?>? Vehicles { get; init; } = [];
    }

    private sealed class VehicleDto
    {
        public string? Id { get; init; } = string.Empty;
        public string? Name { get; init; } = string.Empty;
        public string? Version { get; init; } = string.Empty;
        public double WidthMetres { get; init; }
        public double WheelbaseMetres { get; init; }
        public double FrontOverhangMetres { get; init; }
        public double RearOverhangMetres { get; init; }
        public double AxleTrackMetres { get; init; }
        public double TyreWidthMetres { get; init; }
        public List<double[]?>? BodyOutline { get; init; } = [];
        public Dictionary<string, ModeDto?>? DrivingModes { get; init; } = new(StringComparer.OrdinalIgnoreCase);
        public List<TowedUnitDto?>? TowedUnits { get; init; } = [];
        public List<ProtrusionDto?>? Protrusions { get; init; } = [];
        public VehicleSource? Source { get; init; } = new(string.Empty, string.Empty, string.Empty, string.Empty, string.Empty);
        public ValidationStatus ValidationStatus { get; init; }
        public string? ValidationNotes { get; init; } = string.Empty;

        public VehicleDefinition ToDefinition()
        {
            var id = Id ?? string.Empty;
            if (DrivingModes is null) throw new InvalidDataException($"{id}: drivingModes is null; it must map a mode id to its values.");
            return new VehicleDefinition(
                id,
                Name ?? string.Empty,
                Version ?? string.Empty,
                WidthMetres,
                WheelbaseMetres,
                FrontOverhangMetres,
                RearOverhangMetres,
                AxleTrackMetres,
                TyreWidthMetres,
                Points(BodyOutline, id, "bodyOutline"),
                DrivingModes.ToDictionary(
                    pair => pair.Key,
                    pair => (pair.Value ?? throw new InvalidDataException($"{id}: driving mode '{pair.Key}' is null.")).ToDefinition(pair.Key),
                    StringComparer.OrdinalIgnoreCase),
                Source ?? new VehicleSource(string.Empty, string.Empty, string.Empty, string.Empty, string.Empty),
                ValidationStatus,
                ValidationNotes ?? string.Empty)
            {
                TowedUnits = Items(TowedUnits, id, "towed unit", unit => unit.ToDefinition(id)),
                Protrusions = Items(Protrusions, id, "protrusion", protrusion => protrusion.ToDefinition(id))
            };
        }
    }

    private sealed class TowedUnitDto
    {
        public string? Id { get; init; } = string.Empty;
        public string? Name { get; init; } = string.Empty;
        public double HitchOffsetMetres { get; init; }
        public double WheelbaseMetres { get; init; }
        public double WidthMetres { get; init; }
        public double AxleTrackMetres { get; init; }
        public double TyreWidthMetres { get; init; }
        public List<double[]?>? BodyOutline { get; init; } = [];
        public List<ProtrusionDto?>? Protrusions { get; init; } = [];

        /// <summary>
        /// Optional, because no source publishes one: absent, the unit takes the RhinoRoad default
        /// in <see cref="TowedUnitDefinition.DefaultMaximumArticulationDegrees"/>.
        /// </summary>
        public double? MaximumArticulationDegrees { get; init; }

        public TowedUnitDefinition ToDefinition(string vehicleId)
        {
            var owner = $"{vehicleId}/{Id}";
            // A drawbar dolly carries no outline, so an absent one is an empty one.
            return new TowedUnitDefinition(
                Id ?? string.Empty,
                Name ?? string.Empty,
                HitchOffsetMetres,
                WheelbaseMetres,
                WidthMetres,
                AxleTrackMetres,
                TyreWidthMetres,
                BodyOutline is null ? [] : Points(BodyOutline, owner, "bodyOutline"))
            {
                Protrusions = Items(Protrusions, owner, "protrusion", protrusion => protrusion.ToDefinition(owner)),
                MaximumArticulationDegrees = MaximumArticulationDegrees
                    ?? TowedUnitDefinition.DefaultMaximumArticulationDegrees
            };
        }
    }

    private sealed class ProtrusionDto
    {
        public string? Id { get; init; } = string.Empty;
        public string? Name { get; init; } = string.Empty;
        public List<double[]?>? Outline { get; init; } = [];
        public double? MinHeightMetres { get; init; }
        public double? MaxHeightMetres { get; init; }

        public ProtrusionDefinition ToDefinition(string owner) => new(
            Id ?? string.Empty,
            Name ?? string.Empty,
            Points(Outline, $"{owner}/{Id}", "outline"),
            MinHeightMetres,
            MaxHeightMetres);
    }

    private sealed class ModeDto
    {
        public string? Name { get; init; } = string.Empty;
        public double SpeedKilometresPerHour { get; init; }
        public double MaximumWheelAngleDegrees { get; init; }
        public double LockToLockSeconds { get; init; }
        public double DefaultClearanceMetres { get; init; }

        public DrivingModeDefinition ToDefinition(string id) => new(
            id,
            Name ?? string.Empty,
            SpeedKilometresPerHour,
            MaximumWheelAngleDegrees,
            LockToLockSeconds,
            DefaultClearanceMetres);
    }
}
