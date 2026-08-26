using System.Globalization;
using System.Text.Json;
using ERP_RFQ_Automation.CommercialCases.Promotion;
using ERP_RFQ_Automation.CommercialCases.Lifecycle;
using ERP_RFQ_Automation.DocumentIntelligence.Persistence;
using ERP_RFQ_Automation.Intelligence.Conversion;
using ERP_RFQ_Automation.LeadIdentity;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Sla;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.CommercialCases.Participation;

public interface ILeadDecisionWorkbenchService
{
    Task<LeadDecisionWorkbenchDto> GetAsync(long businessUnitId, long leadId, CancellationToken ct = default);
}

public sealed class LeadDecisionWorkbenchService : ILeadDecisionWorkbenchService
{
    private readonly ErpRfqAutomationContext _db;
    private readonly ILeadOutcomeReasons _leadOutcomeReasons;
    private readonly ILeadConversionIntelligence _conversionIntelligence;
    public LeadDecisionWorkbenchService(ErpRfqAutomationContext db, ILeadOutcomeReasons leadOutcomeReasons,
        ILeadConversionIntelligence conversionIntelligence)
    {
        _db = db;
        _leadOutcomeReasons = leadOutcomeReasons;
        _conversionIntelligence = conversionIntelligence;
    }

    public LeadDecisionWorkbenchService(ErpRfqAutomationContext db, ILeadOutcomeReasons leadOutcomeReasons)
        : this(db, leadOutcomeReasons, new LeadConversionIntelligence(db))
    {
    }

