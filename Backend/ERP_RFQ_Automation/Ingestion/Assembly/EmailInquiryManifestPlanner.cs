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

    /// <summary>The one reason that does NOT send the message to review.</summary>
    public const string NonCommercialInlineAsset = "non_commercial_inline_asset";

    /// <summary>
    /// A named attachment that is recognisably legal or corporate boilerplate — terms and
    /// conditions, an NDA, a privacy notice, a company profile, a registration certificate.
    ///
    /// <para>The second reason that does not send the message to review, and it is reached only
    /// through <see cref="NonCommercialAttachmentClassifier"/>, which never ignores a spreadsheet
    /// and never ignores a name carrying commercial vocabulary.</para>
    /// </summary>
    public const string NonCommercialBoilerplate = "non_commercial_boilerplate";

    /// <summary>A forwarded message whose children carry the commercial content.</summary>
    public const string StructuralContainer = "structural_container";
}

/// <summary>Whether a planned component will be handed to inspection and extraction.</summary>
public enum EmailInquiryComponentDisposition
{
    /// <summary>Goes through the inspection boundary and into extraction.</summary>
    Process = 0,

    /// <summary>Recorded with its reason and immediately terminal. Never silently dropped —
    /// the row exists so the loss is visible on the message it arrived with, and one of these
    /// sends the whole message to review.</summary>
    Skip = 1,

    /// <summary>
    /// A deterministically classified non-commercial inline asset. Recorded, terminal, and one of
    /// only two dispositions that do not force review — see <see cref="InlineAssetClassifier"/>.
    /// </summary>
    IgnoreInlineAsset = 2,

    /// <summary>
    /// A <c>message/rfc822</c> container: recorded for presence, raw identity and subtree
    /// relationship, but NOT sent to commercial extraction.
    ///
    /// <para>Its children are planned as their own components and they carry the commercial
    /// content. Extracting the container as well would put the same body and the same attachments
    /// through extraction twice — <c>EmailContainerReader</c> unwraps an <c>.eml</c> internally —
    /// producing duplicated line items on one inquiry and provenance that names two sources for
    /// one physical document.</para>
    /// </summary>
    StructuralContainer = 3,

    /// <summary>
    /// A named attachment whose FILENAME is unambiguously legal or corporate boilerplate — see
    /// <see cref="NonCommercialAttachmentClassifier"/>. Recorded, terminal, and the second of the
    /// dispositions that do not force review.
    ///
    /// <para>Kept distinct from <see cref="IgnoreInlineAsset"/> rather than folded into it. They
    /// are proven non-commercial by completely different evidence — one by measured size and a
    /// cid reference from the body, the other by an unambiguous name — and an operator asking
    /// "why was this part not read?" must get the real answer. Folding them together would also
    /// make the enum member name a lie the next reader would have to discover by experiment.</para>
    /// </summary>
    IgnoreNonCommercial = 4
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

