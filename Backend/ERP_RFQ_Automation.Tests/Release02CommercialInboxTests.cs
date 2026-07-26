using System.Reflection;
using ERP_RFQ_Automation.Authorization;
using ERP_RFQ_Automation.CommercialDocuments;
using ERP_RFQ_Automation.Controllers;
using ERP_RFQ_Automation.DocumentIntelligence.Persistence;

namespace ERP_RFQ_Automation.Tests;

public sealed class Release02CommercialInboxTests
{
    private const string Hash = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    [Fact]
    public void Supplier_quote_projection_requires_review_and_commercial_matches()
    {
        var review = Row(CommercialDocumentType.Unknown, true);
        Assert.Equal(SupplierQuoteProjectionState.NotApplicable,
            CommercialDocumentClassificationService.ResolveSupplierQuoteProjection(review).State);

        review.Confirm(1, CommercialDocumentType.SupplierQuote, "{}", "reviewer@test",
            "Supplier quote confirmed");
        var missingRfq = CommercialDocumentClassificationService.ResolveSupplierQuoteProjection(review);
        Assert.False(missingRfq.IsReady);
        Assert.Equal(SupplierQuoteProjectionState.MissingSupplierRfqMatch, missingRfq.State);

        var ready = Row(CommercialDocumentType.SupplierQuote, false,
            new CommercialDocumentMatchReferences(SupplierRfqId: 31, SourcingCaseId: 41));
        var readiness = CommercialDocumentClassificationService.ResolveSupplierQuoteProjection(ready);
        Assert.True(readiness.IsReady);
        Assert.Equal(SupplierQuoteProjectionState.Ready, readiness.State);
        Assert.Empty(readiness.BlockingReasons);
    }

    [Fact]
    public void Supplier_quote_revision_requires_prior_quote_and_original_is_fenced_after_projection()
    {
        var revision = Row(CommercialDocumentType.SupplierQuoteRevision, false,
            new CommercialDocumentMatchReferences(SupplierRfqId: 31, SourcingCaseId: 41));
        Assert.Equal(SupplierQuoteProjectionState.MissingPriorSupplierQuoteMatch,
            CommercialDocumentClassificationService.ResolveSupplierQuoteProjection(revision).State);

        var readyRevision = Row(CommercialDocumentType.SupplierQuoteRevision, false,
            new CommercialDocumentMatchReferences(SupplierRfqId: 31, SourcingCaseId: 41, SupplierQuoteId: 51));
        Assert.True(CommercialDocumentClassificationService.ResolveSupplierQuoteProjection(readyRevision).IsReady);

        var projected = Row(CommercialDocumentType.SupplierQuote, false,
            new CommercialDocumentMatchReferences(SupplierRfqId: 31, SourcingCaseId: 41, SupplierQuoteId: 51));
        Assert.Equal(SupplierQuoteProjectionState.AlreadyProjected,
            CommercialDocumentClassificationService.ResolveSupplierQuoteProjection(projected).State);
    }

    [Theory]
    [InlineData(nameof(CommercialDocumentClassificationController.Search))]
    [InlineData(nameof(CommercialDocumentClassificationController.Detail))]
    public void Inbox_reads_require_supplier_history_view_permission(string actionName)
    {
        var action = typeof(CommercialDocumentClassificationController).GetMethod(actionName)!;
        var permission = Assert.Single(action.GetCustomAttributes<RequireModulePermissionAttribute>());
        Assert.Equal("Supplier History", permission.ModuleName);
        Assert.Equal(PermissionAction.View, permission.Action);
    }

    [Fact]
    public async Task Inbox_detail_exposes_evidence_and_readiness_without_storage_location()
    {
        var row = Row(CommercialDocumentType.SupplierQuote, false,
            new CommercialDocumentMatchReferences(SupplierRfqId: 31, SourcingCaseId: 41));
        var store = new InboxStore(7, row);
        var service = new CommercialDocumentClassificationService(store,
            new DeterministicCommercialDocumentClassifier());

        var detail = await service.GetInboxDetailAsync(7, row.Id);

        Assert.Equal("supplier-quote.pdf", detail.Item.OriginalFileName);
        Assert.Equal(DocumentSecurityStatus.Cleared, detail.Item.SecurityStatus);
        Assert.Equal(SupplierQuoteProjectionState.Ready, detail.Item.SupplierQuoteProjection.State);
        Assert.Equal(Hash, detail.SourceDocumentContentHash);
        Assert.Equal("{}", detail.EvidenceJson);
        Assert.DoesNotContain("bucket", detail.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("object key", detail.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Inbox_queries_validate_bounds_and_fence_tenant_before_reading()
    {
        var store = new InboxStore(7, Row(CommercialDocumentType.SupplierQuote, false));
        var service = new CommercialDocumentClassificationService(store,
            new DeterministicCommercialDocumentClassifier());

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            service.SearchInboxAsync(7, new CommercialDocumentInboxQuery(Page: 0)));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            service.SearchInboxAsync(7, new CommercialDocumentInboxQuery(PageSize: 101)));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            service.SearchInboxAsync(8, new CommercialDocumentInboxQuery()));
        Assert.Equal(0, store.SearchCount);
    }

