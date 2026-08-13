using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MimeKit;

namespace ERP_RFQ_Automation.Ingestion.Assembly;

/// <summary>Stable, machine-readable reasons a planned component will not be extracted.</summary>
public static class EmailInquirySkipReasons
{
    public const string UnsupportedFileType = "unsupported_file_type";
    public const string AttachmentEmpty = "attachment_empty";
    public const string AttachmentOversize = "attachment_oversize";
    public const string AttachmentUnnamed = "attachment_unnamed";
    public const string AttachmentUnreadable = "attachment_unreadable";
    public const string NestingLimitExceeded = "nesting_limit_exceeded";
    public const string ComponentLimitExceeded = "component_limit_exceeded";
    public const string TotalSizeLimitExceeded = "total_size_limit_exceeded";
    public const string EmbeddedMessageUnreadable = "embedded_message_unreadable";
}

/// <summary>Whether a planned component will be handed to inspection and extraction.</summary>
public enum EmailInquiryComponentDisposition
{
    /// <summary>Goes through the inspection boundary and into extraction.</summary>
    Process = 0,

    /// <summary>Recorded with its reason and immediately terminal. Never silently dropped —
    /// the row exists so the loss is visible on the message it arrived with.</summary>
    Skip = 1
}

/// <summary>
/// Declared boundaries of a single message. They exist so that a hostile or pathological
/// message terminates at a stated limit rather than by exhausting memory or the stack.
/// </summary>
public sealed record EmailInquiryLimits
{
    /// <summary>Nested <c>message/rfc822</c> depth. 0 is a part of the received message itself.
    /// Three levels covers "customer forwarded a forward of the original enquiry", which is
    /// ordinary; beyond that is a mail bomb or a loop.</summary>
    public int MaxNestingDepth { get; init; } = 3;

    /// <summary>Total components planned for one message, across all nesting levels.</summary>
    public int MaxComponents { get; init; } = 50;

    /// <summary>Per-component ceiling. Matches the pre-existing attachment limit so the change
    /// does not quietly start refusing files that were accepted yesterday.</summary>
    public long MaxComponentBytes { get; init; } = 25L * 1024 * 1024;

    /// <summary>Ceiling on the sum of all planned components — the zip-bomb analogue for mail:
    /// fifty separately-legal attachments are not a legal message.</summary>
    public long MaxTotalBytes { get; init; } = 100L * 1024 * 1024;

    public static EmailInquiryLimits Default { get; } = new();
}

/// <param name="ComponentKey">Stable idempotency identity; equals the extraction
/// <c>SourceOccurrenceId</c>.</param>
/// <param name="Content">
/// The decoded bytes. Held in memory only for the duration of capture — they are written to
/// evidence storage and handed to the queue, never retained on the entity.
/// </param>
public sealed record EmailInquiryComponentPlan(
    string ComponentKey,
    EmailInquiryComponentKind Kind,
    int Ordinal,
    string? FileName,
    string? MimeType,
    long ByteSize,
    string ContentHash,
    EmailInquiryComponentDisposition Disposition,
    string? ReasonCode,
    string? ReasonDetail,
    int NestingDepth,
    ReadOnlyMemory<byte> Content);

/// <param name="Components">
/// EVERY part the message was found to contain, skipped ones included. The count is what the
/// barrier waits for, so a part that will not be processed still gets a row — otherwise "we
/// dropped it" and "it was never there" would be the same observation.
/// </param>
/// <param name="QuotedOnlyBody">
/// True when the body carried no fresh text after quote stripping. Not a skip and not a
/// component: an ordinary reply in a thread. Recorded so the assembly can say so.
/// </param>
public sealed record EmailInquiryManifest(
    string MessageKey,
    IReadOnlyList<EmailInquiryComponentPlan> Components,
    bool QuotedOnlyBody)
{
    public int ExpectedComponentCount => Components.Count;

    /// <summary>Components that will actually be inspected and extracted.</summary>
    public IEnumerable<EmailInquiryComponentPlan> Processable
        => Components.Where(c => c.Disposition == EmailInquiryComponentDisposition.Process);
}

