using System.Threading.Channels;

namespace QuantConnect.FinAI.Web;

/// <summary>
/// Bounded FIFO of pending backtests, drained by a fixed pool of workers.
/// Bounded because each entry represents minutes of CPU, so an unbounded queue
/// would let callers promise work the box cannot get through.
/// </summary>
public sealed class BacktestQueue
{
    private readonly Channel<(BacktestJob Job, CatalogEntry Algorithm)> _channel;

    public BacktestQueue(FinAIOptions options)
    {
        _channel = Channel.CreateBounded<(BacktestJob, CatalogEntry)>(new BoundedChannelOptions(options.MaxQueueDepth)
        {
            FullMode = BoundedChannelFullMode.Wait
        });
    }

    /// <summary>Returns false when the queue is full, so the caller can answer 503 instead of blocking.</summary>
    public bool TryEnqueue(BacktestJob job, CatalogEntry algorithm) => _channel.Writer.TryWrite((job, algorithm));

    public IAsyncEnumerable<(BacktestJob Job, CatalogEntry Algorithm)> ReadAllAsync(CancellationToken cancellationToken) =>
        _channel.Reader.ReadAllAsync(cancellationToken);
}

public sealed class BacktestWorker : BackgroundService
{
    private readonly BacktestQueue _queue;
    private readonly BacktestRunner _runner;
    private readonly FinAIOptions _options;
    private readonly ILogger<BacktestWorker> _logger;

    public BacktestWorker(BacktestQueue queue, BacktestRunner runner, FinAIOptions options, ILogger<BacktestWorker> logger)
    {
        _queue = queue;
        _runner = runner;
        _options = options;
        _logger = logger;
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var workers = Enumerable.Range(0, Math.Max(1, _options.MaxConcurrency))
            .Select(i => Task.Run(() => DrainAsync(i, stoppingToken), stoppingToken));
        return Task.WhenAll(workers);
    }

    private async Task DrainAsync(int worker, CancellationToken stoppingToken)
    {
        _logger.LogInformation("Backtest worker {Worker} started.", worker);
        try
        {
            await foreach (var (job, algorithm) in _queue.ReadAllAsync(stoppingToken))
            {
                _logger.LogInformation("Worker {Worker} running job {Id} ({Algorithm}).", worker, job.Id, algorithm.Id);
                try
                {
                    await _runner.RunAsync(job, algorithm, stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    // RunAsync records the failure on the job itself; one bad run
                    // must not take the worker down with it.
                    _logger.LogError(ex, "Job {Id} failed outside the runner's own handling.", job.Id);
                }
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Backtest worker {Worker} stopping.", worker);
        }
    }
}
