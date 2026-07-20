using System;
using System.Collections.Generic;
using System.Linq;
using ERP_RFQ_Automation.DTOs.DocumentIntelligence;
using ERP_RFQ_Automation.Services.Interfaces;

namespace ERP_RFQ_Automation.Extraction;

/// <summary>
/// Decision logic for splitting ONE source document into N Leads when it verifiably
/// contains N distinct inquiries. Pure/static so the rules are directly unit-testable.
///
/// Guiding rule: NEVER guess-split. Splitting only happens when the grouping evidence is
/// unambiguous and confident; anything else falls back to today's single-lead +
/// NeedsReview behavior (the reviewer decides). Item-count conservation holds by
/// construction: groups are a strict partition of the extracted items, so
/// Σ per-group items == total extracted items.
/// </summary>
public static class MultiInquirySplitter
{
    /// <summary>Safety valve: more groups than this in one document is treated as an
    /// extraction artifact (e.g. a label per row), not a real multi-inquiry file.</summary>
    public const int MaxGroups = 10;

    /// <summary>Minimum average per-item grouping confidence for an LLM-labeled split.
    /// Mirrors the pipeline's MinAcceptableConfidence.</summary>
    public const double MinGroupingConfidence = 0.60;

    /// <summary>
    /// LLM path: partition the merged result's items by their <c>InquiryGroup</c> label.
    /// Returns the per-group results (2..MaxGroups, in first-seen label order), or null
    /// when the document must NOT be split. Split requires:
    ///   * at least 2 distinct non-empty labels,
    ///   * EVERY item labeled (an unlabeled item makes the grouping ambiguous),
    ///   * group count &lt;= <see cref="MaxGroups"/>,
    ///   * average InquiryGroupConfidence &gt;= <see cref="MinGroupingConfidence"/>
    ///     (a missing confidence counts as 0 — unconfident labels never split).
    /// </summary>
    public static List<LeadExtractionResult>? TrySplitByItemGroups(LeadExtractionResult merged)
    {
        var items = merged.Items ?? new List<LeadItemData>();
        if (items.Count < 2)
            return null;

        // Every item must carry a non-empty group label; otherwise the grouping is
        // ambiguous (items could belong to any inquiry) -> never guess.
        if (items.Any(i => string.IsNullOrWhiteSpace(i.InquiryGroup)))
            return null;

        // Partition in first-seen order, case-insensitively on the trimmed label.
        var groups = new List<(string Label, List<LeadItemData> Items)>();
        var index = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in items)
        {
            var label = item.InquiryGroup!.Trim();
            if (!index.TryGetValue(label, out var at))
            {
                index[label] = groups.Count;
                groups.Add((label, new List<LeadItemData>()));
                at = groups.Count - 1;
            }
            groups[at].Items.Add(item);
        }

        if (groups.Count < 2 || groups.Count > MaxGroups)
            return null;

        var avgGroupingConfidence = items.Average(i => i.InquiryGroupConfidence ?? 0.0);
        if (avgGroupingConfidence < MinGroupingConfidence)
            return null;

        // Assemble one result per group: the group label becomes the group's RFQ number
        // (the persister's junk filter nulls implausible labels); all other header fields
        // (buyer, dates, inquiry type) are inherited from the shared document header.
        // Confidence is recomputed per group with the pipeline's 0.4/0.6 weighting.
        return groups
            .Select(g => merged with
            {
                Rfqno = g.Label,
                // The label was asserted by the model as this group's identifier.
                RfqnoConfidence = g.Items.Average(i => i.InquiryGroupConfidence ?? 0.0),
                Items = g.Items,
                OverallConfidence = ComputeOverallConfidence(merged, g.Items)
            })
            .ToList();
    }

    /// <summary>
    /// Structured (spreadsheet/CSV) path: the deterministic normalizer already grouped
    /// rows into <see cref="CanonicalRfqDocument"/>s keyed by (RfqNo | BuyerName).
    /// Splitting is allowed only when every group is a REAL, identified inquiry:
    ///   * 2..MaxGroups groups,
    ///   * every group has at least one line item,
    ///   * every group carries an extracted RfqNo or BuyerName (a group whose key fell
    ///     back to a bare row number is an ungrouped fragment -&gt; ambiguous -&gt; no split).
    /// </summary>
    public static bool ShouldSplitStructured(IReadOnlyList<CanonicalRfqDocument> documents)
    {
        if (documents is null || documents.Count < 2 || documents.Count > MaxGroups)
            return false;

        foreach (var doc in documents)
        {
            if (doc.LineItems.Count == 0)
                return false;
            var hasIdentity = !string.IsNullOrWhiteSpace(doc.RfqNo.Value)
                              || !string.IsNullOrWhiteSpace(doc.BuyerName.Value);
            if (!hasIdentity)
                return false;
        }
        return true;
    }

    /// <summary>Same weighting the extraction service uses (header 0.4 / items 0.6).</summary>
    private static double ComputeOverallConfidence(LeadExtractionResult header, List<LeadItemData> items)
    {
        var headerConf = new[]
        {
            header.RfqnoConfidence, header.BuyersNameConfidence, header.RecDateConfidence,
            header.BidClosingDateConfidence
        }.Where(c => c.HasValue).Select(c => c!.Value).DefaultIfEmpty(0).Average();
        var itemConf = items.Select(i => i.ItemConfidence ?? 0).DefaultIfEmpty(0).Average();
        return items.Count > 0 ? (headerConf * 0.4) + (itemConf * 0.6) : headerConf;
    }
}
