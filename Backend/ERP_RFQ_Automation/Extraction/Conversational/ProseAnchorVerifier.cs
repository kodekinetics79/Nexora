using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using ERP_RFQ_Automation.Services.Interfaces;

namespace ERP_RFQ_Automation.Extraction.Conversational;

/// <param name="Items">The items that survived verification, in document order.</param>
/// <param name="UnanchoredItemCount">Items dropped because their span could not be verified.</param>
/// <param name="CeilingDroppedCount">Items dropped because the message cannot support that many.</param>
/// <param name="Ceiling">The computed hard ceiling on item count for this message.</param>
/// <param name="Diagnostics">Human-readable reasons, one per drop class.</param>
public sealed record ProseAnchorVerification(
    List<LeadItemData> Items,
    int UnanchoredItemCount,
    int CeilingDroppedCount,
    int Ceiling,
    List<string> Diagnostics)
{
    public bool Clean => UnanchoredItemCount == 0 && CeilingDroppedCount == 0;
}

/// <summary>
/// CONSERVATION WITHOUT A ROW COUNT.
///
/// The chunked extractor can assert "Σ chunk items == parsed rows" because a document has
/// rows. A three-line email has none — so the only thing left to conserve is ANCHORS: every
/// item must carry a verbatim quote (<see cref="LeadItemData.SourceSpan"/>) that provably
/// occurs in the text we submitted. An item whose span cannot be found in the message was not
/// read out of the message; it was invented, and it is dropped.
///
/// This check is fully deterministic and is the ONLY invention guard that survives the
/// fabricated-confidence problem: the model's self-reported confidence tells us nothing, but
/// whether a quote occurs in a string is a fact.
/// </summary>
public static class ProseAnchorVerifier
{
    /// <summary>Longest permitted anchor. A "span" longer than this is a paraphrase of the
    /// whole message, not a citation of one request. Raised from 120: a single technical line
    /// ("2 x 300mm hot-dip galvanised perforated cable tray, 2.5m length, with coupler plates
    /// and M8 fixings, to BS EN 61537") is a legitimate one-request citation and its natural
    /// verbatim quote exceeds 120 characters, so the old bound marked real lines unverifiable
    /// purely for being descriptive.</summary>
    public const int MaxSpanLength = 400;

    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(200);
    private const RegexOptions Opts = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

    /// <summary>A digit adjacent to a unit of measure — one anchorable request.</summary>
    private static readonly Regex QuantityUomToken = new(
        @"\b\d{1,6}\s*(?:nos?|pcs?|units?|sets?|mtrs?|m|kg|ltr)\b", Opts, RegexTimeout);

    /// <summary>A bullet or an enumerated line — the other way a prose message lists items.</summary>
    private static readonly Regex BulletOrNumberPrefix = new(
        @"^\s*(?:[-*•·]|\(?\d{1,3}[.)])\s+\S", RegexOptions.CultureInvariant, RegexTimeout);

    private static readonly Regex Whitespace = new(@"\s+", RegexOptions.CultureInvariant, RegexTimeout);

    /// <summary>
    /// The governed redaction markers an EXTERNAL provider sees in place of contact details
    /// (<c>OllamaLlmService.PrepareProviderInput</c>). The model can only quote what it was
    /// shown, so a span may legitimately contain one of these while the submitted text
    /// contains the original characters. Matching is therefore done fragment-by-fragment
    /// around the markers — never by pretending the span is absent, which would DROP a real
    /// line item whose wording happened to look like a phone number.
    /// </summary>
    private static readonly Regex RedactionMarker = new(
        @"\[REDACTED_[A-Z]+\]", RegexOptions.CultureInvariant, RegexTimeout);

    public static ProseAnchorVerification Verify(string? submittedText, IReadOnlyList<LeadItemData>? items)
    {
        var diagnostics = new List<string>();
        var ceiling = ComputeCeiling(submittedText);
        if (items is null || items.Count == 0)
            return new ProseAnchorVerification(new List<LeadItemData>(), 0, 0, ceiling, diagnostics);

        var haystack = Collapse(submittedText);
        var kept = new List<LeadItemData>(items.Count);
        var unanchored = 0;
        // Text already claimed by an earlier item's anchor. Claiming REGIONS rather than
        // advancing a single cursor keeps both properties that matter, which a cursor could
        // only ever trade against each other:
        //   * one sentence cannot be quoted twice to manufacture two line items — the second
        //     quote finds only claimed text, so it stays unverified;
        //   * items returned out of document order (a model that groups by product family)
        //     still verify against their own, unclaimed text, instead of every item after the
        //     first out-of-order one failing behind an advanced cursor. That was a cliff.
        var claimed = new List<(int Start, int End)>();

        foreach (var item in items)
        {
            var span = Collapse(item.SourceSpan);
            var tooLong = (item.SourceSpan?.Length ?? 0) > MaxSpanLength;
            var verified = false;

            if (!string.IsNullOrEmpty(span) && !tooLong && haystack.Length > 0)
            {
                var hit = LocateUnclaimedSpan(haystack, span, claimed);
                if (hit is not null)
                {
                    claimed.Add(hit.Value);
                    verified = true;
                }
            }

            // KEEP IT EITHER WAY. An unverified span means "this quote could not be found",
            // which is a reason to show a human the line — not a reason to delete a request
            // the customer may really have made. Deleting was strictly worse than flagging:
            // every lead from this path goes to review regardless, so a hallucinated line is
            // caught by the reviewer, whereas a silently deleted real line is caught by
            // nobody and costs the bid. The counts below still drive that review flag.
            if (!verified) unanchored++;
            kept.Add(item);
        }

        if (unanchored > 0)
            diagnostics.Add(
                $"{unanchored} item(s) kept but UNVERIFIED: the quoted source span could not be "
                + "located in the submitted message text. Confirm these against the original.");

        // The ceiling is a signal, never a knife. It is derived from quantity+unit tokens and
        // bullet prefixes, so a plain-prose RFQ using units the token list does not know
        // ("each", "box", "roll", "lot", or Arabic units) computes a ceiling of 1 — which
        // used to discard every line but the first, silently, on a perfectly real enquiry.
        var overCeiling = Math.Max(0, kept.Count - ceiling);
        if (overCeiling > 0)
            diagnostics.Add(
                $"{overCeiling} item(s) beyond the {ceiling} the message text obviously supports; "
                + "all are kept for review rather than discarded.");

        return new ProseAnchorVerification(kept, unanchored, overCeiling, ceiling, diagnostics);
    }

