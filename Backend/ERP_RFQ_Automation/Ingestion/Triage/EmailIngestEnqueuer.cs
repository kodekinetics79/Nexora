using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ERP_RFQ_Automation.Extraction;
using ERP_RFQ_Automation.Models;
using Microsoft.Extensions.Logging;
using MimeKit;

namespace ERP_RFQ_Automation.Ingestion.Triage;

/// <param name="BatchId">The batch every job of this message shares.</param>
/// <param name="Queued">Number of jobs enqueued (duplicates count — they are handled work).</param>
/// <param name="SkippedAttachments">"filename (reason)" for every attachment that could not be enqueued.</param>
public sealed record EmailEnqueueResult(Guid BatchId, int Queued, IReadOnlyList<string> SkippedAttachments);

/// <summary>
/// The ONE routine that turns an email into extraction jobs: one job per supported
/// attachment plus one job for the sender's fresh body text, all sharing a batch id and a
/// provenance sidecar that names the real <see cref="EmailIngest"/>.
///
/// It is shared deliberately. The mailbox poller and the manual "reprocess as inquiry"
/// endpoint MUST fan out identically — a replayed message that took a different path would
/// not be a replay, and the difference would only ever be discovered on the message someone
/// was trying to rescue.
/// </summary>
public static class EmailIngestEnqueuer
{
    public const long MaxAttachmentBytes = 25 * 1024 * 1024;

