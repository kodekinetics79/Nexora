using Microsoft.Extensions.Options;

namespace ERP_RFQ_Automation.CommercialFinance;

public sealed class FinanceOutboxDispatcherService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IOptionsMonitor<FinanceOutboxDispatcherOptions> _options;
    private readonly ILogger<FinanceOutboxDispatcherService> _logger;
    private readonly string _workerId;

    public FinanceOutboxDispatcherService(
        IServiceScopeFactory scopeFactory,
        IOptionsMonitor<FinanceOutboxDispatcherOptions> options,
        ILogger<FinanceOutboxDispatcherService> logger)
    {
        _scopeFactory = scopeFactory;
        _options = options;
        _logger = logger;
        _workerId = $"{Environment.MachineName}:{Environment.ProcessId}:{Guid.NewGuid():N}";
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.CurrentValue.Enabled)
        {
            _logger.LogInformation("Commercial finance outbox dispatcher is disabled.");
            return;
        }

        _logger.LogInformation("Commercial finance outbox dispatcher {WorkerId} started.", _workerId);
        var consecutiveErrors = 0;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var options = _options.CurrentValue;
                var messages = await ClaimAsync(options, stoppingToken);
                consecutiveErrors = 0;

                if (messages.Count == 0)
                {
                    await Task.Delay(options.PollInterval, stoppingToken);
                    continue;
                }

                await Parallel.ForEachAsync(
                    messages,
                    new ParallelOptions
                    {
                        CancellationToken = stoppingToken,
                        MaxDegreeOfParallelism = options.MaxConcurrency
                    },
                    (message, cancellationToken) => DispatchAsync(message, options, cancellationToken));
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                consecutiveErrors = Math.Min(consecutiveErrors + 1, 30);
                var delay = CalculateBackoff(
                    TimeSpan.FromSeconds(1),
                    _options.CurrentValue.MaximumDispatcherBackoff,
                    consecutiveErrors);
                _logger.LogError(
                    exception,
                    "Commercial finance outbox dispatcher cycle failed; retrying in {Delay}.",
                    delay);
                await Task.Delay(delay, stoppingToken);
            }
        }

        _logger.LogInformation("Commercial finance outbox dispatcher {WorkerId} stopped.", _workerId);
    }

    private async Task<IReadOnlyList<FinanceOutboxEnvelope>> ClaimAsync(
        FinanceOutboxDispatcherOptions options,
        CancellationToken cancellationToken)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IFinanceOutboxStore>();
        return await store.ClaimAsync(
            _workerId,
            options.BatchSize,
            options.LeaseDuration,
            cancellationToken);
    }

    private async ValueTask DispatchAsync(
        FinanceOutboxEnvelope message,
        FinanceOutboxDispatcherOptions options,
        CancellationToken cancellationToken)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var publisher = scope.ServiceProvider.GetRequiredService<IFinanceEventPublisher>();
        var store = scope.ServiceProvider.GetRequiredService<IFinanceOutboxStore>();

        try
        {
            await publisher.PublishAsync(message, cancellationToken);
            await store.CompleteAsync(message.Id, _workerId, message.LeaseToken, cancellationToken);
            _logger.LogInformation(
                "Published finance event {EventId} ({EventType}) on attempt {AttemptCount}.",
                message.EventId,
                message.EventType,
                message.AttemptCount);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (FinanceOutboxLeaseConflictException exception)
        {
            _logger.LogWarning(
                exception,
                "Finance event {EventId} lost lease ownership; no state transition was made.",
                message.EventId);
        }
        catch (Exception exception)
        {
            var retryDelay = CalculateBackoff(
                options.InitialRetryDelay,
                options.MaximumRetryDelay,
                message.AttemptCount);

            try
            {
                await store.FailAsync(
                    message.Id,
                    _workerId,
                    message.LeaseToken,
                    DescribeFailure(exception),
                    retryDelay,
                    options.MaxAttempts,
                    cancellationToken);
                _logger.LogWarning(
                    exception,
                    "Finance event {EventId} failed on attempt {AttemptCount}; next retry delay is {RetryDelay}.",
                    message.EventId,
                    message.AttemptCount,
                    retryDelay);
            }
            catch (FinanceOutboxLeaseConflictException leaseException)
            {
                _logger.LogWarning(
                    leaseException,
                    "Finance event {EventId} failed after its lease expired or was fenced.",
                    message.EventId);
            }
        }
    }

    private static TimeSpan CalculateBackoff(TimeSpan initial, TimeSpan maximum, int attempt)
    {
        var exponent = Math.Clamp(attempt - 1, 0, 30);
        var milliseconds = Math.Min(
            maximum.TotalMilliseconds,
            initial.TotalMilliseconds * Math.Pow(2, exponent));
        return TimeSpan.FromMilliseconds(milliseconds);
    }

    private static string DescribeFailure(Exception exception)
    {
        var description = $"{exception.GetType().Name}: {exception.Message}";
        return description.Length <= 2000 ? description : description[..2000];
    }
}
