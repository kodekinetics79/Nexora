using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ERP_RFQ_Automation.Ingestion.Triage;
using ERP_RFQ_Automation.Security.DocumentInspection;
using MimeKit;

namespace ERP_RFQ_Automation.Services.DocumentIntelligence;

/// <summary>Flattened view of an email container, ready to become a
/// <c>DocumentExtractionInput</c>.</summary>
/// <param name="Text">Header block, the sender's own words, then each attachment's extracted text.</param>
/// <param name="Notes">User-safe sentences for everything NOT taken. Empty means nothing was
/// dropped; a non-empty list is surfaced to the reviewer, never only to a log.</param>
public sealed record EmailContainerReadResult(
    string Text,
    IReadOnlyList<string> Notes,
    int AttachmentsExtracted,
    int AttachmentsRefused);

/// <summary>
/// The ONE routine that turns an uploaded <c>.eml</c> or <c>.msg</c> into extractable text.
///
/// <para>
/// AN EMAIL IS NOT A TEXT FILE. It has a body AND attachments, and the RFQ is usually in the
/// attachment. Flattening the container to prose would have lost exactly the document the sender
/// meant to send. So this reader does what the mailbox door does: it takes the sender's OWN words
/// (via <see cref="EmailBodyNormalizer"/> — the same normaliser the IMAP path uses, so a message
/// that arrives by mail and the same message uploaded as a file are read identically) and then
/// takes each attachment as its own document.
/// </para>
///
/// <para>
/// WHERE THIS DIFFERS FROM THE MAIL DOOR, AND WHY. <c>EmailIngestEnqueuer</c> creates one
/// extraction JOB per attachment. It can, because it runs at intake with the ingestion gateway in
/// hand. This runs inside <c>ProductionDocumentReader</c>, downstream of intake, inside the
/// worker's claimed lease — enqueuing there would mean nesting a second ingestion transaction
/// inside the lease transaction on the same DbContext. So attachments are extracted INLINE into
/// one document instead: nothing is lost, and every attachment's content reaches the extractor.
/// The per-attachment job fan-out needs a hook at the ingestion gateway and is reported as a seam
/// rather than forced through here.
/// </para>
///
/// <para>
/// SECURITY — an email must never become an inspection bypass. Every attachment is decoded and
/// then put through the SAME <see cref="IFileInspectionService"/> the top-level file went
/// through: extension allow-list, magic-byte typing, OOXML/OLE structure checks, macro refusal,
/// expansion caps and the malware scanner. An attachment that does not clear is NOT extracted and
/// its refusal is recorded. With no inspection service available the reader fails CLOSED: the
/// attachment is refused, never extracted unscanned. Nested email containers recurse only to
/// <see cref="MaxContainerDepth"/>, and the count and total size of attachments are capped, so a
/// message-in-a-message-in-a-message cannot be used to make the parser do unbounded work.
/// </para>
///
/// <para>
/// NOTES CARRY NO FILENAMES. <see cref="EmailContainerReadResult.Notes"/> reaches the reviewer as
/// product copy, and attachment names come from inside an untrusted file. Notes therefore name
/// attachments by ORDINAL and quote only sizes, fixed sentences, and reasons Nexora itself
/// authored (an inspection <c>Reason</c>). The filename does appear in the extracted body text,
/// which is document content rather than an authoritative Nexora sentence.
/// </para>
/// </summary>
public static class EmailContainerReader
{
    /// <summary>Container nesting allowed: the uploaded message, plus one level of forwarded
    /// message. Deeper is refused out loud — depth is unbounded by construction, and each level is
    /// another parser handed untrusted bytes.</summary>
    public const int MaxContainerDepth = 2;

    /// <summary>Attachments processed per message.</summary>
    public const int MaxAttachments = 25;

    /// <summary>Total decoded attachment bytes processed across the whole container tree.</summary>
    public const long MaxTotalAttachmentBytes = 64L * 1024 * 1024;

    /// <summary>Largest single attachment taken — the intake inspection limit.</summary>
    public const long MaxAttachmentBytes = 25L * 1024 * 1024;

    /// <summary>Extension-neutral marker so a reviewer can see where the mail stops and a
    /// document starts.</summary>
    private const string BodyBanner = "[EMAIL MESSAGE BODY]";

