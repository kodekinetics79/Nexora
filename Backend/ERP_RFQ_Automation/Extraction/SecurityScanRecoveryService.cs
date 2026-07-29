using System.Text.Json;
using ERP_RFQ_Automation.DocumentIntelligence.Persistence;
using ERP_RFQ_Automation.Infrastructure.Storage;
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
    IReadOnlyList<SecurityScanRetryItem> Items);

public interface ISecurityScanRecoveryService
{
    Task<SecurityScanRetryResult> RetryBatchAsync(
        long businessUnitId,
        Guid batchId,
        CancellationToken ct = default);
}

public sealed class SecurityScanRecoveryService : ISecurityScanRecoveryService
{
    private const int MaximumBatchFiles = 50;
    private const int MaximumFileBytes = 25 * 1024 * 1024;
    private readonly ErpRfqAutomationContext _db;
    private readonly IEvidenceObjectStorage _storage;
    private readonly IDocumentIngestion _ingestion;

    public SecurityScanRecoveryService(
        ErpRfqAutomationContext db,
        IEvidenceObjectStorage storage,
        IDocumentIngestion ingestion)
    {
        _db = db;
        _storage = storage;
        _ingestion = ingestion;
    }

    public async Task<SecurityScanRetryResult> RetryBatchAsync(
        long businessUnitId,
        Guid batchId,
        CancellationToken ct = default)
    {
        var candidates = await (
            from occurrence in _db.Set<SourceDocumentOccurrence>().AsNoTracking()
            join corpus in _db.Set<DocumentCorpus>().AsNoTracking()
                on new { occurrence.BusinessUnitId, occurrence.CorpusId }
                equals new { corpus.BusinessUnitId, CorpusId = corpus.Id }
            join source in _db.Set<SourceDocument>().AsNoTracking()
                on new { occurrence.BusinessUnitId, occurrence.SourceDocumentId }
                equals new { source.BusinessUnitId, SourceDocumentId = source.Id }
            where occurrence.BusinessUnitId == businessUnitId
                  && corpus.BatchId == batchId
                  && (occurrence.IntakeStatus == IntakeOccurrenceStatus.AwaitingSecurityScan
                      || occurrence.IntakeStatus == IntakeOccurrenceStatus.Rejected)
            orderby occurrence.ReceivedOn, occurrence.Id
            select new
            {
                Occurrence = occurrence,
                Source = source
            }).Take(MaximumBatchFiles).ToListAsync(ct);

        var eligible = candidates.Where(x => IsRetryable(x.Occurrence)).ToArray();
        var items = new List<SecurityScanRetryItem>(eligible.Length);
        foreach (var candidate in eligible)
        {
            ct.ThrowIfCancellationRequested();
            var metadata = ParseMetadata(candidate.Occurrence.SourceMetadataJson);
            if (metadata is null)
            {
                items.Add(new(candidate.Occurrence.Id, candidate.Source.OriginalFileName,
                    "AwaitingSecurityScan", "invalid_quarantine_metadata", null));
                continue;
            }

            await using var stored = await _storage.OpenVerifiedReadAsync(
                metadata.StorageUri, candidate.Source.ContentHash, ct);
            var bytes = await ReadBoundedAsync(stored, ct);
            try
            {
                var ingested = await _ingestion.IngestAsync(
                    bytes,
                    metadata.FileName,
                    businessUnitId,
                    metadata.SourceType,
                    batchId,
                    metadata: metadata.Metadata,
                    ct: ct);
                items.Add(new(candidate.Occurrence.Id, metadata.FileName,
                    "Queued", null, ingested.JobId));
            }
            catch (DocumentInspectionException exception)
            {
                items.Add(new(candidate.Occurrence.Id, metadata.FileName,
                    exception.Inspection.IsRetryable ? "AwaitingSecurityScan" : "Rejected",
                    exception.Inspection.ErrorCode,
                    null));
            }
        }

        return new SecurityScanRetryResult(
            batchId,
            eligible.Length,
            items.Count(x => x.Status == "Queued"),
            items.Count(x => x.Status == "AwaitingSecurityScan"),
            items.Count(x => x.Status == "Rejected"),
            items);
    }

    private static bool IsRetryable(SourceDocumentOccurrence occurrence)
    {
        if (occurrence.IntakeStatus == IntakeOccurrenceStatus.AwaitingSecurityScan)
            return true;
        if (!string.Equals(occurrence.LastErrorCode, "document_quarantined", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(occurrence.LastErrorCode, "security_scanner_unavailable", StringComparison.OrdinalIgnoreCase))
            return false;

        try
        {
            using var metadata = JsonDocument.Parse(occurrence.SourceMetadataJson);
            var inspection = metadata.RootElement.GetProperty("inspection");
            return !inspection.TryGetProperty("ScannerSignature", out var signature)
                   || signature.ValueKind == JsonValueKind.Null
                   || string.IsNullOrWhiteSpace(signature.GetString());
        }
        catch (Exception exception) when (exception is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return false;
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

    private sealed record RecoveryMetadata(
        string FileName,
        ExtractionSourceType SourceType,
        string StorageUri,
        ExtractionJobMetadata? Metadata);
}
