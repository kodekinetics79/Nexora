using System.Security.Claims;
using ERP_RFQ_Automation.Authorization;
using ERP_RFQ_Automation.Extraction;
using ERP_RFQ_Automation.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

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

    private long TenantId() => long.TryParse(
        User.FindFirstValue("businessUnitId") ?? User.FindFirstValue("business_unit_id"), out var id)
        ? id
        : 0;
}