    /// <summary>
    /// Ceiling below which a cid-referenced inline image may be treated as decoration.
    ///
    /// <para><b>16 KB, and the number is doing real work.</b> Outlook names BOTH a signature
    /// logo and a pasted screenshot <c>image001.png</c>, both inline, both cid-referenced, both
    /// image parts — so the filename and the headers cannot separate them and size is the only
    /// honest discriminator left. Signature logos and icons are typically 2–15 KB; a screenshot
    /// of a requirements table is rarely under 20 KB.</para>
    ///
    /// <para>It was 64 KB, which exempted a 30 KB pasted requirements table — a Lead priced
    /// against a document nobody opened. The ceiling is deliberately set below the ambiguous
    /// band so that everything uncertain is processed, because the two errors are not
    /// comparable: a wrongly-processed logo costs one empty extraction job, a wrongly-ignored
    /// screenshot costs the enquiry.</para>
    /// </summary>
    public long InlineAssetMaxBytes { get; init; } = 16L * 1024;

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
    bool QuotedOnlyBody,
    int ContractVersion)
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
    /// <summary>
    /// Version of the planning CONTRACT — which parts count as components, how they are
    /// ordered, and how their keys are formed.
    ///
    /// <para>It is persisted on every assembly so that a future change to this planner cannot
    /// silently reinterpret a message captured under the old rules. Recovery re-plans the raw
    /// bytes and compares; a version difference is a manifest mismatch to be looked at, not a
    /// migration to be guessed at. Bump it whenever component identity, ordering or disposition
    /// changes — never for an internal refactor that leaves those three alone.</para>
    /// </summary>
    /// <para><b>v2</b> — the recursive rewrite. v1 walked one level with flat
    /// <c>:attachment:{n}</c> keys. v2 recurses, uses hierarchical <c>:part:{path}</c> keys,
    /// treats a forwarded container as structural, plans nested bodies, and classifies inline
    /// assets by measured size. Every one of those changes component identity or disposition,
    /// which is exactly what this number exists to record. Leaving it at 1 through that rewrite
    /// would have made the guard blind for the only population it protects: a v1-captured
    /// message re-planned by v2 would pass the version check and then surface as a pile of
    /// misleading per-component mismatches instead of one true "the contract changed".</para>
    /// <para><b>v3</b> — non-commercial boilerplate. A named attachment whose filename is
    /// unambiguously legal or corporate paperwork (terms and conditions, an NDA, a privacy
    /// notice, a company profile, a registration certificate) is now
    /// <see cref="EmailInquiryComponentDisposition.IgnoreNonCommercial"/> rather than processed or
    /// skipped. That changes the DISPOSITION of an existing part for identical bytes, which is
    /// precisely the change this constant exists to record: a v2-captured message re-planned by
    /// v3 must report "the contract changed" rather than a per-component disposition mismatch
    /// that reads like evidence tampering.</para>
    ///
    /// <para><b>v4</b> — control characters are stripped from the body before it becomes a
    /// component (<c>EmailBodyNormalizer.SanitizeControlCharacters</c>). Outlook writes
    /// VERTICAL TAB as a soft line break, which failed the text inspection applied to the
    /// generated <c>_body.txt</c> and held the whole message; removing it was the fix. But it
    /// changes the BYTES the planner produces from an identical stored original, so the body
    /// component's length and hash no longer match what a v3 capture recorded.
    ///
    /// <para>Observed in production the moment the fix deployed: a v3-captured message
    /// re-planned by v4 reported <c>ComponentMetadataMismatch</c> — "the stored original no
    /// longer matches what was recorded when this message arrived" — which reads as evidence
    /// tampering and held every component. Every message captured before the fix would have
    /// been permanently unrecoverable. That is precisely the failure this constant exists to
    /// prevent, and forgetting to bump it is precisely how it happens.</para></para>
    public const int ContractVersion = 4;

    public static async Task<EmailInquiryManifest> PlanAsync(
        MimeMessage message,
        string messageKey,
        string? freshBodyText,
        EmailInquiryLimits? limits = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentException.ThrowIfNullOrWhiteSpace(messageKey);

        var budget = new EmailInquiryBudget(limits ?? EmailInquiryLimits.Default, ct);
        var components = new List<EmailInquiryComponentPlan>();
        var ordinal = 0;

        // 1) The body, and only when the sender actually wrote something. A quoted-only reply
        //    produces NO body component, so a bare "thanks" with nothing attached ends as
        //    NoInquiry rather than sitting in a reviewer's queue forever.
        var quotedOnly = string.IsNullOrWhiteSpace(freshBodyText);
        if (!quotedOnly && budget.TryTakeComponent())
        {
            var bodyDocument =
                $"Subject: {message.Subject}\nFrom: {message.From}\nDate: {message.Date:yyyy-MM-dd}\n\n{freshBodyText}";
            var bytes = Encoding.UTF8.GetBytes(bodyDocument);
            // Refusable. A body that will not fit is recorded with its reason, never charged
            // against an allowance it overdraws and never silently dropped.
            if (!budget.TryChargeBytes(bytes.Length))
            {
                components.Add(Refused(
                    $"email:{messageKey}:body", EmailInquiryComponentKind.Body, ordinal++,
                    $"{SanitizeFileName(message.Subject ?? "email")}_body.txt", 0,
                    EmailInquirySkipReasons.TotalSizeLimitExceeded,
                    "The text of this message is larger than one message may contain."));
                // Recorded and skipped, NOT abandoned. Returning here discarded every attachment
                // on the message, and the attachment is usually the priced document - an
                // oversized covering note must never take the bill of quantities with it.
            }
            else
            {
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
        }

        // 2) The MIME tree, depth-first, sharing ONE budget.
        var cidReferences = HtmlCidReferences(message);
        await WalkAsync(message, messageKey, components, budget, depth: 0,
            path: string.Empty, cidReferences, () => ordinal++);

        return new EmailInquiryManifest(messageKey, components, quotedOnly, ContractVersion);
    }

    /// <summary>
    /// Walks one message level, recursing into embedded messages.
    ///
    /// <para><b>Identity is a hierarchical PATH, not a flat counter.</b> Once traversal recurses,
    /// a counter alone collides: the third top-level attachment and the third attachment inside a
    /// forward would both be "attachment:3". Keys are therefore <c>part:1</c>, <c>part:3</c>,
    /// <c>part:3.1</c>, <c>part:3.2</c> — position within the tree, which is deterministic for
    /// identical bytes and cannot alias across levels.</para>
    ///
    /// <para>Recursion is bounded by <see cref="EmailInquiryBudget.MaxNestingDepth"/> before the
    /// call is made, so the stack depth is a declared constant rather than a property of the
    /// message.</para>
    /// </summary>
    private static async Task WalkAsync(
        MimeMessage message,
        string messageKey,
        List<EmailInquiryComponentPlan> components,
        EmailInquiryBudget budget,
        int depth,
        string path,
        IReadOnlySet<string> cidReferences,
        Func<int> ordinal)
    {
        // MimeKit's `Attachments` is NOT the MIME tree — it yields only entities whose
        // Content-Disposition says "attachment". An embedded message/rfc822 inside a
        // multipart/mixed frequently carries NO disposition header at all, which is how a
        // forwarded enquiry arrives from Outlook and Gmail, and it was therefore invisible to
        // any planner built on `Attachments`.
        var candidates = message.BodyParts.Where(IsCandidatePart).ToList();

        var index = 0;
        foreach (var entity in candidates)
        {
            budget.CancellationToken.ThrowIfCancellationRequested();
            index++;
            var key = $"email:{messageKey}:part:{Segment(path, index)}";

            // The ceiling is checked BEFORE decoding, and the overflow is recorded as ONE row
            // naming how many parts were dropped rather than one row per excess part — answering
            // a ten-thousand-attachment message with ten thousand rows would make the refusal
            // the denial of service it defends against.
            if (budget.ComponentsExhausted)
            {
                var remaining = candidates.Count - index + 1;
                components.Add(Refused(key, EmailInquiryComponentKind.Attachment, ordinal(),
                    NameOf(entity, index), depth,
                    EmailInquirySkipReasons.ComponentLimitExceeded,
                    $"This message carries more parts than the {budget.RemainingComponents + components.Count} "
                    + $"one message may contain; {remaining} part(s) at this level were not processed."));
                return;
            }

            if (entity is MessagePart embedded)
            {
                await PlanEmbeddedAsync(embedded, messageKey, components, budget, depth,
                    Segment(path, index), cidReferences, ordinal);
                continue;
            }

            if (entity is not MimePart part)
            {
                if (!budget.TryTakeComponent()) return;
                components.Add(Refused(key, EmailInquiryComponentKind.Attachment, ordinal(),
                    NameOf(entity, index), depth,
                    EmailInquirySkipReasons.AttachmentUnreadable,
                    "This part could not be read as a file or an embedded message."));
                continue;
            }

            if (!budget.TryTakeComponent()) return;

            var classification = await InlineAssetClassifier.ClassifyAsync(part, cidReferences, budget);
            if (classification == InlineAssetVerdict.Decorative)
            {
                components.Add(new EmailInquiryComponentPlan(
                    key, EmailInquiryComponentKind.Attachment, ordinal(),
                    part.FileName, CanonicalMimeType(part), 0, string.Empty,
                    EmailInquiryComponentDisposition.IgnoreInlineAsset,
                    EmailInquirySkipReasons.NonCommercialInlineAsset,
                    "A small inline image referenced by the message body — a signature logo or "
                    + "similar. Not treated as commercial content.",
                    depth, ReadOnlyMemory<byte>.Empty));
                continue;
            }

            if (string.IsNullOrWhiteSpace(part.FileName) && classification != InlineAssetVerdict.ProcessAsContent)
            {
                components.Add(Refused(key, EmailInquiryComponentKind.Attachment, ordinal(),
                    $"part {Segment(path, index)}", depth,
                    EmailInquirySkipReasons.AttachmentUnnamed,
                    "This attachment arrived without a filename."));
                continue;
            }

            // BOILERPLATE, BY NAME ALONE — decided here, before any decode, because the whole
            // point is that these parts cost the message nothing: no bytes retained, no evidence
            // object, no extraction job, and no trip to a reviewer.
            //
            // It sits after the unnamed check so there is a name to judge, and before the
            // container, allow-list and decode rules so that an unreadable "Terms &
            // Conditions.pdf" and a perfectly readable one reach the same, correct answer. That
            // symmetry is the fix: previously the unreadable one downgraded an entire RFQ to
            // review while the readable one spent an extraction job learning that a legal notice
            // contains no priced lines.
            if (NonCommercialAttachmentClassifier.IsNonCommercialBoilerplate(
                    part.FileName, CanonicalMimeType(part), out var boilerplatePattern))
            {
                components.Add(new EmailInquiryComponentPlan(
                    key, EmailInquiryComponentKind.Attachment, ordinal(),
                    part.FileName, CanonicalMimeType(part), 0, string.Empty,
                    EmailInquiryComponentDisposition.IgnoreNonCommercial,
                    EmailInquirySkipReasons.NonCommercialBoilerplate,
                    $"Recognised as standard non-commercial paperwork from its name "
                    + $"('{boilerplatePattern}'). Not treated as commercial content.",
                    depth, ReadOnlyMemory<byte>.Empty));
                continue;
            }

            var container = ContainerFormatClassifier.Classify(part);
            if (container is { } refusal)
            {
                components.Add(Refused(key, EmailInquiryComponentKind.Attachment, ordinal(),
                    part.FileName ?? NameOf(entity, index), depth,
                    refusal.ReasonCode, refusal.OperatorDetail));
                continue;
            }

            var fileName = part.FileName ?? $"inline-image-{Segment(path, index)}{ExtensionFor(part)}";
            var extension = Path.GetExtension(fileName).ToLowerInvariant();
            if (!ERP_RFQ_Automation.Security.DocumentInspection.DocumentIntakeAllowList.IsAllowed(extension))
            {
                components.Add(Refused(key, EmailInquiryComponentKind.Attachment, ordinal(),
                    fileName, depth,
                    EmailInquirySkipReasons.UnsupportedFileType,
                    $"'{extension}' is not a file type this system reads."));
                continue;
            }

            var decoded = await BoundedComponentDecoder.DecodeAsync(
                part, budget.ComponentLimit, budget.RemainingBytes, budget.CancellationToken);

            if (decoded.Outcome == BoundedDecodeOutcome.Unreadable)
            {
                components.Add(Refused(key, EmailInquiryComponentKind.Attachment, ordinal(),
                    fileName, depth, EmailInquirySkipReasons.AttachmentUnreadable,
                    "This attachment could not be decoded from the message."));
                continue;
            }
            if (decoded.Outcome == BoundedDecodeOutcome.ExceedsComponentLimit)
            {
                components.Add(Refused(key, EmailInquiryComponentKind.Attachment, ordinal(),
                    fileName, depth, EmailInquirySkipReasons.AttachmentOversize,
                    $"This attachment is larger than the {budget.ComponentLimit / (1024 * 1024)} MB limit."));
                continue;
            }
            if (decoded.Outcome == BoundedDecodeOutcome.ExceedsMessageBudget)
            {
                components.Add(Refused(key, EmailInquiryComponentKind.Attachment, ordinal(),
                    fileName, depth, EmailInquirySkipReasons.TotalSizeLimitExceeded,
                    "This message exceeds the total size one message may contain."));
                continue;
            }
            if (decoded.Bytes.Length == 0)
            {
                components.Add(Refused(key, EmailInquiryComponentKind.Attachment, ordinal(),
                    fileName, depth, EmailInquirySkipReasons.AttachmentEmpty,
                    "This attachment is empty."));
                continue;
            }

            budget.ChargeBytes(decoded.Bytes.Length);
            components.Add(new EmailInquiryComponentPlan(
                key, EmailInquiryComponentKind.Attachment, ordinal(),
                fileName, CanonicalMimeType(part), decoded.Bytes.Length, Sha256(decoded.Bytes),
                EmailInquiryComponentDisposition.Process, null, null, depth, decoded.Bytes));
        }
    }

    /// <summary>
    /// An embedded <c>message/rfc822</c> becomes a component in its own right — serialized to
    /// <c>.eml</c> so it crosses the same inspection boundary as any other file — AND is then
    /// walked, so the parts inside it get their own component rows.
    ///
    /// <para>Both matter. Without the component the forward is invisible; without the walk, a
    /// refused spreadsheet inside the forward is a prose note nobody counts, the container
    /// reports Completed, and a clean Lead is priced against a document nobody opened.</para>
    /// </summary>
    private static async Task PlanEmbeddedAsync(
        MessagePart embedded,
        string messageKey,
        List<EmailInquiryComponentPlan> components,
        EmailInquiryBudget budget,
        int depth,
        string segment,
        IReadOnlySet<string> cidReferences,
        Func<int> ordinal)
    {
        var key = $"email:{messageKey}:part:{segment}";
        var nestedDepth = depth + 1;

        if (!budget.TryTakeComponent()) return;

        if (!budget.CanDescendTo(nestedDepth))
        {
            components.Add(Refused(key, EmailInquiryComponentKind.EmbeddedMessage, ordinal(),
                EmbeddedName(embedded, segment), nestedDepth,
                EmailInquirySkipReasons.NestingLimitExceeded,
                $"Forwarded messages are followed {budget.MaxNestingDepth} level(s) deep; this one is deeper."));
            return;
        }

        if (embedded.Message is null)
        {
            components.Add(Refused(key, EmailInquiryComponentKind.EmbeddedMessage, ordinal(),
                EmbeddedName(embedded, segment), nestedDepth,
                EmailInquirySkipReasons.EmbeddedMessageUnreadable,
                "This forwarded message could not be read."));
            return;
        }

        // Serialized ONLY to compute a stable identity hash for the container, then released.
        //
        // The per-component ceiling deliberately does NOT gate this. That ceiling bounds bytes we
        // RETAIN, and a structural container retains none; applying it here made a forward whose
        // envelope happened to exceed the ceiling refuse the whole subtree, hiding the very
        // attachments the recursion exists to find. The message-wide budget still bounds the
        // work, so a hostile forward cannot spend unbounded memory being hashed.
        var serialized = await BoundedComponentDecoder.SerializeAsync(
            embedded.Message, Math.Max(budget.RemainingBytes, 1), budget.RemainingBytes,
            budget.CancellationToken);

        var containerHash = serialized.Outcome == BoundedDecodeOutcome.Decoded
            ? Sha256(serialized.Bytes)
            : string.Empty;
        var containerSize = serialized.Outcome == BoundedDecodeOutcome.Decoded
            ? serialized.Bytes.Length
            : serialized.ObservedBytes;

        // STRUCTURAL ONLY — no bytes carried, no extraction job, no byte charge.
        //
        // The container is recorded so the forward is visible and its raw identity (hash + size)
        // is durable, but its children below are the commercial components. Carrying the
        // serialized .eml as content would send it to extraction, where EmailContainerReader
        // unwraps it and re-extracts the very body and attachments the recursion is about to
        // plan separately — the same lines twice on one inquiry, with provenance naming two
        // sources for one physical document.
        //
        // Not charging the budget is the other half of that contract: the bytes are hashed and
        // released rather than retained, so charging for them would deduct an allowance nothing
        // is holding and starve the children that genuinely do carry content.
        components.Add(new EmailInquiryComponentPlan(
            key, EmailInquiryComponentKind.EmbeddedMessage, ordinal(),
            EmbeddedName(embedded, segment), "message/rfc822",
            containerSize, containerHash,
            EmailInquiryComponentDisposition.StructuralContainer,
            EmailInquirySkipReasons.StructuralContainer,
            "A forwarded message. Its contents are listed separately below.",
            nestedDepth, ReadOnlyMemory<byte>.Empty));

        // The forwarded message's OWN words. Without this a body-only forward — the commonest
        // way an RFQ reaches a distributor — loses its enquiry completely: the container carries
        // no content by contract, and the walk below only plans attachments.
        //
        // Routed through EmailBodyNormalizer, the same quote-stripper the outer body uses, so the
        // two cannot drift into disagreeing about what the sender actually wrote.
        var nestedBody = ERP_RFQ_Automation.Ingestion.Triage.EmailBodyNormalizer.Normalize(
            embedded.Message.TextBody ?? embedded.Message.HtmlBody ?? string.Empty);
        if (!string.IsNullOrWhiteSpace(nestedBody.Fresh) && budget.TryTakeComponent())
        {
            var nestedDocument =
                $"Subject: {embedded.Message.Subject}\nFrom: {embedded.Message.From}\n"
                + $"Date: {embedded.Message.Date:yyyy-MM-dd}\n\n{nestedBody.Fresh}";
            var nestedBytes = Encoding.UTF8.GetBytes(nestedDocument);
            if (!budget.TryChargeBytes(nestedBytes.Length))
            {
                components.Add(Refused(
                    $"email:{messageKey}:part:{segment}.body", EmailInquiryComponentKind.Body,
                    ordinal(), $"{SanitizeFileName(embedded.Message.Subject ?? "forwarded")}_body.txt",
                    nestedDepth, EmailInquirySkipReasons.TotalSizeLimitExceeded,
                    "The text of this forwarded message is larger than one message may contain."));
                return;
            }
            components.Add(new EmailInquiryComponentPlan(
                $"email:{messageKey}:part:{segment}.body",
                EmailInquiryComponentKind.Body,
                ordinal(),
                $"{SanitizeFileName(embedded.Message.Subject ?? "forwarded")}_body.txt",
                "text/plain",
                nestedBytes.Length,
                Sha256(nestedBytes),
                EmailInquiryComponentDisposition.Process,
                null, null, nestedDepth, nestedBytes));
        }

        // The SAME budget instance descends. A nested level never receives a fresh allowance —
        // three forwards each carrying 90 MB must not each pass a "100 MB total" check.
        await WalkAsync(embedded.Message, messageKey, components, budget, nestedDepth,
            segment, HtmlCidReferences(embedded.Message), ordinal);
    }

    /// <summary>Hierarchical path segment: "3" at the top level, "3.2" one level in.</summary>
    private static string Segment(string path, int index)
        => string.IsNullOrEmpty(path) ? index.ToString() : $"{path}.{index}";

    /// <summary>
    /// Content-Ids actually referenced as <c>cid:</c> in the HTML body. Presence of a Content-Id
    /// alone proves nothing — a document attachment may carry one — so the classifier requires
    /// the body to genuinely point at it.
    /// </summary>
    private static IReadOnlySet<string> HtmlCidReferences(MimeMessage message)
    {
        var html = message.HtmlBody;
        if (string.IsNullOrEmpty(html)) return new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var referenced = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (System.Text.RegularExpressions.Match match in
                 System.Text.RegularExpressions.Regex.Matches(
                     html, @"cid:([^\s""'>\)]+)",
                     System.Text.RegularExpressions.RegexOptions.IgnoreCase,
                     TimeSpan.FromSeconds(2)))
        {
            referenced.Add(Uri.UnescapeDataString(match.Groups[1].Value).Trim('<', '>'));
        }
        return referenced;
    }

    /// <summary>Media type and subtype only — parameter order varies between senders and must
    /// not make two renderings of the same part look different.</summary>
    internal static string? CanonicalMimeType(MimePart part)
        => part.ContentType is null
            ? null
            : $"{part.ContentType.MediaType}/{part.ContentType.MediaSubtype}".ToLowerInvariant();

    private static string ExtensionFor(MimePart part) => part.ContentType?.MediaSubtype?.ToLowerInvariant() switch
    {
        "png" => ".png",
        "jpeg" or "jpg" => ".jpg",
        "gif" => ".gif",
        "bmp" => ".bmp",
        "webp" => ".webp",
        "tiff" => ".tif",
        _ => ".bin"
    };

    internal static bool IsIgnorableInlineAsset(MimePart part, EmailInquiryLimits limits)
    {
        if (part.ContentDisposition is null) return false;
        if (part.ContentDisposition.IsAttachment) return false;
        if (!string.Equals(part.ContentDisposition.Disposition, ContentDisposition.Inline,
                StringComparison.OrdinalIgnoreCase)) return false;
        if (string.IsNullOrWhiteSpace(part.ContentId)) return false;
        if (part.ContentType?.MediaType is not { } media
            || !media.Equals("image", StringComparison.OrdinalIgnoreCase)) return false;

        // Size is read from the declared disposition rather than by decoding: the classifier
        // runs before any decode so that a hostile "inline image" cannot force one. A part that
        // does not declare its size is NOT ignorable — unknown size falls through to review.
        var declared = part.ContentDisposition.Size;
        return declared is > 0 && declared <= limits.InlineAssetMaxBytes;
    }

    private static bool IsCandidatePart(MimeEntity entity)
    {
        if (entity is MessagePart) return true;
        if (entity is not MimePart part) return false;
        if (part.ContentDisposition?.IsAttachment == true) return true;
        if (!string.IsNullOrWhiteSpace(part.FileName)) return true;
        return !part.ContentType.IsMimeType("text", "plain")
               && !part.ContentType.IsMimeType("text", "html");
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
           ?? $"part {index}";

    private static string EmbeddedName(MessagePart embedded, string segment)
    {
        var subject = embedded.Message?.Subject;
        return string.IsNullOrWhiteSpace(subject)
            ? $"forwarded_message_{segment.Replace('.', '_')}.eml"
            : $"{SanitizeFileName(subject)}.eml";
    }

    private static string Sha256(ReadOnlySpan<byte> bytes)
        => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    internal static string SanitizeFileName(string fileName)
        => string.Join("_", fileName.Split(Path.GetInvalidFileNameChars(),
            StringSplitOptions.RemoveEmptyEntries)).Replace(" ", "_");
}
