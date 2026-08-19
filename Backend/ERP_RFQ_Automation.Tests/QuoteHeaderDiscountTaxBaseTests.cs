using ERP_RFQ_Automation.DTOs.QuoteDTOs;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Services;
using ERP_RFQ_Automation.Tests.Support;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// A quote-level discount used to be taken on a subtotal that already had each line's VAT inside
/// it, and the VAT itself was derived BEFORE the discount and never re-derived. Three things
/// followed, all of them visible to a customer:
///
/// <list type="number">
/// <item>the create screen showed a total the server never saved — 10,500.00 against 10,350.00 on
/// one 10,000.00 line at 10%;</item>
/// <item>the stored VAT was the VAT on the pre-discount base — 1,500.00 where 1,350.00 was due,
/// and VAT stated on a document is VAT that is owed;</item>
/// <item>the printed "Additional Discount" was reconstructed by subtracting the stored total from a
/// sum that had tax in it, so it came out 15% larger than the figure the rep typed.</item>
/// </list>
///
/// <para>The header discount is now taken on the tax-EXCLUSIVE net, allocated across the lines pro
/// rata, and each line's tax is derived from what is left. Which also makes the line split
/// derivable for an e-invoice: base, allowance, rate and tax are all READ off the line rather than
/// reconstructed by whoever needs them next.</para>
/// </summary>
public sealed class QuoteHeaderDiscountTaxBaseTests
{
    /// <summary>
    /// The worked example from the defect report, end to end.
    /// <code>
    ///   line net    10 x 1,000.00              = 10,000.00
    ///   header      10% of the NET             =  1,000.00   (was 1,150.00, taken on 11,500.00)
    ///   base        10,000.00 - 1,000.00       =  9,000.00
    ///   VAT         9,000.00 x 15%             =  1,350.00   (was 1,500.00, on the pre-discount base)
    ///   total       9,000.00 + 1,350.00        = 10,350.00
    /// </code>
    /// </summary>
    [Fact]
    public async Task HeaderDiscount_IsTakenOnTheNetAndTaxFollowsTheDiscountedBase()
    {
        using var database = new TestDb();
        await using var db = database.ContextFor(BusinessUnitId);
        SeedTenant(db);
        var service = new QuoteService(db, null!, null!);

        await service.CreateQuoteAsync(Request(headerDiscountPercent: 10m,
            (Quantity: 10m, UnitPrice: 1_000m)));

        var quote = await db.Quotes.AsNoTracking().SingleAsync();
        var line = await db.QuoteItems.AsNoTracking().SingleAsync();
        // Money first, deliberately. Reverting the fix must fail on the VAT and the total — the
        // figures a customer sees — not merely on the absence of a new column.
        Assert.Equal(1_350m, line.TaxAmount);
        // The VAT the old arithmetic stated on this quote. Named so the regression is unmistakable.
        Assert.NotEqual(1_500m, line.TaxAmount);
        Assert.Equal(9_000m, line.TaxableBase);
        Assert.Equal(10_350m, quote.TotalAmount);
        Assert.Equal(15m, line.TaxRatePercentApplied);
        Assert.Equal(1_000m, line.HeaderDiscountAllocated);
    }

    /// <summary>
    /// Without a header discount nothing about the arithmetic moves. This is the CONTROL: it passes
    /// against the old code and the new one alike, which is what shows the other tests in this class
    /// are detecting the defect rather than the suite simply being red.
    ///
    /// <para>It therefore asserts only figures both versions produce. <c>HeaderDiscountAllocated</c>
    /// is deliberately NOT asserted here — the old code cannot set a column it does not know about,
    /// and asserting it would turn the control into another regression test.</para>
    /// </summary>
    [Fact]
    public async Task WithoutAHeaderDiscount_TheLineIsUnchanged()
    {
        using var database = new TestDb();
        await using var db = database.ContextFor(BusinessUnitId);
        SeedTenant(db);
        var service = new QuoteService(db, null!, null!);

        await service.CreateQuoteAsync(Request(headerDiscountPercent: null,
            (Quantity: 10m, UnitPrice: 1_000m)));

        var quote = await db.Quotes.AsNoTracking().SingleAsync();
        var line = await db.QuoteItems.AsNoTracking().SingleAsync();
        Assert.Equal(10_000m, line.TaxableBase);
        Assert.Equal(1_500m, line.TaxAmount);
        Assert.Equal(11_500m, quote.TotalAmount);
    }

