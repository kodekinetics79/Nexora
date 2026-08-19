using System.Text.Json;
using ERP_RFQ_Automation.Agent.Models;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.MultiTenancy;
using ERP_RFQ_Automation.Notifications;
using ERP_RFQ_Automation.Platform.Models;
using ERP_RFQ_Automation.Procurement;
using ERP_RFQ_Automation.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace ERP_RFQ_Automation.Tests;

public sealed class ProcurementDispatchWorkerTests
{
    [Fact]
    public async Task Successful_delivery_is_recorded_once()
    {
        using var fixture = new DispatchFixture();
        fixture.SeedPending();

        Assert.True(await fixture.Worker.ProcessOneAsync(default));
        Assert.False(await fixture.Worker.ProcessOneAsync(default));

        var state = await fixture.StateAsync();
        Assert.Equal(1, fixture.Notification.SendCount);
        Assert.Equal(ProcurementOutboxStatuses.Sent, state.Message.Status);
        Assert.NotNull(state.Message.SentOn);
        Assert.Equal("test-acceptance-reference", state.Message.ProviderReference);
        Assert.Equal("test", state.Message.ProviderName);
        Assert.Null(state.Message.LeaseToken);
        Assert.Null(state.Message.LeaseOwner);
        Assert.Equal(SolicitationStatus.Sent, state.Solicitation.Status);
        Assert.Equal($"/procurement/rfqs/{DispatchFixture.Rfq}/sourcing", fixture.Notification.LastRequest?.CtaPath);
        Assert.Equal("SUPPLIER_SOLICITATION_SENT", Assert.Single(state.Events).EventType);
    }

    [Fact]
    public async Task Provider_invoked_failure_is_terminal_uncertain_and_not_automatically_retried()
    {
        using var fixture = new DispatchFixture(new RecordingNotification { Result = false });
        fixture.SeedPending();

        Assert.True(await fixture.Worker.ProcessOneAsync(default));

        var state = await fixture.StateAsync();
        Assert.Equal(1, fixture.Notification.SendCount);
        Assert.Equal(ProcurementOutboxStatuses.Failed, state.Message.Status);
        Assert.Null(state.Message.DeadLetteredOn);
        Assert.Equal(1, state.Message.AttemptCount);
        Assert.Equal("DELIVERY_ACCEPTANCE_EVIDENCE_MISSING", state.Message.LastErrorCode);
        Assert.Equal(SolicitationStatus.DeliveryFailed, state.Solicitation.Status);
        Assert.Equal("SUPPLIER_SOLICITATION_DELIVERY_UNCERTAIN", Assert.Single(state.Events).EventType);
        Assert.False(await fixture.Worker.ProcessOneAsync(default));
        Assert.Equal(1, fixture.Notification.SendCount);
    }

    [Fact]
    public async Task Poison_payload_is_terminal_without_calling_provider()
    {
        using var fixture = new DispatchFixture();
        fixture.SeedPending(payloadJson: "{not-json");

        Assert.True(await fixture.Worker.ProcessOneAsync(default));

        var state = await fixture.StateAsync();
        Assert.Equal(0, fixture.Notification.SendCount);
        Assert.Equal(ProcurementOutboxStatuses.DeadLettered, state.Message.Status);
        Assert.NotNull(state.Message.DeadLetteredOn);
        Assert.Equal("INVALID_DISPATCH_PAYLOAD", state.Message.LastErrorCode);
        Assert.Equal(SolicitationStatus.DeliveryFailed, state.Solicitation.Status);
    }

    [Fact]
    public async Task Provider_exception_is_terminal_delivery_uncertain_and_is_not_retried()
    {
        using var fixture = new DispatchFixture(new RecordingNotification
        {
            Exception = new InvalidOperationException("Ambiguous provider outcome")
        });
        fixture.SeedPending();

        Assert.True(await fixture.Worker.ProcessOneAsync(default));
        Assert.False(await fixture.Worker.ProcessOneAsync(default));

        var state = await fixture.StateAsync();
        Assert.Equal(1, fixture.Notification.SendCount);
        Assert.Equal(ProcurementOutboxStatuses.Failed, state.Message.Status);
        Assert.Equal("DELIVERY_UNCERTAIN", state.Message.LastErrorCode);
        Assert.Equal("SUPPLIER_SOLICITATION_DELIVERY_UNCERTAIN", Assert.Single(state.Events).EventType);
    }

