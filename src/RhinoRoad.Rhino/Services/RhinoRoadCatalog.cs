using RhinoRoad.Core;

namespace RhinoRoad.Rhino.Services;

/// <summary>
/// The one vehicle catalog every command, dialog and conduit shares: the built-in presets plus every
/// *.json in the user vehicles folder. Read <see cref="Current"/> at the point of use rather than
/// caching it, so a reload (RRImportVehicle, RRVehicles) is seen everywhere.
/// </summary>
internal static class RhinoRoadCatalog
{
    private static readonly object Gate = new();
    private static UserCatalogLoad? _load;

    public static VehicleCatalog Current => Load().Catalog;

    /// <summary>Why any user vehicle or file was left out of <see cref="Current"/>.</summary>
    public static IReadOnlyList<string> Errors => Load().Errors;

    /// <summary>
    /// Folder of user vehicle files. Under the plugin's settings directory when the plugin is loaded,
    /// otherwise the equivalent %APPDATA% location.
    /// </summary>
    public static string UserFolder
    {
        get
        {
            var settings = RhinoRoadPlugin.Instance?.SettingsDirectory;
            if (!string.IsNullOrWhiteSpace(settings)) return Path.Combine(settings, "vehicles");
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "McNeel", "Rhinoceros", "8.0", "Plug-ins", "RhinoRoad", "vehicles");
        }
    }

    public static UserCatalogLoad Reload()
    {
        lock (Gate)
        {
            _load = VehicleCatalog.LoadWithUserDirectory(UserFolder);
            return _load;
        }
    }

    private static UserCatalogLoad Load()
    {
        lock (Gate) return _load ?? Reload();
    }

    /// <summary>
    /// Validates <paramref name="path"/> against the built-ins and the other files already in the user
    /// folder, copies it in when it passes (replacing a file of the same name), and reloads.
    /// </summary>
    public static bool TryImport(string path, out string message)
    {
        try
        {
            var name = Path.GetFileName(path);
            var folder = UserFolder;
            var sources = new List<KeyValuePair<string, string>>();
            if (Directory.Exists(folder))
            {
                foreach (var other in Directory.EnumerateFiles(folder, "*.json").OrderBy(file => file, StringComparer.OrdinalIgnoreCase))
                {
                    if (string.Equals(Path.GetFileName(other), name, StringComparison.OrdinalIgnoreCase)) continue;
                    sources.Add(new KeyValuePair<string, string>(Path.GetFileName(other), File.ReadAllText(other)));
                }
            }

            sources.Add(new KeyValuePair<string, string>(name, File.ReadAllText(path)));
            var check = VehicleCatalog.LoadWithUserVehicles(sources);
            var problems = check.Errors.Where(error => error.StartsWith(name + ":", StringComparison.OrdinalIgnoreCase)).ToArray();
            if (problems.Length > 0)
            {
                message = string.Join(Environment.NewLine, problems);
                return false;
            }

            Directory.CreateDirectory(folder);
            File.Copy(path, Path.Combine(folder, name), overwrite: true);
            Reload();
            message = $"Imported {name} into {folder}.";
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            message = $"Could not import {path}: {exception.Message}";
            return false;
        }
    }
}
