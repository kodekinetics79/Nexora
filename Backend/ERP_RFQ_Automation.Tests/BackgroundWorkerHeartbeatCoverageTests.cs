using ERP_RFQ_Automation.Billing;
using ERP_RFQ_Automation.Billing.Accounting;
using ERP_RFQ_Automation.CommercialFinance;
using ERP_RFQ_Automation.HealthChecks;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Platform.Hardening;
using ERP_RFQ_Automation.Platform.Models;
using ERP_RFQ_Automation.Platform.Provisioning;
using ERP_RFQ_Automation.Tests.Support;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// <c>HostOptions.BackgroundServiceExceptionBehavior</c> is <c>Ignore</c>, so a worker whose
/// loop dies is gone for the life of the process while <c>/ready</c> stays green. Five loop
/// workers had no heartbeat at all: tenant provisioning, the extraction-queue metrics poller,
/// subscription dunning, the accounting outbox and the finance outbox. These tests pin that
/// each one registers with the shared liveness ledger and beats once its loop turns — the
/// difference between "idle" and "dead" being observable at all.
///
/// <para>Revert-proofed: remove any worker's <c>Register</c>/<c>Beat</c> calls and its test
/// fails on the missing snapshot entry or the null <c>LastBeatUtc</c>.</para>
/// </summary>
public sealed class BackgroundWorkerHeartbeatCoverageTests
{
    // ------------------------------------------------------------ tenant provisioning

    [Fact]
    public void An_enabled_provisioning_worker_registers_its_heartbeat_at_construction()
    {
        var heartbeats = new BackgroundWorkerHeartbeats();
        using var db = new TestDb();
        using var provider = ProvisioningServices(db);

        _ = new ProvisioningRunWorker(
            provider.GetRequiredService<IServiceScopeFactory>(), new ProvisioningRunSignal(),
            new StaticOptionsMonitor<ProvisioningOptions>(new() { Enabled = true }),
            NullLogger<ProvisioningRunWorker>.Instance, heartbeats);

        var status = Assert.Single(heartbeats.Snapshot(), s => s.Worker == BackgroundWorkerNames.TenantProvisioning);
        Assert.Null(status.LastBeatUtc);
        Assert.True(status.IsAlive); // inside the startup grace
    }

    [Fact]
    public void A_disabled_provisioning_worker_registers_nothing()
    {
        var heartbeats = new BackgroundWorkerHeartbeats();
        using var db = new TestDb();
        using var provider = ProvisioningServices(db);

        _ = new ProvisioningRunWorker(
            provider.GetRequiredService<IServiceScopeFactory>(), new ProvisioningRunSignal(),
            new StaticOptionsMonitor<ProvisioningOptions>(new() { Enabled = false }),
            NullLogger<ProvisioningRunWorker>.Instance, heartbeats);

        Assert.DoesNotContain(heartbeats.Snapshot(), s => s.Worker == BackgroundWorkerNames.TenantProvisioning);
    }

    [Fact]
    public async Task A_turning_provisioning_loop_beats_even_when_every_sweep_throws()
    {
        var heartbeats = new BackgroundWorkerHeartbeats();
        using var db = new TestDb();
        using var provider = ProvisioningServices(db, new ThrowingRunner());
        var worker = new ProvisioningRunWorker(
            provider.GetRequiredService<IServiceScopeFactory>(), new ProvisioningRunSignal(),
            new StaticOptionsMonitor<ProvisioningOptions>(new()
            {
                Enabled = true, InitialDelay = TimeSpan.Zero, PollInterval = TimeSpan.FromMilliseconds(200)
            }),
            NullLogger<ProvisioningRunWorker>.Instance, heartbeats);

        await worker.StartAsync(default);
        await WaitForBeatAsync(heartbeats, BackgroundWorkerNames.TenantProvisioning);
        await worker.StopAsync(default);

        Assert.NotNull(Assert.Single(heartbeats.Snapshot()).LastBeatUtc);
    }

    // ------------------------------------------------------------ extraction queue metrics

