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
    public async Task The_returned_total_is_the_one_that_was_persisted()
    {
        using var database = new TestDb();
        await using var db = database.ContextFor(BusinessUnitId);
        SeedTenant(db);

        var result = await Service(db).BackfillAsync(
            Request(statedTotal: PaperTotal, Line(LineValue)), BusinessUnitId, Actor);

        var quote = await db.Quotes.AsNoTracking().SingleAsync(q => q.Id == result.QuoteId);

        // The assertion the defect fails on: the answer and the row must be the same number.
        Assert.Equal(quote.TotalAmount, result.TotalAmount);
        Assert.Equal(DerivedTotal, result.TotalAmount);

        // And named explicitly, because this is the figure the old code reported while storing
        // another. A back-fill that answers with the tenant's own number has proved nothing.
        Assert.NotEqual(PaperTotal, result.TotalAmount);
    }

    [Fact]
    public async Task A_stated_total_that_disagrees_with_what_was_stored_warns_naming_BOTH_figures()
    {
        using var database = new TestDb();
        await using var db = database.ContextFor(BusinessUnitId);
        SeedTenant(db);

        var result = await Service(db).BackfillAsync(
            Request(statedTotal: PaperTotal, Line(LineValue)), BusinessUnitId, Actor);

        var warning = Assert.IsType<string>(result.TotalMismatchWarning);

        // Both numbers, and which is which. The old warning could only ever name the tenant's
        // figure and a sum of the request's lines — 105,000 against 100,000 — so it described a
        // discrepancy that did not matter and stayed silent about the one that did.
        Assert.Contains("Nexora recomputed this quote at 115000", warning);
        Assert.Contains("the customer holds 105000", warning);
    }

    [Fact]
    public async Task A_stated_total_that_matches_what_was_stored_warns_about_nothing()
    {
        using var database = new TestDb();
        await using var db = database.ContextFor(BusinessUnitId);
        SeedTenant(db);

        // The tenant's paper agrees with the tax-inclusive figure Nexora derives. Nothing is wrong,
        // so nothing may be reported: the old comparison, run against the ex-tax sum of the lines,
        // would have cried mismatch on a perfectly faithful import and taught the implementer to
        // ignore the field.
        var result = await Service(db).BackfillAsync(
            Request(statedTotal: DerivedTotal, Line(LineValue)), BusinessUnitId, Actor);

        Assert.Null(result.TotalMismatchWarning);
        Assert.Equal(DerivedTotal, result.TotalAmount);
    }

    [Fact]
    public async Task A_line_discount_round_trips_instead_of_being_dropped()
    {
        using var database = new TestDb();
        await using var db = database.ContextFor(BusinessUnitId);
        SeedTenant(db);

        // 10,000.00 less a 1,000.00 concession: base 9,000.00, VAT 1,350.00, total 10,350.00.
        // Dropped, the line re-grosses to 10,000.00 + 1,500.00 = 11,500.00 — the customer is
        // shown a price 1,150.00 above the one they were given, concession included.
        var line = Line(10_000m);
        line.Discount = 1_000m;

        var result = await Service(db).BackfillAsync(
            Request(statedTotal: null, line), BusinessUnitId, Actor);

        var stored = await db.QuoteItems.AsNoTracking().SingleAsync();
        Assert.Equal(1_000m, stored.Discount);
        Assert.Equal(9_000m, stored.TaxableBase);
        Assert.Equal(1_350m, stored.TaxAmount);
        Assert.Equal(10_350m, result.TotalAmount);

        // The re-grossed figure, named so a regression is unmistakable.
        Assert.NotEqual(11_500m, result.TotalAmount);
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
    public async Task CONTROL_A_historical_tax_matching_todays_rate_imports_unchanged()
    {
        using var database = new TestDb();
        await using var db = database.ContextFor(BusinessUnitId);
        SeedTenant(db);

        var line = Line(10_000m);
        line.TaxAmount = 1_500m;

        var result = await Service(db).BackfillAsync(
            Request(statedTotal: null, line), BusinessUnitId, Actor);

        var quote = await db.Quotes.AsNoTracking().SingleAsync(q => q.Id == result.QuoteId);
        Assert.Equal(11_500m, quote.TotalAmount);
        Assert.Equal(11_500m, result.TotalAmount);
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