/// <summary>
/// Walks the MIME tree ONCE and decides, up front, exactly what a message is expected to
/// produce.
///
/// <para>This is deliberately separate from enqueueing. The previous design discovered the
/// message's shape as a side effect of enqueueing it — the count of things to wait for was
/// whatever happened to succeed, so a part that failed to enqueue reduced the expectation
/// instead of holding it. Deciding the manifest first, and writing it in one transaction with
/// the assembly, is what makes a crash mid-enqueue recoverable: the rows say four parts were
/// expected and only two have results, which is a hold, not a completed message.</para>
///
/// <para>The walk is deterministic: the same message always yields the same keys in the same
/// order, so a replay after a restart lands on the same component rows.</para>
/// </summary>
public static class EmailInquiryManifestPlanner
{
    public static async Task<EmailInquiryManifest> PlanAsync(
        MimeMessage message,
        string messageKey,
        string? freshBodyText,
        EmailInquiryLimits? limits = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentException.ThrowIfNullOrWhiteSpace(messageKey);
        limits ??= EmailInquiryLimits.Default;

        var components = new List<EmailInquiryComponentPlan>();
        var ordinal = 0;
        long totalBytes = 0;

        // 1) The body, and only when the sender actually wrote something. A quoted-only reply
        //    produces NO body component: expected stays at whatever the attachments contribute,
        //    so a bare "thanks" with nothing attached ends as NoInquiry rather than sitting in a
        //    reviewer's queue forever.
        var quotedOnly = string.IsNullOrWhiteSpace(freshBodyText);
        if (!quotedOnly)
        {
            var bodyDocument =
                $"Subject: {message.Subject}\nFrom: {message.From}\nDate: {message.Date:yyyy-MM-dd}\n\n{freshBodyText}";
            var bytes = Encoding.UTF8.GetBytes(bodyDocument);
            totalBytes += bytes.Length;
            components.Add(new EmailInquiryComponentPlan(
                $"email:{messageKey}:body",
                EmailInquiryComponentKind.Body,
                ordinal++,
                $"{SanitizeFileName(message.Subject ?? "email")}_body.txt",
                "text/plain",
                bytes.Length,
                Sha256(bytes),
                EmailInquiryComponentDisposition.Process,
                null, null, 0, bytes));
        }

        // 2) Attachments, in message order, including embedded messages.
        await WalkAsync(message, messageKey, components, limits, depth: 0,
            path: string.Empty, ordinal: () => ordinal++, total: () => totalBytes,
            addTotal: b => totalBytes += b, ct);

        return new EmailInquiryManifest(messageKey, components, quotedOnly);
    }

