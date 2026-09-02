using Rhino.DocObjects;
using RhinoRoad.Core;

namespace RhinoRoad.Rhino.Services;

/// <summary>
/// Remembers which way a baked leg is driven.
/// </summary>
/// <remarks>
/// A reversing leg looks exactly like a forward one as geometry — the difference is only in how the
/// vehicle travels it, and nothing in a curve can say that. Without this, re-running a three-point
/// turn would drive every leg forwards and produce a different manoeuvre from the one that was
/// drawn, which is the sort of silent change that makes a tool untrustworthy.
/// </remarks>
internal static class IntentLegStore
{
    public const string DirectionKey = "RhinoRoad.TravelDirection";

    public static void Stamp(ObjectAttributes attributes, TravelDirection direction) =>
        attributes.SetUserString(DirectionKey, direction.ToString());

    /// <summary>The stored direction, or <paramref name="fallback"/> for a curve RhinoRoad did not bake.</summary>
    public static TravelDirection Read(RhinoObject rhinoObject, TravelDirection fallback) =>
        Enum.TryParse<TravelDirection>(rhinoObject.Attributes.GetUserString(DirectionKey), out var stored)
            ? stored
            : fallback;
}
