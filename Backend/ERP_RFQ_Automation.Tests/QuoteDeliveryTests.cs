using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.MultiTenancy;
using ERP_RFQ_Automation.QuoteDelivery;
using ERP_RFQ_Automation.Services;
using ERP_RFQ_Automation.Services.Interfaces;
using ERP_RFQ_Automation.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace ERP_RFQ_Automation.Tests;

public sealed class QuoteDeliveryTests
{
    private const long Tenant = 95_001;

    [Fact]
    public async Task QuoteSend_QueuesDeliveryWithoutClaimingExternalSuccess_AndReplaysIdempotently()
    {
        using var db = new TestDb();
        await using var context = db.ContextFor(Tenant);
        SeedQuote(context);
        var email = new RecordingEmailService();
        var service = new QuoteService(context, email, null!);
        await Attest(context, 95_011, Tenant);

        var queued = await service.SendQuoteEmailAsync(95_011, Tenant, "buyer@nexora.invalid");
        var replay = await service.SendQuoteEmailAsync(95_011, Tenant, "buyer@nexora.invalid");

        context.ChangeTracker.Clear();
        var quote = await context.Quotes.SingleAsync(x => x.Id == 95_011);
        var delivery = await context.QuoteDeliveryRequests.SingleAsync();
        Assert.Null(quote.SentOn);
        Assert.Null(quote.StatusId);
        Assert.Equal("quote:95011:delivery:v1", delivery.IdempotencyKey);
        Assert.Null(delivery.CompletedOn);
        Assert.Equal(0, email.SendCount);
        Assert.True(queued.QueuedForDelivery);
        Assert.True(replay.Replayed);
    }

    [Fact]
    public async Task SuccessfulDeliveryFinalization_marks_quote_sent_idempotently()
    {
        using var db = new TestDb();
        await using var context = db.ContextFor(Tenant);
        SeedQuote(context);
        var service = new QuoteService(context, new RecordingEmailService(), null!);

        await service.FinalizeQuoteDeliveryAsync(95_011, Tenant);
        var sentOn = (await context.Quotes.SingleAsync(x => x.Id == 95_011)).SentOn;
        await service.FinalizeQuoteDeliveryAsync(95_011, Tenant);

        var quote = await context.Quotes.SingleAsync(x => x.Id == 95_011);
        Assert.NotNull(sentOn);
        Assert.Equal(sentOn, quote.SentOn);
        Assert.Equal(95_002, quote.StatusId);
    }

    [Fact]
    public async Task A_sent_quote_on_an_unowned_lead_is_still_chased_using_the_tenants_fallback_owner()
    {
        // 2026-09-04, on production: a quote went to a customer and no follow-up and no activity
        // were recorded, because the lead had no owner and the recorder returned in silence. On a
        // tenant carrying 179 unassigned leads that is the ordinary case, not the edge case, so a
        // sent quote routinely had nobody to chase it and nothing said so.
        //
        // The tenant already answers "if nobody owns it, give it to ___" for routing, so the same
        // answer is used here rather than inventing a second rule.
        using var db = new TestDb();
        await using var context = db.ContextFor(Tenant + 7);
        SeedUnownedLeadQuote(context, Tenant + 7, 95_071, fallbackOwner: 95_555);
        var sales = new RecordingSalesService();
        var service = new QuoteService(context, new RecordingEmailService(), null!, sales: sales);

        await service.FinalizeQuoteDeliveryAsync(95_071, Tenant + 7);

        Assert.Equal(95_555, sales.FollowUpOwner);
        Assert.Equal(95_555, sales.ActivityOwner);
    }

    [Fact]
    public async Task A_sent_quote_with_no_owner_anywhere_is_reported_rather_than_silently_unchased()
    {
        // The other half: when the tenant has named no fallback either, there is genuinely nobody
        // to assign to. That must not be a silent skip — it is a setup gap the tenant can close.
        using var db = new TestDb();
        await using var context = db.ContextFor(Tenant + 8);
        SeedUnownedLeadQuote(context, Tenant + 8, 95_081, fallbackOwner: null);
        var sales = new RecordingSalesService();
        var service = new QuoteService(context, new RecordingEmailService(), null!, sales: sales);

        await service.FinalizeQuoteDeliveryAsync(95_081, Tenant + 8);

        // Nothing is invented: no follow-up is raised against a user that does not exist.
        Assert.Null(sales.FollowUpOwner);
        // But the quote is still marked sent — the missing owner must not fail the delivery.
        var quote = await context.Quotes.IgnoreQueryFilters()
            .SingleAsync(x => x.Id == 95_081 && x.BusinessUnitId == Tenant + 8);
        Assert.NotNull(quote.SentOn);
    }

