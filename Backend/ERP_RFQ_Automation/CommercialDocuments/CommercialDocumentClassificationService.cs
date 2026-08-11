using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ERP_RFQ_Automation.Agent.Models;
using ERP_RFQ_Automation.DocumentIntelligence.Persistence;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.OrderToCash;
using ERP_RFQ_Automation.Procurement;
using ERP_RFQ_Automation.SupplierQuotes;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.CommercialDocuments;

public sealed record CommercialSourceDocumentIdentity(
    long Id,
    long BusinessUnitId,
    string ContentHash,
    string ObjectVersion);

public sealed record CommercialDocumentInboxRecord(
    CommercialDocumentClassification Classification,
    string OriginalFileName,
    string DetectedMimeType,
    DocumentSecurityStatus SecurityStatus,
    DocumentProcessingStatus ProcessingStatus);

public interface ICommercialDocumentClassificationStore
{
    long? ScopedTenantId { get; }
    Task<CommercialDocumentClassification?> FindByIdempotencyKeyAsync(long businessUnitId,
        string idempotencyKey, CancellationToken cancellationToken);
    Task<CommercialSourceDocumentIdentity?> FindSourceDocumentAsync(long businessUnitId,
        long sourceDocumentId, CancellationToken cancellationToken);
    Task<CommercialDocumentClassification?> FindBySourceDocumentAsync(long businessUnitId,
        long sourceDocumentId, CancellationToken cancellationToken);
    Task<CommercialDocumentClassification?> FindAsync(long businessUnitId, Guid id,
        CancellationToken cancellationToken);
    Task<CommercialDocumentInboxRecord?> FindInboxAsync(long businessUnitId, Guid id,
        CancellationToken cancellationToken);
    Task<(IReadOnlyList<CommercialDocumentInboxRecord> Rows, int TotalCount)> SearchInboxAsync(
        long businessUnitId, CommercialDocumentInboxQuery query, CancellationToken cancellationToken);
    Task<IReadOnlyList<string>> FindInvalidMatchReferencesAsync(long businessUnitId,
        CommercialDocumentMatchReferences matches, CancellationToken cancellationToken);
    Task<CommercialDocumentClassification> AddAsync(CommercialDocumentClassification classification,
        CancellationToken cancellationToken);
    Task SaveAsync(CancellationToken cancellationToken);
}

