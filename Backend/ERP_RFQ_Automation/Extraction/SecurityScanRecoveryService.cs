using System.Text.Json;
using Amazon.S3;
using ERP_RFQ_Automation.DocumentIntelligence.Persistence;
using ERP_RFQ_Automation.Infrastructure.Storage;
using ERP_RFQ_Automation.Ingestion.Assembly;
using ERP_RFQ_Automation.Ingestion.Triage;
using ERP_RFQ_Automation.Models;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.Extraction;

public sealed record SecurityScanRetryItem(
    long SourceDocumentOccurrenceId,
    string FileName,
    string Status,
    string? ErrorCode,
    long? ExtractionJobId);

public sealed record SecurityScanRetryResult(
    Guid BatchId,
    int Eligible,
    int Queued,
    int StillAwaiting,
    int Rejected,
    int SourceObjectUnavailable,
    IReadOnlyList<SecurityScanRetryItem> Items)
{
    /// <summary>Every batch touched by this sweep. Single-element for a batch-scoped retry.</summary>
    public IReadOnlyList<Guid> Batches { get; init; } = [];

    /// <summary>True when the per-call cap was hit and the caller should invoke the sweep again.</summary>
    public bool MoreRemaining { get; init; }
}

/// <summary>One batch holding files that a scanner outage blocked, for operator discovery.</summary>
public sealed record BlockedBatchSummary(
    Guid BatchId,
    int BlockedFiles,
    DateTimeOffset OldestReceivedOn,
    DateTimeOffset NewestReceivedOn);

public interface ISecurityScanRecoveryService
{
    /// <summary>Replays the scanner-blocked files of one batch from their immutable source objects.</summary>
    Task<SecurityScanRetryResult> RetryBatchAsync(
        long businessUnitId,
        Guid batchId,
        CancellationToken ct = default);

    /// <summary>
    /// Tenant-wide sweep. The operator escape hatch: it needs no batch id and does not depend on the
    /// batch page still offering a retry control, which it stops doing once a hold has been recorded
    /// as Rejected.
    /// </summary>
    Task<SecurityScanRetryResult> RetryTenantAsync(
        long businessUnitId,
        CancellationToken ct = default);

    /// <summary>Lists the batches that currently hold scanner-blocked files.</summary>
    Task<IReadOnlyList<BlockedBatchSummary>> ListBlockedBatchesAsync(
        long businessUnitId,
        CancellationToken ct = default);
}

public sealed class SecurityScanRecoveryService : ISecurityScanRecoveryService
{
    private const int MaximumBatchFiles = 50;
    private const int MaximumDiscoveryFiles = 500;
    private const int MaximumFileBytes = 25 * 1024 * 1024;
    private readonly ErpRfqAutomationContext _db;
    private readonly IEvidenceObjectStorage _storage;
    private readonly IDocumentIngestion _ingestion;
    private readonly ILogger<SecurityScanRecoveryService>? _log;
    private readonly IEmailInquiryAssemblyCoordinator? _assemblies;

    public SecurityScanRecoveryService(
        ErpRfqAutomationContext db,
        IEvidenceObjectStorage storage,
        IDocumentIngestion ingestion,
        ILogger<SecurityScanRecoveryService>? log = null,
        IEmailInquiryAssemblyCoordinator? assemblies = null)
    {
        _db = db;
        _storage = storage;
        _ingestion = ingestion;
        _log = log;
        _assemblies = assemblies;
    }

    public Task<SecurityScanRetryResult> RetryBatchAsync(
        long businessUnitId,
        Guid batchId,
        CancellationToken ct = default) =>
        RetryAsync(businessUnitId, batchId, ct);

    public Task<SecurityScanRetryResult> RetryTenantAsync(
        long businessUnitId,
        CancellationToken ct = default) =>
        RetryAsync(businessUnitId, batchId: null, ct);

