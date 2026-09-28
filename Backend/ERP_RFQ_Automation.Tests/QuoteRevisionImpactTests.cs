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

    // ------------------------------------------------------------ D-01 / D-04 / D-05 (pilot audit)

    private sealed class StubConfig : IQuoteConfigurationRepository
    {
        public Task<QuoteConfiguration?> GetByBusinessUnitIdAsync(long businessUnitId)
            => Task.FromResult<QuoteConfiguration?>(new QuoteConfiguration
            {
                BusinessUnitId = Tenant, CompanyAddress = "King Fahd Road, Al Khobar",
                CompanyPhone = "+966 13 800 0000", CompanyEmail = "sales@example.invalid"
            });
        public Task<QuoteConfiguration> UpsertAsync(QuoteConfiguration configurationToSave) => Task.FromResult(configurationToSave);
        public Task AddAsync(QuoteConfiguration configurationToSave) => Task.CompletedTask;
        public Task UpdateAsync(QuoteConfiguration configurationToSave) => Task.CompletedTask;
    }

    /// <summary>
    /// D-01 / CB-04: QT-0926-0001 was built AFTER the buyer's revision 4 arrived, from an RFQ still
    /// frozen on revision 3, so no impact row existed and readiness said nothing — 20 contactors
    /// went out against the buyer's 35. The warning now compares with the buyer's latest document.
    /// </summary>
    [Fact]
    public async Task A_quote_built_after_the_buyer_revision_warns_before_send_even_without_an_impact()
    {
        using var db = new TestDb();
        await using var context = db.ContextFor(Tenant);
        SeedQuoteBuiltOnRevision3WithRevision4Arrived(context, withImpact: false);
        var service = new QuoteService(context, null!, new StubConfig());

        var readiness = await service.EvaluateSendReadinessAsync(QuoteId, Tenant);

        var warning = Assert.Single(readiness.Warnings, w => w.Code == "BUYER_REVISION_NEWER");
        Assert.StartsWith("The buyer sent a newer version (rev 4): 3 lines changed.", warning.Message);
        Assert.Contains("line 10 20 → 35", warning.Message);
        Assert.True(warning.CanApply);
        Assert.Equal(3, warning.Revision!.FromRevision);
        Assert.Contains(warning.Revision.Changes, c => c.Line == "30" && c.Field == "added");
        // A warning, never a blocker: the rep decides.
        Assert.DoesNotContain(readiness.Blockers, b => b.Code.Contains("REVISION"));
    }

    [Fact]
    public async Task A_quote_built_on_the_buyers_latest_revision_has_no_revision_warning()
    {
        using var db = new TestDb();
        await using var context = db.ContextFor(Tenant);
        SeedQuoteBuiltOnRevision3WithRevision4Arrived(context, withImpact: false, rfqFrozenOnRevision4: true);
        var service = new QuoteService(context, null!, new StubConfig());

        var readiness = await service.EvaluateSendReadinessAsync(QuoteId, Tenant);

        Assert.True(!readiness.Warnings.Any(w => w.Code == "BUYER_REVISION_NEWER"),
            string.Join(" | ", readiness.Warnings.Select(w => w.Message + " from " + w.Revision?.FromRevision)));
    }

    [Fact]
    public async Task A_revision_the_rep_made_by_reviewing_or_linking_the_client_is_not_a_buyer_revision()
    {
        using var db = new TestDb();
        await using var context = db.ContextFor(Tenant);
        SeedQuoteBuiltOnRevision3WithRevision4Arrived(context, withImpact: false, revision4FromBuyer: false);
        var service = new QuoteService(context, null!, new StubConfig());

        var readiness = await service.EvaluateSendReadinessAsync(QuoteId, Tenant);

        Assert.DoesNotContain(readiness.Warnings, w => w.Code == "BUYER_REVISION_NEWER");
    }

    /// <summary>
    /// D-05: "Apply the new quantities" used to update only the customer quote, so the customer was
    /// quoted 10 while the supplier was asked — and asked again — for 8. The RFQ lines now take the
    /// same quantities, open supplier requests are flagged, and the send warning clears.
    /// </summary>
    [Fact]
    public async Task Applying_without_an_impact_updates_the_quote_the_RFQ_lines_and_flags_supplier_requests()
    {
        using var db = new TestDb();
        await using var context = db.ContextFor(Tenant);
        SeedQuoteBuiltOnRevision3WithRevision4Arrived(context, withImpact: false);
        context.Database.ExecuteSqlRaw("PRAGMA foreign_keys = OFF");
        context.Database.ExecuteSqlRaw("PRAGMA ignore_check_constraints = ON");
        context.SourcingCases.Add(new ERP_RFQ_Automation.Procurement.SourcingCase
        {
            Id = 98_501, BusinessUnitId = Tenant, CommercialDemandLineId = 98_502, RfqId = 98_331, RfqItemId = 98_341,
            NexoraSerial = "NXR-QA", Description = "Gasket", RequestedQuantity = 20m, StockQuantity = 0m,
            UnfulfilledQuantity = 20m, Status = ERP_RFQ_Automation.Procurement.SourcingCaseStatuses.OutreachSent,
            NextAction = "Wait for replies", ShortageDecisionKey = new string('c', 64), IdempotencyKey = "qa-case",
            RequestHash = new string('d', 64), CreatedOn = DateTime.UtcNow, CreatedBy = "seed",
            UpdatedOn = DateTime.UtcNow, UpdatedBy = "seed"
        });
        context.Suppliers.Add(new Supplier { Id = 98_511, Buid = Tenant, Name = "Gulf Gaskets", ImageUrl = "n/a", IsActive = true, CreatedBy = "seed", CreatedOn = DateTime.UtcNow });
        context.Set<ERP_RFQ_Automation.Agent.Models.SupplierSolicitation>().Add(new ERP_RFQ_Automation.Agent.Models.SupplierSolicitation
        {
            Id = 98_521, BusinessUnitId = Tenant, RfqId = 98_331, SupplierId = 98_511, SourcingCaseId = 98_501,
            SupplierRfqNumber = "SRFQ-0001", IdempotencyKey = "qa-sol", RequestHash = new string('e', 64),
            RequestedRfqItemIdsJson = "[98341]", Status = ERP_RFQ_Automation.Agent.Models.SolicitationStatus.Sent,
            SentOn = DateTime.UtcNow.AddDays(-1), CreatedOn = DateTime.UtcNow, UpdatedOn = DateTime.UtcNow
        });
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();
        var service = new QuoteService(context, null!, new StubConfig());

        var result = await service.ApplyRevisionQuantitiesAsync(QuoteId, Tenant, "rep@nexora.invalid", "apply-d01");

        Assert.Equal(2, result.LinesUpdated);
        Assert.Equal(new[] { "30" }, result.LinesNotOnQuote);
        Assert.Collection(result.RfqLinesUpdated,
            change => { Assert.Equal("10", change.Line); Assert.Equal("20", change.From); Assert.Equal("35", change.To); },
            change => { Assert.Equal("20", change.Line); Assert.Equal("1500", change.From); Assert.Equal("2000", change.To); });
        var outdated = Assert.Single(result.OutdatedSupplierRequests);
        Assert.Equal("SRFQ-0001", outdated.SupplierRfqNumber);
        Assert.Equal("Gulf Gaskets", outdated.SupplierName);
        Assert.Equal(20m, outdated.AskedQuantity);
        Assert.Equal(35m, outdated.NewQuantity);

        context.ChangeTracker.Clear();
        Assert.Equal(35m, (await context.Rfqitems.AsNoTracking().SingleAsync(x => x.Id == 98_341)).Quantity);
        Assert.Equal(2000m, (await context.Rfqitems.AsNoTracking().SingleAsync(x => x.Id == 98_342)).Quantity);
        var sourcingCase = await context.SourcingCases.AsNoTracking().SingleAsync(x => x.Id == 98_501);
        Assert.Contains("Ask again", sourcingCase.NextAction);
        Assert.Contains("35", sourcingCase.NextAction);

        var after = await service.EvaluateSendReadinessAsync(QuoteId, Tenant);
        Assert.DoesNotContain(after.Warnings, w => w.Code == "BUYER_REVISION_NEWER");
    }

    /// <summary>D-04: "Keep as quoted" must say why, and record the lines that still differ.</summary>
    [Fact]
    public async Task Keeping_the_old_quantities_requires_a_reason_and_records_it_with_the_differing_lines()
    {
        using var db = new TestDb();
        await using var context = db.ContextFor(Tenant);
        SeedQuoteBuiltOnRevision3WithRevision4Arrived(context, withImpact: false);
        var service = new QuoteService(context, null!, new StubConfig());

        await Assert.ThrowsAsync<ArgumentException>(() =>
            service.ResolveRevisionImpactAsync(QuoteId, Tenant, "rep@nexora.invalid", "keep-0", reason: "  "));

        await service.ResolveRevisionImpactAsync(QuoteId, Tenant, "rep@nexora.invalid", "keep-1",
            reason: "Buyer's planner confirmed 20 by phone");

        var reviewed = await context.Set<LeadIdentityAuditEvent>().AsNoTracking()
            .SingleAsync(x => x.EventType == QuoteService.BuyerRevisionReviewedEventType);
        Assert.Contains("KEPT_AS_QUOTED", reviewed.PayloadJson);
        Assert.Contains("Buyer's planner confirmed 20 by phone", System.Text.RegularExpressions.Regex.Unescape(reviewed.PayloadJson));
        Assert.Contains("\"line\":\"10\"", reviewed.PayloadJson);
        Assert.Contains("\"to\":\"35\"", reviewed.PayloadJson);
        // The quote keeps its quantities and the warning clears.
        Assert.Equal(20m, (await context.QuoteItems.AsNoTracking().SingleAsync(x => x.Id == 98_371)).Quantity);
        var readiness = await service.EvaluateSendReadinessAsync(QuoteId, Tenant);
        Assert.DoesNotContain(readiness.Warnings, w => w.Code == "BUYER_REVISION_NEWER");
    }

    [Fact]
    public async Task Keeping_an_open_impact_records_the_reason_in_the_impact_resolution()
    {
        using var db = new TestDb();
        await using var context = db.ContextFor(Tenant);
        SeedQuoteBuiltOnRevision3WithRevision4Arrived(context);
        var service = new QuoteService(context, null!, new StubConfig());

        await service.ResolveRevisionImpactAsync(QuoteId, Tenant, "rep@nexora.invalid", "keep-2",
            reason: "Portal already closed for changes");

        var resolved = await context.Set<LeadIdentityAuditEvent>().AsNoTracking()
            .SingleAsync(x => x.EventType == LeadRevisionImpactQueries.ResolvedEventType);
        Assert.Contains("KEPT_AS_QUOTED", resolved.PayloadJson);
        Assert.Contains("Portal already closed for changes", resolved.PayloadJson);
        Assert.Contains("buyerAsks", resolved.PayloadJson);
        Assert.False(await LeadRevisionImpactQueries.OpenQuoteImpacts(context, Tenant, QuoteId).AnyAsync());
    }

    // ------------------------------------------------------------------------ test plumbing

    /// <summary>
    /// The shape driven on 2026-09-15: a draft prepared from lead revision 3 (two lines, 20 and
    /// 1,500 pieces) plus one line the rep added by hand; then revision 4 arrives with line 10 at
    /// 35, line 20 at 2,000 and a new line 30. The impact row is exactly what
    /// <c>LeadIdentityApplicationService.AddImpactsAsync</c> writes.
    /// </summary>
    private static void SeedQuoteBuiltOnRevision3WithRevision4Arrived(ErpRfqAutomationContext context, bool sent = false,
        bool withImpact = true, bool revision4FromBuyer = true, bool rfqFrozenOnRevision4 = false)
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
            LeadOccurrenceRecordKind.Ingestion,
            (98_361, 98_321, "10", 20m), (98_362, 98_322, "20", 1500m));
        // Revision 4: what the customer just sent.
        var revision4 = SeedRevision(context, 98_412, revisionNumber: 4, occurrenceId: 98_402,
            revision4FromBuyer ? LeadOccurrenceRecordKind.Ingestion : LeadOccurrenceRecordKind.IdentityBaseline,
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
            SourceLeadRevisionId = rfqFrozenOnRevision4 ? 98_412 : 98_411, SourceLeadItemRevisionId = rfqFrozenOnRevision4 ? 98_363 : 98_361,
            CreatedBy = "seed", CreatedDate = DateTime.UtcNow
        });
        context.Rfqitems.Add(new Rfqitem
        {
            Id = 98_342, Rfqid = 98_331, LineItemNo = "20", Quantity = 1500m, UnitOfMeasure = "EA",
            SourceLeadRevisionId = rfqFrozenOnRevision4 ? 98_412 : 98_411, SourceLeadItemRevisionId = rfqFrozenOnRevision4 ? 98_364 : 98_362,
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

        if (withImpact)
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
        long occurrenceId, LeadOccurrenceRecordKind kind, params (long ItemRevisionId, long LeadItemId, string Line, decimal Quantity)[] lines)
    {
        var batch = new LeadIngestionBatch
        {
            Id = Guid.NewGuid(), BusinessUnitId = Tenant, SourceChannel = "Email", CreatedBy = "seed",
            CreatedAtUtc = DateTimeOffset.UtcNow, UpdatedAtUtc = DateTimeOffset.UtcNow
        };
        context.Set<LeadIngestionBatch>().Add(batch);
        context.Set<LeadIngestionOccurrence>().Add(new LeadIngestionOccurrence
        {
            Id = occurrenceId, RecordKind = kind, BusinessUnitId = Tenant, BatchId = batch.Id, LeadId = LeadId,
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
