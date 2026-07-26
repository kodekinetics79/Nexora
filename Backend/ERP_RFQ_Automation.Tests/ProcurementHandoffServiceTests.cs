using ERP_RFQ_Automation.Agent.Models;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Procurement;
using ERP_RFQ_Automation.SupplierQuotes;
using ERP_RFQ_Automation.Tests.Support;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.Tests;

public sealed class ProcurementHandoffServiceTests
{
    [Fact]
    public async Task CreateAndSynchronize_PreserveLineageReplayAndExternalAuthority()
    {
        using var fixture = new CustomerAwardTestFixture();
        var orderLineId = await SeedSourcedCustomerOrderAsync(fixture);
        var service = new ProcurementHandoffService(fixture.Context);
        var command = new CreateProcurementHandoffCommand(orderLineId, "DROP_SHIP", null,
            "Customer ship-to address", new DateOnly(2026, 8, 15));

        var candidate = Assert.Single(await service.CandidatesAsync(fixture.BusinessUnitId));
        Assert.Equal(orderLineId, candidate.CustomerOrderLineId);

        var created = await service.CreateAsync(fixture.BusinessUnitId, "handoff-create",
            "corr-handoff-create", "tests", command);
        var replay = await service.CreateAsync(fixture.BusinessUnitId, "handoff-create",
            "corr-handoff-replay", "tests", command);
        var synchronization = new SynchronizeProcurementHandoffCommand(created.Version, "EXT-PO-7001", "10",
                created.RequiredQuantity, created.SelectedUnitCost, new DateOnly(2026, 8, 12),
                ProcurementHandoffStatuses.ExternalPoCreated,
                new DateTime(2026, 7, 26, 18, 0, 0, DateTimeKind.Utc));
        var synchronized = await service.SynchronizeAsync(fixture.BusinessUnitId, created.Id,
            "handoff-sync", "corr-handoff-sync", "tests", synchronization);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.SynchronizeAsync(
            fixture.BusinessUnitId, created.Id + 1, "handoff-sync", "corr-handoff-replay-target", "tests",
            synchronization));

