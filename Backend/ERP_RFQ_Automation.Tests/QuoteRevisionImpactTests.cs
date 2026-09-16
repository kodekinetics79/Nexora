using ERP_RFQ_Automation.DTOs.QuoteDTOs;
using ERP_RFQ_Automation.Interfaces;
using ERP_RFQ_Automation.LeadIdentity;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Repositories;
using ERP_RFQ_Automation.Services;
using ERP_RFQ_Automation.Tests.Support;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// A customer revision must say WHAT changed, and the rep must be able to take the change.
///
/// <para><b>The defect.</b> Driving QT-0926-0001 on 2026-09-15: the quote view said "This Quote
/// Draft is stale and must be reviewed against Lead Revision 3" — 3 being the revision the quote
/// was BUILT from, not the one that arrived — never said what the customer changed, and its one
/// button, "Mark review complete", let the draft proceed with the OLD quantities. The diff had
/// been sitting in <c>LeadRevisionDifferences</c> the whole time.</para>
///
/// <para>These pin: the impact projection carries the revision span and a compact per-line
/// change list; applying updates the matching draft lines' quantities, re-totals, and resolves
/// the impact; a quote already with the customer is refused and told to revise.</para>
/// </summary>
public class QuoteRevisionImpactTests
{
    private const long Tenant = 98_301;
    private const long LeadId = 98_311;
    private const long QuoteId = 98_351;
    private const long DraftStatusId = 98_391;
    private const long SentStatusId = 98_392;

    [Fact]
    public async Task The_quote_detail_names_the_revision_span_and_what_changed_on_each_line()
    {
        using var db = new TestDb();
        await using var context = db.ContextFor(Tenant);
        SeedQuoteBuiltOnRevision3WithRevision4Arrived(context);

        var impact = await LeadRevisionImpactQueries.DescribeOpenQuoteImpactAsync(context, Tenant, QuoteId);

        Assert.NotNull(impact);
        Assert.Equal("DRAFT_STALE_REVIEW_REQUIRED", impact!.ImpactType);
        Assert.Equal(3, impact.FromRevision);
        Assert.Equal(4, impact.ToRevision);

        // One entry per changed thing, in the buyer's own line numbers. Unchanged lines and header
        // fields are not "changes" and must not pad the list.
        Assert.Collection(impact.Changes,
            change =>
            {
                Assert.Equal("10", change.Line);
                Assert.Equal("quantity", change.Field);
                Assert.Equal("20", change.From);
                Assert.Equal("35", change.To);
            },
            change =>
            {
                Assert.Equal("20", change.Line);
                Assert.Equal("quantity", change.Field);
                Assert.Equal("1500", change.From);
                Assert.Equal("2000", change.To);
            },
            change =>
            {
                Assert.Equal("30", change.Line);
                Assert.Equal("added", change.Field);
            });

        // The same facts reach the screen through the quote detail, beside the legacy type string.
        var dto = await new QuoteRepository(context).GetByIdAsync(QuoteId, Tenant);
        Assert.Equal("DRAFT_STALE_REVIEW_REQUIRED", dto.RevisionImpact);
        Assert.NotNull(dto.RevisionImpactDetail);
        Assert.Equal(4, dto.RevisionImpactDetail!.ToRevision);
        Assert.Equal(3, dto.RevisionImpactDetail.Changes.Count);
    }