    public async Task<LeadDecisionWorkbenchDto> GetAsync(long businessUnitId, long leadId, CancellationToken ct = default)
    {
        var lead = await _db.Leads.AsNoTracking().Include(x => x.LeadStatus).Include(x => x.AssignToNavigation)
            .Include(x => x.LeadItems)
            .SingleOrDefaultAsync(x => x.BusinessUnitId == businessUnitId && x.Id == leadId, ct)
            ?? throw new KeyNotFoundException($"Lead {leadId} was not found in this business unit.");
        if (!lead.CurrentRevisionId.HasValue)
            throw new InvalidOperationException("The lead has no immutable current revision. Reconcile its source evidence first.");

        var revision = await _db.Set<LeadRevision>().AsNoTracking().Include(x => x.Items)
            .SingleAsync(x => x.BusinessUnitId == businessUnitId && x.Id == lead.CurrentRevisionId.Value, ct);
        var occurrence = await _db.Set<LeadIngestionOccurrence>().AsNoTracking()
            .SingleAsync(x => x.BusinessUnitId == businessUnitId && x.Id == revision.EstablishedByOccurrenceId, ct);
        var links = await _db.Set<LeadOccurrenceDocument>().AsNoTracking()
            .Where(x => x.BusinessUnitId == businessUnitId && x.OccurrenceId == occurrence.Id)
            .OrderBy(x => x.Ordinal).ToListAsync(ct);
        var authorizedDocumentIds = links.Select(x => x.SourceDocumentId)
            .Concat(occurrence.SourceDocumentId.HasValue ? new[] { occurrence.SourceDocumentId.Value } : Array.Empty<long>())
            .ToHashSet();
        var documentIds = authorizedDocumentIds.ToArray();
        var documents = documentIds.Length == 0
            ? new Dictionary<long, SourceDocument>()
            : await _db.Set<SourceDocument>().AsNoTracking().Where(x => documentIds.Contains(x.Id))
                .ToDictionaryAsync(x => x.Id, ct);
        var extractionJobIds = documents.Values.Where(x => x.ExtractionJobId.HasValue)
            .Select(x => x.ExtractionJobId!.Value).Distinct().ToArray();
        var extractionJobs = extractionJobIds.Length == 0
            ? new Dictionary<long, ERP_RFQ_Automation.Extraction.ExtractionJob>()
            : await _db.Set<ERP_RFQ_Automation.Extraction.ExtractionJob>().AsNoTracking()
                .Where(x => extractionJobIds.Contains(x.Id)).ToDictionaryAsync(x => x.Id, ct);
        var downloadableDocumentIds = documents.Values.Where(document =>
                authorizedDocumentIds.Contains(document.Id)
                && document.SecurityStatus == DocumentSecurityStatus.Cleared
                && document.PurgeState == EvidencePurgeState.Present
                && document.ExtractionJobId.HasValue
                && extractionJobs.TryGetValue(document.ExtractionJobId.Value, out var job)
                && job.BusinessUnitId == businessUnitId
                && job.ContentHash == document.ContentHash
                && !string.IsNullOrWhiteSpace(job.StoragePath))
            .Select(x => x.Id).ToHashSet();
        var fit = await _db.Set<LeadFitAssessment>().AsNoTracking()
            .Where(x => x.BusinessUnitId == businessUnitId && x.LeadRevisionId == revision.Id)
            .OrderByDescending(x => x.Sequence).FirstOrDefaultAsync(ct);
        var decision = await _db.Set<LeadParticipationDecision>().AsNoTracking().Include(x => x.Lines)
            .Where(x => x.BusinessUnitId == businessUnitId && x.LeadRevisionId == revision.Id)
            .OrderByDescending(x => x.Sequence).FirstOrDefaultAsync(ct);
        var hasDecisionOnPriorRevision = decision is null && await _db.Set<LeadParticipationDecision>().AsNoTracking()
            .AnyAsync(x => x.BusinessUnitId == businessUnitId && x.LeadId == leadId, ct);
        var promotion = await _db.Set<RfqPromotion>().AsNoTracking()
            .SingleOrDefaultAsync(x => x.BusinessUnitId == businessUnitId && x.LeadId == leadId, ct);
        var rfq = await _db.Rfqs.AsNoTracking()
            .SingleOrDefaultAsync(x => x.BusinessUnitId == businessUnitId && x.LeadId == leadId, ct);
        var promotedRevision = promotion is null ? null : await _db.Set<LeadRevision>().AsNoTracking()
            .SingleOrDefaultAsync(x => x.BusinessUnitId == businessUnitId && x.Id == promotion.LeadRevisionId, ct);
        var promotedDecision = promotion is null ? null : await _db.Set<LeadParticipationDecision>().AsNoTracking()
            .SingleOrDefaultAsync(x => x.BusinessUnitId == businessUnitId
                && x.Id == promotion.ParticipationDecisionId, ct);
        var openRfqRevisionImpact = rfq is null ? null : await _db.Set<LeadRevisionImpact>().AsNoTracking()
            .Where(x => x.BusinessUnitId == businessUnitId && x.LeadId == leadId
                && x.AggregateType == "RFQ" && x.AggregateId == rfq.Id
                && x.ImpactType == "RFQ_REVISION_REQUIRED" && x.Status == "OPEN")
            .OrderByDescending(x => x.CreatedAtUtc).FirstOrDefaultAsync(ct);
        var customerName = lead.CustomerId.HasValue
            ? await _db.Customers.AsNoTracking().Where(x => x.Buid == businessUnitId && x.Id == lead.CustomerId.Value)
                .Select(x => x.Name).SingleOrDefaultAsync(ct)
            : null;
        // Catalog matching and quantity/UOM normalization remain read-only decision support.
        // They are deliberately surfaced inside the governed workbench instead of reviving the
        // retired intelligence conversion door.
        var conversionPreview = await _conversionIntelligence.PreviewAsync(leadId, businessUnitId, ct);
        var previewByLeadItem = conversionPreview.Items.ToDictionary(x => x.LeadItemId);

        var evidence = new List<LeadDecisionEvidenceDto>();
        if (occurrence.RecordKind == LeadOccurrenceRecordKind.Ingestion)
        {
            var sourceAvailable = occurrence.SourceDocumentId.HasValue
                && downloadableDocumentIds.Contains(occurrence.SourceDocumentId.Value);
            evidence.Add(new(occurrence.Id, occurrence.SourceDocumentId, "EMAIL_BODY",
                occurrence.Subject ?? occurrence.OriginalFileName ?? "Inbound inquiry", occurrence.MimeType,
                occurrence.SourceReceivedAtUtc, occurrence.Classification.ToString(), sourceAvailable,
                sourceAvailable ? $"/api/File/source-document/{occurrence.SourceDocumentId!.Value}" : null,
                sourceAvailable ? "Digest-bound immutable source evidence; integrity is verified when opened."
                    : "The exact source object is not cleared, retained, or bound to a readable extraction job."));
        }
        foreach (var link in links)
        {
            documents.TryGetValue(link.SourceDocumentId, out var document);
            var sourceAvailable = downloadableDocumentIds.Contains(link.SourceDocumentId);
            evidence.Add(new(occurrence.Id, link.SourceDocumentId, "ATTACHMENT",
                document?.OriginalFileName ?? occurrence.OriginalFileName ?? $"Source document {link.SourceDocumentId}",
                document?.DetectedMimeType ?? occurrence.MimeType, occurrence.SourceReceivedAtUtc,
                document?.ProcessingStatus.ToString() ?? "Linked", sourceAvailable,
                sourceAvailable ? $"/api/File/source-document/{link.SourceDocumentId}" : null,
                sourceAvailable ? "Digest-bound immutable source evidence; integrity is verified when opened."
                    : "The exact source object is not cleared, retained, or bound to a readable extraction job."));
        }

        var canonicalLeadItems = lead.LeadItems.OrderBy(x => x.Id).ToArray();
        var evidenceSourceByLeadItemId = canonicalLeadItems.ToDictionary(
            x => x.Id, x => x.EvidenceSourceLeadItemId ?? x.Id);
        var evidenceSourceLeadItemIds = evidenceSourceByLeadItemId.Values.Distinct().ToArray();
        var currentDocumentIds = authorizedDocumentIds;
        var fieldEvidenceRows = evidenceSourceLeadItemIds.Length == 0 || currentDocumentIds.Count == 0
            ? new List<LineFieldEvidenceProjection>()
            : (await (from field in _db.Set<FieldEvidence>().AsNoTracking()
                      join job in _db.Set<ERP_RFQ_Automation.Extraction.ExtractionJob>().AsNoTracking()
                          on new { field.BusinessUnitId, Id = field.ExtractionRun.ExtractionJobId }
                          equals new { job.BusinessUnitId, job.Id }
                      where field.BusinessUnitId == businessUnitId && field.LineItem != null
                          && field.LineItem.LeadItemId.HasValue
                          && evidenceSourceLeadItemIds.Contains(field.LineItem.LeadItemId.Value)
                          && currentDocumentIds.Contains(field.ExtractionRun.SourceDocumentId)
                          && field.ExtractionRun.SourceDocument.SecurityStatus == DocumentSecurityStatus.Cleared
                          && field.ExtractionRun.SourceDocument.PurgeState == EvidencePurgeState.Present
                          && field.ExtractionRun.SourceDocument.ExtractionJobId == job.Id
                          && field.ExtractionRun.SourceDocument.ContentHash == job.ContentHash
                          && job.StoragePath != null && job.StoragePath != ""
                      select new LineFieldEvidenceProjection(field.LineItem!.LeadItemId!.Value,
                          field.FieldName, field.RawValue, field.Region.SourceAddress))
                .Distinct().ToListAsync(ct));
        var evidencedLeadItemIds = fieldEvidenceRows.Select(x => x.LeadItemId).ToHashSet();
        var exactSourceByLeadItemId = fieldEvidenceRows
            .Where(x => !string.IsNullOrWhiteSpace(x.RawValue))
            .GroupBy(x => x.LeadItemId)
            .ToDictionary(group => group.Key, group => group
                .OrderBy(x => x.FieldName == "ProductName" ? 0
                    : x.FieldName == "ManufacturerPartNumber" ? 1
                    : x.FieldName == "Quantity" ? 2
                    : x.FieldName == "UnitOfMeasure" ? 3
                    : x.FieldName == "Currency" ? 4 : 5)
                .ThenBy(x => x.FieldName, StringComparer.Ordinal)
                .ToArray());
        var canonicalById = canonicalLeadItems.ToDictionary(x => x.Id);
        var decisionByLine = decision?.Lines.ToDictionary(x => x.LeadItemRevisionId) ?? new();
        var lines = revision.Items.OrderBy(x => x.LineNumber).Select(item =>
        {
            decisionByLine.TryGetValue(item.Id, out var lineDecision);
            var canonical = item.LeadItemId.HasValue && canonicalById.TryGetValue(item.LeadItemId.Value, out var linked)
                ? linked : null;
            var preview = canonical is not null && previewByLeadItem.TryGetValue(canonical.Id, out var resolved)
                ? resolved : null;
            var sourceAvailable = canonical is not null
                && evidencedLeadItemIds.Contains(evidenceSourceByLeadItemId[canonical.Id]);
            var verification = !sourceAvailable ? "MISSING_SOURCE"
                : lead.CommercialFactsVerified ? "VERIFIED" : "NEEDS_CHECK";
            var catalogMatches = preview?.Matches.Select(match => new CatalogMatchDto(match.ProductId,
                match.ProductName, match.MaterialCode, match.ManufacturerPartNumber, match.Score, match.Reason)).ToArray()
                ?? Array.Empty<CatalogMatchDto>();
            var catalogResolution = catalogMatches.Length == 0 ? null
                : $"{catalogMatches[0].ProductName ?? catalogMatches[0].MaterialCode ?? $"Product #{catalogMatches[0].ProductId}"} ({catalogMatches[0].Score:P0})";
            const string catalogPolicyVersion = "lead-conversion-preview/v1";
            var warningSnapshotJson = JsonSerializer.Serialize(new
            {
                preview?.NeedsAttention,
                preview?.AttentionReason,
                preview?.Confidence,
                Matches = preview?.Matches.Select(x => new
                {
                    x.ProductId, x.ProductName, x.MaterialCode, x.ManufacturerPartNumber, x.Score, x.Reason
                }) ?? []
            });
            LineFieldEvidenceProjection[] exactSources = [];
            if (canonical is not null
                && exactSourceByLeadItemId.TryGetValue(evidenceSourceByLeadItemId[canonical.Id], out var foundSources))
                exactSources = foundSources;
            var exactSource = exactSources.FirstOrDefault();
            var lineItemNo = string.IsNullOrWhiteSpace(canonical?.LineItemNo)
                ? item.LineNumber.ToString(CultureInfo.InvariantCulture)
                : canonical.LineItemNo.Trim();
            return new LeadDecisionLineDto(item.Id, item.Id, lineItemNo,
                exactSource?.RawValue, exactSource?.FieldName, exactSource?.SourceAddress,
                exactSources.Select(x => new LineSourceFieldDto(x.FieldName, x.RawValue!, x.SourceAddress)).ToArray(),
                canonical?.ProductShortName, canonical?.ProductShortDescription, canonical?.ManufacturerName,
                canonical?.ManufacturerPartNumber, canonical?.Quantity, canonical?.UnitOfMeasure, canonical?.Currency,
                preview?.NormalizedQuantity, preview?.NormalizedUom, catalogResolution, catalogMatches,
                preview?.BestMatchProductId, preview?.Confidence ?? 0m, preview?.NeedsAttention ?? true,
                preview?.AttentionReason, catalogPolicyVersion, warningSnapshotJson, verification,
                sourceAvailable
                    ? lead.CommercialFactsVerified
                        ? "Persisted field evidence is linked and the commercial facts were human-verified."
                        : "Persisted field evidence exists; human verification is still required."
                    : "No persisted field evidence maps to this canonical Lead line.",
                lineDecision is null ? null : new LineParticipationDto(lineDecision.Choice.ToString(),
                    lineDecision.ReasonCode, lineDecision.ReasonNotes, lineDecision.ProductId,
                    lineDecision.Quantity, lineDecision.UnitOfMeasure, lineDecision.Currency,
                    lineDecision.CatalogPolicyVersion, lineDecision.WarningSnapshotJson));
        }).ToArray();

        var blockers = new List<DecisionBlockerDto>();
        if (rfq is not null && promotion is null)
            blockers.Add(new("LEGACY_RFQ",
                "A formal RFQ already exists for this Lead without a governed promotion receipt. The decision record is read-only; review the existing RFQ without fabricating missing lineage.",
                "Open existing RFQ", $"/procurement/rfqs/view/{rfq.Id}"));
        if (LifecyclePolicy.Canonicalize("Lead", lead.LeadStatus?.SetupCode, lead.LeadStatus?.SetupValue)
                == "CONVERTED_TO_RFQ" && rfq is null)
            blockers.Add(new("INCONSISTENT_CONVERTED_STATE",
                "This Lead is marked converted but no formal RFQ exists. An administrator must audit and repair the lifecycle state.",
                "Open Lead lifecycle", $"/procurement/leads/view/{lead.Id}"));
        if (openRfqRevisionImpact is not null && rfq is not null)
            blockers.Add(new("RFQ_REVISION_REQUIRED",
                "A reply or amendment created a newer immutable Lead revision after RFQ promotion. Review the existing RFQ and resolve the change through a governed RFQ revision process; do not promote again.",
                "Open existing RFQ", $"/procurement/rfqs/view/{rfq.Id}"));
        if (evidence.Count == 0) blockers.Add(new("SOURCE_UNAVAILABLE", "No retained source evidence is linked to the current revision."));
        if (lines.Any(x => x.VerificationStatus == "MISSING_SOURCE"))
            blockers.Add(new("SOURCE_LINEAGE_INCOMPLETE", "Every Lead line must have exact persisted source-field evidence before RFQ promotion."));
        if (fit is null) blockers.Add(new("FIT_REQUIRED", "Save a human fit assessment for the current revision."));
        else if (!fit.IsActionable) blockers.Add(new("FIT_NOT_ACTIONABLE", "The current fit assessment does not authorize RFQ promotion."));
        if (decision is null)
        {
            blockers.Add(hasDecisionOnPriorRevision
                ? new("PARTICIPATION_STALE", "The saved participation belongs to an earlier Lead revision. Review and recommit every current line.")
                : new("PARTICIPATION_REQUIRED", "Save participation choices for every current revision line."));
        }
        else if (fit is null || decision.FitAssessmentId != fit.Id)
            blockers.Add(new("PARTICIPATION_STALE", "The fit assessment changed after participation was saved. Review and recommit participation."));
        else if (!decision.IsCommitted) blockers.Add(new("PARTICIPATION_DRAFT", "Commit participation before RFQ promotion."));
        if (decision?.Lines.Any(x => x.Choice is LeadLineParticipationChoice.Pending or LeadLineParticipationChoice.Clarify) == true)
            blockers.Add(new("PARTICIPATION_UNRESOLVED", "Resolve Pending and Clarify lines before RFQ promotion."));
        try { LeadConversionGate.EnsureEligible(lead); }
        catch (InvalidOperationException ex)
        {
            blockers.Add(new("LEAD_NOT_ELIGIBLE", ex.Message, "Open Lead lifecycle", $"/procurement/leads/view/{lead.Id}"));
        }

        var governedNoBidReasons = (await _leadOutcomeReasons.GetAsync(businessUnitId, ct))
            .Select(x => new DecisionReasonCodeDto(x.Code, x.Label, new[] { "NoBid" },
                "Governed business-unit outcome reason."));
        var reasonCodes = governedNoBidReasons.Concat(ClarificationReasonCodes).ToArray();
        var unitOptions = await _db.SetUoms.AsNoTracking()
            .Where(x => x.BusinessUnitId == businessUnitId && x.IsActive)
            .OrderBy(x => x.UomCode)
            .Select(x => new DecisionValueOptionDto(x.UomCode, x.UomName))
            .ToArrayAsync(ct);
        var currencyOptions = await _db.Currencies.AsNoTracking()
            .Where(x => x.BusinessUnitId == businessUnitId && x.IsActive != false)
            .OrderBy(x => x.Code)
            .Select(x => new DecisionValueOptionDto(x.Code, x.CurrencyName))
            .ToArrayAsync(ct);

        var status = LeadDecisionParticipationState.Resolve(decision, fit, hasDecisionOnPriorRevision);
        return new LeadDecisionWorkbenchDto(lead.Id, revision.Id, revision.RevisionNumber, lead.CurrentRevisionNumber,
            decision?.Sequence, status, lead.LeadStatus?.SetupCode ?? "UNKNOWN", lead.LeadStatus?.SetupValue,
            lead.CommercialCaseReference, revision.CustomerRfqReference, lead.CustomerId, customerName,
            lead.BuyersName, occurrence.Sender ?? lead.Clientemail, occurrence.Subject, occurrence.ExternalSourceId,
            occurrence.SourceReceivedAtUtc, lead.BidClosingDate, lead.AssignToNavigation is null ? null
                : $"{lead.AssignToNavigation.FirstName} {lead.AssignToNavigation.LastName}".Trim(),
            lines.Length > 0 && lines.All(x => x.VerificationStatus == "VERIFIED")
                ? "VERIFIED" : evidence.Count > 0 ? "NEEDS_REVIEW" : "SOURCE_UNAVAILABLE",
            lead.ReviewApprovedBy, lead.ReviewApprovedOn.HasValue
                ? new DateTimeOffset(DateTime.SpecifyKind(lead.ReviewApprovedOn.Value, DateTimeKind.Utc)) : null,
            new SourceCoverageDto(lines.Count(x => x.VerificationStatus != "MISSING_SOURCE"), lines.Length), evidence, lines,
            reasonCodes, unitOptions, currencyOptions, fit is null ? DefaultFitAssessment() : FitDto(fit), promotion is null || rfq is null || promotedRevision is null || promotedDecision is null ? null
                : new PromotionReceiptDto(rfq.Id, rfq.Rfqno, promotedRevision.RevisionNumber, promotedDecision.Sequence,
                    rfq.NoOfLineItems ?? 0, promotion.PromotedAtUtc, promotion.PromotedBy), blockers);
    }