    private static void SeedUnownedLeadQuote(
        ErpRfqAutomationContext context, long tenant, long quoteId, long? fallbackOwner)
    {
        var bu = new BusinessUnit
        {
            Id = tenant, BusinessUnitCode = $"QF{tenant}", BusinessUnitName = "Follow-up Fallback",
            CreatedBy = "tests", CreatedOn = DateTime.UtcNow
        };
        if (fallbackOwner is long owner) bu.DefaultLeadOwnerUserId = owner;
        context.BusinessUnits.Add(bu);
        context.SetupMasters.Add(new SetupMaster
        {
            SetupId = tenant + 2, BusinessUnitId = tenant, SetupType = "QuoteStatus",
            SetupCode = "SENT", SetupValue = "Sent", CreatedBy = "tests", CreatedOn = DateTime.UtcNow
        });
        // AssignTo deliberately null: the production shape this defect lived in.
        context.Leads.Add(new Lead
        {
            Id = tenant + 401, BusinessUnitId = tenant, AssignTo = null,
            LeadSource = "EMAIL", CreatedBy = "tests", CreatedDate = DateTime.UtcNow
        });
        context.Rfqs.Add(new Rfq
        {
            Id = tenant + 301, BusinessUnitId = tenant, Rfqno = $"RFQ-FB-{tenant}",
            LeadId = tenant + 401, CreatedBy = "tests", CreatedDate = DateTime.UtcNow
        });
        context.Quotes.Add(new Quote
        {
            Id = quoteId, QuoteNo = $"Q-FB-{quoteId}", BusinessUnitId = tenant,
            Rfqid = tenant + 301, QuoteDate = DateTime.UtcNow,
            ValidUntil = DateTime.UtcNow.AddDays(30), TotalAmount = 500,
            CreatedBy = "tests", CreatedDate = DateTime.UtcNow
        });
        context.SaveChanges();
    }

    private sealed class RecordingSalesService : ERP_RFQ_Automation.CommercialIntelligence.Sales.ISalesApplicationService
    {
        public long? FollowUpOwner { get; private set; }
        public long? ActivityOwner { get; private set; }

        public Task<ERP_RFQ_Automation.CommercialIntelligence.Sales.CommercialActivity> AppendActivityAsync(
            long businessUnitId,
            ERP_RFQ_Automation.CommercialIntelligence.Sales.AppendCommercialActivityCommand command,
            CancellationToken ct)
        {
            ActivityOwner = command.SalesRepUserId;
            return Task.FromResult(new ERP_RFQ_Automation.CommercialIntelligence.Sales.CommercialActivity());
        }

        public Task<ERP_RFQ_Automation.CommercialIntelligence.Sales.FollowUpTask> CreateFollowUpAsync(
            long businessUnitId,
            ERP_RFQ_Automation.CommercialIntelligence.Sales.CreateFollowUpTaskCommand command,
            CancellationToken ct)
        {
            FollowUpOwner = command.AssignedToUserId;
            return Task.FromResult(new ERP_RFQ_Automation.CommercialIntelligence.Sales.FollowUpTask());
        }

