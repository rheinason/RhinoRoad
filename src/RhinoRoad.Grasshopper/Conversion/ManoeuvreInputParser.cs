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
    /// direction repeats the previous one (Forward to begin with), a missing kind is Aim, and a
    /// missing exit heading is none. Unrecognised entries are reported rather than guessed.
    /// </summary>
    /// <param name="exitHeadingsRadians">Per control; null entries mean no exit heading.</param>
    public static IReadOnlyList<ManoeuvreControl> BuildControls(
        IReadOnlyList<Point3> pointsMetres,
        IReadOnlyList<object?> directions,
        IReadOnlyList<object?> kinds,
        IReadOnlyList<double?> exitHeadingsRadians,
        ICollection<string> problems)
    {
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
            controls.Add(new ManoeuvreControl(pointsMetres[index], direction, kind, exit));
        }

        return controls;
    }

    /// <summary>Plan heading of a vector in radians, or null for a zero (or vertical) vector.</summary>
    public static double? HeadingOf(double x, double y) =>
        Math.Sqrt((x * x) + (y * y)) < 1e-9 ? null : Math.Atan2(y, x);
}
