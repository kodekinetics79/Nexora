using ERP_RFQ_Automation.Authorization;
using ERP_RFQ_Automation.DocumentIntelligence.Persistence;
using ERP_RFQ_Automation.Extraction;
using ERP_RFQ_Automation.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.Controllers;

[ApiController]
[Authorize]
[Route("api/processing-evidence")]
public sealed class ProcessingEvidenceController(
    ErpRfqAutomationContext db,
    ICommercialAccessContext commercialAccess) : ControllerBase
{
    [HttpGet("leads/{leadId:long}")]
    [RequireModulePermission("Leads", PermissionAction.View)]
    public async Task<ActionResult<ProcessingEvidenceResponse>> Lead(
        long leadId, CancellationToken cancellationToken)
    {
        var actor = await commercialAccess.ResolveAsync(cancellationToken);
        if (actor is null)
            return Forbid();

        var evidence = await ProcessingEvidenceQuery.ReadLeadAsync(
            db, actor, leadId, cancellationToken);
        return evidence is null ? NotFound() : Ok(evidence);
    }

    [HttpGet("rfqs/{rfqId:long}")]
    [RequireModulePermission("RFQ Management", PermissionAction.View)]
    public async Task<ActionResult<ProcessingEvidenceResponse>> Rfq(
        long rfqId, CancellationToken cancellationToken)
    {
        var actor = await commercialAccess.ResolveAsync(cancellationToken);
        if (actor is null)
            return Forbid();

        var evidence = await ProcessingEvidenceQuery.ReadRfqAsync(
            db, actor, rfqId, cancellationToken);
        return evidence is null ? NotFound() : Ok(evidence);
    }

    [HttpGet("supplier-quotes/{supplierQuoteId:long}")]
    [RequireModulePermission("Supplier History", PermissionAction.View)]
    public async Task<ActionResult<ProcessingEvidenceResponse>> SupplierQuote(
        long supplierQuoteId, CancellationToken cancellationToken)
    {
        var actor = await commercialAccess.ResolveAsync(cancellationToken);
        if (actor is null)
            return Forbid();

        var evidence = await ProcessingEvidenceQuery.ReadSupplierQuoteAsync(
            db, actor, supplierQuoteId, cancellationToken);
        return evidence is null ? NotFound() : Ok(evidence);
    }

    [HttpGet("client-purchase-orders/{clientPurchaseOrderId:long}")]
    [RequireModulePermission("Customer Awards", PermissionAction.View)]
    public async Task<ActionResult<ProcessingEvidenceResponse>> ClientPurchaseOrder(
        long clientPurchaseOrderId, CancellationToken cancellationToken)
    {
        var actor = await commercialAccess.ResolveAsync(cancellationToken);
        if (actor is null)
            return Forbid();

        var evidence = await ProcessingEvidenceQuery.ReadClientPurchaseOrderAsync(
            db, actor, clientPurchaseOrderId, cancellationToken);
        return evidence is null ? NotFound() : Ok(evidence);
    }

    /// <summary>
    /// GLASS BOX: for every extracted value on this lead, where in the customer's document
    /// it came from.
    ///
    /// The join is fully persisted and was rendered nowhere. FieldEvidence already carries
    /// the raw text, the normalized value, the transformation chain and the validation
    /// outcome, and its DocumentRegion already carries the source address — 'Sheet1'!B7 —
    /// plus the sheet or page it sits on. A reviewer looking at a quantity of 250 could not
    /// find out which cell produced it; now the answer is one click, in the customer's own
    /// coordinates.
    ///
    /// HONEST ABOUT ITS LIMITS. This is populated on the structured spreadsheet path only.
    /// PDF and OCR runs discard word boxes today, so those leads return an empty
    /// <see cref="FieldEvidenceResponse.Fields"/> with <c>SourceMapped=false</c> and a
    /// reason — an empty state that says "source not mapped for this document" rather than
    /// a viewer implying a link that does not exist. No bounding boxes are returned even
    /// where regions exist: on the spreadsheet path the X/Y values are cell indices, not
    /// page geometry, and drawing them over a rendered document would place a highlight
    /// somewhere arbitrary.
    /// </summary>
    [HttpGet("leads/{leadId:long}/fields")]
    [RequireModulePermission("Leads", PermissionAction.View)]
    public async Task<ActionResult<FieldEvidenceResponse>> LeadFields(
        long leadId, CancellationToken cancellationToken)
    {
        var actor = await commercialAccess.ResolveAsync(cancellationToken);
        if (actor is null)
            return Forbid();

        var leadExists = await db.Leads.AsNoTracking()
            .Where(l => l.BusinessUnitId == actor.BusinessUnitId)
            .InCommercialScope(db, actor.BusinessUnitId, actor.AccountScope, DateTime.UtcNow)
            .AnyAsync(l => l.Id == leadId, cancellationToken);
        if (!leadExists) return NotFound();

        // Both anchors are tenant-scoped on every leg: canonical inquiries reached through
        // Lead.Id, canonical line items through LeadItem.Id, and the evidence rows through
        // their own BusinessUnitId. A lead in another tenant resolves to nothing.
        var inquiryIds = await db.Set<CanonicalInquiry>().AsNoTracking()
            .Where(i => i.BusinessUnitId == actor.BusinessUnitId && i.LeadId == leadId)
            .Select(i => i.Id)
            .ToListAsync(cancellationToken);

        var lineItemAnchors = await db.Set<CanonicalLineItem>().AsNoTracking()
            .Where(li => li.BusinessUnitId == actor.BusinessUnitId
                         && li.LeadItemId.HasValue
                         && li.Inquiry.LeadId == leadId)
            .Select(li => new { CanonicalId = li.Id, LeadItemId = li.LeadItemId!.Value, li.LineNumber })
            .ToListAsync(cancellationToken);

        var canonicalLineIds = lineItemAnchors.Select(a => a.CanonicalId).ToList();
        var leadItemByCanonical = lineItemAnchors.ToDictionary(a => a.CanonicalId, a => a);

        var rows = await db.Set<FieldEvidence>().AsNoTracking()
            .Where(f => f.BusinessUnitId == actor.BusinessUnitId
                        && ((f.InquiryId.HasValue && inquiryIds.Contains(f.InquiryId.Value))
                            || (f.LineItemId.HasValue && canonicalLineIds.Contains(f.LineItemId.Value))))
            .Select(f => new
            {
                f.Id,
                f.InquiryId,
                f.LineItemId,
                f.FieldName,
                f.RawValue,
                f.NormalizedValue,
                f.ValueKind,
                f.ValidationStatus,
                f.Confidence,
                f.TransformationsJson,
                f.Extractor,
                f.CreatedOn,
                f.Region.SourceAddress,
                f.Region.RowNumber,
                f.Region.ColumnNumber,
                SheetName = f.Region.Page.SheetName,
                PageNumber = f.Region.Page.PageNumber
            })
            .ToListAsync(cancellationToken);

        var fields = rows
            .Select(f => new FieldEvidenceItem(
                f.Id,
                f.LineItemId.HasValue && leadItemByCanonical.TryGetValue(f.LineItemId.Value, out var anchor)
                    ? anchor.LeadItemId
                    : null,
                f.LineItemId.HasValue && leadItemByCanonical.TryGetValue(f.LineItemId.Value, out var line)
                    ? line.LineNumber
                    : null,
                f.InquiryId.HasValue ? "header" : "line",
                f.FieldName,
                f.RawValue,
                f.NormalizedValue,
                f.ValueKind.ToString(),
                f.ValidationStatus.ToString(),
                f.Confidence,
                f.TransformationsJson,
                f.Extractor,
                f.SourceAddress,
                f.SheetName,
                f.PageNumber,
                f.RowNumber,
                f.ColumnNumber,
                f.CreatedOn))
            .OrderBy(f => f.LineNumber ?? 0)
            .ThenBy(f => f.FieldName, StringComparer.Ordinal)
            .ToList();

        // The document's own words outside the table. Retained as a Text region by
        // StructuredEvidenceLedgerPersister and returned here, because a requirement stated
        // in prose — warranty, validity, country of origin, Incoterms, "as per attached
        // specification" — changes what may be quoted just as much as a line does, and it
        // used to reach the reviewer only if they opened the source file by hand.
        var corpusIds = await db.Set<CanonicalInquiry>().AsNoTracking()
            .Where(i => i.BusinessUnitId == actor.BusinessUnitId && i.LeadId == leadId)
            .Select(i => i.CorpusId)
            .Distinct()
            .ToListAsync(cancellationToken);

        var narrative = corpusIds.Count == 0 ? null : await db.Set<DocumentRegion>().AsNoTracking()
            .Where(r => r.BusinessUnitId == actor.BusinessUnitId
                        && r.RegionType == DocumentRegionType.Text
                        && r.Text != null
                        && corpusIds.Contains(r.Page.Document.CorpusId))
            .OrderBy(r => r.Id)
            .Select(r => r.Text)
            .FirstOrDefaultAsync(cancellationToken);

        var addressed = fields.Count(f => !string.IsNullOrWhiteSpace(f.SourceAddress));
        return Ok(new FieldEvidenceResponse(
            leadId,
            fields.Count > 0,
            fields.Count,
            addressed,
            fields,
            fields.Count == 0
                ? "Source not mapped for this document. Per-cell provenance is recorded on the "
                  + "structured spreadsheet path; PDF and OCR extractions do not retain word "
                  + "positions, so no field can be traced to a location in this file."
                : null,
            narrative));
    }

}

