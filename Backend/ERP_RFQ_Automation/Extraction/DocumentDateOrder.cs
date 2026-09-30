using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace ERP_RFQ_Automation.Extraction;

/// <summary>The order a document writes its numeric dates in.</summary>
public enum DateOrder
{
    /// <summary>The document itself does not say. Numeric dates keep the Gulf day-first default and stay flagged.</summary>
    Unknown = 0,
    DayFirst = 1,
    MonthFirst = 2,
}

/// <summary>What established a document's date order, in words a reviewer can check.</summary>
public sealed record DateOrderEvidence(DateOrder Order, string Reason);

/// <summary>
/// One document, one date order. "9/8/2026" is 9 August to a Riyadh buyer and 8 September to an
/// SAP Ariba print, and the token alone cannot say which. The document can:
/// <list type="number">
/// <item>An SAP Ariba event print ("This is a print version of the event", the
/// <c>ariba.sourcing.rfxui</c> class marker, or the "Publish time" / "Due date" timing rules) is
/// rendered by the portal in the downloading user's US locale — month-first on every SEC and
/// Aramco print on file. <c>BuyerTerms.FormatDue</c> already reads the same token month-first.</item>
/// <item>Any numeric date in the same document with a component above 12 fixes the order for the
/// rest of it: "9/13/2026" can only be 13 September, so "9/3/2026" beside it is 3 September.</item>
/// </list>
/// When neither applies the order is <see cref="DateOrder.Unknown"/> and the caller keeps its
/// day-first default and its "confirm it" flag.
/// </summary>
public static class DocumentDateOrder
{
    /// <summary>Only the top of a document carries the print's own banner and timing rules.</summary>
    private const int MaxTextsScanned = 4000;

    private static readonly Regex NumericDate = new(
        @"(?<![\d/.\-])(\d{1,2})\s*([/.\-])\s*(\d{1,2})\s*\2\s*(\d{4})(?!\d)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// The Ariba print signature, when any of <paramref name="texts"/> carries it. Month-first,
    /// because that is how the portal renders every date field of the print.
    /// </summary>
    public static DateOrderEvidence? FromAribaPrint(IEnumerable<string?> texts)
    {
        var sawPublish = false;
        var sawDue = false;
        foreach (var text in texts.Take(MaxTextsScanned))
        {
            if (string.IsNullOrWhiteSpace(text)) continue;
            if (text.Contains("ariba.sourcing.rfxui", StringComparison.OrdinalIgnoreCase)
                || text.Contains("print version of the event", StringComparison.OrdinalIgnoreCase))
                return new DateOrderEvidence(DateOrder.MonthFirst,
                    "the document is an SAP Ariba event print, which writes its dates month-first");

            var label = text.Trim().TrimEnd(':').Trim();
            if (label.Equals("Publish time", StringComparison.OrdinalIgnoreCase)) sawPublish = true;
            else if (label.Equals("Due date", StringComparison.OrdinalIgnoreCase)) sawDue = true;
            if (sawPublish && sawDue)
                return new DateOrderEvidence(DateOrder.MonthFirst,
                    "the document carries SAP Ariba's \"Publish time\" and \"Due date\" timing rules, which are written month-first");
        }
        return null;
    }

    /// <summary>
    /// The order proved by the document's own unambiguous numeric dates, or null when there are
    /// none or they disagree (a document whose dates contradict each other proves nothing).
    /// </summary>
    public static DateOrderEvidence? FromUnambiguousDates(IEnumerable<string?> texts)
    {
        string? dayFirstProof = null;
        string? monthFirstProof = null;
        foreach (var text in texts.Take(MaxTextsScanned))
        {
            if (string.IsNullOrWhiteSpace(text)) continue;
            foreach (Match match in NumericDate.Matches(RfqDateParser.NormalizeDigits(text)))
            {
                if (!int.TryParse(match.Groups[1].Value, out var first)
                    || !int.TryParse(match.Groups[3].Value, out var second))
                    continue;
                if (first is < 1 or > 31 || second is < 1 or > 31) continue;
                if (first > 12 && second <= 12) dayFirstProof ??= match.Value.Trim();
                else if (second > 12 && first <= 12) monthFirstProof ??= match.Value.Trim();
            }
            if (dayFirstProof is not null && monthFirstProof is not null) return null;
        }

        if (monthFirstProof is not null && dayFirstProof is null)
            return new DateOrderEvidence(DateOrder.MonthFirst,
                $"the document's own date \"{monthFirstProof}\" can only be read month-first, which is certain, so its other dates are read the same way");
        if (dayFirstProof is not null && monthFirstProof is null)
            return new DateOrderEvidence(DateOrder.DayFirst,
                $"the document's own date \"{dayFirstProof}\" can only be read day-first, which is certain, so its other dates are read the same way");
        return null;
    }

    /// <summary>The Ariba signature first, then the document's own unambiguous dates.</summary>
    public static DateOrderEvidence? Detect(IReadOnlyCollection<string?> texts)
        => FromAribaPrint(texts) ?? FromUnambiguousDates(texts);
}
