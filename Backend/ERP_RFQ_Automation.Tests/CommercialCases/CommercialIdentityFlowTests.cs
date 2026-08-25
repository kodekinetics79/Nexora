using ERP_RFQ_Automation.CommercialCases.Lifecycle;
using ERP_RFQ_Automation.DTOs.Lead;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Repositories;
using ERP_RFQ_Automation.Tests.Support;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.Tests.CommercialCases;

public sealed class CommercialIdentityFlowTests
{
    [Fact]
    public async Task Quote_revision_retains_tenant_rfq_serial_customer_and_contact()
    {
        using var db = new TestDb();
        await using var context = db.ContextFor(829);
        var lead = Seed.Lead(context, 1, 829); Seed.Customer(context, 7001, 829, "Quote customer");
        Seed.Contact(context, 7002, 829, 7001);
        await context.SaveChangesAsync();
        lead.ResolveCommercialIdentity(7001, 7002, "CONFIRMED");
        await context.SaveChangesAsync();
        var rfq = new Rfq { Id = 2, LeadId = 1, BusinessUnitId = 829 };
        rfq.InheritCommercialIdentity(lead);
        var predecessor = new Quote { Id = 3, Rfqid = 2, BusinessUnitId = 829 };
        predecessor.InheritCommercialIdentity(rfq);
        var revision = new Quote { Rfqid = 2, BusinessUnitId = 829 };

        revision.InheritCommercialIdentity(predecessor);

        Assert.Equal(predecessor.CommercialCaseId, revision.CommercialCaseId);
        Assert.Equal(predecessor.NexoraSerial, revision.NexoraSerial);
        Assert.Equal(predecessor.CustomerId, revision.CustomerId);
        Assert.Equal(predecessor.ContactId, revision.ContactId);
    }

    [Fact]
    public async Task LeadReview_PersistsTenantValidatedCustomerAndContactIdentity()
    {
        using var db = new TestDb();
        await using var context = db.ContextFor(830);
        var lead = Seed.Lead(context, 4001, 830, parseStatus: "NeedsReview");
        Seed.Customer(context, 7001, 830, "Review Customer");
        Seed.Contact(context, 7002, 830, 7001, "casey@example.com");
        await context.SaveChangesAsync();

        var result = await new LeadRepository(context).SubmitLeadReviewAsync(
            lead.Id,
            830,
            new LeadReviewSubmitDTO
            {
                ExpectedVersion = 1,
                Action = "save",
                Header = new LeadReviewHeaderDTO { CustomerId = 7001, ContactId = 7002 }
            },
            "reviewer@example.com");

        Assert.NotNull(result);
        Assert.Equal(7001, result.CustomerId);
        Assert.Equal(7002, result.ContactId);
        Assert.Equal("CONFIRMED", result.CustomerMatchStatus);
        var auditJson = (await context.Set<LeadReviewAudit>().SingleAsync()).AfterJson;
        Assert.Contains("customerId", auditJson, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("7001", auditJson, StringComparison.Ordinal);
        Assert.Contains("7002", auditJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DirectLeadRepositoryConversion_IsRetiredWithoutWrites()
    {
        using var db = new TestDb();
        await using var context = db.ContextFor(831);
        var qualified = Status(context, 6101, 831, "LeadStatus", "QUALIFIED");
        Status(context, 6102, 831, "LeadStatus", "CONVERTED_TO_RFQ");
        Status(context, 6103, 831, "RFQStatus", "DRAFT");
        Seed.Customer(context, 7101, 831, "Conversion Customer");
        Seed.Contact(context, 7102, 831, 7101);
        var lead = Seed.Lead(context, 4101, 831, qualified.SetupId,
            items: new[] { Seed.LeadItem(4102, "1", 2, "Pump") });
        await context.SaveChangesAsync();
        lead.ResolveCommercialIdentity(7101, 7102, "CONFIRMED");
        await context.SaveChangesAsync();

        var repository = new LeadRepository(context);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            repository.ConvertLeadToRfqAsync(lead.Id, 831, "user@example.com"));

        Assert.Contains("Direct LeadRepository RFQ creation is retired", error.Message, StringComparison.Ordinal);
        Assert.Empty(await context.Rfqs.ToListAsync());
        Assert.Empty(await context.CommercialLifecycleEvents.ToListAsync());
        Assert.Empty(await context.LifecycleOutboxMessages.ToListAsync());
    }

    [Fact]
    public async Task DirectLeadRepositoryConversion_FailsClosedBeforeLifecycleWork()
    {
        using var db = new TestDb();
        await using var context = db.ContextFor(832);
        var qualified = Status(context, 6201, 832, "LeadStatus", "QUALIFIED");
        Status(context, 6203, 832, "RFQStatus", "DRAFT");
        Seed.Customer(context, 7201, 832, "Rollback Customer");
        var lead = Seed.Lead(context, 4201, 832, qualified.SetupId,
            items: new[] { Seed.LeadItem(4202, "1", 2, "Valve") });
        await context.SaveChangesAsync();
        lead.ResolveCommercialIdentity(7201, null, "CUSTOMER_CONFIRMED_CONTACT_UNRESOLVED");
        await context.SaveChangesAsync();

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new LeadRepository(context).ConvertLeadToRfqAsync(lead.Id, 832, "user@example.com"));

        context.ChangeTracker.Clear();
        Assert.Contains("Direct LeadRepository RFQ creation is retired", error.Message, StringComparison.Ordinal);
        Assert.Empty(await context.Rfqs.ToListAsync());
        Assert.Empty(await context.CommercialLifecycleEvents.ToListAsync());
        Assert.Equal(1, (await context.Leads.SingleAsync(item => item.Id == lead.Id)).LifecycleVersion);
    }

    [Fact]
    public async Task RfqApproval_RejectsCustomerMismatch()
    {
        using var db = new TestDb();
        await using var context = db.ContextFor(833);
        var lead = Seed.Lead(context, 4301, 833);
        Seed.Customer(context, 7301, 833, "Matched Customer");
        Seed.Contact(context, 7302, 833, 7301);
        var preparing = Status(context, 6301, 833, "RFQStatus", "QUOTE_PREPARATION");
        await context.SaveChangesAsync();
        lead.ResolveCommercialIdentity(7301, 7302, "CONFIRMED");
        await context.SaveChangesAsync();
        var rfq = new Rfq
        {
            Id = 4302,
            Rfqno = "RFQ-MISMATCH",
            RecDate = DateTime.UtcNow,
            LeadId = lead.Id,
            BusinessUnitId = 833,
            RfqstatusId = preparing.SetupId,
            CreatedBy = "seed",
            CreatedDate = DateTime.UtcNow
        };
        rfq.InheritCommercialIdentity(lead);
        context.Rfqs.Add(rfq);
        await context.SaveChangesAsync();

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new RfqRepository(context).ApproveAsync(rfq.Id, "user@example.com", 833, 9999));

        Assert.Contains("does not match", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(await context.Quotes.ToListAsync());
    }

    private static SetupMaster Status(
        ErpRfqAutomationContext context, long id, long businessUnitId, string type, string code)
    {
        Seed.EnsureBusinessUnit(context, businessUnitId);
        var status = new SetupMaster
        {
            SetupId = id,
            BusinessUnitId = businessUnitId,
            SetupType = type,
            SetupCode = code,
            SetupValue = code.Replace('_', ' '),
            IsActive = true,
            CreatedBy = "seed",
            CreatedOn = DateTime.UtcNow
        };
        context.SetupMasters.Add(status);
        return status;
    }
}
