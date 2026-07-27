using System.Text.Json;
using ERP_RFQ_Automation.Authorization;
using ERP_RFQ_Automation.Procurement;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ERP_RFQ_Automation.Controllers;

[ApiController]
[Authorize]
[Route("api/procurement-integrations")]
public sealed class ProcurementIntegrationController(IProcurementIntegrationService service) : ControllerBase
{
    [HttpGet("status")]
    [RequireModulePermission("Orders", PermissionAction.View)]
    public async Task<IActionResult> Status()
        => Ok(await service.GetStatusAsync(TenantId(), HttpContext.RequestAborted));

    [HttpPost("callbacks")]
    [RequireModulePermission("Orders", PermissionAction.Edit)]
    public async Task<IActionResult> Callback()
    {
        try
        {
            Request.EnableBuffering();
            using var reader = new StreamReader(Request.Body, leaveOpen: true);
            var rawBody = await reader.ReadToEndAsync(HttpContext.RequestAborted);
            Request.Body.Position = 0;
            var command = JsonSerializer.Deserialize<ProcurementStatusCallbackCommand>(rawBody,
                new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            }) ?? throw new ArgumentException("A callback payload is required.");
            var result = await service.ApplyCallbackAsync(TenantId(), Header("X-Nexora-Timestamp"),
                Header("X-Nexora-Signature"), CorrelationId(), rawBody, command,
                HttpContext.RequestAborted);
            return result.Applied ? Ok(result) : Conflict(new { result, message = "Callback requires review." });
        }
        catch (ProcurementIntegrationAuthenticationException exception)
        {
            return Unauthorized(new ProblemDetails { Status = 401, Title = "Callback authentication failed",
                Detail = exception.Message });
        }
        catch (KeyNotFoundException exception)
        {
            return NotFound(new ProblemDetails { Status = 404, Title = "Handoff not found", Detail = exception.Message });
        }
        catch (ArgumentException exception)
        {
            return BadRequest(new ProblemDetails { Status = 400, Title = "Invalid callback", Detail = exception.Message });
        }
        catch (InvalidOperationException exception)
        {
            return Conflict(new ProblemDetails { Status = 409, Title = "Callback conflict", Detail = exception.Message });
        }
    }

    private long TenantId() => long.TryParse(User.FindFirst("businessUnitId")?.Value, out var value) && value > 0
        ? value : throw new ArgumentException("A valid tenant claim is required.");
    private string Header(string name) => Request.Headers.TryGetValue(name, out var value)
        && !string.IsNullOrWhiteSpace(value) ? value.ToString().Trim()
        : throw new ProcurementIntegrationAuthenticationException($"{name} header is required.");
    private string CorrelationId() => Request.Headers.TryGetValue("X-Correlation-ID", out var value)
        && !string.IsNullOrWhiteSpace(value) ? value.ToString().Trim() : HttpContext.TraceIdentifier;
}
