using System.Security.Claims;
using ERP_RFQ_Automation.Authorization;
using ERP_RFQ_Automation.SupplierGovernance;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ERP_RFQ_Automation.Controllers;

[ApiController]
[Authorize]
[Route("api/suppliers")]
public sealed class SupplierGovernanceController(SupplierGovernanceService service) : ControllerBase
{
    [HttpPost("{supplierId:long}/governance")]
    [RequireManagerRole]
    [RequireModulePermission("Suppliers", PermissionAction.Edit)]
    public async Task<IActionResult> Govern(long supplierId,
        [FromBody] GovernSupplierRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await service.GovernAsync(new GovernSupplierCommand(
                TenantId(), supplierId, request.GovernanceStatus, request.VerificationStatus,
                request.ComplianceStatus, request.RiskStatus, request.ReadinessStatus,
                request.ExpectedConcurrencyToken, request.Reason, Actor(),
                RequiredHeader("Idempotency-Key"), RequiredHeader("X-Correlation-ID")), cancellationToken);
            return Ok(result);
        }
        catch (SupplierGovernanceConflictException exception)
        {
            return Conflict(Problem(exception.Message, StatusCodes.Status409Conflict));
        }
        catch (SupplierGovernanceNotFoundException)
        {
            return NotFound(Problem("The Supplier was not found.", StatusCodes.Status404NotFound));
        }
        catch (ArgumentException exception)
        {
            return BadRequest(Problem(exception.Message, StatusCodes.Status400BadRequest));
        }
        catch (UnauthorizedAccessException)
        {
            return Unauthorized(Problem("A valid authenticated tenant and actor are required.", StatusCodes.Status401Unauthorized));
        }
    }

    private long TenantId() => long.TryParse(User.FindFirst("businessUnitId")?.Value, out var value)
        && value > 0 ? value : throw new UnauthorizedAccessException("A valid tenant claim is required.");

    private string Actor() => User.FindFirstValue(ClaimTypes.Email)
        ?? User.FindFirstValue(ClaimTypes.NameIdentifier)
        ?? User.Identity?.Name
        ?? throw new UnauthorizedAccessException("An authenticated actor claim is required.");

    private string RequiredHeader(string name)
    {
        var value = Request.Headers[name].ToString().Trim();
        if (string.IsNullOrWhiteSpace(value) || value.Length > 160)
            throw new ArgumentException($"{name} is required and must not exceed 160 characters.");
        return value;
    }

    private static ProblemDetails Problem(string detail, int status) => new()
        { Status = status, Title = "Supplier governance", Detail = detail };
}

public sealed record GovernSupplierRequest(
    string GovernanceStatus,
    string VerificationStatus,
    string ComplianceStatus,
    string RiskStatus,
    string ReadinessStatus,
    Guid ExpectedConcurrencyToken,
    string Reason);
