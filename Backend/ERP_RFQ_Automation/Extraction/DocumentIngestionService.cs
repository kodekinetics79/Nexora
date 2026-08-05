using System;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ERP_RFQ_Automation.DocumentIntelligence.Persistence;
using Microsoft.Extensions.Logging;
using ERP_RFQ_Automation.Infrastructure.Storage;
using ERP_RFQ_Automation.LeadIdentity;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Security.DocumentInspection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace ERP_RFQ_Automation.Extraction;

/// <summary>Result of ingesting one document through the unified gateway.</summary>
public sealed class IngestedDocument
{
    public long JobId { get; init; }
    public long SourceDocumentOccurrenceId { get; init; }
    public Guid BatchId { get; init; }
    public string ContentHash { get; init; } = null!;
    public string StoragePath { get; init; } = null!;
    public EnqueueOutcome Outcome { get; init; }
    public ExtractionStatus? ExistingStatus { get; init; }
}

public sealed class DocumentInspectionException : IOException
{
    public DocumentInspectionException(
        FileInspectionResult inspection,
        long? sourceDocumentOccurrenceId = null,
        Guid? batchId = null)
        : base(inspection.Reason)
    {
        Inspection = inspection;
        SourceDocumentOccurrenceId = sourceDocumentOccurrenceId;
        BatchId = batchId;
    }

    public FileInspectionResult Inspection { get; }
    public long? SourceDocumentOccurrenceId { get; }
    public Guid? BatchId { get; }
}

/// <summary>
/// The ONE ingestion gateway all four doors (modern upload, email poller, folder
/// watcher, manual upload) call to feed the durable extraction queue: persists the raw
/// bytes to the immutable, content-addressed store
/// (<c>Uploads/Extraction/&lt;aa&gt;/&lt;sha256&gt;&lt;ext&gt;</c> — the exact layout the
/// ExtractionController introduced), optionally writes the ingest-provenance sidecar,
/// and enqueues exactly ONE job per document (files are never merged; dedup stays
/// natural via the queue's (BusinessUnitId, ContentHash) idempotency).
/// </summary>
public interface IDocumentIngestion
{
    Task<IngestedDocument> IngestAsync(
        byte[] bytes,
        string fileName,
        long businessUnitId,
        ExtractionSourceType sourceType,
        Guid? batchId = null,
        int priority = 0,
        ExtractionJobMetadata? metadata = null,
        CancellationToken ct = default);
}

public sealed class DocumentIngestionService : IDocumentIngestion
{
    private readonly IExtractionQueue _queue;
    private readonly ILogger<DocumentIngestionService> _log;
    private readonly IEvidenceObjectStorage _storage;
    private readonly IFileInspectionService _inspection;
    private readonly ErpRfqAutomationContext _context;
    private readonly MalwareVerdictPolicyOptions _verdictPolicy;

    public DocumentIngestionService(
        IExtractionQueue queue,
        IEvidenceObjectStorage storage,
        IFileInspectionService inspection,
        ErpRfqAutomationContext context,
        ILogger<DocumentIngestionService> log)
        : this(queue, storage, inspection, context, log,
            Options.Create(new MalwareVerdictPolicyOptions()))
    {
    }

    public DocumentIngestionService(
        IExtractionQueue queue,
        IEvidenceObjectStorage storage,
        IFileInspectionService inspection,
        ErpRfqAutomationContext context,
        ILogger<DocumentIngestionService> log,
        IOptions<MalwareVerdictPolicyOptions> verdictPolicy)
    {
        _queue = queue;
        _log = log;
        _storage = storage;
        _inspection = inspection;
        _context = context;
        _verdictPolicy = verdictPolicy.Value;
        if (_verdictPolicy.MaximumCleanVerdictAge <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(verdictPolicy), "The malware-verdict maximum age must be positive.");
    }

