using System.Text.Json;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.OrderToCash;
using ERP_RFQ_Automation.PlatformGovernance;
using ERP_RFQ_Automation.Reporting;
using ERP_RFQ_Automation.SupplierQuotes;
using ERP_RFQ_Automation.Tests.Support;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// The board-facing gross margin. Every test here fails if a specific piece of wiring is removed —
/// none of them merely assert that a value round-trips.
///
/// <para>The figure these replace was wrong three ways at once: it costed quote lines from
/// <c>Product.FinalLandedCost</c> (a bare purchase price, not a landed cost), averaged per-line
/// PERCENTAGES rather than weighting by value, and sampled every quote line ever written including
/// drafts and lost bids. Each of those three is pinned below by a test that would pass on the old
/// implementation only by coincidence.</para>
/// </summary>
public sealed class Gate8GrossMarginTests
{
    private const long Bu = 8_100;
    private const long OtherBu = 8_900;
    private const long Sar = 8_110;
    private const long Usd = 8_111;
    private const long AcceptedStatusId = 8_120;
    private const long DraftStatusId = 8_121;
    private const long RejectedStatusId = 8_122;

    private static readonly DateTime Anchor = new(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime WindowFrom = new(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime WindowTo = new(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc);

    // ───────────────────────────── 1. the arithmetic

    /// <summary>
    /// THE defect, pinned. One 1-unit line at 60% and one 10,000-unit line at 5% average to 32.5%
    /// as a mean of ratios; weighted by value they are 5.0%. Those numbers are not close, and the
    /// wrong one was the number on the board.
    /// </summary>
    [Fact]
    public async Task Margin_is_weighted_by_value_not_averaged_across_lines()
    {
        using var db = new TestDb();
        using (var seed = db.ContextFor(null))
        {
            SeedBase(seed);
            AcceptedQuote(seed, quoteId: 1, outcomeOn: Anchor);
            // 1 unit: cost 40, price 100 → 60% margin, 100 of revenue.
            Line(seed, quoteId: 1, quoteItemId: 11, decisionId: 101, quantity: 1m, cost: 40m, price: 100m);
            // 10,000 units: cost 95, price 100 → 5% margin, 1,000,000 of revenue.
            Line(seed, quoteId: 1, quoteItemId: 12, decisionId: 102, quantity: 10_000m, cost: 95m, price: 100m);
            await seed.SaveChangesAsync();
        }

        var result = await Compute(db);

        Assert.Equal(GrossMarginStatuses.Available, result.Status);
        // revenue 1,000,100; cost 950,040 → 5.0%. The unweighted mean would be 32.5%.
        Assert.Equal(1_000_100m, result.RevenueTotal);
        Assert.Equal(950_040m, result.CostTotal);
        Assert.Equal(5.0m, result.MarginPercent);
        Assert.Equal(2, result.SampleLines);
    }

    /// <summary>
    /// The cost basis. Both cost columns exist on the product and both are wrong for this purpose;
    /// only the sourcing decision's landed cost was used to build the price. If the service ever
    /// reads the product card again, this test breaks — the product's numbers are deliberately set
    /// to produce a very different, very plausible-looking answer.
    /// </summary>
    [Fact]
    public async Task Cost_comes_from_the_sourcing_decision_not_the_product_card()
    {
        using var db = new TestDb();
        using (var seed = db.ContextFor(null))
        {
            SeedBase(seed);
            // A product card whose "landed cost" is a bare last-purchase price, half the real one.
            seed.Products.Add(new Product
            {
                Id = 8_700, Buid = Bu, PartNo = "P-1", ProductName = "Widget",
                UnitCost = 30m, FinalLandedCost = 40m, QtyOnHand = 0m, ReorderPoint = 0,
                IsActive = true, CreatedBy = "seed", CreatedOn = Anchor
            });
            AcceptedQuote(seed, quoteId: 1, outcomeOn: Anchor);
            Line(seed, quoteId: 1, quoteItemId: 11, decisionId: 101, quantity: 10m,
                cost: 80m, price: 100m, productId: 8_700);
            await seed.SaveChangesAsync();
        }

        var result = await Compute(db);

        // 20% from the decision's landed cost of 80. From FinalLandedCost (40) it would be 60%.
        Assert.Equal(20.0m, result.MarginPercent);
        Assert.Equal(800m, result.CostTotal);
    }

    // ───────────────────────────── 2. the sample

    /// <summary>
    /// Drafts and lost bids are not margin. The old figure counted every quote line in the tenant,
    /// so a rejected bid priced at cost dragged the reported margin down and nobody could see why.
    /// </summary>
    [Fact]
    public async Task Only_accepted_quotes_are_sampled()
    {
        using var db = new TestDb();
        using (var seed = db.ContextFor(null))
        {
            SeedBase(seed);
            AcceptedQuote(seed, quoteId: 1, outcomeOn: Anchor);
            Line(seed, quoteId: 1, quoteItemId: 11, decisionId: 101, quantity: 10m, cost: 80m, price: 100m);

            Quote(seed, quoteId: 2, statusId: DraftStatusId, outcomeOn: null);
            Line(seed, quoteId: 2, quoteItemId: 21, decisionId: 201, quantity: 100m, cost: 10m, price: 100m);

            Quote(seed, quoteId: 3, statusId: RejectedStatusId, outcomeOn: Anchor);
            Line(seed, quoteId: 3, quoteItemId: 31, decisionId: 301, quantity: 100m, cost: 99m, price: 100m);
            await seed.SaveChangesAsync();
        }

        var result = await Compute(db);

        Assert.Equal(1, result.SampleLines);
        Assert.Equal(20.0m, result.MarginPercent);
    }

    /// <summary>An accepted quote outside the window is outside the sample.</summary>
    [Fact]
    public async Task The_period_filter_excludes_acceptances_outside_the_window()
    {
        using var db = new TestDb();
        using (var seed = db.ContextFor(null))
        {
            SeedBase(seed);
            AcceptedQuote(seed, quoteId: 1, outcomeOn: Anchor);
            Line(seed, quoteId: 1, quoteItemId: 11, decisionId: 101, quantity: 10m, cost: 80m, price: 100m);

            AcceptedQuote(seed, quoteId: 2, outcomeOn: WindowFrom.AddDays(-1));
            Line(seed, quoteId: 2, quoteItemId: 21, decisionId: 201, quantity: 1000m, cost: 10m, price: 100m);
            await seed.SaveChangesAsync();
        }

        var result = await Compute(db);

        Assert.Equal(1, result.SampleLines);
        Assert.Equal(20.0m, result.MarginPercent);
    }

    /// <summary>
    /// An accepted quote with no acceptance date belongs to no period. It is DISCLOSED rather than
    /// coalesced into the current window — the silent-fallback failure the wiring contract lists
    /// third.
    /// </summary>
    [Fact]
    public async Task An_accepted_quote_with_no_acceptance_date_is_disclosed_not_absorbed()
    {
        using var db = new TestDb();
        using (var seed = db.ContextFor(null))
        {
            SeedBase(seed);
            AcceptedQuote(seed, quoteId: 1, outcomeOn: Anchor);
            Line(seed, quoteId: 1, quoteItemId: 11, decisionId: 101, quantity: 10m, cost: 80m, price: 100m);

            AcceptedQuote(seed, quoteId: 2, outcomeOn: null);
            Line(seed, quoteId: 2, quoteItemId: 21, decisionId: 201, quantity: 1000m, cost: 10m, price: 100m);
            await seed.SaveChangesAsync();
        }

        var result = await Compute(db);

        Assert.Equal(1, result.SampleLines);
        Assert.Equal(20.0m, result.MarginPercent);
        Assert.Equal(1, result.QuotesExcludedForMissingAcceptanceDate);
    }

    /// <summary>
    /// A re-priced line inserts a SECOND decision row — the table's only uniqueness is on the
    /// idempotency key. Summing both would double-count the line's revenue and cost.
    /// </summary>
    [Fact]
    public async Task A_repriced_line_contributes_once_at_its_latest_decision()
    {
        using var db = new TestDb();
        using (var seed = db.ContextFor(null))
        {
            SeedBase(seed);
            AcceptedQuote(seed, quoteId: 1, outcomeOn: Anchor);
            Line(seed, quoteId: 1, quoteItemId: 11, decisionId: 101, quantity: 10m, cost: 90m, price: 100m,
                createdOn: Anchor.AddDays(-2));
            Line(seed, quoteId: 1, quoteItemId: 11, decisionId: 102, quantity: 10m, cost: 80m, price: 100m,
                createdOn: Anchor.AddDays(-1));
            await seed.SaveChangesAsync();
        }

        var result = await Compute(db);

        Assert.Equal(1, result.SampleLines);
        Assert.Equal(1000m, result.RevenueTotal);
        Assert.Equal(800m, result.CostTotal);
        Assert.Equal(20.0m, result.MarginPercent);
    }

    /// <summary>
    /// An accepted line priced without a sourcing decision cannot be costed. It is excluded and
    /// counted — never costed from somewhere else, which is exactly what the old figure did.
    /// </summary>
    [Fact]
    public async Task Accepted_lines_with_no_sourcing_decision_are_counted_as_a_gap()
    {
        using var db = new TestDb();
        using (var seed = db.ContextFor(null))
        {
            SeedBase(seed);
            AcceptedQuote(seed, quoteId: 1, outcomeOn: Anchor);
            Line(seed, quoteId: 1, quoteItemId: 11, decisionId: 101, quantity: 10m, cost: 80m, price: 100m);
            QuoteItemOnly(seed, quoteId: 1, quoteItemId: 12);
            await seed.SaveChangesAsync();
        }

        var result = await Compute(db);

        Assert.Equal(2, result.AcceptedQuoteLines);
        Assert.Equal(1, result.SampleLines);
        Assert.Equal(1, result.LinesWithoutSourcingEvidence);
    }

    // ───────────────────────────── 3. unavailable rather than a number

    [Fact]
    public async Task No_accepted_line_carrying_a_sourcing_decision_returns_unavailable_with_a_reason()
    {
        using var db = new TestDb();
        using (var seed = db.ContextFor(null))
        {
            SeedBase(seed);
            AcceptedQuote(seed, quoteId: 1, outcomeOn: Anchor);
            QuoteItemOnly(seed, quoteId: 1, quoteItemId: 11);
            await seed.SaveChangesAsync();
        }

        var result = await Compute(db);

        Assert.Equal(GrossMarginStatuses.Unavailable, result.Status);
        Assert.Null(result.MarginPercent);
        Assert.Contains("sourcing decision", result.Reason);
    }

    /// <summary>
    /// A currency with no approved rate must null the figure, not silently drop the row or add
    /// unconverted money to a base-currency total.
    /// </summary>
    [Fact]
    public async Task An_unconvertible_currency_makes_the_figure_unavailable_rather_than_partial()
    {
        using var db = new TestDb();
        using (var seed = db.ContextFor(null))
        {
            SeedBase(seed);
            AcceptedQuote(seed, quoteId: 1, outcomeOn: Anchor);
            Line(seed, quoteId: 1, quoteItemId: 11, decisionId: 101, quantity: 10m, cost: 80m, price: 100m);
            // No FxRate exists for USD → SAR anywhere in this fixture.
            Line(seed, quoteId: 1, quoteItemId: 12, decisionId: 102, quantity: 10m, cost: 80m, price: 100m,
                currencyId: Usd);
            await seed.SaveChangesAsync();
        }

        var result = await Compute(db);

        Assert.Equal(GrossMarginStatuses.Unavailable, result.Status);
        Assert.Null(result.MarginPercent);
        Assert.False(string.IsNullOrWhiteSpace(result.Reason));
    }

    // ───────────────────────────── 4. the two-cost-bases disclosure

    /// <summary>
    /// Sourcing decisions carry no cost-basis stamp — R20 ratified a full recompute instead of a
    /// calculation-version column — so the only evidence of the input-VAT correction is the tenant
    /// governance ledger. A sample straddling it must SAY it blends two bases and offer the
    /// comparable subset.
    /// </summary>
    [Fact]
    public async Task A_sample_straddling_the_input_tax_correction_says_so_and_offers_the_current_basis()
    {
        var changedOn = Anchor.AddDays(-5);
        using var db = new TestDb();
        using (var seed = db.ContextFor(null))
        {
            SeedBase(seed);
            PolicyChange(seed, changedOn, before: 0m, after: 100m);
            AcceptedQuote(seed, quoteId: 1, outcomeOn: Anchor);
            // Priced on the superseded basis: VAT capitalised, so the cost is 15% higher.
            Line(seed, quoteId: 1, quoteItemId: 11, decisionId: 101, quantity: 10m, cost: 92m, price: 100m,
                createdOn: changedOn.AddDays(-1));
            // Priced on the current basis.
            Line(seed, quoteId: 1, quoteItemId: 12, decisionId: 102, quantity: 10m, cost: 80m, price: 100m,
                createdOn: changedOn.AddDays(1));
            await seed.SaveChangesAsync();
        }

        var result = await Compute(db);

        Assert.Equal(GrossMarginStatuses.Available, result.Status);
        Assert.Equal(changedOn, result.CostBasisChangedOn);
        Assert.Equal(1, result.LinesOnPriorCostBasis);
        Assert.Equal(1, result.LinesOnCurrentCostBasis);
        Assert.Contains("blends two cost bases", result.CostBasisNote);
        // Blended: revenue 2000, cost 1720 → 14%. Current basis alone: 20%.
        Assert.Equal(14.0m, result.MarginPercent);
        Assert.Equal(20.0m, result.MarginPercentCurrentBasisOnly);
    }

    /// <summary>
    /// A disclosure that fires on everything carries no information. The policy row's
    /// <c>ModifiedOn</c> moves whenever ANY field changes, so the boundary is read from the ledger
    /// entry where the recoverable percent ACTUALLY moved — a price-tolerance edit is not a change
    /// of cost basis.
    /// </summary>
    [Fact]
    public async Task A_policy_edit_that_did_not_move_the_recoverable_percent_is_not_a_basis_change()
    {
        using var db = new TestDb();
        using (var seed = db.ContextFor(null))
        {
            SeedBase(seed);
            PolicyChange(seed, Anchor.AddDays(-5), before: 100m, after: 100m);
            AcceptedQuote(seed, quoteId: 1, outcomeOn: Anchor);
            Line(seed, quoteId: 1, quoteItemId: 11, decisionId: 101, quantity: 10m, cost: 80m, price: 100m,
                createdOn: Anchor.AddDays(-10));
            await seed.SaveChangesAsync();
        }

        var result = await Compute(db);

        Assert.Null(result.CostBasisChangedOn);
        Assert.Equal(0, result.LinesOnPriorCostBasis);
        Assert.Contains("no change", result.CostBasisNote);
    }

    [Fact]
    public void An_unreadable_evidence_blob_is_not_evidence_of_a_basis_change()
    {
        Assert.False(GrossMarginService.ChangedRecoverablePercent(null));
        Assert.False(GrossMarginService.ChangedRecoverablePercent("{}"));
        Assert.False(GrossMarginService.ChangedRecoverablePercent("not json"));
        Assert.False(GrossMarginService.ChangedRecoverablePercent("""{"before":null,"after":{}}"""));
    }

    // ───────────────────────────── 5. the tenant boundary

    /// <summary>
    /// Reporting reads across a whole tenant, so a single missing predicate is a cross-tenant
    /// aggregate. Another tenant's accepted quotes are seeded with numbers that would visibly move
    /// this tenant's figure, and the assertion is on the figure — not merely on a row count.
    /// </summary>
    [Fact]
    public async Task No_aggregate_crosses_a_tenant_boundary()
    {
        using var db = new TestDb();
        using (var seed = db.ContextFor(null))
        {
            SeedBase(seed);
            SeedBase(seed, OtherBu);

            AcceptedQuote(seed, quoteId: 1, outcomeOn: Anchor);
            Line(seed, quoteId: 1, quoteItemId: 11, decisionId: 101, quantity: 10m, cost: 80m, price: 100m);

            // The neighbour's book is far larger and far thinner. If any query loses its predicate,
            // 20% collapses towards 1%.
            AcceptedQuote(seed, quoteId: 2, outcomeOn: Anchor, businessUnitId: OtherBu);
            Line(seed, quoteId: 2, quoteItemId: 21, decisionId: 201, quantity: 100_000m, cost: 99m, price: 100m,
                businessUnitId: OtherBu);
            await seed.SaveChangesAsync();
        }

        var mine = await Compute(db);
        var theirs = await Compute(db, OtherBu);

        Assert.Equal(20.0m, mine.MarginPercent);
        Assert.Equal(1, mine.SampleLines);
        Assert.Equal(1000m, mine.RevenueTotal);
        Assert.Equal(1, mine.SampleQuotes);

        Assert.Equal(1.0m, theirs.MarginPercent);
        Assert.Equal(1, theirs.SampleLines);
        Assert.Equal(10_000_000m, theirs.RevenueTotal);
    }

    /// <summary>
    /// The same boundary, asserted through the tenant-scoped context the API actually uses — so the
    /// EF global filter is exercised as well as the explicit predicate.
    /// </summary>
    [Fact]
    public async Task The_tenant_scoped_context_sees_only_its_own_accepted_lines()
    {
        using var db = new TestDb();
        using (var seed = db.ContextFor(null))
        {
            SeedBase(seed);
            SeedBase(seed, OtherBu);
            AcceptedQuote(seed, quoteId: 1, outcomeOn: Anchor);
            Line(seed, quoteId: 1, quoteItemId: 11, decisionId: 101, quantity: 10m, cost: 80m, price: 100m);
            AcceptedQuote(seed, quoteId: 2, outcomeOn: Anchor, businessUnitId: OtherBu);
            Line(seed, quoteId: 2, quoteItemId: 21, decisionId: 201, quantity: 100_000m, cost: 99m, price: 100m,
                businessUnitId: OtherBu);
            await seed.SaveChangesAsync();
        }

        using var scoped = db.ContextFor(Bu);
        var result = await new GrossMarginService(scoped)
            .GetAsync(Bu, WindowFrom, WindowTo, WindowTo, CancellationToken.None);

        Assert.Equal(20.0m, result.MarginPercent);
        Assert.Equal(1, result.SampleLines);
    }

    // ───────────────────────────── helpers

    private static async Task<GrossMarginDTO> Compute(TestDb db, long businessUnitId = Bu)
    {
        using var ctx = db.ContextFor(null);
        return await new GrossMarginService(ctx)
            .GetAsync(businessUnitId, WindowFrom, WindowTo, WindowTo, CancellationToken.None);
    }

    private static void SeedBase(ErpRfqAutomationContext ctx, long businessUnitId = Bu)
    {
        // CustomerQuoteSourcingDecision carries seven composite foreign keys into the sourcing
        // aggregate — supplier quote, revision, line, quoted item, demand line, case and award.
        // Standing that whole chain up would make these tests about the sourcing fixture rather
        // than about the margin arithmetic, so referential enforcement is stood down for the seed
        // exactly as the procurement fixtures stand down CHECK constraints. The columns under test
        // (quantity, landed cost, price, currency, dates) are unaffected by that, and the tenant
        // predicate they are actually asserting is enforced in the query, not by a foreign key.
        ctx.Database.ExecuteSqlRaw("PRAGMA foreign_keys = OFF");
        ctx.Database.ExecuteSqlRaw("PRAGMA ignore_check_constraints = ON");

        Seed.EnsureBusinessUnit(ctx, businessUnitId);
        ctx.SaveChanges();

        ctx.Currencies.Add(new Currency
        {
            Id = Sar + businessUnitId, BusinessUnitId = businessUnitId, Code = "SAR",
            CurrencyName = "Saudi Riyal", ExchangeRate = 1m, IsBaseCurrency = true, IsActive = true,
            CreatedBy = "seed", CreatedOn = Anchor
        });
        ctx.Currencies.Add(new Currency
        {
            Id = Usd + businessUnitId, BusinessUnitId = businessUnitId, Code = "USD",
            CurrencyName = "US Dollar", ExchangeRate = 3.75m, IsBaseCurrency = false, IsActive = true,
            CreatedBy = "seed", CreatedOn = Anchor
        });

        Status(ctx, AcceptedStatusId + businessUnitId, businessUnitId, "ACCEPTED", "Accepted");
        Status(ctx, DraftStatusId + businessUnitId, businessUnitId, "DRAFT", "Draft");
        Status(ctx, RejectedStatusId + businessUnitId, businessUnitId, "REJECTED", "Rejected");
    }

    private static void Status(ErpRfqAutomationContext ctx, long setupId, long businessUnitId,
        string code, string value)
        => ctx.SetupMasters.Add(new SetupMaster
        {
            SetupId = setupId, BusinessUnitId = businessUnitId, SetupType = "QuoteStatus",
            SetupCode = code, SetupValue = value, IsActive = true, CreatedBy = "seed", CreatedOn = Anchor
        });

    private static void AcceptedQuote(ErpRfqAutomationContext ctx, long quoteId, DateTime? outcomeOn,
        long businessUnitId = Bu)
        => Quote(ctx, quoteId, AcceptedStatusId + businessUnitId, outcomeOn, businessUnitId);

    private static void Quote(ErpRfqAutomationContext ctx, long quoteId, long statusId, DateTime? outcomeOn,
        long businessUnitId = Bu)
        => ctx.Quotes.Add(new Quote
        {
            Id = quoteId, BusinessUnitId = businessUnitId, QuoteNo = $"Q-{quoteId}",
            StatusId = statusId, OutcomeOn = outcomeOn, CurrencyId = Sar + businessUnitId,
            // QuoteDate carries a now() store default; SQLite has no such function, so the portable
            // lane must supply the value rather than let EF fall through to the default.
            QuoteDate = Anchor,
            CreatedBy = "seed", CreatedDate = Anchor
        });

    private static void QuoteItemOnly(ErpRfqAutomationContext ctx, long quoteId, long quoteItemId,
        long businessUnitId = Bu)
        => ctx.QuoteItems.Add(new QuoteItem
        {
            Id = quoteItemId, QuoteId = quoteId, ItemDescription = $"Line {quoteItemId}",
            Quantity = 1m, UnitPrice = 1m, TotalAmount = 1m, CreatedBy = "seed", CreatedDate = Anchor
        });

    private static void Line(ErpRfqAutomationContext ctx, long quoteId, long quoteItemId, long decisionId,
        decimal quantity, decimal cost, decimal price, long? currencyId = null, long? productId = null,
        DateTime? createdOn = null, long businessUnitId = Bu)
    {
        if (!ctx.QuoteItems.Local.Any(x => x.Id == quoteItemId))
        {
            ctx.QuoteItems.Add(new QuoteItem
            {
                Id = quoteItemId, QuoteId = quoteId, ProductId = productId,
                ItemDescription = $"Line {quoteItemId}", Quantity = quantity, UnitPrice = price,
                TotalAmount = quantity * price, CreatedBy = "seed", CreatedDate = Anchor
            });
        }

        ctx.Set<CustomerQuoteSourcingDecision>().Add(new CustomerQuoteSourcingDecision
        {
            Id = decisionId, BusinessUnitId = businessUnitId, QuoteId = quoteId, QuoteItemId = quoteItemId,
            RfqId = 0, RfqItemId = 0, CommercialDemandLineId = 0, SourcingCaseId = 0, SourcingAwardId = 0,
            SupplierQuotedItemId = 0, SupplierQuoteId = 0, SupplierQuoteRevisionId = 0, SupplierQuoteLineId = 0,
            NexoraSerial = $"NXR-{decisionId}", Quantity = quantity,
            SupplierLandedUnitCost = cost, TargetMarginPercent = 20m, CustomerUnitPrice = price,
            CurrencyId = currencyId is null ? Sar + businessUnitId : currencyId.Value + businessUnitId,
            IdempotencyKey = $"seed:{decisionId}", RequestHash = new string('0', 64),
            Rationale = "seed", CreatedOn = createdOn ?? Anchor, CreatedBy = "seed",
            CorrelationId = $"corr-{decisionId}"
        });
    }

    /// <summary>Writes the governance ledger entry the cost-basis disclosure reads.</summary>
    private static void PolicyChange(ErpRfqAutomationContext ctx, DateTime occurredOn,
        decimal before, decimal after, long businessUnitId = Bu)
        => ctx.TenantGovernanceAuditEvents.Add(new TenantGovernanceAuditEvent
        {
            BusinessUnitId = businessUnitId,
            Area = "commercial-policy",
            AggregateType = nameof(CommercialMatchingPolicy),
            AggregateReference = $"tenant:{businessUnitId}",
            Action = CommercialMatchingPolicyService.ActionPolicyUpdated,
            Reason = "seed",
            EvidenceJson = JsonSerializer.Serialize(new
            {
                before = new { SupplierInputTaxRecoverablePercent = before },
                after = new { SupplierInputTaxRecoverablePercent = after }
            }),
            IdempotencyKey = $"seed-policy:{occurredOn:O}",
            ActorUserId = 1,
            OccurredOn = occurredOn
        });
}
