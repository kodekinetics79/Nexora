using System;
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

    public DocumentIngestionService(
        IExtractionQueue queue,
        IEvidenceObjectStorage storage,
        IFileInspectionService inspection,
        ErpRfqAutomationContext context,
        ILogger<DocumentIngestionService> log)
    {
        _queue = queue;
        _log = log;
        _storage = storage;
        _inspection = inspection;
        _context = context;
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

        var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        var actualBatchId = batchId ?? SourceOccurrenceIdentity.BuildFallbackBatchId(
            businessUnitId, sourceType, fileName, hash, metadata);
        var suppliedExtension = Path.GetExtension(fileName).ToLowerInvariant();
        var quarantineObject = await _storage.WriteImmutableAsync(
            businessUnitId, "quarantine", hash, suppliedExtension, bytes, ct);

        FileInspectionResult inspection;
        await using (var content = new MemoryStream(bytes, writable: false))
            inspection = await _inspection.InspectAsync(
                new FileInspectionRequest(content, fileName, DeclaredLength: bytes.LongLength), ct);

        var selectedObject = inspection.IsCleared
            ? await _storage.WriteImmutableAsync(
                businessUnitId, "cleared", hash, suppliedExtension, bytes, ct)
            : quarantineObject;

        await using var transaction = await _context.Database.BeginTransactionAsync(ct);
        if (_context.Database.IsNpgsql())
        {
            var lockIdentity = $"evidence-ingest:{businessUnitId}:{hash}";
            await _context.Database.ExecuteSqlInterpolatedAsync(
                $"SELECT pg_advisory_xact_lock(hashtextextended({lockIdentity}, 0))", ct);
        }

        var corpus = await _context.Set<DocumentCorpus>()
            .SingleOrDefaultAsync(x => x.BusinessUnitId == businessUnitId && x.BatchId == actualBatchId, ct);
        if (corpus is null)
        {
            corpus = DocumentCorpus.Create(businessUnitId, actualBatchId, MapSourceType(sourceType));
            _context.Add(corpus);
            _context.Add(new LeadIngestionBatch
            {
                Id = actualBatchId,
                BusinessUnitId = businessUnitId,
                SourceChannel = sourceType.ToString(),
                CreatedBy = "document-ingestion",
                CreatedAtUtc = DateTimeOffset.UtcNow,
                UpdatedAtUtc = DateTimeOffset.UtcNow
            });
            await _context.SaveChangesAsync(ct);
        }

        var source = await _context.Set<SourceDocument>()
            .SingleOrDefaultAsync(x => x.BusinessUnitId == businessUnitId && x.ContentHash == hash, ct);
        if (source is null)
        {
            source = SourceDocument.Create(
                businessUnitId,
                corpus.Id,
                hash,
                fileName,
                inspection.DetectedContentType ?? "application/octet-stream",
                selectedObject.Bucket,
                selectedObject.Key,
                selectedObject.Version,
                bytes.LongLength);
            source.MarkSecurityStatus(MapSecurityStatus(inspection.Status));
            _context.Add(source);
            await _context.SaveChangesAsync(ct);
        }
        else if (inspection.IsCleared
                 && source.SecurityStatus == DocumentSecurityStatus.Quarantined
                 && await CanReleaseAfterScannerRecoveryAsync(source.Id, ct))
        {
            source.ReleaseFromQuarantine(
                selectedObject.Bucket,
                selectedObject.Key,
                selectedObject.Version);
            _log.LogInformation(
                "Released source document {SourceDocumentId} for tenant {BusinessUnitId} after a clean security rescan.",
                source.Id, businessUnitId);
            await _context.SaveChangesAsync(ct);
        }

        var occurrenceKey = SourceOccurrenceIdentity.BuildKey(actualBatchId, sourceType, metadata);
        var occurrence = await _context.Set<SourceDocumentOccurrence>()
            .SingleOrDefaultAsync(x => x.BusinessUnitId == businessUnitId && x.IdempotencyKey == occurrenceKey, ct);
        if (occurrence is null)
        {
            occurrence = SourceDocumentOccurrence.Create(
                businessUnitId,
                source.Id,
                corpus.Id,
                occurrenceKey,
                BuildSourceMetadata(fileName, sourceType, metadata, inspection,
                    quarantineObject, selectedObject));
            occurrence.SetLogicalGroup(metadata?.LogicalGroupKey);
            _context.Add(occurrence);
            await _context.SaveChangesAsync(ct);
        }
        else if (occurrence.SourceDocumentId != source.Id)
        {
            throw new InvalidOperationException("The intake idempotency key is already bound to different document content.");
        }

        var sourceIsCleared = source.SecurityStatus == DocumentSecurityStatus.Cleared;
        if (!inspection.IsCleared || !sourceIsCleared)
        {
            if (corpus.Status is CorpusStatus.Received or CorpusStatus.Processing)
                corpus.RequireReview();
            var rejectedInspection = sourceIsCleared || !inspection.IsCleared
                ? inspection
                : new FileInspectionResult(
                    source.SecurityStatus == DocumentSecurityStatus.Rejected
                        ? FileInspectionStatus.Rejected
                        : FileInspectionStatus.Quarantined,
                    source.DetectedMimeType,
                    source.ByteSize,
                    "The authoritative source document has not passed security inspection.",
                    "evidence-ledger",
                    null);
            var detailsJson = JsonSerializer.Serialize(new
            {
                status = rejectedInspection.Status.ToString(),
                reason = rejectedInspection.Reason,
                scanner = rejectedInspection.ScannerEngine,
                signature = rejectedInspection.ScannerSignature,
                retryable = rejectedInspection.IsRetryable
            });
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
        var enqueue = await _queue.EnqueueAsync(new EnqueueExtractionRequest
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
        if (!source.ExtractionJobId.HasValue)
            source.BindExtractionJob(enqueue.JobId);
        if (corpus.Status == CorpusStatus.Received)
            corpus.StartProcessing();
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
                    && inspection.TryGetProperty("ScannerSignature", out var signature)
                    && signature.ValueKind == JsonValueKind.String
                    && !string.IsNullOrWhiteSpace(signature.GetString()))
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
        FileInspectionResult inspection,
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
        inspection = new
        {
            status = inspection.Status.ToString(),
            malwareStatus = inspection.MalwareStatus?.ToString(),
            inspection.DetectedContentType,
            inspection.Reason,
            inspection.ScannerEngine,
            inspection.ScannerSignature,
            inspection.IsRetryable,
            inspection.ErrorCode
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