    public async Task<IReadOnlyList<BlockedBatchSummary>> ListBlockedBatchesAsync(
        long businessUnitId,
        CancellationToken ct = default)
    {
        var held = await (
            from occurrence in _db.Set<SourceDocumentOccurrence>().AsNoTracking()
            join corpus in _db.Set<DocumentCorpus>().AsNoTracking()
                on new { occurrence.BusinessUnitId, occurrence.CorpusId }
                equals new { corpus.BusinessUnitId, CorpusId = corpus.Id }
            where occurrence.BusinessUnitId == businessUnitId
                  && (occurrence.IntakeStatus == IntakeOccurrenceStatus.AwaitingSecurityScan
                      || occurrence.IntakeStatus == IntakeOccurrenceStatus.Rejected)
            orderby occurrence.ReceivedOn, occurrence.Id
            select new
            {
                corpus.BatchId,
                occurrence.IntakeStatus,
                occurrence.LastErrorCode,
                occurrence.SourceMetadataJson,
                occurrence.ReceivedOn
            })
            .Take(MaximumDiscoveryFiles)
            .ToListAsync(ct);

        return held
            .Where(x => SecurityHoldRecovery.IsRecoverableSecurityHold(
                x.IntakeStatus, x.LastErrorCode, x.SourceMetadataJson))
            .GroupBy(x => x.BatchId)
            .Select(group => new BlockedBatchSummary(
                group.Key,
                group.Count(),
                group.Min(x => x.ReceivedOn),
                group.Max(x => x.ReceivedOn)))
            .OrderBy(x => x.OldestReceivedOn)
            .ToArray();
    }

