using ERP_RFQ_Automation.Infrastructure.Storage;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace ERP_RFQ_Automation.HealthChecks;

public sealed class EvidenceStorageHealthCheck : IHealthCheck
{
    private readonly IEvidenceObjectStorage _storage;

    public EvidenceStorageHealthCheck(IEvidenceObjectStorage storage) => _storage = storage;

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        if (!_storage.IsDurable)
            return HealthCheckResult.Unhealthy(
                "Evidence storage is local and ephemeral; configure an S3-compatible provider before certification.");

        try
        {
            await _storage.ProbeAsync(cancellationToken);
            return HealthCheckResult.Healthy("Durable evidence object storage is reachable.");
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return HealthCheckResult.Unhealthy("Durable evidence object storage is not reachable.", exception);
        }
    }
}
