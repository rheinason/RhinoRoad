using RhinoRoad.Core;

namespace RhinoRoad.GrasshopperPlugin.Conversion;

/// <summary>
/// Turns the loosely typed values a Grasshopper wire can carry into Core manoeuvre controls. Pure,
/// so the forgiving parts (a Panel of "R", a Boolean toggle, a short list) live away from Rhino.
/// </summary>
public static class ManoeuvreInputParser
{
    /// <summary>
    /// Reads a travel direction from a Boolean (true is Forward), a number (negative is Reverse) or
    /// text (Forward/F/Reverse/R/Back, case-insensitive). Returns null for anything else.
    /// </summary>
    public static TravelDirection? ParseDirection(object? value) => value switch
    {
        bool forward => forward ? TravelDirection.Forward : TravelDirection.Reverse,
        int number => number < 0 ? TravelDirection.Reverse : TravelDirection.Forward,
        double number => number < 0.0 ? TravelDirection.Reverse : TravelDirection.Forward,
        string text => text.Trim().ToUpperInvariant() switch
        {
            "FORWARD" or "F" or "FWD" or "TRUE" or "1" => TravelDirection.Forward,
            "REVERSE" or "R" or "REV" or "BACK" or "BACKWARD" or "FALSE" or "-1" => TravelDirection.Reverse,
            _ => null
        },
        _ => null
    };

    /// <summary>Reads Aim/Turn/Finish from text (first letter is enough). Null if unrecognised.</summary>
    public static ManoeuvreControlKind? ParseKind(object? value)
    {
        if (value is not string text || text.Trim().Length == 0) return null;
        return char.ToUpperInvariant(text.Trim()[0]) switch
        {
            'A' => ManoeuvreControlKind.Aim,
            'T' => ManoeuvreControlKind.Turn,
            'F' => ManoeuvreControlKind.Finish,
            _ => null
        };
    }

    /// <summary>
    /// Builds the controls. Optional lists may be empty or shorter than the points: a missing
    /// direction repeats the previous one (Forward to begin with), a missing kind is Aim, a missing
    /// exit heading is none, a missing speed is the mode's, and a missing stop is none. Unrecognised
    /// entries are reported rather than guessed.
    /// </summary>
    /// <param name="exitHeadingsRadians">Per control; null entries mean no exit heading.</param>
    /// <param name="speedsKilometresPerHour">
    /// Per control, in km/h. Zero or a null entry is the mode's speed, as a zero exit vector is no
    /// exit heading; a negative or non-finite entry is reported and the mode's speed used.
    /// </param>
    /// <param name="stops">
    /// Per control: whether the vehicle stops before that leg and turns the wheel where it stands
    /// (<see cref="ManoeuvreControl.FromStandstill"/>). A Boolean, or text/number read as one; a null
    /// or missing entry is no stop. Unlike a direction, a stop does not carry on to later controls.
    /// </param>
    public static IReadOnlyList<ManoeuvreControl> BuildControls(
        IReadOnlyList<Point3> pointsMetres,
        IReadOnlyList<object?> directions,
        IReadOnlyList<object?> kinds,
        IReadOnlyList<double?> exitHeadingsRadians,
        ICollection<string> problems,
        IReadOnlyList<double?>? speedsKilometresPerHour = null,
        IReadOnlyList<object?>? stops = null)
    {
        speedsKilometresPerHour ??= [];
        stops ??= [];
        var controls = new List<ManoeuvreControl>(pointsMetres.Count);
        var direction = TravelDirection.Forward;
        for (var index = 0; index < pointsMetres.Count; index++)
        {
            if (index < directions.Count)
            {
                var parsed = ParseDirection(directions[index]);
                if (parsed.HasValue) direction = parsed.Value;
                else problems.Add($"Direction {index} ('{directions[index]}') is not Forward/Reverse or a Boolean; the previous direction is kept.");
            }

            var kind = ManoeuvreControlKind.Aim;
            if (index < kinds.Count)
            {
                var parsed = ParseKind(kinds[index]);
                if (parsed.HasValue) kind = parsed.Value;
                else problems.Add($"Kind {index} ('{kinds[index]}') is not Aim/Turn/Finish; Aim is used.");
            }

            var exit = index < exitHeadingsRadians.Count ? exitHeadingsRadians[index] : null;
            double? speed = null;
            if (index < speedsKilometresPerHour.Count && speedsKilometresPerHour[index] is double given && given != 0.0)
            {
                if (double.IsFinite(given) && given > 0.0) speed = given;
                else problems.Add($"Speed {index} ({given}) is not a positive number of km/h; the mode's speed is used.");
            }

            var standstill = false;
            if (index < stops.Count && stops[index] is not null)
            {
                var parsed = ParseStop(stops[index]);
                if (parsed.HasValue) standstill = parsed.Value;
                else problems.Add($"Stop {index} ('{stops[index]}') is not a Boolean; no stop is made.");
            }

            controls.Add(new ManoeuvreControl(pointsMetres[index], direction, kind, exit,
                FromStandstill: standstill, SpeedKilometresPerHour: speed));
        }

        return controls;
    }

    /// <summary>
    /// Reads a stop from a Boolean, a number (non-zero is a stop) or text (True/Yes/Stop/1 or
    /// False/No/0, case-insensitive). Returns null for anything else.
    /// </summary>
    public static bool? ParseStop(object? value) => value switch
    {
        bool stop => stop,
        int number => number != 0,
        double number => number != 0.0,
        string text => text.Trim().ToUpperInvariant() switch
        {
            "TRUE" or "YES" or "Y" or "STOP" or "1" => true,
            "FALSE" or "NO" or "N" or "0" or "" => false,
            _ => null
        },
        _ => null
    };

    /// <summary>Plan heading of a vector in radians, or null for a zero (or vertical) vector.</summary>
    public static double? HeadingOf(double x, double y) =>
        Math.Sqrt((x * x) + (y * y)) < 1e-9 ? null : Math.Atan2(y, x);
}
