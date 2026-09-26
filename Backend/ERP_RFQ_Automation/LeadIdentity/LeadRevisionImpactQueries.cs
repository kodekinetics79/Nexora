using System.Globalization;
using System.Text.Json;
using ERP_RFQ_Automation.DTOs.QuoteDTOs;
using ERP_RFQ_Automation.Models;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.LeadIdentity;

/// <summary>
/// The one definition of "this quote still has an unresolved customer revision".
///
/// <para><see cref="LeadRevisionImpact"/> rows are append-only (trigger
/// <c>trg_lead_revision_impacts_append_only</c>), so a resolution can never flip
/// <see cref="LeadRevisionImpact.Status"/>: it is recorded as a <c>REVISION_IMPACT_RESOLVED</c>
/// audit event whose correlation id names the impact. Until this existed the quote DETAIL joined
/// on that event (and hid the banner) while send-readiness and the send itself read only
/// <c>Status == "OPEN"</c> — so after "Resolve" the screen said resolved and the send said stale,
/// for ever. Every reader goes through here now.</para>
/// </summary>
public static class LeadRevisionImpactQueries
{
    public const string ResolvedEventType = "REVISION_IMPACT_RESOLVED";

    public static string CorrelationIdFor(long impactId) => "quote-impact:" + impactId;

    /// <summary>Impacts on a quote that are open AND have not been resolved by an audit event.</summary>
    public static IQueryable<LeadRevisionImpact> OpenQuoteImpacts(
        ErpRfqAutomationContext context, long businessUnitId, long quoteId)
        => context.Set<LeadRevisionImpact>()
            .Where(impact => impact.BusinessUnitId == businessUnitId
                && impact.AggregateType == "QUOTE"
                && impact.AggregateId == quoteId
                && impact.Status == "OPEN"
                && impact.ResolvedAtUtc == null)
            .Where(impact => !context.Set<LeadIdentityAuditEvent>()
                .Any(audit => audit.BusinessUnitId == businessUnitId
                    && audit.EventType == ResolvedEventType
                    && audit.CorrelationId == "quote-impact:" + impact.Id));

    /// <summary>
    /// The newest open impact on a quote, said in the rep's terms: which revision arrived, which
    /// one the quote was built from, and what changed on each line.
    ///
    /// <para>Reads <see cref="LeadRevisionDifference"/> rows of the arriving revision — the diff the
    /// identity spine already computed — rather than re-diffing snapshots here, so the screen and
    /// the audit trail describe the same change. Returns null when nothing is open.</para>
    /// </summary>
    public static async Task<QuoteRevisionImpactDTO?> DescribeOpenQuoteImpactAsync(
        ErpRfqAutomationContext context, long businessUnitId, long quoteId, CancellationToken ct = default)
    {
        var impact = await OpenQuoteImpacts(context, businessUnitId, quoteId).AsNoTracking()
            .OrderByDescending(x => x.Id)
            .Select(x => new { x.Id, x.ImpactType, x.LeadRevisionId, x.DetailsJson })
            .FirstOrDefaultAsync(ct);
        if (impact is null) return null;

        var arrived = await context.Set<LeadRevision>().AsNoTracking()
            .Where(r => r.BusinessUnitId == businessUnitId && r.Id == impact.LeadRevisionId)
            .Select(r => (int?)r.RevisionNumber)
            .SingleOrDefaultAsync(ct);
        var (from, to) = RevisionSpan(impact.DetailsJson, arrived);

        var differences = await context.Set<LeadRevisionDifference>().AsNoTracking()
            .Where(d => d.BusinessUnitId == businessUnitId
                && d.LeadRevisionId == impact.LeadRevisionId
                && d.Scope == "Line"
                && d.ChangeType != LeadRevisionChangeType.Unchanged)
            .OrderBy(d => d.Id)
            .ToListAsync(ct);

        return new QuoteRevisionImpactDTO
        {
            ImpactId = impact.Id,
            ImpactType = impact.ImpactType,
            FromRevision = from,
            ToRevision = to,
            Changes = SummariseLineChanges(differences)
        };
    }

    /// <summary>
    /// The revision span an impact describes. <c>DetailsJson</c> is written as
    /// <c>{fromRevision, toRevision}</c> by <c>LeadIdentityApplicationService.Impact</c>; the
    /// arriving revision's own number is the fallback for rows written before that shape.
    /// </summary>
    internal static (int From, int To) RevisionSpan(string? detailsJson, int? arrivedRevisionNumber)
    {
        int? from = null, to = null;
        if (!string.IsNullOrWhiteSpace(detailsJson))
        {
            try
            {
                using var document = JsonDocument.Parse(detailsJson);
                if (document.RootElement.ValueKind == JsonValueKind.Object)
                {
                    if (document.RootElement.TryGetProperty("fromRevision", out var f) && f.ValueKind == JsonValueKind.Number) from = f.GetInt32();
                    if (document.RootElement.TryGetProperty("toRevision", out var t) && t.ValueKind == JsonValueKind.Number) to = t.GetInt32();
                }
            }
            catch (JsonException) { /* an unreadable detail is not a reason to hide the impact */ }
        }
        var resolvedTo = to ?? arrivedRevisionNumber ?? 0;
        var resolvedFrom = from ?? (resolvedTo > 1 ? resolvedTo - 1 : 0);
        return (resolvedFrom, resolvedTo);
    }