    private static async Task WalkAsync(
        MimeMessage message,
        string messageKey,
        List<EmailInquiryComponentPlan> components,
        EmailInquiryLimits limits,
        int depth,
        string path,
        Func<int> ordinal,
        Func<long> total,
        Action<long> addTotal,
        CancellationToken ct)
    {
        var index = 0;
        foreach (var entity in message.Attachments)
        {
            ct.ThrowIfCancellationRequested();
            index++;
            var key = string.IsNullOrEmpty(path)
                ? $"email:{messageKey}:attachment:{index}"
                : $"email:{messageKey}:embedded:{path}:{index}";

            // The component ceiling is checked BEFORE decoding, and the overflow is recorded as
            // ONE row rather than one row per excess part — a message with ten thousand
            // attachments must not be answered with ten thousand database rows, which would
            // make the refusal itself the denial of service it is defending against.
            if (components.Count >= limits.MaxComponents)
            {
                var remaining = message.Attachments.Count() - index + 1;
                components.Add(Refused(key, EmailInquiryComponentKind.Attachment, ordinal(),
                    NameOf(entity, index), depth,
                    EmailInquirySkipReasons.ComponentLimitExceeded,
                    $"This message carries more than the {limits.MaxComponents} parts one message "
                    + $"may contain; {remaining} part(s) were not processed."));
                return;
            }

            if (entity is MessagePart embedded)
            {
                await PlanEmbeddedAsync(embedded, messageKey, components, limits, depth,
                    path, index, key, ordinal, total, addTotal, ct);
                continue;
            }

            if (entity is not MimePart part)
            {
                components.Add(Refused(key, EmailInquiryComponentKind.Attachment, ordinal(),
                    NameOf(entity, index), depth,
                    EmailInquirySkipReasons.AttachmentUnreadable,
                    "This part could not be read as a file or an embedded message."));
                continue;
            }

            if (string.IsNullOrWhiteSpace(part.FileName))
            {
                components.Add(Refused(key, EmailInquiryComponentKind.Attachment, ordinal(),
                    $"attachment #{index}", depth,
                    EmailInquirySkipReasons.AttachmentUnnamed,
                    "This attachment arrived without a filename."));
                continue;
            }

            var extension = Path.GetExtension(part.FileName).ToLowerInvariant();
            if (!ERP_RFQ_Automation.Security.DocumentInspection.DocumentIntakeAllowList.IsAllowed(extension))
            {
                components.Add(Refused(key, EmailInquiryComponentKind.Attachment, ordinal(),
                    part.FileName, depth,
                    EmailInquirySkipReasons.UnsupportedFileType,
                    $"'{extension}' is not a file type this system reads."));
                continue;
            }

            byte[] bytes;
            try
            {
                using var buffer = new MemoryStream();
                await part.Content.DecodeToAsync(buffer, ct);
                bytes = buffer.ToArray();
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // A malformed part is a property of the FILE, so it is a skip rather than a
                // hold: no amount of retrying decodes it. The exception type stays in the log.
                components.Add(Refused(key, EmailInquiryComponentKind.Attachment, ordinal(),
                    part.FileName, depth,
                    EmailInquirySkipReasons.AttachmentUnreadable,
                    "This attachment could not be decoded from the message."));
                continue;
            }

            if (bytes.Length == 0)
            {
                components.Add(Refused(key, EmailInquiryComponentKind.Attachment, ordinal(),
                    part.FileName, depth,
                    EmailInquirySkipReasons.AttachmentEmpty, "This attachment is empty."));
                continue;
            }
            if (bytes.Length > limits.MaxComponentBytes)
            {
                components.Add(Refused(key, EmailInquiryComponentKind.Attachment, ordinal(),
                    part.FileName, depth,
                    EmailInquirySkipReasons.AttachmentOversize,
                    $"This attachment is larger than the {limits.MaxComponentBytes / (1024 * 1024)} MB limit."));
                continue;
            }
            if (total() + bytes.Length > limits.MaxTotalBytes)
            {
                components.Add(Refused(key, EmailInquiryComponentKind.Attachment, ordinal(),
                    part.FileName, depth,
                    EmailInquirySkipReasons.TotalSizeLimitExceeded,
                    $"This message exceeds the {limits.MaxTotalBytes / (1024 * 1024)} MB total limit."));
                continue;
            }

            addTotal(bytes.Length);
            components.Add(new EmailInquiryComponentPlan(
                key, EmailInquiryComponentKind.Attachment, ordinal(),
                part.FileName, part.ContentType?.MimeType, bytes.Length, Sha256(bytes),
                EmailInquiryComponentDisposition.Process, null, null, depth, bytes));
        }
    }

