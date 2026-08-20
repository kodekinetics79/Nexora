using ERP_RFQ_Automation.DTOs.QuoteDTOs;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.OrderToCash;
using ERP_RFQ_Automation.Repositories;
using ERP_RFQ_Automation.Services;
using ERP_RFQ_Automation.Tests.Support;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// <c>GET /api/Quote/{id}</c> does not go through <c>QuoteService</c>. <c>QuoteController.GetById</c>
/// calls <c>QuoteRepository.GetByIdAsync</c>, and that repository holds a SECOND projection of
/// <see cref="QuoteItemResponseDTO"/> which set 17 of its properties and never included the RFQ
/// line. The service's projection, which sets all of them, is unreachable from that endpoint.
///
/// <para>The consequence is not a blank field on a screen. It is a tax position that reverses
/// itself across an edit, silently, with no user action that could be called a mistake:</para>
///
/// <list type="number">
/// <item>a rep marks a line ZERO_RATED_EXPORT with a reason and saves — stored correctly;</item>
/// <item>they reopen the draft; the edit screen reads this endpoint and is told nothing;</item>
/// <item>the screen shows "Standard rated", because a missing category defaults to standard;</item>
/// <item>they save again, and the screen truthfully posts what it was shown;</item>
/// <item>the server's preserve-when-null guard cannot help — the client is not omitting the field,
/// it is stating the wrong one — so 15% VAT is derived onto a zero-rated export.</item>
/// </list>
///
/// <para>These tests walk that exact sequence against the real read path. They assert the MONEY, not
/// merely the presence of a field: reverting the projection has to fail on a tax amount a customer
/// would be invoiced for.</para>
/// </summary>
public sealed class QuoteReadProjectionFieldCarriageTests
{
    /// <summary>
    /// Steps 1-3. The rep's stated tax position survives the read the edit screen performs.
    /// </summary>
    [Fact]
    public async Task TheReadPathTheEditScreenUses_StatesTheLinesOwnTaxPosition()
    {
        using var database = new TestDb();
        await using var db = database.ContextFor(BusinessUnitId);
        await SeedTenantAsync(db);

        var created = await new QuoteService(db, null!, null!).CreateQuoteAsync(ExportQuoteRequest());

        // The stored truth, before any read is involved.
        var stored = await db.QuoteItems.AsNoTracking().SingleAsync();
        Assert.Equal(QuoteLineTaxCategories.ZeroRatedExport, stored.TaxCategory);
        Assert.Equal(0m, stored.TaxAmount);

        var read = await new QuoteRepository(db).GetByIdAsync(created.Id, BusinessUnitId);
        var line = Assert.Single(read.QuoteItems);

        // What the edit screen is told. Before the fix all three were null, and the screen's
        // `taxCategory || 'STANDARD'` fallback turned the first null into a false statement.
        Assert.Equal(QuoteLineTaxCategories.ZeroRatedExport, line.TaxCategory);
        Assert.Equal(ExportReason, line.TaxCategoryReason);
        Assert.Equal(0m, line.TaxRatePercentApplied);
    }

