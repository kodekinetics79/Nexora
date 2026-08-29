using System.Text.Json;
using ERP_RFQ_Automation.DocumentIntelligence.Persistence;
using ERP_RFQ_Automation.LeadIdentity;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Services.Uom;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.CommercialCases.Participation;

/// <summary>
/// Enforces the one provenance contract shared by participation and formal RFQ promotion.
/// A Bid line is admissible only when its exact quote-critical values are backed by the
/// retained source document for the current revision, or by the current governed human
/// review audit whose immutable after-image covers those exact values.
/// </summary>
internal static class BidSourceProvenanceValidator
{
    internal sealed record Requirement(
        long RevisionLineId,
        long ProjectionLeadItemId,
        long EvidenceSourceLeadItemId,
        IReadOnlyList<CriticalSourceEvidence.Identity> IdentityValues,
        decimal? Quantity,
        string? UnitOfMeasure);

    internal sealed record EvidenceObject(
        long SourceDocumentId,
        string StoragePath,
        string ContentHash);

    private sealed record FieldValue(
        long LeadItemId,
        string FieldName,
        string? RawValue,
        string? NormalizedValue,
        long SourceDocumentId,
        string StoragePath,
        string ContentHash);

    internal static async Task<IReadOnlyList<EvidenceObject>> ValidateAsync(
        ErpRfqAutomationContext db,
        long businessUnitId,
        Lead lead,
        long leadRevisionId,
        IReadOnlyCollection<Requirement> requirements,
        CancellationToken ct)
    {
        if (requirements.Count == 0) return Array.Empty<EvidenceObject>();
        if (requirements.Any(x => x.ProjectionLeadItemId <= 0 || x.EvidenceSourceLeadItemId <= 0))
            throw new InvalidOperationException(
                "Every Bid line must retain exact immutable canonical Lead-item lineage.");

        var revision = await db.Set<LeadRevision>().AsNoTracking()
            .Where(x => x.BusinessUnitId == businessUnitId && x.Id == leadRevisionId && x.LeadId == lead.Id)
            .Select(x => new
            {
                x.EstablishedByOccurrenceId,
                x.ProcessingPath,
                x.CreatedBy
            }).SingleAsync(ct);
        var reviewOverride = await LoadCurrentReviewOverrideAsync(
            db, businessUnitId, lead, revision.ProcessingPath, revision.CreatedBy, ct);

        var documentIds = await db.Set<LeadOccurrenceDocument>().AsNoTracking()
            .Where(x => x.BusinessUnitId == businessUnitId
                && x.OccurrenceId == revision.EstablishedByOccurrenceId)
            .Select(x => x.SourceDocumentId).Distinct().ToListAsync(ct);
        var directDocumentId = await db.Set<LeadIngestionOccurrence>().AsNoTracking()
            .Where(x => x.BusinessUnitId == businessUnitId
                && x.Id == revision.EstablishedByOccurrenceId && x.LeadId == lead.Id)
            .Select(x => x.SourceDocumentId).SingleAsync(ct);
        if (directDocumentId.HasValue && !documentIds.Contains(directDocumentId.Value))
            documentIds.Add(directDocumentId.Value);
        if (documentIds.Count == 0 && reviewOverride is null)
            throw new InvalidOperationException(
                "A committed Bid requires retained source documents for the current revision or a current governed human-review approval.");

        var sourceIds = requirements.Select(x => x.EvidenceSourceLeadItemId).Distinct().ToArray();
        var evidence = documentIds.Count == 0
            ? Array.Empty<FieldValue>()
            : await (from field in db.Set<FieldEvidence>().AsNoTracking()
                join job in db.Set<ERP_RFQ_Automation.Extraction.ExtractionJob>().AsNoTracking()
                    on new { field.BusinessUnitId, Id = field.ExtractionRun.ExtractionJobId }
                    equals new { job.BusinessUnitId, job.Id }
                where field.BusinessUnitId == businessUnitId && field.LineItem != null
                    && field.LineItem.LeadItemId.HasValue
                    && sourceIds.Contains(field.LineItem.LeadItemId.Value)
                    && documentIds.Contains(field.ExtractionRun.SourceDocumentId)
                    && field.ExtractionRun.SourceDocument.SecurityStatus == DocumentSecurityStatus.Cleared
                    && field.ExtractionRun.SourceDocument.PurgeState == EvidencePurgeState.Present
                    && field.ExtractionRun.SourceDocument.ExtractionJobId == job.Id
                    && field.ExtractionRun.SourceDocument.ContentHash == job.ContentHash
                    && field.ValidationStatus == FieldValidationStatus.Valid
                    && job.StoragePath != null && job.StoragePath != ""
                select new FieldValue(
                    field.LineItem!.LeadItemId!.Value,
                    field.FieldName,
                    field.RawValue,
                    field.NormalizedValue,
                    field.ExtractionRun.SourceDocumentId,
                    job.StoragePath!,
                    field.ExtractionRun.SourceDocument.ContentHash)).ToArrayAsync(ct);

        foreach (var requirement in requirements)
        {
            var lineEvidence = evidence
                .Where(x => x.LeadItemId == requirement.EvidenceSourceLeadItemId).ToArray();
            var effectiveUom = UomCanonicalizer.CanonicalizeForStorage(requirement.UnitOfMeasure);
            var missing = MissingCriticalEvidence(
                lineEvidence, requirement.IdentityValues, requirement.Quantity, effectiveUom);
            if (missing.Count == 0) continue;
            if (reviewOverride is not null
                && ReviewOverrideCoversLine(reviewOverride, requirement, effectiveUom))
                continue;

            throw new InvalidOperationException(
                $"Bid revision line {requirement.RevisionLineId} cannot be committed or promoted because "
                + $"the current source lacks exact evidence for {string.Join(", ", missing)}. "
                + "Record those source fields or complete a governed extraction approval for "
                + "the current revision with actor, timestamp, reason, and before/after snapshots.");
        }

        return evidence
            .GroupBy(x => x.SourceDocumentId)
            .Select(x => x.First())
            .Select(x => new EvidenceObject(x.SourceDocumentId, x.StoragePath, x.ContentHash))
            .ToArray();
    }

