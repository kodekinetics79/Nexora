using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace ERP_RFQ_Automation.Ingestion.Triage;

/// <summary>
/// The four views of one email body that the intake door needs.
/// </summary>
/// <param name="Fresh">
/// The text THIS sender actually wrote, with every quoted thread, forwarded header block,
/// trailing confidentiality disclaimer and "Sent from my ..." footer removed. The signature
/// block is deliberately KEPT: in this segment the signature is usually the only place the
/// buying organisation is named, and the conversational extractor is told to read it.
/// This — and only this — is what gets submitted for extraction.
/// </param>
/// <param name="Quoted">Everything from the first quote boundary onwards, retained for audit.</param>
/// <param name="Signature">The detected trailing signature block, or null.</param>
/// <param name="ThreadDepth">How many quote levels/boundaries the message carries (0 = original).</param>
/// <param name="BodyEmptyAfterStrip">
/// True when nothing but a signature (or nothing at all) survives the strip — i.e. the sender
/// wrote no new words. Combined with "no attachments" this is positive evidence of noise.
/// </param>
public sealed record EmailBodyParts(
    string Fresh,
    string Quoted,
    string? Signature,
    int ThreadDepth,
    bool BodyEmptyAfterStrip);

/// <summary>
/// Splits an email body into fresh text vs quoted thread, deterministically and without IO.
///
/// WHY THIS EXISTS: a forwarded RFQ thread contains the original request three times. Feeding
/// the whole body to the extractor produces the same line items two or three times over (or,
/// worse, invents a "second inquiry" from the quoted copy). Submitting only <see cref="EmailBodyParts.Fresh"/>
/// is what makes a reply-chain safe to extract.
/// </summary>
public static class EmailBodyNormalizer
{
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(200);

    private const RegexOptions Opts =
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

    /// <summary>"On Tue, 4 Aug 2026 at 09:12, Ahmed &lt;a@x.ae&gt; wrote:"</summary>
    private static readonly Regex OnWroteLine = new(@"^\s*On\b.{0,400}?\bwrote:\s*$", Opts, RegexTimeout);

    /// <summary>The opening half of the same marker when the client wrapped it onto two lines.</summary>
    private static readonly Regex OnLineOpener = new(@"^\s*On\b.{0,300}$", Opts, RegexTimeout);
    private static readonly Regex WroteLineCloser = new(@"^.{0,300}\bwrote:\s*$", Opts, RegexTimeout);

    /// <summary>"-----Original Message-----", "--- Forwarded message ---", "Begin forwarded message:"</summary>
    private static readonly Regex OriginalMessageLine = new(
        @"^\s*(?:[-_=]{2,}\s*)?(?:original message|forwarded message|begin forwarded message)\s*(?:[-_=:]{2,})?\s*:?\s*$",
        Opts, RegexTimeout);

    private static readonly Regex FromHeaderLine = new(@"^\s*From:\s*\S", Opts, RegexTimeout);
    private static readonly Regex SentOrDateHeaderLine = new(@"^\s*(?:Sent|Date):\s*\S", Opts, RegexTimeout);
    private static readonly Regex ToOrSubjectHeaderLine = new(@"^\s*(?:To|Subject):\s*\S", Opts, RegexTimeout);

    private static readonly Regex SentFromMyLine = new(@"^\s*(?:Sent|Get)\s+(?:from|Outlook)\b.{0,120}$", Opts, RegexTimeout);
    private static readonly Regex DisclaimerParagraph = new(
        @"confidential|intended recipient|disclaim", Opts, RegexTimeout);

    private static readonly Regex SignatureDelimiter = new(@"^\s*--\s*$", Opts, RegexTimeout);
    private static readonly Regex EmailAddress = new(
        @"[\w.+-]+@[\w.-]+\.[A-Za-z]{2,}", Opts, RegexTimeout);
    private static readonly Regex PhoneNumber = new(
        @"\+\d[\d\s().\-]{6,}\d", Opts, RegexTimeout);

    /// <summary>Gulf/MENA legal-form and trade tokens that mark a corporate signature block.</summary>
    private static readonly Regex OrganisationSuffix = new(
        @"\b(?:LLC|L\.L\.C|FZE|FZCO|FZ-LLC|W\.L\.L|WLL|Trading|Est\.|Establishment|Contracting)\b",
        Opts, RegexTimeout);