    /// <summary>
    /// Steps 4-5, and the assertion that matters. The response is fed back as the next update
    /// VERBATIM — which is what a screen that renders a response and re-posts it does — and the
    /// zero-rated export must still be zero-rated afterwards.
    ///
    /// <para>With the projection reverted this fails on <c>TaxAmount</c>: the line comes back
    /// carrying 150.00 of VAT that the customer was never quoted and the exporter does not owe.</para>
    /// </summary>
    [Fact]
    public async Task ResubmittingWhatTheReadPathReturned_DoesNotConvertAnExportToStandardRated()
    {
        using var database = new TestDb();
        await using var db = database.ContextFor(BusinessUnitId);
        await SeedTenantAsync(db);

        var service = new QuoteService(db, null!, null!);
        var created = await service.CreateQuoteAsync(ExportQuoteRequest());

        var read = await new QuoteRepository(db).GetByIdAsync(created.Id, BusinessUnitId);

        // The edit screen's round trip, reproduced exactly: whatever arrived on the wire is what
        // goes back out. No test-side knowledge of the true category is used here — that is the
        // whole point, because the screen has none either.
        await service.UpdateQuoteAsync(created.Id, new QuoteUpdateRequestDTO
        {
            Id = created.Id,
            QuoteNo = read.QuoteNo,
            CustomerId = read.CustomerId,
            QuoteDate = read.QuoteDate,
            ValidUntil = read.ValidUntil,
            StatusId = read.StatusId,
            CurrencyId = read.CurrencyId,
            TotalAmount = read.TotalAmount,
            HeaderRemarks = read.HeaderRemarks,
            ModifiedBy = "test",
            DiscountTypeId = read.DiscountTypeId,
            DiscountValue = read.DiscountValue,
            QuoteItems = read.QuoteItems.Select(i => new QuoteItemUpdateRequestDTO
            {
                Id = i.Id,
                RfqItemId = i.RfqItemId,
                ProductId = i.ProductId,
                ItemDescription = i.ItemDescription,
                UnitOfMeasure = i.UnitOfMeasure,
                CustomerLineRef = i.CustomerLineRef,
                Quantity = i.Quantity,
                UnitPrice = i.UnitPrice,
                TotalAmount = i.TotalAmount,
                Discount = i.Discount,
                TaxAmount = i.TaxAmount,
                // The screen sends `i.taxCategory || 'STANDARD'`.
                TaxCategory = string.IsNullOrWhiteSpace(i.TaxCategory)
                    ? QuoteLineTaxCategories.Standard
                    : i.TaxCategory,
                TaxCategoryReason = i.TaxCategoryReason,
                DeliveryLeadTime = i.DeliveryLeadTime,
                DiscountTypeId = i.DiscountTypeId,
                DiscountValue = i.DiscountValue
            }).ToList()
        });

        var afterEdit = await db.QuoteItems.AsNoTracking().SingleAsync();
        // Money first, deliberately: reverting the projection must fail on a figure a customer
        // would be invoiced for, not merely on a category string.
        Assert.Equal(0m, afterEdit.TaxAmount);
        // The VAT the round trip used to manufacture on this export. Named so it cannot be missed.
        Assert.NotEqual(150m, afterEdit.TaxAmount);
        Assert.Equal(1_000m, afterEdit.TotalAmount);
        Assert.Equal(1_000m, (await db.Quotes.AsNoTracking().SingleAsync()).TotalAmount);
        Assert.Equal(QuoteLineTaxCategories.ZeroRatedExport, afterEdit.TaxCategory);
        Assert.Equal(ExportReason, afterEdit.TaxCategoryReason);
    }

    /// <summary>
    /// The buyer's own requested details, which live on the RFQ line and are read through it. The
    /// repository projection never included <c>Rfqitem</c>, so a quote created from an RFQ could not
    /// show a reviewer what the customer actually asked for — the fields were declared on the DTO
    /// and the screen had somewhere to put them.
    /// </summary>
    [Fact]
    public async Task TheReadPathStatesWhatTheBuyerAskedFor_ReadThroughTheRfqLine()
    {
        using var database = new TestDb();
        await using var db = database.ContextFor(BusinessUnitId);
        await SeedTenantAsync(db);
        var rfqItemId = await SeedRfqLineAsync(db);

        var request = ExportQuoteRequest();
        request.RfqId = RfqId;
        request.QuoteItems[0].RfqItemId = rfqItemId;
        var created = await new QuoteService(db, null!, null!).CreateQuoteAsync(request);

        var line = Assert.Single((await new QuoteRepository(db).GetByIdAsync(created.Id, BusinessUnitId)).QuoteItems);

        Assert.Equal("Rosemount", line.RequestedManufacturerName);
        Assert.Equal("3051S-CD", line.RequestedManufacturerPartNumber);
        Assert.Equal("MAT-88213", line.RequestedItemMaterialCode);
        Assert.Equal("3051S-CD-ALT", line.RequestedAlternatePartNumber);
        Assert.Equal(RequiredDesiredDate, line.RequestedDeliveryDate);
        Assert.Equal(45, line.RequestedLeadTimeDays);
        Assert.Equal("SAR", line.RequestedCurrency);
    }

    /// <summary>
    /// The header discount is allocated per line and STORED, because reconstructing it by
    /// subtracting a tax-inclusive total from a tax-exclusive net is what broke the printed quote
    /// (see <c>QuoteItem.HeaderDiscountAllocated</c>). The column existed and no DTO carried it, so
    /// the quote screen had no choice but to redo exactly that broken reconstruction — and printed
    /// a header discount of 80.00 where the rep had entered 200.00.
    ///
    /// <para>Worked example, one 1,000.00 line at 20% header discount and 15% VAT: allocation
    /// 200.00, taxable base 800.00, VAT 120.00, grand total 920.00. The reconstruction the screen
    /// used to perform gives 1,000.00 - 920.00 = 80.00.</para>
    /// </summary>
    [Fact]
    public async Task TheReadPathCarriesTheStoredHeaderDiscountAllocationAndTaxableBase()
    {
        using var database = new TestDb();
        await using var db = database.ContextFor(BusinessUnitId);
        await SeedTenantAsync(db);

        var request = StandardQuoteRequest();
        request.DiscountTypeId = PercentageDiscountId;
        request.DiscountValue = 20m;
        var created = await new QuoteService(db, null!, null!).CreateQuoteAsync(request);

        var read = await new QuoteRepository(db).GetByIdAsync(created.Id, BusinessUnitId);
        var line = Assert.Single(read.QuoteItems);

        Assert.Equal(200m, line.HeaderDiscountAllocated);
        Assert.Equal(800m, line.TaxableBase);
        Assert.Equal(120m, line.TaxAmount);
        Assert.Equal(920m, read.TotalAmount);
        // The figure the screen used to print as the header discount, named so the regression is
        // unmistakable: it is not 200.00 and it never was.
        Assert.NotEqual(80m, line.HeaderDiscountAllocated);
    }

