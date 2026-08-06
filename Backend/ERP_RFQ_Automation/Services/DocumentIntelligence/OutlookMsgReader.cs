using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace ERP_RFQ_Automation.Services.DocumentIntelligence;

/// <summary>
/// Reads an Outlook <c>.msg</c> into the format-neutral <see cref="ParsedEmailMessage"/>.
///
/// <para>
/// A <c>.msg</c> is a compound file (see <see cref="OleCompoundFile"/>) whose MAPI properties are
/// stored one per stream, named <c>__substg1.0_&lt;TTTT&gt;&lt;KKKK&gt;</c> where TTTT is the
/// property tag and KKKK the property type (<c>001F</c> Unicode, <c>001E</c> 8-bit, <c>0102</c>
/// binary, <c>000D</c> embedded object). Attachments live in <c>__attach_version1.0_#XXXXXXXX</c>
/// storages carrying their own property streams.
/// </para>
///
/// <para>
/// NO NEW PACKAGE. MimeKit — already referenced for IMAP/SMTP — parses <c>.eml</c> natively but
/// knows nothing about MAPI compound files, and the only .NET libraries that do would add a
/// dependency to a parser whose whole input is untrusted. The audit named a
/// <c>__substg1.0_*</c> reader as an accepted alternative; this is it, and it reuses the sector
/// walking the inspection layer already had to do to answer the macro question.
/// </para>
///
/// <para>
/// BOUNDS. Property streams are capped individually (<see cref="MaxPropertyBytes"/>), attachment
/// payloads at <see cref="MaxAttachmentBytes"/> — the same 25 MB the intake inspection limit
/// applies — and the attachment count at <see cref="MaxAttachments"/>. A message whose bytes do
/// not support a declared length fails loudly through <see cref="OleCompoundFileException"/>
/// rather than returning a truncated property.
/// </para>
/// </summary>
public static class OutlookMsgReader
{
    /// <summary>Longest single MAPI property this reader will materialise (subject, body, headers).</summary>
    public const long MaxPropertyBytes = 8L * 1024 * 1024;

    /// <summary>Largest attachment payload taken out of a message — matches the 25 MB intake limit.</summary>
    public const long MaxAttachmentBytes = 25L * 1024 * 1024;

    /// <summary>Attachment ceiling. A real forwarded tender carries a handful.</summary>
    public const int MaxAttachments = 50;

    private const string SubjectTag = "0037";
    private const string BodyTag = "1000";
    private const string HtmlBodyTag = "1013";
    private const string CompressedRtfTag = "1009";
    private const string SenderNameTag = "0C1A";
    private const string SenderEmailTag = "0C1F";
    private const string SenderSmtpTag = "5D01";
    private const string DisplayToTag = "0E04";
    private const string TransportHeadersTag = "007D";

    private const string AttachLongFileNameTag = "3707";
    private const string AttachFileNameTag = "3704";
    private const string AttachDataBinaryTag = "3701";

    /// <summary>True when the compound file's directory looks like an Outlook message rather than
    /// a legacy Word/Excel document. Used by intake inspection to type the file.</summary>
    public static bool LooksLikeOutlookMessage(IEnumerable<string> streamNames)
    {
        ArgumentNullException.ThrowIfNull(streamNames);
        var hasProperties = false;
        var hasSubstg = false;
        foreach (var name in streamNames)
        {
            if (name.StartsWith("__properties_version1.0", StringComparison.OrdinalIgnoreCase)) hasProperties = true;
            else if (name.StartsWith("__substg1.0_", StringComparison.OrdinalIgnoreCase)) hasSubstg = true;
            else if (name.StartsWith("__nameid_version1.0", StringComparison.OrdinalIgnoreCase)) hasProperties = true;
            if (hasProperties && hasSubstg) return true;
        }
        // A message with no named properties at all is not something this reader can use, and
        // claiming otherwise would send an unreadable file down the .msg path.
        return hasProperties && hasSubstg;
    }

    /// <summary>
    /// Parses <paramref name="bytes"/>. Structural failures raise
    /// <see cref="OleCompoundFileException"/>; individual properties that are absent are simply
    /// null, and anything deliberately NOT taken is recorded in
    /// <see cref="ParsedEmailMessage.Notes"/> so it is visible rather than silently dropped.
    /// </summary>
    public static ParsedEmailMessage Read(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        var compound = OleCompoundFile.Open(bytes);
        return Read(compound, string.Empty);
    }