public sealed class EfCommercialDocumentClassificationStore(ErpRfqAutomationContext context)
    : ICommercialDocumentClassificationStore
{
    public long? ScopedTenantId => context.ScopedTenantId;

    public Task<CommercialDocumentClassification?> FindByIdempotencyKeyAsync(long businessUnitId,
        string idempotencyKey, CancellationToken cancellationToken) =>
        context.Set<CommercialDocumentClassification>().SingleOrDefaultAsync(row =>
            row.BusinessUnitId == businessUnitId && row.IdempotencyKey == idempotencyKey, cancellationToken);

    public async Task<CommercialSourceDocumentIdentity?> FindSourceDocumentAsync(long businessUnitId,
        long sourceDocumentId, CancellationToken cancellationToken) =>
        await context.Set<SourceDocument>().Where(document => document.BusinessUnitId == businessUnitId &&
                                                              document.Id == sourceDocumentId)
            .Select(document => new CommercialSourceDocumentIdentity(document.Id, document.BusinessUnitId,
                document.ContentHash, document.ObjectVersion))
            .SingleOrDefaultAsync(cancellationToken);

    public Task<CommercialDocumentClassification?> FindBySourceDocumentAsync(long businessUnitId,
        long sourceDocumentId, CancellationToken cancellationToken) =>
        context.Set<CommercialDocumentClassification>().SingleOrDefaultAsync(row =>
            row.BusinessUnitId == businessUnitId && row.SourceDocumentId == sourceDocumentId, cancellationToken);

    public Task<CommercialDocumentClassification?> FindAsync(long businessUnitId, Guid id,
        CancellationToken cancellationToken) =>
        context.Set<CommercialDocumentClassification>().SingleOrDefaultAsync(row =>
            row.BusinessUnitId == businessUnitId && row.Id == id, cancellationToken);

    public Task<CommercialDocumentInboxRecord?> FindInboxAsync(long businessUnitId, Guid id,
        CancellationToken cancellationToken) =>
        Project(ClassificationQuery(businessUnitId).Where(row => row.Id == id))
            .SingleOrDefaultAsync(cancellationToken);

    /// <summary>
    /// Pages the commercial-document inbox.
    ///
    /// <para><b>The defect this shape closes.</b> Filtering, ordering and paging used to be applied
    /// to the PROJECTED <c>CommercialDocumentInboxRecord</c>. EF Core can see through a constructor
    /// projection for a scalar predicate, but not for an ordering key reached through the entity the
    /// record holds: <c>OrderByDescending(row =&gt; row.Classification.UpdatedOn)</c> failed to
    /// translate and <c>GET /api/commercial-inbox/classifications</c> returned 500 on every call —
    /// including the unfiltered first page, which is what the inbox screen opens with.</para>
    ///
    /// <para>The fix keeps the whole query server-side rather than pulling it client-side with
    /// <c>AsEnumerable()</c>: predicate, ORDER BY and OFFSET/LIMIT are expressed against the
    /// classification entity, and the record projection — the part that needs the joined source
    /// document — is applied last, to the single page. Ordering an inbox in memory would have meant
    /// loading every classification in the tenant to return twenty-five rows.</para>
    /// </summary>
    public async Task<(IReadOnlyList<CommercialDocumentInboxRecord> Rows, int TotalCount)> SearchInboxAsync(
        long businessUnitId, CommercialDocumentInboxQuery query, CancellationToken cancellationToken)
    {
        var rows = ClassificationQuery(businessUnitId);
        if (query.DocumentType.HasValue)
            rows = rows.Where(row => row.DocumentType == query.DocumentType.Value);
        if (query.ReviewStatus.HasValue)
            rows = rows.Where(row => row.ReviewStatus == query.ReviewStatus.Value);
        if (query.ProjectionState.HasValue)
            rows = ApplyProjectionFilter(rows, query.ProjectionState.Value);

        var totalCount = await rows.CountAsync(cancellationToken);
        var page = await Project(rows.OrderByDescending(row => row.UpdatedOn)
                .ThenByDescending(row => row.CreatedOn)
                .Skip((query.Page - 1) * query.PageSize)
                .Take(query.PageSize))
            .ToListAsync(cancellationToken);
        return (page, totalCount);
    }

    public async Task<IReadOnlyList<string>> FindInvalidMatchReferencesAsync(long businessUnitId,
        CommercialDocumentMatchReferences matches, CancellationToken cancellationToken)
    {
        var invalid = new List<string>();
        if (matches.CustomerRfqId.HasValue && !await context.Set<Rfq>().AnyAsync(row =>
                row.BusinessUnitId == businessUnitId && row.Id == matches.CustomerRfqId.Value, cancellationToken))
            invalid.Add(nameof(matches.CustomerRfqId));
        if (matches.SupplierRfqId.HasValue && !await context.Set<SupplierSolicitation>().AnyAsync(row =>
                row.BusinessUnitId == businessUnitId && row.Id == matches.SupplierRfqId.Value, cancellationToken))
            invalid.Add(nameof(matches.SupplierRfqId));
        if (matches.SourcingCaseId.HasValue && !await context.Set<SourcingCase>().AnyAsync(row =>
                row.BusinessUnitId == businessUnitId && row.Id == matches.SourcingCaseId.Value, cancellationToken))
            invalid.Add(nameof(matches.SourcingCaseId));
        if (matches.SupplierQuoteId.HasValue && !await context.Set<SupplierQuote>().AnyAsync(row =>
                row.BusinessUnitId == businessUnitId && row.Id == matches.SupplierQuoteId.Value, cancellationToken))
            invalid.Add(nameof(matches.SupplierQuoteId));
        if (matches.PurchaseOrderId.HasValue && !await context.Set<CustomerPurchaseOrder>().AnyAsync(row =>
                row.BusinessUnitId == businessUnitId && row.Id == matches.PurchaseOrderId.Value, cancellationToken))
            invalid.Add(nameof(matches.PurchaseOrderId));

        // No authoritative Supplier Invoice aggregate exists yet. Never trust a client-provided placeholder ID.
        if (matches.SupplierInvoiceId.HasValue) invalid.Add(nameof(matches.SupplierInvoiceId));
        return invalid;
    }

    /// <summary>The tenant's classifications, unprojected — the shape every filter and ORDER BY is
    /// expressed against, so nothing has to translate through a constructor projection.</summary>
    private IQueryable<CommercialDocumentClassification> ClassificationQuery(long businessUnitId) =>
        context.Set<CommercialDocumentClassification>().AsNoTracking()
            .Where(row => row.BusinessUnitId == businessUnitId);

    /// <summary>Applied LAST, to an already ordered and paged query, so the join onto the source
    /// document is the only thing the projection adds.</summary>
    private static IQueryable<CommercialDocumentInboxRecord> Project(
        IQueryable<CommercialDocumentClassification> rows) =>
        rows.Select(row => new CommercialDocumentInboxRecord(row,
            row.SourceDocument.OriginalFileName,
            row.SourceDocument.DetectedMimeType,
            row.SourceDocument.SecurityStatus,
            row.SourceDocument.ProcessingStatus));

    private static IQueryable<CommercialDocumentClassification> ApplyProjectionFilter(
        IQueryable<CommercialDocumentClassification> rows, SupplierQuoteProjectionState state) => state switch
    {
        SupplierQuoteProjectionState.NotApplicable => rows.Where(row =>
            row.DocumentType != CommercialDocumentType.SupplierQuote &&
            row.DocumentType != CommercialDocumentType.SupplierQuoteRevision),
        SupplierQuoteProjectionState.ReviewRequired => rows.Where(row =>
            (row.DocumentType == CommercialDocumentType.SupplierQuote ||
             row.DocumentType == CommercialDocumentType.SupplierQuoteRevision) &&
            row.ReviewStatus == CommercialDocumentReviewStatus.ReviewRequired),
        SupplierQuoteProjectionState.Rejected => rows.Where(row =>
            (row.DocumentType == CommercialDocumentType.SupplierQuote ||
             row.DocumentType == CommercialDocumentType.SupplierQuoteRevision) &&
            row.ReviewStatus == CommercialDocumentReviewStatus.Rejected),
        SupplierQuoteProjectionState.MissingSupplierRfqMatch => Projectable(rows).Where(row =>
            row.SupplierRfqId == null),
        SupplierQuoteProjectionState.MissingSourcingCaseMatch => Projectable(rows).Where(row =>
            row.SupplierRfqId != null && row.SourcingCaseId == null),
        SupplierQuoteProjectionState.MissingPriorSupplierQuoteMatch => Projectable(rows).Where(row =>
            row.DocumentType == CommercialDocumentType.SupplierQuoteRevision &&
            row.SupplierRfqId != null && row.SourcingCaseId != null &&
            row.SupplierQuoteId == null),
        SupplierQuoteProjectionState.AlreadyProjected => Projectable(rows).Where(row =>
            row.DocumentType == CommercialDocumentType.SupplierQuote &&
            row.SupplierQuoteId != null),
        SupplierQuoteProjectionState.Ready => Projectable(rows).Where(row =>
            row.SupplierRfqId != null && row.SourcingCaseId != null &&
            ((row.DocumentType == CommercialDocumentType.SupplierQuote &&
              row.SupplierQuoteId == null) ||
             (row.DocumentType == CommercialDocumentType.SupplierQuoteRevision &&
              row.SupplierQuoteId != null))),
        _ => rows.Where(_ => false)
    };

    private static IQueryable<CommercialDocumentClassification> Projectable(
        IQueryable<CommercialDocumentClassification> rows) => rows.Where(row =>
        (row.DocumentType == CommercialDocumentType.SupplierQuote ||
         row.DocumentType == CommercialDocumentType.SupplierQuoteRevision) &&
        row.ReviewStatus != CommercialDocumentReviewStatus.ReviewRequired &&
        row.ReviewStatus != CommercialDocumentReviewStatus.Rejected);

    public async Task<CommercialDocumentClassification> AddAsync(
        CommercialDocumentClassification classification, CancellationToken cancellationToken)
    {
        context.Add(classification);
        try
        {
            await context.SaveChangesAsync(cancellationToken);
            return classification;
        }
        catch (DbUpdateException)
        {
            context.Entry(classification).State = EntityState.Detached;
            var concurrent = await context.Set<CommercialDocumentClassification>().AsNoTracking()
                .SingleOrDefaultAsync(row => row.BusinessUnitId == classification.BusinessUnitId &&
                                             row.IdempotencyKey == classification.IdempotencyKey,
                    cancellationToken);
            if (concurrent is not null && concurrent.RequestHash == classification.RequestHash) return concurrent;
            throw;
        }
    }

    public async Task SaveAsync(CancellationToken cancellationToken)
    {
        try { await context.SaveChangesAsync(cancellationToken); }
        catch (DbUpdateConcurrencyException exception)
        {
            throw new CommercialDocumentConflictException(
                $"The classification changed while the review was being saved: {exception.Message}");
        }
    }
}

