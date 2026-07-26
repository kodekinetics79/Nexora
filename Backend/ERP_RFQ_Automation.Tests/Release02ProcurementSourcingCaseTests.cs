using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Procurement;
using ERP_RFQ_Automation.Tests.Support;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.Tests;

public sealed class Release02ProcurementSourcingCaseTests
{
    [Fact]
    public async Task Out_of_stock_line_establishes_one_demand_line_and_replays_case_creation()
    {
        using var fixture = new ProcurementScenario();
        await MakeSourcingReadyAsync(fixture);
        var command = CreateCase(fixture, "case-replay");

        var first = await fixture.Execute(service => service.CreateOrOpenSourcingCaseAsync(command));
        var replay = await fixture.Execute(service => service.CreateOrOpenSourcingCaseAsync(command));

        Assert.Equal(first.Id, replay.Id);
        Assert.Equal(first.CommercialDemandLineId, replay.CommercialDemandLineId);
        Assert.Equal("NXR-QA-0001", first.NexoraSerial);
        Assert.Equal(8m, first.UnfulfilledQuantity);
        Assert.Equal(2m, first.StockQuantity);
        Assert.Contains(first.Candidates, candidate => candidate.SupplierId == ProcurementTestData.Supplier
            && candidate.EvidenceType == SourcingCandidateEvidenceTypes.PreferredSupplier);

        await Assert.ThrowsAsync<ProcurementConflictException>(() => fixture.Execute(service =>
            service.CreateOrOpenSourcingCaseAsync(command with { SearchLimit = 20 })));

        await using var verify = fixture.Context();
        Assert.Single(await verify.CommercialDemandLines.ToListAsync());
        Assert.Single(await verify.SourcingCases.ToListAsync());
    }

    [Fact]
    public async Task Fully_covered_line_cannot_create_sourcing_case()
    {
        using var fixture = new ProcurementScenario();
        await MakeSourcingReadyAsync(fixture);
        await using (var setup = fixture.Context())
        {
            var inventory = await setup.Set<Models.Inventory>().SingleAsync(x => x.Id == ProcurementTestData.Inventory);
            inventory.QtyOnHand = 100m;
            await setup.SaveChangesAsync();
        }

        await Assert.ThrowsAsync<ProcurementConflictException>(() => fixture.Execute(service =>
            service.CreateOrOpenSourcingCaseAsync(CreateCase(fixture, "covered-line"))));

        await using var verify = fixture.Context();
        Assert.Empty(await verify.CommercialDemandLines.ToListAsync());
        Assert.Empty(await verify.SourcingCases.ToListAsync());
    }

