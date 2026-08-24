using ERP_RFQ_Automation.CommercialFinance;
using ERP_RFQ_Automation.CommercialRouting;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.MultiTenancy;
using ERP_RFQ_Automation.Platform.Lifecycle;
using ERP_RFQ_Automation.Platform.Models;
using ERP_RFQ_Automation.QuoteDelivery;
using ERP_RFQ_Automation.Services;
using ERP_RFQ_Automation.Sla;
using ERP_RFQ_Automation.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// Suspension enforcement on the paths that never pass through the HTTP pipeline.
///
/// <para>Before these, "suspended" meant "cannot sign in" and nothing else: the mailbox poller
/// kept sending a suspended tenant's documents to an inference endpoint that bills per token, the
/// quote dispatcher kept emailing their PDFs to THEIR customers, and the finance dispatcher kept
/// publishing their events. Each test below pairs a suspended tenant with an active one, because
/// the failure that matters is not "does it skip" — it is "does it skip the right one".</para>
///
/// <para>Every test also asserts DEFERRAL rather than merely absence. Skipping work is only
/// correct if the work survives to be done on reinstatement; a gate that silently dropped a
/// customer's leads would be a data-loss defect introduced in the name of cost control.</para>
/// </summary>
public sealed class TenantWorkGateSuspensionTests
{
    private const long Active = 5_101;
    private const long Suspended = 5_102;

    // ---------------------------------------------------------------------------- the gate

    [Theory]
    [InlineData(TenantStatus.Active, true)]
    [InlineData(TenantStatus.Provisioning, false)]
    [InlineData(TenantStatus.PastDue, false)]
    [InlineData(TenantStatus.Suspended, false)]
    [InlineData(TenantStatus.Archived, false)]
    public async Task The_gate_admits_exactly_what_the_request_path_admits(TenantStatus status, bool expected)
    {
        // Deliberately the same predicate as TenantAccessSnapshot.IsAccessDenied. A background
        // path with a second opinion about what suspension means produces a tenant that can use
        // the API and whose jobs never run, or the reverse.
        using var harness = new TenantWorkGateHarness();
        await harness.SeedTenantAsync(Active, status, $"gate-{status}");

        using var scope = harness.ScopeFactory.CreateScope();
        Assert.Equal(expected, await harness.Gate(scope).MayConsumeResourcesAsync(Active));
    }

    [Fact]
    public async Task A_business_unit_with_no_tenant_row_is_admitted()
    {
        // The contracted fail-open, inherited from ITenantAccessService rather than restated. A
        // background sweep that stopped working because the control plane could not answer would
        // be a worse outage than the leak it was closing.
        using var harness = new TenantWorkGateHarness();
        await harness.SeedTenantAsync(Active, status: null, "legacy");

        using var scope = harness.ScopeFactory.CreateScope();
        Assert.True(await harness.Gate(scope).MayConsumeResourcesAsync(Active));

        var serviceable = await harness.Gate(scope).FilterServiceableAsync([Active]);
        Assert.Equal([Active], serviceable);
    }

    [Fact]
    public async Task Filtering_keeps_the_serviceable_tenants_and_drops_the_rest()
    {
        using var harness = new TenantWorkGateHarness();
        await harness.SeedTenantAsync(Active, TenantStatus.Active, "keep");
        await harness.SeedTenantAsync(Suspended, TenantStatus.Suspended, "drop");

        using var scope = harness.ScopeFactory.CreateScope();
        var serviceable = await harness.Gate(scope).FilterServiceableAsync([Active, Suspended, Active]);

        Assert.Equal([Active], serviceable);
    }

    [Fact]
    public async Task Reinstating_a_tenant_restores_its_background_work()
    {
        // The other half of the contract. Suspension has to be reversible in the workers too, or
        // a reinstated customer stays silently switched off until the process restarts.
        using var harness = new TenantWorkGateHarness();
        await harness.SeedTenantAsync(Active, TenantStatus.Suspended, "reinstate");

        using var scope = harness.ScopeFactory.CreateScope();
        Assert.False(await harness.Gate(scope).MayConsumeResourcesAsync(Active));

        await harness.SetStatusAsync(Active, TenantStatus.Active);
        Assert.True(await harness.Gate(scope).MayConsumeResourcesAsync(Active));
    }