public sealed class CommercialDocumentClassificationService
{
    private readonly ICommercialDocumentClassificationStore _store;
    private readonly ICommercialDocumentClassifier _classifier;

    public CommercialDocumentClassificationService(ErpRfqAutomationContext context,
        ICommercialDocumentClassifier classifier)
        : this(new EfCommercialDocumentClassificationStore(context), classifier) { }

    public CommercialDocumentClassificationService(ICommercialDocumentClassificationStore store,
        ICommercialDocumentClassifier classifier)
    {
        _store = store;
        _classifier = classifier;
    }

    public async Task<CommercialDocumentClassification> ClassifyAsync(long businessUnitId,
        ClassifyCommercialDocumentRequest request, CancellationToken cancellationToken = default)
    {
        EnsureTenant(businessUnitId);
        ArgumentNullException.ThrowIfNull(request);
        var idempotencyKey = Required(request.IdempotencyKey, 256, nameof(request.IdempotencyKey));
        var matches = request.Matches ?? new CommercialDocumentMatchReferences();
        await ValidateMatchesAsync(businessUnitId, matches, cancellationToken);
        var requestHash = ComputeRequestHash(request.SourceDocumentId, request.Signals, matches);
        var replay = await _store.FindByIdempotencyKeyAsync(businessUnitId, idempotencyKey, cancellationToken);
        if (replay is not null)
        {
            if (!CryptographicOperations.FixedTimeEquals(
                    Convert.FromHexString(replay.RequestHash), Convert.FromHexString(requestHash)))
                throw new CommercialDocumentConflictException(
                    "The idempotency key was already used for a different classification request.");
            return replay;
        }

        var sourceDocument = await _store.FindSourceDocumentAsync(businessUnitId,
            request.SourceDocumentId, cancellationToken)
            ?? throw new CommercialDocumentNotFoundException("The source document does not exist in this tenant.");
        if (await _store.FindBySourceDocumentAsync(businessUnitId, request.SourceDocumentId, cancellationToken) is not null)
            throw new CommercialDocumentConflictException("The source document already has a classification record.");

        var decision = _classifier.Classify(request.Signals, matches);
        var classification = CommercialDocumentClassification.Create(businessUnitId, sourceDocument.Id,
            sourceDocument.ContentHash, sourceDocument.ObjectVersion, idempotencyKey, requestHash, decision, matches);
        return await _store.AddAsync(classification, cancellationToken);
    }

