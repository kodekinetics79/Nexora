using System.Text.Json;
using ERP_RFQ_Automation.Inventory.Commercial;
using ERP_RFQ_Automation.LeadIdentity;
using ERP_RFQ_Automation.ProductIntelligence;
using ERP_RFQ_Automation.Tests.Support;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.Tests;

public sealed class CommercialLineResolutionApplicationServiceTests
{
    [Fact]
    public async Task ResolveLead_reuses_sufficient_snapshot_and_appends_for_a_larger_resource_limit()
    {
        using var database = new TestDb();
        await SeedRevisionAsync(database, 1, 100, "KNOWN-1", 4);
        await using var context = database.ContextFor(1);
        var service = Service(context, ProductResolutionDecisionState.AutoLinked, 901);

        var first = await service.ResolveLeadAsync(1, 100, 10);
        var expanded = await service.ResolveLeadAsync(1, 100, 50);
        var replay = await service.ResolveLeadAsync(1, 100, 50);

        Assert.Single(first);
        Assert.Single(expanded);
        Assert.Single(replay);
        Assert.NotEqual(first[0].Id, expanded[0].Id);
        Assert.Equal(expanded[0].Id, replay[0].Id);
        Assert.Equal(50, expanded[0].ResourceLimit);
        Assert.Equal(CommercialResolutionClassification.KnownShortage, first[0].Classification);
        Assert.Equal(2, await context.Set<LeadLineCommercialResolution>().CountAsync());
    }

    [Fact]
    public async Task ResolveLead_force_refresh_appends_an_immutable_inventory_snapshot()
    {
        using var database = new TestDb();
        await SeedRevisionAsync(database, 1, 103, "KNOWN-2", 4);
        await using var context = database.ContextFor(1);
        var service = Service(context, ProductResolutionDecisionState.AutoLinked, 904);

        var first = Assert.Single(await service.ResolveLeadAsync(1, 103, 10));
        await Task.Delay(2);
        var refreshed = Assert.Single(await service.ResolveLeadAsync(1, 103, 10, forceRefresh: true));

        Assert.NotEqual(first.Id, refreshed.Id);
        Assert.NotEqual(first.ResolutionBatchId, refreshed.ResolutionBatchId);
        Assert.True(refreshed.ResolvedOn > first.ResolvedOn);
        Assert.Equal(2, await context.Set<LeadLineCommercialResolution>().CountAsync());
    }

    [Fact]
    public async Task Ambiguous_product_is_persisted_for_review_without_inheriting_candidate_stock()
    {
        using var database = new TestDb();
        await SeedRevisionAsync(database, 1, 101, "MAYBE-1", 2);
        await using var context = database.ContextFor(1);
        var service = Service(context, ProductResolutionDecisionState.ReviewRequired, 902);

        var row = Assert.Single(await service.ResolveLeadAsync(1, 101, 20));

        Assert.Null(row.ProductId);
        Assert.Equal(CommercialResolutionClassification.PossibleMatchReview, row.Classification);
        Assert.Equal(0m, row.AvailableToPromise);
        Assert.Contains("ReviewRequired", row.ProductResolutionJson);
    }

    [Fact]
    public async Task Service_line_bypasses_product_and_inventory_resolution()
    {
        using var database = new TestDb();
        await SeedRevisionAsync(database, 1, 102, "FIELD-SERVICE", 1);
        await using var context = database.ContextFor(1);
        var service = Service(context, ProductResolutionDecisionState.AutoLinked, 903);

        var row = Assert.Single(await service.ResolveLeadAsync(1, 102, 10));

        Assert.Equal(CommercialResolutionClassification.NonInventoryService, row.Classification);
        Assert.Null(row.ProductId);
        Assert.Equal(0m, row.AvailableToPromise);
        Assert.Equal(0m, row.Fulfilment.ShortageQuantity);
        Assert.Contains("LocalDeterministicServiceClassification", row.ProductResolutionJson);
    }

