using ERP_RFQ_Automation.Infrastructure.Storage;
using ERP_RFQ_Automation.LeadIdentity;
using ERP_RFQ_Automation.Models;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.Retention;

public sealed record LegacyCopyOutcome(long AttachmentId, string Path, string Result, long BytesFreed);

public sealed record LegacyPurgeReport(
    IReadOnlyList<LegacyCopyOutcome> Copies,
    int Unresolved,
    long BytesFreed)
{
    public static readonly LegacyPurgeReport Empty = new([], 0, 0);
}

/// <summary>
/// Finds and removes the NON content-addressed duplicates of a purged document.
///
/// <para>
/// Before the evidence ledger existed, four writers each kept their own copy of the same
/// bytes under a physical path with no hash and no link back to <c>source_documents</c>:
/// <c>RFQ_Attachments/</c> (email), <c>Leads_Folder_Attachments/</c> (watched folder),
/// <c>Manual_Attachments/</c> (upload), all recorded in <c>Attachments.FilePath</c>. An
/// email-borne PDF therefore occupies roughly 4x its own size on disk. Purging only the two
/// content-addressed zones would leave the largest share of the bytes — and a readable copy
/// of a document the tenant was told was deleted — sitting on the volume.
/// </para>
///
/// <para>
/// There is no hash index to join on, so the match is (lead, filename, exact byte size) and
/// it must be UNAMBIGUOUS. Anything else is reported as unresolved rather than guessed at:
/// deleting the wrong customer's attachment is unrecoverable, and silently skipping is how
/// a "space reclaimed" number becomes fiction. <c>Attachments</c> carries no immutability
/// trigger, so the row's path is rewritten to a tombstone marker — the row itself, its
/// filename and its size survive.
/// </para>
/// </summary>
public sealed class LegacyAttachmentPurgeResolver(ErpRfqAutomationContext db, IFileStorage files)
{
    public const string TombstonePrefix = "purged:";
    public const string ResultDeleted = "DELETED";
    public const string ResultAlreadyAbsent = "ALREADY_ABSENT";
    public const string ResultUnresolved = "LEGACY_COPY_UNRESOLVED";
    public const string ResultRefused = "REFUSED_UNSAFE_PATH";

    /// <summary>
    /// Resolves the legacy copies of one purged document. <paramref name="dryRun"/> reports
    /// exactly what would happen and touches nothing, so the confirmation screen's byte
    /// figure comes from the same code path that later does the deleting.
    /// </summary>
    public async Task<LegacyPurgeReport> PurgeCopiesAsync(
        long tenantId,
        long sourceDocumentId,
        string originalFileName,
        long byteSize,
        bool dryRun,
        CancellationToken ct)
    {
        var leadIds = await db.Set<LeadIngestionOccurrence>().AsNoTracking()
            .Where(x => x.BusinessUnitId == tenantId && x.SourceDocumentId == sourceDocumentId
                && x.LeadId.HasValue)
            .Select(x => x.LeadId!.Value)
            .Distinct()
            .ToListAsync(ct);
        if (leadIds.Count == 0)
            return LegacyPurgeReport.Empty;

        // Attachments are keyed by (ParentType, ParentId) and carry no tenant column of their
        // own, so the tenant boundary is the lead id set above — which came from a
        // tenant-filtered query. Never widen this to a filename-only lookup.
        var candidates = await db.Attachments
            .Where(x => x.ParentType == "Lead" && leadIds.Contains(x.ParentId)
                && x.FileName == originalFileName
                && !x.FilePath.StartsWith(TombstonePrefix))
            .ToListAsync(ct);

        // Size is part of the identity, not a sanity check: two files with the same name on
        // one lead are common (a revision), and only the one whose length matches the
        // evidence record is the copy of the document being purged.
        var matches = candidates.Where(x => x.FileSize == byteSize).ToList();
        if (matches.Count == 0)
            return candidates.Count == 0
                ? LegacyPurgeReport.Empty
                : new LegacyPurgeReport([], candidates.Count, 0);

        var outcomes = new List<LegacyCopyOutcome>();
        var unresolved = 0;
        long freed = 0;
        foreach (var attachment in matches)
        {
            string resolved;
            try
            {
                resolved = files.ResolvePath(attachment.FilePath);
            }
            catch (Exception exception) when (exception is UnauthorizedAccessException
                                                  or ArgumentException)
            {
                // A stored path that points outside the storage root is a finding, not a
                // file to delete. Report it and move on; never widen containment to
                // "reclaim" it.
                outcomes.Add(new LegacyCopyOutcome(attachment.Id, attachment.FilePath, ResultRefused, 0));
                unresolved++;
                continue;
            }

            var size = File.Exists(resolved) ? new FileInfo(resolved).Length : 0L;
            if (dryRun)
            {
                outcomes.Add(new LegacyCopyOutcome(attachment.Id, attachment.FilePath,
                    size > 0 ? ResultDeleted : ResultAlreadyAbsent, size));
                freed += size;
                continue;
            }

            var deleted = await files.TryDeleteAsync(attachment.FilePath, ct);
            if (deleted) freed += size;
            attachment.FilePath = $"{TombstonePrefix}source-document/{sourceDocumentId}";
            outcomes.Add(new LegacyCopyOutcome(attachment.Id, attachment.FilePath,
                deleted ? ResultDeleted : ResultAlreadyAbsent, deleted ? size : 0));
        }

        return new LegacyPurgeReport(outcomes, unresolved, freed);
    }

    /// <summary>
    /// Dry-run byte estimate across many documents at once, without opening a single file
    /// handle per candidate more than necessary. Used for the pre-purge disclosure figure.
    /// </summary>
    public async Task<long> EstimateLegacyBytesAsync(
        long tenantId, IReadOnlyCollection<long> sourceDocumentIds, CancellationToken ct)
    {
        if (sourceDocumentIds.Count == 0)
            return 0;

        var links = await db.Set<LeadIngestionOccurrence>().AsNoTracking()
            .Where(x => x.BusinessUnitId == tenantId && x.LeadId.HasValue
                && x.SourceDocumentId.HasValue
                && sourceDocumentIds.Contains(x.SourceDocumentId.Value))
            .Select(x => new { SourceDocumentId = x.SourceDocumentId!.Value, LeadId = x.LeadId!.Value })
            .Distinct()
            .ToListAsync(ct);
        if (links.Count == 0)
            return 0;

        var documents = await db.Set<DocumentIntelligence.Persistence.SourceDocument>().AsNoTracking()
            .Where(x => x.BusinessUnitId == tenantId && sourceDocumentIds.Contains(x.Id))
            .Select(x => new { x.Id, x.OriginalFileName, x.ByteSize })
            .ToListAsync(ct);

        var leadIds = links.Select(x => x.LeadId).Distinct().ToList();
        var attachments = await db.Attachments.AsNoTracking()
            .Where(x => x.ParentType == "Lead" && leadIds.Contains(x.ParentId)
                && !x.FilePath.StartsWith(TombstonePrefix))
            .Select(x => new { x.ParentId, x.FileName, x.FileSize })
            .ToListAsync(ct);

        long total = 0;
        foreach (var document in documents)
        {
            var documentLeads = links.Where(x => x.SourceDocumentId == document.Id)
                .Select(x => x.LeadId).ToHashSet();
            total += attachments
                .Where(x => documentLeads.Contains(x.ParentId)
                    && x.FileName == document.OriginalFileName
                    && x.FileSize == document.ByteSize)
                .Sum(x => x.FileSize ?? 0);
        }
        return total;
    }
}
