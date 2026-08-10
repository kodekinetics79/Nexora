using ERP_RFQ_Automation.Agent.Models;
using ERP_RFQ_Automation.Intelligence.Pricing;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Sla;
using ERP_RFQ_Automation.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace ERP_RFQ_Automation.Tests.Intelligence.Pricing;

/// <summary>
/// The below-floor guard is the OTHER writer of <see cref="SlaEvent"/> — the SLA sweep is the
/// first — and it writes the "already escalated" stamp that stops the sweep re-sending an
/// escalation the guard has just sent by hand.
///
/// It was missed when the send-once ledger moved to a claim key: <c>SlaEvent.DedupKey</c> carries
/// an UNFILTERED UNIQUE index (UX_SlaEvents_BU_DedupKey) and
/// <see cref="SlaSweepWorker.TryClaimEventAsync"/> matches on that column ALONE, so the guard's
/// row — persisted with the property default of "" — was
///
///   * invisible to the sweep, which then re-sent the identical escalation to the requester's
///     manager on its next 5-minute tick, and
///   * a duplicate-key collision for the SECOND below-floor deadline-buffer hold in the same
///     tenant (every such row shared the key ""), whose 23505 the guard's bare catch swallowed,
///     leaving a poisoned Added entity on the request-scoped DbContext.
///
/// Neither consequence was reachable from any existing test: this class had ZERO coverage.
/// </summary>
public sealed class BelowFloorGuardEscalationTests
{
    private const long Tenant = 96_001;
    private const long OtherTenant = 96_002;
    private const long RfqId = 96_010;
    private const long RequesterId = 96_020;
    private const long ManagerId = 96_021;
    private const string RequesterEmail = "rep@acme.test";
    private const string ManagerEmail = "boss@acme.test";
    private const string OtherRequesterEmail = "rep@other.test";

    /// <summary>The awarded supplier's landed unit cost, in the seeded tenant currency.</summary>
    private const decimal LandedCost = 100m;

    /// <summary>What the rep put on the quote — 20% under the cost of the goods.</summary>
    private const decimal QuotedPrice = 80m;

    [Fact]
    public async Task An_immediate_escalation_stamps_a_real_dedup_key_the_sweep_can_see()
    {
        using var database = NewDatabase();
        var notifications = new CapturingNotifications();

        var approval = await CreateHoldAsync(database, notifications, Tenant, RfqId);

        // Requester + manager, exactly as the sweep's escalation would have done.
        Assert.Equal([RequesterEmail, ManagerEmail], notifications.Sent.Select(x => x.ToEmail).ToArray());

        await using var verify = database.ContextFor(Tenant);
        var stamp = Assert.Single(await verify.Set<SlaEvent>().ToListAsync());
        var entityKey = BitConverter.ToInt64(approval.Id.ToByteArray(), 0);

        Assert.Equal(SlaEvent.BuildDedupKey("approval", entityKey, "escalated"), stamp.DedupKey);
        Assert.NotEqual(string.Empty, stamp.DedupKey);
        Assert.Equal("approval", stamp.EntityType);
        Assert.Equal("escalated", stamp.Level);
        Assert.Equal(entityKey, stamp.EntityId);
    }

    [Fact]
    public async Task The_stamp_stops_the_sweep_re_sending_the_same_escalation()
    {
        using var database = NewDatabase();
        var approval = await CreateHoldAsync(database, new CapturingNotifications(), Tenant, RfqId);
        var entityKey = BitConverter.ToInt64(approval.Id.ToByteArray(), 0);

        // Verbatim what SlaSweepWorker does before escalating a pending approval. With the empty
        // key this returned a fresh claim, and the requester's manager was mailed a second time
        // within one 5-minute tick.
        await using var sweep = database.ContextFor(Tenant);
        var claim = await SlaSweepWorker.TryClaimEventAsync(
            sweep, Tenant, "approval", entityKey, "escalated", null, default);

        Assert.Null(claim);
        Assert.Equal(1, await sweep.Set<SlaEvent>().CountAsync());
    }

