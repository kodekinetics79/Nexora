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
        await SeedRevisionAsync(database, 1, 100, "KNOWN-1", 4, 901);
        await using var context = database.ContextFor(1);
        var service = Service(context, ProductResolutionDecisionState.AutoLinked, 901);

        var first = await service.ResolveLeadAsync(1, 100, 10, forceRefresh: false);
        var expanded = await service.ResolveLeadAsync(1, 100, 50, forceRefresh: false);
        var replay = await service.ResolveLeadAsync(1, 100, 50, forceRefresh: false);

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
        await SeedRevisionAsync(database, 1, 103, "KNOWN-2", 4, 904);
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
    public async Task LinkRfq_attaches_resolution_to_the_exact_persisted_rfq_line()
    {
        using var database = new TestDb();
        await SeedRevisionAsync(database, 1, 104, "KNOWN-3", 4, 905);
        await using var context = database.ContextFor(1);
        var service = Service(context, ProductResolutionDecisionState.AutoLinked, 905);
        var resolution = Assert.Single(await service.ResolveLeadAsync(1, 104, 10));
        context.Rfqs.Add(new ERP_RFQ_Automation.Models.Rfq
        {
            Id = 504, LeadId = 104, BusinessUnitId = 1, Rfqno = "RFQ-504",
            BuyersName = "Module 4 buyer", RecDate = DateTime.UtcNow,
            CreatedBy = "test", CreatedDate = DateTime.UtcNow,
        });
        context.Rfqitems.Add(new ERP_RFQ_Automation.Models.Rfqitem
        {
            Id = 505, Rfqid = 504, ProductId = 905, LineItemNo = "1",
            ManufacturerPartNumber = "KNOWN-3", ProductShortName = "Known product",
            Quantity = 4, CreatedBy = "test", CreatedDate = DateTime.UtcNow,
        });
        await context.SaveChangesAsync();

        await service.LinkRfqAsync(1, 104, 504);

        var linked = await context.Set<LeadLineCommercialResolution>()
            .AsNoTracking().SingleAsync(x => x.Id == resolution.Id);
        Assert.Equal(504, linked.RfqId);
        Assert.Equal(505, linked.RfqItemId);
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

    [Fact]
    public async Task A_line_whose_quantity_the_document_did_not_state_still_resolves()
    {
        // PRODUCTION, LEAD 470. An Aramco bid line carried no quantity, so the extractor wrote
        // "quantity": null — exactly what it is instructed to do rather than invent a number.
        // ParseSnapshot called JsonElement.TryGetDecimal on it, which THROWS for a non-number
        // element instead of returning false, ResolveLead mapped that InvalidOperationException
        // to 409, and the lead screen rendered the raw serializer sentence:
        //
        //   "The requested operation requires an element of type 'Number', but the target
        //    element has type 'Null'."
        //
        // One unquantified line took out supplier resolution for the WHOLE lead — the step the
        // product exists to perform.
        using var database = new TestDb();
        await SeedRevisionWithSnapshotAsync(database, 1, 480,
            """{"part":"TUBING-METALLIC-316L","quantity":null}""");

        await using var context = database.ContextFor(null);
        var service = Service(context, ProductResolutionDecisionState.Unresolved, 0);

        var row = Assert.Single(await service.ResolveLeadAsync(1, 480, 10));

        Assert.Equal("TUBING-METALLIC-316L", row.RequestedPartNumber);
        // "Not stated" becomes one, never zero: a quantity of zero cannot be sourced, and the
        // caller's `> 0m ? quantity : 1m` fallback is what makes the line reviewable.
        Assert.Equal(1m, row.RequestedQuantity);
    }

    [Theory]
    [InlineData("null")]          // the observed case
    [InlineData("\"200\"")]       // a quantity the sender wrote as text
    [InlineData("true")]          // nonsense, but it must not take the lead down
    [InlineData("[]")]
    [InlineData("{}")]
    public async Task No_shape_of_quantity_can_take_down_the_whole_lead(string quantityJson)
    {
        // The guard is on ValueKind, so it holds for every non-number shape rather than only the
        // one that was reported. A malformed quantity is a line-level data problem; it must never
        // become a lead-level outage.
        using var database = new TestDb();
        await SeedRevisionWithSnapshotAsync(database, 1, 481,
            $$"""{"part":"VALVE-BALL-2IN","quantity":{{quantityJson}}}""");

        await using var context = database.ContextFor(null);
        var service = Service(context, ProductResolutionDecisionState.Unresolved, 0);

        var row = Assert.Single(await service.ResolveLeadAsync(1, 481, 10));
        Assert.Equal(1m, row.RequestedQuantity);
    }

    [Fact]
    public async Task A_real_quantity_is_still_read()
    {
        // The control. Guarding the read must not stop it working.
        using var database = new TestDb();
        await SeedRevisionWithSnapshotAsync(database, 1, 482,
            """{"part":"CABLE-TRAY-300MM","quantity":40}""");

        await using var context = database.ContextFor(null);
        var service = Service(context, ProductResolutionDecisionState.Unresolved, 0);

        Assert.Equal(40m, Assert.Single(await service.ResolveLeadAsync(1, 482, 10)).RequestedQuantity);
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

    [Fact]
    public async Task A_line_identified_only_by_a_long_description_still_resolves()
    {
        // PRODUCTION, LEAD 466 — the Siemens turbine spares bid. Not one of its 21 lines states a
        // part number or a material code, so the identifying key falls through to the DESCRIPTION,
        // and those descriptions run to several hundred characters. RequestedPartNumber is
        // varchar(200), so Postgres refused the insert with
        //
        //   22001: value too long for type character varying(200)
        //
        // EF raised DbUpdateException, which ResolveLead does not map, so it escaped as a 500 and
        // the screen said "An unexpected error occurred."
        //
        // The richer the bid, the more certain the failure — which is exactly backwards.
        var description = "KEY:SHAFT,SQUARE,10 MM X 10 MM LG,X22CRMOV12-1 ADDITIONAL DATA: ITEM "
            + "ADDITIONAL DESCRIPTION #MBRTM10A00-A01,0002,SYSTEM PARENT EQUIPMENT INFORMATION FOR "
            + "SIEMENS GAS TURBINE MODEL # V94.2 (SGT5-2000E); SIEMENS (LM/FH): FYNMPGDATE, "
            + "STANDARD/SPECIFICATION: ASTM, ADDITIONAL DATA: ITEM ADDITIONAL DESCRIPTION";
        Assert.True(description.Length > 200, "the fixture must exceed the column width to be a test");

        using var database = new TestDb();
        await SeedRevisionWithSnapshotAsync(database, 1, 483,
            JsonSerializer.Serialize(new { description, quantity = 176m }));

        await using var context = database.ContextFor(null);
        var service = Service(context, ProductResolutionDecisionState.Unresolved, 0);

        var row = Assert.Single(await service.ResolveLeadAsync(1, 483, 10));

        Assert.Equal(176m, row.RequestedQuantity);
        Assert.True(row.RequestedPartNumber.Length <= 200);
        // Truncation is acceptable ONLY because this is a lookup key — the untouched description
        // stays on the line snapshot, which is what a reviewer actually reads. So the key must
        // still begin with the real identifying text rather than being blanked or hashed.
        Assert.StartsWith("KEY:SHAFT,SQUARE,10 MM X 10 MM LG", row.RequestedPartNumber);
    }

    /// <summary>
    /// Seeds one revision line with the snapshot JSON EXACTLY as given. The typed seeder below
    /// cannot express "quantity": null, and that is precisely the shape the defect needed.
    /// </summary>
    private static async Task SeedRevisionWithSnapshotAsync(
        TestDb database, long tenant, long leadId, string snapshotJson)
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
            LineFingerprint = new string('c', 64), SnapshotJson = snapshotJson });
        context.Add(revision);
        await context.SaveChangesAsync();
        lead.CurrentRevisionId = revision.Id; lead.CurrentRevisionNumber = 1;
        await context.SaveChangesAsync();
    }

    private static async Task SeedRevisionAsync(TestDb database, long tenant, long leadId, string part,
        decimal quantity, long? productId = null)
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
        if (productId.HasValue)
            context.Products.Add(new ERP_RFQ_Automation.Models.Product
            {
                Id = productId.Value, Buid = tenant, PartNo = part, ProductName = part,
                CreatedBy = "test", CreatedOn = DateTime.UtcNow, IsActive = true,
            });
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
