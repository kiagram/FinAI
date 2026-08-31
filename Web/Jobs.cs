using System.Collections.Concurrent;
using System.Text.Json;

namespace QuantConnect.FinAI.Web;

public enum JobStatus { Queued, Running, Succeeded, Failed }

/// <summary>A single equity-curve sample, seconds since the Unix epoch.</summary>
public sealed record EquityPoint(long Time, double Value);

public sealed class BacktestJob
{
    public string Id { get; init; } = "";
    public string AlgorithmId { get; init; } = "";
    public string AlgorithmName { get; init; } = "";
    public Dictionary<string, string> Parameters { get; init; } = new();

    public JobStatus Status { get; set; } = JobStatus.Queued;
    public DateTimeOffset CreatedUtc { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? StartedUtc { get; set; }
    public DateTimeOffset? FinishedUtc { get; set; }

    /// <summary>Populated on failure only; surfaced verbatim to the client.</summary>
    public string? Error { get; set; }

    /// <summary>LEAN's own exit code, recorded for diagnostics but not used to decide success.</summary>
    public int? ExitCode { get; set; }

    public Dictionary<string, string>? Statistics { get; set; }
    public List<EquityPoint>? Equity { get; set; }
    public int? OrderCount { get; set; }

    public double? DurationSeconds =>
        StartedUtc is { } s && FinishedUtc is { } f ? (f - s).TotalSeconds : null;
}

/// <summary>
/// Job index. Kept in memory for reads and mirrored to
/// &lt;ResultsRoot&gt;/&lt;id&gt;/job.json so a restart does not lose completed runs.
/// </summary>
public sealed class JobStore
{
    private readonly ConcurrentDictionary<string, BacktestJob> _jobs = new();
    private readonly ConcurrentDictionary<string, List<string>> _logs = new();
    private readonly FinAIOptions _options;
    private readonly ILogger<JobStore> _logger;

    public JobStore(FinAIOptions options, ILogger<JobStore> logger)
    {
        _options = options;
        _logger = logger;
        Rehydrate();
    }

    public string JobDirectory(string id) => Path.Combine(_options.ResultsRoot, id);

    public void Add(BacktestJob job)
    {
        _jobs[job.Id] = job;
        _logs[job.Id] = new List<string>();
        Persist(job);
    }

    public BacktestJob? Find(string id) => _jobs.TryGetValue(id, out var job) ? job : null;

    public IEnumerable<BacktestJob> Recent(int limit) =>
        _jobs.Values.OrderByDescending(j => j.CreatedUtc).Take(limit);

    public void Update(BacktestJob job) => Persist(job);

    public void AppendLog(string id, string line)
    {
        if (!_logs.TryGetValue(id, out var lines)) return;
        lock (lines)
        {
            lines.Add(line);
            // Bound memory: the complete log is on disk in the job directory.
            var overflow = lines.Count - _options.LogTailLines;
            if (overflow > 0) lines.RemoveRange(0, overflow);
        }
    }

    public IReadOnlyList<string> LogTail(string id)
    {
        if (!_logs.TryGetValue(id, out var lines)) return Array.Empty<string>();
        lock (lines) return lines.ToArray();
    }

    private void Persist(BacktestJob job)
    {
        try
        {
            var dir = JobDirectory(job.Id);
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "job.json"), JsonSerializer.Serialize(job, JsonOptions.Default));
        }
        catch (Exception ex)
        {
            // Losing the on-disk mirror degrades restart recovery but must not fail a run.
            _logger.LogWarning(ex, "Could not persist job {Id}.", job.Id);
        }
    }

    /// <summary>
    /// Reloads jobs written by a previous process. Anything left mid-flight is
    /// marked failed: the child process died with the service that owned it.
    /// </summary>
    private void Rehydrate()
    {
        var root = _options.ResultsRoot;
        if (!Directory.Exists(root)) return;

        foreach (var file in Directory.EnumerateFiles(root, "job.json", SearchOption.AllDirectories))
        {
            try
            {
                var job = JsonSerializer.Deserialize<BacktestJob>(File.ReadAllText(file), JsonOptions.Default);
                if (job is null || string.IsNullOrEmpty(job.Id)) continue;

                if (job.Status is JobStatus.Queued or JobStatus.Running)
                {
                    job.Status = JobStatus.Failed;
                    job.Error = "Interrupted by a service restart.";
                    job.FinishedUtc = DateTimeOffset.UtcNow;
                }

                _jobs[job.Id] = job;
                _logs[job.Id] = new List<string>();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Skipping unreadable job file {File}.", file);
            }
        }

        if (!_jobs.IsEmpty) _logger.LogInformation("Rehydrated {Count} job(s) from {Root}.", _jobs.Count, root);
    }
}
