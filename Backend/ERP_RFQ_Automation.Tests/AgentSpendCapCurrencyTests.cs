using ERP_RFQ_Automation.Agent;
using ERP_RFQ_Automation.Agent.Guardrails;
using ERP_RFQ_Automation.Agent.Models;
using ERP_RFQ_Automation.Agent.Tools;
using ERP_RFQ_Automation.Fx;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Procurement;
using ERP_RFQ_Automation.Tests.Support;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// The agent spend cap must be able to tell currencies apart.
///
/// <para><b>The defect.</b> <see cref="AgentPolicy"/> stored its auto-approve caps as bare
/// decimals with no currency, and every comparison site tested them directly against amounts
/// denominated in a supplier quote's own currency (<c>SupplierQuotedItem.CurrencyId</c>) with no
/// conversion. A cap of 10,000 stopped a 10,000 SAR award and a 10,000 USD award alike, so the
/// same configured ceiling authorised several times more unattended spend depending only on
/// which currency a supplier happened to quote in. This decides what the platform commits
/// WITHOUT a human, so it is a control defect.</para>
///
/// <para><b>What these tests pin.</b> The first two are the ones that would have caught it: the
/// SAME NUMBER in two different currencies must produce DIFFERENT guardrail outcomes, at both
/// comparison surfaces. The rest pin the fail-closed edges — no rate, no policy currency, no
/// amount currency, unapproved rate — where the temptation is to fall through to a raw numeric
/// comparison. Every one of them fails if someone deletes the conversion, because deleting it
/// makes the two currencies behave identically again.</para>
///
/// <para><b>All FX rates here are SYNTHETIC.</b> <see cref="SyntheticUsdToSar"/> is a round 4.0
/// chosen to be obviously not a market rate and to make the arithmetic checkable by eye. No test
/// in this file asserts anything about a real-world exchange rate, and nothing here is a rate
/// source for production code.</para>
/// </summary>
public sealed class AgentSpendCapCurrencyTests
{
    private const long Bu = 9_700;
    private const long Sar = 9_710; // base currency
    private const long Usd = 9_711;
    private const long Eur = 9_712; // deliberately never given a rate

    private const long RfqId = 9_720;
    private const long RfqItemId = 9_721;
    private const long SupplierId = 9_722;
    private const long SolicitationId = 9_723;
    private const long QuotedItemId = 9_724;

    /// <summary>SYNTHETIC. One USD = 4 SAR. Not a market rate; chosen for legible arithmetic.</summary>
    private const decimal SyntheticUsdToSar = 4.0m;

