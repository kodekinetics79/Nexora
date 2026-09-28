using ERP_RFQ_Automation.DTOs.QuoteDTOs;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Repositories;
using ERP_RFQ_Automation.Services;
using ERP_RFQ_Automation.Tests.Support;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// Pilot audit UX-03 / CB-08: the buyer's material number, maker and maker part number reach the
/// quote line on EVERY path that makes one from an RFQ line — draft preparation, "Price it" on a
/// line not yet on the quote, the create and edit screens, a revision, and RFQ approval — and an
/// edit that does not send them keeps them.
/// </summary>
public sealed class QuoteLineBuyerIdentityMappingTests
{
    private const long Bu = 97_101;
    private const long DraftStatusId = 97_102;
    private const long SentStatusId = 97_103;
    private const long CustomerId = 97_104;

    [Fact]
    public async Task Preparing_the_draft_copies_material_maker_and_part_from_each_rfq_line()
    {
        var (db, ctx, rfq) = await SeedAsync(97_110, markedLines: 2);
        using var _ = db;
        await using var __ = ctx;

        var draft = await new QuoteService(ctx, null!, null!).PrepareDraftFromRfqAsync(rfq.Id, Bu, "rep@nexora.sa");

        Assert.All(draft.QuoteItems, AssertCarried);
    }

    [Fact]
    public async Task Pricing_a_line_that_is_not_on_the_quote_yet_adds_it_with_its_buyer_identity()
    {
        var (db, ctx, rfq) = await SeedAsync(97_120, markedLines: 1, lineCount: 2);
        using var _ = db;
        await using var __ = ctx;
        var service = new QuoteService(ctx, null!, null!);
        var notOnQuote = rfq.Rfqitems.OrderBy(x => x.Id).Last();

        var quote = await service.PriceRfqLineAsync(rfq.Id, notOnQuote.Id, Bu, "rep@nexora.sa", 99.50m, false, null);

        var added = Assert.Single(quote.QuoteItems, x => x.RfqItemId == notOnQuote.Id);
        AssertCarried(added);
    }

    [Fact]
    public async Task Pricing_a_line_written_before_the_columns_existed_fills_them_and_keeps_existing_ones()
    {
        var (db, ctx, rfq) = await SeedAsync(97_130, markedLines: 2);
        using var _ = db;
        await using var __ = ctx;
        var service = new QuoteService(ctx, null!, null!);
        var draft = await service.PrepareDraftFromRfqAsync(rfq.Id, Bu, "rep@nexora.sa");
        var lines = await ctx.QuoteItems.Where(x => x.QuoteId == draft.Id).OrderBy(x => x.Id).ToListAsync();
        // A legacy line (nothing stored) and a line whose maker the rep already corrected.
        lines[0].CustomerMaterialCode = lines[0].ManufacturerName = lines[0].ManufacturerPartNumber = null;
        lines[1].ManufacturerName = "SAFT (corrected)";
        await ctx.SaveChangesAsync();
        ctx.ChangeTracker.Clear();

        await service.PriceRfqLineAsync(rfq.Id, lines[0].RfqitemId!.Value, Bu, "rep@nexora.sa", 10m, false, null);
        var quote = await service.PriceRfqLineAsync(rfq.Id, lines[1].RfqitemId!.Value, Bu, "rep@nexora.sa", 20m, false, null);

        AssertCarried(quote.QuoteItems.Single(x => x.Id == lines[0].Id));
        Assert.Equal("SAFT (corrected)", quote.QuoteItems.Single(x => x.Id == lines[1].Id).ManufacturerName);
    }

    [Fact]
    public async Task A_quote_created_on_screen_from_rfq_lines_carries_them_and_a_typed_line_keeps_what_was_typed()
    {
        var (db, ctx, rfq) = await SeedAsync(97_140, markedLines: 1);
        using var _ = db;
        await using var __ = ctx;
        var line = rfq.Rfqitems.Single();

        var created = await new QuoteService(ctx, null!, null!).CreateQuoteAsync(new QuoteCreateRequestDTO
        {
            BusinessUnitId = Bu, RfqId = rfq.Id, CustomerId = CustomerId, CreatedBy = "rep@nexora.sa",
            QuoteDate = DateTime.UtcNow, ValidUntil = DateTime.UtcNow.AddDays(60),
            QuoteItems =
            {
                new QuoteItemCreateRequestDTO { RfqItemId = line.Id, ItemDescription = "Battery", Quantity = 2, UnitPrice = 50, TotalAmount = 0 },
                new QuoteItemCreateRequestDTO
                {
                    ItemDescription = "Typed line", Quantity = 1, UnitPrice = 10, TotalAmount = 0,
                    CustomerMaterialCode = " 1637334 ", ManufacturerName = "OEM", ManufacturerPartNumber = "P-1"
                }
            }
        });

        AssertCarried(created.QuoteItems.Single(x => x.RfqItemId == line.Id));
        var typed = created.QuoteItems.Single(x => x.RfqItemId is null);
        Assert.Equal("1637334", typed.CustomerMaterialCode);
        Assert.Equal("OEM", typed.ManufacturerName);
        Assert.Equal("P-1", typed.ManufacturerPartNumber);
    }