    // ------------------------------------------------------- (a) the AI spend path: mailboxes

    [Fact]
    public async Task A_suspended_tenants_mailbox_is_not_polled_and_no_attempt_is_recorded()
    {
        // The most expensive gate in the product: ProcessConfigAsync resolves ILLMService from its
        // own scope and enqueues every attachment for extraction, so an unpolled mailbox is the
        // difference between a suspended tenant costing nothing and one spending inference tokens
        // on documents nobody will be invoiced for.
        using var harness = new TenantWorkGateHarness();
        await harness.SeedTenantAsync(Active, TenantStatus.Active, "mail-active");
        await harness.SeedTenantAsync(Suspended, TenantStatus.Suspended, "mail-suspended");
        await SeedMailboxAsync(harness, Active);
        await SeedMailboxAsync(harness, Suspended);

        var report = await PollAsync(harness);

        // Exactly one mailbox entered the poll loop. It fails — there is no IMAP server and no
        // ILLMService registered — and that is the point: an ATTEMPT was made for the active
        // tenant and none at all for the suspended one.
        Assert.Equal(1, report.Polled);

        await using var verify = harness.Context();
        var suspended = await verify.EmailConfigurations.IgnoreQueryFilters()
            .SingleAsync(c => c.BusinessUnitId == Suspended);
        var active = await verify.EmailConfigurations.IgnoreQueryFilters()
            .SingleAsync(c => c.BusinessUnitId == Active);

        // Deferral, not a recorded failure. The poll ledger is untouched for the suspended
        // mailbox, so LastSuccessfulPollOn never moves and the lookback window on reinstatement
        // still starts where the tenant left off — the mail is still on their server.
        Assert.Null(suspended.LastPollAttemptOn);
        Assert.Null(suspended.LastPollError);
        Assert.Equal(0, suspended.ConsecutivePollFailures);

        Assert.NotNull(active.LastPollAttemptOn);
    }

    [Fact]
    public async Task A_suspension_longer_than_the_lookback_cap_warns_while_it_is_still_fixable()
    {
        // FINDING R8. The deferral is only lossless while the suspension is shorter than
        // Ingestion:Email:MaxLookbackDays — and the default cap is 30 days while the default
        // retention window before deletion is also 30, so for non-payment, the commonest reason a
        // tenant is suspended, the two are the same length.
        //
        // The cap already warned, but only when the mailbox was polled: AFTER reinstatement, when
        // the mail is already unreachable and widening the setting is too late. The warning now
        // fires on every skipped cycle, while it can still be acted on.
        using var harness = new TenantWorkGateHarness();
        await harness.SeedTenantAsync(Suspended, TenantStatus.Suspended, "lookback-lost");
        await SeedMailboxAsync(harness, Suspended, lastSuccessfulPoll: DateTime.UtcNow.AddDays(-75));

        var log = new CapturingLogger();
        await PollAsync(harness, log);

        var warning = Assert.Single(log.Warnings, message => message.Contains("lookback cap"));
        Assert.Contains("Ingestion:Email:MaxLookbackDays", warning);
        Assert.Contains($"in-{Suspended}@customer.test", warning);

        // 75 days of outage against a 30-day cap: 45 days are already unreachable. Asserted as a
        // floor rather than an exact figure — the service reads its own clock a moment after the
        // seed does, and rounding up a hair over 45 days is the correct answer, not a defect.
        var lost = System.Text.RegularExpressions.Regex.Match(warning, @"(\d+) day\(s\) of it");
        Assert.True(lost.Success, warning);
        Assert.InRange(int.Parse(lost.Groups[1].Value), 45, 46);
    }