    [Fact]
    public async Task A_second_hold_in_the_same_tenant_neither_throws_nor_double_escalates()
    {
        using var database = NewDatabase();
        var notifications = new CapturingNotifications();

        // THE 23505. Both rows carried DedupKey "", so the second insert violated
        // UX_SlaEvents_BU_DedupKey. The exception was swallowed by the guard's catch — no stamp,
        // a poisoned Added entity left on the request-scoped DbContext to fail the NEXT
        // SaveChanges, and the sweep free to re-escalate both holds.
        var first = await CreateHoldAsync(database, notifications, Tenant, RfqId);
        var second = await CreateHoldAsync(database, notifications, Tenant, RfqId);

        Assert.NotEqual(first.Id, second.Id);
        Assert.Equal(4, notifications.Sent.Count); // two recipients per hold, once each

        await using var verify = database.ContextFor(Tenant);
        var stamps = await verify.Set<SlaEvent>().ToListAsync();
        Assert.Equal(2, stamps.Count);
        Assert.Equal(2, stamps.Select(x => x.DedupKey).Distinct().Count());
        Assert.DoesNotContain(stamps, x => string.IsNullOrEmpty(x.DedupKey));

        // Both approvals were persisted; neither hold was lost to the collision.
        Assert.Equal(2, await verify.Set<AgentApproval>().CountAsync());
    }

    [Fact]
    public async Task A_repeated_escalation_of_the_same_approval_leaves_the_context_usable()
    {
        using var database = NewDatabase();
        var notifications = new CapturingNotifications();

        await using var context = database.ContextFor(Tenant);
        var check = await DetectAsync(database, Tenant, RfqId);
        var guard = new BelowFloorGuard(context, new StubPricingEngine(), notifications,
            NullLogger<BelowFloorGuard>.Instance);

        // Same approval id escalated twice on ONE context: the collision must read as "already
        // escalated" (detached, no throw), not poison the DbContext for everything that follows.
        var approval = await guard.CreateApplyPricingHoldAsync(
            RfqId, Tenant, check, RequesterId, RequesterEmail, default);
        context.Set<SlaEvent>().Remove(await context.Set<SlaEvent>().SingleAsync());
        await context.SaveChangesAsync();
        var replay = await SlaSweepWorker.InsertClaimAsync(context, Tenant, "approval",
            BitConverter.ToInt64(approval.Id.ToByteArray(), 0), "escalated",
            SlaEvent.BuildDedupKey("approval", BitConverter.ToInt64(approval.Id.ToByteArray(), 0), "escalated"),
            default);
        Assert.NotNull(replay);

        var loser = await SlaSweepWorker.InsertClaimAsync(context, Tenant, "approval",
            BitConverter.ToInt64(approval.Id.ToByteArray(), 0), "escalated",
            SlaEvent.BuildDedupKey("approval", BitConverter.ToInt64(approval.Id.ToByteArray(), 0), "escalated"),
            default);

        Assert.Null(loser);
        // The context still works: a poisoned Added entity would fail this save.
        var next = await guard.CreateApplyPricingHoldAsync(RfqId, Tenant, check, RequesterId, RequesterEmail, default);
        Assert.NotEqual(Guid.Empty, next.Id);
    }

    [Fact]
    public async Task The_same_approval_key_in_another_tenant_is_not_blocked()
    {
        using var database = NewDatabase();
        var notifications = new CapturingNotifications();

        await CreateHoldAsync(database, notifications, Tenant, RfqId, RequesterId, RequesterEmail);
        await CreateHoldAsync(database, notifications, OtherTenant, RfqId + 1,
            RequesterId + 100, OtherRequesterEmail);

        await using var verify = database.ContextFor(null);
        var stamps = await verify.Set<SlaEvent>().IgnoreQueryFilters().ToListAsync();
        Assert.Equal(2, stamps.Count);
        Assert.Contains(stamps, x => x.BusinessUnitId == Tenant);
        Assert.Contains(stamps, x => x.BusinessUnitId == OtherTenant);
    }

    // ------------------------------------------------------------------ harness

    private static async Task<AgentApproval> CreateHoldAsync(
        TestDb database, CapturingNotifications notifications, long tenant, long rfqId,
        long? requesterId = null, string? requesterEmail = null)
    {
        var check = await DetectAsync(database, tenant, rfqId);
        await using var context = database.ContextFor(tenant);
        var guard = new BelowFloorGuard(context, new StubPricingEngine(), notifications,
            NullLogger<BelowFloorGuard>.Instance);
        return await guard.CreateApplyPricingHoldAsync(rfqId, tenant, check,
            requesterId ?? RequesterId, requesterEmail ?? RequesterEmail, default);
    }

