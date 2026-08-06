using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ERP_RFQ_Automation.DocumentIntelligence.Persistence;
using ERP_RFQ_Automation.Extraction;
using ERP_RFQ_Automation.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using MimeKit;

namespace ERP_RFQ_Automation.Ingestion.Triage;

/// <param name="Outcome">The gate's decision, or "Legacy" for mail ingested before the gate existed.</param>
/// <param name="ReasonCodes">Stable snake_case codes; the UI renders them as chips.</param>
/// <param name="LinkedBatchId">The extraction batch this message produced, when it produced one.</param>
/// <param name="AttachmentNames">Null when unknown — never an empty array, which would claim
/// "this message provably had no attachments" for a message nobody looked inside.</param>
/// <param name="BodySubmitted">True when the body text itself was sent for extraction.</param>
/// <param name="LeadId">The lead this message produced, when it produced one.</param>
/// <param name="SkippedAttachments">ING-06: "filename (reason)" for every attachment that was
/// NOT handed to extraction. Empty when nothing was skipped. A durable record nobody can see is
/// only half a fix, and this screen is the one place a human goes to find lost mail.</param>
public sealed record EmailTriageRow(
    long Id,
    DateTime ReceivedOn,
    string? From,
    string? Subject,
    string Outcome,
    IReadOnlyList<string> ReasonCodes,
    bool HasAttachments,
    Guid? LinkedBatchId,
    string? ParseStatus,
    DateTime? TriageDecidedOn,
    int? AttachmentCount,
    IReadOnlyList<string>? AttachmentNames,
    bool BodySubmitted,
    long? LeadId,
    IReadOnlyList<string> SkippedAttachments);

public sealed record EmailTriagePage(
    int PageNumber, int PageSize, int TotalCount, IReadOnlyList<EmailTriageRow> Items);

/// <param name="Enqueued">Jobs created by the replay. Zero means nothing extractable was found.</param>
/// <param name="Status">The ingest's resulting ParseStatus.</param>
public sealed record EmailTriageReprocessResult(
    long Id, Guid BatchId, int Enqueued, string Outcome, string Status);

public interface IEmailTriageService
{
    Task<EmailTriagePage> ListAsync(
        long businessUnitId, string? outcome, int page, int pageSize, CancellationToken ct = default);

    /// <summary>
    /// Replays a stored message through ingestion as an INQUIRY-eligible message, whatever the
    /// gate originally decided. This is the guarantee that a rejected email is never
    /// unreachable: the raw .eml is retained for every message, so any triage decision can be
    /// overturned by a human without asking the customer to resend.
    /// </summary>
    Task<EmailTriageReprocessResult> ReprocessAsync(
        long businessUnitId, long id, string actor, string reason, string idempotencyKey,
        CancellationToken ct = default);
}

public sealed class EmailTriageService : IEmailTriageService
{
    private readonly ErpRfqAutomationContext _context;
    private readonly IDocumentIngestion _ingestion;
    private readonly ILogger<EmailTriageService> _log;

    /// <summary>Emails larger than this are not opened just to count attachments.</summary>
    private const long MaxEmlInspectionBytes = 10 * 1024 * 1024;

    public EmailTriageService(
        ErpRfqAutomationContext context, IDocumentIngestion ingestion, ILogger<EmailTriageService> log)
    {
        _context = context;
        _ingestion = ingestion;
        _log = log;
    }