    [Fact]
    public async Task A_suspension_inside_the_lookback_cap_is_silent_because_nothing_is_lost()
    {
        // The inverse, so the warning stays worth reading. A short suspension loses nothing and
        // must not train the operator to ignore the line that says something was.
        using var harness = new TenantWorkGateHarness();
        await harness.SeedTenantAsync(Suspended, TenantStatus.Suspended, "lookback-safe");
        await SeedMailboxAsync(harness, Suspended, lastSuccessfulPoll: DateTime.UtcNow.AddDays(-3));

        var log = new CapturingLogger();
        await PollAsync(harness, log);

        Assert.DoesNotContain(log.Warnings, message => message.Contains("lookback cap"));
    }

    [Fact]
    public async Task Without_the_lifecycle_module_every_mailbox_is_still_polled()
    {
        // A deployment that has not registered the gate must behave exactly as it did before it
        // existed. The absence of a cost control is not permission to stop serving customers.
        using var harness = new TenantWorkGateHarness(registerGate: false);
        await harness.SeedTenantAsync(Active, TenantStatus.Active, "nogate-active");
        await harness.SeedTenantAsync(Suspended, TenantStatus.Suspended, "nogate-suspended");
        await SeedMailboxAsync(harness, Active);
        await SeedMailboxAsync(harness, Suspended);

        Assert.Equal(2, (await PollAsync(harness)).Polled);
    }

    // -------------------------------------------- (b) the outbound path: quote delivery email

    [Fact]
    public async Task A_suspended_tenants_quote_is_never_claimed_and_keeps_its_whole_retry_budget()
    {
        // Worse than a cost problem: this sends a quote PDF from the customer's own address to the
        // customer's CLIENT, so a suspended tenant that keeps dispatching means the product is
        // transacting on behalf of an account we have told the customer is switched off.
        using var harness = new TenantWorkGateHarness();
        await harness.SeedTenantAsync(Active, TenantStatus.Active, "quote-active");
        await harness.SeedTenantAsync(Suspended, TenantStatus.Suspended, "quote-suspended");
        await SeedQuoteDeliveryAsync(harness, Active);
        await SeedQuoteDeliveryAsync(harness, Suspended);

        var dispatcher = new QuoteDeliveryDispatcher(
            QuoteDeliveryServices(harness), NullLogger<QuoteDeliveryDispatcher>.Instance, harness.TenantScope);

        Assert.Equal(1, await dispatcher.DispatchOnceAsync(CancellationToken.None));

        await using var verify = harness.Context();
        var suspended = await verify.Set<QuoteDeliveryRequest>().IgnoreQueryFilters()
            .SingleAsync(x => x.BusinessUnitId == Suspended);
        var active = await verify.Set<QuoteDeliveryRequest>().IgnoreQueryFilters()
            .SingleAsync(x => x.BusinessUnitId == Active);

        // Untouched, not failed. No lease was taken and no attempt was spent, so the delivery goes
        // out unchanged on reinstatement with its full MaxAttempts budget intact.
        Assert.Equal(0, suspended.AttemptCount);
        Assert.Null(suspended.LeaseOwner);
        Assert.Null(suspended.CompletedOn);
        Assert.Null(suspended.DeadLetteredOn);

        Assert.Equal(1, active.AttemptCount);
    }

    // ------------------------------------------------------ (c) the finance outbox: deferral

    [Fact]
    public async Task The_finance_outbox_defers_a_suspended_tenant_without_spending_an_attempt()
    {
        // Why the gate filters the CLAIM and not the dispatch: claiming increments AttemptCount.
        // Leasing a suspended tenant's events and then declining to publish them would re-claim
        // every cycle, burn a MaxAttempts budget nobody had spent, and eventually dead-letter a
        // customer's finance events for the crime of being suspended.
        using var harness = new TenantWorkGateHarness();
        await harness.SeedTenantAsync(Active, TenantStatus.Active, "outbox-active");
        await harness.SeedTenantAsync(Suspended, TenantStatus.Suspended, "outbox-suspended");
        await SeedFinanceOutboxAsync(harness, Active);
        await SeedFinanceOutboxAsync(harness, Suspended);

        var claimed = await FinanceDispatcher(harness)
            .ClaimAsync(new FinanceOutboxDispatcherOptions { BatchSize = 50 }, CancellationToken.None);

        var only = Assert.Single(claimed);
        Assert.Equal(Active, only.BusinessUnitId);

        await using var verify = harness.Context();
        var suspended = await verify.Set<FinanceOutboxMessage>().IgnoreQueryFilters()
            .SingleAsync(x => x.BusinessUnitId == Suspended);
        Assert.Equal(0, suspended.AttemptCount);
        Assert.Null(suspended.LeaseOwner);
        Assert.Null(suspended.ProcessedOn);
        Assert.Null(suspended.DeadLetteredOn);
    }