    private static readonly DateTime Jan1 = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Unspecified);

    private static AgentToolContext Ctx => new() { BusinessUnitId = Bu, UserId = 42, UserName = "tester" };

    // ───────────────────────────── 1. the test that would have caught it (guardrail surface)

    /// <summary>
    /// Cap = 10,000 SAR. The award is 10,000 units of currency either way. In SAR that is at the
    /// cap and auto-executes; in USD it is 40,000 SAR and must go to a human.
    ///
    /// Before the fix BOTH rows returned Allow, because 10,000 was compared to 10,000 with no
    /// regard for what the units were. If the conversion is ever removed, both rows return Allow
    /// again and this test fails on the USD row.
    /// </summary>
    [Theory]
    [InlineData(Sar, GuardrailOutcome.Allow)]          // 10,000 SAR vs 10,000 SAR cap -> at the cap
    [InlineData(Usd, GuardrailOutcome.RequireApproval)] // 10,000 USD = 40,000 SAR -> 4x over the cap
    public async Task Identical_award_numbers_in_different_currencies_get_different_outcomes(
        long quoteCurrencyId, GuardrailOutcome expected)
    {
        using var db = new TestDb();
        using (var seed = db.ContextFor(null))
        {
            SeedCurrencies(seed);
            seed.FxRates.Add(ApprovedRate(Usd, Sar, SyntheticUsdToSar));
            SeedQuotedItem(seed, quoteCurrencyId);
            AgentSeed.Policy(seed, Bu, AgentAutonomyLevel.Act,
                maxAutoAwardValue: 10_000m, currencyId: Sar, requireApprovalForAwards: false);
            seed.SaveChanges();
        }

        using var ctx = db.ContextFor(Bu);
        var decision = await new AgentGuardrail(ctx).EvaluateAsync(
            new FakeAgentTool(AgentToolNames.AwardRfq, isMutation: true),
            AgentSeed.Json($"{{\"awards\":[{{\"supplierQuotedItemId\":{QuotedItemId},\"unitPrice\":1000,\"quantity\":10}}]}}"),
            Ctx, CancellationToken.None);

        Assert.Equal(expected, decision.Outcome);
    }

    // ───────────────────────────── 2. the same test at the tool surface (SourcingTools.cs)

    /// <summary>
    /// <c>AwardRfqTool</c> enforces the cap itself against the PERSISTED landed cost, so it is a
    /// second, independent comparison site and needs its own proof. 10 units at a landed cost of
    /// 1,000 is 10,000 either way; only the currency differs.
    /// </summary>
    [Theory]
    [InlineData(Sar, true)]  // 10,000 SAR, at the 10,000 SAR cap -> proceeds
    [InlineData(Usd, false)] // 10,000 USD = 40,000 SAR -> refused
    public async Task AwardRfqTool_applies_the_cap_in_the_policy_currency_not_the_quote_currency(
        long quoteCurrencyId, bool expectSuccess)
    {
        using var db = new TestDb();
        using (var seed = db.ContextFor(null))
        {
            SeedCurrencies(seed);
            seed.FxRates.Add(ApprovedRate(Usd, Sar, SyntheticUsdToSar));
            SeedQuotedItem(seed, quoteCurrencyId, landedUnitCost: 1_000m);
            AgentSeed.Policy(seed, Bu, AgentAutonomyLevel.Act,
                maxAutoAwardValue: 10_000m, currencyId: Sar, requireApprovalForAwards: false);
            seed.SaveChanges();
        }

        var procurement = new CapturingProcurement();
        using var ctx = db.ContextFor(Bu);
        var result = await new AwardRfqTool(ctx, procurement).ExecuteAsync(
            AgentSeed.Json($"{{\"rfqId\":{RfqId},\"awards\":[{{\"supplierQuotedItemId\":{QuotedItemId}," +
                           "\"expectedQuoteVersion\":1,\"quantity\":10}]}"),
            Ctx, CancellationToken.None);

        Assert.Equal(expectSuccess, result.Success);
        if (expectSuccess)
        {
            Assert.Single(procurement.Awards);
        }
        else
        {
            // Refused, and the refusal names both denominations so a human can audit it.
            Assert.Empty(procurement.Awards);
            Assert.Contains("USD", result.Error!, StringComparison.Ordinal);
            Assert.Contains("SAR", result.Error!, StringComparison.Ordinal);
        }
    }

    // ───────────────────────────── 3. unconvertible must REFUSE, never fall through

    /// <summary>
    /// EUR is a real currency of this tenant with a real quote, but no approved rate joins it to
    /// the cap currency. The raw number (100) is far below the cap (10,000), so a fall-through to
    /// a numeric comparison would auto-approve it. It must not.
    /// </summary>
    [Fact]
    public async Task No_approved_rate_for_the_pair_refuses_even_when_the_bare_number_is_tiny()
    {
        using var db = new TestDb();
        using (var seed = db.ContextFor(null))
        {
            SeedCurrencies(seed);
            seed.FxRates.Add(ApprovedRate(Usd, Sar, SyntheticUsdToSar)); // nothing joins EUR to SAR
            SeedQuotedItem(seed, Eur);
            AgentSeed.Policy(seed, Bu, AgentAutonomyLevel.Act,
                maxAutoAwardValue: 10_000m, currencyId: Sar, requireApprovalForAwards: false);
            seed.SaveChanges();
        }

        using var ctx = db.ContextFor(Bu);
        var decision = await new AgentGuardrail(ctx).EvaluateAsync(
            new FakeAgentTool(AgentToolNames.AwardRfq, isMutation: true),
            AgentSeed.Json($"{{\"awards\":[{{\"supplierQuotedItemId\":{QuotedItemId},\"unitPrice\":100,\"quantity\":1}}]}}"),
            Ctx, CancellationToken.None);

        Assert.Equal(GuardrailOutcome.RequireApproval, decision.Outcome);
        // The message must name what is missing, not just say "requires approval".
        Assert.Contains("EUR", decision.Reason, StringComparison.Ordinal);
        Assert.Contains("SAR", decision.Reason, StringComparison.Ordinal);
        Assert.Contains("rate", decision.Reason, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A rate row exists for the pair but has never been approved. FxConversionService only ever
    /// reads <see cref="FxRateStatuses.Approved"/> rows, so this must behave exactly like no rate
    /// at all — a pending rate must not be able to authorise spend.
    /// </summary>
    [Fact]
    public async Task A_pending_unapproved_rate_does_not_authorise_spend()
    {
        using var db = new TestDb();
        using (var seed = db.ContextFor(null))
        {
            SeedCurrencies(seed);
            var pending = ApprovedRate(Usd, Sar, SyntheticUsdToSar);
            pending.Status = FxRateStatuses.Pending;
            pending.ApprovedBy = null;
            pending.ApprovedOn = null;
            seed.FxRates.Add(pending);
            SeedQuotedItem(seed, Usd);
            AgentSeed.Policy(seed, Bu, AgentAutonomyLevel.Act,
                maxAutoAwardValue: 10_000m, currencyId: Sar, requireApprovalForAwards: false);
            seed.SaveChanges();
        }

        using var ctx = db.ContextFor(Bu);
        var decision = await new AgentGuardrail(ctx).EvaluateAsync(
            new FakeAgentTool(AgentToolNames.AwardRfq, isMutation: true),
            AgentSeed.Json($"{{\"awards\":[{{\"supplierQuotedItemId\":{QuotedItemId},\"unitPrice\":10,\"quantity\":1}}]}}"),
            Ctx, CancellationToken.None);

        Assert.Equal(GuardrailOutcome.RequireApproval, decision.Outcome);
    }

    // ───────────────────────────── 4. an unconfigured policy currency suspends auto-approval

    /// <summary>
    /// This is the state EVERY pre-existing row is in: caps set, currency null. A generous cap
    /// and a trivially small award still route to a human, and the message names the missing
    /// configuration so an admin knows what to do about it.
    /// </summary>
    [Fact]
    public async Task A_policy_with_no_cap_currency_cannot_auto_approve_anything()
    {
        using var db = new TestDb();
        using (var seed = db.ContextFor(null))
        {
            SeedCurrencies(seed);
            seed.FxRates.Add(ApprovedRate(Usd, Sar, SyntheticUsdToSar));
            SeedQuotedItem(seed, Sar);
            AgentSeed.Policy(seed, Bu, AgentAutonomyLevel.Act,
                maxAutoAwardValue: 1_000_000m, currencyId: null, requireApprovalForAwards: false);
            seed.SaveChanges();
        }

        using var ctx = db.ContextFor(Bu);
        var decision = await new AgentGuardrail(ctx).EvaluateAsync(
            new FakeAgentTool(AgentToolNames.AwardRfq, isMutation: true),
            AgentSeed.Json($"{{\"awards\":[{{\"supplierQuotedItemId\":{QuotedItemId},\"unitPrice\":1,\"quantity\":1}}]}}"),
            Ctx, CancellationToken.None);

        Assert.Equal(GuardrailOutcome.RequireApproval, decision.Outcome);
        Assert.Contains("no cap currency", decision.Reason, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The conservative default carries no cap currency, so it denominates nothing.</summary>
    [Fact]
    public async Task The_default_policy_for_a_tenant_with_no_stored_row_has_no_cap_currency()
    {
        using var db = new TestDb();
        using var ctx = db.ContextFor(Bu);

        var policy = await new AgentGuardrail(ctx).GetPolicyAsync(Bu, CancellationToken.None);

        Assert.Null(policy.CurrencyId);
        Assert.Equal(0m, policy.MaxAutoAwardValue);
        Assert.Equal(0m, policy.MaxAutoOrderValue);
    }

    // ───────────────────────────── 5. an amount with no currency is not "the same currency"

    /// <summary>
    /// The award names no persisted quote line, so the guardrail cannot learn the currency of the
    /// money it is being asked to authorise. The old code treated that as "same currency" by
    /// default. It is not — it is unknown, and unknown goes to a human.
    /// </summary>
    [Fact]
    public async Task An_award_with_no_resolvable_currency_is_not_assumed_to_match_the_cap()
    {
        using var db = new TestDb();
        using (var seed = db.ContextFor(null))
        {
            SeedCurrencies(seed);
            AgentSeed.Policy(seed, Bu, AgentAutonomyLevel.Act,
                maxAutoAwardValue: 10_000m, currencyId: Sar, requireApprovalForAwards: false);
            seed.SaveChanges();
        }

        using var ctx = db.ContextFor(Bu);
        var decision = await new AgentGuardrail(ctx).EvaluateAsync(
            new FakeAgentTool(AgentToolNames.AwardRfq, isMutation: true),
            // No supplierQuotedItemId: a bare caller-asserted total, well under the cap.
            AgentSeed.Json("{\"awards\":[{\"unitPrice\":100,\"quantity\":1}]}"),
            Ctx, CancellationToken.None);

        Assert.Equal(GuardrailOutcome.RequireApproval, decision.Outcome);
        Assert.Contains("carries no currency", decision.Reason, StringComparison.OrdinalIgnoreCase);
    }

    // ───────────────────────────── 6. the order cap site converts too

    /// <summary>
    /// <c>create_order_from_quote</c> reads the total AND the denomination off the quote. Same
    /// 10,000 against a 10,000 SAR cap: allowed in SAR, refused in USD.
    /// </summary>
    [Theory]
    [InlineData(Sar, GuardrailOutcome.Allow)]
    [InlineData(Usd, GuardrailOutcome.RequireApproval)]
    public async Task Order_cap_converts_the_quote_total_into_the_policy_currency(
        long quoteCurrencyId, GuardrailOutcome expected)
    {
        const long quoteId = 9_730;
        using var db = new TestDb();
        using (var seed = db.ContextFor(null))
        {
            SeedCurrencies(seed);
            seed.FxRates.Add(ApprovedRate(Usd, Sar, SyntheticUsdToSar));
            seed.Quotes.Add(new Quote
            {
                Id = quoteId,
                BusinessUnitId = Bu,
                QuoteNo = "Q-CAP-1",
                CurrencyId = quoteCurrencyId,
                TotalAmount = 10_000m,
                CreatedBy = "seed",
                CreatedDate = Jan1
            });
            AgentSeed.Policy(seed, Bu, AgentAutonomyLevel.Act,
                maxAutoOrderValue: 10_000m, currencyId: Sar, requireApprovalForOrders: false);
            seed.SaveChanges();
        }

        using var ctx = db.ContextFor(Bu);
        var decision = await new AgentGuardrail(ctx).EvaluateAsync(
            new FakeAgentTool(AgentToolNames.CreateOrderFromQuote, isMutation: true),
            AgentSeed.Json($"{{\"quoteId\":{quoteId}}}"),
            Ctx, CancellationToken.None);

        Assert.Equal(expected, decision.Outcome);
    }

    // ───────────────────────────── 7. the conversion is visible in the message

    /// <summary>
    /// A refusal that says "10,000 exceeds 10,000" is unauditable and reads like a false
    /// positive. The message must show the original amount, the converted amount and the rate.
    /// </summary>
    [Fact]
    public async Task A_converted_refusal_shows_both_denominations_and_the_rate_applied()
    {
        using var db = new TestDb();
        using (var seed = db.ContextFor(null))
        {
            SeedCurrencies(seed);
            seed.FxRates.Add(ApprovedRate(Usd, Sar, SyntheticUsdToSar));
            SeedQuotedItem(seed, Usd);
            AgentSeed.Policy(seed, Bu, AgentAutonomyLevel.Act,
                maxAutoAwardValue: 10_000m, currencyId: Sar, requireApprovalForAwards: false);
            seed.SaveChanges();
        }

        using var ctx = db.ContextFor(Bu);
        var decision = await new AgentGuardrail(ctx).EvaluateAsync(
            new FakeAgentTool(AgentToolNames.AwardRfq, isMutation: true),
            AgentSeed.Json($"{{\"awards\":[{{\"supplierQuotedItemId\":{QuotedItemId},\"unitPrice\":1000,\"quantity\":10}}]}}"),
            Ctx, CancellationToken.None);

        Assert.Equal(GuardrailOutcome.RequireApproval, decision.Outcome);
        Assert.Contains("10000 USD", decision.Reason, StringComparison.Ordinal);
        Assert.Contains("40000 SAR", decision.Reason, StringComparison.Ordinal); // 10,000 x synthetic 4.0
        Assert.Contains("Direct", decision.Reason, StringComparison.Ordinal);    // the resolution path
    }

    // ───────────────────────────── helpers

    private static void SeedCurrencies(ErpRfqAutomationContext db)
    {
        Seed.EnsureBusinessUnit(db, Bu);
        db.Currencies.AddRange(
            Currency(Sar, "SAR", "Saudi Riyal", isBase: true),
            Currency(Usd, "USD", "US Dollar", isBase: false),
            Currency(Eur, "EUR", "Euro", isBase: false));
    }

    private static Currency Currency(long id, string code, string name, bool isBase) => new()
    {
        Id = id,
        BusinessUnitId = Bu,
        Code = code,
        CurrencyName = name,
        IsBaseCurrency = isBase,
        IsActive = true,
        CreatedBy = "test",
        CreatedOn = Jan1
    };

    /// <summary>
    /// An approved rate row. The RATE PASSED IN IS SYNTHETIC in every caller — see the class
    /// summary. "One unit of <paramref name="from"/> equals <paramref name="rate"/> of
    /// <paramref name="to"/>", per FxRate's documented semantics.
    /// </summary>
    private static FxRate ApprovedRate(long from, long to, decimal rate) => new()
    {
        BusinessUnitId = Bu,
        FromCurrencyId = from,
        ToCurrencyId = to,
        Rate = rate,
        EffectiveFrom = Jan1,
        Source = "SyntheticTestRate",
        Status = FxRateStatuses.Approved,
        ApprovedBy = "treasury",
        ApprovedOn = Jan1,
        Version = 1,
        CreatedBy = "test",
        CreatedOn = Jan1
    };

    /// <summary>The persisted supplier quote line that gives an award its denomination.</summary>
    private static void SeedQuotedItem(ErpRfqAutomationContext db, long currencyId, decimal landedUnitCost = 100m)
    {
        AgentSeed.Supplier(db, SupplierId, Bu, "Cap Test Supplier");
        AgentSeed.Rfq(db, RfqId, Bu, "RFQ-CAP");
        AgentSeed.RfqItem(db, RfqItemId, RfqId, "Widget", 10);
        AgentSeed.Solicitation(db, SolicitationId, Bu, RfqId, SupplierId);
        db.SupplierQuotedItems.Add(new SupplierQuotedItem
        {
            Id = QuotedItemId,
            BusinessUnitId = Bu,
            SupplierId = SupplierId,
            SupplierSolicitationId = SolicitationId,
            RfqId = RfqId,
            RfqItemId = RfqItemId,
            Quantity = 10,
            UnitPrice = landedUnitCost,
            LandedUnitCost = landedUnitCost,
            CurrencyId = currencyId,
            QuoteReference = "SUP-Q-CAP",
            LeadTimeDays = 5,
            AvailableQuantity = 100,
            ValidUntil = new DateTime(2030, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            ResponseIdempotencyKey = $"cap-test:{QuotedItemId}",
            RequestHash = new string('0', 64),
            QuoteRevision = 1,
            Version = 1,
            IsActive = true,
            CreatedBy = "seed",
            CreatedDate = Jan1
        });
    }

    /// <summary>Records awards instead of executing them, so a leak past the cap is visible.</summary>
    private sealed class CapturingProcurement : IProcurementApplicationService
    {
        public List<ApproveAwardCommand> Awards { get; } = [];

        public Task<AwardResult> ApproveAwardAsync(ApproveAwardCommand command, CancellationToken ct = default)
        {
            Awards.Add(command);
            return Task.FromResult(new AwardResult(8_001, "APPROVED", 1m, false));
        }

        public Task<SolicitationResult> CreateSolicitationAsync(CreateSolicitationCommand command, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<SupplierQuoteResult> CaptureSupplierQuoteAsync(CaptureSupplierQuoteCommand command, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<ProcurementWorkbench> GetWorkbenchAsync(long businessUnitId, long rfqId, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<IReadOnlyCollection<SupplierPurchaseOrderSummary>> SearchPurchaseOrdersAsync(
            long businessUnitId, string? search, int limit, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<SolicitationResult> RetrySolicitationAsync(RetrySolicitationCommand command, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<QuoteComparisonResult> CompareQuotesAsync(long businessUnitId, long rfqItemId, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<PurchaseOrderResult> CreatePurchaseOrderAsync(CreatePurchaseOrderCommand command, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<PurchaseOrderResult> IssuePurchaseOrderAsync(IssuePurchaseOrderCommand command, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<GoodsReceiptResult> PostGoodsReceiptAsync(PostGoodsReceiptCommand command, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<SupplierPurchaseOrderAcknowledgementResult> AcknowledgePurchaseOrderAsync(AcknowledgeSupplierPurchaseOrderCommand command, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<PurchaseOrderTradeTermsResult> AmendPurchaseOrderTradeTermsAsync(AmendPurchaseOrderTradeTermsCommand command, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }
}