    public async Task<EmailTriagePage> ListAsync(
        long businessUnitId, string? outcome, int page, int pageSize, CancellationToken ct = default)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 100);

        var query = _context.EmailIngests.AsNoTracking()
            .Where(e => e.EmailConfiguration.BusinessUnitId == businessUnitId);

        if (!string.IsNullOrWhiteSpace(outcome))
        {
            var wanted = outcome.Trim();
            query = wanted.Equals("Legacy", StringComparison.OrdinalIgnoreCase)
                ? query.Where(e => e.TriageOutcome == null)
                : query.Where(e => e.TriageOutcome == wanted);
        }

        var total = await query.CountAsync(ct);
        var rows = await query
            .OrderByDescending(e => e.CreatedOn).ThenByDescending(e => e.Id)
            .Skip((page - 1) * pageSize).Take(pageSize)
            .Select(e => new
            {
                e.Id,
                e.CreatedOn,
                e.FromEmail,
                e.EmailSubject,
                e.TriageOutcome,
                e.TriageReasonJson,
                e.TriageDecidedOn,
                e.ParseStatus,
                e.RawEmailPath,
                e.MessageId,
                e.SkippedAttachmentsJson
            })
            .ToListAsync(ct);

        // One round trip for every message on the page: the extraction occurrences share the
        // message's logical group key, and each carries its job (hence the batch).
        var groupKeys = rows.Select(r => $"email:{r.MessageId}").ToList();
        var occurrences = groupKeys.Count == 0
            ? new List<(string Key, long? JobId)>()
            : (await _context.Set<SourceDocumentOccurrence>().AsNoTracking()
                    .Where(o => o.BusinessUnitId == businessUnitId
                        && o.LogicalGroupKey != null && groupKeys.Contains(o.LogicalGroupKey))
                    .Select(o => new { o.LogicalGroupKey, o.ExtractionJobId })
                    .ToListAsync(ct))
                .Select(o => (Key: o.LogicalGroupKey!, JobId: o.ExtractionJobId))
                .ToList();

        var jobIds = occurrences.Where(o => o.JobId.HasValue).Select(o => o.JobId!.Value).Distinct().ToList();
        var jobs = jobIds.Count == 0
            ? new List<(long Id, Guid BatchId, string? FileName)>()
            : (await _context.Set<ExtractionJob>().AsNoTracking()
                    .Where(j => j.BusinessUnitId == businessUnitId && jobIds.Contains(j.Id))
                    .Select(j => new { j.Id, j.BatchId, j.FileName })
                    .ToListAsync(ct))
                .Select(j => (j.Id, j.BatchId, j.FileName))
                .ToList();
        var jobsById = jobs.ToDictionary(j => j.Id);

        var ingestIds = rows.Select(r => r.Id).ToList();
        var leadIdByIngest = ingestIds.Count == 0
            ? new Dictionary<long, long>()
            : (await _context.Leads.AsNoTracking()
                    .Where(l => l.BusinessUnitId == businessUnitId
                        && l.EmailIngestsId != null && ingestIds.Contains(l.EmailIngestsId.Value))
                    .Select(l => new { IngestId = l.EmailIngestsId!.Value, l.Id })
                    .ToListAsync(ct))
                .GroupBy(x => x.IngestId)
                .ToDictionary(g => g.Key, g => g.Min(x => x.Id));

        var items = new List<EmailTriageRow>(rows.Count);
        foreach (var row in rows)
        {
            var key = $"email:{row.MessageId}";
            var messageJobs = occurrences
                .Where(o => o.Key == key && o.JobId.HasValue && jobsById.ContainsKey(o.JobId!.Value))
                .Select(o => jobsById[o.JobId!.Value])
                .ToList();

            // Attachment names are known only for messages that produced jobs. For a message
            // that was STOPPED — the ones this screen exists for — the stored .eml is opened
            // instead, because "rejected, and it had an attachment" is the single most
            // important fact on the page.
            List<string>? attachmentNames = null;
            int? attachmentCount = null;
            if (messageJobs.Count > 0)
            {
                attachmentNames = messageJobs
                    .Where(j => !IsBodyDocument(j.FileName))
                    .Select(j => j.FileName ?? "(unnamed)")
                    .ToList();
                attachmentCount = attachmentNames.Count;
            }
            else
            {
                attachmentCount = CountRawEmailAttachments(row.RawEmailPath);
            }

            items.Add(new EmailTriageRow(
                Id: row.Id,
                ReceivedOn: row.CreatedOn,
                From: row.FromEmail,
                Subject: row.EmailSubject,
                Outcome: row.TriageOutcome ?? "Legacy",
                ReasonCodes: ParseReasonCodes(row.TriageReasonJson),
                HasAttachments: attachmentCount is > 0,
                LinkedBatchId: messageJobs.Count > 0 ? messageJobs[0].BatchId : null,
                ParseStatus: row.ParseStatus,
                TriageDecidedOn: row.TriageDecidedOn,
                AttachmentCount: attachmentCount,
                AttachmentNames: attachmentNames,
                BodySubmitted: messageJobs.Any(j => IsBodyDocument(j.FileName)),
                LeadId: leadIdByIngest.TryGetValue(row.Id, out var leadId) ? leadId : null,
                SkippedAttachments: ParseSkippedAttachments(row.SkippedAttachmentsJson)));
        }

        return new EmailTriagePage(page, pageSize, total, items);
    }

    public async Task<EmailTriageReprocessResult> ReprocessAsync(
        long businessUnitId, long id, string actor, string reason, string idempotencyKey,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(reason))
            throw new ArgumentException("A reason is required to reprocess a message.", nameof(reason));
        if (string.IsNullOrWhiteSpace(idempotencyKey))
            throw new ArgumentException("An idempotency key is required.", nameof(idempotencyKey));

        var ingest = await _context.EmailIngests
            .Include(e => e.EmailConfiguration)
            .FirstOrDefaultAsync(e => e.Id == id && e.EmailConfiguration.BusinessUnitId == businessUnitId, ct)
            ?? throw new KeyNotFoundException($"Email ingest {id} was not found for this tenant.");

        if (string.IsNullOrWhiteSpace(ingest.RawEmailPath) || !File.Exists(ingest.RawEmailPath))
            throw new InvalidOperationException(
                "The stored raw message is no longer available, so it cannot be reprocessed.");

        MimeMessage message;
        await using (var stream = File.OpenRead(ingest.RawEmailPath))
        {
            message = await MimeMessage.LoadAsync(stream, ct);
        }

        var parts = EmailBodyNormalizer.Normalize(GetBodyText(message));
        // FORCED Uncertain: a human overrode the gate, so the message is extracted and flagged
        // — never re-judged by the same rules that stopped it, and never silently promoted to
        // a clean Inquiry either.
        var decision = new EmailTriageDecision(
            EmailTriageOutcome.Uncertain,
            new[] { EmailTriageReasonCodes.ManualReprocess },
            CommercialDocumentTypeHint: null,
            ThreadContinuation: !string.IsNullOrWhiteSpace(message.InReplyTo) || message.References?.Count > 0);

        var result = await EmailIngestEnqueuer.EnqueueAsync(
            message, ingest, businessUnitId, ingest.EmailConfiguration.EmailAddress,
            _ingestion, decision, parts, _log, ct);

        ingest.TriageOutcome = EmailTriageOutcome.Uncertain.ToString();
        ingest.TriageReasonJson = JsonSerializer.Serialize(decision.ReasonCodes);
        ingest.TriageDecidedOn = DateTime.UtcNow;
        ingest.ParseStatus = result.Queued > 0 ? "Queued" : "Failed - nothing to extract";
        ingest.ParsedAt = result.Queued > 0 ? null : DateTime.UtcNow;
        await _context.SaveChangesAsync(ct);

        _log.LogInformation(
            "Email ingest {IngestId} reprocessed as an inquiry by {Actor} (idempotency {Key}): "
            + "{Queued} job(s) enqueued in batch {BatchId}. Reason: {Reason}",
            ingest.Id, actor, idempotencyKey, result.Queued, result.BatchId, reason);

        return new EmailTriageReprocessResult(
            ingest.Id, result.BatchId, result.Queued,
            EmailTriageOutcome.Uncertain.ToString(), ingest.ParseStatus!);
    }

    private static bool IsBodyDocument(string? fileName)
        => fileName is not null && fileName.EndsWith("_body.txt", StringComparison.OrdinalIgnoreCase);

    /// <summary>ING-06: the same tolerant JSON-array reader as the reason codes. A corrupt column
    /// must read as "nothing recorded" rather than 500 the one page an operator opens to find
    /// mail the system dropped.</summary>
    internal static IReadOnlyList<string> ParseSkippedAttachments(string? json) => ParseReasonCodes(json);

    internal static IReadOnlyList<string> ParseReasonCodes(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return Array.Empty<string>();
        try
        {
            return JsonSerializer.Deserialize<string[]>(json) ?? Array.Empty<string>();
        }
        catch (JsonException)
        {
            return Array.Empty<string>();
        }
    }

    /// <summary>
    /// How many attachments a message that produced NO extraction jobs was carrying. Returns
    /// null — "not known" — rather than 0 when the stored message cannot be opened, so the
    /// screen never states an absence it did not verify.
    /// </summary>
    private int? CountRawEmailAttachments(string? rawEmailPath)
    {
        if (string.IsNullOrWhiteSpace(rawEmailPath)) return null;
        try
        {
            var info = new FileInfo(rawEmailPath);
            if (!info.Exists || info.Length > MaxEmlInspectionBytes) return null;
            using var stream = File.OpenRead(rawEmailPath);
            var message = MimeMessage.Load(stream);
            return message.Attachments.Count();
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Could not inspect stored message {Path} for attachments.", rawEmailPath);
            return null;
        }
    }

    private static string GetBodyText(MimeMessage message)
    {
        var text = message.GetTextBody(MimeKit.Text.TextFormat.Plain);
        if (!string.IsNullOrWhiteSpace(text)) return text;
        var html = message.GetTextBody(MimeKit.Text.TextFormat.Html);
        return html is null
            ? string.Empty
            : System.Text.RegularExpressions.Regex.Replace(html, "<.*?>", " ")
                .Replace("&nbsp;", " ")
                .Replace("\r\n", "\n");
    }
}
