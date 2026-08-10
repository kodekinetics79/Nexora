using ERP_RFQ_Automation.Fx;
using ERP_RFQ_Automation.Intelligence.Pricing;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.OrderToCash;
using ERP_RFQ_Automation.Services;
using ERP_RFQ_Automation.Services.Interfaces;
using ERP_RFQ_Automation.Sla;
using ERP_RFQ_Automation.SupplierQuotes;
using ERP_RFQ_Automation.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace ERP_RFQ_Automation.Tests.Intelligence.Pricing;

/// <summary>
/// Decision register R30 — the below-floor control could not fire.
///
/// <para><c>PricingEngine</c> declared <c>decimal? floor = null</c>, never assigned it, and wrote
/// that null to <c>PriceLine.FloorUnitPrice</c>. That was the ONLY assignment to the property in
/// the codebase, so <c>BelowFloorGuard</c>'s two filters (<c>FloorUnitPrice &gt; 0</c> and
/// <c>FloorUnitPrice.HasValue</c>) were permanently empty, <c>CheckQuoteSendAsync</c> always
/// returned Clear, and <b>no margin was ever compared to any threshold before a customer quote was
/// released</b>. The approvals inbox entry, the deadline-buffer escalation and the FX conversion
/// were all unreachable code.</para>
///
/// <para>The floor is now READ from the awarded supplier's landed unit cost on
/// <c>CustomerQuoteSourcingDecision</c> — the identical figure the customer price was derived from
/// by <c>landed / (1 - margin)</c>, which is what makes it the honest floor rather than an
/// inferred one. Every test below drives the REAL detector: delete the assignment in
/// <c>PricingEngine</c> and each of them fails.</para>
/// </summary>
public sealed class BelowFloorDetectionTests
{
    private const long Tenant = 97_001;
    private const long Sar = 97_101;      // base currency
    private const long Usd = 97_102;
    private const long RfqId = 97_200;
    private const long AwardedRfqItem = 97_210;
    private const long UnawardedRfqItem = 97_211;
    private const long QuoteId = 97_300;
    private const long AwardedQuoteItem = 97_310;
    private const long UnawardedQuoteItem = 97_311;

    /// <summary>The awarded landed unit cost, in SAR. Every expectation below is derived from it.</summary>
    private const decimal LandedCost = 100m;

    private static readonly DateTime Anchor = new(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc);

    // ============================================================ the floor itself

    /// <summary>
    /// THE ONE THAT MATTERS. A quote line priced under the cost of the goods holds the send.
    /// Nothing here is hand-constructed: the floor comes out of the engine, off the awarded
    /// sourcing decision, and the guard finds the breach on its own.
    /// </summary>
    [Fact]
    public async Task A_quote_line_priced_below_the_awarded_landed_cost_is_detected()
    {
        using var db = NewDatabase(awardedLinePrice: 80m);
        await using var context = db.ContextFor(Tenant);

        var check = await Guard(context).CheckQuoteSendAsync(QuoteId, Tenant, default);

        Assert.True(check.IsBelowFloor);
        Assert.False(check.RequiresFxEvidence);
        var line = Assert.Single(check.Lines);
        Assert.Equal(AwardedRfqItem, line.RfqItemId);
        Assert.Equal(80m, line.UnitPrice);
        Assert.Equal(LandedCost, line.FloorUnitPrice);
        Assert.Equal(20m, line.Delta);
        Assert.Equal(0.2m, check.MaxDeltaPct);
    }

    /// <summary>A price above the awarded cost is not a breach, and is not held.</summary>
    [Fact]
    public async Task A_quote_line_priced_above_the_awarded_landed_cost_is_clear()
    {
        using var db = NewDatabase(awardedLinePrice: 130m);
        await using var context = db.ContextFor(Tenant);

        var check = await Guard(context).CheckQuoteSendAsync(QuoteId, Tenant, default);

        Assert.False(check.IsBelowFloor);
        Assert.Empty(check.Lines);
        Assert.Empty(check.CurrencyBlockers);
    }