    [Fact]
    public async Task The_extraction_queue_metrics_poller_registers_and_beats_per_poll()
    {
        var heartbeats = new BackgroundWorkerHeartbeats();
        using var db = new TestDb();
        using var provider = new ServiceCollection().AddScoped(_ => db.ContextFor(null)).BuildServiceProvider();
        var poller = new ExtractionQueueMetricsPoller(
            provider.GetRequiredService<IServiceScopeFactory>(), new ExtractionQueueSnapshotProvider(),
            new ExtractionQueueMetricsOptions { PollInterval = TimeSpan.FromMilliseconds(200) },
            NullLogger<ExtractionQueueMetricsPoller>.Instance, heartbeats);

        Assert.Null(Assert.Single(heartbeats.Snapshot(), s => s.Worker == BackgroundWorkerNames.ExtractionQueueMetrics).LastBeatUtc);

        await poller.StartAsync(default);
        await WaitForBeatAsync(heartbeats, BackgroundWorkerNames.ExtractionQueueMetrics);
        await poller.StopAsync(default);

        Assert.NotNull(Assert.Single(heartbeats.Snapshot()).LastBeatUtc);
    }

    [Fact]
    public void A_disabled_metrics_poller_registers_nothing()
    {
        var heartbeats = new BackgroundWorkerHeartbeats();
        using var db = new TestDb();
        using var provider = new ServiceCollection().AddScoped(_ => db.ContextFor(null)).BuildServiceProvider();

        _ = new ExtractionQueueMetricsPoller(
            provider.GetRequiredService<IServiceScopeFactory>(), new ExtractionQueueSnapshotProvider(),
            new ExtractionQueueMetricsOptions { Enabled = false },
            NullLogger<ExtractionQueueMetricsPoller>.Instance, heartbeats);

        Assert.Empty(heartbeats.Snapshot());
    }

    // ------------------------------------------------------------ subscription dunning

    [Fact]
    public async Task The_subscription_dunning_worker_beats_whether_or_not_dunning_is_enabled()
    {
        var heartbeats = new BackgroundWorkerHeartbeats();
        using var db = new TestDb();
        using var provider = new ServiceCollection().AddScoped(_ => db.ContextFor(null)).BuildServiceProvider();
        var worker = new SubscriptionDunningWorker(
            provider.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(new SubscriptionDunningOptions { Enabled = false, PollMinutes = 1 }),
            NullLogger<SubscriptionDunningWorker>.Instance, heartbeats);

        Assert.Null(Assert.Single(heartbeats.Snapshot(), s => s.Worker == BackgroundWorkerNames.SubscriptionDunning).LastBeatUtc);

        await worker.StartAsync(default);
        await WaitForBeatAsync(heartbeats, BackgroundWorkerNames.SubscriptionDunning);
        await worker.StopAsync(default);

        Assert.NotNull(Assert.Single(heartbeats.Snapshot()).LastBeatUtc);
    }

    // ------------------------------------------------------------ accounting outbox

    [Fact]
    public async Task The_accounting_outbox_dispatcher_beats_whether_or_not_export_is_enabled()
    {
        var heartbeats = new BackgroundWorkerHeartbeats();
        using var db = new TestDb();
        using var provider = new ServiceCollection().AddScoped(_ => db.ContextFor(null)).BuildServiceProvider();
        var worker = new AccountingOutboxDispatcher(
            provider.GetRequiredService<IServiceScopeFactory>(), new NeverCalledConnector(),
            Options.Create(new AccountingExportOptions { Enabled = false, PollSeconds = 2 }),
            NullLogger<AccountingOutboxDispatcher>.Instance, heartbeats);

        Assert.Null(Assert.Single(heartbeats.Snapshot(), s => s.Worker == BackgroundWorkerNames.AccountingOutbox).LastBeatUtc);

        await worker.StartAsync(default);
        await WaitForBeatAsync(heartbeats, BackgroundWorkerNames.AccountingOutbox);
        await worker.StopAsync(default);

        Assert.NotNull(Assert.Single(heartbeats.Snapshot()).LastBeatUtc);
    }

    // ------------------------------------------------------------ finance outbox

