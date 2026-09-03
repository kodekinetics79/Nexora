using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using ERP_RFQ_Automation.Infrastructure.Storage;
using ERP_RFQ_Automation.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using MimeKit;

namespace ERP_RFQ_Automation.Ingestion.Assembly;

public interface IRawEmailEvidenceReader
{
    /// <summary>
    /// Loads the original message for an ingest, or null when no copy survives.
    /// Prefers the durable object-storage copy; falls back to the legacy local path only for
    /// rows written before the assembly existed.
    /// </summary>
    Task<MimeMessage?> TryLoadAsync(long businessUnitId, EmailIngest ingest, CancellationToken ct = default);
}

/// <summary>
/// The one place that answers "give me the original message again".
///
/// <para>Two sources exist and they are NOT equal. The
/// <see cref="EmailInquiryAssembly.RawEvidenceUri"/> is the authoritative production copy,
/// content-addressed and read back against its own SHA-256. <see cref="EmailIngest.RawEmailPath"/>
/// is a container-local path from before object storage carried inbound mail; it survives only
/// as long as that disk does, and it is consulted second and never written again.</para>
///
/// <para>Keeping the fallback is deliberate: messages ingested before this change have no
/// assembly, and the triage screen and the reprocess endpoint must keep working for them
/// rather than reporting historic mail as lost.</para>
/// </summary>
public sealed class RawEmailEvidenceReader : IRawEmailEvidenceReader
{
    private readonly ErpRfqAutomationContext _context;
    private readonly IEvidenceObjectStorage _evidence;
    private readonly ILogger<RawEmailEvidenceReader> _logger;

    public RawEmailEvidenceReader(
        ErpRfqAutomationContext context,
        IEvidenceObjectStorage evidence,
        ILogger<RawEmailEvidenceReader> logger)
    {
        _context = context;
        _evidence = evidence;
        _logger = logger;
    }

    public async Task<MimeMessage?> TryLoadAsync(
        long businessUnitId, EmailIngest ingest, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(ingest);

        var assembly = await _context.EmailInquiryAssemblies.AsNoTracking()
            .Where(x => x.BusinessUnitId == businessUnitId && x.EmailIngestId == ingest.Id)
            .Select(x => new { x.RawEvidenceUri, x.RawEvidenceSha256 })
            .FirstOrDefaultAsync(ct);

        if (assembly?.RawEvidenceUri is { Length: > 0 } uri
            && assembly.RawEvidenceSha256 is { Length: > 0 } sha)
        {
            // OpenVerifiedReadAsync re-hashes on the way out, so a silently corrupted or
            // substituted object fails here rather than being reprocessed as if it were the
            // message the customer sent.
            await using var stream = await _evidence.OpenVerifiedReadAsync(uri, sha, ct);
            return await MimeMessage.LoadAsync(stream, ct);
        }

        // The compatibility copy itself may already be an object (legacy writers routed to the
        // object store, or a row the migration job re-pointed): its key carries the digest.
        if (EvidenceObjectUris.IsObjectUri(ingest.RawEmailPath)
            && EvidenceObjectUris.TryParseDigest(ingest.RawEmailPath, out var keyDigest))
        {
            await using var objectStream = await _evidence.OpenVerifiedReadAsync(ingest.RawEmailPath!, keyDigest, ct);
            return await MimeMessage.LoadAsync(objectStream, ct);
        }

        if (!string.IsNullOrWhiteSpace(ingest.RawEmailPath) && File.Exists(ingest.RawEmailPath))
        {
            _logger.LogDebug(
                "Reading the raw message for ingest {IngestId} from the legacy local path; "
                + "this row predates durable inbound-email evidence.", ingest.Id);
            await using var stream = File.OpenRead(ingest.RawEmailPath);
            return await MimeMessage.LoadAsync(stream, ct);
        }

        return null;
    }
}