    /// <summary>
    /// One entry per changed fact per line. Quantity, unit, part and description are the four
    /// things a rep re-quotes on; anything else that moved on a line is reported as the line
    /// having changed, so a change is never silently absent from the list.
    /// </summary>
    internal static List<QuoteRevisionLineChangeDTO> SummariseLineChanges(IEnumerable<LeadRevisionDifference> differences)
    {
        var changes = new List<QuoteRevisionLineChangeDTO>();
        foreach (var difference in differences)
        {
            using var previous = ParseOrNull(difference.PreviousValueJson);
            using var current = ParseOrNull(difference.CurrentValueJson);
            var line = LineLabel(current?.RootElement, previous?.RootElement, difference.Path);

            switch (difference.ChangeType)
            {
                case LeadRevisionChangeType.Added:
                    changes.Add(new QuoteRevisionLineChangeDTO { Line = line, Field = "added", To = Quantity(current?.RootElement) });
                    continue;
                case LeadRevisionChangeType.Removed:
                    changes.Add(new QuoteRevisionLineChangeDTO { Line = line, Field = "removed", From = Quantity(previous?.RootElement) });
                    continue;
                case LeadRevisionChangeType.Unchanged:
                    continue;
            }

            var before = changes.Count;
            Compare("quantity", Quantity(previous?.RootElement), Quantity(current?.RootElement));
            Compare("unit", Text(previous?.RootElement, "unitOfMeasure", "uom"), Text(current?.RootElement, "unitOfMeasure", "uom"));
            Compare("part", Text(previous?.RootElement, "manufacturerPartNumber", "itemMaterialCode", "part"),
                Text(current?.RootElement, "manufacturerPartNumber", "itemMaterialCode", "part"));
            Compare("description", Text(previous?.RootElement, "productShortDescription", "itemText", "description"),
                Text(current?.RootElement, "productShortDescription", "itemText", "description"));
            if (changes.Count == before)
                changes.Add(new QuoteRevisionLineChangeDTO { Line = line, Field = "changed" });

            void Compare(string field, string? from, string? to)
            {
                if (string.Equals(from, to, StringComparison.Ordinal)) return;
                changes.Add(new QuoteRevisionLineChangeDTO { Line = line, Field = field, From = from, To = to });
            }
        }
        return changes;
    }

    /// <summary>The exact quantity a line snapshot carries, printed the way the document had it.</summary>
    internal static string? Quantity(JsonElement? element)
        => QuantityValue(element)?.ToString("0.####", CultureInfo.InvariantCulture);

    /// <summary>The exact quantity a line snapshot carries, or null when it has none.</summary>
    internal static decimal? QuantityValue(JsonElement? element)
    {
        if (element is not { ValueKind: JsonValueKind.Object } line) return null;
        foreach (var name in new[] { "quantity", "Quantity" })
            if (line.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number)
                return value.GetDecimal();
        return null;
    }

    /// <summary>The buyer's own line reference from a snapshot: the exact value first, the
    /// normalized identity field second, the diff path's key last.</summary>
    internal static string LineLabel(JsonElement? current, JsonElement? previous, string path)
        => Text(current, "lineItemNo", "line") ?? Text(previous, "lineItemNo", "line") ?? PathKey(path);

    private static string PathKey(string path)
    {
        var open = path.IndexOf('[');
        var close = path.LastIndexOf(']');
        if (open < 0 || close <= open) return path;
        var raw = path.Substring(open + 1, close - open - 1);
        try
        {
            var key = JsonSerializer.Deserialize<string>(raw) ?? raw;
            return key.StartsWith("ordinal:", StringComparison.Ordinal) ? key.Substring("ordinal:".Length) : key;
        }
        catch (JsonException) { return raw; }
    }

    internal static string? Text(JsonElement? element, params string[] names)
    {
        if (element is not { ValueKind: JsonValueKind.Object } line) return null;
        foreach (var name in names)
            if (line.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
                && value.GetString() is { Length: > 0 } text)
                return text;
        return null;
    }

    private static JsonDocument? ParseOrNull(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return JsonDocument.Parse(json); }
        catch (JsonException) { return null; }
    }
}
