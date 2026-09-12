using ERP_RFQ_Automation.DocumentIntelligence.Persistence;
using ERP_RFQ_Automation.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace ERP_RFQ_Automation.ProductIntelligence.ManufacturerKnowledge;

/// <summary>
/// Everything one tenant has taught the platform about makers, loaded once per document.
/// </summary>
/// <param name="Patterns">The tenant's learned (pattern, maker) rows.</param>
/// <param name="KnownManufacturers">
/// The distinct display names those rows carry — the only names the inference will recognise
/// inside a description.
/// </param>
public sealed record ManufacturerKnowledgeSnapshot(
    long BusinessUnitId,
    IReadOnlyList<ManufacturerPartPattern> Patterns,
    IReadOnlySet<string> KnownManufacturers)
{
    public bool IsEmpty => Patterns.Count == 0;

    public static ManufacturerKnowledgeSnapshot Empty(long businessUnitId) =>
        new(businessUnitId, [], new HashSet<string>(StringComparer.Ordinal));
}

/// <summary>
/// Read side. Scoped, EF-backed, and deliberately uncached: the extraction worker loads a
/// tenant's knowledge once per document it processes, and a request-scoped context is the
/// right lifetime for that. A process-wide cache would serve a maker learned in one review to
/// the next document only after some expiry nobody can explain to a reviewer.
/// </summary>
public interface IManufacturerKnowledge
{
    Task<ManufacturerKnowledgeSnapshot> ForBusinessUnitAsync(long businessUnitId, CancellationToken ct = default);
}

/// <summary>
/// Write side. Turns a reviewed lead's stated (manufacturer, part number) pairs into durable
/// patterns. Separate from <see cref="IManufacturerKnowledge"/> because the two run in different
/// places — the reader inside the extraction pipeline, the learner inside the lead review — and
/// the review path must not be able to reach the reader's snapshot, nor the pipeline the
/// learner's writes.
/// </summary>
public interface IManufacturerPatternLearner
{
    /// <summary>
    /// Stages the patterns this lead's lines teach on the caller's DbContext. Never calls
    /// SaveChanges — the caller owns the transaction, so the learned rows commit with the review
    /// that taught them or not at all (the same contract as <c>ICustomerAliasLearner</c>).
    /// </summary>
    Task<ManufacturerPatternLearningResult> LearnFromReviewAsync(
        long businessUnitId, Lead lead, long? reviewAuditId, CancellationToken ct = default);
}

public sealed record ManufacturerPatternLearningResult(int Learned, int Reinforced, IReadOnlyList<string> SkipReasons)
{
    public static readonly ManufacturerPatternLearningResult None = new(0, 0, []);
    public string? SkippedReason => SkipReasons.Count == 0 ? null : string.Join(",", SkipReasons.Distinct());
}

public sealed class EfManufacturerKnowledge : IManufacturerKnowledge
{
    private readonly ErpRfqAutomationContext _db;

    public EfManufacturerKnowledge(ErpRfqAutomationContext db) => _db = db;

