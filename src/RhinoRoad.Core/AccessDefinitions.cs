using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace RhinoRoad.Core;

public enum PathSourceKind
{
    ExistingCurve,
    Interactive
}

public enum RoadEdgeMethod
{
    Both,
    MinimumFootprint,
    FixedWidth,
    None
}

public enum FootprintMode
{
    None,
    EndsOnly,
    AtInterval
}

public enum ManoeuvreControlKind
{
    Aim,
    Finish,

    /// <summary>
    /// Turn at the wheel's lock through the amount of heading the point asks for, and stop there
    /// still turning. The tightest turn the vehicle can make, which aiming at a point cannot reach.
    /// </summary>
    Turn
}

/// <param name="FromStandstill">
/// Whether the vehicle is taken to stop before this leg and turn the wheel where it stands. A change
/// of direction is a standstill whether or not this is set, because the vehicle has to stop to make
/// one; setting it asks for a stop that the route would not otherwise have had.
/// </param>
/// <param name="SpeedKilometresPerHour">
/// The speed this leg is driven at, when slower than the driving mode's: a reverse or a shuffle at
/// walking pace rather than at the mode's 15 or 5 km/h. Null, which every road saved before this
/// existed reads as, means the mode's speed. Only the slew per metre changes -- a slower leg turns
/// the wheel in less distance -- and the lock stays the mode's. A speed above the mode's is driven
/// at the mode's (see <see cref="DrivingModeDefinition.LegSpeedKilometresPerHour"/>).
/// </param>
/// <param name="SealsLeg">
/// Whether the control after this one may rewind into this leg to ease its exit. An aimed leg's tail
/// is reshaped by the click that follows it; the last control of a planned reverse is not, because
/// the planner searched the approach as a whole and the docked arrival is what was accepted. Saved
/// on the control rather than kept by the pick loop, so a replay of the saved journey stops at the
/// same boundary the designer drove to -- without it, a reverse Finish after a planned reverse eased
/// back into the dock on replay and ended 15 to 42 m from where it was driven.
/// </param>
public sealed record ManoeuvreControl(
    Point3 PositionMetres,
    TravelDirection Direction,
    ManoeuvreControlKind Kind = ManoeuvreControlKind.Aim,
    double? ExitHeadingRadians = null,
    bool FromStandstill = false,
    double? SpeedKilometresPerHour = null,
    bool SealsLeg = false);

public sealed record ManoeuvreDefinition(
    int SchemaVersion,
    Point3 StartPositionMetres,
    double StartHeadingRadians,
    TravelDirection StartDirection,
    IReadOnlyList<ManoeuvreControl> Controls)
{
    /// <summary>
    /// The manoeuvre schema written today. Version 2 is the first written after reversing legs began
    /// to be planned differently -- a rigid reverse aimed with a direction now docks, a two-joint
    /// chain is planned by simulation, a missed semitrailer dock falls back to the same planner, and
    /// a reverse with no speed of its own runs at the vehicle's manoeuvring speed -- and the first
    /// with a per-leg speed and a sealed leg (<see cref="ManoeuvreControl.SealsLeg"/>). The controls
    /// of a version 1 manoeuvre read unchanged, but they can replay to a different route.
    /// </summary>
    public const int CurrentSchemaVersion = 2;

    public VehicleState StartState => new(
        StartPositionMetres,
        StartHeadingRadians,
        0.0,
        StartDirection,
        0.0);
}

/// <summary>The complete, portable intent required to repeat a vehicle-access analysis.</summary>
public sealed record VehicleAccessDefinition(
    int SchemaVersion,
    Guid StableSourceId,
    Guid SourceObjectId,
    PathSourceKind SourceKind,
    string VehicleId,
    string ModeId,
    TravelDirection ExistingCurveDirection,
    ManoeuvreDefinition? Manoeuvre,
    double ClearanceMetres,
    RoadEdgeMethod RoadEdgeMethod,
    double LeftWidthMetres,
    double RightWidthMetres,
    bool CheckMaximumGrade,
    double MaximumGradePercent,
    bool CheckObstacles,
    bool CheckAllowedArea,
    FootprintMode FootprintMode,
    double FootprintIntervalMetres,
    bool PreviewBeforeBaking,
    bool ReplaceExisting,
    IReadOnlyList<Guid> ObstacleObjectIds,
    IReadOnlyList<Guid> AllowedBoundaryObjectIds,
    IReadOnlyDictionary<Guid, string> ReferenceFingerprints,
    string SourceFingerprint,
    string AnalysisId,
    Guid? TerrainObjectId = null,
    bool BakeWheelEdgePaths = true,
    bool IncludeMirrorAllowance = false)
{
    /// <summary>
    /// The definition schema written today. Version 2 adds the terrain, the wheel-edge paths and the
    /// mirror allowance, and carries a version 2 manoeuvre; it was moved on with the manoeuvre so that
    /// a RhinoRoad that predates them refuses the road as newer than itself instead of quietly
    /// regenerating it without them.
    /// </summary>
    public const int CurrentSchemaVersion = 2;

    /// <summary>
    /// The schema the definition was read from, when that was older than
    /// <see cref="CurrentSchemaVersion"/> and it was upgraded on the way in; null for one read at the
    /// current schema or made in this session. Never written: a road saved again is saved at the
    /// current schema, and it is the re-derivation that the upgrade warns about
    /// (<see cref="UpgradeWarning"/>), not the reading.
    /// </summary>
    [JsonIgnore]
    public int? UpgradedFromSchema { get; init; }

    /// <summary>
    /// What to tell the designer about a road read from an older schema, or null when there is
    /// nothing to say.
    /// </summary>
    [JsonIgnore]
    public string? UpgradeWarning => UpgradedFromSchema is int from && from < 2
        ? "This road was saved by RhinoRoad 0.5 or earlier; reversing legs are now planned differently, "
          + "so its route may have changed — review it before relying on the result."
        : null;
}

