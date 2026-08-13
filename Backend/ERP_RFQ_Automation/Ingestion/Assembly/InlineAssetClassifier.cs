using System;
using System.Collections.Generic;
using System.Threading.Tasks;
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
    public static async Task<InlineAssetVerdict> ClassifyAsync(
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

        // MEASURED DECODED SIZE, via a bounded probe that reads at most the decoration
        // threshold plus one byte. Encoded length would be a safe upper bound but rejects
        // legitimate logos unnecessarily — base64 inflates by a third, so a 50 KB logo measures
        // 67 KB encoded and loses its exemption for arithmetic reasons rather than commercial
        // ones. The probe costs at most InlineAssetMaxBytes + 1 bytes, which is the same bound
        // B1 established, so this cannot reintroduce the OOM vector.
        var probe = await BoundedComponentDecoder.DecodeAsync(
            part, budget.InlineAssetMaxBytes + 1, budget.InlineAssetMaxBytes + 1,
            budget.CancellationToken);

        // Anything the probe could not settle — too big for the threshold, or unreadable —
        // resolves to processing. Uncertainty must never produce Ignored.
        if (!probe.IsDecoded || probe.Bytes.Length > budget.InlineAssetMaxBytes)
            return InlineAssetVerdict.ProcessAsContent;

        return InlineAssetVerdict.Decorative;
    }


    /// <summary>Words that make an image commercially significant regardless of placement.</summary>
    private static bool HasCommercialFileName(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName)) return false;
        ReadOnlySpan<string> signals =
        [
            "rfq", "quote", "quotation", "boq", "bill", "spec", "requirement", "drawing",
            "tender", "enquiry", "inquiry", "schedule", "pricing", "price", "scope", "qty",
            "screenshot", "screen shot", "table", "list",
            // A QR code in commercial mail encodes a portal link or a reference number. It is
            // small and inline like an icon, so only the name distinguishes it — and a sender
            // who names it that is telling us it carries information.
            "qr"
        ];
        foreach (var signal in signals)
            if (fileName.Contains(signal, StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }
}