    [Fact]
    public async Task The_finance_outbox_no_longer_claims_every_tenant_in_one_unscoped_batch()
    {
        // The pre-existing defect this closes, independent of suspension: ClaimAsync reads
        // ScopedTenantId ?? 0 and treats 0 as "every tenant", and a background worker has no
        // ambient tenant — so the wildcard was reached by FALLBACK rather than by decision and one
        // claim leased the oldest rows across every customer under the BYPASSRLS pipeline role.
        // Each claim now happens inside a pushed scope, so every message in a batch belongs to the
        // tenant it was claimed for; the dispatcher throws if that is ever untrue.
        using var harness = new TenantWorkGateHarness();
        await harness.SeedTenantAsync(Active, TenantStatus.Active, "outbox-a");
        await harness.SeedTenantAsync(Suspended, TenantStatus.Active, "outbox-b");
        await SeedFinanceOutboxAsync(harness, Active);
        await SeedFinanceOutboxAsync(harness, Suspended);

        var claimed = await FinanceDispatcher(harness)
            .ClaimAsync(new FinanceOutboxDispatcherOptions { BatchSize = 50 }, CancellationToken.None);

        // Both tenants are serviceable here, so both are drained — but through two scoped claims
        // rather than one wildcard sweep.
        Assert.Equal(2, claimed.Count);
        Assert.Equal([Active, Suspended], claimed.Select(c => c.BusinessUnitId).OrderBy(id => id));
    }

    [Fact]
    public async Task The_finance_outbox_honours_its_batch_size_as_a_per_cycle_budget()
    {
        // Claiming a full batch for each of a hundred tenants would turn one configured batch into
        // a hundred. The budget is spent across tenants, not per tenant.
        using var harness = new TenantWorkGateHarness();
        await harness.SeedTenantAsync(Active, TenantStatus.Active, "budget-a");
        await harness.SeedTenantAsync(Suspended, TenantStatus.Active, "budget-b");
        await SeedFinanceOutboxAsync(harness, Active);
        await SeedFinanceOutboxAsync(harness, Active, "second");
        await SeedFinanceOutboxAsync(harness, Suspended);

        var claimed = await FinanceDispatcher(harness)
            .ClaimAsync(new FinanceOutboxDispatcherOptions { BatchSize = 2 }, CancellationToken.None);

        Assert.Equal(2, claimed.Count);
    }

    // ------------------------------------------- (d) the sweeps: SLA and routing reconciliation