    [Fact]
    public async Task Applying_the_new_quantities_updates_matching_lines_retotals_and_resolves_the_impact()
    {
        using var db = new TestDb();
        await using var context = db.ContextFor(Tenant);
        SeedQuoteBuiltOnRevision3WithRevision4Arrived(context);
        var service = new QuoteService(context, null!, null!);

        var result = await service.ApplyRevisionQuantitiesAsync(QuoteId, Tenant, "rep@nexora.invalid", "apply-1");

        Assert.Equal(2, result.LinesUpdated);
        Assert.Collection(result.Applied,
            change => { Assert.Equal("10", change.Line); Assert.Equal("20", change.From); Assert.Equal("35", change.To); },
            change => { Assert.Equal("20", change.Line); Assert.Equal("1500", change.From); Assert.Equal("2000", change.To); });
        // The revision added a line the draft does not have. Nothing is invented; the rep is told.
        Assert.Equal(new[] { "30" }, result.LinesNotOnQuote);

        context.ChangeTracker.Clear();
        var quote = await context.Quotes.AsNoTracking().Include(q => q.QuoteItems)
            .SingleAsync(q => q.Id == QuoteId);
        var byId = quote.QuoteItems.ToDictionary(i => i.Id);
        Assert.Equal(35m, byId[98_371].Quantity);
        Assert.Equal(2000m, byId[98_372].Quantity);
        // A line the draft carries that the customer's document never had is left alone.
        Assert.Equal(1m, byId[98_373].Quantity);

        // Re-totalled from the new quantities: every line total reflects its quantity, and the
        // quote total is the sum of its lines, not the figure stored before the change.
        Assert.True(byId[98_371].TotalAmount >= 35m * 10m, $"line 10 total {byId[98_371].TotalAmount}");
        Assert.True(byId[98_372].TotalAmount >= 2000m * 2m, $"line 20 total {byId[98_372].TotalAmount}");
        Assert.Equal(quote.QuoteItems.Sum(i => i.TotalAmount), quote.TotalAmount);
        Assert.NotEqual(4_205m, quote.TotalAmount);

        // Applying IS the review. The impact is resolved through the same append-only mechanism
        // "Keep as quoted" uses, so every reader agrees the quote is no longer stale.
        Assert.False(await LeadRevisionImpactQueries.OpenQuoteImpacts(context, Tenant, QuoteId).AnyAsync());
        Assert.Null(await LeadRevisionImpactQueries.DescribeOpenQuoteImpactAsync(context, Tenant, QuoteId));
    }

    [Fact]
    public async Task A_quote_already_with_the_customer_cannot_take_new_quantities_and_is_told_to_revise()
    {
        using var db = new TestDb();
        await using var context = db.ContextFor(Tenant);
        SeedQuoteBuiltOnRevision3WithRevision4Arrived(context, sent: true);
        var service = new QuoteService(context, null!, null!);

        var refusal = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.ApplyRevisionQuantitiesAsync(QuoteId, Tenant, "rep@nexora.invalid", "apply-2"));

