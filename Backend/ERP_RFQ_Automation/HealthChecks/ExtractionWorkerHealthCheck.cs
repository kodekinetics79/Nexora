using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace ERP_RFQ_Automation.HealthChecks;

public interface IExtractionWorkerHeartbeat
{
    DateTimeOffset? LastSeen { get; }
    void Beat();
}

public sealed class ExtractionWorkerHeartbeat : IExtractionWorkerHeartbeat
{
    private long _lastSeenTicks;

    public DateTimeOffset? LastSeen
    {
        get
        {
            var ticks = Interlocked.Read(ref _lastSeenTicks);
            return ticks == 0 ? null : new DateTimeOffset(ticks, TimeSpan.Zero);
        }
    }

    public void Beat() => Interlocked.Exchange(ref _lastSeenTicks, DateTimeOffset.UtcNow.UtcTicks);
}

public sealed class ExtractionWorkerHealthCheck : IHealthCheck
{
    private static readonly TimeSpan MaximumSilence = TimeSpan.FromSeconds(30);
    private readonly IExtractionWorkerHeartbeat _heartbeat;

    public ExtractionWorkerHealthCheck(IExtractionWorkerHeartbeat heartbeat) => _heartbeat = heartbeat;

    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        var lastSeen = _heartbeat.LastSeen;
        if (!lastSeen.HasValue)
            return Task.FromResult(HealthCheckResult.Unhealthy("Extraction worker has not started."));
        if (DateTimeOffset.UtcNow - lastSeen.Value > MaximumSilence)
            return Task.FromResult(HealthCheckResult.Unhealthy("Extraction worker heartbeat is stale."));
        return Task.FromResult(HealthCheckResult.Healthy("Extraction worker claim loop is active."));
    }
}