    private async Task<SecurityScanRetryResult> RetryAsync(
        long businessUnitId,
        Guid? batchId,
        CancellationToken ct)
    {
        var page = await (
            from occurrence in _db.Set<SourceDocumentOccurrence>().AsNoTracking()
            join corpus in _db.Set<DocumentCorpus>().AsNoTracking()
                on new { occurrence.BusinessUnitId, occurrence.CorpusId }
                equals new { corpus.BusinessUnitId, CorpusId = corpus.Id }
            join source in _db.Set<SourceDocument>().AsNoTracking()
                on new { occurrence.BusinessUnitId, occurrence.SourceDocumentId }
                equals new { source.BusinessUnitId, SourceDocumentId = source.Id }
            where occurrence.BusinessUnitId == businessUnitId
                  && (batchId == null || corpus.BatchId == batchId)
                  // Rejected is included deliberately: a scanner outage must never be a terminal
                  // user-facing state. SecurityHoldRecovery then separates "our infrastructure
                  // failed" from a real malware verdict or a malformed document.
                  && (occurrence.IntakeStatus == IntakeOccurrenceStatus.AwaitingSecurityScan
                      || occurrence.IntakeStatus == IntakeOccurrenceStatus.Rejected)
            orderby occurrence.ReceivedOn, occurrence.Id
            select new
            {
                Occurrence = occurrence,
                Source = source,
                corpus.BatchId
            })
            .Take(MaximumBatchFiles + 1)
            .ToListAsync(ct);
        var moreRemaining = page.Count > MaximumBatchFiles;
        var candidates = moreRemaining ? page.Take(MaximumBatchFiles).ToList() : page;

        var eligible = candidates.Where(x => IsRetryable(x.Occurrence)).ToArray();
        var items = new List<SecurityScanRetryItem>(eligible.Length);
        _log?.LogInformation(
            "Security-scan recovery sweep for business unit {BusinessUnitId} scope {Scope}: {Candidates} candidate(s), {Eligible} eligible.",
            businessUnitId, batchId?.ToString() ?? "all-batches", candidates.Count, eligible.Length);
        foreach (var candidate in eligible)
        {
            var batchIdForCandidate = candidate.BatchId;
            ct.ThrowIfCancellationRequested();
            var metadata = ParseMetadata(candidate.Occurrence.SourceMetadataJson);
            if (metadata is null)
            {
                await RecordTerminalEvidenceFailureAsync(
                    businessUnitId,
                    candidate.Occurrence.Id,
                    "source_object_metadata_unavailable",
                    "The immutable source-object identity is unavailable.",
                    integrityFailure: false,
                    ct);
                items.Add(new(candidate.Occurrence.Id, candidate.Source.OriginalFileName,
                    "SOURCE_OBJECT_UNAVAILABLE", "source_object_metadata_unavailable", null));
                continue;
            }

            // WHO OWNS THIS FILE, resolved BEFORE a byte is read.
            //
            // A blocked email attachment is not a loose document: it is one part of a message
            // that is waiting at the barrier for it. Replaying it as a loose document — which is
            // what omitting the component id did — produces a job that owns nothing, and the
            // worker's cutover fence then holds the whole message at NeedsReview and throws the
            // extraction away. So the ownership question is answered first, and a file whose
            // owner cannot be established is left held rather than replayed into that wall.
            var ownership = await ResolveEmailOwnershipAsync(
                businessUnitId, candidate.Occurrence, metadata, batchIdForCandidate, ct);
            if (ownership.Refusal is { } refusalCode)
            {
                _log?.LogWarning(
                    "Occurrence {OccurrenceId} in batch {BatchId} belongs to an email message whose "
                    + "component could not be resolved ({ReasonCode}); it is left held rather than "
                    + "replayed without its owner.",
                    candidate.Occurrence.Id, batchIdForCandidate, refusalCode);
                items.Add(new(candidate.Occurrence.Id, metadata.FileName,
                    "AwaitingSecurityScan", refusalCode, null));
                continue;
            }

            byte[] bytes;
            try
            {
                await using var stored = await _storage.OpenVerifiedReadAsync(
                    metadata.StorageUri, candidate.Source.ContentHash, ct);
                bytes = await ReadBoundedAsync(stored, ct);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                _log?.LogWarning(exception,
                    "Recovery source object is unavailable for occurrence {OccurrenceId} in batch {BatchId}.",
                    candidate.Occurrence.Id, batchIdForCandidate);
                var errorCode = SourceObjectErrorCode(exception);
                if (exception is InvalidDataException)
                {
                    await RecordTerminalEvidenceFailureAsync(
                        businessUnitId,
                        candidate.Occurrence.Id,
                        errorCode,
                        "The immutable source failed integrity or recovery-limit verification.",
                        integrityFailure: true,
                        ct);
                    items.Add(new(candidate.Occurrence.Id, metadata.FileName,
                        "Rejected", errorCode, null));
                }
                else if (IsMissingSource(exception))
                {
                    await RecordTerminalEvidenceFailureAsync(
                        businessUnitId,
                        candidate.Occurrence.Id,
                        errorCode,
                        "The immutable source object is no longer available.",
                        integrityFailure: false,
                        ct);
                    items.Add(new(candidate.Occurrence.Id, metadata.FileName,
                        "SOURCE_OBJECT_UNAVAILABLE", errorCode, null));
                }
                else
                {
                    items.Add(new(candidate.Occurrence.Id, metadata.FileName,
                        "AwaitingSecurityScan", EvidenceStorageUnavailableException.ErrorCode, null));
                }
                continue;
            }
            try
            {
                var ingested = await _ingestion.IngestAsync(
                    bytes,
                    metadata.FileName,
                    businessUnitId,
                    metadata.SourceType,
                    batchIdForCandidate,
                    metadata: metadata.Metadata,
                    // THE ownership authority, carried through the replay exactly as the email
                    // door carries it on the first pass. Omitting it here is what made the one
                    // control an operator has destroy the extraction it was meant to rescue.
                    emailInquiryComponentId: ownership.Owner?.ComponentId,
                    ct: ct);
                if (ownership.Owner is { } owner)
                    await BindRecoveredComponentAsync(businessUnitId, owner, ingested, ct);
                items.Add(new(candidate.Occurrence.Id, metadata.FileName,
                    "Queued", null, ingested.JobId));
            }
            catch (DocumentInspectionException exception)
            {
                // Still blocked: the scanner has not come back yet. The occurrence stays replayable.
                items.Add(new(candidate.Occurrence.Id, metadata.FileName,
                    exception.Inspection.IsRetryable ? "AwaitingSecurityScan" : "Rejected",
                    exception.Inspection.ErrorCode,
                    null));
            }
            catch (EvidenceStorageUnavailableException exception)
            {
                // The READ side above already answers a dead store with evidence_storage_unavailable
                // and leaves the occurrence replayable; the re-INGEST side used to let the same
                // fault escape and abort the sweep mid-way, so the candidates after this one were
                // neither retried nor reported. Same code, same replayable status, and the sweep
                // stops deliberately — every remaining candidate would fail identically.
                _log?.LogError(exception,
                    "Security-scan recovery stopped for business unit {BusinessUnitId}: durable evidence storage is "
                    + "unavailable (configuration fault: {IsConfigurationFault}). {Recovered} of {Eligible} candidate(s) "
                    + "were replayed; the rest stay replayable.",
                    businessUnitId, exception.IsConfigurationFault, items.Count, eligible.Length);
                items.Add(new(candidate.Occurrence.Id, metadata.FileName,
                    "AwaitingSecurityScan", EvidenceStorageUnavailableException.ErrorCode, null));
                break;
            }
        }

        var result = new SecurityScanRetryResult(
            batchId ?? Guid.Empty,
            eligible.Length,
            items.Count(x => x.Status == "Queued"),
            items.Count(x => x.Status == "AwaitingSecurityScan"),
            items.Count(x => x.Status == "Rejected"),
            items.Count(x => x.Status == "SOURCE_OBJECT_UNAVAILABLE"),
            items)
        {
            Batches = candidates.Select(x => x.BatchId).Distinct().ToArray(),
            MoreRemaining = moreRemaining
        };
        _log?.LogInformation(
            "Security-scan recovery sweep finished for business unit {BusinessUnitId}: Queued={Queued} StillAwaiting={StillAwaiting} Rejected={Rejected} SourceUnavailable={SourceUnavailable} MoreRemaining={MoreRemaining}.",
            businessUnitId, result.Queued, result.StillAwaiting, result.Rejected,
            result.SourceObjectUnavailable, result.MoreRemaining);
        return result;
    }