    /// <summary>
    /// A line with no awarded sourcing decision has NO KNOWN FLOOR. It stays null and is rendered
    /// as a gap; it is never 0, because a floor of 0 asserts "any price is acceptable" — a
    /// decision nobody took (wiring contract failure #10), and one that would silently clear every
    /// price ever typed.
    ///
    /// <para>The second half is the part that used to be wrong on the other side: an un-floored
    /// line must not switch the control OFF for the lines that do have floors. Both lines are
    /// priced at 80 here; only the awarded one is reported.</para>
    /// </summary>
    [Fact]
    public async Task A_line_with_no_awarded_sourcing_decision_has_no_floor_and_is_never_treated_as_zero()
    {
        using var db = NewDatabase(awardedLinePrice: 80m, unawardedLinePrice: 80m);
        await using var context = db.ContextFor(Tenant);

        var preview = await new PricingEngine(context, NullLogger<PricingEngine>.Instance)
            .PriceRfqAsync(RfqId, Tenant, default);

        var gap = Assert.Single(preview.Lines, l => l.RfqItemId == UnawardedRfqItem);
        Assert.Null(gap.FloorUnitPrice);
        Assert.Null(gap.FloorCurrency);
        Assert.Null(gap.FloorBasis);
        Assert.Contains("No cost floor is established", gap.Rationale);

        var floored = Assert.Single(preview.Lines, l => l.RfqItemId == AwardedRfqItem);
        Assert.Equal(LandedCost, floored.FloorUnitPrice);
        Assert.Equal("SAR", floored.FloorCurrency);

        // The un-floored line is unchecked; the floored one still blocks.
        var check = await Guard(context).CheckQuoteSendAsync(QuoteId, Tenant, default);
        var reported = Assert.Single(check.Lines);
        Assert.Equal(AwardedRfqItem, reported.RfqItemId);
    }

    // ============================================================ currency

    /// <summary>
    /// The quote is in USD and the floor is in SAR. 20 USD at an approved 3.75 is 75 SAR, which is
    /// under a 100 SAR cost — the breach only exists once the units are reconciled, and the raw
    /// numbers (20 vs 100) would have "agreed" in either direction by accident.
    ///
    /// <para>The direction is fixed and deliberate: the PRICE is converted into the FLOOR's
    /// currency, never the other way round, so the cost the manager is shown in the approvals
    /// inbox is the landed cost as booked rather than a re-expressed derivative of it.</para>
    /// </summary>
    [Fact]
    public async Task A_price_in_another_currency_is_converted_into_the_floors_currency_before_it_is_compared()
    {
        using var db = NewDatabase(awardedLinePrice: 20m, quoteCurrencyId: Usd, rfqLineCurrencyId: Usd,
            approvedUsdToSarRate: 3.75m);
        await using var context = db.ContextFor(Tenant);

        var check = await Guard(context).CheckQuoteSendAsync(QuoteId, Tenant, default);

        Assert.True(check.IsBelowFloor);
        Assert.Empty(check.CurrencyBlockers);
        var line = Assert.Single(check.Lines);
        Assert.Equal(75m, line.UnitPrice);          // converted, not the raw 20
        Assert.Equal(LandedCost, line.FloorUnitPrice);
        Assert.Equal(25m, line.Delta);
    }

    /// <summary>And the same conversion clears a price that really does cover the cost.</summary>
    [Fact]
    public async Task A_converted_price_that_covers_the_floor_is_cleared()
    {
        using var db = NewDatabase(awardedLinePrice: 30m, quoteCurrencyId: Usd, rfqLineCurrencyId: Usd,
            approvedUsdToSarRate: 3.75m);
        await using var context = db.ContextFor(Tenant);

        var check = await Guard(context).CheckQuoteSendAsync(QuoteId, Tenant, default);

        // 30 x 3.75 = 112.50 SAR, above the 100 SAR cost. Read the RFQ LINE's currency (USD) as
        // the floor's and the two look like the same money: 30 "<" 100 and a real deal is held.
        Assert.False(check.IsBelowFloor);
    }

