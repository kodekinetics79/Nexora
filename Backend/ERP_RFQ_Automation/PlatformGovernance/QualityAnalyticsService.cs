using ERP_RFQ_Automation.AI;
using ERP_RFQ_Automation.CommercialDocuments;
using ERP_RFQ_Automation.DocumentIntelligence.Persistence;
using ERP_RFQ_Automation.Extraction;
using ERP_RFQ_Automation.LeadIdentity;
using ERP_RFQ_Automation.Models;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;

namespace ERP_RFQ_Automation.PlatformGovernance;

public sealed record QualityMetric(string Key, string Label, decimal? Value, string Unit,
    long Numerator, long Denominator, string Definition, string EvidenceStatus, string DrilldownKey);
public sealed record QualityCause(string Category, string Code, long Count);
public sealed record QualityDrilldownItem(long OccurrenceId, string FileName, DateTimeOffset IngestedOn,
    string IntakeStatus, string ProcessingStatus, string ProcessingPath, bool HumanReview,
    bool LocalProcessing, bool ExternalProcessing, bool ProcessingReused, decimal ActualCost,
    string CostStatus);
public sealed record QualityRecommendation(string Priority, string Title, string Recommendation,
    string Evidence, string DrilldownKey);
public sealed record QualityAnalyticsView(DateTimeOffset From, DateTimeOffset To,
    IReadOnlyList<QualityMetric> Metrics, IReadOnlyList<QualityCause> ExceptionCauses,
    IReadOnlyList<QualityDrilldownItem> Records, IReadOnlyList<QualityRecommendation> Recommendations,
    string DefinitionVersion, string AccuracyLimitation);