    // Shared with the batch-reconciliation read model so the "Retry blocked files" affordance and the
    // replay it triggers can never disagree. The previous local copy called TryGetProperty on the
    // `inspection` element without checking its kind; the ingest gateway always writes
    // `"inspection": null`, so that threw InvalidOperationException and every Rejected occurrence was
    // reported as ineligible — the retry endpoint answered Eligible: 0 and released nothing.
    private static bool IsRetryable(SourceDocumentOccurrence occurrence) =>
        SecurityHoldRecovery.IsRecoverableSecurityHold(
            occurrence.IntakeStatus,
            occurrence.LastErrorCode,
            occurrence.SourceMetadataJson);

    /// <summary>
    /// Establishes which <see cref="EmailInquiryComponent"/>, if any, owns a held occurrence.
    ///
    /// <para><b>The persisted component row is the authority; the recorded ids only say where to
    /// look.</b> The occurrence's stored metadata names a component, but that metadata came from
    /// the caller that wrote it, so it is verified against the full tuple the coordinator itself
    /// checks: the component exists for THIS tenant, its persisted <c>ComponentKey</c> is the
    /// occurrence identity the intake key was built from, and its assembly derives THIS batch. A
    /// job from another message therefore cannot be claimed by a mistyped id, and the composite
    /// foreign key on <c>ExtractionJobs</c> stands behind the answer.</para>
    ///
    /// <para>Ownership that cannot be established for an occurrence the email door plainly wrote
    /// is a REFUSAL, never a fallback to "replay it loose". Replaying it loose is the defect.</para>
    /// </summary>
    private async Task<EmailOwnership> ResolveEmailOwnershipAsync(
        long businessUnitId,
        SourceDocumentOccurrence occurrence,
        RecoveryMetadata metadata,
        Guid batchId,
        CancellationToken ct)
    {
        var sidecar = metadata.Metadata;
        var isEmailOwned = SourceOccurrenceIdentity.IsEmailOwned(occurrence.LogicalGroupKey)
                           || SourceOccurrenceIdentity.IsEmailOwned(sidecar?.LogicalGroupKey);
        if (!isEmailOwned)
            return EmailOwnership.NotEmailOwned;

        // A container without the assembly capability cannot rebind a component, and a replay it
        // cannot rebind is a burned extraction and a message left where it was. Refuse instead.
        if (_assemblies is null)
            return EmailOwnership.Refuse("email_component_recovery_unavailable");

        if (sidecar?.EmailInquiryComponentId is not { } recordedComponentId
            || string.IsNullOrWhiteSpace(sidecar.SourceOccurrenceId))
            return EmailOwnership.Refuse("email_component_ownership_unrecorded");

        var component = await _db.Set<EmailInquiryComponent>().AsNoTracking()
            .Where(x => x.BusinessUnitId == businessUnitId && x.Id == recordedComponentId)
            .Select(x => new
            {
                x.Id,
                x.AssemblyId,
                x.ComponentKey,
                x.ExtractionJobId,
                x.Status,
                AssemblyStatus = x.Assembly.Status,
                x.Assembly.MessageKey
            })
            .FirstOrDefaultAsync(ct);

        if (component is null
            || !string.Equals(component.ComponentKey, sidecar.SourceOccurrenceId.Trim(), StringComparison.Ordinal)
            || EmailIngestEnqueuer.DeriveBatchId(component.AssemblyId, component.MessageKey) != batchId)
            return EmailOwnership.Refuse("email_component_ownership_unresolved");

        // A component that already holds a durable job belongs to governed dead-letter recovery,
        // not to this sweep: re-submitting the same content would make it look active while the
        // exhausted job it is bound to is the thing that actually has to be retried.
        if (component.ExtractionJobId is not null)
            return EmailOwnership.Refuse("email_component_already_scheduled");

        if (component.Status is EmailInquiryComponentStatus.Completed
            or EmailInquiryComponentStatus.Skipped
            or EmailInquiryComponentStatus.RefusedSecurity
            or EmailInquiryComponentStatus.Ignored
            or EmailInquiryComponentStatus.StructuralOnly)
            return EmailOwnership.Refuse("email_component_already_settled");

        // Checked BEFORE the replay, not after. The coordinator refuses to resume a component
        // whose message cannot legally re-enter scheduling, and discovering that after the
        // ingest would leave a job running toward a barrier that can never open.
        if (!EmailInquiryAssemblyStateMachine.CanAutomaticSchedulingRecoveryTransition(
                component.AssemblyStatus))
            return EmailOwnership.Refuse("email_message_not_recoverable");

        return EmailOwnership.Owned(
            new EmailComponentOwner(component.Id, component.AssemblyId, component.ComponentKey));
    }

