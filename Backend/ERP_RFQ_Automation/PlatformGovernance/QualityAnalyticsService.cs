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
    string CostStatus,
    /// <summary>
    /// This occurrence had at least one external call with NO allow-list receipt — the
    /// population the ceiling governs, and a strict subset of
    /// <paramref name="ExternalProcessing"/>. Separate because the Critical recommendation
    /// links to evidence, and listing approved egress under "unauthorized" is the same
    /// mislabelling the recommendation itself was fixed for.
    /// </summary>
    bool UnauthorizedExternalProcessing = false);
/// <summary>
/// A recommendation and the evidence behind it. <paramref name="MetricKey"/> names the metric
/// it was judged on — separate from <paramref name="DrilldownKey"/>, which names the cohort of
/// documents to list, because several metrics share one cohort. Empty when a recommendation is
/// not about a single metric.
/// </summary>
public sealed record QualityRecommendation(string Priority, string Title, string Recommendation,
    string Evidence, string DrilldownKey, string MetricKey = "");
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
        // Identity baselines are excluded: they are always ProcessingPath.Deterministic, so
        // counting them would inflate the touchless-decision rate published on this
        // auditor-facing screen with decisions no pipeline actually made.
        var leadPaths = await db.Set<LeadIngestionOccurrence>().AsNoTracking()
            .Where(x => x.BusinessUnitId == tenantId && x.IngestedAtUtc >= fromDate && x.IngestedAtUtc <= toDate
                        && x.RecordKind == LeadOccurrenceRecordKind.Ingestion)
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
        // Ground truth accumulated so far. Deliberately NOT windowed: a labelled document
        // does not stop being a labelled document because the reporting window moved.
        var labelledDocuments = await db.Set<ExtractionCorpusEntry>().AsNoTracking()
            .Where(x => x.BusinessUnitId == tenantId)
            .Select(x => x.LeadReviewAuditId).Distinct().LongCountAsync(ct);

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
        // A DENIED reservation is not a governed call and was never egress: it is the control
        // working. Those rows carry ProviderClass.External with a null receipt — the refusal
        // never reached a provider, so there was no authorization to record — which made them
        // indistinguishable from unauthorized egress to any count that does not exclude them.
        // Counting them inverted this screen: because the allow-list gate refuses every
        // unauthorized external reservation outright, a SUCCEEDED external row always carries
        // a receipt, so on current data a denial was the only thing that could make the
        // unauthorized share nonzero. The better the gate worked, the louder the alarm.
        var governedCalls = ai.Where(x => x.Status != AiCallStatuses.Denied).ToList();
        var localAi = governedCalls.Count(x => x.ProviderClass == AiProviderClass.Local);
        var externalAi = governedCalls.Count(x => x.ProviderClass == AiProviderClass.External);
        var authorizedExternalAi = governedCalls.Count(x => x.ProviderClass == AiProviderClass.External
            && x.ExternalAuthorizationId != null);
        // One definition, computed by the enforcement projection over THIS screen's cohort, so
        // the number here and the number on AI Trust differ only by the window an operator
        // chose — never by the rule. Recomputing the share locally is what let the two screens
        // disagree on both the numerator (denials) and the denominator (Unknown provider class).
        // Floored at the receipt era. This screen's window is operator-chosen and reaches back
        // a year, so unlike AI Trust's month it can still see rows written before the
        // authorization receipt was recorded on every authorized call. Those cannot be
        // classified, and counting them as unauthorized made the 90- and 365-day cohorts carry
        // a Critical that the 30-day cohort did not — the same tenant, the same second, two
        // verdicts, decided by a dropdown.
        var classifiable = governedCalls
            .Where(x => x.CreatedOn >= AiExternalDependencyEvaluator.ReceiptRecordingBegan)
            .ToList();
        var dependency = AiExternalDependencyEvaluator.Evaluate(
            classifiable.Select(x => new AiExternalDependencyEvaluator.GovernedCall(
                x.ProviderClass, x.ExternalAuthorizationId, x.Status)).ToList(),
            thresholds.ExternalDependencyCeilingPercent);
        var unauthorizedExternalAi = dependency.External - dependency.AuthorizedExternal;
        var governedAi = dependency.Total;
        var localAiOccurrences = governedCalls.Where(x => x.ProviderClass == AiProviderClass.Local
                && x.SourceDocumentOccurrenceId.HasValue)
            .Select(x => x.SourceDocumentOccurrenceId!.Value).ToHashSet();
        var externalAiOccurrences = governedCalls.Where(x => x.ProviderClass == AiProviderClass.External
                && x.SourceDocumentOccurrenceId.HasValue)
            .Select(x => x.SourceDocumentOccurrenceId!.Value).ToHashSet();
        // The Critical recommendation links to evidence, so the evidence has to be the
        // documents it is actually about. An earlier comment claimed an occurrence carries no
        // per-call authorization to narrow by; it does — the same AiRequest rows that build the
        // set above carry both the receipt and the occurrence id.
        var unauthorizedExternalAiOccurrences = classifiable
            .Where(x => x.ProviderClass == AiProviderClass.External
                && x.ExternalAuthorizationId == null && x.SourceDocumentOccurrenceId.HasValue)
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
                "External governed AI requests / all governed AI requests. Egress as it happened, authorized or not, excluding denied reservations, which never reached a provider. This is the number an auditor asks for, and it carries no threshold.", "external-ai", thresholds.MinimumSampleSize),
            // The share the ceiling is actually enforced against. Published alongside the raw
            // figure rather than replacing it: on a deployment with no loopback endpoint the
            // raw share is 100% and the unauthorized share is 0%, and an auditor needs to see
            // both — the first says everything left the box, the second says everything that
            // left it was approved. Reporting only the raw share made the recommendation
            // below fire "Critical" forever; reporting only this one would have hidden the
            // egress entirely.
            Rate("unauthorized-external-dependency", "Unauthorized external AI dependency",
                unauthorizedExternalAi, governedAi,
                "External governed AI requests with no allow-list authorization receipt / all governed AI requests. Denied reservations are excluded: a refusal is the control working, not egress. This is the ratio the external-dependency ceiling governs.", "unauthorized-external-ai", thresholds.MinimumSampleSize),
            Rate("correction-reuse", "Processing reuse", reused, occurrences.Count,
                "Occurrences reusing parser, OCR, local-model or prior processing / all intake occurrences.", "processing-reuse", thresholds.MinimumSampleSize),
            Duration("turnaround-p50", "Extraction turnaround p50", durationMinutes, .50m),
            Duration("turnaround-p95", "Extraction turnaround p95", durationMinutes, .95m),
            Rate("action-completion", "Human action completion", actions.Count(x => x.Status == HumanActionStatus.Completed),
                actions.Count, "Completed governed human actions / governed human actions created in the period.", "actions", thresholds.MinimumSampleSize),
            // Progress toward a publishable accuracy figure. Carries a COUNT and no value:
            // "4 of 30 approved documents" is the honest state of the evidence, and a
            // percentage here would be read as an accuracy the moment it appeared on a
            // screen next to nine other percentages.
            new("accuracy-corpus", "Labelled documents toward a published accuracy figure", null,
                "documents", labelledDocuments,
                ERP_RFQ_Automation.Services.Measurement.AccuracyMeasurementService.MinimumDocuments,
                "Documents whose extraction a reviewer has approved, becoming ground truth. "
                + $"At {ERP_RFQ_Automation.Services.Measurement.AccuracyMeasurementService.MinimumDocuments} "
                + "per field and extraction path, a 95% Wilson lower bound is published. "
                + "This row is a sample size, never an accuracy.",
                labelledDocuments >= ERP_RFQ_Automation.Services.Measurement.AccuracyMeasurementService.MinimumDocuments
                    ? "Measured" : "InsufficientEvidence",
                "accuracy-corpus")
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
                x.Occurrence.TotalActualCost, x.Occurrence.CostStatus,
                // Deliberately NOT falling back to path.ExternalAiUsed the way the line above
                // does: that flag records that external AI was used, not whether it was
                // authorized, so treating it as unauthorized would put approved egress back
                // into the cohort this arm exists to separate.
                unauthorizedExternalAiOccurrences.Contains(x.Occurrence.Id));
        }).Where(x => MatchesDrilldown(x, drilldown)).OrderByDescending(x => x.IngestedOn).Take(100).ToList();
        var recommendations = Recommendations(metrics, causes, thresholds, dependency);
        return new(fromDate, toDate, metrics, causes, records, recommendations, thresholds.DefinitionVersion,
            "None of the rates on this page is an extraction accuracy, and none should be quoted as one. "
            + "They describe validation outcomes, routing and throughput. Measured accuracy requires labelled "
            + "ground truth, which Nexora now harvests from reviewers' own corrections on approved documents "
            + $"({labelledDocuments} collected so far). A per-field figure is published, as a 95% Wilson lower "
            + $"bound, once {ERP_RFQ_Automation.Services.Measurement.AccuracyMeasurementService.MinimumDocuments} "
            + "approved documents exist for a given field and extraction path; below that the accuracy endpoint "
            + "returns counts and no percentage.");
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
        "unauthorized-external-ai" => item.UnauthorizedExternalProcessing,
        "local-ai" => item.LocalProcessing,
        "processing-reuse" => item.ProcessingReused,
        "terminal-intake" => item.IntakeStatus is "Resolved" or "ReviewRequired" or "Rejected" or "DeadLetter",
        _ => true
    };

    private static IReadOnlyList<QualityRecommendation> Recommendations(
        IReadOnlyList<QualityMetric> metrics, IReadOnlyList<QualityCause> causes,
        QualityThresholds thresholds, AiExternalDependencySnapshot dependency)
    {
        var output = new List<QualityRecommendation>();
        var review = metrics.Single(x => x.Key == "human-review");
        if (review.Denominator >= thresholds.MinimumSampleSize
            && review.Value > thresholds.ReviewRateWarningPercent)
            output.Add(new("High", "Reduce repeated review demand",
                "Evaluate the leading exception against the current document skill and rule versions.",
                $"Human review is {review.Value}% ({review.Numerator}/{review.Denominator}); leading cause: {causes.FirstOrDefault()?.Code ?? "not classified"}.",
                "human-review", "human-review"));
        // Judged on the UNAUTHORIZED share, which is what the ceiling governs and what the
        // sentence below has always claimed to be reporting. Judging the raw external share
        // against it raised a permanent Critical on every deployment whose inference endpoint
        // is not loopback, while enforcement denied nothing — a recommendation nobody can act
        // on trains an auditor to ignore the ones that matter.
        var external = metrics.Single(x => x.Key == "unauthorized-external-dependency");
        // The VERDICT comes from the evaluator, not from re-comparing the card's displayed
        // value. Quality Analytics renders every rate at two decimals while the evaluator
        // reports one, so comparing the rendered number would let the two screens disagree
        // about a breach at a fractional ceiling even though they now count identical rows —
        // the same rounded-comparison hole that let 2.53% sit quietly under a 2.5% ceiling.
        // Precision may differ between screens; the decision may not.
        if (external.Denominator >= thresholds.MinimumSampleSize && dependency.CeilingBreached)
            output.Add(new("Critical", "Review external dependency against the allow-list",
                "Inspect external call evidence and move supported operations to approved local paths.",
                $"Unauthorized external dependency is {external.Value}% ({external.Numerator}/{external.Denominator}), above the {thresholds.ExternalDependencyCeilingPercent}% ceiling. Allow-list-authorized calls are exempt and are excluded from this figure.",
                "unauthorized-external-ai", "unauthorized-external-dependency"));
        if (output.Count == 0)
            output.Add(new("Monitor", "No threshold breach in the selected cohort",
                "Continue collecting validated outcomes and labeled evaluation examples.",
                "Measured review and external-dependency rates remain within default governance thresholds, or evidence is insufficient.",
                // Named for the metric it is about, like every other recommendation. Left empty
                // it selected a cohort with nothing on screen explaining the change: the record
                // table reloaded, the explanation alert vanished and every card un-pressed.
                "terminal-intake", "straight-through"));
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