        Assert.Equal(created.Id, replay.Id);
        Assert.Equal("EXT-PO-7001", synchronized.ExternalSupplierPoNumber);
        Assert.Equal("10", synchronized.ExternalSupplierPoLineNumber);
        Assert.False(synchronized.IsAuthoritative);
        Assert.Equal(ProcurementHandoffStatuses.Created, synchronized.Status);
        Assert.Equal(ProcurementHandoffStatuses.ExternalPoCreated, synchronized.ExternalStatus);
        Assert.Equal("MANUAL", synchronized.ExternalSystemTarget);
        Assert.Equal(2, synchronized.Version);
        Assert.Equal(2, await fixture.Context.ProcurementEvents.CountAsync(x =>
            x.AggregateType == "ProcurementHandoff" && x.AggregateId == created.Id));
        Assert.Empty(await fixture.Context.IncomingInventory.ToListAsync());
        Assert.Empty(await service.CandidatesAsync(fixture.BusinessUnitId));
    }

    [Fact]
    public async Task Synchronize_RejectsStaleVersionAndOtherTenantCannotRead()
    {
        using var fixture = new CustomerAwardTestFixture();
        var orderLineId = await SeedSourcedCustomerOrderAsync(fixture);
        var service = new ProcurementHandoffService(fixture.Context);
        var created = await service.CreateAsync(fixture.BusinessUnitId, "handoff-tenant",
            "corr-handoff-tenant", "tests", new(orderLineId, "DROP_SHIP", null, null, null));

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.SynchronizeAsync(
            fixture.BusinessUnitId, created.Id, "handoff-stale", "corr-handoff-stale", "tests",
            new(99, "EXT-PO-STALE", "1", 1m, 100m, new DateOnly(2026, 8, 12),
                ProcurementHandoffStatuses.ExternalPoCreated,
                new DateTime(2026, 7, 26, 18, 0, 0, DateTimeKind.Utc))));

        using var otherContext = fixture.Database.ContextFor(fixture.BusinessUnitId + 1);
        var otherService = new ProcurementHandoffService(otherContext);
        Assert.Empty(await otherService.SearchAsync(fixture.BusinessUnitId + 1, null, null, 20));
        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            otherService.GetAsync(fixture.BusinessUnitId + 1, created.Id));
    }

    [Fact]
    public async Task Create_RejectsPartialOrInactiveSourcingLineage()
    {
        using var partialFixture = new CustomerAwardTestFixture();
        var partialLineId = await SeedSourcedCustomerOrderAsync(partialFixture);
        var partialDecision = await partialFixture.Context.CustomerQuoteSourcingDecisions.SingleAsync();
        partialDecision.Quantity -= 1;
        await partialFixture.Context.SaveChangesAsync();
        var partialService = new ProcurementHandoffService(partialFixture.Context);
        await Assert.ThrowsAsync<ArgumentException>(() => partialService.CreateAsync(
            partialFixture.BusinessUnitId, "handoff-partial", "corr-handoff-partial", "tests",
            new(partialLineId, "DROP_SHIP", null, "Customer ship-to", null)));

        using var inactiveFixture = new CustomerAwardTestFixture();
        var inactiveLineId = await SeedSourcedCustomerOrderAsync(inactiveFixture);
        var quotedItem = await inactiveFixture.Context.SupplierQuotedItems.SingleAsync();
        quotedItem.IsActive = false;
        await inactiveFixture.Context.SaveChangesAsync();
        var inactiveService = new ProcurementHandoffService(inactiveFixture.Context);
        await Assert.ThrowsAsync<ArgumentException>(() => inactiveService.CreateAsync(
            inactiveFixture.BusinessUnitId, "handoff-inactive", "corr-handoff-inactive", "tests",
            new(inactiveLineId, "DROP_SHIP", null, "Customer ship-to", null)));
    }

    [Fact]
    public async Task Synchronize_RejectsSkippedStateAndCommercialVariance()
    {
        using var fixture = new CustomerAwardTestFixture();
        var orderLineId = await SeedSourcedCustomerOrderAsync(fixture);
        var service = new ProcurementHandoffService(fixture.Context);
        var created = await service.CreateAsync(fixture.BusinessUnitId, "handoff-governed-sync",
            "corr-handoff-governed-sync", "tests",
            new(orderLineId, "DROP_SHIP", null, "Customer ship-to", null));
        var synchronizedOn = DateTime.UtcNow.AddMinutes(-1);

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.SynchronizeAsync(
            fixture.BusinessUnitId, created.Id, "handoff-skip", "corr-handoff-skip", "tests",
            new(created.Version, "EXT-PO-STATE", "1", created.RequiredQuantity, created.SelectedUnitCost,
                new DateOnly(2026, 8, 12), ProcurementHandoffStatuses.Received, synchronizedOn)));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.SynchronizeAsync(
            fixture.BusinessUnitId, created.Id, "handoff-qty", "corr-handoff-qty", "tests",
            new(created.Version, "EXT-PO-QTY", "1", created.RequiredQuantity + 1, created.SelectedUnitCost,
                new DateOnly(2026, 8, 12), ProcurementHandoffStatuses.ExternalPoCreated, synchronizedOn)));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.SynchronizeAsync(
            fixture.BusinessUnitId, created.Id, "handoff-cost", "corr-handoff-cost", "tests",
            new(created.Version, "EXT-PO-COST", "1", created.RequiredQuantity, created.SelectedUnitCost + 1,
                new DateOnly(2026, 8, 12), ProcurementHandoffStatuses.ExternalPoCreated, synchronizedOn)));
    }

    private static async Task<long> SeedSourcedCustomerOrderAsync(CustomerAwardTestFixture fixture)
    {
        var purchaseOrder = await fixture.CreatePurchaseOrderAsync("handoff-source-po", 10m);
        var award = await fixture.CreateAwardAsync(purchaseOrder, "handoff-source-award", 10m);
        var confirmed = await fixture.Service.ConfirmAwardAsync(fixture.BusinessUnitId, award.Id,
            "handoff-source-confirm", "corr-handoff-source-confirm", new(award.Version), "tests");
        var order = await fixture.Service.ConvertToOrderAsync(fixture.BusinessUnitId, award.Id,
            "handoff-source-order", "corr-handoff-source-order", new(confirmed.Version), "tests");
        fixture.Context.ChangeTracker.Clear();
        var orderLineId = await fixture.Context.OrderItems.Where(x => x.OrderId == order.Id)
            .Select(x => x.Id).SingleAsync();
        var rfqItem = new Rfqitem
        {
            Id = 889_109, Rfqid = 880_010, ProductId = 880_004, LineItemNo = "1",
            ProductShortDescription = "Sourced handoff widget", Quantity = 10,
            CreatedBy = "tests", CreatedDate = DateTime.UtcNow
        };
        fixture.Context.Rfqitems.Add(rfqItem);
        AgentSeed.Supplier(fixture.Context, 889_100, fixture.BusinessUnitId,
            "Handoff Supplier", "handoff-supplier@example.test");
        fixture.Context.CommercialDemandLines.Add(new CommercialDemandLine
        {
            Id = 889_101, BusinessUnitId = fixture.BusinessUnitId, RfqId = 880_010,
            RfqItemId = rfqItem.Id, NexoraSerial = "NXR-HANDOFF-TEST",
            IdentityKey = "handoff-test-demand", CreatedOn = DateTime.UtcNow, CreatedBy = "tests"
        });
        fixture.Context.SourcingCases.Add(new SourcingCase
        {
            Id = 889_105, BusinessUnitId = fixture.BusinessUnitId, CommercialDemandLineId = 889_101,
            RfqId = 880_010, RfqItemId = rfqItem.Id, ProductId = 880_004,
            NexoraSerial = "NXR-HANDOFF-TEST", RequestedPartNumber = "PART-880004",
            Description = "Sourced handoff widget", RequestedQuantity = 10m, StockQuantity = 0m,
            UnfulfilledQuantity = 10m, SearchLimit = 10, Priority = "NORMAL",
            Status = SourcingCaseStatuses.SupplierSelected, NextAction = "Create procurement handoff",
            ShortageDecisionKey = new string('b', 64), IdempotencyKey = "handoff-case",
            RequestHash = new string('c', 64), Version = 1, CreatedOn = DateTime.UtcNow,
            CreatedBy = "tests", UpdatedOn = DateTime.UtcNow, UpdatedBy = "tests"
        });
        fixture.Context.Set<SupplierSolicitation>().Add(new SupplierSolicitation
        {
            Id = 889_110, BusinessUnitId = fixture.BusinessUnitId, RfqId = 880_010,
            SupplierId = 889_100, SourcingCaseId = 889_105, CommercialDemandLineId = 889_101,
            NexoraSerial = "NXR-HANDOFF-TEST", SupplierRfqNumber = "SRFQ-HANDOFF",
            IdempotencyKey = "handoff-solicitation", RequestHash = new string('d', 64),
            RequestedRfqItemIdsJson = "[889109]", Status = SolicitationStatus.Responded,
            SentOn = DateTime.UtcNow.AddDays(-2), RespondedOn = DateTime.UtcNow.AddDays(-1),
            Channel = "Email", CreatedOn = DateTime.UtcNow.AddDays(-2), UpdatedOn = DateTime.UtcNow.AddDays(-1)
        });
        var supplierQuote = new SupplierQuote
        {
            Id = 889_106, BusinessUnitId = fixture.BusinessUnitId, SupplierId = 889_100,
            SupplierSolicitationId = 889_110, SourcingCaseId = 889_105, RfqId = 880_010,
            NexoraSerial = "NXR-HANDOFF-TEST", SupplierQuoteReference = "SQ-HANDOFF-SOURCE",
            CurrentRevisionNumber = 1, InboxStatus = SupplierQuoteInboxStatuses.ReadyForComparison,
            Version = 1, CreatedOn = DateTime.UtcNow.AddDays(-1), CreatedBy = "tests",
            UpdatedOn = DateTime.UtcNow.AddDays(-1), UpdatedBy = "tests"
        };
        var revision = new SupplierQuoteRevision
        {
            Id = 889_107, BusinessUnitId = fixture.BusinessUnitId, RevisionNumber = 1,
            CaptureChannel = SupplierQuoteCaptureChannels.Manual, SourceIdentity = "handoff-source",
            SourceSha256 = new string('e', 64), CurrencyId = 880_003,
            ValidUntil = DateTime.UtcNow.AddDays(30), IdempotencyKey = "handoff-quote-revision",
            RequestHash = new string('f', 64), CapturedOn = DateTime.UtcNow.AddDays(-1),
            CapturedBy = "tests", CorrelationId = "corr-handoff-quote"
        };
        revision.Lines.Add(new SupplierQuoteLine
        {
            Id = 889_108, BusinessUnitId = fixture.BusinessUnitId, LineNumber = 1,
            RfqItemId = rfqItem.Id, CommercialDemandLineId = 889_101,
            PartNumber = "PART-880004", Description = "Sourced handoff widget",
            Quantity = 10m, AvailableQuantity = 10m, UnitOfMeasure = "EA", UnitPrice = 90m
        });
        supplierQuote.Revisions.Add(revision);
        fixture.Context.Set<SupplierQuote>().Add(supplierQuote);
        fixture.Context.SupplierQuotedItems.Add(new SupplierQuotedItem
        {
            Id = 889_102, BusinessUnitId = fixture.BusinessUnitId, RfqId = 880_010,
            RfqItemId = rfqItem.Id, CommercialDemandLineId = 889_101, SupplierId = 889_100,
            ProductId = fixture.QuoteItemId == 880_012 ? 880_004 : null,
            CurrencyId = 880_003, Quantity = 10m, UnitPrice = 90m, LandedUnitCost = 95m,
            QuoteReference = "SQ-HANDOFF", QuoteRevision = 1, ValidUntil = DateTime.UtcNow.AddDays(30),
            IsActive = true, CreatedBy = "tests", CreatedDate = DateTime.UtcNow
        });
        fixture.Context.Set<SourcingAward>().Add(new SourcingAward
        {
            Id = 889_103, BusinessUnitId = fixture.BusinessUnitId, RfqId = 880_010,
            RfqItemId = rfqItem.Id, SupplierId = 889_100, SupplierQuotedItemId = 889_102,
            CurrencyId = 880_003, Status = "APPROVED", UnitPrice = 90m, Quantity = 10m,
            TotalValue = 900m, LandedUnitCost = 95m, CreatedOn = DateTime.UtcNow
        });
        fixture.Context.CustomerQuoteSourcingDecisions.Add(new CustomerQuoteSourcingDecision
        {
            Id = 889_104, BusinessUnitId = fixture.BusinessUnitId, QuoteId = fixture.QuoteId,
            QuoteItemId = fixture.QuoteItemId, RfqId = 880_010, RfqItemId = rfqItem.Id,
            CommercialDemandLineId = 889_101, SourcingCaseId = 889_105, SourcingAwardId = 889_103,
            SupplierQuotedItemId = 889_102, SupplierQuoteId = 889_106,
            SupplierQuoteRevisionId = 889_107, SupplierQuoteLineId = 889_108,
            NexoraSerial = "NXR-HANDOFF-TEST", Quantity = 10m, SupplierLandedUnitCost = 95m,
            TargetMarginPercent = 20m, CustomerUnitPrice = 100m, CurrencyId = 880_003,
            IdempotencyKey = "handoff-pricing", RequestHash = new string('a', 64),
            Rationale = "Test sourcing decision", CreatedOn = DateTime.UtcNow,
            CreatedBy = "tests", CorrelationId = "corr-handoff-pricing"
        });
        await fixture.Context.SaveChangesAsync();
        return orderLineId;
    }
}
