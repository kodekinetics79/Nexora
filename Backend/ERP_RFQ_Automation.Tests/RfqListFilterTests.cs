using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Repositories;
using ERP_RFQ_Automation.Tests.Support;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// The RFQs list's Client and Unassigned filters. The owner is the lead's owner, the person the
/// Owner column shows; an RFQ with no lead has no owner.
/// </summary>
public sealed class RfqListFilterTests
{
    private const long Bu = 9_861;
    private static readonly DateTime Now = new(2026, 9, 29, 9, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task The_client_filter_keeps_only_that_clients_rfqs()
    {
        using var db = new TestDb();
        await using var context = db.ContextFor(Bu);
        Seed(context);
        await context.SaveChangesAsync();

        var (rows, total) = await new RfqRepository(context).GetAllAsync(Bu, 1, 50, customerId: 98_611);

        Assert.Equal(2, total);
        Assert.Equal(new[] { "RFQ-A", "RFQ-C" }, rows.Select(r => r.Rfqno).Order().ToArray());
    }

    [Fact]
    public async Task Unassigned_keeps_rfqs_whose_lead_has_no_owner_and_mine_keeps_the_owners()
    {
        using var db = new TestDb();
        await using var context = db.ContextFor(Bu);
        Seed(context);
        await context.SaveChangesAsync();
        var repository = new RfqRepository(context);

        var (unassigned, _) = await repository.GetAllAsync(Bu, 1, 50, unassigned: true);
        var (mine, _) = await repository.GetAllAsync(Bu, 1, 50, assignedToId: 98_601);

        Assert.Equal(new[] { "RFQ-B", "RFQ-C" }, unassigned.Select(r => r.Rfqno).Order().ToArray());
        Assert.Equal(new[] { "RFQ-A" }, mine.Select(r => r.Rfqno).ToArray());
    }

    private static void Seed(ErpRfqAutomationContext context)
    {
        Support.Seed.EnsureBusinessUnit(context, Bu);
        context.Users.Add(new User
        {
            Id = 98_601, FirstName = "Rep", LastName = "One", Email = "rep@tenant.test", PasswordHash = "x",
            ImageUrl = string.Empty, Buid = Bu, IsActive = true, CreatedBy = "seed", CreatedOn = Now
        });
        Support.Seed.Customer(context, 98_611, Bu, "ASMO");
        Support.Seed.Customer(context, 98_612, Bu, "Saudi Electricity Company");
        var owned = Support.Seed.Lead(context, 98_621, Bu);
        owned.AssignTo = 98_601;
        var unowned = Support.Seed.Lead(context, 98_622, Bu);
        context.Rfqs.AddRange(
            new Rfq { Id = 98_631, Rfqno = "RFQ-A", LeadId = 98_621, CustomerId = 98_611, BusinessUnitId = Bu, CreatedBy = "seed", CreatedDate = Now, RecDate = Now },
            new Rfq { Id = 98_632, Rfqno = "RFQ-B", LeadId = 98_622, CustomerId = 98_612, BusinessUnitId = Bu, CreatedBy = "seed", CreatedDate = Now, RecDate = Now },
            new Rfq { Id = 98_633, Rfqno = "RFQ-C", CustomerId = 98_611, BusinessUnitId = Bu, CreatedBy = "seed", CreatedDate = Now, RecDate = Now });
    }
}