public sealed class QualityAnalyticsService(ErpRfqAutomationContext db)
{
    public async Task<QualityAnalyticsView> GetAsync(long tenantId, int windowDays,
        string? drilldown, CancellationToken ct)
    {
        PlatformGovernanceService.EnsureTenant(tenantId);
        var thresholds = await ThresholdsAsync(tenantId, ct);
        windowDays = Math.Clamp(windowDays, 1, 365);
        var toDate = DateTimeOffset.UtcNow;
        var fromDate = toDate.AddDays(-windowDays);

        var occurrences = await (from occurrence in db.Set<SourceDocumentOccurrence>().AsNoTracking()
            join document in db.Set<SourceDocument>().AsNoTracking()
                on new { occurrence.BusinessUnitId, Id = occurrence.SourceDocumentId }
                equals new { document.BusinessUnitId, document.Id }
            where occurrence.BusinessUnitId == tenantId && occurrence.ReceivedOn >= fromDate
                && occurrence.ReceivedOn <= toDate
            select new { Occurrence = occurrence, Document = document }).ToListAsync(ct);
        var leadPaths = await db.Set<LeadIngestionOccurrence>().AsNoTracking()
            .Where(x => x.BusinessUnitId == tenantId && x.IngestedAtUtc >= fromDate && x.IngestedAtUtc <= toDate)
            .Select(x => new { x.SourceDocumentOccurrenceId, x.ProcessingPath, x.ExternalAiUsed })
            .ToListAsync(ct);
        var leadPathByOccurrence = leadPaths.Where(x => x.SourceDocumentOccurrenceId.HasValue)
            .GroupBy(x => x.SourceDocumentOccurrenceId!.Value).ToDictionary(x => x.Key, x => x.First());
        var runs = await db.Set<ExtractionRun>().AsNoTracking().Where(x => x.BusinessUnitId == tenantId
            && x.CreatedOn >= fromDate && x.CreatedOn <= toDate).ToListAsync(ct);
        var fields = await db.Set<FieldEvidence>().AsNoTracking().Where(x => x.BusinessUnitId == tenantId
            && x.CreatedOn >= fromDate && x.CreatedOn <= toDate).ToListAsync(ct);
        var classifications = await db.CommercialDocumentClassifications.AsNoTracking()
            .Where(x => x.BusinessUnitId == tenantId && x.CreatedOn >= fromDate && x.CreatedOn <= toDate)
            .ToListAsync(ct);
        var ai = await db.AiRequests.AsNoTracking().Where(x => x.BusinessUnitId == tenantId
            && x.CreatedOn >= fromDate.UtcDateTime && x.CreatedOn <= toDate.UtcDateTime).ToListAsync(ct);
        var actions = await db.HumanActionItems.AsNoTracking().Where(x => x.BusinessUnitId == tenantId
            && x.CreatedOn >= fromDate.UtcDateTime && x.CreatedOn <= toDate.UtcDateTime).ToListAsync(ct);

        var terminal = occurrences.Count(x => x.Occurrence.IntakeStatus is IntakeOccurrenceStatus.Resolved
            or IntakeOccurrenceStatus.ReviewRequired or IntakeOccurrenceStatus.Rejected
            or IntakeOccurrenceStatus.DeadLetter);
        var resolved = occurrences.Count(x => x.Occurrence.IntakeStatus == IntakeOccurrenceStatus.Resolved);
        var review = occurrences.Count(x => x.Occurrence.IntakeStatus == IntakeOccurrenceStatus.ReviewRequired);
        var completedRuns = runs.Count(x => x.Status == ExtractionRunStatus.Completed);
        var terminalRuns = runs.Count(x => x.Status is ExtractionRunStatus.Completed or ExtractionRunStatus.Failed);
        var validatedFields = fields.Count(x => x.ValidationStatus != FieldValidationStatus.Unvalidated);
        var validFields = fields.Count(x => x.ValidationStatus == FieldValidationStatus.Valid);
        var classified = classifications.Count;
        var acceptedClassifications = classifications.Count(x => x.ReviewStatus is
            CommercialDocumentReviewStatus.AutoClassified or CommercialDocumentReviewStatus.Confirmed);
        var leadDecisions = leadPaths.Count;
        var touchless = leadPaths.Count(x => x.ProcessingPath != LeadProcessingPath.HumanReview);
        var localAi = ai.Count(x => x.ProviderClass == AiProviderClass.Local);
        var externalAi = ai.Count(x => x.ProviderClass == AiProviderClass.External);
        var governedAi = localAi + externalAi;
        var localAiOccurrences = ai.Where(x => x.ProviderClass == AiProviderClass.Local
                && x.SourceDocumentOccurrenceId.HasValue)
            .Select(x => x.SourceDocumentOccurrenceId!.Value).ToHashSet();
        var externalAiOccurrences = ai.Where(x => x.ProviderClass == AiProviderClass.External
                && x.SourceDocumentOccurrenceId.HasValue)
            .Select(x => x.SourceDocumentOccurrenceId!.Value).ToHashSet();
        var reused = occurrences.Count(x => x.Occurrence.ProcessingReused || x.Occurrence.ParserReused
            || x.Occurrence.OcrReused || x.Occurrence.LocalModelReused);
        var durationMinutes = runs.Where(x => x.StartedOn.HasValue && x.CompletedOn.HasValue)
            .Select(x => (decimal)(x.CompletedOn!.Value - x.StartedOn!.Value).TotalMinutes)
            .Where(x => x >= 0).OrderBy(x => x).ToArray();

        var metrics = new List<QualityMetric>
        {
            Rate("straight-through", "Straight-through processing", resolved, terminal,
                "Resolved intake occurrences / terminal intake occurrences.", "terminal-intake", thresholds.MinimumSampleSize),
            Rate("human-review", "Human-review rate", review, terminal,
                "Review-required intake occurrences / terminal intake occurrences.", "human-review", thresholds.MinimumSampleSize),
            Rate("extraction-success", "Extraction completion", completedRuns, terminalRuns,
                "Completed extraction runs / completed or failed extraction runs.", "extraction-runs", thresholds.MinimumSampleSize),
            Rate("field-validation", "Field validation pass rate", validFields, validatedFields,
                "Fields marked Valid / fields with an explicit validation outcome. This is not labeled-ground-truth accuracy.", "field-validation", thresholds.MinimumSampleSize),
            Rate("document-type-acceptance", "Document-type acceptance", acceptedClassifications, classified,
                "Auto-classified or human-confirmed documents / classified documents.", "classification", thresholds.MinimumSampleSize),
            Rate("touchless", "Touchless lead decisions", touchless, leadDecisions,
                "Lead identity decisions completed without the HumanReview processing path / all lead identity decisions.", "touchless", thresholds.MinimumSampleSize),
            Rate("local-processing", "Local AI processing", localAi, governedAi,
                "Local governed AI requests / local plus external governed AI requests.", "local-ai", thresholds.MinimumSampleSize),
            Rate("external-dependency", "External AI dependency", externalAi, governedAi,
                "External governed AI requests / local plus external governed AI requests.", "external-ai", thresholds.MinimumSampleSize),
            Rate("correction-reuse", "Processing reuse", reused, occurrences.Count,
                "Occurrences reusing parser, OCR, local-model or prior processing / all intake occurrences.", "processing-reuse", thresholds.MinimumSampleSize),
            Duration("turnaround-p50", "Extraction turnaround p50", durationMinutes, .50m),
            Duration("turnaround-p95", "Extraction turnaround p95", durationMinutes, .95m),
            Rate("action-completion", "Human action completion", actions.Count(x => x.Status == HumanActionStatus.Completed),
                actions.Count, "Completed governed human actions / governed human actions created in the period.", "actions", thresholds.MinimumSampleSize)
        };

        var causes = occurrences.Where(x => !string.IsNullOrWhiteSpace(x.Occurrence.LastErrorCode))
            .GroupBy(x => x.Occurrence.LastErrorCode!).Select(x => new QualityCause("Intake", x.Key, x.LongCount()))
            .Concat(actions.GroupBy(x => x.ActionType).Select(x => new QualityCause("HumanAction", x.Key, x.LongCount())))
            .OrderByDescending(x => x.Count).ThenBy(x => x.Code).Take(12).ToList();
        var records = occurrences.Select(x =>
        {
            leadPathByOccurrence.TryGetValue(x.Occurrence.Id, out var path);
            return new QualityDrilldownItem(x.Occurrence.Id, x.Document.OriginalFileName,
                x.Occurrence.ReceivedOn, x.Occurrence.IntakeStatus.ToString(),
                x.Document.ProcessingStatus.ToString(), path?.ProcessingPath.ToString() ?? "NotLinked",
                path?.ProcessingPath == LeadProcessingPath.HumanReview,
                localAiOccurrences.Contains(x.Occurrence.Id),
                externalAiOccurrences.Contains(x.Occurrence.Id) || path?.ExternalAiUsed == true,
                x.Occurrence.ProcessingReused || x.Occurrence.ParserReused || x.Occurrence.OcrReused
                    || x.Occurrence.LocalModelReused,
                x.Occurrence.TotalActualCost, x.Occurrence.CostStatus);
        }).Where(x => MatchesDrilldown(x, drilldown)).OrderByDescending(x => x.IngestedOn).Take(100).ToList();
        var recommendations = Recommendations(metrics, causes, thresholds);
        return new(fromDate, toDate, metrics, causes, records, recommendations, thresholds.DefinitionVersion,
            "True extraction and document-type accuracy require an independently labeled evaluation corpus. Until that corpus exists, Nexora reports validation and acceptance rates separately and marks unsupported denominators as insufficient evidence.");
    }