    [Fact]
    public async Task The_sla_sweep_visits_the_active_tenant_and_skips_the_suspended_one()
    {
        // A suspended tenant's staff cannot sign in, so an escalation telling them a bid closes
        // tomorrow reaches an inbox nobody can act from. Skipping is safe: every alert is derived
        // from dates and re-evaluated from scratch on the next sweep, so a suspension is a gap in
        // notification rather than a lost record.
        using var harness = new TenantWorkGateHarness();
        await harness.SeedTenantAsync(Active, TenantStatus.Active, "sla-active");
        await harness.SeedTenantAsync(Suspended, TenantStatus.Suspended, "sla-suspended");
        await SeedLeadAsync(harness, Active, 6_001);
        await SeedLeadAsync(harness, Suspended, 6_002);

        var worker = new SlaSweepWorker(
            harness.ScopeFactory, harness.TenantScope, NullLogger<SlaSweepWorker>.Instance);

        // SweepOnceAsync returns the number of business units it swept, AFTER gating.
        Assert.Equal(1, await worker.SweepOnceAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Routing_reconciliation_defers_a_suspended_tenants_leads()
    {
        using var harness = new TenantWorkGateHarness(registerGate: true, configure: services =>
            services.AddScoped<ICommercialRoutingApplicationService, RecordingRoutingService>());
        await harness.SeedTenantAsync(Active, TenantStatus.Active, "route-active");
        await harness.SeedTenantAsync(Suspended, TenantStatus.Suspended, "route-suspended");
        await SeedLeadAsync(harness, Active, 6_101);
        await SeedLeadAsync(harness, Suspended, 6_102);

        RecordingRoutingService.Routed.Clear();
        var worker = new RoutingReconciliationWorker(
            harness.ScopeFactory, harness.TenantScope, NullLogger<RoutingReconciliationWorker>.Instance);
        await worker.ReconcileBatchAsync(CancellationToken.None);

        Assert.Equal([Active], RecordingRoutingService.Routed.Select(r => r.BusinessUnitId).Distinct());

        // Deferral is structural here: the worker's whole selection is "no AssignTo and no
        // LeadRoutingDecision", so a lead not routed this cycle still matches on the next one and
        // is routed on the first cycle after reinstatement, however long that takes.
        await using var verify = harness.Context();
        var deferred = await verify.Leads.IgnoreQueryFilters().SingleAsync(l => l.BusinessUnitId == Suspended);
        Assert.Null(deferred.AssignTo);
        Assert.False(await verify.Set<LeadRoutingDecision>().IgnoreQueryFilters()
            .AnyAsync(d => d.BusinessUnitId == Suspended));
    }

    // ------------------------------------------------- (e) the folder sweep: directory names

    [Fact]
    public async Task The_watched_folder_sweep_drops_a_suspended_tenant_that_its_discovery_cannot_see()
    {
        // This channel discovers tenants from DIRECTORY NAMES under Uploads/Tenants and has never
        // touched the platform database, so it had no way of knowing a tenant was suspended — a
        // directory outlives every lifecycle decision made about its owner. Everything the mailbox
        // poller ingests, this ingests too, including the trip to the inference endpoint.
        using var harness = new TenantWorkGateHarness();
        await harness.SeedTenantAsync(Active, TenantStatus.Active, "folder-active");
        await harness.SeedTenantAsync(Suspended, TenantStatus.Suspended, "folder-suspended");

        var root = Path.Combine(Path.GetTempPath(), "nexora-work-gate", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "Tenants", Active.ToString()));
        Directory.CreateDirectory(Path.Combine(root, "Tenants", Suspended.ToString()));
        try
        {
            var folders = new FolderService(
                harness.Context(), new TenantWorkGateEnvironment(root), NullLogger<FolderService>.Instance,
                llmService: null!, storage: new TenantWorkGateStorage(root));

            // The filesystem offers up both — that is the defect, stated as an assertion.
            var discovered = folders.DiscoverTenantFolderIds();
            Assert.Equal([Active, Suspended], discovered);

            using var scope = harness.ScopeFactory.CreateScope();
            var admitted = await harness.Gate(scope).FilterServiceableAsync(discovered);

            // Deferral: nothing under the suspended tenant's directory is moved or deleted, so the
            // same files are picked up on the first sweep after reinstatement.
            Assert.Equal([Active], admitted);
            Assert.True(Directory.Exists(Path.Combine(root, "Tenants", Suspended.ToString())));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* best effort */ }
        }
    }

    // ------------------------------------------------------------------------------ helpers