public static class AccessDefinitionSerializer
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
        NumberHandling = JsonNumberHandling.Strict,
        Converters = { new JsonStringEnumConverter() }
    };

    public static string Serialize(VehicleAccessDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        Validate(definition);
        return JsonSerializer.Serialize(definition, Options);
    }

    public static VehicleAccessDefinition Deserialize(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) throw new InvalidDataException("The access definition is empty.");
        VehicleAccessDefinition definition;
        try
        {
            var root = JsonNode.Parse(json) as JsonObject
                ?? throw new InvalidDataException("The access definition is malformed.");
            SchemaMigration.UpgradeDefinition(root);
            // The upgraders record where the definition came from; it is lifted onto the record here
            // rather than deserialized, because it describes this reading and is never written back.
            var upgradedFrom = SchemaMigration.TakeUpgradedFromSchema(root);
            definition = root.Deserialize<VehicleAccessDefinition>(Options)
                ?? throw new InvalidDataException("The access definition is empty.");
            if (upgradedFrom is not null) definition = definition with { UpgradedFromSchema = upgradedFrom };
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("The access definition is malformed.", exception);
        }
        // JsonObject throws ArgumentException for a key that appears twice, which a user string
        // corrupted by hand or by another tool can contain. It is malformed data like any other, and
        // TryDeserialize's callers rely on it being reported as such rather than escaping.
        catch (ArgumentException exception)
        {
            throw new InvalidDataException("The access definition is malformed.", exception);
        }

        Validate(definition);
        return definition;
    }

    public static bool TryDeserialize(string? json, out VehicleAccessDefinition? definition, out string? error)
    {
        try
        {
            definition = Deserialize(json ?? string.Empty);
            error = null;
            return true;
        }
        catch (Exception exception) when (exception is InvalidDataException or NotSupportedException)
        {
            definition = null;
            error = exception.Message;
            return false;
        }
    }

    private static void Validate(VehicleAccessDefinition definition)
    {
        if (definition.SchemaVersion != VehicleAccessDefinition.CurrentSchemaVersion)
            throw new InvalidDataException($"Unsupported access-definition schema {definition.SchemaVersion}.");
        if (definition.StableSourceId == Guid.Empty) throw new InvalidDataException("A stable source id is required.");
        if (definition.SourceObjectId == Guid.Empty) throw new InvalidDataException("A source object id is required.");
        if (string.IsNullOrWhiteSpace(definition.VehicleId) || string.IsNullOrWhiteSpace(definition.ModeId))
            throw new InvalidDataException("Vehicle and mode are required.");
        if (definition.ObstacleObjectIds is null || definition.AllowedBoundaryObjectIds is null ||
            definition.ReferenceFingerprints is null)
            throw new InvalidDataException("Reference collections are required.");
        if (definition.ClearanceMetres < 0.0 || definition.LeftWidthMetres < 0.0 || definition.RightWidthMetres < 0.0)
            throw new InvalidDataException("Clearance and road widths cannot be negative.");
        if (definition.SourceKind == PathSourceKind.Interactive)
        {
            if (definition.Manoeuvre is null) throw new InvalidDataException("An interactive source requires a manoeuvre.");
            if (definition.Manoeuvre.SchemaVersion != ManoeuvreDefinition.CurrentSchemaVersion)
                throw new InvalidDataException($"Unsupported manoeuvre schema {definition.Manoeuvre.SchemaVersion}.");
            if (definition.Manoeuvre.Controls is null || definition.Manoeuvre.Controls.Count == 0)
                throw new InvalidDataException("An interactive manoeuvre requires at least one control.");
            if (definition.Manoeuvre.Controls.Any(control => control.SpeedKilometresPerHour is double speed
                    && (!double.IsFinite(speed) || speed <= 0.0)))
                throw new InvalidDataException("A control's speed must be a positive number of km/h.");
        }
    }
}