    [Fact]
    public async Task Editing_the_quote_keeps_what_it_is_not_sent_and_fills_a_new_rfq_line()
    {
        var (db, ctx, rfq) = await SeedAsync(97_150, markedLines: 1, lineCount: 2);
        using var _ = db;
        await using var __ = ctx;
        var service = new QuoteService(ctx, null!, null!);
        var draft = await service.PrepareDraftFromRfqAsync(rfq.Id, Bu, "rep@nexora.sa");
        var existing = Assert.Single(draft.QuoteItems);
        var secondRfqLine = rfq.Rfqitems.OrderBy(x => x.Id).Last();
        ctx.ChangeTracker.Clear();

        // The edit screen posts no material/maker/part at all (they are read-only there).
        var updated = await service.UpdateQuoteAsync(draft.Id, new QuoteUpdateRequestDTO
        {
            Id = draft.Id, QuoteNo = draft.QuoteNo, CustomerId = draft.CustomerId, StatusId = draft.StatusId,
            ModifiedBy = "rep@nexora.sa", QuoteDate = DateTime.UtcNow, ValidUntil = DateTime.UtcNow.AddDays(60),
            QuoteItems =
            {
                new QuoteItemUpdateRequestDTO { Id = existing.Id, RfqItemId = existing.RfqItemId, ItemDescription = "Battery", Quantity = 4, UnitPrice = 12.345m },
                new QuoteItemUpdateRequestDTO { RfqItemId = secondRfqLine.Id, ItemDescription = "Second", Quantity = 1, UnitPrice = 5m }
            }
        });

        Assert.All(updated.QuoteItems, AssertCarried);
        // D-11: the price is stored at the scale it prints at.
        Assert.Equal(12.35m, updated.QuoteItems.Single(x => x.Id == existing.Id).UnitPrice);
    }

    [Fact]
    public async Task A_revision_keeps_what_the_sent_quote_said_and_fills_a_line_that_never_had_it()
    {
        var (db, ctx, rfq) = await SeedAsync(97_160, markedLines: 2);
        using var _ = db;
        await using var __ = ctx;
        var service = new QuoteService(ctx, null!, null!);
        var draft = await service.PrepareDraftFromRfqAsync(rfq.Id, Bu, "rep@nexora.sa");
        var sent = await ctx.Quotes.Include(x => x.QuoteItems).SingleAsync(x => x.Id == draft.Id);
        sent.StatusId = SentStatusId;
        sent.SentOn = DateTime.UtcNow;
        var ordered = sent.QuoteItems.OrderBy(x => x.Id).ToList();
        ordered[0].ManufacturerName = "AS SENT";
        ordered[1].CustomerMaterialCode = ordered[1].ManufacturerName = ordered[1].ManufacturerPartNumber = null;
        await ctx.SaveChangesAsync();
        ctx.ChangeTracker.Clear();

        var revision = await service.ReviseQuoteAsync(draft.Id, Bu, "rep@nexora.sa");

        var lines = revision.QuoteItems.OrderBy(x => x.RfqItemId).ToList();
        Assert.Equal("AS SENT", lines[0].ManufacturerName);
        AssertCarried(lines[1]);
    }

    [Fact]
    public async Task Approving_an_rfq_into_a_quote_carries_them()
    {
        using var db = new TestDb();
        long rfqId;
        await using (var seed = db.ContextFor(null))
        {
            rfqId = SeedApprovableRfq(seed);
            await seed.SaveChangesAsync();
        }
        await using var ctx = db.ContextFor(Bu);

        var quoteId = await new RfqRepository(ctx).ApproveAsync(rfqId, "rep@nexora.sa", Bu);

        var line = await ctx.QuoteItems.SingleAsync(x => x.QuoteId == quoteId);
        Assert.Equal("905750742", line.CustomerMaterialCode);
        Assert.Equal("SAFT", line.ManufacturerName);
        Assert.Equal("LS14500-AX", line.ManufacturerPartNumber);
    }

    // ================================================================== fixture