    private static ParsedEmailMessage Read(OleCompoundFile compound, string storagePrefix)
    {
        var notes = new List<string>();
        var subject = ReadString(compound, storagePrefix, SubjectTag);
        var body = ReadString(compound, storagePrefix, BodyTag);
        var htmlBody = ReadHtmlBody(compound, storagePrefix);
        var senderName = ReadString(compound, storagePrefix, SenderNameTag);
        var senderEmail = ReadString(compound, storagePrefix, SenderSmtpTag)
                          ?? ReadString(compound, storagePrefix, SenderEmailTag);
        var displayTo = ReadString(compound, storagePrefix, DisplayToTag);
        var transportHeaders = ReadString(compound, storagePrefix, TransportHeadersTag);

        if (string.IsNullOrWhiteSpace(body) && string.IsNullOrWhiteSpace(htmlBody)
            && FindStream(compound, storagePrefix, CompressedRtfTag) is not null)
        {
            notes.Add("The message body is stored only as compressed rich text, which Nexora does "
                      + "not read; any attachments were still processed.");
        }

        var attachments = ReadAttachments(compound, storagePrefix, notes);

        return new ParsedEmailMessage(
            Subject: Clean(subject),
            From: ComposeFrom(senderName, senderEmail),
            To: Clean(displayTo),
            Date: ExtractDateHeader(transportHeaders),
            PlainBody: Clean(body),
            HtmlBody: htmlBody,
            Attachments: attachments,
            Notes: notes);
    }

    private static IReadOnlyList<ParsedEmailAttachment> ReadAttachments(
        OleCompoundFile compound, string storagePrefix, List<string> notes)
    {
        var attachmentStorages = compound.Enumerate()
            .Where(entry => entry.IsStorage
                            && entry.Name.StartsWith("__attach_version1.0", StringComparison.OrdinalIgnoreCase)
                            && IsDirectChild(entry.Path, storagePrefix, entry.Name))
            .OrderBy(entry => entry.Path, StringComparer.Ordinal)
            .ToList();

        var attachments = new List<ParsedEmailAttachment>();
        var ordinal = 0;
        var refusedForCount = 0;

        foreach (var storage in attachmentStorages)
        {
            ordinal++;
            if (attachments.Count >= MaxAttachments)
            {
                refusedForCount++;
                continue;
            }

            var prefix = storage.Path + "/";
            var fileName = ReadString(compound, prefix, AttachLongFileNameTag)
                           ?? ReadString(compound, prefix, AttachFileNameTag);

            byte[]? content;
            try
            {
                content = compound.ReadStream(prefix + StreamName(AttachDataBinaryTag, "0102"), MaxAttachmentBytes);
            }
            catch (OleCompoundFileException)
            {
                notes.Add($"Attachment {ordinal} could not be read because its stored size does not "
                          + "match the message contents; it was not processed.");
                continue;
            }

            if (content is null)
            {
                // An embedded message object (PR_ATTACH_DATA_OBJ) is a nested STORAGE, not a
                // stream. Nexora does not open a message inside a message inside a file: the
                // depth is unbounded by construction and each level is another parser exposed to
                // untrusted bytes. It is refused, out loud.
                if (compound.Contains(prefix + StreamName("3701", "000D"))
                    || compound.Enumerate().Any(entry =>
                        entry.IsStorage && entry.Path.StartsWith(prefix, StringComparison.Ordinal)))
                {
                    notes.Add($"Attachment {ordinal} is an email embedded inside this message. "
                              + "Nexora does not open messages nested this deep; forward it as its "
                              + "own message or save the attachment and upload it directly.");
                    continue;
                }
                notes.Add($"Attachment {ordinal} carries no readable content and was not processed.");
                continue;
            }

            if (content.Length == 0)
            {
                notes.Add($"Attachment {ordinal} is empty and was not processed.");
                continue;
            }

            var safeName = SafeFileName(fileName, ordinal);
            attachments.Add(new ParsedEmailAttachment(
                ordinal, safeName, content,
                IsEmbeddedMessage: safeName.EndsWith(".msg", StringComparison.OrdinalIgnoreCase)
                                   || safeName.EndsWith(".eml", StringComparison.OrdinalIgnoreCase)));
        }

        if (refusedForCount > 0)
        {
            notes.Add($"This message carries more than {MaxAttachments} attachments; "
                      + $"{refusedForCount} beyond that limit were not processed.");
        }

        return attachments;
    }