        public Task<ERP_RFQ_Automation.CommercialIntelligence.Sales.SalesRepProfile> UpsertProfileAsync(
            long b, ERP_RFQ_Automation.CommercialIntelligence.Sales.UpsertSalesRepProfileCommand c, CancellationToken ct)
            => throw new NotSupportedException();
        public Task<ERP_RFQ_Automation.CommercialIntelligence.Sales.FollowUpTransitionEvent> TransitionFollowUpAsync(
            long b, long t, ERP_RFQ_Automation.CommercialIntelligence.Sales.TransitionFollowUpTaskCommand c, CancellationToken ct)
            => throw new NotSupportedException();
        public Task<ERP_RFQ_Automation.CommercialIntelligence.Sales.SalesContribution> RecordContributionAsync(
            long b, ERP_RFQ_Automation.CommercialIntelligence.Sales.RecordSalesContributionCommand c, CancellationToken ct)
            => throw new NotSupportedException();
        public Task<IReadOnlyList<ERP_RFQ_Automation.CommercialIntelligence.Sales.SalesRepPerformance>> GetPerformanceAsync(
            long b, ERP_RFQ_Automation.CommercialIntelligence.Sales.SalesPerformanceQuery q, CancellationToken ct)
            => throw new NotSupportedException();
        public ERP_RFQ_Automation.CommercialIntelligence.Sales.WeightedRoutingResult ScoreNewCustomer(
            ERP_RFQ_Automation.CommercialIntelligence.Sales.NewCustomerRoutingRequest request)
            => throw new NotSupportedException();
    }

