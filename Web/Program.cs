using System.Globalization;
using QuantConnect.FinAI.Web;

var builder = WebApplication.CreateBuilder(args);

var options = new FinAIOptions();
builder.Configuration.GetSection("FinAI").Bind(options);

// The repository root anchors catalog locations and every path in FinAIOptions.
// The service's output directory is <repo>/Web/bin/Release, so the root is three
// levels up; FinAI__RepoRoot overrides that for unusual layouts.
var repoRoot = Path.GetFullPath(builder.Configuration["FinAI:RepoRoot"]
                                ?? Path.Combine(AppContext.BaseDirectory, "..", "..", ".."));
options.Resolve(repoRoot);

// Minimal-API responses use their own serializer options, so the enum-as-string
// setting in JsonOptions.Default does not reach them. The client compares
// status against "Queued"/"Running", not against ordinals.
builder.Services.ConfigureHttpJsonOptions(json =>
{
    json.SerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());
    json.SerializerOptions.DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull;
});

builder.Services.AddSingleton(options);
builder.Services.AddSingleton<JobStore>();
builder.Services.AddSingleton<BacktestRunner>();
builder.Services.AddSingleton<BacktestQueue>();
builder.Services.AddHostedService<BacktestWorker>();
builder.Services.AddSingleton(sp => AlgorithmCatalog.Load(
    Path.Combine(AppContext.BaseDirectory, "catalog.json"),
    repoRoot,
    sp.GetRequiredService<ILogger<AlgorithmCatalog>>()));

var app = builder.Build();

var startupLogger = app.Services.GetRequiredService<ILogger<Program>>();
startupLogger.LogInformation("Repository root {Root}; launcher {Launcher}; data {Data}.", repoRoot, options.LauncherDll, options.DataFolder);

if (!File.Exists(options.LauncherDll))
{
    // Fail loudly at boot rather than on the first backtest.
    startupLogger.LogCritical("Launcher not found at {Path}. Build Launcher/QuantConnect.Lean.Launcher.csproj first.", options.LauncherDll);
    return 1;
}
// Resolve the catalog now rather than on first use. As a lazy singleton a bad
// catalog surfaced as a 500 on every /api request with the real reason buried
// in the log; a service that cannot run anything should not report healthy.
try
{
    _ = app.Services.GetRequiredService<AlgorithmCatalog>();
}
catch (Exception ex)
{
    startupLogger.LogCritical(ex, "Catalog could not be loaded. Check that Algorithm.Python is present and readable at {Root}.", repoRoot);
    return 1;
}

if (string.IsNullOrEmpty(options.AccessToken))
{
    startupLogger.LogWarning("FinAI__AccessToken is unset: the API is open and anyone who reaches it can spend CPU.");
}
Directory.CreateDirectory(options.ResultsRoot);

app.UseDefaultFiles();
app.UseStaticFiles();

app.MapGet("/healthz", () => Results.Ok(new { status = "ok" }));

var api = app.MapGroup("/api");

// Shared-secret gate. Applied to /api only so the static page still loads and
// can prompt for the token.
api.AddEndpointFilter((context, next) =>
{
    if (string.IsNullOrEmpty(options.AccessToken)) return next(context);

    var request = context.HttpContext.Request;
    var presented = request.Headers["X-FinAI-Token"].FirstOrDefault()
                    ?? (request.Headers.Authorization.FirstOrDefault() is { } auth
                        && auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
                        ? auth["Bearer ".Length..]
                        : null);

    return CryptographicEquals(presented, options.AccessToken)
        ? next(context)
        : ValueTask.FromResult<object?>(Results.Unauthorized());
});

api.MapGet("/algorithms", (AlgorithmCatalog catalog) => Results.Ok(
    catalog.Entries.Select(e => new
    {
        e.Id,
        e.Name,
        e.Description,
        e.Language,
        e.Parameters
    })));

api.MapGet("/backtests", (JobStore store, int? limit) =>
    Results.Ok(store.Recent(Math.Clamp(limit ?? 25, 1, 200)).Select(Summarize)));

