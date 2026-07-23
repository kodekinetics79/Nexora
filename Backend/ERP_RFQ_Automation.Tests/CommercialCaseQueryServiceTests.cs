using ERP_RFQ_Automation.CommercialCases;
using ERP_RFQ_Automation.CommercialCases.Lifecycle;
using ERP_RFQ_Automation.MultiTenancy;
using ERP_RFQ_Automation.Tests.Support;

namespace ERP_RFQ_Automation.Tests;

public sealed class CommercialCaseQueryServiceTests
{
    [Fact]
    public async Task Search_finds_the_permanent_reference_and_remains_tenant_scoped()
    {
        using var db = new TestDb();
        long tenantOneCaseId;
        await using (var seed = db.ContextFor(null))
        {
            var tenantOne = Seed.Lead(seed, 901, 91, buyersName: "Northwind Buyer");
            Seed.Lead(seed, 902, 92, buyersName: "Northwind Buyer");
            await seed.SaveChangesAsync();
            tenantOneCaseId = tenantOne.CommercialCaseId;
        }

        await using var context = db.ContextFor(91);
        var service = new CommercialCaseQueryService(context, new StubTenant(91));

        var results = await service.SearchAsync(91, "NXR-2026", 20, CancellationToken.None);

        var result = Assert.Single(results);
        Assert.Equal(tenantOneCaseId, result.Id);
        Assert.Equal(901, result.LeadId);
        Assert.Equal("Northwind Buyer", result.BuyerName);
    }

    [Fact]
    public async Task Detail_returns_the_authoritative_lead_and_status_timeline()
    {
        using var db = new TestDb();
        long caseId;
        await using (var seed = db.ContextFor(null))
        {
            Seed.LeadStatus(seed, 991, 93, "Pending Identification");
            var lead = Seed.Lead(seed, 903, 93);
            await seed.SaveChangesAsync();
            await new LifecycleApplicationService(seed).TransitionLeadAsync(93, lead.Id,
                new LifecycleActor("reviewer-93", "AuthenticatedUser"),
                new LifecycleTransitionCommand("PENDING_IDENTIFICATION", 1, null, null,
                    "Api", "corr-903", "req-903", "case-detail-903"), false, default);
            caseId = lead.CommercialCaseId;
        }

        await using var context = db.ContextFor(93);
        var service = new CommercialCaseQueryService(context, new StubTenant(93));

        var result = await service.GetAsync(93, caseId, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal("NXR-2026-000001", result.MasterReference);
        Assert.Equal("Pending Identification", result.CurrentStatus);
        Assert.Contains(result.Documents, d => d.DocumentType == "Lead" && d.DocumentId == 903);
        Assert.Contains(result.StatusHistory, h => h.EventType == "Created");
        Assert.Contains(result.StatusHistory, h => h.EventType == "StatusTransitioned" && h.NewStatus == "PENDING_IDENTIFICATION");
        Assert.DoesNotContain(result.StatusHistory, h => h.EventType == "StatusChanged");
    }

    [Fact]
    public async Task Detail_does_not_disclose_a_case_from_another_tenant()
    {
        using var db = new TestDb();
        long caseId;
        await using (var seed = db.ContextFor(null))
        {
            var lead = Seed.Lead(seed, 904, 94);
            await seed.SaveChangesAsync();
            caseId = lead.CommercialCaseId;
        }

        await using var context = db.ContextFor(95);
        var service = new CommercialCaseQueryService(context, new StubTenant(95));

        Assert.Null(await service.GetAsync(95, caseId, CancellationToken.None));
    }
}
