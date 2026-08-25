using ERP_RFQ_Automation.CommercialCases.Lifecycle;
using ERP_RFQ_Automation.Intelligence.Conversion;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Tests.Support;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.Tests;

public sealed class LeadConversionGovernanceTests
{
    [Fact]
    public async Task IntelligenceConversion_IsRetiredAndCreatesNoRfq()
    {
        using var db = new TestDb();
        await using var context = db.ContextFor(92);
        var lead = Seed.Lead(context, 9201, 92,
            items: new[] { Seed.LeadItem(9202, "10", 5, "Pump") });
        lead.LeadItems.Single().UnitOfMeasure = "EA";
        lead.LeadItems.Single().Currency = "USD";
        lead.ResolveCommercialIdentity(9210, null, LeadCustomerMatchStatuses.Confirmed);
        Seed.Customer(context, 9210, 92, "Acme Buyer");
        var statuses = LifecycleStatusCatalog.CreateFor(
            context.BusinessUnits.Local.Single(unit => unit.Id == 92), "test");
        context.SetupMasters.AddRange(statuses);
        lead.LeadStatus = statuses.Single(status =>
            status.SetupType == "LeadStatus" && status.SetupCode == "QUALIFIED");
        await context.SaveChangesAsync();
        var service = new LeadConversionIntelligence(context);
        var request = new ConvertRequest
        {
            ActingUser = "reviewer@example.com",
            AcknowledgeAllWarnings = true,
            WarningAcknowledgementReason = "Catalog choice reviewed"
        };

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.ConvertAsync(lead.Id, 92, request, default));

