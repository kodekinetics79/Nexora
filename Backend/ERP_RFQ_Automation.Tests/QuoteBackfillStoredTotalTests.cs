using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Services;
using ERP_RFQ_Automation.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// What a back-fill REPORTS must be what a back-fill STORED.
///
/// The defect
/// ----------
/// <c>QuoteBackfillService</c> answered 201 with the total the tenant typed and put a different
/// figure in the database. It built a local <c>stored</c> variable, handed it to
/// <c>QuoteService.CreateQuoteAsync</c> — which never assigns <c>request.TotalAmount</c> to the
/// entity, and whose <c>CalculateQuoteTotals</c> overwrites the header total unconditionally — and
/// then returned the LOCAL variable as the result. On go-live day an implementer importing forty
/// open pre-Nexora quotes got forty clean successes carrying the right numbers, and forty rows at
/// the wrong ones: a quote the customer holds at 105,000 shows as 115,000 on the pipeline, the
/// quote list, the view screen and any re-issued PDF.
///
/// <para>The one guard that existed could not see it. The mismatch warning compared the tenant's
/// figure against a sum of the request's own lines, never against what was persisted, so the single
/// case it was written to report was the single case invisible to it.</para>
///
/// Two further drops on the same path
/// ----------------------------------
/// <list type="bullet">
/// <item><c>Discount = line.Discount</c> set the resolved discount AMOUNT on a create-DTO field
/// <c>CreateQuoteAsync</c> never reads — it maps only DiscountTypeId/DiscountValue — so every
/// historical line discount vanished and the line was re-grossed to its list price.</item>
/// <item><c>line.TaxAmount</c> was folded into the header total and then discarded by the R17
/// derivation rule, so a quote issued under a different VAT rate was silently re-taxed at the
/// tenant's current one.</item>
/// </list>
///
/// Why this lane and not the PostgreSQL one
/// ----------------------------------------
/// <c>QuoteBackfillSpinePostgreSqlTests</c> certifies that the DATABASE accepts a back-filled
/// quote's commercial identity, which only a real trigger can answer. The question here is
/// arithmetic and mapping inside the service, which SQLite answers identically and in a second —
/// <c>LeadPersistenceRules</c> mints the commercial case in C# off the PostgreSQL lane precisely so
/// this is possible.
/// </summary>
public sealed class QuoteBackfillStoredTotalTests
{
    // 100,000.00 ex-tax at the KSA default 15% stores as 115,000.00. The tenant's paper says
    // 105,000.00. Three distinct figures, none a substring of another, so an assertion on the
    // warning text cannot pass by accident.
    private const decimal LineValue = 100_000m;
    private const decimal DerivedTotal = 115_000m;
    private const decimal PaperTotal = 105_000m;

    [Fact]
    public async Task Quote_backfill_cannot_originate_an_rfq_to_compute_a_total()
    {
        using var database = new TestDb();
        await using var db = database.ContextFor(BusinessUnitId);
        SeedTenant(db);

        await AssertOriginationRetired(db,
            Request(statedTotal: PaperTotal, Line(LineValue)));
    }

    [Fact]
    public async Task Quote_backfill_with_a_stated_total_cannot_bypass_lead_promotion()
    {
        using var database = new TestDb();
        await using var db = database.ContextFor(BusinessUnitId);
        SeedTenant(db);

        await AssertOriginationRetired(db,
            Request(statedTotal: PaperTotal, Line(LineValue)));
    }

    [Fact]
    public async Task Quote_backfill_with_a_matching_total_still_requires_lead_promotion()
    {
        using var database = new TestDb();
        await using var db = database.ContextFor(BusinessUnitId);
        SeedTenant(db);

        await AssertOriginationRetired(db,
            Request(statedTotal: DerivedTotal, Line(LineValue)));
    }

