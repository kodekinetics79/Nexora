using System.Data;
using System.Net;
using System.Text.Json;
using Amazon.S3;
using ERP_RFQ_Automation.DocumentIntelligence.Persistence;
using ERP_RFQ_Automation.Infrastructure.Storage;
using ERP_RFQ_Automation.LeadIdentity;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Security.DocumentInspection;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.Extraction;

public sealed record ExtractionDeadLetterItem(
    long JobId,
    Guid BatchId,
    long? SourceDocumentOccurrenceId,
    string FileName,
    string SourceType,
    int Attempts,
    int MaxAttempts,
    string FailureCategory,
    DateTimeOffset CreatedOn,
    DateTimeOffset UpdatedOn,
    string Resolution,
    bool BlocksReadiness,
    /// <summary>
    /// What an operator must do about <see cref="FailureCategory"/>, or null when the
    /// category says it all. A fixed sentence per category from a closed set — never the
    /// stored <c>LastError</c>, which can quote the customer's document.
    /// </summary>
    string? OperatorAction = null);

public sealed record RecoverExtractionDeadLetterCommand(string Reason, string IdempotencyKey);

public sealed record RecoverExtractionDeadLetterResult(
    long JobId,
    Guid BatchId,
    string Status,
    bool BlocksReadiness,
    bool IdempotentReplay);