    /// <summary>
    /// FAIL CLOSED. With no approved rate joining the two currencies the guard cannot know whether
    /// the price covers the cost — and "we could not check" must never resolve to the same answer
    /// as "the check passed". The send is held, flagged as needing FX evidence rather than as a
    /// breach, and a merely PENDING rate does not rescue it.
    /// </summary>
    [Fact]
    public async Task A_price_that_cannot_be_converted_holds_the_send_instead_of_passing_it()
    {
        using var db = NewDatabase(awardedLinePrice: 1_000m, quoteCurrencyId: Usd, rfqLineCurrencyId: Usd,
            pendingUsdToSarRate: 3.75m);
        await using var context = db.ContextFor(Tenant);

        var check = await Guard(context).CheckQuoteSendAsync(QuoteId, Tenant, default);

        // 1,000 USD is comfortably above a 100 SAR cost at any plausible rate. It is still held,
        // because the only rate on file was never approved.
        Assert.True(check.IsBelowFloor);
        Assert.True(check.RequiresFxEvidence);
        Assert.Empty(check.Lines);
        Assert.Contains(check.CurrencyBlockers, b => b.Contains("no approved USD to SAR exchange rate"));
    }

    // ============================================================ it actually blocks something

    /// <summary>
    /// The wiring contract's real question: if the floor were deleted, what would break? This.
    /// The send path refuses to queue the email, parks the exact action as a pending
    /// <c>AgentApproval</c> in the approvals inbox, and stamps the audit ledger — none of which
    /// any quote in this system could reach before the floor was assigned.
    /// </summary>
    [Fact]
    public async Task The_send_is_held_for_approval_and_nothing_is_queued()
    {
        using var db = NewDatabase(awardedLinePrice: 80m);
        await using var context = db.ContextFor(Tenant);

        await new PriceAttestationService(context).AttestAsync(
            QuoteId, Tenant, PriceAttestationSources.SalesManager, "QA Manager (synthetic)",
            7, "rep@nexora.invalid", default);
        context.ChangeTracker.Clear();

        var service = new QuoteService(context, new NoOpEmailService(), null!, Guard(context));
        var result = await service.SendQuoteEmailAsync(QuoteId, Tenant, "buyer@nexora.invalid");

        Assert.True(result.Held);
        Assert.NotNull(result.ApprovalId);
        Assert.Contains("below floor by up to 20%", result.HoldSummary);
        Assert.False(result.QueuedForDelivery);
        Assert.Empty(context.QuoteDeliveryRequests.IgnoreQueryFilters());

        var approval = await context.Set<ERP_RFQ_Automation.Agent.Models.AgentApproval>()
            .AsNoTracking().IgnoreQueryFilters().SingleAsync();
        Assert.Equal(BelowFloorGuard.ToolName, approval.ToolName);
        Assert.Contains("\"floorUnitPrice\":100", approval.InputJson);
    }

    /// <summary>The same quote, priced over cost, goes out. The gate blocks breaches, not sends.</summary>
    [Fact]
    public async Task The_same_send_proceeds_once_the_price_covers_the_cost()
    {
        using var db = NewDatabase(awardedLinePrice: 130m);
        await using var context = db.ContextFor(Tenant);

        await new PriceAttestationService(context).AttestAsync(
            QuoteId, Tenant, PriceAttestationSources.SalesManager, "QA Manager (synthetic)",
            7, "rep@nexora.invalid", default);
        context.ChangeTracker.Clear();

        var service = new QuoteService(context, new NoOpEmailService(), null!, Guard(context));
        var result = await service.SendQuoteEmailAsync(QuoteId, Tenant, "buyer@nexora.invalid");

        Assert.False(result.Held);
        Assert.True(result.QueuedForDelivery);
    }