    private static QualityMetric Rate(string key, string label, long numerator, long denominator,
        string definition, string drilldown, int minimumSampleSize) => new(key, label,
        denominator == 0 ? null : decimal.Round(numerator * 100m / denominator, 2), "%", numerator,
        denominator, definition, denominator == 0 ? "InsufficientEvidence"
            : denominator < minimumSampleSize ? "LimitedSample" : "Measured", drilldown);

    private static QualityMetric Duration(string key, string label, decimal[] values, decimal percentile)
    {
        if (values.Length == 0)
            return new(key, label, null, "minutes", 0, 0,
                $"{percentile:P0} percentile of completed extraction run duration.",
                "InsufficientEvidence", "extraction-runs");
        var index = (int)Math.Ceiling((double)(percentile * values.Length)) - 1;
        return new(key, label, decimal.Round(values[Math.Clamp(index, 0, values.Length - 1)], 2),
            "minutes", values.Length, values.Length,
            $"{percentile:P0} percentile of completed extraction run duration.", "Measured", "extraction-runs");
    }

    private static bool MatchesDrilldown(QualityDrilldownItem item, string? drilldown) => drilldown switch
    {
        "human-review" => item.HumanReview || item.IntakeStatus == IntakeOccurrenceStatus.ReviewRequired.ToString(),
        "external-ai" => item.ExternalProcessing,
        "local-ai" => item.LocalProcessing,
        "processing-reuse" => item.ProcessingReused,
        "terminal-intake" => item.IntakeStatus is "Resolved" or "ReviewRequired" or "Rejected" or "DeadLetter",
        _ => true
    };

