using System.Text.Json;
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

public sealed record ManoeuvreControl(
    Point3 PositionMetres,
    TravelDirection Direction,
    ManoeuvreControlKind Kind = ManoeuvreControlKind.Aim,
    double? ExitHeadingRadians = null);

public sealed record ManoeuvreDefinition(
    int SchemaVersion,
    Point3 StartPositionMetres,
    double StartHeadingRadians,
    TravelDirection StartDirection,
    IReadOnlyList<ManoeuvreControl> Controls)
{
    public const int CurrentSchemaVersion = 1;

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
    string AnalysisId)
{
    public const int CurrentSchemaVersion = 1;
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
            definition = JsonSerializer.Deserialize<VehicleAccessDefinition>(json, Options)
                ?? throw new InvalidDataException("The access definition is empty.");
        }
        catch (JsonException exception)
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
        }
    }
}