        Assert.Contains("revis", refusal.Message, StringComparison.OrdinalIgnoreCase);
        var quote = await context.Quotes.AsNoTracking().Include(q => q.QuoteItems).SingleAsync(q => q.Id == QuoteId);
        Assert.Equal(20m, quote.QuoteItems.Single(i => i.Id == 98_371).Quantity);
        Assert.True(await LeadRevisionImpactQueries.OpenQuoteImpacts(context, Tenant, QuoteId).AnyAsync());
    }

    // ------------------------------------------------------------------------ test plumbing

    /// <summary>
    /// The shape driven on 2026-09-15: a draft prepared from lead revision 3 (two lines, 20 and
    /// 1,500 pieces) plus one line the rep added by hand; then revision 4 arrives with line 10 at
    /// 35, line 20 at 2,000 and a new line 30. The impact row is exactly what
    /// <c>LeadIdentityApplicationService.AddImpactsAsync</c> writes.
    /// </summary>
    private static void SeedQuoteBuiltOnRevision3WithRevision4Arrived(ErpRfqAutomationContext context, bool sent = false)
    {
        Seed.EnsureBusinessUnit(context, Tenant);
        context.SetupMasters.Add(new SetupMaster
        {
            SetupId = DraftStatusId, BusinessUnitId = Tenant, SetupType = "QuoteStatus",
            SetupCode = "DRAFT", SetupValue = "Draft", CreatedBy = "seed", CreatedOn = DateTime.UtcNow
        });
        context.SetupMasters.Add(new SetupMaster
        {
            SetupId = SentStatusId, BusinessUnitId = Tenant, SetupType = "QuoteStatus",
            SetupCode = "SENT", SetupValue = "Sent", CreatedBy = "seed", CreatedOn = DateTime.UtcNow
        });
        context.Currencies.Add(new Currency
        {
            Id = 98_381, BusinessUnitId = Tenant, Code = "SAR", CurrencyName = "Saudi Riyal",
            CreatedBy = "seed", CreatedOn = DateTime.UtcNow
        });

        var lead = Seed.Lead(context, LeadId, Tenant, items: new[]
        {
            Seed.LeadItem(98_321, "10", 35, "Gasket spiral wound"),
            Seed.LeadItem(98_322, "20", 2000, "Hex bolt M12"),
            Seed.LeadItem(98_323, "30", 4, "Flange kit"),
        });
        lead.CurrentRevisionNumber = 4;

        // Revision 3: what the draft was built from.
        var revision3 = SeedRevision(context, 98_411, revisionNumber: 3, occurrenceId: 98_401,
            (98_361, 98_321, "10", 20m), (98_362, 98_322, "20", 1500m));
        // Revision 4: what the customer just sent.
        var revision4 = SeedRevision(context, 98_412, revisionNumber: 4, occurrenceId: 98_402,
            (98_363, 98_321, "10", 35m), (98_364, 98_322, "20", 2000m), (98_365, 98_323, "30", 4m));
        revision4.Differences.Add(Difference(LeadRevisionChangeType.Unchanged, "Field", "$.rfq", "\"RFQ-1\"", "\"RFQ-1\""));
        revision4.Differences.Add(Difference(LeadRevisionChangeType.Modified, "Line", "$.items[\"10\"]",
            LineJson("10", 20m, "EA"), LineJson("10", 35m, "EA")));
        revision4.Differences.Add(Difference(LeadRevisionChangeType.Modified, "Line", "$.items[\"20\"]",
            LineJson("20", 1500m, "EA"), LineJson("20", 2000m, "EA")));
        revision4.Differences.Add(Difference(LeadRevisionChangeType.Added, "Line", "$.items[\"30\"]",
            null, LineJson("30", 4m, "SET")));
        _ = revision3;

        context.Rfqs.Add(new Rfq
        {
            Id = 98_331, Rfqno = "RFQ-98311", LeadId = LeadId, BusinessUnitId = Tenant,
            CreatedBy = "seed", CreatedDate = DateTime.UtcNow
        });
        context.Rfqitems.Add(new Rfqitem
        {
            Id = 98_341, Rfqid = 98_331, LineItemNo = "10", Quantity = 20m, UnitOfMeasure = "EA",
            SourceLeadRevisionId = 98_411, SourceLeadItemRevisionId = 98_361,
            CreatedBy = "seed", CreatedDate = DateTime.UtcNow
        });
        context.Rfqitems.Add(new Rfqitem
        {
            Id = 98_342, Rfqid = 98_331, LineItemNo = "20", Quantity = 1500m, UnitOfMeasure = "EA",
            SourceLeadRevisionId = 98_411, SourceLeadItemRevisionId = 98_362,
            CreatedBy = "seed", CreatedDate = DateTime.UtcNow
        });

        var quote = new Quote
        {
            Id = QuoteId,
            QuoteNo = "QT-0926-0001",
            Rfqid = 98_331,
            BusinessUnitId = Tenant,
            StatusId = sent ? SentStatusId : DraftStatusId,
            CurrencyId = 98_381,
            QuoteDate = DateTime.UtcNow,
            ValidUntil = DateTime.UtcNow.AddDays(30),
            TotalAmount = 4_205m,
            SentOn = sent ? DateTime.UtcNow.AddDays(-1) : null,
            CreatedBy = "seed",
            CreatedDate = DateTime.UtcNow
        };
        quote.QuoteItems.Add(Line(98_371, 98_341, "10", 20m, 10m));
        quote.QuoteItems.Add(Line(98_372, 98_342, "20", 1500m, 2m));
        quote.QuoteItems.Add(Line(98_373, null, null, 1m, 5m));
        context.Quotes.Add(quote);

        context.Set<LeadRevisionImpact>().Add(new LeadRevisionImpact
        {
            BusinessUnitId = Tenant,
            LeadId = LeadId,
            LeadRevisionId = 98_412,
            AggregateType = "QUOTE",
            AggregateId = QuoteId,
            ImpactType = sent ? "QUOTE_REVISION_REQUIRED" : "DRAFT_STALE_REVIEW_REQUIRED",
            Status = "OPEN",
            DetailsJson = "{\"fromRevision\":3,\"toRevision\":4,\"automaticMutation\":false}",
            CreatedAtUtc = DateTimeOffset.UtcNow
        });
        context.SaveChanges();
        context.ChangeTracker.Clear();
    }

    private static QuoteItem Line(long id, long? rfqItemId, string? customerLineRef, decimal quantity, decimal unitPrice) => new()
    {
        Id = id,
        RfqitemId = rfqItemId,
        CustomerLineRef = customerLineRef,
        ItemDescription = $"Line {customerLineRef ?? "extra"}",
        Quantity = quantity,
        UnitOfMeasure = "EA",
        UnitPrice = unitPrice,
        TaxAmount = Math.Round(quantity * unitPrice * 0.15m, 2),
        TaxCategory = ERP_RFQ_Automation.OrderToCash.QuoteLineTaxCategories.Standard,
        TaxRatePercentApplied = 15m,
        TotalAmount = Math.Round(quantity * unitPrice * 1.15m, 2),
        CreatedBy = "seed",
        CreatedDate = DateTime.UtcNow
    };

    private static LeadRevision SeedRevision(ErpRfqAutomationContext context, long revisionId, int revisionNumber,
        long occurrenceId, params (long ItemRevisionId, long LeadItemId, string Line, decimal Quantity)[] lines)
    {
        var batch = new LeadIngestionBatch
        {
            Id = Guid.NewGuid(), BusinessUnitId = Tenant, SourceChannel = "Email", CreatedBy = "seed",
            CreatedAtUtc = DateTimeOffset.UtcNow, UpdatedAtUtc = DateTimeOffset.UtcNow
        };
        context.Set<LeadIngestionBatch>().Add(batch);
        context.Set<LeadIngestionOccurrence>().Add(new LeadIngestionOccurrence
        {
            Id = occurrenceId, BusinessUnitId = Tenant, BatchId = batch.Id, LeadId = LeadId,
            SourceChannel = "Email", IdempotencyKey = $"occ-{occurrenceId}",
            LogicalInquiryFingerprint = new string('a', 64), Classification = LeadOccurrenceClassification.Revision,
            ActorId = "seed", CorrelationId = $"occ-{occurrenceId}",
            IngestedAtUtc = DateTimeOffset.UtcNow, CreatedAtUtc = DateTimeOffset.UtcNow
        });
        var revision = new LeadRevision
        {
            Id = revisionId, BusinessUnitId = Tenant, LeadId = LeadId, RevisionNumber = revisionNumber,
            EstablishedByOccurrenceId = occurrenceId, LogicalInquiryFingerprint = new string('a', 64),
            SnapshotJson = "{\"schemaVersion\":2,\"items\":[]}", CreatedAtUtc = DateTimeOffset.UtcNow, CreatedBy = "seed"
        };
        var lineNumber = 0;
        foreach (var (itemRevisionId, leadItemId, line, quantity) in lines)
            revision.Items.Add(new LeadItemRevision
            {
                Id = itemRevisionId, BusinessUnitId = Tenant, LeadId = LeadId, LeadItemId = leadItemId,
                LineNumber = ++lineNumber, LineFingerprint = new string('b', 64),
                SnapshotJson = LineJson(line, quantity, "EA")
            });
        context.Set<LeadRevision>().Add(revision);
        return revision;
    }

    private static LeadRevisionDifference Difference(LeadRevisionChangeType change, string scope, string path,
        string? previous, string? current) => new()
    {
        BusinessUnitId = Tenant, ChangeType = change, Scope = scope, Path = path,
        PreviousValueJson = previous, CurrentValueJson = current
    };

    /// <summary>The shape <c>LeadRevisionLineCommercialSnapshot</c> serialises: normalized identity
    /// fields at their historical paths plus the exact commercial values.</summary>
    private static string LineJson(string line, decimal quantity, string uom) =>
        $"{{\"line\":\"{line}\",\"part\":null,\"description\":\"gasket\",\"Quantity\":{quantity},\"uom\":\"{uom.ToLowerInvariant()}\"," +
        $"\"schemaVersion\":2,\"lineItemNo\":\"{line}\",\"unitOfMeasure\":\"{uom}\",\"quantity\":{quantity}}}";
}
