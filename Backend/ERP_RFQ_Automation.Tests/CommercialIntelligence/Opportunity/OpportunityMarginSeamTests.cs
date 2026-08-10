using System.Text.Json;
using ERP_RFQ_Automation.CommercialIntelligence.Opportunity;
using ERP_RFQ_Automation.Intelligence.Decision;
using ERP_RFQ_Automation.Inventory.Commercial;
using ERP_RFQ_Automation.LeadIdentity;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Reporting;
using ERP_RFQ_Automation.SupplierQuotes;
using ERP_RFQ_Automation.Tests.Support;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.Tests.CommercialIntelligence.Opportunity;

/// <summary>
/// The seam between the Lead Decision Brief (producer of the margin) and the opportunity queue
/// (consumer of it). Both sides were green in isolation and the join was broken: the brief declared
/// <c>MarginPotentialPct</c> and never assigned it, its own tests asserted the null, and the queue's
/// tests set the field by hand on the object — so nobody ever ran the two together, and in
/// production every opportunity carried "Escalate margin review", permanently.
///
/// <para>Every test here uses the REAL <see cref="LeadDecisionService"/> and the REAL
/// <see cref="GrossMarginService"/> behind the REAL <see cref="OpportunityPriorityApplicationService"/>.
/// Nothing is stubbed, so removing the wiring at either end fails a test here.</para>
///
/// <para>All identifiers, part numbers and company names below are obviously synthetic.</para>
/// </summary>
public sealed class OpportunityMarginSeamTests
{
    private const long TenantId = 73_101;
    private const long CustomerId = 73_201;
    private const long ProductId = 73_301;
    private const long CurrencyId = 73_401;
    private const long DraftStatusId = 73_501;
    private const string PartNo = "SEAM-PART-0001";