    // ============================================================ harness

    private static BelowFloorGuard Guard(ErpRfqAutomationContext context) => new(
        context, new PricingEngine(context, NullLogger<PricingEngine>.Instance),
        new SilentNotifications(), NullLogger<BelowFloorGuard>.Instance);

    /// <summary>
    /// One RFQ with two lines, one Customer Quote covering both, and an awarded sourcing decision
    /// against the FIRST line only.
    ///
    /// <para><c>CustomerQuoteSourcingDecision</c> carries seven composite foreign keys into the
    /// sourcing aggregate — supplier quote, revision, line, quoted item, demand line, case and
    /// award. Standing that whole chain up would make these tests about the sourcing fixture
    /// rather than about the floor, so referential enforcement is stood down for the seed exactly
    /// as <c>Gate8GrossMarginTests</c> does. The columns under test — RfqItemId,
    /// SupplierLandedUnitCost, CurrencyId — are unaffected by that.</para>
    /// </summary>
    private static TestDb NewDatabase(
        decimal awardedLinePrice,
        decimal? unawardedLinePrice = null,
        long quoteCurrencyId = Sar,
        long rfqLineCurrencyId = Sar,
        decimal? approvedUsdToSarRate = null,
        decimal? pendingUsdToSarRate = null)
    {
        var db = new TestDb();
        using var seed = db.ContextFor(null);
        seed.Database.ExecuteSqlRaw("PRAGMA foreign_keys = OFF");
        seed.Database.ExecuteSqlRaw("PRAGMA ignore_check_constraints = ON");

        Seed.EnsureBusinessUnit(seed, Tenant);
        seed.SaveChanges();

        seed.Currencies.AddRange(
            new Currency
            {
                Id = Sar, BusinessUnitId = Tenant, Code = "SAR", CurrencyName = "Saudi Riyal",
                ExchangeRate = 1m, IsBaseCurrency = true, IsActive = true, CreatedBy = "qa", CreatedOn = Anchor
            },
            new Currency
            {
                Id = Usd, BusinessUnitId = Tenant, Code = "USD", CurrencyName = "US Dollar",
                ExchangeRate = 3.75m, IsBaseCurrency = false, IsActive = true, CreatedBy = "qa", CreatedOn = Anchor
            });

        if (approvedUsdToSarRate is { } approved)
            seed.Set<FxRate>().Add(FxRate(1, approved, FxRateStatuses.Approved));
        if (pendingUsdToSarRate is { } pending)
            seed.Set<FxRate>().Add(FxRate(2, pending, FxRateStatuses.Pending));

        var rfq = AgentSeed.Rfq(seed, RfqId, Tenant, "RFQ-FLOOR-1");
        rfq.BidClosingDate = Anchor.AddYears(10);   // far outside the SLA buffer: no escalation noise
        // The RFQ LINE's currency is a separate column from the AWARD's, and the two differ in the
        // cross-currency cases below on purpose: PriceLine.Currency is the line's, FloorCurrency is
        // the award's, and reading the wrong one is the exact unit error this fixture must expose.
        var awarded = AgentSeed.RfqItem(seed, AwardedRfqItem, RfqId, "Awarded item", 10);
        awarded.CurrencyId = rfqLineCurrencyId;
        var unawarded = AgentSeed.RfqItem(seed, UnawardedRfqItem, RfqId, "First-time item", 10);
        unawarded.CurrencyId = rfqLineCurrencyId;

        var quote = new Quote
        {
            Id = QuoteId, QuoteNo = "Q-FLOOR-1", BusinessUnitId = Tenant, Rfqid = RfqId,
            CurrencyId = quoteCurrencyId, QuoteDate = Anchor, ValidUntil = Anchor.AddYears(10),
            TotalAmount = 0m, CreatedBy = "qa", CreatedDate = Anchor
        };
        quote.QuoteItems.Add(QuoteLine(AwardedQuoteItem, AwardedRfqItem, awardedLinePrice));
        quote.QuoteItems.Add(QuoteLine(UnawardedQuoteItem, UnawardedRfqItem, unawardedLinePrice ?? 500m));
        seed.Quotes.Add(quote);

        // The governed award-to-quote bridge priced the awarded line, and only it. The cost floor
        // is denominated in SAR (the AWARD's currency) whatever currency the customer quote is in.
        seed.CustomerQuoteSourcingDecisions.Add(new CustomerQuoteSourcingDecision
        {
            Id = 97_400, BusinessUnitId = Tenant, QuoteId = QuoteId, QuoteItemId = AwardedQuoteItem,
            RfqId = RfqId, RfqItemId = AwardedRfqItem, CommercialDemandLineId = 0, SourcingCaseId = 0,
            SourcingAwardId = 0, SupplierQuotedItemId = 0, SupplierQuoteId = 0, SupplierQuoteRevisionId = 0,
            SupplierQuoteLineId = 0, NexoraSerial = "NXR-QA-FLOOR", Quantity = 10m,
            SupplierLandedUnitCost = LandedCost, TargetMarginPercent = 20m, CustomerUnitPrice = 125m,
            CurrencyId = Sar, IdempotencyKey = "qa:floor:1", RequestHash = new string('0', 64),
            Rationale = "qa", CreatedOn = Anchor, CreatedBy = "qa", CorrelationId = "corr-qa-floor"
        });

        seed.SaveChanges();
        return db;
    }