    /// <summary>A quantity written against a unit of measure — the single strongest "this is a
    /// request for goods" token in conversational text. Used here only as a SAFETY guard so a
    /// disclaimer strip can never swallow a line that carries line-item content.</summary>
    internal static readonly Regex QuantityUnitToken = new(
        @"\b\d{1,6}\s*(?:nos?|pcs?|units?|sets?|mtrs?|m|kg|ltr)\b", Opts, RegexTimeout);

    /// <summary>A conversational request. Like <see cref="QuantityUnitToken"/> it is used here
    /// only as a guard: a line that asks for something is content, never signature.</summary>
    private static readonly Regex RequestVerbLine = new(
        @"\b(?:pls|please|kindly|require|need|quote|offer|supply)\b", Opts, RegexTimeout);

    /// <summary>A bare sign-off line that belongs to the signature block below it.</summary>
    private static readonly Regex ClosingLine = new(
        @"^\s*(?:best\s+)?(?:regards|thanks|thank you|sincerely|br|kind regards|warm regards)\s*[,.!]?\s*$",
        Opts, RegexTimeout);

    /// <summary>The number of trailing non-empty lines that may form a signature block.</summary>
    private const int MaxSignatureLines = 8;

    /// <summary>
    /// Replaces control characters the text inspector refuses with the whitespace they stand
    /// for, and drops the rest.
    ///
    /// <para>VERTICAL TAB and FORM FEED become newlines because that is what they mean in a
    /// mail body — Outlook writes VT for a soft line break, and turning it into a space would
    /// run two lines of a quantity table into one. NUL and the remaining C0/C1 controls carry
    /// no textual meaning and are removed outright.</para>
    ///
    /// <para>CR, LF and TAB pass through untouched: the inspector accepts them and the
    /// line-splitting below depends on them.</para>
    /// </summary>
    internal static string SanitizeControlCharacters(string body)
    {
        // Overwhelmingly the common case — walk once, allocate nothing.
        var needsWork = false;
        foreach (var character in body)
        {
            if (char.IsControl(character) && character is not '\r' and not '\n' and not '\t')
            {
                needsWork = true;
                break;
            }
        }
        if (!needsWork) return body;

        var builder = new System.Text.StringBuilder(body.Length);
        foreach (var character in body)
        {
            if (!char.IsControl(character) || character is '\r' or '\n' or '\t')
            {
                builder.Append(character);
                continue;
            }
            // A soft break is still a break.
            if (character is '\v' or '\f') builder.Append('\n');
            // Everything else is noise with no textual meaning: dropped.
        }
        return builder.ToString();
    }

    public static EmailBodyParts Normalize(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
            return new EmailBodyParts(string.Empty, string.Empty, null, 0, true);

        // CONTROL CHARACTERS OUT, FIRST.
        //
        // The normalized body is written to disk as `<subject>_body.txt` and then put through
        // DocumentFileInspectionService, which refuses any control character other than
        // CR/LF/TAB/FF — a rule written for files a stranger uploads. The body file is not
        // that: it is OUR artifact, generated from the message we just parsed, and it inherits
        // whatever the sender's client emitted.
        //
        // Outlook emits VERTICAL TAB (U+000B) as a soft line break inside a paragraph, so a
        // perfectly ordinary forwarded enquiry failed inspection with "The text file contains
        // binary or unsafe control characters". That refusal held the body component, the
        // barrier could never be satisfied, and the message could never become a Lead however
        // many attachments read cleanly — measured on a live customer RFQ.
        //
        // Cleaning here rather than relaxing the inspector is the important half: the gate
        // still protects genuine uploads byte-for-byte, and we stop handing it text we
        // ourselves made unclean.
        body = SanitizeControlCharacters(body);

        var lines = body.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');

        var boundary = FindQuoteBoundary(lines);
        var freshLines = (boundary < 0 ? lines : lines.Take(boundary)).ToList();
        var quoted = boundary < 0 ? string.Empty : string.Join("\n", lines.Skip(boundary)).Trim();
        var threadDepth = MeasureThreadDepth(lines);

        // Footers the sender's client appended, never content.
        freshLines = freshLines.Where(l => !SentFromMyLine.IsMatch(l)).ToList();
        freshLines = StripTrailingDisclaimers(freshLines);

        var fresh = string.Join("\n", freshLines).Trim();
        var signature = DetectSignature(fresh);
        var withoutSignature = signature is null
            ? fresh
            : fresh[..Math.Max(0, fresh.LastIndexOf(signature, StringComparison.Ordinal))].Trim();

        // FINAL SAFETY: "empty after strip" is the one signal that stops a message with no AI
        // call at all, so it must never fire on text that asks for something. If the fresh
        // text carries a quantity+unit token or a request verb ANYWHERE — signature block
        // included — it is not empty, whatever the block detection concluded.
        var emptyAfterStrip = string.IsNullOrWhiteSpace(withoutSignature)
            && !QuantityUnitToken.IsMatch(fresh)
            && !RequestVerbLine.IsMatch(fresh);

        return new EmailBodyParts(
            Fresh: fresh,
            Quoted: quoted,
            Signature: signature,
            ThreadDepth: threadDepth,
            BodyEmptyAfterStrip: emptyAfterStrip);
    }