    private static readonly DecisionReasonCodeDto[] ClarificationReasonCodes =
    {
        new("TECHNICAL_CLARIFICATION", "Technical clarification", new[] { "Clarify" }, "The source lacks technical detail required for a bid decision."),
        new("COMMERCIAL_CLARIFICATION", "Commercial clarification", new[] { "Clarify" }, "Commercial terms require clarification before a participation commitment."),
        new("DELIVERY_CLARIFICATION", "Delivery clarification", new[] { "Clarify" }, "Delivery dates, capacity, or logistics require clarification.")
    };

    private static FitAssessmentDto FitDto(LeadFitAssessment fit)
    {
        var criteria = new List<FitCriterionDto>();
        var rationale = "Human assessment saved.";
        try
        {
            using var json = JsonDocument.Parse(fit.AssessmentJson);
            if (json.RootElement.TryGetProperty("human", out var human))
            {
                rationale = GetString(human, "rationale") ?? rationale;
                if (human.TryGetProperty("criteria", out var list) && list.ValueKind == JsonValueKind.Array)
                    foreach (var item in list.EnumerateArray())
                        criteria.Add(new(GetString(item, "code") ?? "OTHER", GetString(item, "code") ?? "Other",
                            null, GetString(item, "decision") ?? "UNKNOWN", GetString(item, "note")));
            }
        }
        catch (JsonException) { }
        return new(fit.Sequence, fit.Recommendation, rationale, criteria, fit.AssessedBy, fit.AssessedAtUtc);
    }