    /// <summary>
    /// The most items this message could honestly be asking for: the number of lines that
    /// carry a quantity+unit token or a bullet/enumeration prefix, and — because one prose
    /// sentence routinely carries several requests ("40 nos cable tray 300mm and 12 nos
    /// junction box IP65") — never fewer than the number of quantity+unit tokens in the whole
    /// text. Minimum 1, so an unquantified single request ("please quote cable tray 300mm")
    /// still has room to exist.
    /// </summary>
    public static int ComputeCeiling(string? submittedText)
    {
        if (string.IsNullOrWhiteSpace(submittedText)) return 1;

        var lines = submittedText.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var signalLines = lines.Count(l =>
            !string.IsNullOrWhiteSpace(l) && (QuantityUomToken.IsMatch(l) || BulletOrNumberPrefix.IsMatch(l)));
        var quantityTokens = QuantityUomToken.Matches(submittedText).Count;
        return Math.Max(1, Math.Max(signalLines, quantityTokens));
    }

    /// <summary>
    /// Where the span ENDS in the haystack when it occurs at or after <paramref name="from"/>,
    /// or -1 when it does not occur there at all. Redaction markers split the span into
    /// fragments that must each occur, in order.
    /// </summary>
    /// <summary>
    /// The first occurrence of <paramref name="span"/> that does not overlap text an earlier
    /// item already claimed, or null when every occurrence is claimed (or there are none).
    /// Scanning forward from each successive candidate is what lets a legitimate repeated
    /// phrase anchor to its OWN occurrence while a re-quote of one sentence finds nothing free.
    /// </summary>
    private static (int Start, int End)? LocateUnclaimedSpan(
        string haystack, string span, List<(int Start, int End)> claimed)
    {
        var from = 0;
        while (from <= haystack.Length)
        {
            var end = LocateSpan(haystack, span, from);
            if (end < 0) return null;
            var start = FindStart(haystack, span, from, end);
            if (!claimed.Any(c => start < c.End && c.Start < end))
                return (start, end);
            // Overlaps a claim: step past this occurrence's start and look for another.
            from = start + 1;
        }
        return null;
    }

    /// <summary>
    /// Where the match that ended at <paramref name="end"/> began. For a plain span this is
    /// simple arithmetic; for a redaction-fragmented span the first fragment's position is the
    /// true start, so the claimed region covers the whole quoted stretch rather than its tail.
    /// </summary>
    private static int FindStart(string haystack, string span, int from, int end)
    {
        if (!RedactionMarker.IsMatch(span))
            return Math.Max(from, end - span.Length);
        var first = RedactionMarker.Split(span)
            .Select(f => f.Trim())
            .FirstOrDefault(f => f.Length >= 3);
        if (string.IsNullOrEmpty(first)) return Math.Max(from, end - 1);
        var at = haystack.IndexOf(first, from, StringComparison.OrdinalIgnoreCase);
        return at < 0 ? Math.Max(from, end - 1) : at;
    }

    private static int LocateSpan(string haystack, string span, int from)
    {
        if (from > haystack.Length) return -1;

        if (!RedactionMarker.IsMatch(span))
        {
            var at = haystack.IndexOf(span, from, StringComparison.OrdinalIgnoreCase);
            return at < 0 ? -1 : at + span.Length;
        }

        var fragments = RedactionMarker.Split(span)
            .Select(f => f.Trim())
            .Where(f => f.Length >= 3)
            .ToList();
        if (fragments.Count == 0) return -1; // nothing verifiable survived redaction

        var cursor = from;
        foreach (var fragment in fragments)
        {
            if (cursor > haystack.Length) return -1;
            var at = haystack.IndexOf(fragment, cursor, StringComparison.OrdinalIgnoreCase);
            if (at < 0) return -1;
            cursor = at + fragment.Length;
        }
        return cursor;
    }

    /// <summary>Ordinal comparison with whitespace collapsed: a model that re-wraps a quoted
    /// line is still quoting it, but a model that changes a word is not.</summary>
    private static string Collapse(string? value)
        => string.IsNullOrWhiteSpace(value) ? string.Empty : Whitespace.Replace(value, " ").Trim();
}