    /// <summary>Parses <c>.eml</c> bytes with MimeKit — already referenced for IMAP/SMTP.</summary>
    public static ParsedEmailMessage ParseEml(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        using var stream = new MemoryStream(bytes, writable: false);
        var message = MimeMessage.Load(stream);

        var notes = new List<string>();
        var attachments = new List<ParsedEmailAttachment>();
        var ordinal = 0;
        foreach (var entity in message.Attachments)
        {
            ordinal++;
            if (attachments.Count >= MaxAttachments)
            {
                continue;
            }

            if (entity is MessagePart nested)
            {
                using var nestedStream = new MemoryStream();
                nested.Message.WriteTo(nestedStream);
                attachments.Add(new ParsedEmailAttachment(
                    ordinal,
                    OutlookMsgReader.SafeFileName(entity.ContentDisposition?.FileName, ordinal) + ".eml",
                    nestedStream.ToArray(),
                    IsEmbeddedMessage: true));
                continue;
            }

            if (entity is not MimePart part)
            {
                notes.Add($"Attachment {ordinal} is not a file Nexora can take out of the message and was not processed.");
                continue;
            }

            using var content = new MemoryStream();
            try
            {
                part.Content.DecodeTo(content);
            }
            catch (Exception exception) when (exception is IOException or FormatException or NotSupportedException)
            {
                notes.Add($"Attachment {ordinal} could not be decoded from the message and was not processed.");
                continue;
            }

            if (content.Length == 0)
            {
                notes.Add($"Attachment {ordinal} is empty and was not processed.");
                continue;
            }
            if (content.Length > MaxAttachmentBytes)
            {
                notes.Add($"Attachment {ordinal} is larger than the "
                          + $"{MaxAttachmentBytes / (1024 * 1024)} MB limit and was not processed.");
                continue;
            }

            var fileName = OutlookMsgReader.SafeFileName(part.FileName, ordinal);
            attachments.Add(new ParsedEmailAttachment(
                ordinal, fileName, content.ToArray(),
                IsEmbeddedMessage: fileName.EndsWith(".eml", StringComparison.OrdinalIgnoreCase)
                                   || fileName.EndsWith(".msg", StringComparison.OrdinalIgnoreCase)));
        }

        if (ordinal > MaxAttachments)
        {
            notes.Add($"This message carries more than {MaxAttachments} attachments; "
                      + $"{ordinal - MaxAttachments} beyond that limit were not processed.");
        }

        return new ParsedEmailMessage(
            Subject: NullIfBlank(message.Subject),
            From: NullIfBlank(message.From.ToString()),
            To: NullIfBlank(message.To.ToString()),
            Date: message.Date == default ? null : message.Date.ToString("yyyy-MM-dd HH:mm zzz"),
            PlainBody: NullIfBlank(message.GetTextBody(MimeKit.Text.TextFormat.Plain)),
            HtmlBody: NullIfBlank(message.GetTextBody(MimeKit.Text.TextFormat.Html)),
            Attachments: attachments,
            Notes: notes);
    }

    /// <summary>Parses <c>.msg</c> bytes with the in-house compound-file reader.</summary>
    public static ParsedEmailMessage ParseMsg(byte[] bytes) => OutlookMsgReader.Read(bytes);

    /// <summary>
    /// Flattens a parsed message: header block, the sender's fresh words, then each attachment's
    /// extracted text. <paramref name="extractAttachmentText"/> is the caller's ordinary
    /// single-document reader (PDF/DOCX/XLSX/image/HTML/…); this routine never parses a document
    /// format itself, so an attachment is read by exactly the same code an upload of that same
    /// file would take.
    /// </summary>
    public static async Task<EmailContainerReadResult> FlattenAsync(
        ParsedEmailMessage message,
        IFileInspectionService? inspection,
        Func<string, string, byte[], CancellationToken, Task<string>> extractAttachmentText,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(extractAttachmentText);

        var budget = new Budget();
        var notes = new List<string>();
        var text = new StringBuilder();
        var extracted = 0;
        var refused = 0;

        await AppendAsync(message, inspection, extractAttachmentText, text, notes, budget,
            depth: 1, counters: (count) => { extracted += count.Extracted; refused += count.Refused; },
            cancellationToken);

        return new EmailContainerReadResult(text.ToString().TrimEnd(), notes, extracted, refused);
    }