    private static FitAssessmentDto DefaultFitAssessment() => new(
        0,
        // This is an unsaved UI starting value, never an assessment or score. Every criterion is
        // UNKNOWN and the save command still requires a human rationale and explicit choices.
        "CONDITIONAL",
        string.Empty,
        new[]
        {
            new FitCriterionDto("ELIGIBILITY", "Eligibility", "Customer, geography, tender rules, and authority to participate.", "UNKNOWN", null),
            new FitCriterionDto("CAPABILITY", "Capability", "Product, technical, service, and supplier capability for the requested scope.", "UNKNOWN", null),
            new FitCriterionDto("DELIVERY", "Delivery", "Required dates, lead time, capacity, logistics, and destination feasibility.", "UNKNOWN", null),
            new FitCriterionDto("COMPLIANCE", "Compliance", "Regulatory, contractual, sanctions, quality, and documentation obligations.", "UNKNOWN", null),
            new FitCriterionDto("COMMERCIAL", "Commercial", "Currency, payment terms, margin, credit, exposure, and bid-cost considerations.", "UNKNOWN", null)
        },
        null,
        null);

    private static string? GetString(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private sealed record LineFieldEvidenceProjection(long LeadItemId, string FieldName,
        string? RawValue, string? SourceAddress);
}

internal static class LeadDecisionParticipationState
{
    public static string Resolve(LeadParticipationDecision? decision, LeadFitAssessment? latestFit,
        bool hasDecisionOnPriorRevision)
    {
        if (decision is null)
            return hasDecisionOnPriorRevision ? "STALE" : "NONE";
        if (latestFit is null || decision.FitAssessmentId != latestFit.Id)
            return "STALE";
        return decision.IsCommitted ? "COMMITTED" : "DRAFT";
    }
}

// Lead revision snapshots remain normalized identity evidence. Keep their parser independently
// tested, but do not use the normalized line token as the customer-facing line identifier.
internal sealed record LeadRevisionLineSnapshot(
    string? LineNumber,
    string? Part,
    string? Description,
    int? Quantity,
    string? UnitOfMeasure)
{
    public static LeadRevisionLineSnapshot Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        return new(GetString(root, "line"), GetString(root, "part"), GetString(root, "description"),
            GetInt(root, "Quantity") ?? GetInt(root, "quantity"), GetString(root, "uom"));
    }