    /// <summary>Index of the first line that begins quoted/forwarded material, or -1.</summary>
    private static int FindQuoteBoundary(IReadOnlyList<string> lines)
    {
        for (var i = 0; i < lines.Count; i++)
        {
            if (IsQuoteBoundary(lines, i))
                return i;
        }
        return -1;
    }

    private static bool IsQuoteBoundary(IReadOnlyList<string> lines, int i)
    {
        var line = lines[i];
        if (line.TrimStart().StartsWith(">", StringComparison.Ordinal))
            return true;
        if (OriginalMessageLine.IsMatch(line))
            return true;
        if (OnWroteLine.IsMatch(line))
            return true;
        // The same attribution wrapped onto a second line by the sending client.
        if (OnLineOpener.IsMatch(line) && i + 1 < lines.Count && WroteLineCloser.IsMatch(lines[i + 1]))
            return true;
        // An inlined header block: From: ... Sent:/Date: ... To:/Subject: ...
        if (FromHeaderLine.IsMatch(line))
        {
            var window = lines.Skip(i + 1).Take(4).ToList();
            if (window.Any(SentOrDateHeaderLine.IsMatch) && window.Any(ToOrSubjectHeaderLine.IsMatch))
                return true;
        }
        return false;
    }

    /// <summary>
    /// How deep the thread runs: the number of quote boundaries anywhere in the body, or the
    /// deepest '&gt;' nesting, whichever is larger. A three-deep forward reports 3.
    /// </summary>
    private static int MeasureThreadDepth(IReadOnlyList<string> lines)
    {
        // Markers are counted with any quote prefix REMOVED: a three-deep forward nests its
        // "-----Original Message-----" inside the previous level's "> " quoting, and a
        // depth that ignored that would report 1 for a thread that is plainly three deep.
        var unquoted = lines.Select(StripQuotePrefix).ToList();
        var markers = 0;
        var maxAngle = 0;

        for (var i = 0; i < lines.Count; i++)
        {
            maxAngle = Math.Max(maxAngle, QuoteDepth(lines[i]));
            if (OriginalMessageLine.IsMatch(unquoted[i])
                || OnWroteLine.IsMatch(unquoted[i])
                || (OnLineOpener.IsMatch(unquoted[i]) && i + 1 < unquoted.Count
                    && WroteLineCloser.IsMatch(unquoted[i + 1])))
            {
                markers++;
                continue;
            }
            if (FromHeaderLine.IsMatch(unquoted[i]))
            {
                var window = unquoted.Skip(i + 1).Take(4).ToList();
                if (window.Any(SentOrDateHeaderLine.IsMatch) && window.Any(ToOrSubjectHeaderLine.IsMatch))
                    markers++;
            }
        }
        return Math.Max(markers, maxAngle);
    }

    private static int QuoteDepth(string line)
    {
        var depth = 0;
        foreach (var c in line.TrimStart())
        {
            if (c == '>') depth++;
            else if (c != ' ') break;
        }
        return depth;
    }

    private static string StripQuotePrefix(string line)
    {
        var trimmed = line.TrimStart();
        var index = 0;
        while (index < trimmed.Length && (trimmed[index] == '>' || trimmed[index] == ' '))
            index++;
        return index == 0 ? line : trimmed[index..];
    }

