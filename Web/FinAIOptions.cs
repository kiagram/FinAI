namespace QuantConnect.FinAI.Web;

/// <summary>
/// Everything the service needs to locate LEAN on disk and bound its own
/// resource use. Bound from the "FinAI" configuration section, so every value
/// is also settable as a FinAI__&lt;Name&gt; environment variable in Docker.
/// </summary>
public sealed class FinAIOptions
{
    /// <summary>
    /// Every path below is relative to the repository root unless given as an
    /// absolute path, and is made absolute by <see cref="Resolve"/> at startup.
    /// Anchoring on the repo rather than the working directory keeps `dotnet run`
    /// from the repo root, a run from Web/bin/Release, and the container all
    /// pointing at the same files.
    /// </summary>

    /// <summary>Directory holding QuantConnect.Lean.Launcher.dll and its dependencies.</summary>
    public string LauncherDirectory { get; set; } = "Launcher/bin/Release";

    /// <summary>LEAN's Data/ directory. Read-only as far as this service is concerned.</summary>
    public string DataFolder { get; set; } = "Data";

    /// <summary>Launcher config used as the base layer; per-job overrides are merged on top.</summary>
    public string BaseConfigPath { get; set; } = "Launcher/config.json";

    /// <summary>Root under which each job gets its own directory.</summary>
    public string ResultsRoot { get; set; } = "Web/results";

    /// <summary>Backtests running at once. LEAN is single-threaded per run but memory-hungry.</summary>
    public int MaxConcurrency { get; set; } = 2;

    /// <summary>Jobs allowed to sit in the queue before new submissions are rejected.</summary>
    public int MaxQueueDepth { get; set; } = 32;

    /// <summary>A run exceeding this is killed and marked failed.</summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>Log lines kept in memory per job for the live tail. The full log stays on disk.</summary>
    public int LogTailLines { get; set; } = 400;

    /// <summary>
    /// When non-empty, every /api call must present this value as a Bearer token
    /// or an X-FinAI-Token header. Leave empty only for local development —
    /// this endpoint spends real CPU on every request.
    /// </summary>
    public string AccessToken { get; set; } = "";

    /// <summary>Absolute path to the launcher assembly.</summary>
    public string LauncherDll => Path.Combine(LauncherDirectory, "QuantConnect.Lean.Launcher.dll");

    /// <summary>Rewrites every relative path as an absolute one under <paramref name="repoRoot"/>.</summary>
    public void Resolve(string repoRoot)
    {
        LauncherDirectory = Absolute(repoRoot, LauncherDirectory);
        DataFolder = Absolute(repoRoot, DataFolder);
        BaseConfigPath = Absolute(repoRoot, BaseConfigPath);
        ResultsRoot = Absolute(repoRoot, ResultsRoot);
    }

    private static string Absolute(string root, string path) =>
        Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(root, path));
}