    private static CommercialLineResolutionApplicationService Service(
        ERP_RFQ_Automation.Models.ErpRfqAutomationContext context,
        ProductResolutionDecisionState state, long candidateId)
    {
        var product = new StubProductResolver(state, candidateId);
        var local = new LocalRelatedResourceSearch(new EmptyResourceRepository());
        return new CommercialLineResolutionApplicationService(context, product,
            new LeadLineCommercialResolutionService(new FulfilmentRouteService(), local));
    }

    private static async Task SeedRevisionAsync(TestDb database, long tenant, long leadId, string part, decimal quantity)
    {
        await using var context = database.ContextFor(null);
        var lead = Seed.Lead(context, leadId, tenant);
        var batch = new LeadIngestionBatch { Id = Guid.NewGuid(), BusinessUnitId = tenant,
            SourceChannel = "Test", CreatedBy = "test", CreatedAtUtc = DateTimeOffset.UtcNow,
            UpdatedAtUtc = DateTimeOffset.UtcNow };
        var occurrence = new LeadIngestionOccurrence { BusinessUnitId = tenant, Batch = batch,
            SourceChannel = "Test", IdempotencyKey = $"occurrence-{leadId}",
            LogicalInquiryFingerprint = new string('a', 64), Classification = LeadOccurrenceClassification.New,
            ProcessingPath = LeadProcessingPath.Deterministic, IngestedAtUtc = DateTimeOffset.UtcNow,
            CreatedAtUtc = DateTimeOffset.UtcNow, ActorId = "test", CorrelationId = $"test-{leadId}" };
        var revision = new LeadRevision { BusinessUnitId = tenant, Lead = lead, RevisionNumber = 1,
            EstablishedByOccurrence = occurrence, LogicalInquiryFingerprint = new string('b', 64),
            SnapshotJson = "{}", CreatedAtUtc = DateTimeOffset.UtcNow, CreatedBy = "test",
            ProcessingPath = LeadProcessingPath.Deterministic };
        revision.Items.Add(new LeadItemRevision { BusinessUnitId = tenant, LineNumber = 1,
            LineFingerprint = new string('c', 64), SnapshotJson = JsonSerializer.Serialize(new { part, quantity }) });
        context.Add(revision);
        await context.SaveChangesAsync();
        lead.CurrentRevisionId = revision.Id; lead.CurrentRevisionNumber = 1;
        await context.SaveChangesAsync();
    }

    private sealed class StubProductResolver(ProductResolutionDecisionState state, long candidateId)
        : IProductItemResolver
    {
        public Task<ProductResolutionResult> ResolveAsync(ProductResolutionRequest request,
            CancellationToken cancellationToken = default)
        {
            var candidate = new RankedProductCandidate(candidateId, request.OriginalPartNumber ?? "UNKNOWN",
                null, request.OriginalManufacturer, request.Description, .75m,
                ProductResolutionMethods.LocalSimilarity, "Local candidate", request.Evidence);
            return Task.FromResult(new ProductResolutionResult(request.BusinessUnitId,
                request.SourceLeadRevisionId, request.SourceLeadItemRevisionId, request.OriginalPartNumber,
                request.OriginalPartNumber, request.OriginalManufacturer, request.OriginalManufacturer,
                [candidate], .75m, .1m, ProductResolutionMethods.LocalSimilarity, "test/v1",
                request.Evidence, state, state == ProductResolutionDecisionState.AutoLinked ? candidateId : null,
                state == ProductResolutionDecisionState.ReviewRequired, false));
        }
    }

    private sealed class EmptyResourceRepository : ILocalRelatedResourceRepository
    {
        public Task<IReadOnlyList<RelatedResource>> SearchAsync(long businessUnitId,
            string normalizedPartNumber, long? productId, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<RelatedResource>>([]);
    }
}