    internal static async Task<LeadReviewAudit?> LoadCurrentReviewOverrideAsync(
        ErpRfqAutomationContext db,
        long businessUnitId,
        Lead lead,
        LeadProcessingPath revisionPath,
        string revisionCreatedBy,
        CancellationToken ct)
    {
        if (revisionPath != LeadProcessingPath.HumanReview
            || string.IsNullOrWhiteSpace(lead.ReviewApprovedBy)
            || !lead.ReviewApprovedOn.HasValue
            || lead.ReviewVersion <= 0)
            return null;

        var audit = await db.Set<LeadReviewAudit>().AsNoTracking()
            .Where(x => x.BusinessUnitId == businessUnitId && x.LeadId == lead.Id
                && x.Action == "approve" && x.ToVersion == lead.ReviewVersion)
            .OrderByDescending(x => x.ReviewedOn)
            .FirstOrDefaultAsync(ct);
        if (audit is null
            || audit.FromVersion != audit.ToVersion - 1
            || string.IsNullOrWhiteSpace(audit.ReviewedBy)
            || string.IsNullOrWhiteSpace(audit.Reason)
            || audit.ReviewedOn == default
            || audit.ReviewedOn < lead.ReviewApprovedOn.Value
            || string.IsNullOrWhiteSpace(audit.BeforeJson)
            || string.IsNullOrWhiteSpace(audit.AfterJson)
            || !string.Equals(audit.ReviewedBy, lead.ReviewApprovedBy, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(audit.ReviewedBy, revisionCreatedBy, StringComparison.OrdinalIgnoreCase)
            || !IsJsonObject(audit.BeforeJson)
            || !IsJsonObject(audit.AfterJson))
            return null;
        return audit;
    }

    private static IReadOnlyList<string> MissingCriticalEvidence(
        IReadOnlyCollection<FieldValue> evidence,
        IReadOnlyCollection<CriticalSourceEvidence.Identity> identityValues,
        decimal? quantity,
        string? uom)
        => CriticalSourceEvidence.Assess(
            evidence.Select(field => new CriticalSourceEvidence.Field(
                field.FieldName, field.RawValue, field.NormalizedValue)),
            identityValues, quantity, uom).Missing();

    internal static bool ReviewOverrideCoversLine(
        LeadReviewAudit audit, Requirement requirement, string? effectiveUom)
    {
        try
        {
            using var after = JsonDocument.Parse(audit.AfterJson);
            if (!after.RootElement.TryGetProperty("commercialFactsVerified", out var verified)
                || verified.ValueKind != JsonValueKind.True
                || !after.RootElement.TryGetProperty("reviewVersion", out var version)
                || !version.TryGetInt32(out var reviewVersion)
                || reviewVersion != audit.ToVersion
                || !after.RootElement.TryGetProperty("items", out var items)
                || items.ValueKind != JsonValueKind.Array)
                return false;
            foreach (var item in items.EnumerateArray())
            {
                var projectionId = JsonInt64(item, "projectionId");
                var logicalId = JsonInt64(item, "id");
                if (projectionId != requirement.ProjectionLeadItemId
                    && logicalId != requirement.EvidenceSourceLeadItemId)
                    continue;
                var auditedIdentity = new[]
                {
                    JsonString(item, "itemMaterialCode"),
                    JsonString(item, "manufacturerPartNumber"),
                    JsonString(item, "productShortName"),
                    JsonString(item, "productShortDescription"),
                    JsonString(item, "itemText")
                };
                var expectedIdentity = requirement.IdentityValues
                    .Select(x => x.Value).Where(x => !string.IsNullOrWhiteSpace(x)).ToArray();
                var identityMatches = auditedIdentity.Where(x => !string.IsNullOrWhiteSpace(x))
                    .Any(value => expectedIdentity.Any(expected => SameCommercialText(value!, expected!)));
                var quantityMatches = requirement.Quantity.HasValue
                    && JsonDecimal(item, "quantity") == requirement.Quantity.Value;
                var uomMatches = effectiveUom is not null
                    && string.Equals(UomCanonicalizer.CanonicalizeForStorage(
                        JsonString(item, "unitOfMeasure")), effectiveUom,
                        StringComparison.OrdinalIgnoreCase);
                return identityMatches && quantityMatches && uomMatches;
            }
        }
        catch (JsonException)
        {
            return false;
        }
        return false;
    }

    private static bool SameCommercialText(string left, string right)
        => string.Equals(CanonicalCommercialText(left), CanonicalCommercialText(right),
            StringComparison.Ordinal);

    private static string CanonicalCommercialText(string value)
        => new(value.Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());

    private static bool IsJsonObject(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.ValueKind == JsonValueKind.Object;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static long? JsonInt64(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.TryGetInt64(out var parsed)
            ? parsed : null;

    private static decimal? JsonDecimal(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.TryGetDecimal(out var parsed)
            ? parsed : null;

    private static string? JsonString(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() : null;

}