    public static async Task<EmailEnqueueResult> EnqueueAsync(
        MimeMessage message,
        EmailIngest ingest,
        long businessUnitId,
        string? clientEmail,
        IDocumentIngestion ingestion,
        EmailTriageDecision triage,
        EmailBodyParts bodyParts,
        ILogger logger,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(ingest);
        ArgumentNullException.ThrowIfNull(ingestion);
        ArgumentNullException.ThrowIfNull(triage);
        ArgumentNullException.ThrowIfNull(bodyParts);

        var batchId = Guid.NewGuid();
        var queued = 0;
        var skippedAttachments = new List<string>();
        var messageKey = message.MessageId ?? ingest.Id.ToString();

        var metadata = new ExtractionJobMetadata
        {
            SourceOccurrenceId = $"email:{messageKey}:body",
            LogicalGroupKey = $"email:{messageKey}",
            EmailIngestId = ingest.Id,
            FromEmail = message.From.Mailboxes.FirstOrDefault()?.Address,
            Subject = message.Subject ?? "",
            SourceReceivedAtUtc = message.Date,
            ClientEmail = clientEmail,
            LeadSource = "Email",
            EmailSource = "Text Only",
            // An email BODY is conversational prose, not a document.
            BodyShape = "prose",
            TriageOutcome = triage.Outcome.ToString(),
            TriageReasonCodes = triage.ReasonCodes,
            ThreadContinuation = triage.ThreadContinuation,
            CommercialDocumentTypeHint = triage.CommercialDocumentTypeHint
        };

        void RecordSkippedAttachment(string fileName, string reason)
        {
            skippedAttachments.Add($"{fileName} ({reason})");
            logger.LogWarning("Skipping email attachment {FileName} for ingest {IngestId}: {Reason}.",
                fileName, ingest.Id, reason);
        }

        // 1) Attachments first, so the body job's provenance can carry the complete skip list.
        var attachmentOrdinal = 0;
        foreach (var att in message.Attachments)
        {
            ct.ThrowIfCancellationRequested();
            attachmentOrdinal++;
            if (att is not MimePart part)
            {
                RecordSkippedAttachment(
                    att.ContentDisposition?.FileName ?? $"attachment #{attachmentOrdinal}",
                    "embedded email message is not ingested");
                continue;
            }
            if (part.FileName == null)
            {
                RecordSkippedAttachment($"attachment #{attachmentOrdinal}", "attachment has no filename");
                continue;
            }
            var ext = Path.GetExtension(part.FileName).ToLowerInvariant();
            if (!ERP_RFQ_Automation.Services.EmailService.IsSupportedExtension(ext))
            {
                RecordSkippedAttachment(part.FileName, $"unsupported file type '{ext}'");
                continue;
            }

            try
            {
                using var ms = new MemoryStream();
                await part.Content.DecodeToAsync(ms, ct);
                if (ms.Length == 0)
                {
                    RecordSkippedAttachment(part.FileName, "attachment content is empty");
                    continue;
                }
                if (ms.Length > MaxAttachmentBytes)
                {
                    RecordSkippedAttachment(part.FileName,
                        $"exceeds the {MaxAttachmentBytes / (1024 * 1024)} MB size limit ({ms.Length} bytes)");
                    continue;
                }

                var attachmentMetadata = new ExtractionJobMetadata
                {
                    SourceOccurrenceId = $"email:{messageKey}:attachment:{attachmentOrdinal}",
                    LogicalGroupKey = metadata.LogicalGroupKey,
                    EmailIngestId = ingest.Id,
                    FromEmail = metadata.FromEmail,
                    Subject = metadata.Subject,
                    SourceReceivedAtUtc = metadata.SourceReceivedAtUtc,
                    ClientEmail = metadata.ClientEmail,
                    LeadSource = "Email",
                    EmailSource = GetFileTypeLabel(ext),
                    // The commercial hint is inherited (a supplier's quotation PDF is still a
                    // supplier quotation) but BodyShape is NOT: an attachment is a document
                    // and keeps the structured routing.
                    CommercialDocumentTypeHint = triage.CommercialDocumentTypeHint,
                    TriageOutcome = triage.Outcome.ToString(),
                    TriageReasonCodes = triage.ReasonCodes,
                    ThreadContinuation = triage.ThreadContinuation
                };
                var attachmentResult = await ingestion.IngestAsync(
                    ms.ToArray(), part.FileName, businessUnitId, ExtractionSourceType.Email,
                    batchId, priority: 0, attachmentMetadata, ct);
                queued++;
                logger.LogInformation("Enqueued attachment {FileName} as job {JobId} ({Outcome}) for ingest {IngestId}.",
                    part.FileName, attachmentResult.JobId, attachmentResult.Outcome, ingest.Id);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Poison-attachment isolation: continue with the remaining documents.
                logger.LogError(ex, "Failed to enqueue attachment {FileName} for ingest {IngestId}.",
                    part.FileName, ingest.Id);
            }
        }

        // 2) The body — the sender's OWN words only. A body that is nothing but a quoted
        //    thread produces no job; its attachments (above) still do.
        try
        {
            var body = bodyParts.Fresh;
            if (string.IsNullOrWhiteSpace(body))
            {
                logger.LogInformation(
                    "Email body for ingest {IngestId} carried no fresh text after quote stripping "
                    + "(thread depth {Depth}); body job skipped.", ingest.Id, bodyParts.ThreadDepth);
            }
            else
            {
                if (skippedAttachments.Count > 0)
                    metadata.SkippedAttachments = skippedAttachments.ToArray();
                var bodyDocument =
                    $"Subject: {message.Subject}\nFrom: {message.From}\nDate: {message.Date:yyyy-MM-dd}\n\n{body}";
                var bodyName = $"{SanitizeFileName(message.Subject ?? "email")}_body.txt";
                var bodyResult = await ingestion.IngestAsync(
                    Encoding.UTF8.GetBytes(bodyDocument), bodyName, businessUnitId,
                    ExtractionSourceType.Email, batchId, priority: 0, metadata, ct);
                queued++;
                logger.LogInformation("Enqueued email body as job {JobId} ({Outcome}) for ingest {IngestId}.",
                    bodyResult.JobId, bodyResult.Outcome, ingest.Id);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Failed to enqueue email body for ingest {IngestId}.", ingest.Id);
        }

        return new EmailEnqueueResult(batchId, queued, skippedAttachments);
    }

    /// <summary>File-type label used for Lead.EmailSource. Single owner — the email door and
    /// the reprocess endpoint must label identical bytes identically.</summary>
    internal static string GetFileTypeLabel(string ext) => ext switch
    {
        ".pdf" => "PDF",
        ".doc" or ".docx" => "Word",
        ".xls" or ".xlsx" or ".xlsm" => "Excel",
        ".csv" => "CSV",
        ".txt" => "Text",
        ".jpg" or ".jpeg" => "JPEG",
        ".png" => "PNG",
        ".bmp" => "BMP",
        ".gif" => "GIF",
        ".tif" or ".tiff" => "TIFF",
        ".webp" => "WebP",
        _ => "Unknown"
    };

    internal static string SanitizeFileName(string fileName)
        => string.Join("_", fileName.Split(Path.GetInvalidFileNameChars(),
            StringSplitOptions.RemoveEmptyEntries)).Replace(" ", "_");
}