    [Fact]
    public async Task A_claim_fenced_twice_does_not_wedge_the_queue()
    {
        // THE POISON LOOP, observed in production on 2026-08-19.
        //
        // Fencing a stale claim writes SUPPLIER_SOLICITATION_DELIVERY_UNCERTAIN, whose
        // idempotency key is message + attempt + type. Fencing does not increment the attempt —
        // a fence is not a delivery attempt — so fencing the same claim twice produced an
        // identical key, violated the unique index, and failed the whole SaveChanges. The
        // message then stayed PROCESSING with an expired lease, the next cycle found the same
        // stale claim, and ProcessOneAsync threw roughly every twelve seconds for ever.
        //
        // The worker handles one message per cycle, so that single row stopped EVERY supplier
        // RFQ and customer quote in the deployment, silently. A healthy solicitation sat behind
        // it in PENDINGDISPATCH and was never touched.
        using var fixture = new DispatchFixture(new RecordingNotification
        {
            Exception = new InvalidOperationException("Ambiguous provider outcome")
        });
        fixture.SeedPending(withSourcingCase: true);

        // First pass fences it and records the events.
        Assert.True(await fixture.Worker.ProcessOneAsync(default));
        var first = await fixture.StateAsync();
        Assert.Equal("DELIVERY_UNCERTAIN", first.Message.LastErrorCode);
        Assert.Contains(first.Events, e => e.EventType == "SUPPLIER_SOLICITATION_DELIVERY_UNCERTAIN");
        // The sourcing-case event too — the one #56 missed, and the reason the loop survived it.
        Assert.Contains(first.Events, e => e.EventType == "SOURCING_CASE_DELIVERY_REVIEW_REQUIRED");
        var firstCount = first.Events.Count;

        // Put it back exactly as the crash left it: PROCESSING, lease expired, attempt
        // unchanged — so fencing computes the SAME idempotency key it already wrote.
        await fixture.ReopenStaleClaimAsync();

        // This must not throw, and must not leave the row PROCESSING for the next cycle.
        var exception = await Record.ExceptionAsync(() => fixture.Worker.ProcessOneAsync(default));
        Assert.Null(exception);

        var after = await fixture.StateAsync();
        Assert.Equal(ProcurementOutboxStatuses.Failed, after.Message.Status);
        Assert.Null(after.Message.LeaseOwner);
        // No event written twice: a second fence is the same fact, for BOTH aggregates.
        Assert.Equal(firstCount, after.Events.Count);
    }

    [Fact]
    public async Task Setup_failure_before_provider_invocation_is_retryable()
    {
        using var fixture = new DispatchFixture(registerNotification: false);
        fixture.SeedPending();

        Assert.True(await fixture.Worker.ProcessOneAsync(default));

        var state = await fixture.StateAsync();
        Assert.Equal(ProcurementOutboxStatuses.Pending, state.Message.Status);
        Assert.Equal("DISPATCH_SETUP_FAILED", state.Message.LastErrorCode);
        Assert.Equal(SolicitationStatus.PendingDispatch, state.Solicitation.Status);
        Assert.Equal("SUPPLIER_SOLICITATION_RETRY_SCHEDULED", Assert.Single(state.Events).EventType);
    }

    [Fact]
    public async Task Stale_processing_claim_is_terminal_delivery_uncertain_without_resend()
    {
        using var fixture = new DispatchFixture();
        fixture.SeedPending(
            status: ProcurementOutboxStatuses.Processing,
            attemptCount: 1,
            updatedOn: DateTime.UtcNow.AddMinutes(-11));

        Assert.True(await fixture.Worker.ProcessOneAsync(default));
        Assert.False(await fixture.Worker.ProcessOneAsync(default));

        var state = await fixture.StateAsync();
        Assert.Equal(0, fixture.Notification.SendCount);
        Assert.Equal(ProcurementOutboxStatuses.Failed, state.Message.Status);
        Assert.Equal("DELIVERY_UNCERTAIN", state.Message.LastErrorCode);
        Assert.Equal(SolicitationStatus.DeliveryFailed, state.Solicitation.Status);
        Assert.Equal("SUPPLIER_SOLICITATION_DELIVERY_UNCERTAIN", Assert.Single(state.Events).EventType);
    }