    [Fact]
    public async Task Prior_supplier_quote_becomes_candidate_without_copying_commercial_price_into_evidence()
    {
        using var fixture = new ProcurementScenario();
        await MakeSourcingReadyAsync(fixture);
        const long historicalSupplierId = 97_100;
        await using (var setup = fixture.Context())
        {
            AgentSeed.Supplier(setup, historicalSupplierId, fixture.BusinessUnitId,
                "Historical Supplier", "history@example.test");
            setup.SupplierQuotedItems.Add(new SupplierQuotedItem
            {
                BusinessUnitId = fixture.BusinessUnitId,
                SupplierId = historicalSupplierId,
                ProductId = ProcurementTestData.Product,
                Quantity = 10m,
                UnitPrice = 123.45m,
                CurrencyId = ProcurementTestData.Currency,
                QuoteReference = "SQ-HISTORY",
                QuoteRevision = 1,
                CreatedBy = "qa",
                CreatedDate = DateTime.UtcNow.AddDays(-7),
                IsActive = true
            });
            await setup.SaveChangesAsync();
        }

        var created = await fixture.Execute(service => service.CreateOrOpenSourcingCaseAsync(
            CreateCase(fixture, "history-candidate")));
        var historical = Assert.Single(created.Candidates, x => x.SupplierId == historicalSupplierId);
        Assert.Equal(SourcingCandidateEvidenceTypes.PriorSupplierQuote, historical.EvidenceType);

        await using var verify = fixture.Context();
        var evidenceJson = await verify.SourcingCaseCandidates.Where(x => x.SupplierId == historicalSupplierId)
            .Select(x => x.EvidenceJson).SingleAsync();
        Assert.DoesNotContain("123.45", evidenceJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Candidate_search_honours_10_20_50_without_fabricating_results()
    {
        using var fixture = new ProcurementScenario();
        await MakeSourcingReadyAsync(fixture, additionalMetadataSuppliers: 14);
        var created = await fixture.Execute(service => service.CreateOrOpenSourcingCaseAsync(
            CreateCase(fixture, "candidate-limits")));

        Assert.Equal(10, created.Candidates.Count);
        var twenty = await fixture.Execute(service => service.SearchSourcingCandidatesAsync(new(
            fixture.BusinessUnitId, created.Id, 20, created.Version, "search-20", "qa", "corr-search-20")));
        Assert.Equal(15, twenty.ResultCount);
        Assert.Equal(15, twenty.Candidates.Select(x => x.SupplierId).Distinct().Count());

        var fifty = await fixture.Execute(service => service.SearchSourcingCandidatesAsync(new(
            fixture.BusinessUnitId, created.Id, 50, twenty.Version, "search-50", "qa", "corr-search-50")));
        var replay = await fixture.Execute(service => service.SearchSourcingCandidatesAsync(new(
            fixture.BusinessUnitId, created.Id, 50, twenty.Version, "search-50", "qa", "corr-search-50")));
        Assert.Equal(15, fifty.ResultCount);
        Assert.True(replay.Replayed);
        Assert.Equal(fifty.ResultCount, replay.ResultCount);

        var earlierReplay = await fixture.Execute(service => service.SearchSourcingCandidatesAsync(new(
            fixture.BusinessUnitId, created.Id, 20, created.Version, "search-20", "qa", "corr-search-20")));
        Assert.True(earlierReplay.Replayed);
        Assert.Equal(20, earlierReplay.RequestedLimit);
        Assert.Equal(twenty.Version, earlierReplay.Version);
        Assert.Equal(twenty.Candidates.Select(x => x.SupplierId), earlierReplay.Candidates.Select(x => x.SupplierId));

        await Assert.ThrowsAsync<ProcurementValidationException>(() => fixture.Execute(service =>
            service.SearchSourcingCandidatesAsync(new(fixture.BusinessUnitId, created.Id, 25,
                fifty.Version, "search-invalid", "qa", "corr-search-invalid"))));
    }

    [Fact]
    public async Task Sourcing_case_queries_and_mutations_do_not_cross_tenant_boundary()
    {
        using var fixture = new ProcurementScenario();
        await MakeSourcingReadyAsync(fixture);
        var created = await fixture.Execute(service => service.CreateOrOpenSourcingCaseAsync(
            CreateCase(fixture, "tenant-case")));

        await using var otherTenantContext = fixture.Context(fixture.OtherBusinessUnitId);
        var otherTenantService = new ProcurementApplicationService(otherTenantContext);
        await Assert.ThrowsAsync<ProcurementValidationException>(() =>
            otherTenantService.GetSourcingCaseAsync(fixture.OtherBusinessUnitId, created.Id));
        await Assert.ThrowsAsync<ProcurementValidationException>(() =>
            otherTenantService.SearchSourcingCandidatesAsync(new(fixture.OtherBusinessUnitId, created.Id,
                10, created.Version, "cross-tenant-search", "qa", "corr-cross-tenant")));
    }

    [Fact]
    public async Task Preparing_supplier_rfq_is_candidate_bound_idempotent_and_version_checked()
    {
        using var fixture = new ProcurementScenario();
        await MakeSourcingReadyAsync(fixture);
        var created = await fixture.Execute(service => service.CreateOrOpenSourcingCaseAsync(
            CreateCase(fixture, "prepare-case")));
        var candidate = Assert.Single(created.Candidates);
        var command = new PrepareSupplierRfqCommand(fixture.BusinessUnitId, created.Id, candidate.SupplierId,
            DateTime.UtcNow.AddDays(2), created.Version, "prepare-rfq", "qa", "corr-prepare-rfq");

        var first = await fixture.Execute(service => service.PrepareSupplierRfqAsync(command));
        var replay = await fixture.Execute(service => service.PrepareSupplierRfqAsync(command));
        Assert.False(first.Replayed);
        Assert.True(replay.Replayed);
        Assert.Equal(first.SupplierSolicitationId, replay.SupplierSolicitationId);
        Assert.Equal(SourcingCaseStatuses.OutreachReady,
            (await fixture.Execute(service => service.GetSourcingCaseAsync(fixture.BusinessUnitId, created.Id))).Status);

        await Assert.ThrowsAsync<ProcurementConflictException>(() => fixture.Execute(service =>
            service.PrepareSupplierRfqAsync(command with { DueOn = DateTime.UtcNow.AddDays(5) })));
        await Assert.ThrowsAsync<ProcurementValidationException>(() => fixture.Execute(service =>
            service.PrepareSupplierRfqAsync(command with
            {
                SupplierId = fixture.OtherTenantSupplierId,
                IdempotencyKey = "prepare-forged",
                ExpectedVersion = first.SourcingCaseVersion
            })));

        await using var verify = fixture.Context();
        Assert.Single(await verify.Set<ERP_RFQ_Automation.Agent.Models.SupplierSolicitation>().ToListAsync());
        Assert.Empty(await verify.ProcurementOutboxMessages.ToListAsync());
        Assert.True(await verify.SourcingCaseCandidates.Where(x => x.SourcingCaseId == created.Id)
            .Select(x => x.Selected).SingleAsync());
    }

    private static CreateSourcingCaseCommand CreateCase(ProcurementScenario fixture, string key) => new(
        fixture.BusinessUnitId, fixture.RfqId, fixture.RfqItemId, 10, false,
        key, "qa", $"corr-{key}");

    private static async Task MakeSourcingReadyAsync(ProcurementScenario fixture, int additionalMetadataSuppliers = 0)
    {
        await using var context = fixture.Context();
        var rfq = await context.Rfqs.SingleAsync(x => x.Id == fixture.RfqId);
        context.Entry(rfq).Property(x => x.NexoraSerial).CurrentValue = "NXR-QA-0001";
        var product = await context.Products.SingleAsync(x => x.Id == ProcurementTestData.Product);
        product.PreferredSupplierId = ProcurementTestData.Supplier;
        for (var index = 0; index < additionalMetadataSuppliers; index++)
        {
            var supplier = AgentSeed.Supplier(context, 97_000 + index, fixture.BusinessUnitId,
                $"Known Supplier {index + 1}", $"known-{index + 1}@example.test");
            supplier.Tags = index % 2 == 0 ? "QA-PART-0; electronics" : "manufacturer: QA Maker";
        }
        var line = await context.Rfqitems.SingleAsync(x => x.Id == fixture.RfqItemId);
        line.ManufacturerPartNumber = "QA-PART-0";
        line.ManufacturerName = "QA Maker";
        await context.SaveChangesAsync();
    }
}
