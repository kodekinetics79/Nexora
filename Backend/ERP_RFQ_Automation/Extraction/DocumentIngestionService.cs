using System;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging;
using ERP_RFQ_Automation.Infrastructure.Storage;

namespace ERP_RFQ_Automation.Extraction;

/// <summary>Result of ingesting one document through the unified gateway.</summary>
public sealed class IngestedDocument
{
    public long JobId { get; init; }
    public Guid BatchId { get; init; }
    public string ContentHash { get; init; } = null!;
    public string StoragePath { get; init; } = null!;
    public EnqueueOutcome Outcome { get; init; }
    public ExtractionStatus? ExistingStatus { get; init; }
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
    private readonly IFileStorage _storage;

    public DocumentIngestionService(
        IExtractionQueue queue,
        IFileStorage storage,
        ILogger<DocumentIngestionService> log)
    {
        _queue = queue;
        _log = log;
        _storage = storage;
    }

    public async Task<IngestedDocument> IngestAsync(
        byte[] bytes,
        string fileName,
        long businessUnitId,
        ExtractionSourceType sourceType,
        Guid? batchId = null,
        int priority = 0,
        ExtractionJobMetadata? metadata = null,
        CancellationToken ct = default)
    {
        if (bytes is null || bytes.Length == 0)
            throw new ArgumentException("Cannot ingest an empty document.", nameof(bytes));
        if (businessUnitId <= 0)
            throw new ArgumentException("A valid businessUnitId is required.", nameof(businessUnitId));

        var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        var storagePath = await PersistImmutableAsync(bytes, hash, fileName, ct);

        // Provenance sidecar BEFORE enqueue so a worker can never claim the job and
        // miss the metadata. Best-effort by contract.
        if (metadata != null)
            await metadata.SaveAsync(storagePath, businessUnitId, ct);

        var fileType = Path.GetExtension(fileName).TrimStart('.').ToLowerInvariant();
        var enqueue = await _queue.EnqueueAsync(new EnqueueExtractionRequest
        {
            BusinessUnitId = businessUnitId,
            SourceType = sourceType,
            StoragePath = storagePath,
            FileName = fileName,
            FileType = string.IsNullOrEmpty(fileType) ? null : fileType,
            ContentHash = hash,
            BatchId = batchId,
            Priority = priority
        }, ct);

        _log.LogInformation(
            "Ingested {FileName} ({Bytes} bytes) for BU {BU} via {Source}: job {JobId} ({Outcome}).",
            fileName, bytes.Length, businessUnitId, sourceType, enqueue.JobId, enqueue.Outcome);

        return new IngestedDocument
        {
            JobId = enqueue.JobId,
            BatchId = enqueue.BatchId,
            ContentHash = enqueue.ContentHash,
            StoragePath = storagePath,
            Outcome = enqueue.Outcome,
            ExistingStatus = enqueue.ExistingStatus
        };
    }

    /// <summary>
    /// Writes the raw bytes to an immutable, content-addressed path
    /// (Uploads/Extraction/&lt;aa&gt;/&lt;sha256&gt;&lt;ext&gt;). Identical bytes reuse the same
    /// file — a re-upload never rewrites or mutates existing content. (Moved verbatim from
    /// ExtractionController so every door shares one storage layout.)
    /// </summary>
    private async Task<string> PersistImmutableAsync(byte[] bytes, string hash, string originalName, CancellationToken ct)
    {
        var ext = Path.GetExtension(originalName);
        var shard = hash[..2];
        return await _storage.WriteImmutableAsync(
            Path.Combine("Extraction", shard, hash + ext), bytes, ct);
    }
}