    /// <summary>
    /// An embedded <c>message/rfc822</c> part becomes a first-class component: it is serialized
    /// to <c>.eml</c> and processed like any other file, which means it crosses the SAME
    /// inspection boundary as an attached PDF.
    ///
    /// <para>The previous code recorded it as "embedded email message is not ingested" and
    /// dropped it. A forwarded enquiry — one of the commonest ways a real RFQ arrives — was
    /// therefore invisible to the pipeline. <c>.eml</c> is already on the intake allow-list and
    /// <c>EmailContainerReader</c> already unwraps it, so nothing new has to trust it.</para>
    /// </summary>
    private static async Task PlanEmbeddedAsync(
        MessagePart embedded,
        string messageKey,
        List<EmailInquiryComponentPlan> components,
        EmailInquiryLimits limits,
        int depth,
        string path,
        int index,
        string key,
        Func<int> ordinal,
        Func<long> total,
        Action<long> addTotal,
        CancellationToken ct)
    {
        if (depth + 1 > limits.MaxNestingDepth)
        {
            components.Add(Refused(key, EmailInquiryComponentKind.EmbeddedMessage, ordinal(),
                EmbeddedName(embedded, index), depth + 1,
                EmailInquirySkipReasons.NestingLimitExceeded,
                $"Forwarded messages are followed {limits.MaxNestingDepth} levels deep; this one is deeper."));
            return;
        }

        byte[] bytes;
        try
        {
            using var buffer = new MemoryStream();
            if (embedded.Message is null)
                throw new FormatException("The embedded part carried no message.");
            await embedded.Message.WriteToAsync(buffer, ct);
            bytes = buffer.ToArray();
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            components.Add(Refused(key, EmailInquiryComponentKind.EmbeddedMessage, ordinal(),
                EmbeddedName(embedded, index), depth + 1,
                EmailInquirySkipReasons.EmbeddedMessageUnreadable,
                "This forwarded message could not be read."));
            return;
        }

        if (bytes.Length == 0)
        {
            components.Add(Refused(key, EmailInquiryComponentKind.EmbeddedMessage, ordinal(),
                EmbeddedName(embedded, index), depth + 1,
                EmailInquirySkipReasons.AttachmentEmpty, "This forwarded message is empty."));
            return;
        }
        if (bytes.Length > limits.MaxComponentBytes)
        {
            components.Add(Refused(key, EmailInquiryComponentKind.EmbeddedMessage, ordinal(),
                EmbeddedName(embedded, index), depth + 1,
                EmailInquirySkipReasons.AttachmentOversize,
                $"This forwarded message is larger than the {limits.MaxComponentBytes / (1024 * 1024)} MB limit."));
            return;
        }
        if (total() + bytes.Length > limits.MaxTotalBytes)
        {
            components.Add(Refused(key, EmailInquiryComponentKind.EmbeddedMessage, ordinal(),
                EmbeddedName(embedded, index), depth + 1,
                EmailInquirySkipReasons.TotalSizeLimitExceeded,
                $"This message exceeds the {limits.MaxTotalBytes / (1024 * 1024)} MB total limit."));
            return;
        }

        addTotal(bytes.Length);
        components.Add(new EmailInquiryComponentPlan(
            key, EmailInquiryComponentKind.EmbeddedMessage, ordinal(),
            EmbeddedName(embedded, index), "message/rfc822", bytes.Length, Sha256(bytes),
            EmailInquiryComponentDisposition.Process, null, null, depth + 1, bytes));
    }

    private static EmailInquiryComponentPlan Refused(
        string key, EmailInquiryComponentKind kind, int ordinal, string? fileName,
        int depth, string reasonCode, string reasonDetail)
        => new(key, kind, ordinal, fileName, null, 0, string.Empty,
            EmailInquiryComponentDisposition.Skip, reasonCode, reasonDetail, depth,
            ReadOnlyMemory<byte>.Empty);

    private static string NameOf(MimeEntity entity, int index)
        => entity.ContentDisposition?.FileName
           ?? entity.ContentType?.Name
           ?? $"attachment #{index}";

    private static string EmbeddedName(MessagePart embedded, int index)
    {
        var subject = embedded.Message?.Subject;
        return string.IsNullOrWhiteSpace(subject)
            ? $"forwarded_message_{index}.eml"
            : $"{SanitizeFileName(subject)}.eml";
    }

    private static string Sha256(ReadOnlySpan<byte> bytes)
        => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    internal static string SanitizeFileName(string fileName)
        => string.Join("_", fileName.Split(Path.GetInvalidFileNameChars(),
            StringSplitOptions.RemoveEmptyEntries)).Replace(" ", "_");
}
