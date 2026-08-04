using ERP_RFQ_Automation.Agent.Models;
using ERP_RFQ_Automation.Procurement;
using ERP_RFQ_Automation.Tests.Support;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// A Supplier RFQ response deadline is a commitment the buyer makes to a supplier, so it
/// must be persisted as first-class evidence rather than discarded.
///
/// <c>PrepareSupplierRfqCommand</c> has always accepted a <c>DueOn</c>, but
/// <c>SupplierSolicitation</c> had no column for it: the value survived only as prose inside
/// the free-text Notes column and inside an event payload, so it could never be queried,
/// listed on the workbench, or chased. These tests pin the deadline as a real column that
/// round-trips to the read model, and pin the validation that stops an unhonourable deadline
/// from being accepted in the first place.
/// </summary>
public sealed class SupplierRfqResponseDeadlineTests
{
    [Fact]
    public async Task Preparing_a_supplier_rfq_persists_the_response_deadline_as_a_queryable_column()
    {
        using var fixture = new ProcurementScenario();
        await MakeSourcingReadyAsync(fixture);
        var created = await fixture.Execute(service => service.CreateOrOpenSourcingCaseAsync(
            CreateCase(fixture, "deadline-case")));
        var candidate = Assert.Single(created.Candidates);
        var dueOn = new DateTime(2027, 3, 1, 12, 0, 0, DateTimeKind.Utc);

        var prepared = await fixture.Execute(service => service.PrepareSupplierRfqAsync(
            new PrepareSupplierRfqCommand(fixture.BusinessUnitId, created.Id, candidate.SupplierId,
                dueOn, created.Version, "deadline-prepare", "qa", "corr-deadline")));

        await using var verify = fixture.Context();
        var solicitation = await verify.Set<SupplierSolicitation>()
            .SingleAsync(x => x.Id == prepared.SupplierSolicitationId);
        Assert.Equal(dueOn, solicitation.DueOn);
    }

    [Fact]
    public async Task The_workbench_echoes_the_response_deadline_back_to_the_buyer()
    {
        using var fixture = new ProcurementScenario();
        await MakeSourcingReadyAsync(fixture);
        var created = await fixture.Execute(service => service.CreateOrOpenSourcingCaseAsync(
            CreateCase(fixture, "deadline-view-case")));
        var candidate = Assert.Single(created.Candidates);
        var dueOn = new DateTime(2027, 4, 2, 8, 30, 0, DateTimeKind.Utc);

        await fixture.Execute(service => service.PrepareSupplierRfqAsync(
            new PrepareSupplierRfqCommand(fixture.BusinessUnitId, created.Id, candidate.SupplierId,
                dueOn, created.Version, "deadline-view-prepare", "qa", "corr-deadline-view")));

        var workbench = await fixture.Execute(service =>
            service.GetWorkbenchAsync(fixture.BusinessUnitId, fixture.RfqId));

        var view = Assert.Single(workbench.Solicitations);
        Assert.Equal(dueOn, view.DueOn);
    }

    [Fact]
    public async Task Omitting_a_deadline_stores_no_deadline_rather_than_an_invented_one()
    {
        using var fixture = new ProcurementScenario();
        await MakeSourcingReadyAsync(fixture);
        var created = await fixture.Execute(service => service.CreateOrOpenSourcingCaseAsync(
            CreateCase(fixture, "no-deadline-case")));
        var candidate = Assert.Single(created.Candidates);

        var prepared = await fixture.Execute(service => service.PrepareSupplierRfqAsync(
            new PrepareSupplierRfqCommand(fixture.BusinessUnitId, created.Id, candidate.SupplierId,
                null, created.Version, "no-deadline-prepare", "qa", "corr-no-deadline")));

        await using var verify = fixture.Context();
        Assert.Null(await verify.Set<SupplierSolicitation>()
            .Where(x => x.Id == prepared.SupplierSolicitationId).Select(x => x.DueOn).SingleAsync());
    }

    [Fact]
    public async Task A_deadline_that_has_already_passed_is_rejected_instead_of_silently_accepted()
    {
        using var fixture = new ProcurementScenario();
        await MakeSourcingReadyAsync(fixture);
        var created = await fixture.Execute(service => service.CreateOrOpenSourcingCaseAsync(
            CreateCase(fixture, "past-deadline-case")));
        var candidate = Assert.Single(created.Candidates);

        await Assert.ThrowsAsync<ProcurementValidationException>(() => fixture.Execute(service =>
            service.PrepareSupplierRfqAsync(new PrepareSupplierRfqCommand(
                fixture.BusinessUnitId, created.Id, candidate.SupplierId,
                DateTime.UtcNow.AddDays(-1), created.Version, "past-deadline", "qa", "corr-past"))));

        await using var verify = fixture.Context();
        Assert.Empty(await verify.Set<SupplierSolicitation>().ToListAsync());
    }

    [Fact]
    public async Task A_local_time_deadline_is_rejected_so_the_stored_instant_is_unambiguous()
    {
        using var fixture = new ProcurementScenario();
        await MakeSourcingReadyAsync(fixture);
        var created = await fixture.Execute(service => service.CreateOrOpenSourcingCaseAsync(
            CreateCase(fixture, "local-deadline-case")));
        var candidate = Assert.Single(created.Candidates);

        await Assert.ThrowsAsync<ProcurementValidationException>(() => fixture.Execute(service =>
            service.PrepareSupplierRfqAsync(new PrepareSupplierRfqCommand(
                fixture.BusinessUnitId, created.Id, candidate.SupplierId,
                DateTime.SpecifyKind(DateTime.UtcNow.AddDays(3), DateTimeKind.Local),
                created.Version, "local-deadline", "qa", "corr-local"))));

        await using var verify = fixture.Context();
        Assert.Empty(await verify.Set<SupplierSolicitation>().ToListAsync());
    }

    private static CreateSourcingCaseCommand CreateCase(ProcurementScenario fixture, string key) => new(
        fixture.BusinessUnitId, fixture.RfqId, fixture.RfqItemId, 10, false,
        key, "qa", $"corr-{key}");

    private static async Task MakeSourcingReadyAsync(ProcurementScenario fixture)
    {
        await using var context = fixture.Context();
        var rfq = await context.Rfqs.SingleAsync(x => x.Id == fixture.RfqId);
        context.Entry(rfq).Property(x => x.NexoraSerial).CurrentValue = "NXR-QA-0001";
        var product = await context.Products.SingleAsync(x => x.Id == ProcurementTestData.Product);
        product.PreferredSupplierId = ProcurementTestData.Supplier;
        var line = await context.Rfqitems.SingleAsync(x => x.Id == fixture.RfqItemId);
        line.ManufacturerPartNumber = "QA-PART-0";
        line.ManufacturerName = "QA Maker";
        await context.SaveChangesAsync();
    }
}