    /// <summary>Collects the warnings the poller emits, so the operator-visible half of a skip is
    /// assertable rather than a thing we hope somebody reads.</summary>
    private sealed class CapturingLogger : ILogger<EmailService>
    {
        public List<string> Warnings { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel >= LogLevel.Warning) Warnings.Add(formatter(state, exception));
        }
    }

    private static async Task<MailboxPollReport> PollAsync(
        TenantWorkGateHarness harness, ILogger<EmailService>? logger = null)
    {
        var root = Path.Combine(Path.GetTempPath(), "nexora-work-gate", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var scope = harness.ScopeFactory.CreateScope();
            var service = new EmailService(
                context: harness.Context(),
                env: new TenantWorkGateEnvironment(root),
                logger: logger ?? NullLogger<EmailService>.Instance,
                llmService: null!,
                scopeFactory: harness.ScopeFactory,
                configuration: new ConfigurationBuilder().Build(),
                storage: new TenantWorkGateStorage(root),
                pollerHealth: null,
                workGate: scope.ServiceProvider.GetService<ITenantWorkGate>());
            return await service.FetchAndSaveLeadsAsync();
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* best effort */ }
        }
    }

    private static IServiceScopeFactory QuoteDeliveryServices(TenantWorkGateHarness harness) =>
        harness.ScopeFactory;

    private static FinanceOutboxDispatcherService FinanceDispatcher(TenantWorkGateHarness harness) =>
        new(harness.ScopeFactory,
            new StaticOptionsMonitor<FinanceOutboxDispatcherOptions>(new FinanceOutboxDispatcherOptions()),
            NullLogger<FinanceOutboxDispatcherService>.Instance,
            harness.TenantScope);

    private static async Task SeedMailboxAsync(
        TenantWorkGateHarness harness, long businessUnitId, DateTime? lastSuccessfulPoll = null)
    {
        await using var db = harness.Context();
        db.EmailConfigurations.Add(new EmailConfiguration
        {
            LastSuccessfulPollOn = lastSuccessfulPoll,
            BusinessUnitId = businessUnitId,
            ConfigurationName = $"inbox-{businessUnitId}",
            EmailAddress = $"in-{businessUnitId}@customer.test",
            Protocol = "IMAP",
            // Unroutable by construction: a poll that gets this far must fail fast rather than
            // reach anything real.
            Host = "127.0.0.1",
            Port = 1,
            Username = $"in-{businessUnitId}@customer.test",
            Password = "secret",
            UseSsl = false,
            PollingInterval = 60,
            IsActive = true,
            CreatedOn = DateTime.UtcNow
        });
        await db.SaveChangesAsync();
    }

    private static async Task SeedQuoteDeliveryAsync(TenantWorkGateHarness harness, long businessUnitId)
    {
        await using var db = harness.Context();
        var quoteId = 7_000 + businessUnitId;
        db.Quotes.Add(new Quote
        {
            Id = quoteId,
            QuoteNo = $"Q-{businessUnitId}",
            BusinessUnitId = businessUnitId,
            QuoteDate = DateTime.UtcNow,
            ValidUntil = DateTime.UtcNow.AddDays(30),
            TotalAmount = 100,
            CreatedBy = "tests",
            CreatedDate = DateTime.UtcNow
        });
        db.Set<QuoteDeliveryRequest>().Add(new QuoteDeliveryRequest
        {
            BusinessUnitId = businessUnitId,
            QuoteId = quoteId,
            IdempotencyKey = $"delivery-{businessUnitId}",
            RecipientEmail = "buyer@client.test",
            Subject = "Your quotation",
            Body = "Attached.",
            AttachmentFileName = "quote.pdf",
            RequestedOn = DateTime.UtcNow.AddMinutes(-5),
            AvailableOn = DateTime.UtcNow.AddMinutes(-5),
            // CK_quote_delivery_requests_state requires a positive version.
            Version = 1
        });
        await db.SaveChangesAsync();
    }

    private static async Task SeedFinanceOutboxAsync(
        TenantWorkGateHarness harness, long businessUnitId, string discriminator = "first")
    {
        await using var db = harness.Context();
        db.Set<FinanceOutboxMessage>().Add(new FinanceOutboxMessage
        {
            BusinessUnitId = businessUnitId,
            EventId = Guid.NewGuid(),
            AggregateType = "Invoice",
            AggregateId = businessUnitId,
            AggregateVersion = 1,
            EventType = $"invoice.issued.{discriminator}",
            Payload = "{}",
            SchemaVersion = 1,
            OccurredOn = DateTime.UtcNow.AddMinutes(-5),
            AvailableOn = DateTime.UtcNow.AddMinutes(-5)
        });
        await db.SaveChangesAsync();
    }

    private static async Task SeedLeadAsync(TenantWorkGateHarness harness, long businessUnitId, long leadId)
    {
        await using var db = harness.Context();
        db.Leads.Add(new Lead
        {
            Id = leadId,
            Rfqno = $"GATE-{leadId}",
            BuyersName = "Buyer",
            RecDate = DateTime.UtcNow,
            BidClosingDate = DateTime.UtcNow.AddDays(1),
            LeadSource = "tests",
            CreatedBy = "tests",
            CreatedDate = DateTime.UtcNow,
            BusinessUnitId = businessUnitId
        });
        await db.SaveChangesAsync();
    }

    /// <summary>
    /// Records which tenants routing was attempted for and refuses everything else. Only
    /// <see cref="RouteLeadAsync"/> is on the reconciliation path; the throw is the assertion that
    /// nothing else on this large interface is being reached from a background sweep.
    /// </summary>
    private sealed class RecordingRoutingService : ICommercialRoutingApplicationService
    {
        public static readonly List<(long BusinessUnitId, long LeadId)> Routed = [];

        public Task<RoutingDecisionResponse> RouteLeadAsync(
            long businessUnitId, RouteLeadCommand command, CancellationToken ct)
        {
            lock (Routed) Routed.Add((businessUnitId, command.LeadId));
            // The worker catches and logs; the record above is the observation under test, so
            // there is no need to construct a routing decision the test never inspects.
            throw new NotSupportedException("routing recorded");
        }

        public Task<RoutingDecisionResponse> AssignLeadAsync(long businessUnitId, ManualAssignLeadCommand command, CancellationToken ct) => throw new NotSupportedException();
        public Task<LeadOwnershipResponse> ChangeLeadOwnershipAsync(long businessUnitId, ChangeLeadOwnershipCommand command, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<RoutingOwnerOptionResponse>> GetOwnerOptionsAsync(long businessUnitId, CancellationToken ct) => throw new NotSupportedException();
        public Task<QueuePageResponse> GetQueueAsync(long businessUnitId, WorkItemStatus? status, string? search, bool overdueOnly, int pageNumber, int pageSize, CancellationToken ct) => throw new NotSupportedException();
        public Task<UnassignedQueueItemResponse> ClaimAsync(long businessUnitId, long workItemId, QueueLeaseCommand command, CancellationToken ct) => throw new NotSupportedException();
        public Task<UnassignedQueueItemResponse> ReleaseAsync(long businessUnitId, long workItemId, QueueReleaseCommand command, CancellationToken ct) => throw new NotSupportedException();
        public Task<RoutingDecisionResponse> AssignQueueItemAsync(long businessUnitId, long workItemId, AssignQueueItemCommand command, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<BulkQueueAssignmentResult>> BulkAssignQueueAsync(long businessUnitId, BulkAssignQueueCommand command, CancellationToken ct) => throw new NotSupportedException();
        public Task<CustomerIdentifier> UpsertIdentifierAsync(long businessUnitId, UpsertCustomerIdentifierCommand command, CancellationToken ct) => throw new NotSupportedException();
        public Task<CustomerOwnership> CreateOwnershipAsync(long businessUnitId, CreateCustomerOwnershipCommand command, CancellationToken ct) => throw new NotSupportedException();
        public Task<CustomerRoutingProfileResponse?> GetCustomerProfileAsync(long businessUnitId, long customerId, CancellationToken ct) => throw new NotSupportedException();
        public Task<DefaultLeadOwnerResponse> GetDefaultOwnerAsync(long businessUnitId, CancellationToken ct) => throw new NotSupportedException();
        public Task<DefaultLeadOwnerResponse> SetDefaultOwnerAsync(long businessUnitId, SetDefaultLeadOwnerCommand command, CancellationToken ct) => throw new NotSupportedException();
    }

    private sealed class StaticOptionsMonitor<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue => value;
        public T Get(string? name) => value;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }
}
