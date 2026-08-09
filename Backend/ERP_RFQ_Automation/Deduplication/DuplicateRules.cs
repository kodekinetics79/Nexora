using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using ERP_RFQ_Automation.Models;

namespace ERP_RFQ_Automation.Deduplication;

/// <summary>
/// FR-RFQ-06. The duplicate rule, as pure predicates over two leads.
///
/// <para>The requirement is explicit that a possible duplicate is flagged for human review
/// <b>before a new record is created</b>. That makes the rule a pre-persistence decision, so it
/// deliberately holds no <see cref="ErpRfqAutomationContext"/>, performs no I/O and stamps
/// nothing: the caller
/// (<see cref="ERP_RFQ_Automation.LeadIdentity.LeadIdentityApplicationService"/>) evaluates it
/// against already-loaded tenant candidates while the incoming lead is still an unsaved
/// in-memory object, and raises a review occurrence instead of inserting a row.</para>
///
/// <para>This replaces the earlier post-persistence detector, which committed the lead and then
/// stamped a duplicate flag on it — the one behaviour FR-RFQ-06 forbids. The rule text below is
/// preserved from that detector so detection behaviour is unchanged; only the moment it runs and
/// the queue it feeds have moved.</para>
/// </summary>
public static class DuplicateRules
{
    /// <summary>Two closing dates count as "overlapping" within this window.</summary>
    public static readonly TimeSpan BidDateTolerance = TimeSpan.FromDays(2);

    /// <summary>Share of the smaller line set that must match for two inquiries to be "same item".</summary>
    public const double ItemOverlapThreshold = 0.6;

    /// <summary>Lead status id meaning the inquiry was rejected; rejected leads are never duplicate targets.</summary>
    private const long RejectedLeadStatusId = 25;

    private static readonly Regex NonAlphaNum = new("[^a-z0-9]+", RegexOptions.Compiled);

    /// <summary>
    /// Why <paramref name="incoming"/> may be a duplicate of <paramref name="existing"/>, or
    /// <see langword="null"/> when the rule does not fire. The string is reviewer-facing and is
    /// recorded verbatim on the review occurrence.
    /// </summary>
    public static string? DuplicateReason(Lead incoming, Lead existing)
    {
        if (ReferenceEquals(incoming, existing)) return null;
        if (existing.Id != 0 && incoming.Id == existing.Id) return null;
        if (existing.LeadStatusId == RejectedLeadStatusId) return null;

        // (a) The buyer's own RFQ number is the strongest signal they have given us. When both
        // sides carry one and they are the same string, a human decides — even if the extracted
        // line items look nothing alike, because one of the two extractions may simply be poor.
        var incomingRfq = Normalize(incoming.Rfqno);
        if (incomingRfq is not null && Normalize(existing.Rfqno) == incomingRfq)
            return "The customer's RFQ number matches an existing inquiry.";

        // (b) The requirement's own wording: same buyer, same item, overlapping dates.
        var buyer = CustomerKey(incoming.Clientemail, incoming.BuyersName);
        if (buyer is null || buyer != CustomerKey(existing.Clientemail, existing.BuyersName))
            return null;

        if (!DatesOverlap(incoming.BidClosingDate, existing.BidClosingDate))
            return null;

        var incomingKeys = ItemKeys(incoming.LeadItems);
        if (incomingKeys.Count == 0) return null;

        var existingKeys = ItemKeys(existing.LeadItems);
        return Overlap(incomingKeys, existingKeys) >= ItemOverlapThreshold
            ? "Same buyer, overlapping closing dates and matching line items."
            : null;
    }

    /// <summary>Casefold and strip everything that is not a-z / 0-9. Null when nothing survives.</summary>
    public static string? Normalize(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var s = NonAlphaNum.Replace(raw.ToLowerInvariant(), "");
        return s.Length == 0 ? null : s;
    }

    /// <summary>
    /// Buyer identity: the lowercased client email when it is a real customer address, otherwise
    /// the normalized buyer name. Internal pipeline placeholder addresses identify the pipeline
    /// rather than a customer, so they must never act as a shared key across every extracted lead.
    /// </summary>
    public static string? CustomerKey(string? email, string? buyersName)
    {
        var e = email?.Trim().ToLowerInvariant();
        if (!string.IsNullOrEmpty(e)
            && !e.EndsWith("@pipeline.local", StringComparison.Ordinal)
            && e != "system@rfq.com")
            return "e:" + e;

        var n = Normalize(buyersName);
        return n is null ? null : "n:" + n;
    }

    /// <summary>
    /// Two closing dates overlap when they are within <see cref="BidDateTolerance"/>. Two absent
    /// dates also overlap: neither document tells us they are different deadlines, so the other
    /// two signals decide. One absent date is not evidence of agreement and does not overlap.
    /// </summary>
    public static bool DatesOverlap(DateTime? a, DateTime? b)
    {
        if (a is null && b is null) return true;
        if (a is null || b is null) return false;
        return (a.Value - b.Value).Duration() <= BidDateTolerance;
    }

    /// <summary>Normalized union of manufacturer part number, material code and customer RFQ number.</summary>
    public static HashSet<string> ItemKeys(IEnumerable<LeadItem>? items)
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);
        if (items is null) return keys;

        foreach (var item in items)
        {
            if (Normalize(item.ManufacturerPartNumber) is { } m) keys.Add("m:" + m);
            if (Normalize(item.ItemMaterialCode) is { } c) keys.Add("c:" + c);
            if (Normalize(item.CustomerRfqno) is { } r) keys.Add("r:" + r);
        }
        return keys;
    }

    /// <summary>Intersection over the smaller set. Zero when either side is empty.</summary>
    public static double Overlap(HashSet<string> a, HashSet<string> b)
    {
        if (a.Count == 0 || b.Count == 0) return 0;
        return (double)a.Count(b.Contains) / Math.Min(a.Count, b.Count);
    }
}