    /// <summary>
    /// Binds the replayed job to its component and lets the message re-enter extraction.
    ///
    /// <para>Passing the component id to the queue is necessary but NOT sufficient. The component
    /// is still <c>FailedRecoverable</c> and its message still <c>FailedRecoverable</c>, and the
    /// state machine deliberately has no <c>FailedRecoverable → ReadyForAssembly</c> transition —
    /// so the result would arrive, the barrier would evaluate "ready", and the transition would be
    /// refused and logged while the message stayed exactly where it was. The coordinator's
    /// automatic-scheduling-recovery path is what walks both rows back into <c>Extracting</c>,
    /// and it is the same path the mailbox re-poll uses.</para>
    /// </summary>
    private async Task BindRecoveredComponentAsync(
        long businessUnitId,
        EmailComponentOwner owner,
        IngestedDocument ingested,
        CancellationToken ct)
    {
        try
        {
            await _assemblies!.RecordComponentQueuedAsync(
                businessUnitId, owner.AssemblyId, owner.ComponentKey, ingested.JobId, ct,
                ingested.StoragePath, ingested.SourceDocumentOccurrenceId);
            _log?.LogInformation(
                "Security-scan recovery rebound component {ComponentKey} of assembly {AssemblyId} "
                + "to job {JobId} for business unit {BusinessUnitId}; the message re-enters extraction.",
                owner.ComponentKey, owner.AssemblyId, ingested.JobId, businessUnitId);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // The job exists and carries its owner, so the extraction is not lost — but the
            // message did not move, and that must be visible rather than inferred from a Lead
            // that never appears.
            _log?.LogError(exception,
                "Security-scan recovery queued job {JobId} for component {ComponentKey} of assembly "
                + "{AssemblyId} (business unit {BusinessUnitId}) but could not rebind the component; "
                + "the message remains held.",
                ingested.JobId, owner.ComponentKey, owner.AssemblyId, businessUnitId);
        }
    }