    private static IReadOnlyList<QualityRecommendation> Recommendations(
        IReadOnlyList<QualityMetric> metrics, IReadOnlyList<QualityCause> causes,
        QualityThresholds thresholds)
    {
        var output = new List<QualityRecommendation>();
        var review = metrics.Single(x => x.Key == "human-review");
        if (review.Denominator >= thresholds.MinimumSampleSize
            && review.Value > thresholds.ReviewRateWarningPercent)
            output.Add(new("High", "Reduce repeated review demand",
                "Evaluate the leading exception against the current document skill and rule versions.",
                $"Human review is {review.Value}% ({review.Numerator}/{review.Denominator}); leading cause: {causes.FirstOrDefault()?.Code ?? "not classified"}.",
                "human-review"));
        var external = metrics.Single(x => x.Key == "external-dependency");
        if (external.Denominator >= thresholds.MinimumSampleSize
            && external.Value > thresholds.ExternalDependencyCeilingPercent)
            output.Add(new("Critical", "Restore local-first dependency ceiling",
                "Inspect external call evidence and move supported operations to approved local paths.",
                $"External dependency is {external.Value}% ({external.Numerator}/{external.Denominator}), above the {thresholds.ExternalDependencyCeilingPercent}% policy ceiling.",
                "external-ai"));
        if (output.Count == 0)
            output.Add(new("Monitor", "No threshold breach in the selected cohort",
                "Continue collecting validated outcomes and labeled evaluation examples.",
                "Measured review and external-dependency rates remain within default governance thresholds, or evidence is insufficient.",
                "terminal-intake"));
        return output;
    }

    private async Task<QualityThresholds> ThresholdsAsync(long tenantId, CancellationToken ct)
    {
        var artifact = await db.GovernedArtifacts.AsNoTracking().Where(x => x.BusinessUnitId == tenantId
                && x.ArtifactType == GovernedArtifactType.QualityMetricSet
                && x.Status == GovernedLifecycleStatus.Production && x.ProductionVersionNumber.HasValue)
            .OrderByDescending(x => x.UpdatedOn).FirstOrDefaultAsync(ct);
        if (artifact is null) return QualityThresholds.Default;
        var version = await db.GovernedArtifactVersions.AsNoTracking().SingleAsync(x =>
            x.BusinessUnitId == tenantId && x.GovernedArtifactId == artifact.Id
            && x.VersionNumber == artifact.ProductionVersionNumber!.Value, ct);
        using var document = JsonDocument.Parse(version.DefinitionJson);
        var root = document.RootElement;
        return new(
            Math.Clamp(root.GetProperty("minimumSampleSize").GetInt32(), 1, 100_000),
            root.GetProperty("reviewRateWarningPercent").GetDecimal(),
            root.GetProperty("externalDependencyCeilingPercent").GetDecimal(),
            root.GetProperty("turnaroundP95WarningMinutes").GetDecimal(),
            $"quality-metric-set:{artifact.ArtifactKey}:v{version.VersionNumber}");
    }

    private sealed record QualityThresholds(int MinimumSampleSize, decimal ReviewRateWarningPercent,
        decimal ExternalDependencyCeilingPercent, decimal TurnaroundP95WarningMinutes,
        string DefinitionVersion)
    {
        public static QualityThresholds Default { get; } = new(30, 20m, 10m, 15m,
            "quality-analytics/default-v1");
    }
}
