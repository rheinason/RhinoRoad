using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RhinoRoad.Core;

/// <summary>
/// Reads and writes a <see cref="Manoeuvre"/> as the string stored on the baked control curve.
/// </summary>
/// <remarks>
/// Round-tripping has to be exact, not close: the stored waypoints are replayed to rebuild the
/// route, so a coordinate that loses digits on the way out moves the path on the way back in.
/// Everything numeric is written round-trippable ("R") and parsed invariantly, because a document
/// saved on a machine with a comma decimal separator has to open on one without.
/// </remarks>
public static class ManoeuvreSerializer
{
    private static readonly JsonSerializerOptions Options = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static string ToJson(Manoeuvre manoeuvre)
    {
        var payload = new ManoeuvrePayload
        {
            Vehicle = manoeuvre.VehicleId,
            Mode = manoeuvre.ModeId,
            Start =
            [
                Number(manoeuvre.StartMetres.X),
                Number(manoeuvre.StartMetres.Y),
                Number(manoeuvre.StartMetres.Z)
            ],
            Heading = Number(manoeuvre.StartHeadingRadians),
            Waypoints = manoeuvre.Waypoints
                .Select(waypoint => new[]
                {
                    Number(waypoint.TargetMetres.X),
                    Number(waypoint.TargetMetres.Y),
                    waypoint.Direction == TravelDirection.Reverse ? "R" : "F"
                })
                .ToArray()
        };

        return JsonSerializer.Serialize(payload, Options);
    }

    /// <summary>Parses a stored manoeuvre, or returns null when the text is absent or unusable.</summary>
    public static Manoeuvre? FromJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;

        ManoeuvrePayload? payload;
        try
        {
            payload = JsonSerializer.Deserialize<ManoeuvrePayload>(json, Options);
        }
        catch (JsonException)
        {
            return null;
        }

        if (payload?.Vehicle is null || payload.Mode is null) return null;
        if (payload.Start is not { Length: 3 } || payload.Waypoints is null) return null;
        if (!TryNumber(payload.Start[0], out var x) ||
            !TryNumber(payload.Start[1], out var y) ||
            !TryNumber(payload.Start[2], out var z) ||
            !TryNumber(payload.Heading, out var heading))
        {
            return null;
        }

        var waypoints = new List<RouteWaypoint>(payload.Waypoints.Length);
        foreach (var entry in payload.Waypoints)
        {
            if (entry is not { Length: 3 }) return null;
            if (!TryNumber(entry[0], out var targetX) || !TryNumber(entry[1], out var targetY)) return null;
            waypoints.Add(new RouteWaypoint(
                new Point2(targetX, targetY),
                entry[2] == "R" ? TravelDirection.Reverse : TravelDirection.Forward));
        }

        return new Manoeuvre(payload.Vehicle, payload.Mode, new Point3(x, y, z), heading, waypoints);
    }

    private static string Number(double value) => value.ToString("R", CultureInfo.InvariantCulture);

    private static bool TryNumber(string? text, out double value) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value);

    private sealed class ManoeuvrePayload
    {
        [JsonPropertyName("v")] public string? Vehicle { get; set; }
        [JsonPropertyName("m")] public string? Mode { get; set; }
        [JsonPropertyName("s")] public string[]? Start { get; set; }
        [JsonPropertyName("h")] public string? Heading { get; set; }
        [JsonPropertyName("w")] public string[][]? Waypoints { get; set; }
    }
}