    private sealed class Budget
    {
        public long AttachmentBytes;
    }

    private readonly record struct Counters(int Extracted, int Refused);

    private static async Task AppendAsync(
        ParsedEmailMessage message,
        IFileInspectionService? inspection,
        Func<string, string, byte[], CancellationToken, Task<string>> extractAttachmentText,
        StringBuilder text,
        List<string> notes,
        Budget budget,
        int depth,
        Action<Counters> counters,
        CancellationToken cancellationToken)
    {
        foreach (var note in message.Notes) notes.Add(note);

        // ---- header + the sender's own words -------------------------------------------
        AppendHeader(text, message, depth);

        var rawBody = !string.IsNullOrWhiteSpace(message.PlainBody)
            ? message.PlainBody!
            : !string.IsNullOrWhiteSpace(message.HtmlBody)
                ? HtmlDocumentTextExtractor.ExtractText(message.HtmlBody!)
                : string.Empty;

        // The SAME normaliser the IMAP door uses. A forwarded RFQ thread contains the original
        // request two or three times; submitting the whole body invents duplicate line items.
        var parts = EmailBodyNormalizer.Normalize(rawBody);
        if (!string.IsNullOrWhiteSpace(parts.Fresh))
        {
            text.AppendLine(parts.Fresh.Trim());
        }
        else if (!string.IsNullOrWhiteSpace(parts.Quoted))
        {
            // Uploaded messages differ from polled ones in one way that matters: for a message
            // that arrived by IMAP the earlier thread already came through the mailbox, so
            // dropping the quote loses nothing. For an UPLOADED file the quoted thread may be the
            // only copy Nexora will ever see, and returning nothing would be silent loss.
            text.AppendLine("[QUOTED THREAD — the sender added no new text above the forwarded message]");
            text.AppendLine(parts.Quoted.Trim());
            notes.Add("The sender added no new text to this message, so the quoted thread below "
                      + "the forward was read instead.");
        }
        else if (message.Attachments.Count == 0)
        {
            notes.Add("This message carries no readable body text and no attachments.");
        }

        // ---- attachments ----------------------------------------------------------------
        var extracted = 0;
        var refused = 0;
        var processed = 0;

        foreach (var attachment in message.Attachments)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (processed >= MaxAttachments)
            {
                refused++;
                notes.Add($"Only the first {MaxAttachments} attachments of this message were processed.");
                break;
            }
            processed++;

            if (budget.AttachmentBytes + attachment.Content.LongLength > MaxTotalAttachmentBytes)
            {
                refused++;
                notes.Add($"Attachment {attachment.Ordinal} was not processed because this message's "
                          + $"attachments exceed the {MaxTotalAttachmentBytes / (1024 * 1024)} MB total limit.");
                continue;
            }
            budget.AttachmentBytes += attachment.Content.LongLength;

            var extension = Path.GetExtension(attachment.FileName).ToLowerInvariant();

            // A message inside a message: recurse, but only to the declared depth.
            if (attachment.IsEmbeddedMessage || extension is ".eml" or ".msg")
            {
                if (depth >= MaxContainerDepth)
                {
                    refused++;
                    notes.Add($"Attachment {attachment.Ordinal} is an email forwarded inside a forwarded "
                              + "email. Nexora opens one level of forwarding; save that message and "
                              + "upload it on its own to have it read.");
                    continue;
                }

                // A nested message is inspected exactly like any other attachment BEFORE it is
                // parsed — the recursion must not become the bypass either.
                var nestedInspection = await InspectAsync(inspection, attachment, extension, cancellationToken);
                if (nestedInspection is null)
                {
                    refused++;
                    notes.Add($"Attachment {attachment.Ordinal} was not read because security scanning "
                              + "was unavailable. It is stored with the message and can be retried.");
                    continue;
                }
                if (!nestedInspection.Value.Cleared)
                {
                    refused++;
                    notes.Add($"Attachment {attachment.Ordinal} was not processed. {nestedInspection.Value.Reason}");
                    continue;
                }

                ParsedEmailMessage nested;
                try
                {
                    nested = extension == ".msg" ? ParseMsg(attachment.Content) : ParseEml(attachment.Content);
                }
                catch (Exception exception) when (exception is OleCompoundFileException or FormatException
                                                      or IOException or NotSupportedException)
                {
                    refused++;
                    notes.Add($"Attachment {attachment.Ordinal} is a forwarded email Nexora could not read; "
                              + "ask the sender to resend it, or save its attachments and upload them directly.");
                    continue;
                }

                text.AppendLine();
                text.AppendLine($"[FORWARDED MESSAGE {attachment.Ordinal}: {attachment.FileName}]");
                await AppendAsync(nested, inspection, extractAttachmentText, text, notes, budget,
                    depth + 1, inner => { extracted += inner.Extracted; refused += inner.Refused; },
                    cancellationToken);
                extracted++;
                continue;
            }

            // The extension gate is the SAME shared allow-list every intake door uses. The
            // sentence deliberately does not echo the extension: it comes from inside an
            // untrusted file and notes render verbatim as product copy.
            if (!DocumentIntakeAllowList.IsAllowed(extension))
            {
                refused++;
                notes.Add($"Attachment {attachment.Ordinal} is not a file type Nexora accepts and was not read.");
                continue;
            }

            var result = await InspectAsync(inspection, attachment, extension, cancellationToken);
            if (result is null)
            {
                // Fail CLOSED. Extracting an unscanned attachment out of an email is precisely the
                // inspection bypass this whole path has to be incapable of.
                refused++;
                notes.Add($"Attachment {attachment.Ordinal} was not read because security scanning "
                          + "was unavailable. It is stored with the message and can be retried.");
                continue;
            }
            if (!result.Value.Cleared)
            {
                refused++;
                notes.Add($"Attachment {attachment.Ordinal} was not read. {result.Value.Reason}");
                continue;
            }

            string attachmentText;
            try
            {
                attachmentText = await extractAttachmentText(
                    attachment.FileName, extension.TrimStart('.'), attachment.Content, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception)
            {
                // A parser exception on ONE attachment must never lose the message or the other
                // attachments. It becomes a visible refusal, not an unhandled failure.
                refused++;
                notes.Add($"Attachment {attachment.Ordinal} cleared security checks but could not be read; "
                          + "it may be damaged or password-protected. Ask the sender for a readable copy.");
                continue;
            }

            if (string.IsNullOrWhiteSpace(attachmentText))
            {
                refused++;
                notes.Add($"Attachment {attachment.Ordinal} was read but contained no text to extract.");
                continue;
            }

            extracted++;
            text.AppendLine();
            text.AppendLine($"[ATTACHMENT {attachment.Ordinal}: {attachment.FileName}]");
            text.AppendLine(attachmentText.Trim());
        }

        counters(new Counters(extracted, refused));
    }

    private static void AppendHeader(StringBuilder text, ParsedEmailMessage message, int depth)
    {
        if (depth == 1) text.AppendLine(BodyBanner);
        if (message.Subject is { } subject) text.AppendLine($"Subject: {subject}");
        if (message.From is { } from) text.AppendLine($"From: {from}");
        if (message.To is { } to) text.AppendLine($"To: {to}");
        if (message.Date is { } date) text.AppendLine($"Date: {date}");
        text.AppendLine();
    }

    private readonly record struct InspectionOutcome(bool Cleared, string Reason);

    private static async Task<InspectionOutcome?> InspectAsync(
        IFileInspectionService? inspection,
        ParsedEmailAttachment attachment,
        string extension,
        CancellationToken cancellationToken)
    {
        if (inspection is null) return null;

        try
        {
            await using var stream = new MemoryStream(attachment.Content, writable: false);
            var result = await inspection.InspectAsync(
                new FileInspectionRequest(stream, attachment.FileName,
                    DeclaredLength: attachment.Content.LongLength),
                cancellationToken);
            return new InspectionOutcome(result.IsCleared, result.Reason);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return null; // caller fails closed and says so
        }
    }

    private static string? NullIfBlank(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