    public async Task<CommercialDocumentClassification> ConfirmAsync(long businessUnitId, Guid id,
        int expectedVersion, CommercialDocumentType documentType, string evidenceJson, string actor,
        string reason, CommercialDocumentMatchReferences? matches = null,
        CancellationToken cancellationToken = default)
    {
        EnsureTenant(businessUnitId);
        var row = await FindAsync(businessUnitId, id, cancellationToken);
        var verifiedMatches = matches ?? MatchReferences(row);
        await ValidateMatchesAsync(businessUnitId, verifiedMatches, cancellationToken);
        row.Confirm(expectedVersion, documentType, evidenceJson, actor, reason, verifiedMatches);
        await _store.SaveAsync(cancellationToken);
        return row;
    }

    public async Task<CommercialDocumentClassification> RejectAsync(long businessUnitId, Guid id,
        int expectedVersion, string actor, string reason, CancellationToken cancellationToken = default)
    {
        EnsureTenant(businessUnitId);
        var row = await FindAsync(businessUnitId, id, cancellationToken);
        row.Reject(expectedVersion, actor, reason);
        await _store.SaveAsync(cancellationToken);
        return row;
    }

    public async Task<CommercialDocumentClassification> LinkSupplierQuoteAsync(long businessUnitId,
        Guid id, int expectedVersion, long supplierQuoteId,
        CancellationToken cancellationToken = default)
    {
        EnsureTenant(businessUnitId);
        await ValidateMatchesAsync(businessUnitId,
            new CommercialDocumentMatchReferences(SupplierQuoteId: supplierQuoteId), cancellationToken);
        var row = await FindAsync(businessUnitId, id, cancellationToken);
        row.LinkSupplierQuote(expectedVersion, supplierQuoteId);
        await _store.SaveAsync(cancellationToken);
        return row;
    }