    /// <summary>
    /// Drops trailing confidentiality boilerplate. Deliberately conservative: it stops at the
    /// first paragraph that is not boilerplate, and it will never drop a paragraph that carries
    /// a quantity+unit token — losing a line item to a disclaimer strip is exactly the failure
    /// this whole work item exists to prevent.
    /// </summary>
    private static List<string> StripTrailingDisclaimers(List<string> lines)
    {
        var paragraphs = SplitParagraphs(lines);
        while (paragraphs.Count > 0)
        {
            var last = paragraphs[^1];
            var text = string.Join("\n", last);
            if (string.IsNullOrWhiteSpace(text))
            {
                paragraphs.RemoveAt(paragraphs.Count - 1);
                continue;
            }
            if (!DisclaimerParagraph.IsMatch(text) || QuantityUnitToken.IsMatch(text))
                break;
            paragraphs.RemoveAt(paragraphs.Count - 1);
        }

        var result = new List<string>();
        for (var i = 0; i < paragraphs.Count; i++)
        {
            if (i > 0) result.Add(string.Empty);
            result.AddRange(paragraphs[i]);
        }
        return result;
    }

    private static List<List<string>> SplitParagraphs(List<string> lines)
    {
        var paragraphs = new List<List<string>>();
        var current = new List<string>();
        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                if (current.Count > 0)
                {
                    paragraphs.Add(current);
                    current = new List<string>();
                }
                continue;
            }
            current.Add(line);
        }
        if (current.Count > 0) paragraphs.Add(current);
        return paragraphs;
    }

    /// <summary>
    /// The trailing signature block, verbatim, or null. Recognised either by the RFC 3676
    /// "-- " delimiter or by contact evidence (an address, an international phone number, or a
    /// Gulf legal-form token) inside the last <see cref="MaxSignatureLines"/> non-empty lines.
    /// </summary>
    public static string? DetectSignature(string fresh)
    {
        if (string.IsNullOrWhiteSpace(fresh)) return null;
        var lines = fresh.Split('\n');

        for (var i = lines.Length - 1; i >= 0; i--)
        {
            if (!SignatureDelimiter.IsMatch(lines[i])) continue;
            var block = string.Join("\n", lines.Skip(i + 1)).Trim();
            return string.IsNullOrWhiteSpace(block) ? null : block;
        }

        // Indices of the trailing window of non-empty lines, oldest first.
        var window = new List<int>();
        for (var i = lines.Length - 1; i >= 0 && window.Count < MaxSignatureLines; i--)
        {
            if (!string.IsNullOrWhiteSpace(lines[i])) window.Add(i);
        }
        window.Reverse();

        // A one-line message is content, never a footer — a single line naming a company is
        // an enquiry from that company, not a signature.
        if (lines.Length <= 1) return null;

        foreach (var index in window)
        {
            var line = lines[index];
            if (!EmailAddress.IsMatch(line) && !PhoneNumber.IsMatch(line) && !OrganisationSuffix.IsMatch(line))
                continue;
            var start = ExtendSignatureUpwards(lines, index);
            var block = string.Join("\n", lines.Skip(start)).Trim();
            return string.IsNullOrWhiteSpace(block) ? null : block;
        }
        return null;
    }

    /// <summary>
    /// Walks up from the contact line so the closing and the sender's name belong to the
    /// signature ("Regards, / Ahmed Al Mansoori / Al Noor Trading LLC"), which is what lets a
    /// reply that added nothing but a sign-off be recognised as empty.
    ///
    /// It STOPS HARD at anything that could be a request — a quantity+unit token, a request
    /// verb, or a long sentence. Absorbing a line of content into "signature" would let the
    /// gate call a real enquiry empty, which is the single worst outcome in this whole path.
    /// </summary>
    private static int ExtendSignatureUpwards(string[] lines, int anchor)
    {
        var start = anchor;
        for (var i = anchor - 1; i >= 0; i--)
        {
            var line = lines[i];
            if (string.IsNullOrWhiteSpace(line))
            {
                // A blank line ends the block — unless the line just above it is a bare
                // closing, which belongs to the signature.
                var previous = i - 1;
                while (previous >= 0 && string.IsNullOrWhiteSpace(lines[previous])) previous--;
                if (previous >= 0 && ClosingLine.IsMatch(lines[previous]))
                {
                    start = previous;
                    i = previous; // continue above the closing
                    continue;
                }
                break;
            }
            if (IsContentLine(line)) break;
            start = i;
        }
        return start;
    }

    /// <summary>Anything that could carry a request is content, never signature.</summary>
    private static bool IsContentLine(string line)
        => QuantityUnitToken.IsMatch(line)
           || RequestVerbLine.IsMatch(line)
           || line.Trim().Length > 60;
}
