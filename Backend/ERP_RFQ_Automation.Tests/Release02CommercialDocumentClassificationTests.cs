using ERP_RFQ_Automation.CommercialDocuments;
using ERP_RFQ_Automation.DocumentIntelligence.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;

namespace ERP_RFQ_Automation.Tests;

public sealed class Release02CommercialDocumentClassificationTests
{
    private const string Hash = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    [Fact]
    public void Vocabulary_is_locked_to_the_release_commercial_document_types()
    {
        Assert.Equal(new[]
        {
            "CustomerRfq", "CustomerRfqRevision", "SupplierQuote", "SupplierQuoteRevision",
            "CustomerQuoteResponse", "CustomerOrder", "SupplierConfirmation",
            "ReceiptOrDeliveryDocument", "CustomerRejection", "PurchaseOrder", "SupplierInvoice",
            "InventoryFile", "Unknown"
        }, Enum.GetNames<CommercialDocumentType>());
    }

    [Fact]
    public void Supplier_quote_requires_supplier_evidence_and_preserves_explainability()
    {
        var decision = new DeterministicCommercialDocumentClassifier().Classify(
            new CommercialDocumentClassificationSignals("quote.pdf", "Quotation No Q-19",
                "Supplier", "Unit price USD 4.20; quote validity 30 days", "supplier-rfq"),
            new CommercialDocumentMatchReferences(SupplierRfqId: 82));

        Assert.Equal(CommercialDocumentType.SupplierQuote, decision.DocumentType);
        Assert.False(decision.RequiresReview);
        Assert.Equal(CommercialDocumentClassificationMethods.LocalDeterministicV1, decision.Method);
        Assert.Contains("matched_supplier_rfq", decision.EvidenceJson);
        Assert.DoesNotContain("4.20", decision.EvidenceJson);
    }

    [Fact]
    public void Ambiguous_document_is_unknown_and_requires_review()
    {
        var decision = new DeterministicCommercialDocumentClassifier().Classify(
            new CommercialDocumentClassificationSignals("attachment.pdf", "Documents", null,
                "Please review the attached document."), new CommercialDocumentMatchReferences());

        Assert.Equal(CommercialDocumentType.Unknown, decision.DocumentType);
        Assert.True(decision.RequiresReview);
        Assert.Equal(0m, decision.Confidence);
    }

    [Fact]
    public void Classification_copies_immutable_source_identity_and_review_is_versioned()
    {
        var decision = new CommercialDocumentDecision(CommercialDocumentType.Unknown, 0m,
            CommercialDocumentClassificationMethods.LocalDeterministicV1, "{}", true);
        var row = CommercialDocumentClassification.Create(7, 11, Hash, "v3", "mail:1", Hash,
            decision, createdOn: DateTimeOffset.Parse("2026-07-26T10:00:00Z"));

        Assert.Equal(Hash, row.SourceDocumentContentHash);
        Assert.Equal("v3", row.SourceObjectVersion);
        Assert.Equal(CommercialDocumentReviewStatus.ReviewRequired, row.ReviewStatus);
        Assert.Throws<CommercialDocumentConflictException>(() => row.Confirm(2,
            CommercialDocumentType.SupplierQuote, "{}", "reviewer@test", "Matched supplier RFQ"));

        row.Confirm(1, CommercialDocumentType.SupplierQuote, "{\"rule\":\"reviewed_reference\"}",
            "reviewer@test", "Matched supplier RFQ", new CommercialDocumentMatchReferences(SupplierRfqId: 91));

        Assert.Equal(CommercialDocumentReviewStatus.Confirmed, row.ReviewStatus);
        Assert.Equal(2, row.Version);
        Assert.Equal(91, row.SupplierRfqId);
        Assert.Equal(CommercialDocumentClassificationMethods.HumanReview, row.ClassificationMethod);
        Assert.Throws<InvalidOperationException>(() => row.Reject(2, "reviewer@test", "Changed mind"));
    }