    public async Task<CommercialDocumentInboxResult> SearchInboxAsync(long businessUnitId,
        CommercialDocumentInboxQuery query, CancellationToken cancellationToken = default)
    {
        EnsureTenant(businessUnitId);
        ArgumentNullException.ThrowIfNull(query);
        if (query.Page < 1) throw new ArgumentOutOfRangeException(nameof(query.Page), "Page must be positive.");
        if (query.PageSize is < 1 or > 100)
            throw new ArgumentOutOfRangeException(nameof(query.PageSize), "Page size must be between 1 and 100.");

        var result = await _store.SearchInboxAsync(businessUnitId, query, cancellationToken);
        return new CommercialDocumentInboxResult(result.Rows.Select(ToItem).ToArray(), result.TotalCount,
            query.Page, query.PageSize);
    }

    public async Task<CommercialDocumentInboxDetail> GetInboxDetailAsync(long businessUnitId, Guid id,
        CancellationToken cancellationToken = default)
    {
        EnsureTenant(businessUnitId);
        var record = await _store.FindInboxAsync(businessUnitId, id, cancellationToken)
            ?? throw new CommercialDocumentNotFoundException("The classification does not exist in this tenant.");
        var row = record.Classification;
        return new CommercialDocumentInboxDetail(ToItem(record), row.SourceDocumentContentHash,
            row.SourceObjectVersion, row.EvidenceJson, row.ReviewedBy, row.ReviewReason, row.ReviewedOn);
    }

    public static SupplierQuoteProjectionReadiness ResolveSupplierQuoteProjection(
        CommercialDocumentClassification row)
    {
        ArgumentNullException.ThrowIfNull(row);
        if (row.DocumentType is not (CommercialDocumentType.SupplierQuote or
            CommercialDocumentType.SupplierQuoteRevision))
            return Projection(SupplierQuoteProjectionState.NotApplicable, false,
                "Document is not classified as a Supplier Quote.");
        if (row.ReviewStatus == CommercialDocumentReviewStatus.ReviewRequired)
            return Projection(SupplierQuoteProjectionState.ReviewRequired, false,
                "Classification review must be completed before projection.");
        if (row.ReviewStatus == CommercialDocumentReviewStatus.Rejected)
            return Projection(SupplierQuoteProjectionState.Rejected, false,
                "The commercial document classification was rejected.");
        if (!row.SupplierRfqId.HasValue)
            return Projection(SupplierQuoteProjectionState.MissingSupplierRfqMatch, false,
                "A tenant-qualified Supplier RFQ match is required.");
        if (!row.SourcingCaseId.HasValue)
            return Projection(SupplierQuoteProjectionState.MissingSourcingCaseMatch, false,
                "A tenant-qualified Sourcing Case match is required.");
        if (row.DocumentType == CommercialDocumentType.SupplierQuoteRevision && !row.SupplierQuoteId.HasValue)
            return Projection(SupplierQuoteProjectionState.MissingPriorSupplierQuoteMatch, false,
                "A Supplier Quote revision must identify the prior Supplier Quote.");
        if (row.DocumentType == CommercialDocumentType.SupplierQuote && row.SupplierQuoteId.HasValue)
            return Projection(SupplierQuoteProjectionState.AlreadyProjected, false,
                "This source document is already linked to a projected Supplier Quote.");
        return Projection(SupplierQuoteProjectionState.Ready, true);
    }