    public async Task<ManufacturerKnowledgeSnapshot> ForBusinessUnitAsync(long businessUnitId, CancellationToken ct = default)
    {
        if (businessUnitId <= 0) return ManufacturerKnowledgeSnapshot.Empty(businessUnitId);

        // The explicit tenant predicate is the one that matters here. The extraction worker
        // drains a shared queue with no tenant pushed, so the global query filter is a no-op on
        // that path, and it executes as nexora_pipeline_app, which BYPASSES row-level security.
        // Nothing but this WHERE keeps one tenant's numbering knowledge out of another's document.
        var patterns = await _db.Set<ManufacturerPartPattern>()
            .AsNoTracking()
            .Where(p => p.BusinessUnitId == businessUnitId)
            .OrderBy(p => p.Id)
            .ToListAsync(ct);

        if (patterns.Count == 0) return ManufacturerKnowledgeSnapshot.Empty(businessUnitId);

        // A name is "known" for description matching only once it has been confirmed as often
        // as a part-number family must be. One approved line naming "Universal" would otherwise
        // turn every "UNIVERSAL JOINT" into a Universal product.
        var known = patterns
            .GroupBy(p => p.Manufacturer.Trim(), StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Key.Length > 0 && group.Sum(p => p.ObservationCount) >= ManufacturerInference.MinimumObservations)
            .Select(group => group.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return new ManufacturerKnowledgeSnapshot(businessUnitId, patterns, known);
    }
}

/// <summary>
/// The learning loop for makers: a reviewer confirming a line that names both its manufacturer
/// and its part number teaches Nexora to name the maker itself on the NEXT document that gives
/// only the number.
///
/// <para><b>The poisoning guard.</b> A manufacturer the platform itself inferred must never be
/// learned back as if a person had stated it — that is the path by which one guess becomes two
/// observations, clears the <see cref="ManufacturerInference.MinimumObservations"/> threshold, and
/// bootstraps itself into an authoritative pattern. Whether a line's maker was inferred is read
/// from the evidence ledger: the extraction wrote a <c>ManufacturerName</c>
/// <see cref="FieldEvidence"/> row whose transformations carry
/// <see cref="ManufacturerInference.ReasonPrefix"/>. If that row's value still equals what the
/// line says now, the machine's answer is standing unchanged and is skipped; if the reviewer
/// overwrote it, the value on the line is a human statement and is learned. A line with no
/// evidence row at all (a lead entered by hand, an older extraction, the SQLite unit lane where
/// the ledger is not mapped) is treated as stated, because the only way it got a manufacturer is
/// that somebody typed one.</para>
///
/// <para>Rows are keyed per (tenant, pattern, maker) and reinforced rather than duplicated; a
/// second line on the same lead teaching the same pairing counts once per line, which is what
/// "observation" means.</para>
/// </summary>
public sealed class ManufacturerPatternLearner : IManufacturerPatternLearner
{
    public const string SkipNoEvidence = "noEvidence";
    public const string SkipInferredNotStated = "inferredNotStated";
    public const string SkipUnusablePartNumber = "unusablePartNumber";
    public const string SkipManufacturerUnusable = "manufacturerUnusable";

    private const int MaxManufacturerLength = 200;

    private readonly ErpRfqAutomationContext _db;
    private readonly ILogger<ManufacturerPatternLearner>? _log;

    public ManufacturerPatternLearner(ErpRfqAutomationContext db, ILogger<ManufacturerPatternLearner>? log = null)
    {
        _db = db;
        _log = log;
    }