    [Fact]
    public async Task Concurrent_worker_cannot_claim_message_while_delivery_is_in_flight()
    {
        var notification = new RecordingNotification { PauseDelivery = true };
        using var fixture = new DispatchFixture(notification);
        fixture.SeedPending();

        var first = fixture.Worker.ProcessOneAsync(default);
        await notification.SendStarted.Task.WaitAsync(TestWaits.Liveness);

        var claimed = await fixture.StateAsync();
        Assert.NotNull(claimed.Message.LeaseToken);
        Assert.NotNull(claimed.Message.LeaseUntil);

        Assert.False(await fixture.Worker.ProcessOneAsync(default));
        notification.ReleaseDelivery.TrySetResult();
        Assert.True(await first);

        var state = await fixture.StateAsync();
        Assert.Equal(1, notification.SendCount);
        Assert.Equal(ProcurementOutboxStatuses.Sent, state.Message.Status);
        Assert.Equal(1, state.Message.AttemptCount);
        Assert.Null(state.Message.LeaseToken);
    }

    [Fact]
    public async Task Unconfigured_provider_is_dead_lettered_without_fabricating_delivery()
    {
        using var fixture = new DispatchFixture(deliveryConfiguration: new DisabledDeliveryConfiguration());
        fixture.SeedPending();

        Assert.True(await fixture.Worker.ProcessOneAsync(default));

        var state = await fixture.StateAsync();
        Assert.Equal(0, fixture.Notification.SendCount);
        Assert.Equal(ProcurementOutboxStatuses.DeadLettered, state.Message.Status);
        Assert.Equal("DELIVERY_PROVIDER_NOT_CONFIGURED", state.Message.LastErrorCode);
        Assert.Equal("console", state.Message.ProviderName);
        Assert.Null(state.Message.ProviderReference);
    }

    [Fact]
    public async Task Supplier_blocked_after_queue_is_not_contacted()
    {
        using var fixture = new DispatchFixture();
        fixture.SeedPending();
        await fixture.BlockSupplierAsync();

        Assert.True(await fixture.Worker.ProcessOneAsync(default));

        var state = await fixture.StateAsync();
        Assert.Equal(0, fixture.Notification.SendCount);
        Assert.Equal(ProcurementOutboxStatuses.DeadLettered, state.Message.Status);
        Assert.Equal("SUPPLIER_GOVERNANCE_NOT_READY", state.Message.LastErrorCode);
        Assert.Equal(SolicitationStatus.DeliveryFailed, state.Solicitation.Status);
    }

    [Fact]
    public async Task Provider_timeout_is_terminal_delivery_uncertain_without_resend()
    {
        var notification = new RecordingNotification { PauseDelivery = true };
        using var fixture = new DispatchFixture(notification, TimeSpan.FromMilliseconds(50));
        fixture.SeedPending();

        Assert.True(await fixture.Worker.ProcessOneAsync(default));
        Assert.False(await fixture.Worker.ProcessOneAsync(default));

        var state = await fixture.StateAsync();
        Assert.Equal(1, notification.SendCount);
        Assert.Equal(ProcurementOutboxStatuses.Failed, state.Message.Status);
        Assert.Equal("DELIVERY_TIMEOUT_UNCERTAIN", state.Message.LastErrorCode);
        Assert.Equal("SUPPLIER_SOLICITATION_DELIVERY_UNCERTAIN", Assert.Single(state.Events).EventType);
    }

    [Fact]
    public async Task Mutations_are_executed_inside_the_discovered_tenant_scope()
    {
        using var fixture = new DispatchFixture();
        fixture.SeedPending();

        Assert.True(await fixture.Worker.ProcessOneAsync(default));

        Assert.Contains((long?)null, fixture.ResolvedTenantScopes);
        Assert.Contains(72_001, fixture.ResolvedTenantScopes);
    }