    private async Task<CommercialDocumentClassification> FindAsync(long businessUnitId, Guid id,
        CancellationToken cancellationToken) =>
        await _store.FindAsync(businessUnitId, id, cancellationToken)
        ?? throw new CommercialDocumentNotFoundException("The classification does not exist in this tenant.");

    private async Task ValidateMatchesAsync(long businessUnitId, CommercialDocumentMatchReferences matches,
        CancellationToken cancellationToken)
    {
        var invalid = await _store.FindInvalidMatchReferencesAsync(businessUnitId, matches, cancellationToken);
        if (invalid.Count > 0)
            throw new CommercialDocumentMatchValidationException(
                $"Commercial match references are not valid for the authenticated tenant: {string.Join(", ", invalid)}.");
    }

    private static CommercialDocumentInboxItem ToItem(CommercialDocumentInboxRecord record)
    {
        var row = record.Classification;
        return new CommercialDocumentInboxItem(row.Id, row.SourceDocumentId, record.OriginalFileName,
            record.DetectedMimeType, record.SecurityStatus, record.ProcessingStatus, row.DocumentType,
            row.ReviewStatus, row.Confidence, row.ClassificationMethod,
            MatchReferences(row),
            ResolveSupplierQuoteProjection(row), row.Version, row.CreatedOn, row.UpdatedOn);
    }

    private static CommercialDocumentMatchReferences MatchReferences(CommercialDocumentClassification row) =>
        new(row.CustomerRfqId, row.SupplierRfqId, row.SourcingCaseId, row.SupplierQuoteId,
            row.PurchaseOrderId, row.SupplierInvoiceId);

    private static SupplierQuoteProjectionReadiness Projection(SupplierQuoteProjectionState state,
        bool isReady, params string[] blockingReasons) => new(state, isReady, blockingReasons);

    private void EnsureTenant(long businessUnitId)
    {
        if (businessUnitId <= 0) throw new ArgumentOutOfRangeException(nameof(businessUnitId));
        if (_store.ScopedTenantId.HasValue && _store.ScopedTenantId != businessUnitId)
            throw new UnauthorizedAccessException("The requested business unit does not match the authenticated tenant.");
    }

    private static string ComputeRequestHash(long sourceDocumentId,
        CommercialDocumentClassificationSignals signals, CommercialDocumentMatchReferences matches)
    {
        ArgumentNullException.ThrowIfNull(signals);
        var canonical = JsonSerializer.Serialize(new
        {
            sourceDocumentId,
            originalFileName = Normalize(signals.OriginalFileName),
            subject = Normalize(signals.Subject),
            senderPartyType = Normalize(signals.SenderPartyType),
            bodyExcerpt = Normalize(signals.BodyExcerpt),
            referenceKind = Normalize(signals.ReferenceKind),
            matches
        });
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }

    private static string? Normalize(string? value) => string.IsNullOrWhiteSpace(value)
        ? null
        : value.Trim().ToLowerInvariant();

    private static string Required(string? value, int maximum, string name)
    {
        var normalized = value?.Trim();
        if (string.IsNullOrEmpty(normalized) || normalized.Length > maximum)
            throw new ArgumentException($"Value is required and must not exceed {maximum} characters.", name);
        return normalized;
    }
}