/// <param name="LeadItemId">The reviewer-visible line this value belongs to; null for lead-level fields.</param>
/// <param name="SourceAddress">The customer's own coordinate, e.g. 'Sheet1'!B7. Null when the path did not record one.</param>
/// <param name="TransformationsJson">Ordered record of what was done to the raw text to reach the normalized value.</param>
/// <param name="Confidence">
/// The certainty the extractor recorded for THIS value, 0..1, exactly as stored in
/// <c>FieldEvidence.Confidence</c>.
///
/// <para>
/// It was captured and then omitted from this DTO, so a reviewer could see where a value
/// came from and how it was transformed but not whether it was read with certainty or
/// merely salvaged. That is the difference that decides what a human checks first, and it
/// was the one thing withheld.
/// </para>
/// <para>
/// It is NOT a measured accuracy and must never be rendered as one. On the deterministic
/// path it is a parse verdict from a closed set — 1.0 for a value parsed exactly, 0.2 for
/// source text that could not be interpreted, 0 for a field the document does not state —
/// which is why the review screen renders its MEANING and not a percentage. Read it with
/// <c>ValueKind</c> and <c>RawValue</c>: confidence 0 with raw text present would be a
/// contradiction, and there is no path that produces one.
/// </para>
/// <para>
/// The model path writes no <c>FieldEvidence</c> at all, so nothing here is invented for
/// it — see <c>AiPromptVersions.StructuredRfqExtraction</c> v1→v2, which removed the
/// per-field confidences from the prompt because they were self-reported, discarded on
/// persistence, and cost half the output budget.
/// </para>
/// </param>
public sealed record FieldEvidenceItem(
    long EvidenceId,
    long? LeadItemId,
    int? LineNumber,
    string Scope,
    string FieldName,
    string? RawValue,
    string? NormalizedValue,
    string ValueKind,
    string ValidationStatus,
    decimal Confidence,
    string TransformationsJson,
    string Extractor,
    string? SourceAddress,
    string? SheetName,
    int PageNumber,
    int? RowNumber,
    int? ColumnNumber,
    DateTimeOffset CapturedOn);

/// <param name="SourceMapped">False when this document produced no per-field provenance at all.</param>
/// <param name="FieldsWithSourceAddress">Of the fields returned, how many carry a usable address.</param>
/// <param name="UnmappedReason">Plain-language empty state; null when fields exist.</param>
/// <param name="DocumentNarrative">
/// The document's own prose OUTSIDE the extracted table, verbatim, or null when the
/// document had none or was read on a path that retains none.
///
/// <para>
/// It is deliberately unparsed. Every RFQ in the pilot corpus ends with instructions naming
/// the required warranty, validity, country of origin, Incoterms and submission method, and
/// none of it reached the lead for any of the 120 documents. Turning that into fields is a
/// separate decision with real risk — "as per attached specification" is not a value —
/// whereas putting it in front of the reviewer beside the lines is most of the value and
/// carries none.
/// </para>
/// </param>
public sealed record FieldEvidenceResponse(
    long LeadId,
    bool SourceMapped,
    int FieldCount,
    int FieldsWithSourceAddress,
    IReadOnlyList<FieldEvidenceItem> Fields,
    string? UnmappedReason,
    string? DocumentNarrative = null);
