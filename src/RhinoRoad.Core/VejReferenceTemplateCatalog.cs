using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace RhinoRoad.Core;

public sealed record VejReferenceTemplate(
    string Key,
    double AngleDegrees,
    double SourceWidthMetres,
    IReadOnlyList<IReadOnlyList<Point2>> DesignEnvelopeLines,
    IReadOnlyList<IReadOnlyList<Point2>> WheelTrackLines);

public sealed class VejReferenceTemplateCatalog
{
    private static readonly Regex PayloadExpression = new(
        "_TEMPLATE_B64\\s*=\\s*\"\"\"(?<payload>.*?)\"\"\"",
        RegexOptions.Singleline | RegexOptions.CultureInvariant);

    private readonly Dictionary<string, VejReferenceTemplate> _templates;

    private VejReferenceTemplateCatalog(IEnumerable<VejReferenceTemplate> templates) =>
        _templates = templates.ToDictionary(template => template.Key, StringComparer.OrdinalIgnoreCase);

    public IReadOnlyCollection<string> Keys => _templates.Keys.OrderBy(key => key).ToArray();

    public VejReferenceTemplate Get(int typeId, int angleGon)
    {
        var key = $"{typeId}:{angleGon}";
        return _templates.TryGetValue(key, out var template)
            ? template
            : throw new KeyNotFoundException($"Reference template '{key}' was not found.");
    }

    public static VejReferenceTemplateCatalog LoadFromLegacyPython(string pythonSource)
    {
        var match = PayloadExpression.Match(pythonSource);
        if (!match.Success) throw new InvalidDataException("The legacy template payload was not found.");
        var compressed = Convert.FromBase64String(match.Groups["payload"].Value);
        using var input = new MemoryStream(compressed);
        using var zlib = new ZLibStream(input, CompressionMode.Decompress);
        using var reader = new StreamReader(zlib, Encoding.UTF8);
        using var document = JsonDocument.Parse(reader.ReadToEnd());
        var templates = new List<VejReferenceTemplate>();
        foreach (var property in document.RootElement.EnumerateObject())
        {
            var record = property.Value;
            templates.Add(new VejReferenceTemplate(
                property.Name,
                record.GetProperty("angle_deg").GetDouble(),
                record.GetProperty("source_width").GetDouble(),
                ParseLines(record.GetProperty("main")),
                ParseLines(record.GetProperty("tracks"))));
        }
        return new VejReferenceTemplateCatalog(templates);
    }

    private static IReadOnlyList<IReadOnlyList<Point2>> ParseLines(JsonElement lines) => lines
        .EnumerateArray()
        .Select(line => (IReadOnlyList<Point2>)line.EnumerateArray()
            .Select(point => new Point2(point[0].GetDouble(), point[1].GetDouble()))
            .ToArray())
        .ToArray();
}