    private static string? GetString(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static int? GetInt(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.Number
        && value.TryGetInt32(out var result)
            ? result
            : null;
}

public sealed record DecisionReasonCodeDto(string Code, string Label, IReadOnlyList<string> AppliesTo, string? Description);
public sealed record LeadDecisionEvidenceDto(long OccurrenceId, long? SourceDocumentId, string Kind, string Name,
    string? MediaType, DateTimeOffset? ReceivedAtUtc, string Status, bool SourceAvailable, string? DownloadUrl, string? Detail);
public sealed record CatalogMatchDto(long ProductId, string? ProductName, string? MaterialCode,
    string? ManufacturerPartNumber, decimal Score, string Reason);
public sealed record LineSourceFieldDto(string Field, string RawValue, string? SourceAddress);
public sealed record LineParticipationDto(string Decision, string? ReasonCode, string? Note,
    long? ProductId, int? Quantity, string? UnitOfMeasure, string? Currency,
    string CatalogPolicyVersion, string WarningSnapshotJson);
public sealed record LeadDecisionLineDto(long Id, long RevisionLineId, string? LineItemNo, string? SourceText,
    string? SourceField, string? SourceAddress, IReadOnlyList<LineSourceFieldDto> SourceFields,
    string? ProductName, string? Description, string? ManufacturerName, string? ManufacturerPartNumber,
    int? Quantity, string? UnitOfMeasure, string? Currency, decimal? NormalizedQuantity, string? NormalizedUom,
    string? CatalogResolution, IReadOnlyList<CatalogMatchDto> CatalogMatches, long? BestMatchProductId,
    decimal CatalogConfidence, bool NeedsAttention, string? AttentionReason, string CatalogPolicyVersion,
    string WarningSnapshotJson, string VerificationStatus,
    string? VerificationDetail, LineParticipationDto? Participation);
public sealed record FitCriterionDto(string Code, string Label, string? Description, string Decision, string? Note);
public sealed record FitAssessmentDto(int Version, string OverallDecision, string Rationale,
    IReadOnlyList<FitCriterionDto> Criteria, string? AssessedBy, DateTimeOffset? AssessedAtUtc);
public sealed record PromotionReceiptDto(long RfqId, string? RfqNumber, int LeadRevisionNumber,
    int ParticipationVersion, int PromotedLineCount, DateTimeOffset PromotedAtUtc, string? PromotedBy);
public sealed record SourceCoverageDto(int CoveredLines, int TotalLines);
public sealed record DecisionBlockerDto(string Code, string Message, string? ActionLabel = null, string? ActionPath = null);
public sealed record DecisionValueOptionDto(string Code, string Label);
public sealed record LeadDecisionWorkbenchDto(long LeadId, long LeadRevisionId, int LeadRevisionNumber,
    int DecisionVersion, int? ParticipationVersion, string ParticipationStatus, string LifecycleStatusCode,
    string? LifecycleStatusLabel, string? NexoraSerial, string? CustomerRfqReference, long? CustomerId,
    string? CustomerName, string? BuyerName, string? SenderEmail, string? EmailSubject, string? EmailMessageId,
    DateTimeOffset? ReceivedAtUtc, DateTime? BidClosingDate, string? AssignedToName, string VerificationStatus,
    string? VerifiedBy, DateTimeOffset? VerifiedAtUtc, SourceCoverageDto? SourceCoverage,
    IReadOnlyList<LeadDecisionEvidenceDto> Evidence, IReadOnlyList<LeadDecisionLineDto> Lines,
    IReadOnlyList<DecisionReasonCodeDto> ReasonCodes, IReadOnlyList<DecisionValueOptionDto> UnitOptions,
    IReadOnlyList<DecisionValueOptionDto> CurrencyOptions, FitAssessmentDto? FitAssessment,
    PromotionReceiptDto? Promotion, IReadOnlyList<DecisionBlockerDto> Blockers);