    /// <summary>
    /// The allocation must sum EXACTLY to the header discount, including when the split does not
    /// divide cleanly. 10% of 100.00 across three lines of 33.33/33.33/33.34 is 3.333/3.333/3.334,
    /// and three independently rounded shares would be 3.33 each — 9.99 against a 10.00 discount,
    /// leaving the printed total a halala away from the sum of its own lines.
    /// </summary>
    [Fact]
    public async Task Allocation_SumsExactlyToTheHeaderDiscount_WhenItDoesNotDivideCleanly()
    {
        using var database = new TestDb();
        await using var db = database.ContextFor(BusinessUnitId);
        SeedTenant(db);
        var service = new QuoteService(db, null!, null!);

        await service.CreateQuoteAsync(Request(headerDiscountPercent: 10m,
            (Quantity: 1m, UnitPrice: 33.33m),
            (Quantity: 1m, UnitPrice: 33.33m),
            (Quantity: 1m, UnitPrice: 33.34m)));

        var quote = await db.Quotes.AsNoTracking().SingleAsync();
        var lines = await db.QuoteItems.AsNoTracking().OrderBy(i => i.Id).ToListAsync();
        Assert.Equal(3, lines.Count);
        Assert.Equal(10.00m, lines.Sum(l => l.HeaderDiscountAllocated ?? 0m));
        // And the header still reconciles to the lines it is made of.
        Assert.Equal(quote.TotalAmount, lines.Sum(l => l.TotalAmount));
        Assert.Equal(90.00m, lines.Sum(l => l.TaxableBase));
    }

    /// <summary>
    /// What an e-invoice needs off each line: a taxable base, the allowance that produced it, the
    /// rate applied and the tax. All four read from the row, none of them reconstructed. The
    /// reconstruction is what produced a printed discount 15% larger than the one that was agreed,
    /// so the assertion here is that the row is self-describing.
    /// </summary>
    [Fact]
    public async Task EveryLineCarriesItsOwnBaseAllowanceRateAndTax()
    {
        using var database = new TestDb();
        await using var db = database.ContextFor(BusinessUnitId);
        SeedTenant(db);
        var service = new QuoteService(db, null!, null!);

        await service.CreateQuoteAsync(Request(headerDiscountPercent: 10m,
            (Quantity: 4m, UnitPrice: 250m),
            (Quantity: 2m, UnitPrice: 500m)));

        var lines = await db.QuoteItems.AsNoTracking().OrderBy(i => i.Id).ToListAsync();
        var quote = await db.Quotes.AsNoTracking().SingleAsync();

        foreach (var line in lines)
        {
            var gross = line.Quantity * line.UnitPrice;
            var allowance = (line.Discount ?? 0m) + (line.HeaderDiscountAllocated ?? 0m);
            Assert.Equal(gross - allowance, line.TaxableBase);
            Assert.NotNull(line.TaxRatePercentApplied);
            Assert.Equal(decimal.Round(line.TaxableBase * line.TaxRatePercentApplied!.Value / 100m, 2,
                MidpointRounding.AwayFromZero), line.TaxAmount);
        }

        // Both lines are 1,000.00 net, so a 10% header discount splits 100.00 / 100.00.
        Assert.Equal(100m, lines[0].HeaderDiscountAllocated);
        Assert.Equal(100m, lines[1].HeaderDiscountAllocated);
        Assert.Equal(quote.TotalAmount, lines.Sum(l => l.TaxableBase) + lines.Sum(l => l.TaxAmount ?? 0m));
    }