    public Task<IngestedDocument> IngestAsync(
        byte[] bytes,
        string fileName,
        long businessUnitId,
        ExtractionSourceType sourceType,
        Guid? batchId = null,
        int priority = 0,
        ExtractionJobMetadata? metadata = null,
        CancellationToken ct = default)
    {
        return _context.Database.CreateExecutionStrategy().ExecuteAsync(
            () => IngestCoreAsync(bytes, fileName, businessUnitId, sourceType, batchId, priority, metadata, ct));
    }

    private async Task<IngestedDocument> IngestCoreAsync(
        byte[] bytes,
        string fileName,
        long businessUnitId,
        ExtractionSourceType sourceType,
        Guid? batchId,
        int priority,
        ExtractionJobMetadata? metadata,
        CancellationToken ct)
    {
        if (bytes is null || bytes.Length == 0)
            throw new ArgumentException("Cannot ingest an empty document.", nameof(bytes));
        if (businessUnitId <= 0)
            throw new ArgumentException("A valid businessUnitId is required.", nameof(businessUnitId));

        var hashTimer = Stopwatch.StartNew();
        var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        hashTimer.Stop();
        var actualBatchId = batchId ?? SourceOccurrenceIdentity.BuildFallbackBatchId(
            businessUnitId, sourceType, fileName, hash, metadata);
        var suppliedExtension = Path.GetExtension(fileName).ToLowerInvariant();
        var quarantineObject = await _storage.WriteImmutableAsync(
            businessUnitId, "quarantine", hash, suppliedExtension, bytes, ct);

        DocumentCorpus corpus;
        SourceDocument source;
        SourceDocumentOccurrence occurrence;
        SourceDocumentOccurrence? originalOccurrence;
        bool sourceWasExisting;
        bool occurrenceWasExisting;
        var occurrenceKey = SourceOccurrenceIdentity.BuildKey(actualBatchId, sourceType, metadata);
        var lockIdentity = $"evidence-ingest:{businessUnitId}:{hash}";

        await using (var intakeTransaction = await _context.Database.BeginTransactionAsync(ct))
        {
            if (_context.Database.IsNpgsql())
            {
                await _context.Database.ExecuteSqlInterpolatedAsync(
                    $"SELECT pg_advisory_xact_lock(hashtextextended({lockIdentity}, 0))", ct);
            }

            corpus = await _context.Set<DocumentCorpus>()
                .SingleOrDefaultAsync(x => x.BusinessUnitId == businessUnitId && x.BatchId == actualBatchId, ct)
                ?? DocumentCorpus.Create(businessUnitId, actualBatchId, MapSourceType(sourceType));
            if (corpus.Id == 0)
            {
                _context.Add(corpus);
                _context.Add(new LeadIngestionBatch
                {
                    Id = actualBatchId,
                    BusinessUnitId = businessUnitId,
                    SourceChannel = sourceType.ToString(),
                    CreatedBy = string.IsNullOrWhiteSpace(metadata?.UploadedBy)
                        ? "document-ingestion"
                        : metadata.UploadedBy.Trim(),
                    CreatedAtUtc = DateTimeOffset.UtcNow,
                    UpdatedAtUtc = DateTimeOffset.UtcNow
                });
                await _context.SaveChangesAsync(ct);
            }

            source = await _context.Set<SourceDocument>()
                .SingleOrDefaultAsync(x => x.BusinessUnitId == businessUnitId && x.ContentHash == hash, ct);
            sourceWasExisting = source is not null;
            if (source is null)
            {
                source = SourceDocument.Create(
                    businessUnitId, corpus.Id, hash, fileName,
                    ProvisionalMime(suppliedExtension), quarantineObject.Bucket,
                    quarantineObject.Key, quarantineObject.Version, bytes.LongLength);
                _context.Add(source);
                await _context.SaveChangesAsync(ct);
            }

            occurrence = await _context.Set<SourceDocumentOccurrence>()
                .SingleOrDefaultAsync(x => x.BusinessUnitId == businessUnitId && x.IdempotencyKey == occurrenceKey, ct);
            occurrenceWasExisting = occurrence is not null;
            if (occurrence is null)
            {
                occurrence = SourceDocumentOccurrence.Create(
                    businessUnitId, source.Id, corpus.Id, occurrenceKey,
                    BuildSourceMetadata(fileName, sourceType, metadata, null,
                        quarantineObject, quarantineObject));
                occurrence.SetLogicalGroup(metadata?.LogicalGroupKey);
                occurrence.RecordUploadResources(bytes.LongLength, hashTimer.ElapsedMilliseconds,
                    sourceWasExisting ? 0 : bytes.LongLength);
                _context.Add(occurrence);
                await _context.SaveChangesAsync(ct);
            }
            else if (occurrence.SourceDocumentId != source.Id)
            {
                throw new InvalidOperationException("The intake idempotency key is already bound to different document content.");
            }

            originalOccurrence = await _context.Set<SourceDocumentOccurrence>()
                .Where(x => x.BusinessUnitId == businessUnitId
                            && x.SourceDocumentId == source.Id
                            && x.Id != occurrence.Id)
                .OrderBy(x => x.ReceivedOn).ThenBy(x => x.Id)
                .FirstOrDefaultAsync(ct);
            if (originalOccurrence is not null && !occurrence.OriginalOccurrenceId.HasValue)
            {
                occurrence.MarkExactDuplicateCandidate(originalOccurrence.Id,
                    !source.HasFreshCleanMalwareVerdict(DateTimeOffset.UtcNow,
                        _verdictPolicy.MaximumCleanVerdictAge));
            }

            await _context.SaveChangesAsync(ct);
            await intakeTransaction.CommitAsync(ct);
        }

        if (occurrenceWasExisting && occurrence.ExtractionJobId is { } replayJobId)
        {
            var replayStatus = await _context.Set<ExtractionJob>().AsNoTracking()
                .Where(x => x.BusinessUnitId == businessUnitId && x.Id == replayJobId)
                .Select(x => (ExtractionStatus?)x.Status)
                .SingleOrDefaultAsync(ct);
            return new IngestedDocument
            {
                JobId = replayJobId,
                SourceDocumentOccurrenceId = occurrence.Id,
                BatchId = actualBatchId,
                ContentHash = hash,
                StoragePath = quarantineObject.StorageUri,
                Outcome = EnqueueOutcome.Duplicate,
                ExistingStatus = replayStatus
            };
        }

        var reusableVerdict = source.HasFreshCleanMalwareVerdict(
            DateTimeOffset.UtcNow, _verdictPolicy.MaximumCleanVerdictAge)
            ? new ReusableMalwareVerdict(
                source.MalwareScannerEngine ?? "recorded-verdict",
                source.MalwareSignatureVersion,
                source.MalwareScannedOn!.Value)
            : null;
        FileInspectionResult inspection;
        await using (var content = new MemoryStream(bytes, writable: false))
            inspection = await _inspection.InspectAsync(
                new FileInspectionRequest(content, fileName, DeclaredLength: bytes.LongLength,
                    ReusableMalwareVerdict: reusableVerdict), ct);

        var selectedObject = inspection.IsCleared
            ? await _storage.WriteImmutableAsync(
                businessUnitId, "cleared", hash, suppliedExtension, bytes, ct)
            : quarantineObject;

        await using var transaction = await _context.Database.BeginTransactionAsync(ct);
        if (_context.Database.IsNpgsql())
        {
            await _context.Database.ExecuteSqlInterpolatedAsync(
                $"SELECT pg_advisory_xact_lock(hashtextextended({lockIdentity}, 0))", ct);
            await _context.Entry(source).ReloadAsync(ct);
            await _context.Entry(occurrence).ReloadAsync(ct);
        }
        if (!string.IsNullOrWhiteSpace(inspection.DetectedContentType))
            source.RecordInspection(inspection.DetectedContentType);
        occurrence.RecordSecurityWork(inspection.MalwareVerdictReused, !inspection.MalwareVerdictReused);
        occurrence.RecordUploadResources(bytes.LongLength, hashTimer.ElapsedMilliseconds,
            sourceWasExisting ? 0 : bytes.LongLength + (inspection.IsCleared ? bytes.LongLength : 0));
        occurrence.RecordActualCost(0m, 0m, "LOCAL_COMPUTE_UNPRICED");
        if (!inspection.MalwareVerdictReused && inspection.MalwareStatus.HasValue)
            source.RecordMalwareVerdict(inspection.MalwareStatus.Value,
                inspection.ScannerEngine, inspection.ScannerSignature);

        if (inspection.IsCleared
                 && source.SecurityStatus is DocumentSecurityStatus.Pending or DocumentSecurityStatus.Quarantined
                 && await CanReleaseAfterScannerRecoveryAsync(source.Id, ct))
            source.ReleaseFromQuarantine(selectedObject.Bucket, selectedObject.Key, selectedObject.Version);
        else if (!inspection.IsCleared && source.SecurityStatus == DocumentSecurityStatus.Pending)
            source.MarkSecurityStatus(inspection.MalwareStatus == MalwareScanStatus.Infected
                ? DocumentSecurityStatus.Rejected
                : MapSecurityStatus(inspection.Status));
        else if (inspection.MalwareStatus == MalwareScanStatus.Infected
                 && source.SecurityStatus != DocumentSecurityStatus.Rejected)
            source.MarkSecurityStatus(DocumentSecurityStatus.Rejected);

        var sourceIsCleared = source.SecurityStatus == DocumentSecurityStatus.Cleared;
        if (!inspection.IsCleared || !sourceIsCleared)
        {
            if (corpus.Status is CorpusStatus.Received or CorpusStatus.Processing)
                corpus.RequireReview();
            var rejectedInspection = sourceIsCleared || !inspection.IsCleared
                ? inspection
                : await CarryForwardSourceRejectionAsync(businessUnitId, source, occurrence.Id, ct);
            var detailsJson = JsonSerializer.Serialize(new
            {
                status = rejectedInspection.Status.ToString(),
                reason = rejectedInspection.Reason,
                scanner = rejectedInspection.ScannerEngine,
                signature = rejectedInspection.ScannerSignature,
                retryable = rejectedInspection.IsRetryable
            });
            if (rejectedInspection.MalwareStatus is MalwareScanStatus.Unavailable or MalwareScanStatus.Error)
            {
                // The tenant only ever sees rejectedInspection.Reason; the endpoint/exception detail
                // that tells an operator WHAT to fix goes here and nowhere else.
                _log.LogError(
                    "Security scanning unavailable — {FileName} (occurrence {OccurrenceId}, batch {BatchId}) is held, not lost. " +
                    "Engine={Engine} ErrorCode={ErrorCode} Retryable={Retryable}. Detail: {Diagnostics}",
                    fileName,
                    occurrence.Id,
                    actualBatchId,
                    rejectedInspection.ScannerEngine,
                    rejectedInspection.ErrorCode,
                    rejectedInspection.IsRetryable,
                    rejectedInspection.OperatorDiagnostics ?? "(no scanner diagnostics recorded)");
            }

            if (rejectedInspection.IsRetryable)
            {
                occurrence.MarkAwaitingSecurityScan(rejectedInspection.ErrorCode, detailsJson);
            }
            else if (occurrence.IntakeStatus is IntakeOccurrenceStatus.Accepted
                     or IntakeOccurrenceStatus.AwaitingSecurityScan)
            {
                occurrence.MarkRejected(
                    "SecurityInspection",
                    rejectedInspection.ErrorCode,
                    detailsJson);
            }
            await _context.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            throw new DocumentInspectionException(rejectedInspection, occurrence.Id, actualBatchId);
        }

        var fileType = ExtensionForMime(inspection.DetectedContentType, suppliedExtension);
        EnqueueResult enqueue;
        if (source.ExtractionJobId is { } existingJobId)
        {
            var existingStatus = await _context.Set<ExtractionJob>().AsNoTracking()
                .Where(x => x.BusinessUnitId == businessUnitId && x.Id == existingJobId)
                .Select(x => (ExtractionStatus?)x.Status)
                .SingleOrDefaultAsync(ct);
            enqueue = new EnqueueResult
            {
                JobId = existingJobId,
                BatchId = actualBatchId,
                ContentHash = hash,
                Outcome = EnqueueOutcome.Duplicate,
                ExistingStatus = existingStatus
            };
            if (existingStatus == ExtractionStatus.Succeeded)
                occurrence.ResolveByProcessingReuse(existingJobId);
            else if (existingStatus == ExtractionStatus.DeadLetter)
                occurrence.BindDeadLetterJob(existingJobId, "extraction_dead_letter");
            else
                occurrence.BindExtractionJob(existingJobId);
            if (occurrence.OriginalOccurrenceId.HasValue)
                occurrence.ConfirmExactDuplicate(processingReused: existingStatus == ExtractionStatus.Succeeded);
        }
        else
        {
            enqueue = await _queue.EnqueueAsync(new EnqueueExtractionRequest
            {
                BusinessUnitId = businessUnitId,
                SourceDocumentOccurrenceId = occurrence.Id,
                SourceType = sourceType,
                StoragePath = selectedObject.StorageUri,
                FileName = fileName,
                FileType = string.IsNullOrEmpty(fileType) ? null : fileType,
                ContentHash = hash,
                BatchId = actualBatchId,
                Priority = priority
            }, ct);
            occurrence.BindExtractionJob(enqueue.JobId);
            source.BindExtractionJob(enqueue.JobId);
            if (occurrence.OriginalOccurrenceId.HasValue)
                occurrence.ConfirmExactDuplicate(processingReused: false);
        }
        if (corpus.Status == CorpusStatus.Received)
        {
            corpus.StartProcessing();
            if (enqueue.Outcome == EnqueueOutcome.Duplicate
                && enqueue.ExistingStatus == ExtractionStatus.Succeeded)
                corpus.Complete();
            else if (enqueue.ExistingStatus == ExtractionStatus.DeadLetter)
                corpus.RequireReview();
        }
        await _context.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);