api.MapPost("/backtests", (BacktestRequest request, AlgorithmCatalog catalog, JobStore store, BacktestQueue queue) =>
{
    var algorithm = catalog.Find(request.AlgorithmId ?? "");
    if (algorithm is null)
    {
        return Results.BadRequest(new { error = $"Unknown algorithm '{request.AlgorithmId}'." });
    }

    if (!TryBindParameters(algorithm, request.Parameters, out var parameters, out var error))
    {
        return Results.BadRequest(new { error });
    }

    var job = new BacktestJob
    {
        Id = Guid.NewGuid().ToString("n")[..12],
        AlgorithmId = algorithm.Id,
        AlgorithmName = algorithm.Name,
        Parameters = parameters
    };

    store.Add(job);

    if (!queue.TryEnqueue(job, algorithm))
    {
        job.Status = JobStatus.Failed;
        job.Error = "The queue is full. Try again once running backtests finish.";
        job.FinishedUtc = DateTimeOffset.UtcNow;
        store.Update(job);
        return Results.Json(new { error = job.Error }, statusCode: StatusCodes.Status503ServiceUnavailable);
    }

    return Results.Accepted($"/api/backtests/{job.Id}", Summarize(job));
});

api.MapGet("/backtests/{id}", (string id, JobStore store) =>
    store.Find(id) is { } job ? Results.Ok(Detail(job)) : Results.NotFound());

api.MapGet("/backtests/{id}/log", (string id, JobStore store) =>
    store.Find(id) is null
        ? Results.NotFound()
        : Results.Ok(new { lines = store.LogTail(id) }));

app.Run();
return 0;

static object Summarize(BacktestJob job) => new
{
    job.Id,
    job.AlgorithmId,
    job.AlgorithmName,
    job.Status,
    job.CreatedUtc,
    job.StartedUtc,
    job.FinishedUtc,
    job.DurationSeconds,
    job.Error
};

static object Detail(BacktestJob job) => new
{
    job.Id,
    job.AlgorithmId,
    job.AlgorithmName,
    job.Parameters,
    job.Status,
    job.CreatedUtc,
    job.StartedUtc,
    job.FinishedUtc,
    job.DurationSeconds,
    job.Error,
    job.ExitCode,
    job.OrderCount,
    job.Statistics,
    job.Equity
};

/// <summary>
/// Accepts only parameters the algorithm declares, and only inside the declared
/// range. Values reach LEAN as strings, so this is the one place that decides
/// what a caller may put in front of the engine.
/// </summary>
static bool TryBindParameters(
    CatalogEntry algorithm,
    Dictionary<string, string>? supplied,
    out Dictionary<string, string> bound,
    out string? error)
{
    bound = new Dictionary<string, string>();
    error = null;
    supplied ??= new Dictionary<string, string>();

    var declared = algorithm.Parameters.Select(p => p.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
    var unknown = supplied.Keys.FirstOrDefault(k => !declared.Contains(k));
    if (unknown is not null)
    {
        error = $"'{algorithm.Id}' does not accept a parameter named '{unknown}'.";
        return false;
    }

    foreach (var parameter in algorithm.Parameters)
    {
        var value = parameter.Default;

        if (supplied.TryGetValue(parameter.Name, out var raw) && !string.IsNullOrWhiteSpace(raw))
        {
            if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out value))
            {
                error = $"'{parameter.Name}' must be a number.";
                return false;
            }

            if (value < parameter.Min || value > parameter.Max)
            {
                error = $"'{parameter.Name}' must be between {parameter.Min} and {parameter.Max}.";
                return false;
            }
        }

        bound[parameter.Name] = value.ToString(CultureInfo.InvariantCulture);
    }

    return true;
}

/// <summary>Length-independent comparison so the token check does not leak its length by timing.</summary>
static bool CryptographicEquals(string? presented, string expected)
{
    if (presented is null) return false;
    return System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
        System.Text.Encoding.UTF8.GetBytes(presented),
        System.Text.Encoding.UTF8.GetBytes(expected));
}

public sealed record BacktestRequest(string? AlgorithmId, Dictionary<string, string>? Parameters);