    [Fact]
    public void Model_has_tenant_qualified_source_link_idempotency_and_concurrency()
    {
        var entity = BuildModel().FindEntityType(typeof(CommercialDocumentClassification))!;
        var sourceForeignKey = Assert.Single(entity.GetForeignKeys());
        var indexes = entity.GetIndexes().ToDictionary(index => index.GetDatabaseName()!);

        Assert.Equal(new[] { nameof(CommercialDocumentClassification.BusinessUnitId),
                nameof(CommercialDocumentClassification.SourceDocumentId) },
            sourceForeignKey.Properties.Select(property => property.Name));
        Assert.Equal(typeof(SourceDocument), sourceForeignKey.PrincipalEntityType.ClrType);
        Assert.Equal(DeleteBehavior.Restrict, sourceForeignKey.DeleteBehavior);
        Assert.True(indexes["ux_commercial_document_classifications_tenant_document"].IsUnique);
        Assert.True(indexes["ux_commercial_document_classifications_tenant_idempotency"].IsUnique);
        Assert.True(entity.FindProperty(nameof(CommercialDocumentClassification.Version))!.IsConcurrencyToken);
        Assert.Equal("jsonb", entity.FindProperty(nameof(CommercialDocumentClassification.EvidenceJson))!.GetColumnType());
    }

    [Fact]
    public async Task Classification_is_idempotent_and_changed_replay_conflicts()
    {
        var store = new MemoryStore(71, new CommercialSourceDocumentIdentity(9, 71, Hash, "object-v1"));
        var service = new CommercialDocumentClassificationService(store, new DeterministicCommercialDocumentClassifier());
        var request = new ClassifyCommercialDocumentRequest(9, "message:44:attachment:1",
            new CommercialDocumentClassificationSignals("quote.pdf", "Quotation No Q-19",
                "Supplier", "Unit price and validity", "supplier-rfq"),
            new CommercialDocumentMatchReferences(SupplierRfqId: 82));

        var created = await service.ClassifyAsync(71, request);
        var replay = await service.ClassifyAsync(71, request);

        Assert.Same(created, replay);
        Assert.Single(store.Rows);
        await Assert.ThrowsAsync<CommercialDocumentConflictException>(() => service.ClassifyAsync(71,
            request with { Signals = request.Signals with { Subject = "Different quotation" } }));
    }