        _log.LogInformation(
            "Ingested {FileName} ({Bytes} bytes) for BU {BU} via {Source}: job {JobId} ({Outcome}).",
            fileName, bytes.Length, businessUnitId, sourceType, enqueue.JobId, enqueue.Outcome);

        return new IngestedDocument
        {
            JobId = enqueue.JobId,
            SourceDocumentOccurrenceId = occurrence.Id,
            BatchId = enqueue.BatchId,
            ContentHash = enqueue.ContentHash,
            StoragePath = selectedObject.StorageUri,
            Outcome = enqueue.Outcome,
            ExistingStatus = enqueue.ExistingStatus
        };
    }

    private static CorpusSourceType MapSourceType(ExtractionSourceType sourceType) => sourceType switch
    {
        ExtractionSourceType.Email => CorpusSourceType.Email,
        ExtractionSourceType.ManualUpload => CorpusSourceType.ManualUpload,
        ExtractionSourceType.Folder => CorpusSourceType.Folder,
        ExtractionSourceType.ExcelTemplate => CorpusSourceType.Import,
        _ => CorpusSourceType.Api
    };

    private static DocumentSecurityStatus MapSecurityStatus(FileInspectionStatus status) => status switch
    {
        FileInspectionStatus.Cleared => DocumentSecurityStatus.Cleared,
        FileInspectionStatus.Quarantined => DocumentSecurityStatus.Quarantined,
        _ => DocumentSecurityStatus.Rejected
    };

    /// <summary>
    /// This upload's own inspection passed, but the authoritative source document is already
    /// Quarantined/Rejected from an EARLIER occurrence — so there is no current-inspection reason
    /// to report. The earlier occurrence recorded the real one, and substituting a generic sentence
    /// here is how a truthful explanation ("this workbook contains macros") degraded into a vacuous
    /// one ("has not passed security inspection") the moment the user retried. Carry the recorded
    /// reason and code forward instead; fall back to the generic sentence only when nothing was
    /// ever recorded.
    /// </summary>
    private async Task<FileInspectionResult> CarryForwardSourceRejectionAsync(
        long businessUnitId,
        SourceDocument source,
        long currentOccurrenceId,
        CancellationToken ct)
    {
        var status = source.SecurityStatus == DocumentSecurityStatus.Rejected
            ? FileInspectionStatus.Rejected
            : FileInspectionStatus.Quarantined;

        var recorded = await _context.Set<SourceDocumentOccurrence>().AsNoTracking()
            .Where(x => x.BusinessUnitId == businessUnitId
                        && x.SourceDocumentId == source.Id
                        && x.Id != currentOccurrenceId
                        && x.LastErrorCode != null)
            .OrderBy(x => x.ReceivedOn).ThenBy(x => x.Id)
            .Select(x => new { x.LastErrorCode, x.LastErrorDetailsJson })
            .ToListAsync(ct);

        // The carried code must be CONSISTENT WITH THE STATUS BEING CARRIED. A source whose
        // history is [scanner outage -> Infected verdict] has an earliest occurrence coded
        // security_scanner_unavailable; carrying that code onto a Rejected status makes
        // SecurityHoldRecovery.IsRecoverableSecurityHold answer true, so the UI offers
        // "Retry blocked files" and the sweep replays a source that no path can ever
        // release — a permanent phantom retry. When carrying Rejected, only terminal codes
        // are eligible; the recoverable ones (and their outage-flavoured reasons, which
        // describe the scanner, not the document) are skipped.
        var eligible = status == FileInspectionStatus.Rejected
            ? recorded.Where(x => !SecurityHoldRecovery.RecoverableErrorCodes
                .Contains(x.LastErrorCode!, StringComparer.OrdinalIgnoreCase)).ToList()
            : recorded;

        string? carriedReason = null;
        string? carriedCode = eligible.Count > 0 ? eligible[0].LastErrorCode : null;
        foreach (var entry in eligible)
        {
            if (string.IsNullOrWhiteSpace(entry.LastErrorDetailsJson))
                continue;
            try
            {
                using var details = JsonDocument.Parse(entry.LastErrorDetailsJson);
                if (details.RootElement.ValueKind == JsonValueKind.Object
                    && details.RootElement.TryGetProperty("reason", out var reason)
                    && reason.ValueKind == JsonValueKind.String
                    && !string.IsNullOrWhiteSpace(reason.GetString()))
                {
                    carriedReason = reason.GetString();
                    carriedCode = entry.LastErrorCode;
                    break;
                }
            }
            catch (JsonException)
            {
                // Malformed legacy details: the machine code stays authoritative.
            }
        }

        var carried = new FileInspectionResult(
            status,
            source.DetectedMimeType,
            source.ByteSize,
            carriedReason ?? "The authoritative source document has not passed security inspection.",
            "evidence-ledger",
            null);
        return string.IsNullOrWhiteSpace(carriedCode) ? carried : carried with { ErrorCode = carriedCode };
    }

    private async Task<bool> CanReleaseAfterScannerRecoveryAsync(long sourceDocumentId, CancellationToken ct)
    {
        var metadata = await _context.Set<SourceDocumentOccurrence>()
            .Where(x => x.SourceDocumentId == sourceDocumentId)
            .Select(x => x.SourceMetadataJson)
            .ToListAsync(ct);

        foreach (var value in metadata)
        {
            try
            {
                using var document = JsonDocument.Parse(value);
                if (document.RootElement.TryGetProperty("inspection", out var inspection)
                    && inspection.ValueKind == JsonValueKind.Object
                    && inspection.TryGetProperty("malwareStatus", out var malwareStatus)
                    && malwareStatus.ValueKind == JsonValueKind.String
                    && string.Equals(malwareStatus.GetString(), MalwareScanStatus.Infected.ToString(),
                        StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
            }
            catch (JsonException)
            {
                return false;
            }
        }

        return true;
    }

    private static string BuildSourceMetadata(
        string fileName,
        ExtractionSourceType sourceType,
        ExtractionJobMetadata? metadata,
        FileInspectionResult? inspection,
        EvidenceObject quarantineObject,
        EvidenceObject selectedObject) => JsonSerializer.Serialize(new
    {
        fileName,
        sourceType = sourceType.ToString(),
        metadata,
        immutableObjects = new
        {
            quarantine = ObjectIdentity(quarantineObject),
            selected = ObjectIdentity(selectedObject)
        },
        inspection = inspection is null ? null : new
        {
            status = inspection.Status.ToString(),
            malwareStatus = inspection.MalwareStatus?.ToString(),
            inspection.DetectedContentType,
            inspection.Reason,
            inspection.ScannerEngine,
            inspection.ScannerSignature,
            inspection.IsRetryable,
            inspection.ErrorCode,
            inspection.MalwareVerdictReused
        }
    });

    private static object ObjectIdentity(EvidenceObject value) => new
    {
        value.StorageUri,
        value.Bucket,
        value.Key,
        value.Version,
        value.ETag,
        value.ByteSize
    };

    private static string ProvisionalMime(string extension) => extension switch
    {
        ".pdf" => "application/pdf",
        ".doc" => "application/msword",
        ".docx" => "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        ".xls" => "application/vnd.ms-excel",
        ".xlsx" or ".xlsm" => "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
        ".csv" => "text/csv",
        ".txt" => "text/plain",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".png" => "image/png",
        ".tif" or ".tiff" => "image/tiff",
        _ => "application/octet-stream"
    };

    private static string ExtensionForMime(string? mime, string suppliedExtension) => mime switch
    {
        "application/pdf" => "pdf",
        "application/msword" => "doc",
        "application/vnd.ms-excel" => "xls",
        "application/vnd.openxmlformats-officedocument.wordprocessingml.document" => "docx",
        "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet" =>
            suppliedExtension.Equals(".xlsm", StringComparison.OrdinalIgnoreCase) ? "xlsm" : "xlsx",
        "text/csv" => "csv",
        "text/plain" => "txt",
        "image/jpeg" => "jpg",
        "image/png" => "png",
        "image/gif" => "gif",
        "image/bmp" => "bmp",
        "image/tiff" => "tiff",
        "image/webp" => "webp",
        _ => suppliedExtension.TrimStart('.').ToLowerInvariant()
    };
}

public static class SourceOccurrenceIdentity
{
    public static string BuildKey(
        Guid batchId,
        ExtractionSourceType sourceType,
        ExtractionJobMetadata? metadata)
    {
        var stableIdentity = string.IsNullOrWhiteSpace(metadata?.SourceOccurrenceId)
            ? $"fallback:{metadata?.EmailIngestId}:{metadata?.LogicalGroupKey}"
            : metadata.SourceOccurrenceId.Trim();
        var occurrenceIdentity = Convert.ToHexString(SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(stableIdentity))).ToLowerInvariant();
        return $"{batchId:D}:{sourceType}:{occurrenceIdentity}";
    }

    public static Guid BuildFallbackBatchId(long businessUnitId, ExtractionSourceType sourceType,
        string fileName, string contentHash, ExtractionJobMetadata? metadata)
    {
        if (string.IsNullOrWhiteSpace(metadata?.SourceOccurrenceId))
            return Guid.NewGuid();

        var stableReceipt = $"{businessUnitId}:{sourceType}:{metadata.SourceOccurrenceId.Trim()}";
        var bytes = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(stableReceipt));
        return new Guid(bytes.AsSpan(0, 16));
    }
}