    [Fact]
    public async Task Heartbeat_reports_worker_activity_and_success()
    {
        using var fixture = new DispatchFixture();
        fixture.SeedPending();

        Assert.True(await fixture.Worker.ProcessOneAsync(default));

        Assert.NotNull(fixture.Heartbeat.LastSeen);
        Assert.NotNull(fixture.Heartbeat.LastSuccess);
    }

    [Fact]
    public async Task Readiness_stays_healthy_during_permitted_provider_call()
    {
        var notification = new RecordingNotification { PauseDelivery = true };
        using var fixture = new DispatchFixture(notification, TimeSpan.FromSeconds(5));
        fixture.SeedPending();

        var processing = fixture.Worker.ProcessOneAsync(default);
        await notification.SendStarted.Task.WaitAsync(TestWaits.Liveness);
        var health = await new ProcurementDispatchHealthCheck(fixture.Heartbeat)
            .CheckHealthAsync(new Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckContext());

        Assert.Equal(Microsoft.Extensions.Diagnostics.HealthChecks.HealthStatus.Healthy, health.Status);
        Assert.NotNull(fixture.Heartbeat.ProviderCallDeadline);
        notification.ReleaseDelivery.TrySetResult();
        Assert.True(await processing);
        Assert.Null(fixture.Heartbeat.ProviderCallDeadline);
    }

    [Fact]
    public async Task Readiness_is_unhealthy_after_repeated_cycle_failures()
    {
        var heartbeat = new ProcurementDispatchHeartbeat();
        heartbeat.RecordCycleFailure();
        heartbeat.RecordCycleFailure();
        heartbeat.RecordCycleFailure();

        var health = await new ProcurementDispatchHealthCheck(heartbeat)
            .CheckHealthAsync(new Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckContext());

        Assert.Equal(Microsoft.Extensions.Diagnostics.HealthChecks.HealthStatus.Unhealthy, health.Status);
        Assert.Equal(3, heartbeat.ConsecutiveCycleFailures);
    }

    [Fact]
    public async Task Readiness_is_unhealthy_after_repeated_delivery_failures()
    {
        var heartbeat = new ProcurementDispatchHeartbeat();
        heartbeat.RecordDeliveryFailure();
        heartbeat.RecordDeliveryFailure();
        heartbeat.RecordDeliveryFailure();
        heartbeat.RecordCycleSuccess();

        var health = await new ProcurementDispatchHealthCheck(heartbeat)
            .CheckHealthAsync(new Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckContext());

        Assert.Equal(Microsoft.Extensions.Diagnostics.HealthChecks.HealthStatus.Unhealthy, health.Status);
        Assert.Equal(3, heartbeat.ConsecutiveDeliveryFailures);
    }

    private sealed class DispatchFixture : IDisposable
    {
        private const long Tenant = 72_001;
        public const long Rfq = 72_010;
        public const long SourcingCase = 72_040;
        public const long DemandLine = 72_050;
        public const long RfqItem = 72_060;
        private const long Supplier = 72_020;
        private const long Solicitation = 72_030;
        private const long Message = 72_040;
        private readonly TestDb _database = new();
        private readonly ServiceProvider _provider;
        private readonly TenantScopeAccessor _tenantScope = new();

        public DispatchFixture(
            RecordingNotification? notification = null,
            TimeSpan? providerCallTimeout = null,
            bool registerNotification = true,
            IProcurementDeliveryConfiguration? deliveryConfiguration = null)
        {
            Notification = notification ?? new RecordingNotification();
            var services = new ServiceCollection()
                .AddScoped(_ =>
                {
                    ResolvedTenantScopes.Add(_tenantScope.BusinessUnitId);
                    return _database.ContextFor(_tenantScope.BusinessUnitId);
                });
            if (registerNotification)
                services.AddSingleton<INotificationService>(Notification);
            _provider = services.BuildServiceProvider();
            Heartbeat = new ProcurementDispatchHeartbeat();
            Worker = new ProcurementDispatchWorker(
                _provider.GetRequiredService<IServiceScopeFactory>(),
                NullLogger<ProcurementDispatchWorker>.Instance,
                _tenantScope,
                Heartbeat,
                providerCallTimeout,
                deliveryConfiguration);
        }

        public RecordingNotification Notification { get; }
        public ProcurementDispatchWorker Worker { get; }
        public ProcurementDispatchHeartbeat Heartbeat { get; }
        public List<long?> ResolvedTenantScopes { get; } = [];