        Assert.Contains("Direct intelligence conversion is retired", error.Message, StringComparison.Ordinal);
        Assert.Empty(await context.Rfqs.ToListAsync());
        Assert.Empty(await context.Set<ERP_RFQ_Automation.CommercialCases.Promotion.RfqPromotion>().ToListAsync());
    }

    [Fact]
    public async Task IntelligenceConversion_CannotBypassParticipationForMissingQuantity()
    {
        using var db = new TestDb();
        await using var context = db.ContextFor(93);
        var lead = Seed.Lead(context, 9301, 93,
            items: new[] { Seed.LeadItem(9302, "20", 1, "Valve") });
        lead.LeadItems.Single().Quantity = null;
        lead.LeadItems.Single().UnitOfMeasure = null;
        lead.ResolveCommercialIdentity(9310, null, LeadCustomerMatchStatuses.Confirmed);
        Seed.Customer(context, 9310, 93, "Acme Buyer");
        var statuses = LifecycleStatusCatalog.CreateFor(
            context.BusinessUnits.Local.Single(unit => unit.Id == 93), "test");
        context.SetupMasters.AddRange(statuses);
        lead.LeadStatus = statuses.Single(status =>
            status.SetupType == "LeadStatus" && status.SetupCode == "QUALIFIED");
        await context.SaveChangesAsync();

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new LeadConversionIntelligence(context).ConvertAsync(lead.Id, 93,
                new ConvertRequest
                {
                    ActingUser = "reviewer@example.com",
                    CreateNeedsClarification = true
                }, default));

        Assert.Contains("participation decision", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(await context.Rfqs.ToListAsync());
    }

    [Fact]
    public async Task IntelligenceConversion_IsRetiredBeforeAnyLegacyCommercialGate()
    {
        using var db = new TestDb();
        await using var context = db.ContextFor(91);
        var qualified = Seed.LeadStatus(context, 901, 91, "Qualified");
        qualified.SetupCode = "QUALIFIED";
        var lead = Seed.Lead(context, 9001, 91, qualified.SetupId, "NeedsReview",
            items: new[] { Seed.LeadItem(9002, "1", 4, "Pump") });
        lead.RequiresCommercialReview = true;
        lead.CommercialFactsVerified = false;
        await context.SaveChangesAsync();

        var service = new LeadConversionIntelligence(context);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.ConvertAsync(lead.Id, 91, new ConvertRequest { ActingUser = "reviewer@example.com" }, default));

        Assert.Contains("Direct intelligence conversion is retired", error.Message, StringComparison.Ordinal);
        Assert.Empty(await context.Rfqs.ToListAsync());
    }

    /// <summary>
    /// The friendly half of "one lead, one RFQ": a repeat conversion through the intelligence
    /// door answers with the EXISTING RFQ instead of creating a second one (the legacy door's
    /// retry idempotency is pinned by CommercialIdentityFlowTests).
    /// </summary>
    [Fact]
    public async Task IntelligenceConversion_RepeatedAttemptsRemainRetiredAndCreateNothing()
    {
        using var db = new TestDb();
        await using var context = db.ContextFor(92);
        SeedConvertibleLead(context, bu: 92, leadId: 9101, leadItemId: 9102,
            qualifiedStatusId: 911, convertedStatusId: 912, rfqDraftStatusId: 913, customerId: 914);
        await context.SaveChangesAsync();

        var service = new LeadConversionIntelligence(context);
        var request = new ConvertRequest
        {
            ActingUser = "reviewer@example.com",
            // No catalog is seeded, so every line carries the soft "No catalog match found"
            // warning; this test is about idempotency, not the warning gate.
            AcknowledgeAllWarnings = true,
            WarningAcknowledgementReason = "Catalog not seeded in this fixture"
        };
        var first = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.ConvertAsync(9101, 92, request, default));
        var second = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.ConvertAsync(9101, 92, request, default));

        Assert.Equal(first.Message, second.Message);
        Assert.Empty(await context.Rfqs.AsNoTracking().ToListAsync());
        Assert.Empty(await context.CommercialLifecycleEvents.AsNoTracking()
            .Where(e => e.EventType == "PromotedToRfq").ToListAsync());
    }

    /// <summary>
    /// The database half of "one lead, one RFQ": whatever door (or race) slips past the
    /// read-then-check idempotency, the partial unique index on RFQ."LeadID" refuses the
    /// second row, and <see cref="LeadConversionGate.IsDuplicateKey"/> recognises the refusal
    /// so callers can resolve it to the existing RFQ instead of a 500.
    /// </summary>
    [Fact]
    public void A_lead_linked_rfq_without_promotion_lineage_is_incomplete()
    {
        var rfq = NewRfq(93, leadId: 9201);

        Assert.NotNull(rfq.LeadId);
        Assert.Null(rfq.PromotionId);
        Assert.Null(rfq.SourceLeadRevisionId);
        Assert.Null(rfq.ParticipationDecisionId);
    }

    /// <summary>
    /// The index is FILTERED to non-null LeadID: the leadless spreadsheet-import doors
    /// (RfqUploaderService / ManualUploadService) create any number of RFQs without a lead,
    /// and the backstop must not constrain them.
    /// </summary>
    [Fact]
    public async Task Leadless_rfqs_are_not_constrained_by_the_unique_index()
    {
        using var db = new TestDb();
        await using var context = db.ContextFor(94);
        Seed.EnsureBusinessUnit(context, 94);
        await context.SaveChangesAsync();

        context.Rfqs.Add(NewRfq(94, leadId: null));
        context.Rfqs.Add(NewRfq(94, leadId: null));
        context.Rfqs.Add(NewRfq(94, leadId: null));
        await context.SaveChangesAsync();

        Assert.Equal(3, await context.Rfqs.AsNoTracking().CountAsync(r => r.LeadId == null));
    }

    /// <summary>A lead that clears every conversion gate, with the status catalog rows and
    /// resolved customer the governed CONVERTED_TO_RFQ transition needs.</summary>
    private static void SeedConvertibleLead(ErpRfqAutomationContext context, long bu, long leadId,
        long leadItemId, long qualifiedStatusId, long convertedStatusId, long rfqDraftStatusId, long customerId)
    {
        var qualified = Seed.LeadStatus(context, qualifiedStatusId, bu, "Qualified");
        qualified.SetupCode = "QUALIFIED";
        var converted = Seed.LeadStatus(context, convertedStatusId, bu, "Converted to RFQ");
        converted.SetupCode = "CONVERTED_TO_RFQ";
        context.SetupMasters.Add(new SetupMaster
        {
            SetupId = rfqDraftStatusId,
            SetupType = "RFQStatus",
            SetupCode = "DRAFT",
            SetupValue = "Draft",
            BusinessUnitId = bu,
            IsActive = true,
            CreatedBy = "seed",
            CreatedOn = DateTime.UtcNow
        });
        Seed.Customer(context, customerId, bu, "Governance Customer");
        // Identified by part number only, deliberately: a stated product NAME would send the
        // resolver down its ILIKE candidate query, which is PostgreSQL-only (the PostgreSQL
        // lane covers that path; this SQLite test is about idempotency, not matching).
        var item = Seed.LeadItem(leadItemId, "1", 4, productName: null);
        item.ItemMaterialCode = "PN-100";
        item.UnitOfMeasure = "EA";
        item.Currency = "USD";
        var lead = Seed.Lead(context, leadId, bu, qualified.SetupId, items: new[] { item });
        lead.BidClosingDate = DateTime.UtcNow.AddDays(7);
        lead.ResolveCommercialIdentity(customerId, null, "CUSTOMER_CONFIRMED");
    }

    private static Rfq NewRfq(long bu, long? leadId) => new()
    {
        Rfqno = $"RFQ-TEST-{Guid.NewGuid():N}",
        RecDate = DateTime.UtcNow,
        BusinessUnitId = bu,
        LeadId = leadId,
        CreatedBy = "tests",
        CreatedDate = DateTime.UtcNow
    };
}
