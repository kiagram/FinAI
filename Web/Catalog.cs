using System.Text.Json;
using System.Text.Json.Serialization;

namespace QuantConnect.FinAI.Web;

/// <summary>
/// A parameter an algorithm reads through QCAlgorithm.get_parameter.
/// </summary>
public sealed class AlgorithmParameter
{
    public string Name { get; set; } = "";
    public string Label { get; set; } = "";
    public double Default { get; set; }
    public double Min { get; set; }
    public double Max { get; set; }
    public double Step { get; set; } = 1;
}

/// <summary>
/// One runnable algorithm. The catalog is a fixed allow-list shipped with the
/// image: the API only ever runs code that is already in the repository, and
/// callers choose an entry by id rather than supplying a path. Accepting an
/// arbitrary algorithm-location from the network would be remote code
/// execution, so <see cref="Location"/> is never taken from a request.
/// </summary>
public sealed class CatalogEntry
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public string Language { get; set; } = "Python";
    public string TypeName { get; set; } = "";
    public string Location { get; set; } = "";
    public List<AlgorithmParameter> Parameters { get; set; } = new();
}

public sealed class AlgorithmCatalog
{
    private readonly Dictionary<string, CatalogEntry> _byId;

    public AlgorithmCatalog(IEnumerable<CatalogEntry> entries)
    {
        _byId = entries.ToDictionary(e => e.Id, StringComparer.OrdinalIgnoreCase);
    }

    public IReadOnlyCollection<CatalogEntry> Entries => _byId.Values;

    public CatalogEntry? Find(string id) =>
        id is not null && _byId.TryGetValue(id, out var entry) ? entry : null;

    /// <summary>
    /// Reads catalog.json and resolves each Location against the repository
    /// root, dropping entries whose source file is missing so a trimmed image
    /// cannot offer an algorithm it can't run.
    /// </summary>
    public static AlgorithmCatalog Load(string path, string repoRoot, ILogger logger)
    {
        var json = File.ReadAllText(path);
        var entries = JsonSerializer.Deserialize<List<CatalogEntry>>(json, JsonOptions.Default)
                      ?? throw new InvalidOperationException($"{path} did not contain a catalog array.");

        var resolved = new List<CatalogEntry>();
        foreach (var entry in entries)
        {
            entry.Location = Path.GetFullPath(Path.Combine(repoRoot, entry.Location));
            if (!File.Exists(entry.Location))
            {
                logger.LogWarning("Catalog entry {Id} dropped: {Location} does not exist.", entry.Id, entry.Location);
                continue;
            }
            resolved.Add(entry);
        }

        if (resolved.Count == 0)
        {
            throw new InvalidOperationException($"No catalog entry in {path} resolved to a file under {repoRoot}.");
        }

        logger.LogInformation("Catalog loaded with {Count} algorithm(s).", resolved.Count);
        return new AlgorithmCatalog(resolved);
    }
}

public static class JsonOptions
{
    public static readonly JsonSerializerOptions Default = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() }
    };
}