        public void SeedPending(
            string? payloadJson = null,
            string status = ProcurementOutboxStatuses.Pending,
            int attemptCount = 0,
            DateTime? updatedOn = null,
            bool withSourcingCase = false)
        {
            using var db = _database.ContextFor(null);
            db.Set<Tenant>().Add(new Tenant
            {
                Id = Tenant,
                Name = "Dispatch Tenant",
                Slug = "dispatch-tenant",
                Status = TenantStatus.Active,
                PrimaryBusinessUnitId = Tenant
            });
            AgentSeed.Rfq(db, Rfq, Tenant, "RFQ-DISPATCH-1");
            AgentSeed.Supplier(db, Supplier, Tenant, "Supplier One", "supplier@example.test");
            var governedSupplier = db.Suppliers.Local.Single(x => x.Id == Supplier);
            governedSupplier.IsActive = true;
            governedSupplier.GovernanceStatus = SupplierGovernanceStatuses.Approved;
            governedSupplier.VerificationStatus = SupplierVerificationStatuses.Verified;
            governedSupplier.ComplianceStatus = SupplierComplianceStatuses.Cleared;
            governedSupplier.RiskStatus = SupplierRiskStatuses.Low;
            governedSupplier.ReadinessStatus = SupplierReadinessStatuses.Ready;
            var now = updatedOn ?? DateTime.UtcNow;
            var seededSolicitation = AgentSeed.Solicitation(
                db, Solicitation, Tenant, Rfq, Supplier, SolicitationStatus.PendingDispatch);

            // A REAL solicitation belongs to a sourcing case, and the default fixture's does not.
            // UpdateSourcingCaseLifecycleAsync returns immediately when SourcingCaseId is null,
            // so half the fencing path — including the sourcing-case event that kept production
            // crash-looping after #56 — is never exercised without this. Opt-in so the tests
            // written against the simpler shape keep working unchanged.
            if (withSourcingCase)
            {
                AgentSeed.RfqItem(db, RfqItem, Rfq, "Dispatch fixture item", 10);
                db.Set<CommercialDemandLine>().Add(new CommercialDemandLine
                {
                    Id = DemandLine, BusinessUnitId = Tenant, RfqId = Rfq, RfqItemId = RfqItem,
                    NexoraSerial = "NXR-DISPATCH", IdentityKey = "dispatch-line",
                    CreatedBy = "tests", CreatedOn = now,
                });
                db.Set<SourcingCase>().Add(new SourcingCase
                {
                    Id = SourcingCase, BusinessUnitId = Tenant, CommercialDemandLineId = DemandLine,
                    RfqId = Rfq, RfqItemId = RfqItem, NexoraSerial = "NXR-DISPATCH",
                    Description = "Dispatch fixture line", RequestedQuantity = 10m,
                    StockQuantity = 0m, UnfulfilledQuantity = 10m, SearchLimit = 10,
                    Status = SourcingCaseStatuses.OutreachReady, NextAction = "Review known suppliers",
                    ShortageDecisionKey = "dispatch-shortage", IdempotencyKey = "dispatch-case",
                    RequestHash = new string('E', 64), CreatedBy = "tests", UpdatedBy = "tests",
                    CreatedOn = now, UpdatedOn = now,
                });
                seededSolicitation.SourcingCaseId = SourcingCase;
            }
            db.ProcurementOutboxMessages.Add(new ProcurementOutboxMessage
            {
                Id = Message,
                BusinessUnitId = Tenant,
                SupplierSolicitationId = Solicitation,
                Status = status,
                PayloadJson = payloadJson ?? JsonSerializer.Serialize(new
                {
                    SolicitationId = Solicitation,
                    BusinessUnitId = Tenant,
                    RfqId = Rfq,
                    ToEmail = "supplier@example.test",
                    SupplierName = "Supplier One",
                    RfqNumber = "RFQ-DISPATCH-1",
                    ItemSummary = "10 x NX-100",
                    DueOn = DateTime.UtcNow.AddDays(3)
                }),
                AttemptCount = attemptCount,
                NextAttemptOn = DateTime.UtcNow.AddMinutes(-1),
                CreatedOn = now,
                UpdatedOn = now
            });
            db.SaveChanges();
        }

