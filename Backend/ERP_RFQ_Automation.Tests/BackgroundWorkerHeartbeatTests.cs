using ERP_RFQ_Automation.HealthChecks;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// `HostOptions.BackgroundServiceExceptionBehavior` is `Ignore` (Program.cs:92), so a
/// worker whose ExecuteAsync throws is gone for the lifetime of the process while
/// /ready stays green. Routing reconciliation, the SLA sweep, the email poller and AI
/// reservation reconciliation had no heartbeat at all. These tests pin the semantics of
/// the registry that now covers them.
/// </summary>
public sealed class BackgroundWorkerHeartbeatTests
{
    private static readonly DateTimeOffset Start = new(2026, 8, 4, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void No_registered_workers_is_healthy_not_unhealthy()
    {
        // A host that runs none of these workers must not be reported as broken.
        var result = Check(new BackgroundWorkerHeartbeats(new FakeClock(Start)));

        Assert.Equal(HealthStatus.Healthy, result.Status);
    }

    [Fact]
    public void A_beating_worker_is_healthy()
    {
        var clock = new FakeClock(Start);
        var heartbeats = new BackgroundWorkerHeartbeats(clock);
        heartbeats.Register(BackgroundWorkerNames.SlaSweep, TimeSpan.FromMinutes(5));
        heartbeats.Beat(BackgroundWorkerNames.SlaSweep);

        clock.Advance(TimeSpan.FromMinutes(10));

        Assert.Equal(HealthStatus.Healthy, Check(heartbeats).Status);
    }

    [Fact]
    public void A_worker_that_stopped_beating_turns_ready_red_and_is_named()
    {
        var clock = new FakeClock(Start);
        var heartbeats = new BackgroundWorkerHeartbeats(clock);
        heartbeats.Register(BackgroundWorkerNames.SlaSweep, TimeSpan.FromMinutes(5));
        heartbeats.Register(BackgroundWorkerNames.RoutingReconciliation, TimeSpan.FromMinutes(1));
        heartbeats.Beat(BackgroundWorkerNames.SlaSweep);
        heartbeats.Beat(BackgroundWorkerNames.RoutingReconciliation);

        // Routing dies; the SLA sweep keeps going. Tolerance is 3 x period + 1 min, so
        // at +6 min routing (4 min) is dead and the SLA sweep (16 min) is not.
        clock.Advance(TimeSpan.FromMinutes(6));
        heartbeats.Beat(BackgroundWorkerNames.SlaSweep);

        var result = Check(heartbeats);

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
        Assert.Contains(BackgroundWorkerNames.RoutingReconciliation, result.Description);
        Assert.DoesNotContain(BackgroundWorkerNames.SlaSweep, result.Description);
    }

    [Fact]
    public void A_worker_registered_but_never_started_fails_after_the_startup_grace()
    {
        var clock = new FakeClock(Start);
        var heartbeats = new BackgroundWorkerHeartbeats(clock);
        // Constructed by DI but ExecuteAsync never ran — exactly what Ignore hides.
        heartbeats.Register(BackgroundWorkerNames.EmailPoller, TimeSpan.FromMinutes(5));

        clock.Advance(TimeSpan.FromMinutes(2));
        Assert.Equal(HealthStatus.Healthy, Check(heartbeats).Status);   // still inside grace

        clock.Advance(TimeSpan.FromMinutes(5));
        Assert.Equal(HealthStatus.Unhealthy, Check(heartbeats).Status);
    }

    [Fact]
    public void A_data_driven_interval_widens_the_window_on_the_next_beat()
    {
        // The email poller's period comes from EmailConfigurations.PollingInterval, so a
        // tenant raising it must not make /ready flap.
        var clock = new FakeClock(Start);
        var heartbeats = new BackgroundWorkerHeartbeats(clock);
        heartbeats.Register(BackgroundWorkerNames.EmailPoller, TimeSpan.FromMinutes(5));
        heartbeats.Beat(BackgroundWorkerNames.EmailPoller, TimeSpan.FromMinutes(30));

        clock.Advance(TimeSpan.FromMinutes(60)); // inside 3 x 30 + 1

        Assert.Equal(HealthStatus.Healthy, Check(heartbeats).Status);
    }

    private static HealthCheckResult Check(IBackgroundWorkerHeartbeats heartbeats)
        => new BackgroundWorkerHealthCheck(heartbeats)
            .CheckHealthAsync(new HealthCheckContext(), default).GetAwaiter().GetResult();

    private sealed class FakeClock(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now += by;
    }
}
