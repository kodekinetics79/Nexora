using System.Security.Claims;
using ERP_RFQ_Automation.Authorization;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Procurement.SupplierEmail;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ERP_RFQ_Automation.Controllers;

/// <summary>
/// The wording of supplier request emails. The company standard is the tenant admin's (module
/// "Quote Configuration"); a sales person's own default message and signature are theirs alone,
/// resolved from the token, never from the request.
/// </summary>
[ApiController]
[Authorize]
[Route("api/supplier-email-settings")]
public sealed class SupplierEmailSettingsController(SupplierEmailSettingsService settings, ErpRfqAutomationContext db) : ControllerBase
{
    [HttpGet("company")]
    [RequireModulePermission("Quote Configuration", PermissionAction.View)]
    public Task<IActionResult> GetCompany(CancellationToken ct) =>
        Execute(async () => Ok(await settings.GetCompanyAsync(TenantId(), ct)));

    [HttpPut("company")]
    [RequireModulePermission("Quote Configuration", PermissionAction.Edit)]
    public Task<IActionResult> SaveCompany([FromBody] SaveCompanySupplierEmailCommand command, CancellationToken ct) =>
        Execute(async () => Ok(await settings.SaveCompanyAsync(TenantId(), command, Actor(), ct)));

    [HttpGet("mine")]
    public Task<IActionResult> GetMine(CancellationToken ct) =>
        Execute(async () => Ok(await settings.GetMineAsync(TenantId(), UserId(), ct)));

    [HttpPut("mine")]
    public Task<IActionResult> SaveMine([FromBody] SaveMySupplierEmailCommand command, CancellationToken ct) =>
        Execute(async () => Ok(await settings.SaveMineAsync(TenantId(), UserId(), command, Actor(), ct)));

    [HttpGet("effective")]
    public Task<IActionResult> GetEffective(CancellationToken ct) =>
        Execute(async () => Ok(await settings.GetEffectiveAsync(TenantId(), UserId(), ct)));

    [HttpGet("send-from")]
    [RequireModulePermission("Supplier History", PermissionAction.View)]
    public Task<IActionResult> GetSendFrom(CancellationToken ct) =>
        Execute(async () => Ok(await SupplierEmailSettingsService.GetSendFromAsync(db, TenantId(), ct)));

    private async Task<IActionResult> Execute(Func<Task<IActionResult>> action)
    {
        try { return await action(); }
        catch (SupplierEmailValidationException ex)
        {
            return BadRequest(new ProblemDetails { Status = StatusCodes.Status400BadRequest, Title = "Supplier email not saved", Detail = ex.Message });
        }
        catch (UnauthorizedAccessException)
        {
            return Unauthorized();
        }
    }

    private long TenantId() =>
        long.TryParse(User.FindFirst("businessUnitId")?.Value, out var id) && id > 0
            ? id
            : throw new UnauthorizedAccessException("Business Unit ID is required.");

    private long UserId() =>
        long.TryParse(User.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? User.FindFirst("sub")?.Value, out var id) && id > 0
            ? id
            : throw new UnauthorizedAccessException("An authenticated user is required.");

    private string Actor() =>
        User.FindFirst(ClaimTypes.Email)?.Value ?? User.FindFirst("email")?.Value ??
        User.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? "authenticated-user";
}
