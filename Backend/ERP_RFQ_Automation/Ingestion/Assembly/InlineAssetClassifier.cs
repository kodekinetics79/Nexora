using System;
using System.Collections.Generic;
using MimeKit;

namespace ERP_RFQ_Automation.Ingestion.Assembly;

/// <summary>What the classifier could prove about an image part.</summary>
public enum InlineAssetVerdict
{
    /// <summary>Not an inline image at all — handle as an ordinary part.</summary>
    NotInlineAsset,

    /// <summary>Proven decoration: signature logo, icon, tracking pixel.</summary>
    Decorative,

    /// <summary>
    /// An inline image the classifier could NOT prove decorative. It is processed as content —
    /// never ignored — because the alternative is discarding a pasted requirements table.
    /// </summary>
    ProcessAsContent
}

/// <summary>
/// Decides whether an image may go unread without sending the message to a human.
///
/// <para><b>Two failure directions, and they are not symmetric.</b> Wrongly ignoring a part
/// produces a Lead priced against content nobody saw — a pasted screenshot of a requirements
/// table is inline, cid-referenced and an image, structurally identical to a logo. Wrongly
/// reviewing a logo costs a few seconds of attention. So every uncertain case resolves toward
/// processing, and <see cref="InlineAssetVerdict.Decorative"/> requires positive evidence on
/// every axis.</para>
///
/// <para><b>What changed and why.</b> The first version required
/// <c>Content-Disposition: size</c>, which RFC 2183 makes optional and which Gmail, Apple Mail,
/// Outlook and Exchange all omit. The classifier therefore almost never fired: every signature
/// logo became a full extraction job yielding no text, and every unnamed cid image became
/// "attachment has no filename" and forced a human review of a message with nothing wrong with
/// it. A review queue full of signature graphics is how a review gate stops being read. Size is
/// now MEASURED from the encoded body rather than believed from a header.</para>
/// </summary>
public static class InlineAssetClassifier
{
    /// <summary>
    /// Requires ALL of: image media type, a Content-Id, an actual <c>cid:</c> reference from the
    /// HTML body, inline rather than attachment intent, a measurable size within the decoration
    /// ceiling, and no contradictory commercial signal in the filename.
    /// </summary>
    public static InlineAssetVerdict Classify(
        MimePart part, IReadOnlySet<string> htmlCidReferences, EmailInquiryBudget budget)
    {
        ArgumentNullException.ThrowIfNull(part);
        ArgumentNullException.ThrowIfNull(htmlCidReferences);
        ArgumentNullException.ThrowIfNull(budget);

        if (part.ContentType?.MediaType is not { } media
            || !media.Equals("image", StringComparison.OrdinalIgnoreCase))
            return InlineAssetVerdict.NotInlineAsset;

        // A sender who marks something an attachment is telling us it is content. Believe them.
        if (part.ContentDisposition?.IsAttachment == true)
            return InlineAssetVerdict.ProcessAsContent;

        var contentId = part.ContentId?.Trim('<', '>', ' ');
        if (string.IsNullOrWhiteSpace(contentId))
            return InlineAssetVerdict.ProcessAsContent;

        // Presence of a Content-Id proves nothing on its own — a document attachment can carry
        // one. The body must actually point at it.
        if (!htmlCidReferences.Contains(contentId))
            return InlineAssetVerdict.ProcessAsContent;

        // A filename that talks about the deal is a contradictory signal, whatever the headers
        // say. "rfq-lines.png" pasted into a signature block is still the RFQ.
        if (HasCommercialFileName(part.FileName))
            return InlineAssetVerdict.ProcessAsContent;

        // MEASURED, not declared. Encoded length is a safe upper bound on the decoded size for
        // base64 and quoted-printable, and it is read without decoding anything.
        var measured = MeasureEncodedLength(part);
        if (measured is null || measured > budget.InlineAssetMaxBytes)
            return InlineAssetVerdict.ProcessAsContent;

        return InlineAssetVerdict.Decorative;
    }

    /// <summary>
    /// Upper bound on the part's size taken from the encoded stream, without decoding.
    ///
    /// <para>Base64 inflates by 4/3, so the encoded length always over-states the decoded size;
    /// using it means the classifier can only ever be too cautious, never too permissive. Null
    /// when the length cannot be established, which resolves to processing.</para>
    /// </summary>
    private static long? MeasureEncodedLength(MimePart part)
    {
        try
        {
            var stream = part.Content?.Stream;
            if (stream is null || !stream.CanSeek) return null;
            return stream.Length;
        }
        catch (NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>Words that make an image commercially significant regardless of placement.</summary>
    private static bool HasCommercialFileName(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName)) return false;
        ReadOnlySpan<string> signals =
        [
            "rfq", "quote", "quotation", "boq", "bill", "spec", "requirement", "drawing",
            "tender", "enquiry", "inquiry", "schedule", "pricing", "price", "scope", "qty",
            "screenshot", "screen shot", "table", "list"
        ];
        foreach (var signal in signals)
            if (fileName.Contains(signal, StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }
}
