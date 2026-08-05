using System.Security.Claims;
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
public sealed class ProcessingEvidenceController(ErpRfqAutomationContext db) : ControllerBase
{
    [HttpGet("leads/{leadId:long}")]
    [RequireModulePermission("Leads", PermissionAction.View)]
    public async Task<ActionResult<ProcessingEvidenceResponse>> Lead(
        long leadId, CancellationToken cancellationToken)
    {
        var businessUnitId = TenantId();
        if (businessUnitId <= 0)
            return Forbid();

        var evidence = await ProcessingEvidenceQuery.ReadLeadAsync(
            db, businessUnitId, leadId, cancellationToken);
        return evidence is null ? NotFound() : Ok(evidence);
    }

    [HttpGet("rfqs/{rfqId:long}")]
    [RequireModulePermission("RFQ Management", PermissionAction.View)]
    public async Task<ActionResult<ProcessingEvidenceResponse>> Rfq(
        long rfqId, CancellationToken cancellationToken)
    {
        var businessUnitId = TenantId();
        if (businessUnitId <= 0)
            return Forbid();

        var evidence = await ProcessingEvidenceQuery.ReadRfqAsync(
            db, businessUnitId, rfqId, cancellationToken);
        return evidence is null ? NotFound() : Ok(evidence);
    }

    [HttpGet("supplier-quotes/{supplierQuoteId:long}")]
    [RequireModulePermission("Supplier History", PermissionAction.View)]
    public async Task<ActionResult<ProcessingEvidenceResponse>> SupplierQuote(
        long supplierQuoteId, CancellationToken cancellationToken)
    {
        var businessUnitId = TenantId();
        if (businessUnitId <= 0)
            return Forbid();

        var evidence = await ProcessingEvidenceQuery.ReadSupplierQuoteAsync(
            db, businessUnitId, supplierQuoteId, cancellationToken);
        return evidence is null ? NotFound() : Ok(evidence);
    }

    [HttpGet("client-purchase-orders/{clientPurchaseOrderId:long}")]
    [RequireModulePermission("Customer Awards", PermissionAction.View)]
    public async Task<ActionResult<ProcessingEvidenceResponse>> ClientPurchaseOrder(
        long clientPurchaseOrderId, CancellationToken cancellationToken)
    {
        var businessUnitId = TenantId();
        if (businessUnitId <= 0)
            return Forbid();

        var evidence = await ProcessingEvidenceQuery.ReadClientPurchaseOrderAsync(
            db, businessUnitId, clientPurchaseOrderId, cancellationToken);
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
        var businessUnitId = TenantId();
        if (businessUnitId <= 0)
            return Forbid();

        var leadExists = await db.Leads.AsNoTracking()
            .AnyAsync(l => l.Id == leadId && l.BusinessUnitId == businessUnitId, cancellationToken);
        if (!leadExists) return NotFound();

        // Both anchors are tenant-scoped on every leg: canonical inquiries reached through
        // Lead.Id, canonical line items through LeadItem.Id, and the evidence rows through
        // their own BusinessUnitId. A lead in another tenant resolves to nothing.
        var inquiryIds = await db.Set<CanonicalInquiry>().AsNoTracking()
            .Where(i => i.BusinessUnitId == businessUnitId && i.LeadId == leadId)
            .Select(i => i.Id)
            .ToListAsync(cancellationToken);

        var lineItemAnchors = await db.Set<CanonicalLineItem>().AsNoTracking()
            .Where(li => li.BusinessUnitId == businessUnitId
                         && li.LeadItemId.HasValue
                         && li.Inquiry.LeadId == leadId)
            .Select(li => new { CanonicalId = li.Id, LeadItemId = li.LeadItemId!.Value, li.LineNumber })
            .ToListAsync(cancellationToken);

        var canonicalLineIds = lineItemAnchors.Select(a => a.CanonicalId).ToList();
        var leadItemByCanonical = lineItemAnchors.ToDictionary(a => a.CanonicalId, a => a);

        var rows = await db.Set<FieldEvidence>().AsNoTracking()
            .Where(f => f.BusinessUnitId == businessUnitId
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
                : null));
    }

    private long TenantId() => long.TryParse(
        User.FindFirstValue("businessUnitId") ?? User.FindFirstValue("business_unit_id"), out var id)
        ? id
        : 0;
}

/// <param name="LeadItemId">The reviewer-visible line this value belongs to; null for lead-level fields.</param>
/// <param name="SourceAddress">The customer's own coordinate, e.g. 'Sheet1'!B7. Null when the path did not record one.</param>
/// <param name="TransformationsJson">Ordered record of what was done to the raw text to reach the normalized value.</param>
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
public sealed record FieldEvidenceResponse(
    long LeadId,
    bool SourceMapped,
    int FieldCount,
    int FieldsWithSourceAddress,
    IReadOnlyList<FieldEvidenceItem> Fields,
    string? UnmappedReason);
