using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ERP_RFQ_Automation.Models;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.Services.Measurement;

/// <summary>
/// Measured extraction accuracy, or an explicit refusal to state one.
///
/// THE RULE THIS SERVICE ENFORCES: no accuracy percentage is returned until the cell
/// (tenant × field × extraction path) holds at least <see cref="MinimumDocuments"/>
/// approved documents. Below that the response carries a status, the counts, and how
/// many more documents are needed — never a number. Callers cannot accidentally render
/// a figure that does not exist, because there is no figure in the payload to render.
///
/// WHY A LOWER BOUND AND NOT THE POINT ESTIMATE. A point estimate off a small sample is
/// an overclaim: 27 for 27 is a 100% point estimate whose 95% Wilson lower bound is
/// 87.5%. What we can defend to a customer is the lower bound — "at least X%" — so that
/// is what <see cref="ExtractionFieldAccuracy.LowerBoundPercent"/> carries, with the
/// point estimate reported alongside it and clearly named as an observation.
///
/// WHY DOCUMENTS AND NOT LINES. Line items are not independent trials: 2,966 lines came
/// from 27 documents and two of those documents carry 98% of the lines. A line-weighted
/// interval would be a confident statement about two spreadsheets. Every denominator
/// here is documents.
/// </summary>
public sealed class AccuracyMeasurementService
{
    /// <summary>
    /// Documents required in a cell before any figure is published. Thirty keeps the
    /// 95% Wilson lower bound on a perfect record at 88.6% — the smallest sample where
    /// the published number is not dominated by the width of the interval.
    /// </summary>
    public const int MinimumDocuments = 30;

    /// <summary>Two-sided 95% normal quantile.</summary>
    private const double Z95 = 1.959963984540054;

    public const string StatusMeasured = "measured";
    public const string StatusInsufficientData = "insufficient-data";

    private readonly ErpRfqAutomationContext _db;

    public AccuracyMeasurementService(ErpRfqAutomationContext db) => _db = db;

    /// <summary>
    /// Wilson score interval lower bound for k successes in n trials at 95%.
    /// Returns null for n &lt;= 0 — an interval over no trials is not a number.
    /// </summary>
    public static double? WilsonLowerBound(int successes, int trials, double z = Z95)
    {
        if (trials <= 0) return null;
        if (successes < 0 || successes > trials)
            throw new ArgumentOutOfRangeException(nameof(successes),
                "Successes must lie between zero and the number of trials.");

        var n = (double)trials;
        var phat = successes / n;
        var z2 = z * z;
        var centre = phat + z2 / (2 * n);
        var margin = z * Math.Sqrt((phat * (1 - phat) + z2 / (4 * n)) / n);
        var lower = (centre - margin) / (1 + z2 / n);
        return Math.Clamp(lower, 0d, 1d);
    }

    /// <summary>
    /// Per-field accuracy for a tenant. Every cell is returned — including the ones that
    /// cannot be published — because "we have 4 of the 30 documents we need for
    /// unitOfMeasure" is itself the honest answer to "how accurate is it?".
    /// </summary>
    public async Task<ExtractionAccuracyReport> GetFieldAccuracyAsync(
        long businessUnitId, CancellationToken cancellationToken = default)
    {
        var cells = await _db.Set<ExtractionCorpusEntry>().AsNoTracking()
            .Where(e => e.BusinessUnitId == businessUnitId)
            .GroupBy(e => new { e.ExtractionPath, e.Scope, e.FieldName })
            .Select(g => new
            {
                g.Key.ExtractionPath,
                g.Key.Scope,
                g.Key.FieldName,
                Documents = g.Count(),
                DocumentsCorrect = g.Count(e => e.FieldCorrect),
                Observed = g.Sum(e => (long)e.ObservedCount),
                Corrected = g.Sum(e => (long)e.CorrectedCount)
            })
            .ToListAsync(cancellationToken);

        var fields = cells
            .OrderBy(c => c.ExtractionPath, StringComparer.Ordinal)
            .ThenBy(c => c.Scope, StringComparer.Ordinal)
            .ThenBy(c => c.FieldName, StringComparer.Ordinal)
            .Select(c =>
            {
                var measurable = c.Documents >= MinimumDocuments;
                var lower = measurable ? WilsonLowerBound(c.DocumentsCorrect, c.Documents) : null;
                return new ExtractionFieldAccuracy(
                    c.ExtractionPath,
                    c.Scope,
                    c.FieldName,
                    c.Documents,
                    c.DocumentsCorrect,
                    c.Observed,
                    c.Corrected,
                    measurable ? StatusMeasured : StatusInsufficientData,
                    measurable ? null : $"{MinimumDocuments - c.Documents} more approved document(s) required before a figure can be published.",
                    lower is null ? null : decimal.Round((decimal)(lower.Value * 100), 1),
                    measurable
                        ? decimal.Round(c.DocumentsCorrect * 100m / c.Documents, 1)
                        : (decimal?)null);
            })
            .ToList();

        var documents = await _db.Set<ExtractionCorpusEntry>().AsNoTracking()
            .Where(e => e.BusinessUnitId == businessUnitId)
            .Select(e => e.LeadReviewAuditId)
            .Distinct()
            .CountAsync(cancellationToken);

        return new ExtractionAccuracyReport(
            documents,
            MinimumDocuments,
            fields,
            fields.Count > 0 && fields.All(f => f.Status == StatusMeasured),
            "Accuracy is measured from reviewers' own corrections on approved documents. "
            + $"A figure is published per field only once {MinimumDocuments} approved documents exist "
            + "for that field and extraction path; until then this endpoint reports counts and returns "
            + "no percentage. Denominators are documents, never line items.");
    }

