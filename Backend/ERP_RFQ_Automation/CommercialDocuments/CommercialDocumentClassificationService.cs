using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ERP_RFQ_Automation.DocumentIntelligence.Persistence;
using ERP_RFQ_Automation.Models;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.CommercialDocuments;

public sealed record CommercialSourceDocumentIdentity(
    long Id,
    long BusinessUnitId,
    string ContentHash,
    string ObjectVersion);

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
        row.Confirm(expectedVersion, documentType, evidenceJson, actor, reason, matches);
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

    private async Task<CommercialDocumentClassification> FindAsync(long businessUnitId, Guid id,
        CancellationToken cancellationToken) =>
        await _store.FindAsync(businessUnitId, id, cancellationToken)
        ?? throw new CommercialDocumentNotFoundException("The classification does not exist in this tenant.");

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
