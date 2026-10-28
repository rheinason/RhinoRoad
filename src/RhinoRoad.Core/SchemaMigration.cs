using System.Text.Json.Nodes;

namespace RhinoRoad.Core;

/// <summary>
/// Brings a saved JSON object up to a current schema one version at a time.
/// </summary>
/// <remarks>
/// A saved road outlives the plugin that wrote it, so a schema change must not make older documents
/// unreadable. Each upgrader rewrites a document from version N to N+1 in place; registering one
/// per change keeps every old document reachable without any upgrader knowing about the others.
/// Properties added since a document was written need no upgrader when their record parameter has
/// a default, because the deserializer supplies it.
/// </remarks>
public static class SchemaMigration
{
    /// <summary>
    /// Where an upgrader records the schema a definition was first read at, so that the reader can
    /// say a road was upgraded. Removed again before the definition is deserialized.
    /// </summary>
    internal const string UpgradedFromSchemaProperty = "upgradedFromSchema";

    /// <summary>The upgraders for the saved definition, keyed by the version each upgrades from.</summary>
    internal static IReadOnlyDictionary<int, Action<JsonObject>> DefinitionUpgraders { get; } =
        new Dictionary<int, Action<JsonObject>>
        {
            // Version 2 only adds properties with defaults (the terrain, the wheel-edge paths, the
            // mirror allowance), so nothing is rewritten. What changed is how the saved manoeuvre's
            // reverses replay, which is recorded so the road can say its route may have changed.
            [1] = RecordUpgradeFrom(1)
        };

    /// <summary>The upgraders for the nested manoeuvre, keyed by the version each upgrades from.</summary>
    internal static IReadOnlyDictionary<int, Action<JsonObject>> ManoeuvreUpgraders { get; } =
        new Dictionary<int, Action<JsonObject>>
        {
            // Version 2 adds the per-leg speed and the sealed leg, both defaulted -- a version 1
            // control is a leg at the mode's speed that may be rewound into, as it always was. Its
            // reverses are planned the new way; the definition's upgrader records that.
            [1] = _ => { }
        };

    /// <summary>
    /// Takes the schema the upgraders recorded off <paramref name="root"/>, or null when it was read
    /// at the current schema.
    /// </summary>
    public static int? TakeUpgradedFromSchema(JsonObject root)
    {
        ArgumentNullException.ThrowIfNull(root);
        if (!root.TryGetPropertyValue(UpgradedFromSchemaProperty, out var recorded)) return null;
        root.Remove(UpgradedFromSchemaProperty);
        return recorded is JsonValue value && value.TryGetValue<int>(out var version) ? version : null;
    }

    /// <summary>
    /// An upgrader that records <paramref name="version"/> as the schema the definition was first
    /// read at, unless an earlier upgrader in the chain already recorded an older one.
    /// </summary>
    private static Action<JsonObject> RecordUpgradeFrom(int version) => node =>
    {
        if (!node.ContainsKey(UpgradedFromSchemaProperty)) node[UpgradedFromSchemaProperty] = version;
    };

    public static void UpgradeDefinition(JsonObject root) =>
        UpgradeDefinition(root, VehicleAccessDefinition.CurrentSchemaVersion, ManoeuvreDefinition.CurrentSchemaVersion,
            DefinitionUpgraders, ManoeuvreUpgraders);

    /// <summary>Upgrades the definition and its manoeuvre to the given versions using the given upgraders.</summary>
    public static void UpgradeDefinition(
        JsonObject root,
        int currentDefinitionVersion,
        int currentManoeuvreVersion,
        IReadOnlyDictionary<int, Action<JsonObject>> definitionUpgraders,
        IReadOnlyDictionary<int, Action<JsonObject>> manoeuvreUpgraders)
    {
        Upgrade(root, currentDefinitionVersion, definitionUpgraders, "access definition");
        if (root["manoeuvre"] is JsonObject manoeuvre)
            Upgrade(manoeuvre, currentManoeuvreVersion, manoeuvreUpgraders, "manoeuvre");
    }

    /// <summary>
    /// Applies upgraders from the object's own <c>schemaVersion</c> up to <paramref name="current"/>,
    /// leaving the version field at <paramref name="current"/>.
    /// </summary>
    public static void Upgrade(JsonObject node, int current, IReadOnlyDictionary<int, Action<JsonObject>> upgraders, string what)
    {
        if (!node.TryGetPropertyValue("schemaVersion", out var versionNode) || versionNode is not JsonValue value ||
            !value.TryGetValue<int>(out var version))
            throw new InvalidDataException($"The {what} has no schema version.");

        if (version > current)
            throw new InvalidDataException(
                $"The {what} uses schema {version}, saved by a newer RhinoRoad; update the plugin.");
        if (version < 1)
            throw new InvalidDataException($"Unsupported {what} schema {version}.");

        while (version < current)
        {
            if (!upgraders.TryGetValue(version, out var upgrade))
                throw new InvalidDataException($"Unsupported {what} schema {version}: no upgrade to {version + 1} is available.");
            upgrade(node);
            version++;
            node["schemaVersion"] = version;
        }
    }
}