    private static void AssertCarried(QuoteItemResponseDTO line)
    {
        Assert.Equal("905750742", line.CustomerMaterialCode);
        Assert.Equal("SAFT", line.ManufacturerName);
        Assert.Equal("LS14500-AX", line.ManufacturerPartNumber);
    }

    private static async Task<(TestDb db, ErpRfqAutomationContext ctx, Rfq rfq)> SeedAsync(
        long seed, int markedLines, int lineCount = -1)
    {
        if (lineCount < 0) lineCount = markedLines;
        var db = new TestDb();
        var ctx = db.ContextFor(Bu);
        var leadLines = Enumerable.Range(0, lineCount).Select(i => new LeadItem
        {
            Id = seed + 10 + i, LineItemNo = $"{(i + 1) * 10}", ItemMaterialCode = "905750742",
            ProductShortDescription = "Battery 3.6 V", Quantity = 4, UnitOfMeasure = "EA", Currency = "SAR"
        }).ToArray();
        var lead = Seed.Lead(ctx, seed, Bu, items: leadLines);
        Seed.Customer(ctx, CustomerId, Bu, "Saudi Electricity Company");
        ctx.SetupMasters.AddRange(Status(DraftStatusId, "DRAFT"), Status(SentStatusId, "SENT"));
        await ctx.SaveChangesAsync();
        lead.ResolveCommercialIdentity(CustomerId, null, "CONFIRMED");
        var rfq = new Rfq
        {
            Id = seed + 3, Rfqno = $"RFQ-{seed}", RecDate = lead.RecDate, LeadId = lead.Id, BusinessUnitId = Bu,
            CustomerId = lead.CustomerId, CreatedBy = "seed", CreatedDate = DateTime.UtcNow,
            Rfqitems = leadLines.Select((line, index) => new Rfqitem
            {
                Id = seed + 20 + index, LineItemNo = line.LineItemNo, ItemMaterialCode = line.ItemMaterialCode,
                ProductShortDescription = line.ProductShortDescription, Quantity = line.Quantity!.Value,
                UnitOfMeasure = line.UnitOfMeasure, ManufacturerName = "SAFT", ManufacturerPartNumber = "LS14500-AX",
                Currency = "SAR", CreatedBy = "seed", CreatedDate = DateTime.UtcNow
            }).ToList()
        };
        rfq.InheritCommercialIdentity(lead);
        ctx.Rfqs.Add(rfq);
        await ctx.SaveChangesAsync();
        foreach (var line in rfq.Rfqitems.OrderBy(x => x.Id).Take(markedLines))
            line.DecideParticipation(Rfqitem.ParticipationQuote, null, "rep@nexora.sa", DateTime.UtcNow);
        await ctx.SaveChangesAsync();
        return (db, ctx, rfq);
    }

    private static SetupMaster Status(long id, string code) => new()
    {
        SetupId = id, BusinessUnitId = Bu, SetupType = "QuoteStatus", SetupCode = code,
        SetupValue = code == "DRAFT" ? "Draft" : "Sent", IsActive = true, CreatedBy = "seed", CreatedOn = DateTime.UtcNow
    };

    /// <summary>An RFQ in QUOTE_PREPARATION with a resolved commercial identity, ready to approve.</summary>
    private static long SeedApprovableRfq(ErpRfqAutomationContext db)
    {
        const long offset = 97_170;
        db.SetupMasters.AddRange(
            new SetupMaster
            {
                SetupId = offset + 1, BusinessUnitId = Bu, SetupType = "RFQStatus", SetupCode = "QUOTE_PREPARATION",
                SetupValue = "QUOTE_PREPARATION", IsActive = true, CreatedBy = "seed", CreatedOn = DateTime.UtcNow
            },
            Status(offset + 2, "DRAFT"));
        var customer = Seed.Customer(db, offset + 3, Bu, "Saudi Electricity Company");
        var contact = Seed.Contact(db, offset + 4, Bu, customer.Id);
        var lead = Seed.Lead(db, offset + 5, Bu);
        db.SaveChanges();
        lead.ResolveCommercialIdentity(customer.Id, contact.Id, "CONFIRMED");
        db.SaveChanges();
        var rfq = new Rfq
        {
            Id = offset + 6, Rfqno = "RFQ-APPROVE", RecDate = DateTime.UtcNow, LeadId = lead.Id, BusinessUnitId = Bu,
            RfqstatusId = offset + 1, CreatedBy = "seed", CreatedDate = DateTime.UtcNow
        };
        rfq.InheritCommercialIdentity(lead);
        db.Rfqs.Add(rfq);
        db.Rfqitems.Add(new Rfqitem
        {
            Id = offset + 10, Rfqid = rfq.Id, ProductShortName = "Battery", Quantity = 3, UnitPrice = 45m,
            ItemMaterialCode = "905750742", ManufacturerName = "SAFT", ManufacturerPartNumber = "LS14500-AX",
            CreatedBy = "seed", CreatedDate = DateTime.UtcNow
        });
        return rfq.Id;
    }
}

