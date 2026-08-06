using System;
using System.Collections.Generic;

namespace ERP_RFQ_Automation.Services.DocumentIntelligence;

/// <summary>
/// One attachment carried by an email container (<c>.eml</c> or <c>.msg</c>).
/// </summary>
/// <param name="Ordinal">1-based position in the message. Used INSTEAD of the filename in any
/// user-facing sentence: an attachment name is attacker-controlled text and intake dispositions
/// render verbatim as product copy.</param>
/// <param name="FileName">The declared filename, already stripped of path separators. Used for
/// extension routing and for the inspection request; never interpolated into a rejection reason.</param>
/// <param name="Content">Decoded bytes.</param>
/// <param name="IsEmbeddedMessage">True when the attachment is itself an email (message/rfc822 or
/// an embedded Outlook message object), which is what the recursion depth limit governs.</param>
public sealed record ParsedEmailAttachment(
    int Ordinal,
    string FileName,
    byte[] Content,
    bool IsEmbeddedMessage);

/// <summary>
/// The format-neutral view of an email that <c>.eml</c> (MimeKit) and <c>.msg</c>
/// (<see cref="OutlookMsgReader"/>) both reduce to, so the downstream flattening,
/// body-normalisation and attachment fan-out exist exactly once.
/// </summary>
/// <param name="Notes">Truthful, user-safe sentences about anything the PARSER itself could not
/// take (an attachment with no name, a body available only as compressed RTF). Never silent.</param>
public sealed record ParsedEmailMessage(
    string? Subject,
    string? From,
    string? To,
    string? Date,
    string? PlainBody,
    string? HtmlBody,
    IReadOnlyList<ParsedEmailAttachment> Attachments,
    IReadOnlyList<string> Notes)
{
    public static ParsedEmailMessage Empty { get; } = new(
        null, null, null, null, null, null, Array.Empty<ParsedEmailAttachment>(), Array.Empty<string>());
}