    [Fact]
    public void An_enabled_finance_outbox_dispatcher_registers_and_a_disabled_one_does_not()
    {
        var enabled = new BackgroundWorkerHeartbeats();
        var disabled = new BackgroundWorkerHeartbeats();
        using var harness = new TenantWorkGateHarness(configure: services => services
            .AddSingleton<IFinanceOutboxStore>(new EmptyFinanceStore()));

        _ = new FinanceOutboxDispatcherService(harness.ScopeFactory,
            new StaticOptionsMonitor<FinanceOutboxDispatcherOptions>(new() { Enabled = true }),
            NullLogger<FinanceOutboxDispatcherService>.Instance, harness.TenantScope, enabled);
        _ = new FinanceOutboxDispatcherService(harness.ScopeFactory,
            new StaticOptionsMonitor<FinanceOutboxDispatcherOptions>(new() { Enabled = false }),
            NullLogger<FinanceOutboxDispatcherService>.Instance, harness.TenantScope, disabled);

        Assert.Single(enabled.Snapshot(), s => s.Worker == BackgroundWorkerNames.FinanceOutbox);
        Assert.Empty(disabled.Snapshot());
    }

    [Fact]
    public async Task A_turning_finance_outbox_loop_beats()
    {
        var heartbeats = new BackgroundWorkerHeartbeats();
        using var harness = new TenantWorkGateHarness(configure: services => services
            .AddSingleton<IFinanceOutboxStore>(new EmptyFinanceStore()));
        var dispatcher = new FinanceOutboxDispatcherService(harness.ScopeFactory,
            new StaticOptionsMonitor<FinanceOutboxDispatcherOptions>(new()
            {
                Enabled = true, Endpoint = "http://localhost/finance-events",
                PollInterval = TimeSpan.FromMilliseconds(250)
            }),
            NullLogger<FinanceOutboxDispatcherService>.Instance, harness.TenantScope, heartbeats);

        await dispatcher.StartAsync(default);
        await WaitForBeatAsync(heartbeats, BackgroundWorkerNames.FinanceOutbox);
        await dispatcher.StopAsync(default);

        Assert.NotNull(Assert.Single(heartbeats.Snapshot()).LastBeatUtc);
    }

    // ------------------------------------------------------------ helpers

    private static async Task WaitForBeatAsync(BackgroundWorkerHeartbeats heartbeats, string worker)
    {
        var deadline = DateTime.UtcNow + TestWaits.Liveness;
        while (DateTime.UtcNow < deadline)
        {
            if (heartbeats.Snapshot().SingleOrDefault(s => s.Worker == worker)?.LastBeatUtc is not null) return;
            await Task.Delay(25);
        }
        Assert.Fail($"{worker} never beat within {TestWaits.Liveness}.");
    }

    private static ServiceProvider ProvisioningServices(TestDb db, ITenantProvisioningRunner? runner = null)
        => new ServiceCollection()
            .AddScoped(_ => db.ContextFor(null))
            .AddSingleton(runner ?? new ThrowingRunner())
            .BuildServiceProvider();

    /// <summary>A sweep that always fails — the case a heartbeat must NOT confuse with death.</summary>
    private sealed class ThrowingRunner : ITenantProvisioningRunner
    {
        public Task<ProvisioningRunOutcome?> RunAsync(long executionId, CancellationToken ct = default)
            => throw new InvalidOperationException("sweep failed");
        public Task<int> RunAvailableAsync(int batchSize, CancellationToken ct = default)
            => throw new InvalidOperationException("sweep failed");
    }

    private sealed class NeverCalledConnector : IAccountingExportConnector
    {
        public Task<AccountingExportReceipt> ExportAsync(AccountingOutboxMessage message, CancellationToken ct)
            => throw new InvalidOperationException("export is disabled in this test");
    }

    private sealed class EmptyFinanceStore : IFinanceOutboxStore
    {
        public Task<IReadOnlyList<FinanceOutboxEnvelope>> ClaimAsync(
            string workerId, int batchSize, TimeSpan lease, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<FinanceOutboxEnvelope>>([]);
        public Task CompleteAsync(long id, string workerId, Guid leaseToken, CancellationToken ct) => Task.CompletedTask;
        public Task FailAsync(long id, string workerId, Guid leaseToken, string error, TimeSpan retryDelay, int maxAttempts, CancellationToken ct)
            => Task.CompletedTask;
    }

    private sealed class StaticOptionsMonitor<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue => value;
        public T Get(string? name) => value;
        public IDisposable OnChange(Action<T, string?> listener) => new NullChangeToken();
        private sealed class NullChangeToken : IDisposable { public void Dispose() { } }
    }
}