    private static readonly DateTime Anchor = new(2026, 7, 29, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime BidCloses = new(2035, 6, 1, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task A_margin_measured_from_the_priced_case_reaches_the_opportunity_queue()
    {
        using var database = new TestDb();
        await using var context = database.ContextFor(TenantId);
        var lead = await SeedOpportunityAsync(context, leadId: 74_001);
        // 20% on a synthetic landed cost of 80 against a customer price of 100.
        await SeedPricedQuoteAsync(context, lead, landedUnitCost: 80m, customerUnitPrice: 100m);

        var item = await ReconcileAsync(context, "seam-healthy-margin");

        var margin = item.Components.Single(x => x.Code == "margin");
        Assert.Equal("measured", margin.Status);
        Assert.Equal(20m, margin.Value);
        Assert.NotEqual("ESCALATE_APPROVAL", item.RecommendedActionCode);
        // Whatever else this opportunity is still missing, the cost evidence is no longer it.
        Assert.NotEqual("Verified cost and margin evidence is unavailable.", item.CurrentBlocker);
    }

    /// <summary>
    /// The one test that fails if the wiring is removed from EITHER end. Delete the producer's
    /// assignment and the margin is null again, so nothing escalates and this assertion fails;
    /// restore the consumer's old <c>margin is null or &lt; 15m</c> and the "unmeasured" test below
    /// fails instead.
    /// </summary>
    [Fact]
    public async Task Only_an_evidenced_thin_margin_escalates_for_approval()
    {
        using var database = new TestDb();
        await using var context = database.ContextFor(TenantId);
        var lead = await SeedOpportunityAsync(context, leadId: 74_002);
        // 5%: a synthetic landed cost of 95 against a customer price of 100.
        await SeedPricedQuoteAsync(context, lead, landedUnitCost: 95m, customerUnitPrice: 100m);

        var item = await ReconcileAsync(context, "seam-thin-margin");

        var margin = item.Components.Single(x => x.Code == "margin");
        Assert.Equal("measured", margin.Status);
        Assert.Equal(5m, margin.Value);
        Assert.Equal("ESCALATE_APPROVAL", item.RecommendedActionCode);
    }

    /// <summary>
    /// An unpriced opportunity has no margin, and "no margin" is not "thin margin". It must not
    /// escalate; the gap is named instead, on the blocker and in the brief's own reasons.
    /// </summary>
    [Fact]
    public async Task An_unmeasured_margin_names_the_gap_instead_of_escalating()
    {
        using var database = new TestDb();
        await using var context = database.ContextFor(TenantId);
        var lead = await SeedOpportunityAsync(context, leadId: 74_003);

        var item = await ReconcileAsync(context, "seam-unmeasured-margin");

        var margin = item.Components.Single(x => x.Code == "margin");
        Assert.Equal("unavailable", margin.Status);
        Assert.Null(margin.Value);
        Assert.NotEqual("ESCALATE_APPROVAL", item.RecommendedActionCode);
        Assert.Equal("Verified cost and margin evidence is unavailable.", item.CurrentBlocker);

        var brief = await new LeadDecisionService(context, new GrossMarginService(context))
            .GetBriefAsync(lead.Id, TenantId, default);
        Assert.Null(brief.MarginPotentialPct);
        Assert.Equal(0, brief.MarginCostedItems);
        Assert.Contains(brief.Reasons, reason =>
            reason.Contains("Margin is not measured yet", StringComparison.Ordinal));
    }

    /// <summary>
    /// The brief must read the case it belongs to and no other. A priced quote on a different
    /// lead's case is not this lead's margin evidence.
    /// </summary>
    [Fact]
    public async Task Margin_evidence_belonging_to_another_case_is_not_borrowed()
    {
        using var database = new TestDb();
        await using var context = database.ContextFor(TenantId);
        var priced = await SeedOpportunityAsync(context, leadId: 74_004);
        await SeedPricedQuoteAsync(context, priced, landedUnitCost: 80m, customerUnitPrice: 100m);
        var unpriced = await SeedOpportunityAsync(context, leadId: 74_005);

        var brief = await new LeadDecisionService(context, new GrossMarginService(context))
            .GetBriefAsync(unpriced.Id, TenantId, default);

        Assert.Null(brief.MarginPotentialPct);
        Assert.Equal(0, brief.MarginCostedItems);
        Assert.False(brief.IsMarginComplete);
    }

    // ───────────────────────────── helpers

    private static async Task<OpportunityPriorityItem> ReconcileAsync(
        ErpRfqAutomationContext context, string key)
    {
        var service = new OpportunityPriorityApplicationService(
            context,
            new StubTenant(TenantId),
            new LeadDecisionService(context, new GrossMarginService(context)));
        await service.ReconcileAsync(
            TenantId, new ReconcileOpportunityPrioritiesCommand($"correlation-{key}", key, "seam-tests"), default);
        var page = await service.QueryAsync(
            TenantId, new OpportunityPriorityQuery(), OpportunityPriorityAccessScope.ForTenant(), default);
        return page.Items.Single();
    }

    /// <summary>
    /// A lead that reaches the margin rung of the action ladder: one line with an exact catalogue
    /// identity, a canonical customer, a live deadline and complete internal availability.
    /// </summary>
    private static async Task<Lead> SeedOpportunityAsync(ErpRfqAutomationContext context, long leadId)
    {
        if (!await context.Customers.IgnoreQueryFilters().AnyAsync(x => x.Id == CustomerId))
        {
            Seed.Customer(context, CustomerId, TenantId, "Synthetic Buyer (test fixture)");
            context.Products.Add(new Product
            {
                Id = ProductId,
                Buid = TenantId,
                PartNo = PartNo,
                ProductName = "Synthetic seam widget",
                IsActive = true,
                CreatedBy = "seam-tests",
                CreatedOn = Anchor
            });
            context.Currencies.Add(new Currency
            {
                Id = CurrencyId,
                BusinessUnitId = TenantId,
                Code = "SAR",
                CurrencyName = "Saudi Riyal",
                ExchangeRate = 1m,
                IsBaseCurrency = true,
                IsActive = true,
                CreatedBy = "seam-tests",
                CreatedOn = Anchor
            });
            context.SetupMasters.Add(new SetupMaster
            {
                SetupId = DraftStatusId,
                BusinessUnitId = TenantId,
                SetupType = "QuoteStatus",
                SetupCode = "DRAFT",
                SetupValue = "Draft",
                IsActive = true,
                CreatedBy = "seam-tests",
                CreatedOn = Anchor
            });
            await context.SaveChangesAsync();
        }

        var lead = Seed.Lead(context, leadId, TenantId, buyersName: "Synthetic Buyer (test fixture)");
        lead.BidClosingDate = BidCloses;
        lead.LeadItems.Add(new LeadItem
        {
            Id = leadId * 10,
            ItemMaterialCode = PartNo,
            ProductShortName = "Synthetic seam widget",
            Quantity = 10,
            UnitPrice = 100m,
            Currency = "SAR"
        });
        await context.SaveChangesAsync();
        lead.ResolveCommercialIdentity(CustomerId, null, "VERIFIED");
        await context.SaveChangesAsync();

        await SeedCompleteFulfilmentAsync(context, lead, leadLineId: leadId * 10 + 1, quantity: 10m);
        return lead;
    }

    /// <summary>
    /// A draft customer quote on the lead's own commercial case whose single line has been priced
    /// from an approved supplier award — the only record in the system that carries a landed cost
    /// and the price built from it, in one stated currency, on one row.
    /// </summary>
    private static async Task SeedPricedQuoteAsync(
        ErpRfqAutomationContext context, Lead lead, decimal landedUnitCost, decimal customerUnitPrice)
    {
        var rfq = new Rfq
        {
            Id = lead.Id + 100_000,
            Rfqno = $"RFQ-{lead.Id}",
            RecDate = Anchor,
            LeadId = lead.Id,
            CustomerId = CustomerId,
            BusinessUnitId = TenantId,
            CreatedBy = "seam-tests",
            CreatedDate = Anchor
        };
        rfq.InheritCommercialIdentity(lead);
        context.Rfqs.Add(rfq);

        var quote = new Quote
        {
            Id = lead.Id + 200_000,
            QuoteNo = $"QT-{lead.Id}",
            Rfqid = rfq.Id,
            CustomerId = CustomerId,
            BusinessUnitId = TenantId,
            QuoteDate = Anchor,
            StatusId = DraftStatusId,
            CurrencyId = CurrencyId,
            TotalAmount = customerUnitPrice * 10m,
            RevisionNo = 1,
            CreatedBy = "seam-tests",
            CreatedDate = Anchor
        };
        quote.InheritCommercialIdentity(rfq);
        quote.QuoteItems.Add(new QuoteItem
        {
            Id = lead.Id + 300_000,
            QuoteId = quote.Id,
            ProductId = ProductId,
            ItemDescription = "Synthetic seam widget",
            Quantity = 10m,
            UnitPrice = customerUnitPrice,
            TotalAmount = customerUnitPrice * 10m,
            CreatedBy = "seam-tests",
            CreatedDate = Anchor
        });
        context.Quotes.Add(quote);
        await context.SaveChangesAsync();

        // CustomerQuoteSourcingDecision carries seven composite foreign keys into the sourcing
        // aggregate. Standing that whole chain up would make this test about the sourcing fixture
        // rather than about the margin seam, so referential enforcement is stood down for the seed
        // exactly as Gate8GrossMarginTests does. Quantity, landed cost, price, currency and the
        // case linkage — the columns this seam actually depends on — are unaffected.
        await context.Database.ExecuteSqlRawAsync("PRAGMA foreign_keys = OFF");
        await context.Database.ExecuteSqlRawAsync("PRAGMA ignore_check_constraints = ON");
        context.Set<CustomerQuoteSourcingDecision>().Add(new CustomerQuoteSourcingDecision
        {
            Id = lead.Id + 400_000,
            BusinessUnitId = TenantId,
            QuoteId = quote.Id,
            QuoteItemId = lead.Id + 300_000,
            RfqId = rfq.Id,
            RfqItemId = lead.Id + 500_000,
            CommercialDemandLineId = 0,
            SourcingCaseId = 0,
            SourcingAwardId = 0,
            SupplierQuotedItemId = 0,
            SupplierQuoteId = 0,
            SupplierQuoteRevisionId = 0,
            SupplierQuoteLineId = 0,
            NexoraSerial = $"NXR-SEAM-{lead.Id}",
            Quantity = 10m,
            SupplierLandedUnitCost = landedUnitCost,
            TargetMarginPercent = 20m,
            CustomerUnitPrice = customerUnitPrice,
            CurrencyId = CurrencyId,
            IdempotencyKey = $"seam:{lead.Id}",
            RequestHash = new string('0', 64),
            Rationale = "seam-tests",
            CreatedOn = Anchor,
            CreatedBy = "seam-tests",
            CorrelationId = $"corr-seam-{lead.Id}"
        });
        await context.SaveChangesAsync();
        await context.Database.ExecuteSqlRawAsync("PRAGMA foreign_keys = ON");
    }

    /// <summary>Persisted line-resolution evidence covering the whole line from internal stock.</summary>
    private static async Task SeedCompleteFulfilmentAsync(
        ErpRfqAutomationContext context, Lead lead, long leadLineId, decimal quantity)
    {
        var now = DateTime.UtcNow;
        var revisionNumber = Math.Max(1, lead.CurrentRevisionNumber + 1);
        var batch = new LeadIngestionBatch
        {
            Id = Guid.NewGuid(),
            BusinessUnitId = TenantId,
            SourceChannel = "Test",
            CreatedBy = "seam-tests",
            CreatedAtUtc = DateTimeOffset.UtcNow,
            UpdatedAtUtc = DateTimeOffset.UtcNow
        };
        var occurrence = new LeadIngestionOccurrence
        {
            BusinessUnitId = TenantId,
            Batch = batch,
            SourceChannel = "Test",
            IdempotencyKey = $"seam-occurrence-{lead.Id}-{revisionNumber}",
            LogicalInquiryFingerprint = $"{lead.Id}a".PadRight(64, 'a')[..64],
            Classification = LeadOccurrenceClassification.New,
            ProcessingPath = LeadProcessingPath.Deterministic,
            IngestedAtUtc = DateTimeOffset.UtcNow,
            CreatedAtUtc = DateTimeOffset.UtcNow,
            ActorId = "seam-tests",
            CorrelationId = $"seam-correlation-{lead.Id}-{revisionNumber}"
        };
        var revision = new LeadRevision
        {
            BusinessUnitId = TenantId,
            Lead = lead,
            RevisionNumber = revisionNumber,
            EstablishedByOccurrence = occurrence,
            LogicalInquiryFingerprint = $"{lead.Id}b".PadRight(64, 'b')[..64],
            SnapshotJson = "{}",
            CreatedAtUtc = DateTimeOffset.UtcNow,
            CreatedBy = "seam-tests",
            ProcessingPath = LeadProcessingPath.Deterministic
        };
        revision.Items.Add(new LeadItemRevision
        {
            Id = leadLineId,
            BusinessUnitId = TenantId,
            LineNumber = 1,
            LineFingerprint = $"{lead.Id}c".PadRight(64, 'c')[..64],
            SnapshotJson = JsonSerializer.Serialize(new { part = PartNo, quantity })
        });
        context.Add(revision);
        await context.SaveChangesAsync();

        lead.CurrentRevisionId = revision.Id;
        lead.CurrentRevisionNumber = revisionNumber;
        context.Set<LeadLineCommercialResolution>().Add(new LeadLineCommercialResolution
        {
            BusinessUnitId = TenantId,
            LeadId = lead.Id,
            LeadRevisionId = revision.Id,
            LeadLineId = leadLineId,
            ResolutionBatchId = Guid.NewGuid(),
            ResourceLimit = 10,
            RequestedPartNumber = PartNo,
            RequestedQuantity = quantity,
            Classification = CommercialResolutionClassification.KnownInStock,
            AvailableToPromise = quantity,
            IncomingAvailable = 0m,
            FulfilmentJson = "{}",
            RelatedResourcesJson = "[]",
            ProductResolutionJson = "{}",
            ResolutionMethod = "LocalDeterministicTest",
            InventoryAsOfUtc = now,
            ResolvedOn = now
        });
        await context.SaveChangesAsync();
    }
}