        /// <summary>
        /// Returns the outbox row to the state the crash loop left it in: claimed, lease
        /// expired, attempt count unchanged. The next cycle will treat it as a stale claim and
        /// fence it again — with the idempotency key it has already written.
        /// </summary>
        public async Task ReopenStaleClaimAsync()
        {
            await using var db = _database.ContextFor(null);
            var message = await db.ProcurementOutboxMessages.SingleAsync();
            message.Status = ProcurementOutboxStatuses.Processing;
            message.LeaseOwner = "stale-worker";
            message.LeaseToken = Guid.NewGuid();
            message.LeaseUntil = DateTime.UtcNow.AddMinutes(-5);
            message.NextAttemptOn = DateTime.UtcNow.AddMinutes(-1);
            await db.SaveChangesAsync();
        }

        public async Task<(ProcurementOutboxMessage Message, SupplierSolicitation Solicitation, List<ProcurementEvent> Events)> StateAsync()
        {
            await using var db = _database.ContextFor(null);
            return (
                await db.ProcurementOutboxMessages.AsNoTracking().SingleAsync(),
                await db.Set<SupplierSolicitation>().AsNoTracking().SingleAsync(),
                await db.ProcurementEvents.AsNoTracking().ToListAsync());
        }

        public async Task BlockSupplierAsync()
        {
            await using var db = _database.ContextFor(null);
            var supplier = await db.Suppliers.SingleAsync(x => x.Id == Supplier);
            supplier.GovernanceStatus = SupplierGovernanceStatuses.Blocked;
            supplier.ReadinessStatus = SupplierReadinessStatuses.Blocked;
            await db.SaveChangesAsync();
        }

        public void Dispose()
        {
            _provider.Dispose();
            _database.Dispose();
        }
    }

    private sealed class DisabledDeliveryConfiguration : IProcurementDeliveryConfiguration
    {
        public bool IsConfigured => false;
        public string ProviderName => "console";
    }