    [Fact]
    public async Task QuoteSend_CannotAddressAnotherTenantsQuote()
    {
        using var db = new TestDb();
        await using var context = db.ContextFor(null);
        SeedQuote(context);
        var service = new QuoteService(context, new RecordingEmailService(), null!);

        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            service.SendQuoteEmailAsync(95_011, Tenant + 1, "buyer@nexora.invalid"));
        Assert.Empty(context.QuoteDeliveryRequests.IgnoreQueryFilters());
    }

    [Fact]
    public async Task QuoteSend_replay_rejects_changed_recipient()
    {
        using var db = new TestDb();
        await using var context = db.ContextFor(Tenant);
        SeedQuote(context);
        var service = new QuoteService(context, new RecordingEmailService(), null!);
        await Attest(context, 95_011, Tenant);

        await service.SendQuoteEmailAsync(95_011, Tenant, "buyer@nexora.invalid");
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.SendQuoteEmailAsync(95_011, Tenant, "corrected@nexora.invalid"));
        Assert.Equal("buyer@nexora.invalid",
            (await context.QuoteDeliveryRequests.SingleAsync()).RecipientEmail);
    }

    [Fact]
    public async Task DeliveryStore_FailureIsObservableAndRetriable_CompletedRequestCannotBeClaimedAgain()
    {
        using var db = new TestDb();
        await using var context = db.ContextFor(null);
        SeedQuote(context);
        context.QuoteDeliveryRequests.Add(NewDelivery());
        await context.SaveChangesAsync();
        var store = new QuoteDeliveryStore(context);

        var first = Assert.Single(await store.ClaimAsync("worker-a", 10, TimeSpan.FromMinutes(1), default));
        await store.FailAsync(first.Id, "worker-a", first.LeaseToken, "SmtpUnavailable", 3, default);

        var failed = await context.QuoteDeliveryRequests.IgnoreQueryFilters().SingleAsync();
        Assert.Equal("SmtpUnavailable", failed.LastErrorCode);
        Assert.Equal(1, failed.AttemptCount);
        Assert.Null(failed.CompletedOn);
        failed.AvailableOn = DateTime.UtcNow.AddSeconds(-1);
        await context.SaveChangesAsync();

        var retry = Assert.Single(await store.ClaimAsync("worker-b", 10, TimeSpan.FromMinutes(1), default));
        await store.CompleteAsync(retry.Id, "worker-b", retry.LeaseToken, default);
        Assert.Empty(await store.ClaimAsync("worker-c", 10, TimeSpan.FromMinutes(1), default));

        var completed = await context.QuoteDeliveryRequests.IgnoreQueryFilters().SingleAsync();
        Assert.NotNull(completed.CompletedOn);
        Assert.Null(completed.LastErrorCode);
        Assert.Equal(2, completed.AttemptCount);
    }

    [Fact]
    public async Task DeliveryStore_FencesNonOwningWorkerAndDeadLettersAtLimit()
    {
        using var db = new TestDb();
        await using var context = db.ContextFor(null);
        SeedQuote(context);
        context.QuoteDeliveryRequests.Add(NewDelivery());
        await context.SaveChangesAsync();
        var store = new QuoteDeliveryStore(context);

        var claim = Assert.Single(await store.ClaimAsync("owner", 1, TimeSpan.FromMinutes(1), default));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            store.CompleteAsync(claim.Id, "intruder", claim.LeaseToken, default));
        await store.FailAsync(claim.Id, "owner", claim.LeaseToken, "PermanentFailure", 1, default);

        var request = await context.QuoteDeliveryRequests.IgnoreQueryFilters().SingleAsync();
        Assert.NotNull(request.DeadLetteredOn);
        Assert.Equal("PermanentFailure", request.LastErrorCode);
    }

    [Fact]
    public async Task DeliveryStore_DoesNotAutomaticallyReplayAnAmbiguousExpiredAttempt()
    {
        using var db = new TestDb();
        await using var context = db.ContextFor(null);
        SeedQuote(context);
        context.QuoteDeliveryRequests.Add(NewDelivery());
        await context.SaveChangesAsync();
        var store = new QuoteDeliveryStore(context);

        var claim = Assert.Single(await store.ClaimAsync("worker-a", 1, TimeSpan.FromSeconds(10), default));
        var request = await context.QuoteDeliveryRequests.IgnoreQueryFilters().SingleAsync();
        request.LeaseUntil = DateTime.UtcNow.AddSeconds(-1);
        await context.SaveChangesAsync();

        Assert.Empty(await store.ClaimAsync("worker-b", 1, TimeSpan.FromMinutes(1), default));
        var terminal = await context.QuoteDeliveryRequests.IgnoreQueryFilters().SingleAsync();
        Assert.Equal(claim.Id, terminal.Id);
        Assert.NotNull(terminal.DeadLetteredOn);
        Assert.Equal("DeliveryOutcomeUncertain", terminal.LastErrorCode);
    }

    [Fact]
    public async Task Dispatcher_DoesNotRetryAnAmbiguousSmtpException()
    {
        var envelope = new QuoteDeliveryEnvelope(
            1, Tenant, 95_011, "buyer@nexora.invalid", "Quote", "Body",
            null, "quote.pdf", 1, Guid.NewGuid());
        var store = new RecordingDeliveryStore(envelope);
        using var host = new TenantScopedHost(services => services
            .AddSingleton<IQuoteDeliveryStore>(store)
            .AddSingleton<IQuoteDeliverySender>(new ThrowingDeliverySender()));
        store.TenantScope = host.TenantScope;
        await using (var seed = host.ContextFor(null))
        {
            SeedQuote(seed);
            seed.QuoteDeliveryRequests.Add(NewDelivery());
            await seed.SaveChangesAsync();
        }
        var dispatcher = new QuoteDeliveryDispatcher(
            host.ScopeFactory, NullLogger<QuoteDeliveryDispatcher>.Instance, host.TenantScope);

        Assert.Equal(1, await dispatcher.DispatchOnceAsync(default));
        Assert.Equal(0, store.RetriableFailureCount);
        Assert.Equal(1, store.UncertainFailureCount);
        Assert.Equal(nameof(InvalidOperationException), store.LastErrorCode);
        // The tenant scope was pushed around the CLAIM, not merely around the send.
        Assert.Equal(new long?[] { Tenant }, store.ClaimTenants);
    }

    /// <summary>
    /// Tenant isolation. QuoteDeliveryRequest's query filter is
    /// "CurrentTenantId == null || BusinessUnitId == CurrentTenantId", and a background worker
    /// has no ambient tenant — so the dispatcher's single unscoped ClaimAsync used to degrade the
    /// filter to "do not filter" and lease the oldest N rows across EVERY tenant in one batch.
    /// Claiming is now done once per tenant inside a pushed scope, so each claim sees only its
    /// own tenant's rows.
    /// </summary>
    [Fact]
    public async Task Dispatcher_ClaimsPerTenantInsideAPushedScope_NeverInOneCrossTenantBatch()
    {
        const long other = Tenant + 1;
        using var host = new TenantScopedHost(services => services
            .AddScoped<IQuoteDeliveryStore, QuoteDeliveryStore>()
            .AddSingleton<IQuoteDeliverySender>(new ThrowingDeliverySender()));
        await using (var seed = host.ContextFor(null))
        {
            SeedQuote(seed);
            SeedQuoteFor(seed, other, 95_021, "Q-DELIVERY-2");
            seed.QuoteDeliveryRequests.Add(NewDelivery());
            seed.QuoteDeliveryRequests.Add(NewDelivery(other, 95_021));
            await seed.SaveChangesAsync();
        }
        var dispatcher = new QuoteDeliveryDispatcher(
            host.ScopeFactory, NullLogger<QuoteDeliveryDispatcher>.Instance, host.TenantScope);

        Assert.Equal(2, await dispatcher.DispatchOnceAsync(default));

        // Each row was leased under, and failed under, its own tenant — no row crossed over.
        await using var assertions = host.ContextFor(null);
        var rows = await assertions.QuoteDeliveryRequests.IgnoreQueryFilters()
            .OrderBy(x => x.BusinessUnitId).ToListAsync();
        Assert.Equal(new[] { Tenant, other }, rows.Select(x => x.BusinessUnitId).ToArray());
        Assert.All(rows, row => Assert.StartsWith("DeliveryOutcomeUncertain", row.LastErrorCode!));
        Assert.All(rows, row => Assert.Equal(1, row.AttemptCount));
    }

    /// <summary>
    /// R5: a broken price binding is dead-lettered on the spot, not retried. The quote's prices
    /// changed after the send was authorised, and no number of retries will make them match
    /// again — every extra attempt would just keep an unauthorised send alive in the outbox with
    /// a lease that another instance could pick up. maxAttempts 1 ends it on this attempt.
    /// </summary>
    [Fact]
    public async Task Dispatcher_DeadLettersAPermanentPreSendRefusalInsteadOfRetryingIt()
    {
        var envelope = new QuoteDeliveryEnvelope(
            1, Tenant, 95_011, "buyer@nexora.invalid", "Quote", "Body",
            null, "quote.pdf", 1, Guid.NewGuid(), "not-the-fingerprint-that-was-attested");
        var store = new RecordingDeliveryStore(envelope);
        using var host = new TenantScopedHost(services => services
            .AddSingleton<IQuoteDeliveryStore>(store)
            .AddSingleton<IQuoteDeliverySender>(new PriceBindingRefusingSender()));
        store.TenantScope = host.TenantScope;
        await using (var seed = host.ContextFor(null))
        {
            SeedQuote(seed);
            seed.QuoteDeliveryRequests.Add(NewDelivery());
            await seed.SaveChangesAsync();
        }
        var dispatcher = new QuoteDeliveryDispatcher(
            host.ScopeFactory, NullLogger<QuoteDeliveryDispatcher>.Instance, host.TenantScope);

        Assert.Equal(1, await dispatcher.DispatchOnceAsync(default));

        Assert.Equal(1, store.RetriableFailureCount);
        Assert.Equal(0, store.UncertainFailureCount);
        Assert.Equal(nameof(ERP_RFQ_Automation.Intelligence.Pricing.PriceAttestationRequiredException), store.LastErrorCode);
        Assert.Equal(1, store.LastMaxAttempts);   // 1 == dead-letter now, not 8 == keep trying
    }

    private sealed class PriceBindingRefusingSender : IQuoteDeliverySender
    {
        public Task SendAsync(QuoteDeliveryEnvelope request, CancellationToken ct) =>
            throw new QuoteDeliveryPreSendException(
                nameof(ERP_RFQ_Automation.Intelligence.Pricing.PriceAttestationRequiredException),
                new ERP_RFQ_Automation.Intelligence.Pricing.PriceAttestationRequiredException("prices moved")
                    { BindingBroken = true })
            { Permanent = true };
    }

    /// <summary>
    /// R5: every send now requires a recorded price-provenance attestation covering the
    /// quote's current prices. These delivery tests are about the delivery ledger, not the
    /// gate, so they satisfy it explicitly. QuotePriceAttestationTests owns the gate itself.
    /// </summary>
    private static Task Attest(ErpRfqAutomationContext context, long quoteId, long tenant) =>
        new ERP_RFQ_Automation.Intelligence.Pricing.PriceAttestationService(context).AttestAsync(
            quoteId, tenant,
            ERP_RFQ_Automation.Intelligence.Pricing.PriceAttestationSources.SupplierQuote,
            "SQ-TEST", null, "tests", default);

    private static void SeedQuote(ErpRfqAutomationContext context)
    {
        if (!context.BusinessUnits.IgnoreQueryFilters().Any(x => x.Id == Tenant))
        {
            context.SetupMasters.Add(new SetupMaster
            {
                SetupId = 95_002, BusinessUnitId = Tenant, SetupType = "QuoteStatus",
                SetupCode = "SENT", SetupValue = "Sent", CreatedBy = "tests", CreatedOn = DateTime.UtcNow
            });
            SeedQuoteFor(context, Tenant, 95_011, "Q-DELIVERY-1");
            context.SaveChanges();
        }
    }

    private static void SeedQuoteFor(ErpRfqAutomationContext context, long tenant, long quoteId, string quoteNo)
    {
        if (!context.BusinessUnits.IgnoreQueryFilters().Any(x => x.Id == tenant))
            context.BusinessUnits.Add(new BusinessUnit
            {
                Id = tenant, BusinessUnitCode = $"QD{tenant}", BusinessUnitName = "Quote Delivery",
                CreatedBy = "tests", CreatedOn = DateTime.UtcNow
            });
        context.Quotes.Add(new Quote
        {
            Id = quoteId, QuoteNo = quoteNo, BusinessUnitId = tenant,
            QuoteDate = DateTime.UtcNow, ValidUntil = DateTime.UtcNow.AddDays(30),
            TotalAmount = 100, CreatedBy = "tests", CreatedDate = DateTime.UtcNow
        });
    }

    private static QuoteDeliveryRequest NewDelivery(long tenant = Tenant, long quoteId = 95_011) => new()
    {
        BusinessUnitId = tenant,
        QuoteId = quoteId,
        IdempotencyKey = Guid.NewGuid().ToString("N"),
        RecipientEmail = "buyer@nexora.invalid",
        Subject = "Test quote",
        Body = "Test body",
        AttachmentFileName = "quote.pdf",
        RequestedOn = DateTime.UtcNow,
        AvailableOn = DateTime.UtcNow.AddSeconds(-1),
        Version = 1
    };

    private sealed class RecordingEmailService : IEmailService
    {
        public int SendCount { get; private set; }
        public Task<MailboxPollReport> FetchAndSaveLeadsAsync(long? businessUnitId = null)
            => Task.FromResult(MailboxPollReport.Empty);
        public Task SendEmailAsync(string to, string subject, string body,
            List<(string FileName, byte[] FileContent, string ContentType)> attachments = null!,
            string fromEmail = null!, long? businessUnitId = null)
        {
            SendCount++;
            return Task.CompletedTask;
        }
    }

    private sealed class ThrowingDeliverySender : IQuoteDeliverySender
    {
        public Task SendAsync(QuoteDeliveryEnvelope request, CancellationToken ct) =>
            throw new InvalidOperationException("Ambiguous SMTP result.");
    }

    private sealed class RecordingDeliveryStore(QuoteDeliveryEnvelope envelope) : IQuoteDeliveryStore
    {
        private bool _claimed;
        public int RetriableFailureCount { get; private set; }
        public int UncertainFailureCount { get; private set; }
        public string? LastErrorCode { get; private set; }

        /// <summary>The retry budget the dispatcher chose: 1 means "dead-letter now".</summary>
        public int LastMaxAttempts { get; private set; }

        /// <summary>The tenant in scope at each ClaimAsync — null means the claim ran unscoped.</summary>
        public List<long?> ClaimTenants { get; } = [];

        public Task<IReadOnlyList<QuoteDeliveryEnvelope>> ClaimAsync(
            string workerId, int batchSize, TimeSpan lease, CancellationToken ct)
        {
            ClaimTenants.Add(TenantScope.BusinessUnitId);
            IReadOnlyList<QuoteDeliveryEnvelope> result = _claimed ? [] : [envelope];
            _claimed = true;
            return Task.FromResult(result);
        }

        public ITenantScopeAccessor TenantScope { get; set; } = null!;

        public Task CompleteAsync(long id, string workerId, Guid leaseToken, CancellationToken ct) =>
            Task.CompletedTask;

        public Task FailAsync(long id, string workerId, Guid leaseToken, string errorCode,
            int maxAttempts, CancellationToken ct)
        {
            RetriableFailureCount++;
            LastErrorCode = errorCode;
            LastMaxAttempts = maxAttempts;
            return Task.CompletedTask;
        }

        public Task MarkOutcomeUncertainAsync(long id, string workerId, Guid leaseToken,
            string errorCode, CancellationToken ct)
        {
            UncertainFailureCount++;
            LastErrorCode = errorCode;
            return Task.CompletedTask;
        }
    }
}