/// <summary>
/// D-11 on the award path: the customer price derived from a supplier award was written to the
/// quote line at six decimals (2,338.541625), so the PDF printed 12 x 2,338.54 = 28,062.50. The
/// line now holds the price at the scale it prints at, whatever margin formula produced it.
/// </summary>
public sealed class AwardPricingRoundsToThePrintedScaleTests
{
    [Fact]
    public async Task The_quote_line_holds_the_awarded_price_at_two_decimals_and_its_total_follows()
    {
        using var fixture = new ProcurementScenario();
        await using (var context = fixture.Context())
        {
            var inventory = await context.Set<Models.Inventory>().SingleAsync(x => x.Id == ProcurementTestData.Inventory);
            inventory.QtyOnHand = 0m;
            await context.SaveChangesAsync();
        }
        var solicitation = await fixture.Execute(service => service.CreateSolicitationAsync(fixture.Solicitation("d11-sol")));
        await fixture.MarkSolicitationSentAsync(solicitation.Id);
        var captured = await fixture.Execute(service => service.CaptureSupplierQuoteAsync(
            new Procurement.CaptureSupplierQuoteCommand(fixture.BusinessUnitId, solicitation.Id,
                "SQ-D11", 1, DateTime.UtcNow.AddDays(30), "d11-quote", "qa", "corr-d11",
                [new Procurement.CaptureSupplierQuoteLine(fixture.RfqItemId, ProcurementTestData.Product,
                    10m, 100m, ProcurementTestData.Currency, 5, 10m, 0m, 0m, 0m, 0m, 0m, 1m, 95m)])));
        var award = await fixture.Execute(service => service.ApproveAwardAsync(new Procurement.ApproveAwardCommand(
            fixture.BusinessUnitId, Assert.Single(captured.LineIds), 10m, 1, "d11-award", "qa", "corr-d11-award", 42,
            "Best eligible landed cost")));

        long quoteItemId;
        await using (var context = fixture.Context())
        {
            context.SetupMasters.Add(new SetupMaster
            {
                SetupId = 96_598, SetupType = "QuoteStatus", SetupCode = "DRAFT", SetupValue = "Draft",
                BusinessUnitId = fixture.BusinessUnitId, IsActive = true, CreatedBy = "qa", CreatedOn = DateTime.UtcNow
            });
            var quote = new Quote
            {
                QuoteNo = "QT-D11", Rfqid = fixture.RfqId, BusinessUnitId = fixture.BusinessUnitId,
                QuoteDate = DateTime.UtcNow, ValidUntil = DateTime.UtcNow.AddDays(14), StatusId = 96_598,
                CurrencyId = ProcurementTestData.Currency, TotalAmount = 0m, CreatedBy = "qa", CreatedDate = DateTime.UtcNow
            };
            quote.QuoteItems.Add(new QuoteItem
            {
                RfqitemId = fixture.RfqItemId, ProductId = ProcurementTestData.Product, ItemDescription = "QA Product",
                Quantity = 10m, UnitPrice = 0m, TotalAmount = 0m, CreatedBy = "qa", CreatedDate = DateTime.UtcNow
            });
            context.Quotes.Add(quote);
            await context.SaveChangesAsync();
            quoteItemId = quote.QuoteItems.Single().Id;
        }

        await using var db = fixture.Context();
        // 13%: a margin whose price has more than two decimals under the margin-on-sale formula
        // (100 / 0.87 = 114.942529) — and a clean one under margin on cost (113.00). Either way the
        // line must hold what prints.
        var priced = await new ERP_RFQ_Automation.SupplierQuotes.SupplierQuoteCommercialService(db).ApplyPricingAsync(
            new ERP_RFQ_Automation.SupplierQuotes.ApplyCustomerQuotePricingCommand(fixture.BusinessUnitId, quoteItemId,
                award.Id, 13m, "Evidence-backed target margin", "d11-pricing", "qa", "corr-d11-pricing"));

        var line = await db.QuoteItems.SingleAsync(x => x.Id == quoteItemId);
        Assert.Equal(Math.Round(priced.CustomerUnitPrice, 2, MidpointRounding.AwayFromZero), line.UnitPrice);
        Assert.Equal(decimal.Round(line.UnitPrice, 2), line.UnitPrice);
        Assert.Equal(Math.Round(10m * line.UnitPrice, 2), line.TaxableBase);
    }
}
