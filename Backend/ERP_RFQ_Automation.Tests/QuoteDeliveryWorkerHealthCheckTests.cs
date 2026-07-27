using ERP_RFQ_Automation.HealthChecks;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace ERP_RFQ_Automation.Tests;

public sealed class QuoteDeliveryWorkerHealthCheckTests
{
    [Fact]
    public async Task Worker_health_requires_a_recent_successful_claim_loop()
    {
        var heartbeat = new QuoteDeliveryWorkerHeartbeat();
        var check = new QuoteDeliveryWorkerHealthCheck(heartbeat);

        Assert.Equal(HealthStatus.Unhealthy,
            (await check.CheckHealthAsync(new HealthCheckContext())).Status);

        heartbeat.Beat();
        heartbeat.RecordSuccess();
        Assert.Equal(HealthStatus.Healthy,
            (await check.CheckHealthAsync(new HealthCheckContext())).Status);

        heartbeat.RecordFailure();
        heartbeat.RecordFailure();
        heartbeat.RecordFailure();
        Assert.Equal(HealthStatus.Unhealthy,
            (await check.CheckHealthAsync(new HealthCheckContext())).Status);
    }
}