    /// <summary>
    /// The check these tests escalate is produced by the REAL detector against the REAL pricing
    /// engine, not hand-built.
    ///
    /// <para>It used to be <c>new BelowFloorCheck { Lines = [new BelowFloorLine(1, 80m, 100m, 20m)] }</c>,
    /// which meant every test in this class passed while <c>PricingEngine</c> never assigned a
    /// floor at all and <c>CheckQuoteSendAsync</c> could not return a single line in production.
    /// The escalation machinery was exercised over a breach the system was structurally incapable
    /// of finding. Cut the floor assignment in <c>PricingEngine</c> and the assertion below fires
    /// before any escalation is ever attempted.</para>
    ///
    /// <para>The hold itself is still created through <see cref="StubPricingEngine"/>, because
    /// re-pricing on the hold path would be a second, different defect.</para>
    /// </summary>
    private static async Task<BelowFloorCheck> DetectAsync(TestDb database, long tenant, long rfqId)
    {
        await using var context = database.ContextFor(tenant);
        var guard = new BelowFloorGuard(context, new PricingEngine(context, NullLogger<PricingEngine>.Instance),
            new CapturingNotifications(), NullLogger<BelowFloorGuard>.Instance);

        var check = await guard.CheckQuoteSendAsync(QuoteId(rfqId), tenant, default);

        Assert.True(check.IsBelowFloor, "the detector found no below-floor line to escalate");
        var line = Assert.Single(check.Lines);
        Assert.Equal(QuotedPrice, line.UnitPrice);
        Assert.Equal(LandedCost, line.FloorUnitPrice);
        return check;
    }

    // Ids for the priced graph, derived from the RFQ so the two seeded tenants never collide.
    private static long CurrencyId(long rfqId) => rfqId + 1;
    private static long RfqItemId(long rfqId) => rfqId + 2;
    private static long QuoteId(long rfqId) => rfqId + 3;
    private static long QuoteItemId(long rfqId) => rfqId + 4;
    private static long DecisionId(long rfqId) => rfqId + 5;

    private static TestDb NewDatabase()
    {
        var database = new TestDb();
        using var seed = database.ContextFor(null);
        // CustomerQuoteSourcingDecision carries seven composite foreign keys into the sourcing
        // aggregate. Standing that chain up would make this class about the sourcing fixture
        // rather than about the escalation, so referential enforcement is stood down for the
        // seed exactly as Gate8GrossMarginTests does.
        seed.Database.ExecuteSqlRaw("PRAGMA foreign_keys = OFF");
        SeedTenant(seed, Tenant, RfqId, RequesterId, ManagerId, RequesterEmail, ManagerEmail);
        // Users.Email is globally unique, so the second tenant needs its own addresses.
        SeedTenant(seed, OtherTenant, RfqId + 1, RequesterId + 100, ManagerId + 100,
            OtherRequesterEmail, "boss@other.test");
        seed.SaveChanges();
        return database;
    }