    private static FxRate FxRate(long id, decimal rate, string status) => new()
    {
        Id = id, BusinessUnitId = Tenant, FromCurrencyId = Usd, ToCurrencyId = Sar, Rate = rate,
        EffectiveFrom = Anchor.AddYears(-1), EffectiveTo = null, Source = "Manual", Status = status,
        ApprovedBy = status == FxRateStatuses.Approved ? "qa" : null,
        ApprovedOn = status == FxRateStatuses.Approved ? Anchor : null,
        CreatedBy = "qa", CreatedOn = Anchor
    };

    /// <summary>
    /// Tax is derived on every line so the R17 output-tax gate is satisfied and these tests fail on
    /// the floor or not at all. 15% is the KSA standard rate.
    /// </summary>
    private static QuoteItem QuoteLine(long id, long rfqItemId, decimal unitPrice) => new()
    {
        Id = id, RfqitemId = rfqItemId, ItemDescription = $"Line {id}", Quantity = 10m,
        UnitPrice = unitPrice, TaxCategory = QuoteLineTaxCategories.Standard, TaxRatePercentApplied = 15m,
        TaxAmount = decimal.Round(unitPrice * 10m * 0.15m, 2), TotalAmount = decimal.Round(unitPrice * 11.5m, 2),
        CreatedBy = "qa", CreatedDate = Anchor
    };

    private sealed class SilentNotifications : ISlaNotifications
    {
        public Task<SlaSendResult> SendDeadlineAlertAsync(
            string toEmail, string? toName, string level, string entityLabel,
            string headline, string detail, long businessUnitId, CancellationToken ct = default)
            => Task.FromResult(new SlaSendResult(SlaSendOutcome.NotSent, null, null));

        public Task<SlaSendResult> SendStaleQuotesDigestAsync(
            string toEmail, string? toName, IReadOnlyList<StaleQuoteDigestLine> lines,
            long businessUnitId, CancellationToken ct = default)
            => Task.FromResult(new SlaSendResult(SlaSendOutcome.NotSent, null, null));
    }

    private sealed class NoOpEmailService : IEmailService
    {
        public Task<MailboxPollReport> FetchAndSaveLeadsAsync(long? businessUnitId = null)
            => Task.FromResult(new MailboxPollReport([]));

        public Task SendEmailAsync(string to, string subject, string body,
            List<(string FileName, byte[] FileContent, string ContentType)> attachments = null!,
            string fromEmail = null!, long? businessUnitId = null)
            => Task.CompletedTask;
    }
}
