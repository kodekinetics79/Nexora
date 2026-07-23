using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using ERP_RFQ_Automation.Authorization;
using ERP_RFQ_Automation.CommercialFinance;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace ERP_RFQ_Automation.Controllers;

[ApiController]
[Route("api/receivables-operations")]
[Authorize]
public sealed class ReceivablesOperationsController(
    IReceivablesOperationsService service,
    ILogger<ReceivablesOperationsController> logger,
    IConfiguration configuration) : ControllerBase
{
    [HttpPost("contacts")]
    [RequireModulePermission("Collection Controls", PermissionAction.Create)]
    public Task<IActionResult> CreateContact([FromBody] CreateFinanceCommunicationContactRequest request)
        => ExecuteAsync(async () =>
        {
            var result = await service.CreateContactAsync(TenantId(), IdempotencyKey(), request, Actor());
            return Created($"/api/receivables-operations/contacts/{result.Id}", result);
        });

    [HttpPost("contacts/{contactId:long}/deactivate")]
    [RequireModulePermission("Collection Controls", PermissionAction.Edit)]
    public Task<IActionResult> DeactivateContact(
        long contactId, [FromBody] DeactivateFinanceCommunicationContactRequest request)
        => ExecuteAsync(async () => Ok(await service.DeactivateContactAsync(TenantId(), contactId, request, Actor())));

    [HttpGet("contacts")]
    [RequireModulePermission("Collection Controls", PermissionAction.View)]
    public Task<IActionResult> GetContacts([FromQuery] long? customerId, [FromQuery] string? purpose)
        => ExecuteAsync(async () => Ok(await service.GetContactsAsync(TenantId(), customerId, purpose)));

    [HttpPost("statements")]
    [RequireModulePermission("Customer Statements", PermissionAction.Create)]
    public Task<IActionResult> CreateStatement([FromBody] CreateCustomerStatementRequest request)
        => ExecuteAsync(async () =>
        {
            var result = await service.CreateStatementAsync(TenantId(), IdempotencyKey(), request, Actor());
            return Created($"/api/receivables-operations/statements/{result.Id}", result);
        });

    [HttpPost("statements/{statementId:long}/finalize")]
    [RequireModulePermission("Customer Statements", PermissionAction.Edit)]
    public Task<IActionResult> FinalizeStatement(long statementId, [FromBody] StatementActionRequest request)
        => ExecuteAsync(async () => Ok(await service.FinalizeStatementAsync(TenantId(), statementId, request, Actor())));

    [HttpPost("statements/{statementId:long}/cancel")]
    [RequireModulePermission("Customer Statements", PermissionAction.Edit)]
    public Task<IActionResult> CancelStatement(long statementId, [FromBody] StatementActionRequest request)
        => ExecuteAsync(async () => Ok(await service.CancelStatementAsync(TenantId(), statementId, request, Actor())));

    [HttpGet("statements/{statementId:long}")]
    [RequireModulePermission("Customer Statements", PermissionAction.View)]
    public Task<IActionResult> GetStatement(long statementId)
        => ExecuteAsync(async () =>
        {
            var result = await service.GetStatementAsync(TenantId(), statementId);
            return result is null
                ? NotFound(Problem(404, "Receivables record not found", "The requested statement was not found."))
                : Ok(result);
        });

    [HttpGet("statements/{statementId:long}/artifact")]
    [RequireModulePermission("Customer Statements", PermissionAction.View)]
    public Task<IActionResult> GetStatementArtifact(long statementId)
        => ExecuteAsync(async () =>
        {
            var artifact = await service.GetStatementArtifactAsync(TenantId(), statementId);
            if (artifact is null)
                return NotFound(Problem(404, "Receivables record not found", "The requested statement was not found."));
            Response.Headers.ContentSecurityPolicy = "sandbox; default-src 'none'; style-src 'unsafe-inline'";
            Response.Headers.ContentDisposition = $"attachment; filename=statement-{artifact.StatementId}.html";
            return Content(artifact.Content, artifact.MediaType);
        });

    [HttpGet("statements")]
    [RequireModulePermission("Customer Statements", PermissionAction.View)]
    public Task<IActionResult> GetStatements([FromQuery] long? customerId, [FromQuery] string? status)
        => ExecuteAsync(async () => Ok(await service.GetStatementsAsync(TenantId(), customerId, status)));

    [HttpPost("policies")]
    [RequireModulePermission("Dunning Policies", PermissionAction.Create)]
    public Task<IActionResult> CreatePolicy([FromBody] CreateDunningPolicyRequest request)
        => ExecuteAsync(async () =>
        {
            var result = await service.CreatePolicyAsync(TenantId(), IdempotencyKey(), request, Actor());
            return Created($"/api/receivables-operations/policies/{result.Id}", result);
        });

    [HttpPost("policies/{policyId:long}/approve")]
    [RequireModulePermission("Dunning Policies", PermissionAction.Edit)]
    public Task<IActionResult> ApprovePolicy(long policyId, [FromBody] DunningPolicyActionRequest request)
        => ExecuteAsync(async () => Ok(await service.ApprovePolicyAsync(TenantId(), policyId, request, Actor())));

    [HttpPost("policies/{policyId:long}/activate")]
    [RequireModulePermission("Dunning Policies", PermissionAction.Edit)]
    public Task<IActionResult> ActivatePolicy(long policyId, [FromBody] DunningPolicyActionRequest request)
        => ExecuteAsync(async () => Ok(await service.ActivatePolicyAsync(TenantId(), policyId, request, Actor())));

    [HttpPost("policies/{policyId:long}/retire")]
    [RequireModulePermission("Dunning Policies", PermissionAction.Edit)]
    public Task<IActionResult> RetirePolicy(long policyId, [FromBody] DunningPolicyActionRequest request)
        => ExecuteAsync(async () => Ok(await service.RetirePolicyAsync(TenantId(), policyId, request, Actor())));

    [HttpGet("policies")]
    [RequireModulePermission("Dunning Policies", PermissionAction.View)]
    public Task<IActionResult> GetPolicies([FromQuery] string? status)
        => ExecuteAsync(async () => Ok(await service.GetPoliciesAsync(TenantId(), status)));

    [HttpPut("collection-profiles")]
    [RequireModulePermission("Dunning Policies", PermissionAction.Edit)]
    public Task<IActionResult> UpsertCollectionProfile([FromBody] UpsertCustomerCollectionProfileRequest request)
        => ExecuteAsync(async () => Ok(await service.UpsertCollectionProfileAsync(TenantId(), request, Actor())));

    [HttpGet("collection-profiles")]
    [RequireModulePermission("Dunning Policies", PermissionAction.View)]
    public Task<IActionResult> GetCollectionProfiles([FromQuery] long? customerId)
        => ExecuteAsync(async () => Ok(await service.GetCollectionProfilesAsync(TenantId(), customerId)));

    [HttpPost("controls")]
    [RequireModulePermission("Collection Controls", PermissionAction.Create)]
    public Task<IActionResult> CreateControl([FromBody] CreateCollectionControlRequest request)
        => ExecuteAsync(async () =>
        {
            var result = await service.CreateControlAsync(TenantId(), IdempotencyKey(), request, Actor());
            return Created($"/api/receivables-operations/controls/{result.Id}", result);
        });

    [HttpPost("controls/{controlId:long}/resolve")]
    [RequireModulePermission("Collection Controls", PermissionAction.Edit)]
    public Task<IActionResult> ResolveControl(long controlId, [FromBody] ResolveCollectionControlRequest request)
        => ExecuteAsync(async () => Ok(await service.ResolveControlAsync(TenantId(), controlId, request, Actor())));

    [HttpGet("controls")]
    [RequireModulePermission("Collection Controls", PermissionAction.View)]
    public Task<IActionResult> GetControls(
        [FromQuery] long? customerId, [FromQuery] string? status)
        => ExecuteAsync(async () => Ok(await service.GetControlsAsync(TenantId(), customerId, status)));

    [HttpPost("cases")]
    [RequireModulePermission("Dunning Cases", PermissionAction.Create)]
    public Task<IActionResult> OpenCase([FromBody] OpenDunningCaseRequest request)
        => ExecuteAsync(async () =>
        {
            var result = await service.OpenCaseAsync(TenantId(), IdempotencyKey(), request, Actor());
            return Created($"/api/receivables-operations/cases/{result.Id}", result);
        });

    [HttpPost("cases/{caseId:long}/transitions/{action}")]
    [RequireModulePermission("Dunning Cases", PermissionAction.Edit)]
    public Task<IActionResult> TransitionCase(
        long caseId, string action, [FromBody] DunningCaseActionRequest request)
        => ExecuteAsync(async () => Ok(await service.TransitionCaseAsync(TenantId(), caseId, action, request, Actor())));

    [HttpPost("cases/{caseId:long}/assign")]
    [RequireModulePermission("Dunning Cases", PermissionAction.Edit)]
    public Task<IActionResult> AssignCase(long caseId, [FromBody] AssignDunningCaseRequest request)
        => ExecuteAsync(async () => Ok(await service.AssignCaseAsync(TenantId(), caseId, request, Actor())));

    [HttpPost("cases/{caseId:long}/promises")]
    [RequireModulePermission("Dunning Cases", PermissionAction.Create)]
    public Task<IActionResult> CreatePromise(long caseId, [FromBody] CreatePromiseToPayRequest request)
        => ExecuteAsync(async () =>
        {
            var result = await service.CreatePromiseAsync(TenantId(), caseId, IdempotencyKey(), request, Actor());
            return Created($"/api/receivables-operations/promises/{result.Id}", result);
        });

    [HttpPost("promises/{promiseId:long}/close")]
    [RequireModulePermission("Dunning Cases", PermissionAction.Edit)]
    public Task<IActionResult> ClosePromise(long promiseId, [FromBody] ClosePromiseToPayRequest request)
        => ExecuteAsync(async () => Ok(await service.ClosePromiseAsync(TenantId(), promiseId, request, Actor())));

    [HttpGet("cases")]
    [RequireModulePermission("Dunning Cases", PermissionAction.View)]
    public Task<IActionResult> GetCases([FromQuery] long? customerId, [FromQuery] string? status)
        => ExecuteAsync(async () => Ok(await service.GetCasesAsync(TenantId(), customerId, status)));

    [HttpPost("notices")]
    [RequireModulePermission("Dunning Notices", PermissionAction.Create)]
    public Task<IActionResult> CreateNotice([FromBody] CreateDunningNoticeRequest request)
        => ExecuteAsync(async () =>
        {
            var result = await service.CreateNoticeAsync(TenantId(), IdempotencyKey(), request, Actor());
            return Created($"/api/receivables-operations/notices/{result.Id}", result);
        });

    [HttpPost("notices/{noticeId:long}/transitions/{action}")]
    [RequireModulePermission("Dunning Notices", PermissionAction.Edit)]
    public Task<IActionResult> TransitionNotice(
        long noticeId, string action, [FromBody] DunningNoticeActionRequest request)
        => ExecuteAsync(async () => Ok(await service.TransitionNoticeAsync(TenantId(), noticeId, action, request, Actor())));

    [HttpPost("notices/{noticeId:long}/delivery-results/{delivered:bool}")]
    [RequireModulePermission("Dunning Notices", PermissionAction.Edit)]
    public Task<IActionResult> RecordDeliveryResult(
        long noticeId, bool delivered, [FromBody] DunningDeliveryResultRequest request)
        => ExecuteAsync(async () =>
        {
            var tenantId = TenantId();
            var signature = ValidateProviderSignature(tenantId, noticeId, delivered, request);
            return Ok(await service.RecordDeliveryResultAsync(
                tenantId, noticeId, delivered, request with { ProviderSignature = signature }, Actor()));
        });

    [HttpGet("notices")]
    [RequireModulePermission("Dunning Notices", PermissionAction.View)]
    public Task<IActionResult> GetNotices([FromQuery] long? caseId, [FromQuery] string? status)
        => ExecuteAsync(async () => Ok(await service.GetNoticesAsync(TenantId(), caseId, status)));

    [HttpPost("runs")]
    [RequireModulePermission("Dunning Notices", PermissionAction.Create)]
    public Task<IActionResult> RunDunning([FromBody] CreateDunningRunRequest request)
        => ExecuteAsync(async () =>
        {
            var result = await service.RunDunningAsync(TenantId(), IdempotencyKey(), request, Actor());
            return Created($"/api/receivables-operations/runs/{result.Id}", result);
        });

    [HttpGet("runs")]
    [RequireModulePermission("Dunning Notices", PermissionAction.View)]
    public Task<IActionResult> GetRuns()
        => ExecuteAsync(async () => Ok(await service.GetRunsAsync(TenantId())));

    private async Task<IActionResult> ExecuteAsync(Func<Task<IActionResult>> action)
    {
        try
        {
            return await action();
        }
        catch (FinanceConflictException exception)
        {
            return Conflict(Problem(409, "Receivables operation conflict", exception.Message));
        }
        catch (DbUpdateConcurrencyException)
        {
            return Conflict(Problem(409, "Receivables operation conflict",
                "The record changed concurrently. Reload and try again."));
        }
        catch (PostgresException exception) when (exception.SqlState is PostgresErrorCodes.UniqueViolation
            or PostgresErrorCodes.CheckViolation or PostgresErrorCodes.SerializationFailure
            or PostgresErrorCodes.DeadlockDetected)
        {
            return Conflict(Problem(409, "Receivables operation conflict",
                "The request conflicts with a concurrent or existing receivables record. Reload and try again."));
        }
        catch (DbUpdateException exception) when (exception.InnerException is PostgresException postgres
            && postgres.SqlState is PostgresErrorCodes.UniqueViolation or PostgresErrorCodes.CheckViolation
                or PostgresErrorCodes.SerializationFailure or PostgresErrorCodes.DeadlockDetected)
        {
            return Conflict(Problem(409, "Receivables operation conflict",
                "The request conflicts with a concurrent or existing receivables record. Reload and try again."));
        }
        catch (KeyNotFoundException exception)
        {
            return NotFound(Problem(404, "Receivables record not found", exception.Message));
        }
        catch (ArgumentException exception)
        {
            return BadRequest(Problem(400, "Invalid receivables operation", exception.Message));
        }
        catch (DbUpdateException)
        {
            return BadRequest(Problem(400, "Invalid receivables operation",
                "The request violates a receivables data constraint."));
        }
        catch (NpgsqlException exception)
        {
            logger.LogError(exception, "A PostgreSQL error interrupted a receivables operation.");
            return StatusCode(503, Problem(503, "Receivables operation unavailable",
                "The receivables data service is temporarily unavailable."));
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "An unexpected error interrupted a receivables operation.");
            return StatusCode(500, Problem(500, "Receivables operation failed",
                "An unexpected error interrupted the operation."));
        }
    }

    private static ProblemDetails Problem(int status, string title, string detail)
        => new() { Status = status, Title = title, Detail = detail };

    private long TenantId()
        => long.TryParse(User.FindFirst("businessUnitId")?.Value, out var tenantId) && tenantId > 0
            ? tenantId
            : throw new ArgumentException("A valid tenant claim is required.");

    private string Actor()
        => User.FindFirst(ClaimTypes.NameIdentifier)?.Value
            ?? User.FindFirst("sub")?.Value
            ?? User.FindFirst(ClaimTypes.Email)?.Value
            ?? User.FindFirst("email")?.Value
            ?? User.FindFirst(ClaimTypes.Name)?.Value
            ?? User.Identity?.Name
            ?? throw new ArgumentException("An authenticated actor claim is required.");

    private string IdempotencyKey()
        => Request.Headers.TryGetValue("Idempotency-Key", out var value) && !string.IsNullOrWhiteSpace(value)
            ? value.ToString().Trim()
            : throw new ArgumentException("Idempotency-Key header is required.");

    private string ValidateProviderSignature(
        long businessUnitId, long noticeId, bool delivered, DunningDeliveryResultRequest request)
    {
        var secret = configuration["CommercialFinance:DunningProviderWebhookSecret"];
        if (string.IsNullOrWhiteSpace(secret) || Encoding.UTF8.GetByteCount(secret) < 32)
            throw new InvalidOperationException("Dunning provider verification is not configured.");
        if (!Request.Headers.TryGetValue("X-Nexora-Provider-Signature", out var supplied) ||
            supplied.Count != 1)
            throw new ArgumentException("A provider signature is required.");
        var canonical = string.Join('\n', businessUnitId, noticeId, delivered.ToString().ToLowerInvariant(),
            request.ProviderEventId.ToString("D"), request.ProviderReference?.Trim(),
            new DateTimeOffset(NormalizeUtc(request.ProviderOccurredOn)).ToUnixTimeMilliseconds(),
            request.FailureCode?.Trim(), request.SignedEvidenceReference?.Trim());
        var expected = Convert.ToHexString(HMACSHA256.HashData(
            Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
        byte[] expectedBytes;
        byte[] suppliedBytes;
        try
        {
            expectedBytes = Convert.FromHexString(expected);
            suppliedBytes = Convert.FromHexString(supplied.ToString().Trim());
        }
        catch (FormatException)
        {
            throw new ArgumentException("The provider signature is invalid.");
        }
        if (!CryptographicOperations.FixedTimeEquals(expectedBytes, suppliedBytes))
            throw new ArgumentException("The provider signature is invalid.");
        return supplied.ToString().Trim().ToLowerInvariant();
    }

    private static DateTime NormalizeUtc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Local => value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(value, DateTimeKind.Utc)
    };
}