    private static RecoveryMetadata? ParseMetadata(string sourceMetadataJson)
    {
        try
        {
            using var document = JsonDocument.Parse(sourceMetadataJson);
            var root = document.RootElement;
            var quarantine = root.GetProperty("immutableObjects").GetProperty("quarantine");
            var sourceType = Enum.Parse<ExtractionSourceType>(root.GetProperty("sourceType").GetString()!, true);
            var metadata = root.TryGetProperty("metadata", out var metadataElement)
                           && metadataElement.ValueKind is not JsonValueKind.Null
                ? metadataElement.Deserialize<ExtractionJobMetadata>()
                : null;
            return new RecoveryMetadata(
                root.GetProperty("fileName").GetString()!,
                sourceType,
                quarantine.GetProperty("StorageUri").GetString()!,
                metadata);
        }
        catch (Exception exception) when (exception is JsonException or KeyNotFoundException or InvalidOperationException or ArgumentException)
        {
            return null;
        }
    }

    private static async Task<byte[]> ReadBoundedAsync(Stream stream, CancellationToken ct)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(chunk.AsMemory(0, chunk.Length), ct)) > 0)
        {
            if (buffer.Length + read > MaximumFileBytes)
                throw new InvalidDataException("The quarantined file exceeds the recovery limit.");
            await buffer.WriteAsync(chunk.AsMemory(0, read), ct);
        }
        return buffer.ToArray();
    }

    private static string SourceObjectErrorCode(Exception exception) => exception switch
    {
        InvalidDataException when exception.Message.Contains("exceeds the recovery limit", StringComparison.Ordinal) =>
            "source_object_exceeds_recovery_limit",
        InvalidDataException => "source_object_integrity_failed",
        _ => "source_object_unavailable"
    };

    private async Task RecordTerminalEvidenceFailureAsync(
        long businessUnitId,
        long occurrenceId,
        string errorCode,
        string reason,
        bool integrityFailure,
        CancellationToken ct)
    {
        var occurrence = await _db.Set<SourceDocumentOccurrence>().SingleAsync(
            x => x.BusinessUnitId == businessUnitId && x.Id == occurrenceId, ct);
        var details = JsonSerializer.Serialize(new { errorCode, reason, retryable = false });
        if (integrityFailure)
            occurrence.MarkEvidenceIntegrityFailure(errorCode, details);
        else
            occurrence.MarkSourceObjectUnavailable(errorCode, details);
        await _db.SaveChangesAsync(ct);
        _db.ChangeTracker.Clear();
    }

    private static bool IsMissingSource(Exception exception) =>
        exception is FileNotFoundException or DirectoryNotFoundException
        || exception is AmazonS3Exception { StatusCode: System.Net.HttpStatusCode.NotFound };

    private sealed record RecoveryMetadata(
        string FileName,
        ExtractionSourceType SourceType,
        string StorageUri,
        ExtractionJobMetadata? Metadata);

    /// <summary>The verified message part a held occurrence belongs to.</summary>
    private sealed record EmailComponentOwner(long ComponentId, long AssemblyId, string ComponentKey);

    /// <summary>
    /// Three outcomes, deliberately distinct: the file is a loose document (replay it as before),
    /// the file is a message part whose owner is proven (replay it as that part), or the file is a
    /// message part whose owner is not proven (leave it held and say why). There is no fourth
    /// outcome in which an email part is replayed as a loose document — that is the defect.
    /// </summary>
    private readonly record struct EmailOwnership(EmailComponentOwner? Owner, string? Refusal)
    {
        public static readonly EmailOwnership NotEmailOwned = new(null, null);
        public static EmailOwnership Owned(EmailComponentOwner owner) => new(owner, null);
        public static EmailOwnership Refuse(string reasonCode) => new(null, reasonCode);
    }
}
