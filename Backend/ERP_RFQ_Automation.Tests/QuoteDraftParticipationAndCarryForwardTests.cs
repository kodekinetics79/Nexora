using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Services;
using ERP_RFQ_Automation.Tests.Support;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// Two repairs on the RFQ → Customer Quote Draft handoff.
///
/// <para><b>Participation gate.</b> A Quote Draft is a partial-bid instrument. Which lines it
/// covers must be an explicit human decision, never "all of them by default".</para>
///
/// <para><b>Carry-forward.</b> The engineer pricing the draft was shown quantity, UoM and a
/// description — and no manufacturer and no part number. On a bid list where "BALL VALVE 2IN"
/// is a dozen different parts at a dozen different prices, that is not enough to quote.</para>
/// </summary>
public sealed class QuoteDraftParticipationAndCarryForwardTests
{
    private const long Bu = 9601;

    private static SetupMaster DraftStatus(long id) => new()
    {
        SetupId = id,
        BusinessUnitId = Bu,
        SetupType = "QuoteStatus",
        SetupCode = "DRAFT",
        SetupValue = "Draft",
        IsActive = true,
        CreatedBy = "seed",
        CreatedOn = DateTime.UtcNow
    };

    private static LeadItem SourceLine(long id, string lineNo) => new()
    {
        Id = id,
        LineItemNo = lineNo,
        ItemMaterialCode = "SEC-MAT-889120",
        ProductShortDescription = "Ball valve 2IN class 300",
        Quantity = 4,
        UnitOfMeasure = "EA",
        Currency = "SAR"
    };

    /// <summary>Builds an RFQ whose lines carry the full industrial identity of the request.</summary>
    private static Rfq RfqWithCommercialDetail(Lead lead, long id)
    {
        var rfq = new Rfq
        {
            Id = id,
            Rfqno = $"RFQ-{id}",
            RecDate = lead.RecDate,
            BidClosingDate = lead.BidClosingDate,
            LeadId = lead.Id,
            BusinessUnitId = lead.BusinessUnitId,
            CustomerId = lead.CustomerId,
            CreatedBy = "seed",
            CreatedDate = DateTime.UtcNow,
            Rfqitems = lead.LeadItems.Select((line, index) => new Rfqitem
            {
                Id = id + 1 + index,
                LineItemNo = line.LineItemNo,
                ItemMaterialCode = line.ItemMaterialCode,
                ProductShortDescription = line.ProductShortDescription,
                // LeadItem.Quantity is nullable so an unread quantity is distinguishable from a
                // real zero; Rfqitem.Quantity is NOT NULL and positive-checked. Production
                // (LeadRepository.CreateRfqFromLeadAsync) refuses an unquantified line rather than
                // coalescing it, so the fixture refuses too instead of silently seeding a 0.
                Quantity = line.Quantity ?? throw new InvalidOperationException(
                    $"Fixture lead line '{line.LineItemNo}' states no quantity; an RFQ line cannot be built from it."),
                UnitOfMeasure = line.UnitOfMeasure,
                ManufacturerName = "GOULDS PUMPS",
                ManufacturerPartNumber = "P/N#HTGD337039P0002",
                AlternatePartNumber = "ALT-4471",
                Currency = "SAR",
                LeadTime = 45,
                RequiredDesiredDate = new DateTime(2026, 11, 30),
                CreatedBy = "seed",
                CreatedDate = DateTime.UtcNow
            }).ToList()
        };
        rfq.InheritCommercialIdentity(lead);
        return rfq;
    }

    private static async Task<(TestDb db, ERP_RFQ_Automation.Models.ErpRfqAutomationContext ctx, Rfq rfq)>
        SeedAsync(long seed, int lineCount = 1)
    {
        var db = new TestDb();
        var ctx = db.ContextFor(Bu);
        var lines = Enumerable.Range(0, lineCount)
            .Select(i => SourceLine(seed + 10 + i, $"000{i + 1}0")).ToArray();
        var lead = Seed.Lead(ctx, seed, Bu, items: lines);
        Seed.Customer(ctx, seed + 1, Bu, "Saudi Electricity Company");
        ctx.SetupMasters.Add(DraftStatus(seed + 2));
        await ctx.SaveChangesAsync();
        lead.ResolveCommercialIdentity(seed + 1, null, "CONFIRMED");
        var rfq = RfqWithCommercialDetail(lead, seed + 3);
        ctx.Rfqs.Add(rfq);
        await ctx.SaveChangesAsync();
        return (db, ctx, rfq);
    }

