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
    /// whole message, not a citation of one request.</summary>
    public const int MaxSpanLength = 120;

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
        var searchFrom = 0;

        foreach (var item in items)
        {
            var span = Collapse(item.SourceSpan);
            if (string.IsNullOrEmpty(span) || (item.SourceSpan?.Length ?? 0) > MaxSpanLength)
            {
                unanchored++;
                continue;
            }
            if (haystack.Length == 0)
            {
                unanchored++;
                continue;
            }

            // Monotonic and non-overlapping: each span must occur AFTER the previous span
            // ended. This is what stops the model from quoting the same sentence twice to
            // manufacture two line items out of one request.
            var end = LocateSpan(haystack, span, searchFrom);
            if (end < 0)
            {
                unanchored++;
                continue;
            }
            searchFrom = end;
            kept.Add(item);
        }

        if (unanchored > 0)
            diagnostics.Add(
                $"{unanchored} item(s) dropped: the quoted source span does not occur, in order, "
                + "in the submitted message text.");

        var ceilingDropped = 0;
        if (kept.Count > ceiling)
        {
            ceilingDropped = kept.Count - ceiling;
            kept = kept.Take(ceiling).ToList();
            diagnostics.Add(
                $"{ceilingDropped} item(s) dropped: the message supports at most {ceiling} "
                + "requestable item(s).");
        }

        return new ProseAnchorVerification(kept, unanchored, ceilingDropped, ceiling, diagnostics);
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
    private static int LocateSpan(string haystack, string span, int from)
    {
        if (from > haystack.Length) return -1;

        if (!RedactionMarker.IsMatch(span))
        {
            var at = haystack.IndexOf(span, from, StringComparison.Ordinal);
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
            var at = haystack.IndexOf(fragment, cursor, StringComparison.Ordinal);
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
