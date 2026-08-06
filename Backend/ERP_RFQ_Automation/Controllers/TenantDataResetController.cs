using ERP_RFQ_Automation.Authorization;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Platform.Testing;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ERP_RFQ_Automation.Controllers;

public sealed record TenantResetExecuteRequest(string Confirmation);

/// <summary>
/// Erases this tenant's transactional data so the same source emails can be ingested again from a
/// clean slate — the loop a pilot needs when a defect is found, fixed, and the original leads have
/// to be re-run against the corrected code.
///
/// <para><b>Every action here is refused in Production</b>, whatever the configuration says, and
/// outside Production still requires <c>Testing:AllowTenantDataReset=true</c>. The environment
/// check comes first deliberately: a mistyped environment variable must not be sufficient to erase
/// a customer's data.</para>
///
/// <para>The tenant is always taken from the caller's own claim. There is no parameter for it, so
/// there is no shape of request that erases a tenant other than the caller's own.</para>
/// </summary>
[Route("api/[controller]")]
[ApiController]
[Authorize]
public sealed class TenantDataResetController(
    TenantDataReset reset,
    IIamAuditWriter audit,
    ErpRfqAutomationContext context) : ControllerBase
{
    /// <summary>Whether the capability is available here, so the UI can hide it entirely rather
    /// than offer a button that always fails.</summary>
    [HttpGet("availability")]
    [RequireModulePermission("Business Units", PermissionAction.View)]
    public ActionResult<object> Availability() => Ok(new
    {
        enabled = reset.IsEnabled,
        configurationPath = TenantDataReset.EnabledConfigurationPath,
        detail = reset.IsEnabled
            ? "Transactional data can be erased in this environment."
            : "Data reset is not available here. It is refused in Production, and elsewhere must " +
              $"be enabled with {TenantDataReset.EnabledConfigurationPath}=true."
    });

    /// <summary>
    /// Exactly what would be deleted, table by table, with row counts — and what would survive.
    /// Always run before the reset: it is the only place the blast radius is visible, and it also
    /// returns the confirmation string the reset requires.
    /// </summary>
    [HttpGet("preview")]
    [RequireModulePermission("Business Units", PermissionAction.Delete)]
    public async Task<ActionResult<TenantResetPreview>> Preview()
    {
        if (!TryTenant(out var tenant)) return Forbid();

        try
        {
            return Ok(await reset.PreviewAsync(tenant, HttpContext.RequestAborted));
        }
        catch (TenantResetRefusedException exception)
        {
            return StatusCode(StatusCodes.Status403Forbidden, new { message = exception.Message });
        }
        catch (KeyNotFoundException exception)
        {
            return NotFound(new { message = exception.Message });
        }
    }

    [HttpPost]
    [RequireModulePermission("Business Units", PermissionAction.Delete)]
    public async Task<ActionResult<TenantResetOutcome>> Execute([FromBody] TenantResetExecuteRequest request)
    {
        if (!TryTenant(out var tenant)) return Forbid();
        if (request is null || string.IsNullOrWhiteSpace(request.Confirmation))
            return BadRequest(new { message = "Type the business unit's name to confirm the reset." });

        try
        {
            var outcome = await reset.ExecuteAsync(tenant, request.Confirmation, HttpContext.RequestAborted);

            // Written AFTER the reset and through the request-scoped context, so the audit row
            // lands in a table the reset has already cleared — the record of the erasure survives
            // the erasure. Auditing it beforehand would delete the evidence of its own action.
            await audit.WriteAsync(User, new IamAuditEntry(
                IamAuditActions.TenantDataReset, IamAuditTargets.BusinessUnit, tenant,
                outcome.Summary,
                After: new { outcome.RowsDeleted, Tables = outcome.Deleted.Count }));
            await context.SaveChangesAsync(HttpContext.RequestAborted);

            return Ok(outcome);
        }
        catch (TenantResetRefusedException exception)
        {
            return StatusCode(StatusCodes.Status403Forbidden, new { message = exception.Message });
        }
        catch (KeyNotFoundException exception)
        {
            return NotFound(new { message = exception.Message });
        }
    }

    private bool TryTenant(out long tenant)
    {
        tenant = 0;
        return long.TryParse(User.FindFirst("businessUnitId")?.Value, out tenant) && tenant > 0;
    }
}