public sealed class ExtractionDeadLetterService(
    ErpRfqAutomationContext db,
    IEvidenceObjectStorage evidenceStorage,
    IMalwareScanner malwareScanner)
{
    private const int AdditionalAttemptsPerRecovery = 3;
    private const long MaximumRecoveryBytes = DocumentInspectionOptions.DefaultMaximumFileBytes;

    public async Task<IReadOnlyList<ExtractionDeadLetterItem>> ListAsync(
        long tenantId, CancellationToken ct)
    {
        var rows = await db.Set<ExtractionJob>().AsNoTracking()
            .Where(x => x.BusinessUnitId == tenantId && x.Status == ExtractionStatus.DeadLetter)
            .OrderByDescending(x => x.UpdatedOn)
            .Take(200)
            .Select(x => new
            {
                Job = x,
                Resolution = db.ExtractionDeadLetterEvents
                    .Where(e => e.BusinessUnitId == tenantId
                        && e.ExtractionJobId == x.Id
                        && e.AttemptNumber == x.Attempts)
                    .OrderByDescending(e => e.Id)
                    .Select(e => (ExtractionDeadLetterAction?)e.Action)
                    .FirstOrDefault(),
                SecurityBlocker = db.ExtractionDeadLetterEvents.Any(e =>
                    e.BusinessUnitId == tenantId
                    && e.ExtractionJobId == x.Id
                    && e.AttemptNumber == x.Attempts
                    && (e.Action == ExtractionDeadLetterAction.MalwareDetected
                        || e.Action == ExtractionDeadLetterAction.EvidenceIntegrityFailure))
            })
            .ToListAsync(ct);

        return rows
            .Where(x => x.SecurityBlocker || x.Resolution != ExtractionDeadLetterAction.SourceObjectUnavailable)
            .Select(x =>
            {
                var category = FailureCategory(x.Job.LastError);
                return new ExtractionDeadLetterItem(
                    x.Job.Id,
                    x.Job.BatchId,
                    x.Job.SourceDocumentOccurrenceId,
                    x.Job.FileName ?? "Unnamed document",
                    x.Job.SourceType.ToString(),
                    x.Job.Attempts,
                    x.Job.MaxAttempts,
                    category,
                    AsUtc(x.Job.CreatedOn),
                    AsUtc(x.Job.UpdatedOn),
                    x.Resolution?.ToString() ?? "Open",
                    x.SecurityBlocker || x.Resolution != ExtractionDeadLetterAction.SourceObjectUnavailable,
                    OperatorAction(category));
            })
            .ToArray();
    }

    public async Task<RecoverExtractionDeadLetterResult> RecoverAsync(
        long tenantId,
        long jobId,
        string actorId,
        RecoverExtractionDeadLetterCommand command,
        CancellationToken ct)
    {
        var reason = Required(command.Reason, 1000, nameof(command.Reason));
        var idempotencyKey = Required(command.IdempotencyKey, 128, nameof(command.IdempotencyKey));
        actorId = Required(actorId, 255, nameof(actorId));

        var replay = await ReplayAsync(tenantId, jobId, idempotencyKey, ct);
        if (replay is not null)
            return replay with { IdempotentReplay = true };

        var job = await db.Set<ExtractionJob>().AsNoTracking()
            .SingleOrDefaultAsync(x => x.BusinessUnitId == tenantId && x.Id == jobId, ct)
            ?? throw new KeyNotFoundException("Extraction job was not found.");
        if (job.Status != ExtractionStatus.DeadLetter)
            throw new InvalidOperationException("Only a dead-letter extraction job can be recovered.");
        var hasSecurityBlocker = await db.ExtractionDeadLetterEvents.AsNoTracking().AnyAsync(e =>
            e.BusinessUnitId == tenantId
            && e.ExtractionJobId == job.Id
            && e.AttemptNumber == job.Attempts
            && (e.Action == ExtractionDeadLetterAction.MalwareDetected
                || e.Action == ExtractionDeadLetterAction.EvidenceIntegrityFailure), ct);
        if (hasSecurityBlocker)
            throw new InvalidOperationException(
                "This job has an unresolved malware or evidence-integrity disposition and cannot be retried.");

        if (IsLegacySourcePathUnavailable(evidenceStorage, job.StoragePath))
        {
            await ProbeStorageAsync(ct);
            return await RecordDispositionAsync(tenantId, job, actorId, reason, idempotencyKey,
                ExtractionDeadLetterAction.SourceObjectUnavailable, ct);
        }

        Stream source;
        try
        {
            source = await evidenceStorage.OpenVerifiedReadAsync(job.StoragePath, job.ContentHash, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            await ProbeStorageAsync(ct);

            if (IsMissingSource(exception))
                return await RecordDispositionAsync(tenantId, job, actorId, reason, idempotencyKey,
                    ExtractionDeadLetterAction.SourceObjectUnavailable, ct);
            if (exception is InvalidDataException)
                return await RecordDispositionAsync(tenantId, job, actorId, reason, idempotencyKey,
                    ExtractionDeadLetterAction.EvidenceIntegrityFailure, ct);
            throw new InvalidOperationException(
                "The immutable source could not be verified. The job remains blocked.");
        }

        byte[] content;
        MalwareScanResult scan;
        await using (source)
        {
            content = await ReadBoundedAsync(source, ct);
            await using var scanStream = new MemoryStream(content, writable: false);
            scan = await malwareScanner.ScanAsync(scanStream, ct);
            if (scan.Status is MalwareScanStatus.Unavailable or MalwareScanStatus.Error)
                throw new DeadLetterDependencyUnavailableException(
                    "The malware scanner is unavailable. The job remains blocked.");
            if (scan.Status == MalwareScanStatus.Infected)
                return await RecordDispositionAsync(tenantId, job, actorId, reason, idempotencyKey,
                    ExtractionDeadLetterAction.MalwareDetected, ct, scan);
        }

        EvidenceObject clearedObject;
        try
        {
            clearedObject = await evidenceStorage.WriteImmutableAsync(
                tenantId, "cleared", job.ContentHash, RecoveryExtension(job), content, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            await ProbeStorageAsync(ct);
            throw new InvalidOperationException(
                "The verified source could not be promoted to durable cleared storage. The job remains blocked.");
        }

        return await QueueRetryAsync(tenantId, job, actorId, reason, idempotencyKey,
            new VerifiedRecoverySource(clearedObject, content.LongLength, scan), ct);
    }

    private async Task<RecoverExtractionDeadLetterResult> QueueRetryAsync(
        long tenantId,
        ExtractionJob snapshot,
        string actorId,
        string reason,
        string idempotencyKey,
        VerifiedRecoverySource verifiedSource,
        CancellationToken ct)
    {
        return await ExecuteMutationAsync(tenantId, snapshot.Id, async () =>
        {
            var replay = await ReplayAsync(tenantId, snapshot.Id, idempotencyKey, ct);
            if (replay is not null)
                return replay with { IdempotentReplay = true };

            var job = await db.Set<ExtractionJob>().SingleAsync(
                x => x.BusinessUnitId == tenantId && x.Id == snapshot.Id, ct);
            if (job.Status != ExtractionStatus.DeadLetter || job.Attempts != snapshot.Attempts)
                throw new InvalidOperationException("The dead-letter job changed. Refresh and retry.");
            await EnsureNoSecurityBlockerAsync(tenantId, job, ct);
            await EnsureIntakeLineageAsync(job, verifiedSource, actorId, ct);

            job.Status = ExtractionStatus.Pending;
            job.MaxAttempts = Math.Max(job.MaxAttempts, job.Attempts) + AdditionalAttemptsPerRecovery;
            job.NextAttemptAt = DateTime.UtcNow;
            job.LeasedBy = null;
            job.LeaseExpiresAt = null;
            job.LastError = null;
            job.UpdatedOn = DateTime.UtcNow;

            db.ExtractionDeadLetterEvents.Add(Event(job, actorId, reason, idempotencyKey,
                ExtractionDeadLetterAction.RetryQueued));
            await db.SaveChangesAsync(ct);
            return new(job.Id, job.BatchId, "RetryQueued", false, false);
        }, ct);
    }

    private async Task EnsureIntakeLineageAsync(
        ExtractionJob job,
        VerifiedRecoverySource verified,
        string actorId,
        CancellationToken ct)
    {
        if (job.SourceDocumentOccurrenceId is { } occurrenceId)
        {
            var existingOccurrence = await db.Set<SourceDocumentOccurrence>()
                .Include(x => x.SourceDocument)
                .SingleOrDefaultAsync(x =>
                    x.BusinessUnitId == job.BusinessUnitId && x.Id == occurrenceId, ct)
                ?? throw new InvalidOperationException(
                    "The extraction job references a missing intake occurrence and cannot be retried.");
            ApplyVerifiedSecurity(existingOccurrence.SourceDocument, verified);
            if (existingOccurrence.IntakeStatus == IntakeOccurrenceStatus.DeadLetter)
                existingOccurrence.RequeueDeadLetter(job.Id);
            else if (existingOccurrence.IntakeStatus is not (
                         IntakeOccurrenceStatus.Queued or IntakeOccurrenceStatus.Retryable))
                throw new InvalidOperationException(
                    $"The intake occurrence is {existingOccurrence.IntakeStatus} and cannot be retried.");
            job.StoragePath = verified.Object.StorageUri;
            return;
        }

        var corpus = await db.Set<DocumentCorpus>().SingleOrDefaultAsync(x =>
            x.BusinessUnitId == job.BusinessUnitId && x.BatchId == job.BatchId, ct);
        if (corpus is null)
        {
            corpus = DocumentCorpus.Create(job.BusinessUnitId, job.BatchId, MapSourceType(job.SourceType));
            db.Add(corpus);
        }
        if (!await db.Set<LeadIngestionBatch>().AnyAsync(x =>
                x.BusinessUnitId == job.BusinessUnitId && x.Id == job.BatchId, ct))
        {
            db.Add(new LeadIngestionBatch
            {
                Id = job.BatchId,
                BusinessUnitId = job.BusinessUnitId,
                SourceChannel = job.SourceType.ToString(),
                CreatedBy = actorId,
                CreatedAtUtc = DateTimeOffset.UtcNow,
                UpdatedAtUtc = DateTimeOffset.UtcNow,
            });
        }
        if (corpus.Id == 0)
            await db.SaveChangesAsync(ct);

        var source = await db.Set<SourceDocument>().SingleOrDefaultAsync(x =>
            x.BusinessUnitId == job.BusinessUnitId && x.ContentHash == job.ContentHash, ct);
        if (source is null)
        {
            source = SourceDocument.Create(
                job.BusinessUnitId,
                corpus.Id,
                job.ContentHash,
                job.FileName ?? $"recovered-job-{job.Id}",
                MimeType(job),
                verified.Object.Bucket,
                verified.Object.Key,
                verified.Object.Version,
                verified.ByteSize);
            db.Add(source);
            await db.SaveChangesAsync(ct);
        }
        ApplyVerifiedSecurity(source, verified);
        if (!source.ExtractionJobId.HasValue)
            source.BindExtractionJob(job.Id);

        var occurrenceKey = $"dead-letter-recovery:{job.BusinessUnitId}:{job.Id}";
        var occurrence = await db.Set<SourceDocumentOccurrence>().SingleOrDefaultAsync(x =>
            x.BusinessUnitId == job.BusinessUnitId && x.IdempotencyKey == occurrenceKey, ct);
        if (occurrence is null)
        {
            occurrence = SourceDocumentOccurrence.Create(
                job.BusinessUnitId,
                source.Id,
                corpus.Id,
                occurrenceKey,
                JsonSerializer.Serialize(new
                {
                    recoveredFromDeadLetter = true,
                    extractionJobId = job.Id,
                    job.FileName,
                    storageUri = verified.Object.StorageUri,
                    scanner = verified.Scan.Engine,
                    signature = verified.Scan.Signature,
                }));
            occurrence.RecordSecurityWork(reused: false, rerun: true);
            occurrence.RecordUploadResources(verified.ByteSize, 0, 0);
            occurrence.RecordActualCost(0m, 0m, "LOCAL_COMPUTE_UNPRICED");
            occurrence.BindExtractionJob(job.Id);
            db.Add(occurrence);
            await db.SaveChangesAsync(ct);
        }
        else if (occurrence.IntakeStatus == IntakeOccurrenceStatus.DeadLetter)
        {
            occurrence.RequeueDeadLetter(job.Id);
        }
        else if (occurrence.IntakeStatus is not (
                     IntakeOccurrenceStatus.Queued or IntakeOccurrenceStatus.Retryable))
        {
            throw new InvalidOperationException(
                $"The recovered intake occurrence is {occurrence.IntakeStatus} and cannot be retried.");
        }

        if (corpus.Status == CorpusStatus.Received)
            corpus.StartProcessing();
        job.SourceDocumentOccurrenceId = occurrence.Id;
        job.StoragePath = verified.Object.StorageUri;
    }

    private static void ApplyVerifiedSecurity(
        SourceDocument source, VerifiedRecoverySource verified)
    {
        if (source.SecurityStatus == DocumentSecurityStatus.Rejected)
            throw new InvalidOperationException(
                "The source document has a rejected security disposition and cannot be retried.");
        source.RecordMalwareVerdict(
            MalwareScanStatus.Clean, verified.Scan.Engine, verified.Scan.Signature);
        if (source.SecurityStatus is DocumentSecurityStatus.Pending or DocumentSecurityStatus.Quarantined)
            source.ReleaseFromQuarantine(
                verified.Object.Bucket, verified.Object.Key, verified.Object.Version);
    }

    private Task<RecoverExtractionDeadLetterResult> RecordDispositionAsync(
        long tenantId,
        ExtractionJob snapshot,
        string actorId,
        string reason,
        string idempotencyKey,
        ExtractionDeadLetterAction action,
        CancellationToken ct,
        MalwareScanResult? malwareVerdict = null)
        => ExecuteMutationAsync(tenantId, snapshot.Id, async () =>
        {
            var replay = await ReplayAsync(tenantId, snapshot.Id, idempotencyKey, ct);
            if (replay is not null)
                return replay with { IdempotentReplay = true };

            var job = await db.Set<ExtractionJob>().SingleAsync(
                x => x.BusinessUnitId == tenantId && x.Id == snapshot.Id, ct);
            if (job.Status != ExtractionStatus.DeadLetter || job.Attempts != snapshot.Attempts)
                throw new InvalidOperationException("The dead-letter job changed. Refresh and retry.");
            if (action is ExtractionDeadLetterAction.RetryQueued
                or ExtractionDeadLetterAction.SourceObjectUnavailable)
                await EnsureNoSecurityBlockerAsync(tenantId, job, ct);
            if (action is ExtractionDeadLetterAction.MalwareDetected
                or ExtractionDeadLetterAction.EvidenceIntegrityFailure)
                await RejectLinkedSourceAsync(tenantId, job, malwareVerdict, ct);
            db.ExtractionDeadLetterEvents.Add(Event(job, actorId, reason, idempotencyKey, action));
            await db.SaveChangesAsync(ct);
            return new(job.Id, job.BatchId, action.ToString(),
                action != ExtractionDeadLetterAction.SourceObjectUnavailable, false);
        }, ct);

    private async Task RejectLinkedSourceAsync(
        long tenantId,
        ExtractionJob job,
        MalwareScanResult? malwareVerdict,
        CancellationToken ct)
    {
        var occurrences = await db.Set<SourceDocumentOccurrence>()
            .Include(x => x.SourceDocument)
            .Where(x => x.BusinessUnitId == tenantId && x.ExtractionJobId == job.Id)
            .ToListAsync(ct);
        var source = occurrences.Select(x => x.SourceDocument).FirstOrDefault()
            ?? await db.Set<SourceDocument>().SingleOrDefaultAsync(x =>
                x.BusinessUnitId == tenantId && x.ExtractionJobId == job.Id, ct)
            ?? throw new InvalidOperationException(
                "The extraction job has no authoritative source lineage for security disposition.");

        source.RejectForSecurityFinding(malwareVerdict);
        var malwareDetected = malwareVerdict?.Status == MalwareScanStatus.Infected;
        var errorCode = malwareDetected ? "malware_detected" : "source_object_integrity_failed";
        var details = JsonSerializer.Serialize(new
        {
            errorCode,
            retryable = false,
            sourceDocumentId = source.Id,
            extractionJobId = job.Id
        });
        foreach (var occurrence in occurrences)
            occurrence.MarkTerminalSecurityFailure(malwareDetected, errorCode, details);
    }

    private async Task<RecoverExtractionDeadLetterResult?> ReplayAsync(
        long tenantId, long jobId, string idempotencyKey, CancellationToken ct)
    {
        var replay = await db.ExtractionDeadLetterEvents.AsNoTracking()
            .Where(x => x.BusinessUnitId == tenantId
                && x.ExtractionJobId == jobId
                && x.IdempotencyKey == idempotencyKey)
            .Select(x => new { x.ExtractionJobId, x.ExtractionJob.BatchId, x.Action })
            .SingleOrDefaultAsync(ct);
        return replay is null ? null : new RecoverExtractionDeadLetterResult(
            replay.ExtractionJobId, replay.BatchId, replay.Action.ToString(),
            replay.Action != ExtractionDeadLetterAction.SourceObjectUnavailable, true);
    }

    private async Task<T> ExecuteMutationAsync<T>(
        long tenantId, long jobId, Func<Task<T>> action, CancellationToken ct)
    {
        if (!db.Database.IsRelational())
            return await action();
        return await db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            db.ChangeTracker.Clear();
            await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
            if (db.Database.IsNpgsql())
            {
                var lockKey = $"dead-letter-recovery:{tenantId}:{jobId}";
                await db.Database.ExecuteSqlInterpolatedAsync(
                    $"SELECT pg_advisory_xact_lock(hashtextextended({lockKey}, 0))", ct);
            }
            var result = await action();
            await transaction.CommitAsync(ct);
            return result;
        });
    }

    private async Task ProbeStorageAsync(CancellationToken ct)
    {
        try
        {
            await evidenceStorage.ProbeAsync(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            throw new DeadLetterDependencyUnavailableException(
                "Evidence storage is unavailable. The job remains blocked.");
        }
    }

    private async Task EnsureNoSecurityBlockerAsync(
        long tenantId, ExtractionJob job, CancellationToken ct)
    {
        var blocked = await db.ExtractionDeadLetterEvents.AnyAsync(e =>
            e.BusinessUnitId == tenantId
            && e.ExtractionJobId == job.Id
            && e.AttemptNumber == job.Attempts
            && (e.Action == ExtractionDeadLetterAction.MalwareDetected
                || e.Action == ExtractionDeadLetterAction.EvidenceIntegrityFailure), ct);
        if (blocked)
            throw new InvalidOperationException(
                "This job has an unresolved malware or evidence-integrity disposition and cannot be retried.");
    }

    private static ExtractionDeadLetterEvent Event(
        ExtractionJob job,
        string actorId,
        string reason,
        string idempotencyKey,
        ExtractionDeadLetterAction action) => new()
        {
            BusinessUnitId = job.BusinessUnitId,
            ExtractionJobId = job.Id,
            AttemptNumber = job.Attempts,
            Action = action,
            Reason = reason,
            ActorId = actorId,
            IdempotencyKey = idempotencyKey,
            CreatedOn = DateTimeOffset.UtcNow
        };

    private static string FailureCategory(string? error) => ClassifyFailure(error);

    /// <summary>Tenant-facing name for a document refused because AI processing is not authorized.</summary>
    internal const string AiNotAuthorizedCategory = "AI_NOT_AUTHORIZED";

    /// <summary>The evidence record survives but its bytes do not — distinct from
    /// EVIDENCE_INTEGRITY, which means the bytes are present and altered.</summary>
    internal const string EvidenceMissingCategory = "EVIDENCE_MISSING";

    /// <summary>
    /// What the operator must DO about this category, in words, or null where the category
    /// alone is the whole story.
    ///
    /// <para>
    /// The raw <c>LastError</c> is deliberately still not on the DTO — it can quote document
    /// content. This is the opposite: a fixed string per category, chosen from a closed set,
    /// carrying no tenant data. Without it the AI refusal reached the screen as one
    /// underscored word with nothing an operator could act on.
    /// </para>
    /// </summary>
    internal static string? OperatorAction(string category) => category switch
    {
        AiNotAuthorizedCategory => ChunkedExtractionService.AiNotAuthorizedOperatorAction,
        "MALWARE" => "The stored file failed malware inspection. It cannot be retried until a "
            + "platform owner clears the disposition; recovery is blocked by design.",
        EvidenceMissingCategory => "The stored copy of this document is gone from evidence "
            + "storage, so nothing can be re-read from it — this is a storage-durability "
            + "incident, not a corrupt file. Retrying this job cannot succeed. For mail-borne "
            + "documents the original message is still retained: reprocess it from Inbound Mail "
            + "and the parts are rebuilt from the source email. Check that evidence storage is "
            + "backed by a persistent volume before reprocessing, or the bytes will vanish again.",
        "EVIDENCE_INTEGRITY" => "The stored bytes no longer match the hash recorded at intake. "
            + "Re-upload the document from its original source; retrying this copy cannot succeed.",
        "UNSUPPORTED_DOCUMENT" => "The file passed inspection but no reader in this deployment "
            + "can parse it. Ask the sender for a PDF, Word or spreadsheet version.",
        "PROCESSING_TIMEOUT" => "The model did not answer within the request timeout. Retrying is "
            + "worthwhile; if it repeats, the document is likely too large for the configured "
            + "output budget.",
        _ => null
    };

    /// <summary>
    /// The dead-letter failure vocabulary, shared with the metrics emission in
    /// <c>ExtractionWorker</c> so the <c>nexora.extraction.jobs.deadlettered</c> counter's
    /// <c>failure.category</c> tag and this API's <c>FailureCategory</c> field can never
    /// drift apart. The returned set is small and CLOSED, which is what makes it safe to
    /// use as a metric dimension — the raw error text never is.
    /// </summary>
    internal static string ClassifyFailure(string? error)
    {
        if (string.IsNullOrWhiteSpace(error)) return "UNCLASSIFIED";
        if (error.StartsWith("[EXTRACTION_INTAKE_", StringComparison.Ordinal))
            return "INTAKE_INVARIANT";
        // BEFORE the PROVIDER rule below, which the refusal text would otherwise match: on a
        // stock deploy this is the single most common failure — every PDF, email body and
        // scan — and it reported as generic EXTRACTION_FAILURE, indistinguishable from a
        // model timeout. Matched on the extractor's own closed marker rather than on prose,
        // and Contains rather than StartsWith because the worker prefixes the reader's
        // structured-fallback note onto the stored reason.
        if (error.Contains(ChunkedExtractionService.AiNotAuthorizedCode, StringComparison.Ordinal))
            return AiNotAuthorizedCategory;
        // BEFORE the integrity rule, whose prose this message also contains: a missing object
        // and an altered one are different incidents and only one of them is a corruption bug.
        if (error.Contains(EvidenceIntegrityException.ObjectMissingMarker, StringComparison.Ordinal))
            return EvidenceMissingCategory;
        if (error.Contains("integrity", StringComparison.OrdinalIgnoreCase)) return "EVIDENCE_INTEGRITY";
        if (error.Contains("malware", StringComparison.OrdinalIgnoreCase)) return "MALWARE";
        if (error.Contains("unsupported", StringComparison.OrdinalIgnoreCase)) return "UNSUPPORTED_DOCUMENT";
        if (error.Contains("timeout", StringComparison.OrdinalIgnoreCase)) return "PROCESSING_TIMEOUT";
        if (error.Contains("provider", StringComparison.OrdinalIgnoreCase)) return "PROCESSING_PROVIDER";
        return "EXTRACTION_FAILURE";
    }

    private static string Required(string? value, int maxLength, string name)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException($"{name} is required.", name);
        var trimmed = value.Trim();
        if (trimmed.Length > maxLength)
            throw new ArgumentException($"{name} exceeds {maxLength} characters.", name);
        return trimmed;
    }

    private static bool IsMissingSource(Exception exception) =>
        exception is FileNotFoundException or DirectoryNotFoundException
        || exception is AmazonS3Exception
        {
            StatusCode: HttpStatusCode.NotFound
        };

    private static async Task<byte[]> ReadBoundedAsync(Stream source, CancellationToken ct)
    {
        if (source.CanSeek && source.Length > MaximumRecoveryBytes)
            throw new InvalidOperationException(
                $"The source exceeds the {MaximumRecoveryBytes} byte recovery limit.");

        await using var copy = new MemoryStream();
        var buffer = new byte[81_920];
        while (true)
        {
            var read = await source.ReadAsync(buffer, ct);
            if (read == 0) break;
            if (copy.Length + read > MaximumRecoveryBytes)
                throw new InvalidOperationException(
                    $"The source exceeds the {MaximumRecoveryBytes} byte recovery limit.");
            await copy.WriteAsync(buffer.AsMemory(0, read), ct);
        }
        return copy.ToArray();
    }

    private static string RecoveryExtension(ExtractionJob job)
    {
        var extension = Path.GetExtension(job.FileName ?? string.Empty);
        if (!string.IsNullOrWhiteSpace(extension)) return extension;
        return string.IsNullOrWhiteSpace(job.FileType) ? string.Empty : job.FileType;
    }

    private static CorpusSourceType MapSourceType(ExtractionSourceType sourceType) => sourceType switch
    {
        ExtractionSourceType.Email => CorpusSourceType.Email,
        ExtractionSourceType.ManualUpload => CorpusSourceType.ManualUpload,
        ExtractionSourceType.Folder => CorpusSourceType.Folder,
        ExtractionSourceType.ExcelTemplate => CorpusSourceType.Import,
        _ => CorpusSourceType.Api,
    };

    private static string MimeType(ExtractionJob job) =>
        (Path.GetExtension(job.FileName ?? string.Empty).ToLowerInvariant(),
            job.FileType?.Trim().ToLowerInvariant()) switch
        {
            (".pdf", _) or (_, "pdf") => "application/pdf",
            (".doc", _) or (_, "doc") => "application/msword",
            (".docx", _) or (_, "docx") =>
                "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
            (".xls", _) or (_, "xls") => "application/vnd.ms-excel",
            (".xlsx", _) or (".xlsm", _) or (_, "xlsx") or (_, "xlsm") =>
                "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            (".csv", _) or (_, "csv") => "text/csv",
            (".txt", _) or (_, "txt") => "text/plain",
            (".jpg", _) or (".jpeg", _) or (_, "jpg") or (_, "jpeg") => "image/jpeg",
            (".png", _) or (_, "png") => "image/png",
            (".tif", _) or (".tiff", _) or (_, "tif") or (_, "tiff") => "image/tiff",
            _ => "application/octet-stream",
        };

    internal static bool IsLegacySourcePathUnavailable(
        IEvidenceObjectStorage storage, string storagePath) =>
        storage is S3EvidenceObjectStorage
        && !storagePath.StartsWith("s3://", StringComparison.OrdinalIgnoreCase);

    private static DateTimeOffset AsUtc(DateTime value) =>
        new(DateTime.SpecifyKind(value, DateTimeKind.Utc));

    private sealed record VerifiedRecoverySource(
        EvidenceObject Object,
        long ByteSize,
        MalwareScanResult Scan);
}

public sealed class DeadLetterDependencyUnavailableException(string message) : Exception(message)
{
}