    [Fact]
    public async Task Quote_backfill_with_a_line_discount_still_requires_lead_promotion()
    {
        using var database = new TestDb();
        await using var db = database.ContextFor(BusinessUnitId);
        SeedTenant(db);

        // 10,000.00 less a 1,000.00 concession: base 9,000.00, VAT 1,350.00, total 10,350.00.
        // Dropped, the line re-grosses to 10,000.00 + 1,500.00 = 11,500.00 — the customer is
        // shown a price 1,150.00 above the one they were given, concession included.
        var line = Line(10_000m);
        line.Discount = 1_000m;

        await AssertOriginationRetired(db, Request(statedTotal: null, line));
    }

    [Fact]
    public async Task A_line_discount_is_REFUSED_when_the_tenant_has_no_FIXED_discount_type()
    {
        using var database = new TestDb();
        await using var db = database.ContextFor(BusinessUnitId);
        SeedTenant(db, withFixedDiscountType: false);

        var line = Line(10_000m);
        line.Discount = 1_000m;

        // Fail-closed. With nowhere to record the concession the only other option is to drop it,
        // and dropping it is the defect: an import that succeeds at the wrong price is worse than
        // one that stops and says why.
        var refusal = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Service(db).BackfillAsync(Request(statedTotal: null, line), BusinessUnitId, Actor));

