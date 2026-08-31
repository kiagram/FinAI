using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace QuantConnect.FinAI.Web;

/// <summary>
/// Runs one backtest as a child LEAN process and reads the results back off disk.
/// </summary>
public sealed class BacktestRunner
{
    private readonly FinAIOptions _options;
    private readonly JobStore _store;
    private readonly ILogger<BacktestRunner> _logger;

    public BacktestRunner(FinAIOptions options, JobStore store, ILogger<BacktestRunner> logger)
    {
        _options = options;
        _store = store;
        _logger = logger;
    }

    public async Task RunAsync(BacktestJob job, CatalogEntry algorithm, CancellationToken cancellationToken)
    {
        var jobDir = _store.JobDirectory(job.Id);
        Directory.CreateDirectory(jobDir);

        job.Status = JobStatus.Running;
        job.StartedUtc = DateTimeOffset.UtcNow;
        _store.Update(job);

        try
        {
            var configPath = WriteJobConfig(job, algorithm, jobDir);
            var exitCode = await ExecuteAsync(job, configPath, jobDir, cancellationToken);
            job.ExitCode = exitCode;

            // Success is decided by the result file, never by the exit code: LEAN's
            // Python teardown aborts with SIGABRT (exit 134) *after* results are
            // written. See "Known issue: exit code 134" in SETUP.md.
            var summary = Directory.EnumerateFiles(jobDir, "*-summary.json").FirstOrDefault();
            if (summary is null)
            {
                job.Status = JobStatus.Failed;
                job.Error = DescribeFailure(job, exitCode);
                return;
            }

            ApplyResults(job, summary);
            job.Status = JobStatus.Succeeded;
        }
        catch (OperationCanceledException)
        {
            job.Status = JobStatus.Failed;
            job.Error = "Cancelled during shutdown.";
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Backtest {Id} threw.", job.Id);
            job.Status = JobStatus.Failed;
            job.Error = ex.Message;
        }
        finally
        {
            job.FinishedUtc = DateTimeOffset.UtcNow;
            _store.Update(job);
        }
    }

    /// <summary>
    /// Layers per-job overrides onto Launcher/config.json. Every path is made
    /// absolute because the child runs with its working directory set to the
    /// launcher's own bin directory, where the committed config's relative
    /// paths are resolved differently.
    /// </summary>
    private string WriteJobConfig(BacktestJob job, CatalogEntry algorithm, string jobDir)
    {
        // Launcher/config.json is JSON with comments, so parse permissively.
        var text = File.ReadAllText(_options.BaseConfigPath);
        var config = JsonNode.Parse(text, null, new JsonDocumentOptions
        {
            CommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true
        })!.AsObject();

        config["environment"] = "backtesting";
        config["algorithm-type-name"] = algorithm.TypeName;
        config["algorithm-language"] = algorithm.Language;
        config["algorithm-location"] = algorithm.Location;
        config["data-folder"] = _options.DataFolder + Path.DirectorySeparatorChar;
        config["results-destination-folder"] = jobDir;
        config["algorithm-id"] = job.Id;
        config["close-automatically"] = true;

        var parameters = new JsonObject();
        foreach (var (key, value) in job.Parameters) parameters[key] = value;
        config["parameters"] = parameters;

        var configPath = Path.Combine(jobDir, "config.json");
        File.WriteAllText(configPath, config.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        return configPath;
    }

    private async Task<int> ExecuteAsync(BacktestJob job, string configPath, string jobDir, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet",
            // The launcher resolves its plugin assemblies relative to the working
            // directory, so it has to run from its own output directory.
            WorkingDirectory = _options.LauncherDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        startInfo.ArgumentList.Add(_options.LauncherDll);
        startInfo.ArgumentList.Add("--config");
        startInfo.ArgumentList.Add(configPath);
        startInfo.ArgumentList.Add("--results-destination-folder");
        startInfo.ArgumentList.Add(jobDir);

        using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        await using var logFile = new StreamWriter(Path.Combine(jobDir, "runner.log"), append: false) { AutoFlush = true };

        void Capture(string? line)
        {
            if (line is null) return;
            lock (logFile) logFile.WriteLine(line);
            _store.AppendLog(job.Id, line);
        }

        process.OutputDataReceived += (_, e) => Capture(e.Data);
        process.ErrorDataReceived += (_, e) => Capture(e.Data);

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_options.Timeout);

        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            TryKill(process);
            throw new TimeoutException($"Backtest exceeded the {_options.Timeout.TotalMinutes:0.#} minute limit.");
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            throw;
        }