    internal sealed class RecordingNotification : INotificationService
    {
        public bool Result { get; init; } = true;
        public Exception? Exception { get; init; }
        public bool PauseDelivery { get; init; }
        public int SendCount { get; private set; }
        public RfqToSupplierNotification? LastRequest { get; private set; }
        public TaskCompletionSource SendStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseDelivery { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<bool> SendRfqToSupplierAsync(RfqToSupplierNotification request, CancellationToken ct = default)
            => (await SendRfqToSupplierWithReceiptAsync(request, ct)).Accepted;

        public async Task<NotificationDispatchReceipt> SendRfqToSupplierWithReceiptAsync(
            RfqToSupplierNotification request,
            CancellationToken ct = default)
        {
            SendCount++;
            LastRequest = request;
            SendStarted.TrySetResult();
            if (PauseDelivery) await ReleaseDelivery.Task.WaitAsync(ct);
            if (Exception is not null) throw Exception;
            return Result
                ? new NotificationDispatchReceipt(
                    true,
                    "test",
                    "test-acceptance-reference",
                    DateTimeOffset.UtcNow)
                : new NotificationDispatchReceipt(false, null, null, null);
        }

        public Task<bool> NotifyLeadNeedsReviewAsync(LeadNeedsReviewNotification request, CancellationToken ct = default) => Task.FromResult(true);
        public Task<bool> SendQuoteToBuyerAsync(QuoteToBuyerNotification request, CancellationToken ct = default) => Task.FromResult(true);
        public Task<bool> SendOrderConfirmationAsync(OrderConfirmationNotification request, CancellationToken ct = default) => Task.FromResult(true);
        public Task<bool> NotifyLeadAssignedAsync(LeadAssignedNotification request, CancellationToken ct = default) => Task.FromResult(true);
        public Task<bool> NotifyLeadReassignedAwayAsync(LeadReassignedAwayNotification request, CancellationToken ct = default) => Task.FromResult(true);
        public Task<bool> NotifyDuplicateLeadAsync(DuplicateLeadNotification request, CancellationToken ct = default) => Task.FromResult(true);
    }
}

[Collection(PostgreSqlIntegrationCollection.Name)]
public sealed class ProcurementDispatchWorkerPostgreSqlTests(PostgreSqlTestDatabase database)
{
    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task Explicit_transactions_run_with_retry_strategy_and_tenant_rls()
    {
        var suffix = Random.Shared.Next(100_000, 900_000);
        var tenantId = 88_000_000L + suffix;
        var rfqId = tenantId + 1;
        var supplierId = tenantId + 2;
        var solicitationId = tenantId + 3;
        var messageId = tenantId + 4;
        await using (var seed = database.ContextFor(null))
        {
            seed.Set<Tenant>().Add(new Tenant
            {
                Id = tenantId,
                Name = "Dispatch retry tenant",
                Slug = $"dispatch-retry-{suffix}",
                Status = TenantStatus.Active,
                PrimaryBusinessUnitId = tenantId
            });
            AgentSeed.Rfq(seed, rfqId, tenantId, $"RFQ-RETRY-{suffix}");
            AgentSeed.Supplier(seed, supplierId, tenantId, "Retry Supplier", "retry@example.test");
            var governedSupplier = seed.Suppliers.Local.Single(x => x.Id == supplierId);
            governedSupplier.IsActive = true;
            governedSupplier.GovernanceStatus = SupplierGovernanceStatuses.Approved;
            governedSupplier.VerificationStatus = SupplierVerificationStatuses.Verified;
            governedSupplier.ComplianceStatus = SupplierComplianceStatuses.Cleared;
            governedSupplier.RiskStatus = SupplierRiskStatuses.Low;
            governedSupplier.ReadinessStatus = SupplierReadinessStatuses.Ready;
            AgentSeed.Solicitation(
                seed, solicitationId, tenantId, rfqId, supplierId, SolicitationStatus.PendingDispatch);
            seed.ProcurementOutboxMessages.Add(new ProcurementOutboxMessage
            {
                Id = messageId,
                BusinessUnitId = tenantId,
                SupplierSolicitationId = solicitationId,
                Status = ProcurementOutboxStatuses.Pending,
                PayloadJson = JsonSerializer.Serialize(new
                {
                    SolicitationId = solicitationId,
                    BusinessUnitId = tenantId,
                    RfqId = rfqId,
                    ToEmail = "retry@example.test",
                    SupplierName = "Retry Supplier",
                    RfqNumber = $"RFQ-RETRY-{suffix}",
                    ItemSummary = "1 x RETRY",
                    DueOn = DateTime.UtcNow.AddDays(1)
                }),
                NextAttemptOn = DateTime.UtcNow.AddMinutes(-1),
                CreatedOn = DateTime.UtcNow,
                UpdatedOn = DateTime.UtcNow
            });
            await seed.SaveChangesAsync();
        }

        var tenantScope = new TenantScopeAccessor();
        var notification = new ProcurementDispatchWorkerTests.RecordingNotification();
        await using var provider = new ServiceCollection()
            .AddScoped(_ =>
            {
                var tenantContext = new ScopeBackedTenantContext(tenantScope);
                var options = new DbContextOptionsBuilder<ErpRfqAutomationContext>()
                    .UseNpgsql(database.ConnectionString, npgsql => npgsql.EnableRetryOnFailure())
                    .AddInterceptors(new TenantRlsCommandInterceptor(tenantContext))
                    .Options;
                return new ErpRfqAutomationContext(options, tenantContext);
            })
            .AddSingleton<INotificationService>(notification)
            .BuildServiceProvider();
        var worker = new ProcurementDispatchWorker(
            provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<ProcurementDispatchWorker>.Instance,
            tenantScope,
            new ProcurementDispatchHeartbeat());

        Assert.True(await worker.ProcessOneAsync(default));

        await using var verify = database.ContextFor(null);
        var message = await verify.ProcurementOutboxMessages.AsNoTracking()
            .SingleAsync(x => x.Id == messageId);
        Assert.Equal(ProcurementOutboxStatuses.Sent, message.Status);
        Assert.Equal(1, notification.SendCount);
    }

    private sealed class ScopeBackedTenantContext(ITenantScopeAccessor scope) : ITenantContext
    {
        public long? BusinessUnitId => scope.BusinessUnitId;
    }
}