    // ------------------------------------------------------------------------------- fixture

    private const string ExportReason = "Goods exported to Bahrain under bill of lading BL-4471";
    private static readonly DateTime RequiredDesiredDate = new(2026, 12, 15, 0, 0, 0, DateTimeKind.Utc);

    private static QuoteCreateRequestDTO StandardQuoteRequest() => new()
    {
        QuoteNo = $"QT-RP-{Guid.NewGuid():N}"[..12],
        CustomerId = CustomerId,
        BusinessUnitId = BusinessUnitId,
        CreatedBy = "test",
        QuoteDate = DateTime.UtcNow,
        TotalAmount = 0m,
        QuoteItems =
        [
            new QuoteItemCreateRequestDTO
            {
                ProductId = ProductId,
                ItemDescription = "Pressure transmitter",
                Quantity = 1m,
                UnitPrice = 1_000m,
                TaxAmount = 0m,
                TotalAmount = 0m,
                TaxCategory = QuoteLineTaxCategories.Standard
            }
        ]
    };

    private static QuoteCreateRequestDTO ExportQuoteRequest()
    {
        var request = StandardQuoteRequest();
        request.QuoteItems[0].TaxCategory = QuoteLineTaxCategories.ZeroRatedExport;
        request.QuoteItems[0].TaxCategoryReason = ExportReason;
        return request;
    }

    private static async Task<long> SeedRfqLineAsync(ErpRfqAutomationContext db)
    {
        db.Rfqs.Add(new Rfq
        {
            Id = RfqId,
            Rfqno = "NXR-RFQ-77001-2026-00000001",
            BusinessUnitId = BusinessUnitId,
            CustomerId = CustomerId,
            CreatedBy = "test",
            CreatedDate = DateTime.UtcNow
        });
        var line = new Rfqitem
        {
            Rfqid = RfqId,
            LineItemNo = "00010",
            Quantity = 1,
            ManufacturerName = "Rosemount",
            ManufacturerPartNumber = "3051S-CD",
            ItemMaterialCode = "MAT-88213",
            AlternatePartNumber = "3051S-CD-ALT",
            RequiredDesiredDate = RequiredDesiredDate,
            LeadTime = 45,
            Currency = "SAR",
            CreatedBy = "test",
            CreatedDate = DateTime.UtcNow
        };
        db.Rfqitems.Add(line);
        await db.SaveChangesAsync();
        return line.Id;
    }

    private static async Task SeedTenantAsync(ErpRfqAutomationContext db)
    {
        Seed.EnsureBusinessUnit(db, BusinessUnitId);
        Seed.Customer(db, CustomerId, BusinessUnitId, "Gulf Export Trading");
        db.Products.Add(new Product
        {
            Id = ProductId,
            ProductName = "Rosemount 3051S coplanar transmitter",
            PartNo = "3051S-CD",
            Buid = BusinessUnitId,
            CreatedBy = "test",
            CreatedOn = DateTime.UtcNow,
            IsActive = true
        });
        db.SetupMasters.AddRange(
            new SetupMaster
            {
                SetupId = PercentageDiscountId, SetupType = "DiscountType", SetupCode = "PERCENTAGE",
                SetupValue = "PERCENTAGE", BusinessUnitId = BusinessUnitId, IsActive = true,
                CreatedBy = "test", CreatedOn = DateTime.UtcNow
            },
            new SetupMaster
            {
                SetupId = QuoteDraftStatusId, SetupType = "QuoteStatus", SetupCode = "DRAFT",
                SetupValue = "DRAFT", BusinessUnitId = BusinessUnitId, IsActive = true,
                CreatedBy = "test", CreatedOn = DateTime.UtcNow
            });
        await db.SaveChangesAsync();
    }

    private const long BusinessUnitId = 77_001;
    private const long CustomerId = 77_002;
    private const long ProductId = 77_004;
    private const long RfqId = 77_006;
    private const long PercentageDiscountId = 77_010;
    private const long QuoteDraftStatusId = 42;
}