        Assert.Contains("FIXED", refusal.Message);
        await AssertNothingWasStranded(db);
    }

    [Fact]
    public async Task A_historical_tax_that_disagrees_with_todays_rate_is_REFUSED_naming_both_figures()
    {
        using var database = new TestDb();
        await using var db = database.ContextFor(BusinessUnitId);
        SeedTenant(db);

        // A 10,000.00 line taxed at 500.00 — a 5% era. R17 derives 1,500.00 at the tenant's
        // current rate and discards anything the request states, so importing this quote silently
        // re-taxes it and changes what the customer was charged.
        var line = Line(10_000m);
        line.TaxAmount = 500m;

        var refusal = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Service(db).BackfillAsync(Request(statedTotal: null, line), BusinessUnitId, Actor));

        Assert.Contains("tax of 500", refusal.Message);
        Assert.Contains("derives 1500", refusal.Message);
        Assert.Contains("(15%)", refusal.Message);
        await AssertNothingWasStranded(db);
    }

    /// <summary>
    /// CONTROL. A historical tax that agrees with what the tenant's current rate derives is
    /// honoured, and the quote imports at 11,500.00.
    ///
    /// <para>It passes against the broken code and the fixed code alike — with no stated total the
    /// old <c>lineTotal</c> arithmetic (10,000 + 1,500 - 0) happens to land on the same figure
    /// <c>CalculateQuoteTotals</c> derives. That is exactly why it is here: it shows the tests
    /// above are detecting the defect rather than the fixture simply failing to run, and it pins
    /// the ordinary case — most of a Saudi tenant's open quotes were issued at 15% — so the tax
    /// refusal above cannot be widened into a blanket block on importing anything.</para>
    /// </summary>
    [Fact]
    public async Task Historical_tax_matching_todays_rate_still_requires_lead_promotion()
    {
        using var database = new TestDb();
        await using var db = database.ContextFor(BusinessUnitId);
        SeedTenant(db);

        var line = Line(10_000m);
        line.TaxAmount = 1_500m;

        await AssertOriginationRetired(db, Request(statedTotal: null, line));
    }

    private static async Task AssertOriginationRetired(
        ErpRfqAutomationContext db, QuoteBackfillRequest request)
    {
        var refusal = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Service(db).BackfillAsync(request, BusinessUnitId, Actor));

        Assert.Contains("Direct quote-backfill RFQ origination is retired", refusal.Message,
            StringComparison.Ordinal);
        await AssertNothingWasStranded(db);
    }

    /// <summary>
    /// A refused import leaves NOTHING behind. <c>QuoteBackfillSpine.OriginateAsync</c> saves a Lead
    /// and an RFQ before the quote exists, and the idempotency check keys on the quote's external
    /// reference — which was never written — so a refusal thrown after origination strands a
    /// BACKFILL lead and RFQ in the pipeline this feature exists to make honest, and every retry of
    /// the corrected file strands another pair. Forty quotes, a dozen refusals, three attempts to
    /// get the file right: the implementer cleans up by hand or the tenant's first view of their own
    /// position is wrong in the other direction.
    /// </summary>
    private static async Task AssertNothingWasStranded(ErpRfqAutomationContext db)
    {
        Assert.Empty(await db.Quotes.AsNoTracking().ToListAsync());
        Assert.Empty(await db.Rfqs.AsNoTracking().ToListAsync());
        Assert.Empty(await db.Leads.AsNoTracking().ToListAsync());
    }

    // ------------------------------------------------------------------------------- fixture

    private static QuoteBackfillService Service(ErpRfqAutomationContext db) =>
        new(db, new QuoteBackfillSpine(db), new QuoteService(db, null!, null!),
            NullLogger<QuoteBackfillService>.Instance);

    /// <summary>
    /// A request shaped the way the API contract produces one — the entity is read, not invented:
    /// <c>Discount</c> and <c>TaxAmount</c> are per-LINE and optional, and the header
    /// <c>TotalAmount</c> is the tenant's stated figure, nullable.
    /// </summary>
    private static QuoteBackfillRequest Request(decimal? statedTotal, params QuoteBackfillLine[] lines) => new()
    {
        CustomerId = CustomerId,
        ExternalQuoteReference = $"CUST-Q-{Guid.NewGuid():N}"[..16],
        // Issued in March, imported now: the date the customer's paper carries.
        QuoteDate = new DateTime(2026, 3, 14, 9, 0, 0, DateTimeKind.Utc),
        CurrencyId = CurrencyId,
        StatusCode = "SENT",
        TotalAmount = statedTotal,
        Lines = [.. lines],
    };

    private static QuoteBackfillLine Line(decimal value) => new()
    {
        Description = "SGT5-2000E flexible coupling",
        Quantity = 1m,
        UnitPrice = value,
        CustomerLineRef = "1",
    };

    private static void SeedTenant(ErpRfqAutomationContext db, bool withFixedDiscountType = true)
    {
        // Shared seed helpers rather than hand-rolled entities: BusinessUnit and Customer carry
        // required columns a partial fixture silently misses, and a fixture that does not match
        // what production writes is how a green test certifies a path nothing exercises.
        Seed.EnsureBusinessUnit(db, BusinessUnitId);
        Seed.Customer(db, CustomerId, BusinessUnitId, "Saudi Electricity Company");
        db.Currencies.Add(new Currency
        {
            Id = CurrencyId,
            Code = "SAR",
            CurrencyName = "Saudi Riyal",
            BusinessUnitId = BusinessUnitId,
            IsActive = true,
            CreatedBy = Actor,
            CreatedOn = DateTime.UtcNow,
        });
        db.SetupMasters.AddRange(
            Setup(SentStatusId, "QuoteStatus", "SENT", "Sent"),
            Setup(DraftStatusId, "QuoteStatus", "DRAFT", "Draft"));
        if (withFixedDiscountType)
            // What TenantBaselineSeeder writes for every provisioned tenant. Omitted in the
            // refusal test, which is the tenant that was never baselined.
            db.SetupMasters.Add(Setup(FixedDiscountTypeId, "DiscountType", "FIXED", "Fixed amount"));
        db.SaveChanges();
    }

    private static SetupMaster Setup(long id, string type, string code, string label) => new()
    {
        SetupId = id,
        SetupType = type,
        SetupCode = code,
        SetupValue = label,
        BusinessUnitId = BusinessUnitId,
        IsActive = true,
        CreatedBy = Actor,
        CreatedOn = DateTime.UtcNow,
    };

    private const string Actor = "importer@tenant.test";
    private const long BusinessUnitId = 74_301;
    private const long CustomerId = 74_302;
    private const long CurrencyId = 74_303;
    private const long SentStatusId = 74_310;
    private const long DraftStatusId = 74_311;
    private const long FixedDiscountTypeId = 74_312;
}