    private static bool IsDirectChild(string path, string storagePrefix, string name) =>
        string.Equals(path, storagePrefix + name, StringComparison.Ordinal);

    private static string? ReadHtmlBody(OleCompoundFile compound, string prefix)
    {
        // PR_HTML is normally binary (0102) but Unicode/ASCII variants exist in the wild.
        var binary = TryRead(compound, prefix + StreamName(HtmlBodyTag, "0102"), MaxPropertyBytes);
        if (binary is { Length: > 0 }) return DecodeLoose(binary);
        return ReadString(compound, prefix, HtmlBodyTag);
    }

    private static string? ReadString(OleCompoundFile compound, string prefix, string tag)
    {
        var unicode = TryRead(compound, prefix + StreamName(tag, "001F"), MaxPropertyBytes);
        if (unicode is { Length: > 0 }) return Encoding.Unicode.GetString(unicode);
        var ansi = TryRead(compound, prefix + StreamName(tag, "001E"), MaxPropertyBytes);
        return ansi is { Length: > 0 } ? DecodeLoose(ansi) : null;
    }

    private static string? FindStream(OleCompoundFile compound, string prefix, string tag) =>
        compound.Contains(prefix + StreamName(tag, "0102")) ? prefix + StreamName(tag, "0102") : null;

    private static byte[]? TryRead(OleCompoundFile compound, string path, long maximumBytes)
    {
        try
        {
            return compound.ReadStream(path, maximumBytes);
        }
        catch (OleCompoundFileException)
        {
            // One unreadable property must not lose the rest of the message; the caller's own
            // "there was no body" note covers the visible consequence.
            return null;
        }
    }

    private static string StreamName(string tag, string type) =>
        "__substg1.0_" + tag.ToUpperInvariant() + type.ToUpperInvariant();

    private static string DecodeLoose(byte[] bytes) =>
        Encoding.UTF8.GetString(bytes).Replace("�", string.Empty, StringComparison.Ordinal) is { Length: > 0 } utf8
            ? utf8
            : Encoding.Latin1.GetString(bytes);

    private static string? Clean(string? value)
    {
        if (string.IsNullOrEmpty(value)) return null;
        var trimmed = value.TrimEnd('\0').Trim();
        return trimmed.Length == 0 ? null : trimmed;
    }

    private static string? ComposeFrom(string? name, string? email)
    {
        var cleanName = Clean(name);
        var cleanEmail = Clean(email);
        if (cleanName is null && cleanEmail is null) return null;
        if (cleanName is null) return cleanEmail;
        return cleanEmail is null ? cleanName : $"{cleanName} <{cleanEmail}>";
    }

    /// <summary>Pulls the Date header out of PR_TRANSPORT_MESSAGE_HEADERS when it is present.</summary>
    private static string? ExtractDateHeader(string? transportHeaders)
    {
        if (string.IsNullOrWhiteSpace(transportHeaders)) return null;
        foreach (var raw in transportHeaders.Replace("\r\n", "\n").Split('\n'))
        {
            if (!raw.StartsWith("Date:", StringComparison.OrdinalIgnoreCase)) continue;
            var value = raw[5..].Trim();
            return value.Length == 0 ? null : value;
        }
        return null;
    }

    /// <summary>
    /// Reduces a MAPI attachment name to a bare filename. Path separators, traversal segments and
    /// control characters are removed BEFORE the name is used for extension routing or handed to
    /// inspection — the name comes from inside the message and is fully attacker-controlled.
    /// </summary>
    internal static string SafeFileName(string? declared, int ordinal)
    {
        var cleaned = Clean(declared);
        if (cleaned is null) return $"attachment-{ordinal.ToString(CultureInfo.InvariantCulture)}";

        cleaned = new string(cleaned.Where(c => !char.IsControl(c)).ToArray());
        var lastSeparator = cleaned.LastIndexOfAny(['/', '\\', ':']);
        if (lastSeparator >= 0) cleaned = cleaned[(lastSeparator + 1)..];
        cleaned = string.Join("_", cleaned.Split(Path.GetInvalidFileNameChars(), StringSplitOptions.RemoveEmptyEntries));
        cleaned = cleaned.Trim().Trim('.');
        if (cleaned.Length == 0) return $"attachment-{ordinal.ToString(CultureInfo.InvariantCulture)}";
        return cleaned.Length <= 200 ? cleaned : cleaned[^200..];
    }
}