    /// <summary>
    /// A zero-rated line takes its share of the header discount like any other, but derives no tax.
    /// The share must still be computed off the line's net, otherwise a quote mixing treatments
    /// would push the whole discount onto the taxable lines and understate the VAT due.
    /// </summary>
    [Fact]
    public async Task AZeroRatedLine_TakesItsShareOfTheDiscountAndStillDerivesNoTax()
    {
        using var database = new TestDb();
        await using var db = database.ContextFor(BusinessUnitId);
        SeedTenant(db);
        var service = new QuoteService(db, null!, null!);

        var request = Request(headerDiscountPercent: 10m,
            (Quantity: 1m, UnitPrice: 1_000m),
            (Quantity: 1m, UnitPrice: 1_000m));
        request.QuoteItems[1].TaxCategory = "ZERO_RATED_EXPORT";
        request.QuoteItems[1].TaxCategoryReason = "Goods shipped to Bahrain, BoL on file";

        await service.CreateQuoteAsync(request);
        var quote = await db.Quotes.AsNoTracking().SingleAsync();
        var lines = await db.QuoteItems.AsNoTracking().OrderBy(i => i.Id).ToListAsync();

        Assert.Equal(100m, lines[0].HeaderDiscountAllocated);
        Assert.Equal(100m, lines[1].HeaderDiscountAllocated);
        Assert.Equal(135m, lines[0].TaxAmount);
        Assert.Equal(0m, lines[1].TaxAmount);
        Assert.Equal(0m, lines[1].TaxRatePercentApplied);
        Assert.Equal(900m + 135m + 900m, quote.TotalAmount);
    }

    // ------------------------------------------------------------------------------- fixture

    private static QuoteCreateRequestDTO Request(decimal? headerDiscountPercent,
        params (decimal Quantity, decimal UnitPrice)[] lines) => new()
        {
            QuoteNo = $"QT-HD-{Guid.NewGuid():N}"[..12],
            CustomerId = CustomerId,
            BusinessUnitId = BusinessUnitId,
            CreatedBy = "test",
            QuoteDate = DateTime.UtcNow,
            TotalAmount = 0m,
            DiscountTypeId = headerDiscountPercent.HasValue ? PercentageDiscountId : null,
            DiscountValue = headerDiscountPercent,
            QuoteItems = lines.Select(l => new QuoteItemCreateRequestDTO
            {
                ProductId = ProductId,
                ItemDescription = "Turbine spare",
                Quantity = l.Quantity,
                UnitPrice = l.UnitPrice,
                TaxAmount = 0m,
                TotalAmount = 0m
            }).ToList()
        };

    private static void SeedTenant(ErpRfqAutomationContext db)
    {
        // Use the shared seed helpers rather than hand-rolled entities: BusinessUnit and Customer
        // both carry required columns (BusinessUnitCode among them) that a partial fixture silently
        // misses, and a fixture that does not match the shape production writes is the failure mode
        // that lets a green test certify a path nothing exercises.
        Seed.EnsureBusinessUnit(db, BusinessUnitId);
        Seed.Customer(db, CustomerId, BusinessUnitId, "Saudi Electricity Company");
        db.Products.Add(new Product
        {
            Id = ProductId,
            ProductName = "SGT5-2000E flexible coupling",
            PartNo = "5-2841-A2A",
            Buid = BusinessUnitId,
            CreatedBy = "test",
            CreatedOn = DateTime.UtcNow,
            IsActive = true
        });
        db.SetupMasters.AddRange(
            new SetupMaster
            {
                SetupId = PercentageDiscountId,
                SetupType = "DiscountType",
                SetupCode = "PERCENTAGE",
                SetupValue = "PERCENTAGE",
                BusinessUnitId = BusinessUnitId,
                IsActive = true,
                CreatedBy = "test",
                CreatedOn = DateTime.UtcNow
            },
            new SetupMaster
            {
                SetupId = QuoteDraftStatusId,
                SetupType = "QuoteStatus",
                SetupCode = "DRAFT",
                SetupValue = "DRAFT",
                BusinessUnitId = BusinessUnitId,
                IsActive = true,
                CreatedBy = "test",
                CreatedOn = DateTime.UtcNow
            });
        db.SaveChanges();
    }

    private const long BusinessUnitId = 74_001;
    private const long CustomerId = 74_002;
    private const long ProductId = 74_004;
    private const long PercentageDiscountId = 74_010;
    private const long QuoteDraftStatusId = 42;
}