        return process.ExitCode;
    }

    private void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not kill launcher process {Pid}.", process.Id);
        }
    }

    /// <summary>
    /// Builds an error message for a run that produced no summary, preferring
    /// the last real line LEAN logged over a bare exit code.
    /// </summary>
    private string DescribeFailure(BacktestJob job, int exitCode)
    {
        var lastError = _store.LogTail(job.Id)
            .Reverse()
            .FirstOrDefault(l => l.Contains("ERROR", StringComparison.OrdinalIgnoreCase)
                              || l.Contains("Exception", StringComparison.OrdinalIgnoreCase));

        return lastError is not null
            ? $"The algorithm did not produce results (exit {exitCode}). Last error: {lastError.Trim()}"
            : $"The algorithm did not produce results (exit {exitCode}). See the log for details.";
    }

    /// <summary>
    /// Pulls statistics and the equity curve out of LEAN's summary file.
    /// </summary>
    private void ApplyResults(BacktestJob job, string summaryPath)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(summaryPath));
        var root = document.RootElement;

        if (root.TryGetProperty("statistics", out var statistics) && statistics.ValueKind == JsonValueKind.Object)
        {
            job.Statistics = statistics.EnumerateObject()
                .ToDictionary(p => p.Name, p => p.Value.ToString());
        }

        if (root.TryGetProperty("state", out var state) &&
            state.TryGetProperty("OrderCount", out var orders) &&
            int.TryParse(orders.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var orderCount))
        {
            job.OrderCount = orderCount;
        }

        // A runtime error is reported inside the results rather than as a
        // non-zero exit, so an otherwise complete run can still be a failure.
        if (state.ValueKind == JsonValueKind.Object &&
            state.TryGetProperty("RuntimeError", out var runtimeError) &&
            !string.IsNullOrWhiteSpace(runtimeError.GetString()))
        {
            job.Error = runtimeError.GetString();
        }

        job.Equity = ExtractEquity(root);
    }

    /// <summary>
    /// Reads the "Strategy Equity"/"Equity" series. LEAN writes candlestick
    /// values as [time, open, high, low, close] and line values as [time, value],
    /// so take the last element either way.
    /// </summary>
    private static List<EquityPoint>? ExtractEquity(JsonElement root)
    {
        if (!root.TryGetProperty("charts", out var charts) || charts.ValueKind != JsonValueKind.Object) return null;
        if (!charts.TryGetProperty("Strategy Equity", out var equityChart)) return null;
        if (!equityChart.TryGetProperty("series", out var series) || series.ValueKind != JsonValueKind.Object) return null;
        if (!series.TryGetProperty("Equity", out var equity)) return null;
        if (!equity.TryGetProperty("values", out var values) || values.ValueKind != JsonValueKind.Array) return null;

        var points = new List<EquityPoint>();
        foreach (var value in values.EnumerateArray())
        {
            if (value.ValueKind != JsonValueKind.Array) continue;
            var fields = value.EnumerateArray().ToArray();
            if (fields.Length < 2) continue;
            if (!fields[0].TryGetInt64(out var time)) continue;

            var last = fields[^1];
            if (last.ValueKind != JsonValueKind.Number) continue;
            points.Add(new EquityPoint(time, last.GetDouble()));
        }

        return points.Count > 0 ? points : null;
    }
}
