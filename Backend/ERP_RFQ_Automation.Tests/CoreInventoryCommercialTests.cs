using ERP_RFQ_Automation.Inventory.Commercial;

namespace ERP_RFQ_Automation.Tests;

public sealed class CoreInventoryCommercialTests
{
    [Fact]
    public void Atp_deducts_every_unavailable_and_protected_bucket()
    {
        var snapshot = Snapshot(1, onHand: 100, reserved: 10, allocated: 5,
            quarantine: 7, damaged: 3, expired: 4, safety: 11);

        Assert.Equal(60m, snapshot.AvailableToPromise);
    }

    [Fact]
    public void Atp_never_reports_negative_stock()
    {
        Assert.Equal(0m, Snapshot(1, onHand: 5, reserved: 10).AvailableToPromise);
    }

    [Fact]
    public void Atp_rejects_invalid_negative_buckets()
    {
        Assert.Throws<InvalidOperationException>(() => Snapshot(1, onHand: 10, damaged: -1).AvailableToPromise);
    }

    [Fact]
    public void Fulfilment_uses_multiple_warehouses_when_one_cannot_cover_demand()
    {
        var result = new FulfilmentRouteService().Classify(70m,
            [Snapshot(1, onHand: 40), Snapshot(2, onHand: 35)]);

        Assert.Equal(FulfilmentRouteClassification.MultipleWarehouses, result.Classification);
        Assert.Equal(70m, result.AllocatedQuantity);
        Assert.Equal(0m, result.ShortageQuantity);
        Assert.Equal(2, result.Allocations.Count);
    }

    [Fact]
    public void Fulfilment_reports_truthful_partial_shortage()
    {
        var result = new FulfilmentRouteService().Classify(90m,
            [Snapshot(1, onHand: 40), Snapshot(2, onHand: 35, safety: 5)]);

        Assert.Equal(FulfilmentRouteClassification.PartialStock, result.Classification);
        Assert.Equal(70m, result.AllocatedQuantity);
        Assert.Equal(20m, result.ShortageQuantity);
    }

    [Fact]
    public void Identity_resolution_follows_tenant_scoped_supersession_chain()
    {
        var alias = Alias(tenant: 8, product: 10, "OLD-100");
        var supersessions = new[]
        {
            Supersession(8, 10, 11),
            Supersession(8, 11, 12),
            Supersession(9, 12, 99),
        };

        var result = new ProductIdentityResolver().Resolve(8, "old 100", [alias], supersessions,
            new DateOnly(2026, 7, 25));

        Assert.Equal(12, result.ProductId);
        Assert.Equal([10L, 11L, 12L], result.SupersessionPath);
        Assert.True(result.IsSuperseded);
    }

    [Fact]
    public void Ambiguous_alias_requires_possible_match_review()
    {
        var result = new ProductIdentityResolver().Resolve(8, "ABC-1",
            [Alias(8, 10, "ABC-1"), Alias(8, 11, "ABC-1")], [], new DateOnly(2026, 7, 25));

        Assert.Null(result.ProductId);
        Assert.Equal("PossibleMatchReview", result.Method);
    }

