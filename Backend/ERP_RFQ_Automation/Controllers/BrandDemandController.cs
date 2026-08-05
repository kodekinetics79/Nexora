using System;
using System.Threading;
using System.Threading.Tasks;
using ERP_RFQ_Automation.Authorization;
using ERP_RFQ_Automation.DTOs.Dashboard;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Repositories;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ERP_RFQ_Automation.Controllers;

/// <summary>
/// Brand demand concentration across extracted enquiry lines.
///
/// Reads the tenant's own leads only; the business unit comes from the authenticated
/// claim and never from the route or query string, so a caller cannot ask for another
/// tenant's purchasing profile.
/// </summary>
[ApiController]
[Authorize]
[Route("api/brand-demand")]
public sealed class BrandDemandController(ErpRfqAutomationContext db) : ControllerBase
{
    [HttpGet]
    [RequireModulePermission("Leads", PermissionAction.View)]
    public async Task<ActionResult<BrandDemandDTO>> Get(
        [FromQuery] DateTime? from,
        [FromQuery] DateTime? to,
        [FromQuery] int topN = 25,
        CancellationToken cancellationToken = default)
    {
        var businessUnitId = TenantId();
        if (businessUnitId <= 0) return Forbid();
        if (from.HasValue && to.HasValue && from.Value >= to.Value)
            return BadRequest("The 'from' value must be earlier than 'to'.");

        var repository = new BrandDemandRepository(db);
        return Ok(await repository.GetAsync(
            businessUnitId, Normalize(from), Normalize(to), topN, cancellationToken));
    }

    private long TenantId() => long.TryParse(
        User.FindFirst("businessUnitId")?.Value, out var id) ? id : 0;

    private static DateTime? Normalize(DateTime? value) => value?.Kind switch
    {
        null => null,
        DateTimeKind.Utc => value,
        DateTimeKind.Local => value.Value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(value!.Value, DateTimeKind.Utc)
    };
}