    private static void SeedTenant(ErpRfqAutomationContext context, long tenant, long rfqId,
        long requesterId, long managerId, string requesterEmail, string managerEmail)
    {
        Seed.EnsureBusinessUnit(context, tenant);

        // Bid closes in 2 hours: inside the default 12-hour DeadlineBufferHours, which is what
        // makes the hold notify immediately instead of waiting for the sweep.
        context.Rfqs.Add(new Rfq
        {
            Id = rfqId,
            Rfqno = $"RFQ-{rfqId}",
            RecDate = DateTime.UtcNow.AddDays(-3),
            BidClosingDate = DateTime.UtcNow.AddHours(2),
            BusinessUnitId = tenant,
            CreatedBy = "qa",
            CreatedDate = DateTime.UtcNow.AddDays(-3)
        });
        context.Users.AddRange(
            User(managerId, tenant, managerEmail, "Boss", null),
            User(requesterId, tenant, requesterEmail, "Rep", managerId));

        // A real priced graph for the detector to find a breach in: one RFQ line, one Customer
        // Quote line priced at 80, and the governed award that says the goods cost 100.
        context.Currencies.Add(new Currency
        {
            Id = CurrencyId(rfqId), BusinessUnitId = tenant, Code = "SAR", CurrencyName = "Saudi Riyal",
            ExchangeRate = 1m, IsBaseCurrency = true, IsActive = true,
            CreatedBy = "qa", CreatedOn = DateTime.UtcNow
        });
        context.Rfqitems.Add(new Rfqitem
        {
            Id = RfqItemId(rfqId), Rfqid = rfqId, ProductShortName = "Awarded item", Quantity = 10,
            CurrencyId = CurrencyId(rfqId), CreatedBy = "qa", CreatedDate = DateTime.UtcNow
        });
        var quote = new Quote
        {
            Id = QuoteId(rfqId), QuoteNo = $"Q-{rfqId}", BusinessUnitId = tenant, Rfqid = rfqId,
            CurrencyId = CurrencyId(rfqId), QuoteDate = DateTime.UtcNow,
            ValidUntil = DateTime.UtcNow.AddDays(30), TotalAmount = QuotedPrice * 10m,
            CreatedBy = "qa", CreatedDate = DateTime.UtcNow
        };
        quote.QuoteItems.Add(new QuoteItem
        {
            Id = QuoteItemId(rfqId), RfqitemId = RfqItemId(rfqId), ItemDescription = "Awarded item",
            Quantity = 10m, UnitPrice = QuotedPrice, TotalAmount = QuotedPrice * 10m,
            CreatedBy = "qa", CreatedDate = DateTime.UtcNow
        });
        context.Quotes.Add(quote);
        context.Set<ERP_RFQ_Automation.SupplierQuotes.CustomerQuoteSourcingDecision>().Add(
            new ERP_RFQ_Automation.SupplierQuotes.CustomerQuoteSourcingDecision
            {
                Id = DecisionId(rfqId), BusinessUnitId = tenant, QuoteId = QuoteId(rfqId),
                QuoteItemId = QuoteItemId(rfqId), RfqId = rfqId, RfqItemId = RfqItemId(rfqId),
                CommercialDemandLineId = 0, SourcingCaseId = 0, SourcingAwardId = 0,
                SupplierQuotedItemId = 0, SupplierQuoteId = 0, SupplierQuoteRevisionId = 0,
                SupplierQuoteLineId = 0, NexoraSerial = $"NXR-QA-{rfqId}", Quantity = 10m,
                SupplierLandedUnitCost = LandedCost, TargetMarginPercent = 20m, CustomerUnitPrice = 125m,
                CurrencyId = CurrencyId(rfqId), IdempotencyKey = $"qa:floor:{rfqId}",
                RequestHash = new string('0', 64), Rationale = "qa", CreatedOn = DateTime.UtcNow,
                CreatedBy = "qa", CorrelationId = $"corr-{rfqId}"
            });
    }

    private static User User(long id, long tenant, string email, string firstName, long? managerId) => new()
    {
        Id = id,
        FirstName = firstName,
        LastName = "Tester",
        Email = email,
        PasswordHash = "x",
        ImageUrl = "n/a",
        Buid = tenant,
        ManagerId = managerId,
        IsActive = true,
        CreatedBy = "qa",
        CreatedOn = DateTime.UtcNow
    };

    private sealed record SentAlert(string ToEmail, string Level, long BusinessUnitId);

    private sealed class CapturingNotifications : ISlaNotifications
    {
        public List<SentAlert> Sent { get; } = [];

        public Task<SlaSendResult> SendDeadlineAlertAsync(
            string toEmail, string? toName, string level, string entityLabel,
            string headline, string detail, long businessUnitId, CancellationToken ct = default)
        {
            Sent.Add(new SentAlert(toEmail, level, businessUnitId));
            return Task.FromResult(new SlaSendResult(SlaSendOutcome.Sent, "test-transport", "accepted"));
        }

        public Task<SlaSendResult> SendStaleQuotesDigestAsync(
            string toEmail, string? toName, IReadOnlyList<StaleQuoteDigestLine> lines,
            long businessUnitId, CancellationToken ct = default)
            => Task.FromResult(new SlaSendResult(SlaSendOutcome.Sent, "test-transport", "accepted"));
    }

    /// <summary>The hold path never prices anything — the check is already computed.</summary>
    private sealed class StubPricingEngine : IPricingEngine
    {
        public Task<PricePreview> PriceRfqAsync(long rfqId, long businessUnitId, CancellationToken ct)
            => throw new NotSupportedException("The hold path must not re-price the RFQ.");

        public Task<ApplyPricingResult> ApplyPricingAsync(
            long rfqId, long businessUnitId, ApplyPricingRequest req, CancellationToken ct)
            => throw new NotSupportedException();
    }
}