    [Fact]
    public async Task Movement_ledger_is_idempotent_but_rejects_key_reuse_for_different_fact()
    {
        var ledger = new InMemoryInventoryMovementLedger();
        var movement = Movement("receipt:1", 20m);

        Assert.Equal(movement, await ledger.AppendAsync(movement));
        Assert.Equal(movement, await ledger.AppendAsync(movement));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ledger.AppendAsync(Movement("receipt:1", 21m)));
        Assert.Single(await ledger.ReadAsync(1, 100));
    }

    [Theory]
    [InlineData(10)]
    [InlineData(20)]
    [InlineData(50)]
    public async Task Local_resource_search_supports_only_governed_result_sizes(int limit)
    {
        var repo = new RecordingResourceRepository(Enumerable.Range(1, 60)
            .Select(i => Resource(i, tenant: 1)).ToArray());
        var search = new LocalRelatedResourceSearch(repo);

        var results = await search.SearchAsync(new(1, "pn-100", 7, limit));

        Assert.Equal(limit, results.Count);
        Assert.Equal("PN100", repo.LastNormalizedPartNumber);
        Assert.All(results, x => Assert.Equal(1, x.BusinessUnitId));
    }

    [Fact]
    public async Task Local_resource_search_rejects_arbitrary_limit_and_cross_tenant_results()
    {
        var search = new LocalRelatedResourceSearch(new RecordingResourceRepository(
            [Resource(1, 2), Resource(2, 1)]));

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            search.SearchAsync(new(1, "PN", null, 25)));
        var results = await search.SearchAsync(new(1, "PN", null, 10));
        Assert.Single(results);
        Assert.Equal(1, results[0].BusinessUnitId);
    }

    [Fact]
    public async Task Known_product_with_shortage_and_incoming_stock_is_classified_as_incoming()
    {
        var service = new LeadLineCommercialResolutionService(
            new FulfilmentRouteService(),
            new LocalRelatedResourceSearch(new RecordingResourceRepository([])));
        var request = Request(productId: 7, inventory: [Snapshot(1, onHand: 5)], incoming:
        [
            new IncomingInventory
            {
                BusinessUnitId = 1, ProductId = 7, WarehouseId = 1,
                OrderedQuantity = 20, ReceivedQuantity = 2, AllocatedQuantity = 3,
                ExpectedOn = new DateOnly(2026, 8, 1), Status = IncomingInventoryStatus.Confirmed,
                SourceType = "PurchaseOrder", SourceId = "PO-1",
            },
        ]);

        var result = await service.ResolveAsync(request);

        Assert.Equal(CommercialResolutionClassification.KnownIncoming, result.Classification);
        Assert.Equal(15m, result.IncomingAvailable);
        Assert.Equal(5m, result.AvailableToPromise);
        Assert.Empty(result.RelatedResources);
    }

    [Fact]
    public async Task Unknown_product_uses_local_evidence_without_inventing_suppliers()
    {
        var evidence = Resource(1, tenant: 1);
        var service = new LeadLineCommercialResolutionService(
            new FulfilmentRouteService(),
            new LocalRelatedResourceSearch(new RecordingResourceRepository([evidence])));

        var result = await service.ResolveAsync(Request(productId: null, inventory: [], incoming: []));

        Assert.Equal(CommercialResolutionClassification.UnknownProduct, result.Classification);
        Assert.Equal([evidence], result.RelatedResources);
    }

    [Fact]
    public async Task Resolution_rejects_evidence_for_another_product()
    {
        var service = new LeadLineCommercialResolutionService(
            new FulfilmentRouteService(),
            new LocalRelatedResourceSearch(new RecordingResourceRepository([])));
        var wrongProduct = Snapshot(1, onHand: 10) with { ProductId = 99 };

        await Assert.ThrowsAsync<ArgumentException>(() =>
            service.ResolveAsync(Request(productId: 7, inventory: [wrongProduct], incoming: [])));
    }

    private static InventorySnapshot Snapshot(long warehouse, decimal onHand, decimal reserved = 0,
        decimal allocated = 0, decimal quarantine = 0, decimal damaged = 0, decimal expired = 0,
        decimal safety = 0) => new()
        {
            BusinessUnitId = 1, ProductId = 7, InventoryId = warehouse * 10,
            WarehouseId = warehouse, WarehouseCode = $"WH-{warehouse}", OnHand = onHand,
            Reserved = reserved, Allocated = allocated, Quarantine = quarantine, Damaged = damaged,
            Expired = expired, SafetyStock = safety, AsOf = DateTime.UtcNow,
        };

    private static ProductAlias Alias(long tenant, long product, string value) => new()
    {
        BusinessUnitId = tenant, ProductId = product, Kind = ProductAliasKind.LegacyPartNumber,
        Value = value, NormalizedValue = CommercialInventoryNormalization.PartNumber(value),
        CreatedBy = "test", CreatedOn = DateTime.UtcNow,
    };

    private static ProductSupersession Supersession(long tenant, long oldProduct, long replacement) => new()
    {
        BusinessUnitId = tenant, SupersededProductId = oldProduct, ReplacementProductId = replacement,
        Kind = ProductSupersessionKind.DirectReplacement, EffectiveOn = new DateOnly(2026, 1, 1),
        CreatedBy = "test", CreatedOn = DateTime.UtcNow,
    };

    private static InventoryMovement Movement(string key, decimal quantity) => new()
    {
        BusinessUnitId = 1, ProductId = 7, InventoryId = 100, WarehouseId = 1,
        Type = InventoryMovementType.Receipt, Quantity = quantity, OccurredOn = DateTime.UtcNow,
        IdempotencyKey = key, SourceType = "Receipt", SourceId = "GRN-1",
        CreatedBy = "test", CreatedOn = DateTime.UtcNow,
    };

    private static RelatedResource Resource(int id, long tenant) => new()
    {
        ResourceId = $"resource:{id}", BusinessUnitId = tenant,
        Kind = RelatedResourceKind.SupplierQuoteHistory, ProductId = 7, SupplierId = id,
        DisplayName = $"Persisted resource {id}", MatchReason = "Exact local part-number evidence",
        Score = 100m - id, EvidenceReference = $"history:{id}",
    };

    private static CommercialResolutionRequest Request(long? productId,
        IReadOnlyCollection<InventorySnapshot> inventory,
        IReadOnlyCollection<IncomingInventory> incoming) =>
        new(1, 20, 21, 22, "PN-100", 10m, productId, false, inventory, incoming);

    private sealed class RecordingResourceRepository(IReadOnlyList<RelatedResource> resources)
        : ILocalRelatedResourceRepository
    {
        public string? LastNormalizedPartNumber { get; private set; }

        public Task<IReadOnlyList<RelatedResource>> SearchAsync(long businessUnitId,
            string normalizedPartNumber, long? productId, CancellationToken ct = default)
        {
            LastNormalizedPartNumber = normalizedPartNumber;
            return Task.FromResult(resources);
        }
    }
}
