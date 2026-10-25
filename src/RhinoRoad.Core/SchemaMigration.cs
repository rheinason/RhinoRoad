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
    /// <summary>The upgraders for the saved definition, keyed by the version each upgrades from. None yet.</summary>
    internal static IReadOnlyDictionary<int, Action<JsonObject>> DefinitionUpgraders { get; } =
        new Dictionary<int, Action<JsonObject>>();

    /// <summary>The upgraders for the nested manoeuvre, keyed by the version each upgrades from. None yet.</summary>
    internal static IReadOnlyDictionary<int, Action<JsonObject>> ManoeuvreUpgraders { get; } =
        new Dictionary<int, Action<JsonObject>>();

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