    /// <summary>
    /// Per-field CORRECTION RATE read straight from <see cref="LeadReviewAudit"/>.
    ///
    /// This is not accuracy and is never labelled as such: it is a count of how often
    /// reviewers changed what the machine produced, with its denominators attached. It is
    /// reportable at any sample size because it describes what happened rather than
    /// estimating what will happen — but it carries <see cref="ExtractionCorrectionRate.Documents"/>
    /// so a reader can see how thin the evidence is.
    ///
    /// It reads audits directly rather than the corpus table so that reviews recorded
    /// before the corpus writer existed still count. Both go through the same projection.
    /// </summary>
    public async Task<CorrectionSignalReport> GetCorrectionSignalAsync(
        long businessUnitId, int maxAudits = 2000, CancellationToken cancellationToken = default)
    {
        var audits = await _db.Set<LeadReviewAudit>().AsNoTracking()
            .Where(a => a.BusinessUnitId == businessUnitId)
            .OrderByDescending(a => a.ReviewedOn)
            .Take(Math.Clamp(maxAudits, 1, 20_000))
            .Select(a => new { a.Id, a.LeadId, a.Action, a.BeforeJson, a.AfterJson, a.ReviewedOn })
            .ToListAsync(cancellationToken);

        var approvals = audits
            .Where(a => string.Equals(a.Action, "approve", StringComparison.OrdinalIgnoreCase))
            .ToList();

        var tally = new Dictionary<(string Scope, string Field), (int Documents, int DocumentsCorrected, long Observed, long Corrected)>();
        foreach (var audit in approvals)
        {
            foreach (var observation in ExtractionCorpusProjection.Diff(audit.BeforeJson, audit.AfterJson))
            {
                var key = (observation.Scope, observation.FieldName);
                tally.TryGetValue(key, out var current);
                tally[key] = (
                    current.Documents + 1,
                    current.DocumentsCorrected + (observation.Correct ? 0 : 1),
                    current.Observed + observation.Observed,
                    current.Corrected + observation.Corrected);
            }
        }

        var rates = tally
            .OrderBy(kv => kv.Key.Scope, StringComparer.Ordinal)
            .ThenBy(kv => kv.Key.Field, StringComparer.Ordinal)
            .Select(kv => new ExtractionCorrectionRate(
                kv.Key.Scope,
                kv.Key.Field,
                kv.Value.Documents,
                kv.Value.DocumentsCorrected,
                kv.Value.Observed,
                kv.Value.Corrected,
                kv.Value.Observed == 0
                    ? null
                    : decimal.Round(kv.Value.Corrected * 100m / kv.Value.Observed, 1)))
            .ToList();

        return new CorrectionSignalReport(
            audits.Count,
            approvals.Count,
            audits.Count - approvals.Count,
            approvals.Select(a => a.LeadId).Distinct().Count(),
            rates,
            "Correction rate is the share of machine-produced values a reviewer changed, "
            + "counted only where a value existed to judge. It is a record of reviewer "
            + "activity, not an accuracy measurement, and no confidence interval is implied.");
    }
}

/// <param name="LowerBoundPercent">95% Wilson lower bound — the defensible claim. Null until measurable.</param>
/// <param name="ObservedPercent">Point estimate. Null until measurable, so it cannot be shown alone.</param>
public sealed record ExtractionFieldAccuracy(
    string ExtractionPath,
    string Scope,
    string FieldName,
    int Documents,
    int DocumentsCorrect,
    long ObservedValues,
    long CorrectedValues,
    string Status,
    string? StatusDetail,
    decimal? LowerBoundPercent,
    decimal? ObservedPercent);

public sealed record ExtractionAccuracyReport(
    int ApprovedDocuments,
    int MinimumDocumentsPerField,
    IReadOnlyList<ExtractionFieldAccuracy> Fields,
    bool Publishable,
    string Method);

public sealed record ExtractionCorrectionRate(
    string Scope,
    string FieldName,
    int Documents,
    int DocumentsWithACorrection,
    long ObservedValues,
    long CorrectedValues,
    decimal? CorrectionRatePercent);

public sealed record CorrectionSignalReport(
    int ReviewsExamined,
    int Approvals,
    int SavesWithoutApproval,
    int LeadsApproved,
    IReadOnlyList<ExtractionCorrectionRate> Fields,
    string Method);