    public async Task<ManufacturerPatternLearningResult> LearnFromReviewAsync(
        long businessUnitId, Lead lead, long? reviewAuditId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(lead);
        if (businessUnitId <= 0) throw new ArgumentOutOfRangeException(nameof(businessUnitId));

        var candidatesLines = lead.LeadItems
            .Where(item => !string.IsNullOrWhiteSpace(item.ManufacturerName)
                           && !string.IsNullOrWhiteSpace(item.ManufacturerPartNumber))
            .ToList();
        if (candidatesLines.Count == 0)
            return new ManufacturerPatternLearningResult(0, 0, [SkipNoEvidence]);

        var inferredByLeadItem = await LoadStandingInferencesAsync(
            businessUnitId, candidatesLines.Select(item => item.Id).Where(id => id > 0).ToList(), ct);

        var skips = new List<string>();
        var proposals = new List<(string Pattern, string Manufacturer, string Normalized)>();
        foreach (var line in candidatesLines)
        {
            if (inferredByLeadItem.TryGetValue(line.Id, out var machineValue)
                && string.Equals(machineValue?.Trim(), line.ManufacturerName!.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                skips.Add(SkipInferredNotStated);
                continue;
            }

            var display = line.ManufacturerName!.Trim();
            var normalized = ProductIdentityNormalizer.NormalizeManufacturer(display);
            if (normalized is null || display.Length > MaxManufacturerLength || normalized.Length > MaxManufacturerLength)
            {
                skips.Add(SkipManufacturerUnusable);
                continue;
            }

            var patterns = ManufacturerInference.ExtractStatedPairs(display, line.ManufacturerPartNumber);
            if (patterns.Count == 0)
            {
                skips.Add(SkipUnusablePartNumber);
                continue;
            }

            foreach (var pattern in patterns)
                proposals.Add((pattern, display, normalized));
        }

        if (proposals.Count == 0)
            return new ManufacturerPatternLearningResult(0, 0, skips);

        var patternKeys = proposals.Select(p => p.Pattern).Distinct(StringComparer.Ordinal).ToList();
        var existing = await _db.Set<ManufacturerPartPattern>().IgnoreQueryFilters()
            .Where(p => p.BusinessUnitId == businessUnitId && patternKeys.Contains(p.Pattern))
            .ToListAsync(ct);
        var byKey = existing.ToDictionary(p => (p.Pattern, p.NormalizedManufacturer));

        var now = DateTimeOffset.UtcNow;
        var learned = 0;
        var reinforced = 0;
        foreach (var (pattern, display, normalized) in proposals)
        {
            if (byKey.TryGetValue((pattern, normalized), out var row))
            {
                row.ObservationCount += 1;
                row.LastObservedOn = now;
                reinforced++;
                continue;
            }

            row = new ManufacturerPartPattern
            {
                BusinessUnitId = businessUnitId,
                Pattern = pattern,
                Manufacturer = display,
                NormalizedManufacturer = normalized,
                ObservationCount = 1,
                LearnedFromLeadId = lead.Id,
                LearnedFromReviewAuditId = reviewAuditId,
                CreatedOn = now,
                LastObservedOn = now
            };
            _db.Set<ManufacturerPartPattern>().Add(row);
            byKey[(pattern, normalized)] = row;
            learned++;
        }

        _log?.LogInformation(
            "Manufacturer pattern learning for lead {LeadId}: {Learned} learned, {Reinforced} reinforced, {Skipped} line(s) skipped.",
            lead.Id, learned, reinforced, skips.Count);
        return new ManufacturerPatternLearningResult(learned, reinforced, skips);
    }

    /// <summary>
    /// For each lead item, the manufacturer value the extraction INFERRED for it, if the ledger
    /// says one was. Joins <c>field_evidence</c> through <c>canonical_line_items.lead_item_id</c>,
    /// the binding <c>StructuredEvidenceLedgerPersister.BindLeadItem</c> writes. Empty when the
    /// ledger is not part of the model (it is PostgreSQL-only), which is the "no evidence row
    /// exists" case and is treated as stated.
    /// </summary>
    private async Task<Dictionary<long, string?>> LoadStandingInferencesAsync(
        long businessUnitId, List<long> leadItemIds, CancellationToken ct)
    {
        var result = new Dictionary<long, string?>();
        if (leadItemIds.Count == 0) return result;
        if (_db.Model.FindEntityType(typeof(FieldEvidence)) is null) return result;

        // The transformations column is jsonb on PostgreSQL, and a LIKE against jsonb is an
        // operator that does not exist (42883) — the reason is tested in memory instead. A lead
        // item has at most a handful of evidence rows, so the extra rows fetched are few.
        var rows = await _db.Set<FieldEvidence>().IgnoreQueryFilters().AsNoTracking()
            .Where(fe => fe.BusinessUnitId == businessUnitId
                         && fe.LineItemId != null
                         && fe.FieldName == "ManufacturerName"
                         && fe.LineItem!.LeadItemId != null
                         && leadItemIds.Contains(fe.LineItem.LeadItemId!.Value))
            .Select(fe => new { LeadItemId = fe.LineItem!.LeadItemId!.Value, fe.NormalizedValue, fe.RawValue, fe.CreatedOn, fe.TransformationsJson })
            .ToListAsync(ct);

        // Newest run first, ordered in memory: the portable lane's provider cannot ORDER BY a
        // DateTimeOffset, and a lead item has at most a handful of runs.
        foreach (var row in rows
                     .Where(r => r.TransformationsJson is not null
                                 && r.TransformationsJson.Contains(ManufacturerInference.ReasonPrefix, StringComparison.Ordinal))
                     .OrderByDescending(r => r.CreatedOn))
            result.TryAdd(row.LeadItemId, row.NormalizedValue ?? row.RawValue);
        return result;
    }
}