    // ------------------------------------------------------------------ participation gate

    [Fact]
    public async Task PrepareDraftFromRfq_RefusesWhenNoLineIsMarkedForQuote()
    {
        var (db, ctx, rfq) = await SeedAsync(96100);
        using var _ = db;
        await using var __ = ctx;

        var service = new QuoteService(ctx, null!, null!);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.PrepareDraftFromRfqAsync(rfq.Id, Bu, "sara@nexora.sa"));
        Assert.Contains("Mark at least one RFQ line as Quote", ex.Message);
        // and nothing was half-created
        Assert.Empty(await ctx.Quotes.ToListAsync());
    }

    [Fact]
    public async Task PrepareDraftFromRfq_RefusesWhenEveryLineIsNoQuote()
    {
        var (db, ctx, rfq) = await SeedAsync(96200, lineCount: 2);
        using var _ = db;
        await using var __ = ctx;
        foreach (var line in rfq.Rfqitems)
            line.DecideParticipation(Rfqitem.ParticipationNoQuote, "Obsolete, no supplier source", "sara@nexora.sa", DateTime.UtcNow);
        await ctx.SaveChangesAsync();

        var service = new QuoteService(ctx, null!, null!);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.PrepareDraftFromRfqAsync(rfq.Id, Bu, "sara@nexora.sa"));
        Assert.Empty(await ctx.Quotes.ToListAsync());
    }

    [Fact]
    public async Task PrepareDraftFromRfq_QuotesOnlyTheMarkedLines_NotTheWholeBidList()
    {
        // THE partial-bid case: 3 lines on the customer's list, 1 line on our quote.
        var (db, ctx, rfq) = await SeedAsync(96300, lineCount: 3);
        using var _ = db;
        await using var __ = ctx;
        var ordered = rfq.Rfqitems.OrderBy(item => item.Id).ToArray();
        ordered[0].DecideParticipation(Rfqitem.ParticipationQuote, null, "sara@nexora.sa", DateTime.UtcNow);
        ordered[1].DecideParticipation(Rfqitem.ParticipationNoQuote, "Lead time exceeds bid validity", "sara@nexora.sa", DateTime.UtcNow);
        // ordered[2] deliberately left Pending — undecided must NOT be quoted either
        await ctx.SaveChangesAsync();

        var service = new QuoteService(ctx, null!, null!);
        var draft = await service.PrepareDraftFromRfqAsync(rfq.Id, Bu, "sara@nexora.sa");

        ctx.ChangeTracker.Clear();
        var quote = await ctx.Quotes.Include(q => q.QuoteItems).SingleAsync();
        var quotedLine = Assert.Single(quote.QuoteItems);
        Assert.Equal(ordered[0].Id, quotedLine.RfqitemId);
        Assert.Single(draft.QuoteItems);
        // the RFQ still holds all three — declining a line does not delete the customer's request
        Assert.Equal(3, await ctx.Rfqitems.CountAsync(i => i.Rfqid == rfq.Id));
    }

    [Fact]
    public async Task PrepareDraftFromRfq_IgnoresIncompleteLinesThatAreNotBeingQuoted()
    {
        // A line missing a part number is frequently WHY it is declined. Its incompleteness
        // must not block the draft for the lines we ARE quoting.
        var (db, ctx, rfq) = await SeedAsync(96400, lineCount: 2);
        using var _ = db;
        await using var __ = ctx;
        var ordered = rfq.Rfqitems.OrderBy(item => item.Id).ToArray();
        ordered[0].DecideParticipation(Rfqitem.ParticipationQuote, null, "sara@nexora.sa", DateTime.UtcNow);
        ordered[1].UnitOfMeasure = null; // incomplete...
        ordered[1].DecideParticipation(Rfqitem.ParticipationNoQuote, "Specification incomplete, clarification sent", "sara@nexora.sa", DateTime.UtcNow);
        await ctx.SaveChangesAsync();

        var service = new QuoteService(ctx, null!, null!);
        var draft = await service.PrepareDraftFromRfqAsync(rfq.Id, Bu, "sara@nexora.sa");

        Assert.Single(draft.QuoteItems);
    }

    // ------------------------------------------------------------------ carry-forward

    [Fact]
    public async Task QuoteDraft_ShowsTheManufacturerPartNumberAndRequestedDelivery_ReadThroughTheRfqLine()
    {
        var (db, ctx, rfq) = await SeedAsync(96500);
        using var _ = db;
        await using var __ = ctx;
        foreach (var line in rfq.Rfqitems)
            line.DecideParticipation(Rfqitem.ParticipationQuote, null, "sara@nexora.sa", DateTime.UtcNow);
        await ctx.SaveChangesAsync();

        var service = new QuoteService(ctx, null!, null!);
        var draft = await service.PrepareDraftFromRfqAsync(rfq.Id, Bu, "sara@nexora.sa");

        var quoted = Assert.Single(draft.QuoteItems);
        Assert.Equal("GOULDS PUMPS", quoted.RequestedManufacturerName);
        Assert.Equal("P/N#HTGD337039P0002", quoted.RequestedManufacturerPartNumber);
        Assert.Equal("SEC-MAT-889120", quoted.RequestedItemMaterialCode);
        Assert.Equal("ALT-4471", quoted.RequestedAlternatePartNumber);
        Assert.Equal(new DateTime(2026, 11, 30), quoted.RequestedDeliveryDate);
        Assert.Equal(45, quoted.RequestedLeadTimeDays);
        Assert.Equal("SAR", quoted.RequestedCurrency);
    }

    [Fact]
    public async Task WhatTheCustomerAskedFor_IsNeverPresentedAsWhatNexoraCommitted()
    {
        // The requested date and the promised lead time are different commercial facts.
        // DeliveryLeadTime is OUR commitment and stays unknown until costing; conflating the
        // two would let a buyer's request masquerade as our promise.
        var (db, ctx, rfq) = await SeedAsync(96600);
        using var _ = db;
        await using var __ = ctx;
        foreach (var line in rfq.Rfqitems)
            line.DecideParticipation(Rfqitem.ParticipationQuote, null, "sara@nexora.sa", DateTime.UtcNow);
        await ctx.SaveChangesAsync();

        var service = new QuoteService(ctx, null!, null!);
        var draft = await service.PrepareDraftFromRfqAsync(rfq.Id, Bu, "sara@nexora.sa");

        var quoted = Assert.Single(draft.QuoteItems);
        Assert.Null(quoted.DeliveryLeadTime);          // ours — not yet established
        Assert.Equal(45, quoted.RequestedLeadTimeDays); // theirs — stated on the request
        Assert.Equal(0m, quoted.UnitPrice);             // no price invented
    }

    [Fact]
    public async Task TheRequestedValuesAreNotCopiedOntoTheQuoteLine()
    {
        // They are projected through QuoteItem.RfqitemId. Copying them would create a second
        // version of the buyer's request that can silently drift from the governed one.
        var (db, ctx, rfq) = await SeedAsync(96700);
        using var _ = db;
        await using var __ = ctx;
        foreach (var line in rfq.Rfqitems)
            line.DecideParticipation(Rfqitem.ParticipationQuote, null, "sara@nexora.sa", DateTime.UtcNow);
        await ctx.SaveChangesAsync();

        var service = new QuoteService(ctx, null!, null!);
        await service.PrepareDraftFromRfqAsync(rfq.Id, Bu, "sara@nexora.sa");

        ctx.ChangeTracker.Clear();
        var quoteLine = await ctx.Set<QuoteItem>().SingleAsync();
        Assert.NotNull(quoteLine.RfqitemId); // the link IS the lineage
        var columns = typeof(QuoteItem).GetProperties().Select(p => p.Name).ToArray();
        Assert.DoesNotContain("ManufacturerName", columns);
        Assert.DoesNotContain("ManufacturerPartNumber", columns);
    }
}