    [Fact]
    public async Task Cross_tenant_request_fails_before_store_access()
    {
        var store = new MemoryStore(71, new CommercialSourceDocumentIdentity(1, 71, Hash, "v1"));
        var service = new CommercialDocumentClassificationService(store,
            new DeterministicCommercialDocumentClassifier());

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.ClassifyAsync(72,
            new ClassifyCommercialDocumentRequest(1, "tenant-crossing",
                new CommercialDocumentClassificationSignals("rfq.pdf", "RFQ", "Customer"))));
        Assert.Equal(0, store.ReadCount);
    }

    [Fact]
    public async Task Successful_projection_links_canonical_Supplier_Quote_without_reclassifying_source()
    {
        var store = new MemoryStore(71, new CommercialSourceDocumentIdentity(9, 71, Hash, "object-v1"));
        var service = new CommercialDocumentClassificationService(store,
            new DeterministicCommercialDocumentClassifier());
        var row = await service.ClassifyAsync(71, new ClassifyCommercialDocumentRequest(9, "document:9",
            new CommercialDocumentClassificationSignals("supplier-quote.csv", "Supplier quotation",
                "Supplier", "Unit price and validity", "supplier-rfq"),
            new CommercialDocumentMatchReferences(SupplierRfqId: 82, SourcingCaseId: 83)));

        var linked = await service.LinkSupplierQuoteAsync(71, row.Id, row.Version, 91);

        Assert.Equal(91, linked.SupplierQuoteId);
        Assert.Equal(2, linked.Version);
        Assert.Equal(CommercialDocumentType.SupplierQuote, linked.DocumentType);
        Assert.Throws<CommercialDocumentConflictException>(() => linked.LinkSupplierQuote(2, 92));
    }

    private sealed class ModelContext(DbContextOptions<ModelContext> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.AddEvidenceLedger();
            modelBuilder.AddCommercialDocuments();
        }
    }

    private static IModel BuildModel()
    {
        var options = new DbContextOptionsBuilder<ModelContext>()
            .UseNpgsql("Host=localhost;Database=release02_model_only;Username=test;Password=test")
            .Options;
        using var context = new ModelContext(options);
        return context.GetService<IDesignTimeModel>().Model;
    }

    private sealed class MemoryStore(long? scopedTenantId, params CommercialSourceDocumentIdentity[] documents)
        : ICommercialDocumentClassificationStore
    {
        private readonly Dictionary<(long Tenant, long Id), CommercialSourceDocumentIdentity> _documents =
            documents.ToDictionary(document => (document.BusinessUnitId, document.Id));

        public long? ScopedTenantId { get; } = scopedTenantId;
        public List<CommercialDocumentClassification> Rows { get; } = [];
        public int ReadCount { get; private set; }

        public Task<CommercialDocumentClassification?> FindByIdempotencyKeyAsync(long businessUnitId,
            string idempotencyKey, CancellationToken cancellationToken)
        {
            ReadCount++;
            return Task.FromResult(Rows.SingleOrDefault(row => row.BusinessUnitId == businessUnitId &&
                                                               row.IdempotencyKey == idempotencyKey));
        }

        public Task<CommercialSourceDocumentIdentity?> FindSourceDocumentAsync(long businessUnitId,
            long sourceDocumentId, CancellationToken cancellationToken)
        {
            ReadCount++;
            _documents.TryGetValue((businessUnitId, sourceDocumentId), out var document);
            return Task.FromResult(document);
        }

        public Task<CommercialDocumentClassification?> FindBySourceDocumentAsync(long businessUnitId,
            long sourceDocumentId, CancellationToken cancellationToken)
        {
            ReadCount++;
            return Task.FromResult(Rows.SingleOrDefault(row => row.BusinessUnitId == businessUnitId &&
                                                               row.SourceDocumentId == sourceDocumentId));
        }

        public Task<CommercialDocumentClassification?> FindAsync(long businessUnitId, Guid id,
            CancellationToken cancellationToken)
        {
            ReadCount++;
            return Task.FromResult(Rows.SingleOrDefault(row => row.BusinessUnitId == businessUnitId && row.Id == id));
        }

        public Task<CommercialDocumentInboxRecord?> FindInboxAsync(long businessUnitId, Guid id,
            CancellationToken cancellationToken)
        {
            ReadCount++;
            var row = Rows.SingleOrDefault(row => row.BusinessUnitId == businessUnitId && row.Id == id);
            return Task.FromResult(row is null ? null : Inbox(row));
        }

        public Task<(IReadOnlyList<CommercialDocumentInboxRecord> Rows, int TotalCount)> SearchInboxAsync(
            long businessUnitId, CommercialDocumentInboxQuery query, CancellationToken cancellationToken)
        {
            ReadCount++;
            var rows = Rows.Where(row => row.BusinessUnitId == businessUnitId).Select(Inbox).ToArray();
            return Task.FromResult(((IReadOnlyList<CommercialDocumentInboxRecord>)rows, rows.Length));
        }

        public Task<IReadOnlyList<string>> FindInvalidMatchReferencesAsync(long businessUnitId,
            CommercialDocumentMatchReferences matches, CancellationToken cancellationToken)
        {
            ReadCount++;
            return Task.FromResult<IReadOnlyList<string>>([]);
        }

        private static CommercialDocumentInboxRecord Inbox(CommercialDocumentClassification row) =>
            new(row, "document.pdf", "application/pdf", DocumentSecurityStatus.Cleared,
                DocumentProcessingStatus.Completed);

        public Task<CommercialDocumentClassification> AddAsync(CommercialDocumentClassification classification,
            CancellationToken cancellationToken)
        {
            Rows.Add(classification);
            return Task.FromResult(classification);
        }

        public Task SaveAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