    [Fact]
    public async Task Classification_rejects_cross_tenant_match_before_copying_client_reference()
    {
        var store = new InboxStore(7, Row(CommercialDocumentType.Unknown, true))
        {
            InvalidReferences = [nameof(CommercialDocumentMatchReferences.SupplierRfqId)]
        };
        var service = new CommercialDocumentClassificationService(store,
            new DeterministicCommercialDocumentClassifier());

        var error = await Assert.ThrowsAsync<CommercialDocumentMatchValidationException>(() =>
            service.ClassifyAsync(7, new ClassifyCommercialDocumentRequest(91, "cross-tenant-reference",
                new CommercialDocumentClassificationSignals("quote.pdf", "Quote", "Supplier"),
                new CommercialDocumentMatchReferences(SupplierRfqId: 8002))));

        Assert.Contains(nameof(CommercialDocumentMatchReferences.SupplierRfqId), error.Message);
        Assert.Equal(0, store.AddCount);
    }

    [Fact]
    public async Task Confirmation_rejects_cross_tenant_or_unsupported_matches_without_mutating_review()
    {
        var row = Row(CommercialDocumentType.Unknown, true);
        var store = new InboxStore(7, row)
        {
            InvalidReferences = [nameof(CommercialDocumentMatchReferences.SupplierInvoiceId)]
        };
        var service = new CommercialDocumentClassificationService(store,
            new DeterministicCommercialDocumentClassifier());

        await Assert.ThrowsAsync<CommercialDocumentMatchValidationException>(() => service.ConfirmAsync(7,
            row.Id, 1, CommercialDocumentType.SupplierInvoice, "{}", "reviewer@test", "Invoice review",
            new CommercialDocumentMatchReferences(SupplierInvoiceId: 901)));

        Assert.Equal(CommercialDocumentReviewStatus.ReviewRequired, row.ReviewStatus);
        Assert.Null(row.SupplierInvoiceId);
        Assert.Equal(1, row.Version);
    }

    private static CommercialDocumentClassification Row(CommercialDocumentType type, bool requiresReview,
        CommercialDocumentMatchReferences? matches = null)
    {
        var decision = new CommercialDocumentDecision(type, requiresReview ? 0m : .95m,
            CommercialDocumentClassificationMethods.LocalDeterministicV1, "{}", requiresReview);
        return CommercialDocumentClassification.Create(7, Random.Shared.NextInt64(1, long.MaxValue), Hash,
            "v1", Guid.NewGuid().ToString("N"), Hash, decision, matches);
    }

    private sealed class InboxStore(long tenantId, params CommercialDocumentClassification[] rows)
        : ICommercialDocumentClassificationStore
    {
        public long? ScopedTenantId { get; } = tenantId;
        public int SearchCount { get; private set; }
        public int AddCount { get; private set; }
        public IReadOnlyList<string> InvalidReferences { get; init; } = [];

        public Task<CommercialDocumentInboxRecord?> FindInboxAsync(long businessUnitId, Guid id,
            CancellationToken cancellationToken)
        {
            var row = rows.SingleOrDefault(candidate => candidate.BusinessUnitId == businessUnitId &&
                                                        candidate.Id == id);
            return Task.FromResult(row is null ? null : Record(row));
        }

        public Task<(IReadOnlyList<CommercialDocumentInboxRecord> Rows, int TotalCount)> SearchInboxAsync(
            long businessUnitId, CommercialDocumentInboxQuery query, CancellationToken cancellationToken)
        {
            SearchCount++;
            var matches = rows.Where(row => row.BusinessUnitId == businessUnitId).Select(Record).ToArray();
            return Task.FromResult(((IReadOnlyList<CommercialDocumentInboxRecord>)matches, matches.Length));
        }

        public Task<IReadOnlyList<string>> FindInvalidMatchReferencesAsync(long businessUnitId,
            CommercialDocumentMatchReferences matches, CancellationToken cancellationToken) =>
            Task.FromResult(InvalidReferences);

        public Task<CommercialDocumentClassification?> FindByIdempotencyKeyAsync(long businessUnitId,
            string idempotencyKey, CancellationToken cancellationToken) => Task.FromResult<CommercialDocumentClassification?>(null);

        public Task<CommercialSourceDocumentIdentity?> FindSourceDocumentAsync(long businessUnitId,
            long sourceDocumentId, CancellationToken cancellationToken) => Task.FromResult<CommercialSourceDocumentIdentity?>(null);

        public Task<CommercialDocumentClassification?> FindBySourceDocumentAsync(long businessUnitId,
            long sourceDocumentId, CancellationToken cancellationToken) => Task.FromResult<CommercialDocumentClassification?>(null);

        public Task<CommercialDocumentClassification?> FindAsync(long businessUnitId, Guid id,
            CancellationToken cancellationToken) => Task.FromResult(rows.SingleOrDefault(row =>
                row.BusinessUnitId == businessUnitId && row.Id == id));

        public Task<CommercialDocumentClassification> AddAsync(CommercialDocumentClassification classification,
            CancellationToken cancellationToken)
        {
            AddCount++;
            return Task.FromResult(classification);
        }

        public Task SaveAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        private static CommercialDocumentInboxRecord Record(CommercialDocumentClassification row) =>
            new(row, "supplier-quote.pdf", "application/pdf", DocumentSecurityStatus.Cleared,
                DocumentProcessingStatus.Completed);
    }
}
