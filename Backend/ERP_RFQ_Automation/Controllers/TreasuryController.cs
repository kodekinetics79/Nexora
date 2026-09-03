using System.Security.Claims;
using System.Security.Cryptography;
using ERP_RFQ_Automation.Authorization;
using ERP_RFQ_Automation.BankReconciliation;
using ERP_RFQ_Automation.BankReconciliation.Parsing;
using ERP_RFQ_Automation.BankReconciliation.Services;
using ERP_RFQ_Automation.Security.DocumentInspection;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace ERP_RFQ_Automation.Controllers;

[ApiController]
[Route("api/treasury")]
[Authorize]
public sealed class TreasuryController(
    IBankReconciliationService service,
    IBankAdjustmentService adjustments,
    IFileInspectionService fileInspection) : ControllerBase
{
    private const int MaximumUploadBytes = 10 * 1024 * 1024;
    private readonly IBankReconciliationService _service = service;
    private readonly IBankAdjustmentService _adjustments = adjustments;
    private readonly IFileInspectionService _fileInspection = fileInspection;

    [HttpPost("matching-rules")]
    [RequireModulePermission("Bank Matching Rule Administration", PermissionAction.Create)]
    public Task<IActionResult> CreateMatchingRule([FromBody] CreateBankMatchingRuleRequest request)
        => Execute(async () => Created("api/treasury/matching-rules",
            await _service.CreateMatchingRuleAsync(TenantId(), IdempotencyKey(), request, Actor(), HttpContext.RequestAborted)));

    [HttpGet("matching-rules")]
    [RequireModulePermission("Bank Matching Rule Administration", PermissionAction.View)]
    public Task<IActionResult> GetMatchingRules()
        => Execute(async () => Ok(await _service.GetMatchingRulesAsync(TenantId(), HttpContext.RequestAborted)));

    [HttpPost("matching-rules/{ruleId:long}/{action}")]
    [RequireModulePermission("Bank Matching Rule Approval", PermissionAction.Edit)]
    public Task<IActionResult> TransitionMatchingRule(long ruleId, string action,
        [FromBody] BankMatchingRuleActionRequest request)
        => Execute(async () => Ok(await _service.TransitionMatchingRuleAsync(
            TenantId(), ruleId, action, request, Actor(), HttpContext.RequestAborted)));

    [HttpPost("adjustments")]
    [RequireModulePermission("Bank Adjustments", PermissionAction.Create)]
    public Task<IActionResult> CreateAdjustment([FromBody] CreateBankAdjustmentRequest request)
        => Execute(async () => Created("api/treasury/adjustments",
            await _adjustments.CreateAsync(TenantId(), IdempotencyKey(), request, Actor(), HttpContext.RequestAborted)));

    [HttpGet("adjustments")]
    [RequireModulePermission("Bank Adjustments", PermissionAction.View)]
    public Task<IActionResult> GetAdjustments([FromQuery] string? status)
        => Execute(async () => Ok(await _adjustments.GetAllAsync(TenantId(), status, HttpContext.RequestAborted)));

    [HttpGet("adjustments/{adjustmentId:long}")]
    [RequireModulePermission("Bank Adjustments", PermissionAction.View)]
    public Task<IActionResult> GetAdjustment(long adjustmentId)
        => Execute(async () => Ok(await _adjustments.GetAsync(TenantId(), adjustmentId, HttpContext.RequestAborted)));

    [HttpPost("adjustments/{adjustmentId:long}/{action}")]
    [RequireModulePermission("Bank Adjustment Approval", PermissionAction.Edit)]
    public Task<IActionResult> TransitionAdjustment(long adjustmentId, string action,
        [FromBody] BankAdjustmentActionRequest request)
        => Execute(async () => Ok(await _adjustments.TransitionAsync(
            TenantId(), adjustmentId, action, request, Actor(), HttpContext.RequestAborted)));

    [HttpPost("bank-accounts")]
    [RequireModulePermission("Bank Accounts", PermissionAction.Create)]
    public Task<IActionResult> CreateBankAccount([FromBody] CreateBankAccountRequest request)
        => Execute(async () => Created("api/treasury/bank-accounts",
            await _service.CreateBankAccountAsync(TenantId(), IdempotencyKey(), request, Actor(), HttpContext.RequestAborted)));

    [HttpGet("bank-accounts")]
    [RequireModulePermission("Bank Accounts", PermissionAction.View)]
    public Task<IActionResult> GetBankAccounts([FromQuery] bool includeClosed = false)
        => Execute(async () => Ok(await _service.GetBankAccountsAsync(TenantId(), includeClosed, HttpContext.RequestAborted)));

    [HttpGet("bank-accounts/{accountId:long}")]
    [RequireModulePermission("Bank Accounts", PermissionAction.View)]
    public Task<IActionResult> GetBankAccount(long accountId)
        => Execute(async () => Ok(await _service.GetBankAccountAsync(TenantId(), accountId, HttpContext.RequestAborted)));

    [HttpPost("bank-accounts/{accountId:long}/{action}")]
    [RequireModulePermission("Bank Accounts", PermissionAction.Edit)]
    public Task<IActionResult> TransitionBankAccount(long accountId, string action, [FromBody] BankAccountActionRequest request)
        => Execute(async () => Ok(await _service.TransitionBankAccountAsync(
            TenantId(), accountId, action, request, Actor(), HttpContext.RequestAborted)));

    [HttpPost("statements/import")]
    [Consumes("multipart/form-data")]
    [RequestSizeLimit(MaximumUploadBytes)]
    [Microsoft.AspNetCore.RateLimiting.EnableRateLimiting(ERP_RFQ_Automation.Platform.Hardening.RateLimitingExtensions.UploadPolicy)]
    [RequireModulePermission("Bank Statement Import", PermissionAction.Create)]
    public Task<IActionResult> ImportStatement([FromForm] long bankAccountId, [FromForm] string sourceType,
        [FromForm] IFormFile file)
        => Execute(async () =>
        {
            if (file.Length <= 0 || file.Length > MaximumUploadBytes)
                throw new ArgumentException("Statement file must contain data and cannot exceed 10 MiB.");
            // Inspected BEFORE parsing, like every other upload door; a scanner that cannot
            // answer refuses the import rather than letting an unscanned statement be parsed.
            await using var inspected = await UploadInspectionGate.InspectAsync(
                _fileInspection, file, HttpContext.RequestAborted);
            if (!inspected.IsCleared)
                return UploadInspectionGate.Refuse(this, inspected.Inspection, "Bank statement file rejected");
            var buffer = inspected.Content;
            var payload = buffer.ToArray();
            var sourceHash = Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();
            buffer.Position = 0;
            IBankStatementParser parser = sourceType.Trim().ToUpperInvariant() switch
            {
                "CSV" => new StrictCsvBankStatementParser(),
                "CAMT053" or "CAMT.053" => new Camt053BankStatementParser(),
                _ => throw new ArgumentException("Source type must be CSV or CAMT053.")
            };
            var parsed = parser.Parse(buffer);
            var statement = await _service.ImportStatementAsync(TenantId(), IdempotencyKey(), bankAccountId,
                sourceType.Trim().ToUpperInvariant(), file.FileName, $"sha256:{sourceHash}", sourceHash,
                "bank-parser-v1", payload, parsed, Actor(), HttpContext.RequestAborted);
            return CreatedAtAction(nameof(GetStatement), new { statementId = statement.Id }, statement);
        });

    [HttpGet("statements/{statementId:long}")]
    [RequireModulePermission("Bank Statement Import", PermissionAction.View)]
    public Task<IActionResult> GetStatement(long statementId)
        => Execute(async () => Ok(await _service.GetStatementAsync(TenantId(), statementId, HttpContext.RequestAborted)));

    [HttpGet("statements/{statementId:long}/source")]
    [RequireModulePermission("Bank Statement Import", PermissionAction.View)]
    public Task<IActionResult> GetStatementSource(long statementId)
        => Execute(async () =>
        {
            var source = await _service.GetStatementSourceAsync(TenantId(), statementId, HttpContext.RequestAborted);
            var contentType = source.SourceType.Equals("CSV", StringComparison.OrdinalIgnoreCase)
                ? "text/csv" : "application/xml";
            Response.Headers.Append("X-Content-SHA256", source.SourceHash);
            return File(source.Payload, contentType, source.FileName);
        });

    [HttpPost("reconciliations")]
    [RequireModulePermission("Bank Reconciliation", PermissionAction.Create)]
    public Task<IActionResult> CreateRun([FromBody] CreateReconciliationRunRequest request)
        => Execute(async () => Created("api/treasury/reconciliations",
            await _service.CreateRunAsync(TenantId(), IdempotencyKey(), request, Actor(), HttpContext.RequestAborted)));

    [HttpGet("reconciliations/{runId:long}")]
    [RequireModulePermission("Bank Reconciliation", PermissionAction.View)]
    public Task<IActionResult> GetRun(long runId)
        => Execute(async () => Ok(await _service.GetRunAsync(TenantId(), runId, HttpContext.RequestAborted)));

    [HttpPost("reconciliations/{runId:long}/exact-candidates")]
    [RequireModulePermission("Bank Reconciliation", PermissionAction.Edit)]
    public Task<IActionResult> GenerateExactCandidates(long runId)
        => Execute(async () => Ok(await _service.GenerateExactCandidatesAsync(
            TenantId(), runId, IdempotencyKey(), Actor(), HttpContext.RequestAborted)));

    [HttpPost("matches")]
    [RequireModulePermission("Bank Reconciliation", PermissionAction.Create)]
    public Task<IActionResult> CreateMatch([FromBody] CreateReconciliationMatchRequest request)
        => Execute(async () => Created("api/treasury/matches",
            await _service.CreateMatchAsync(TenantId(), IdempotencyKey(), request, Actor(), HttpContext.RequestAborted)));

    [HttpPost("matches/{matchId:long}/confirm")]
    [RequireModulePermission("Bank Reconciliation", PermissionAction.Edit)]
    public Task<IActionResult> ConfirmMatch(long matchId, [FromBody] MatchActionRequest request)
        => Execute(async () => Ok(await _service.ConfirmMatchAsync(TenantId(), matchId, request, Actor(), HttpContext.RequestAborted)));

    [HttpPost("matches/{matchId:long}/void")]
    [RequireModulePermission("Bank Reconciliation", PermissionAction.Edit)]
    public Task<IActionResult> VoidMatch(long matchId, [FromBody] MatchActionRequest request)
        => Execute(async () => Ok(await _service.VoidMatchAsync(TenantId(), matchId, request, Actor(), HttpContext.RequestAborted)));

    [HttpPost("reconciliations/{runId:long}/submit")]
    [RequireModulePermission("Bank Reconciliation", PermissionAction.Edit)]
    public Task<IActionResult> SubmitRun(long runId, [FromBody] ReconciliationActionRequest request)
        => Execute(async () => Ok(await _service.SubmitRunAsync(TenantId(), runId, request, Actor(), HttpContext.RequestAborted)));

    [HttpPost("reconciliations/{runId:long}/approve")]
    [RequireModulePermission("Bank Reconciliation Approval", PermissionAction.Edit)]
    public Task<IActionResult> ApproveRun(long runId, [FromBody] ReconciliationActionRequest request)
        => Execute(async () => Ok(await _service.ApproveRunAsync(TenantId(), runId, request, Actor(), HttpContext.RequestAborted)));

    [HttpPost("reconciliations/{runId:long}/reopen")]
    [RequireModulePermission("Bank Reconciliation Approval", PermissionAction.Edit)]
    public Task<IActionResult> ReopenRun(long runId, [FromBody] ReconciliationActionRequest request)
        => Execute(async () => Ok(await _service.ReopenRunAsync(TenantId(), runId, request, Actor(), HttpContext.RequestAborted)));

    private async Task<IActionResult> Execute(Func<Task<IActionResult>> action)
    {
        try { return await action(); }
        catch (BankReconciliationConflictException exception)
        { return Conflict(Problem(409, "Bank reconciliation conflict", exception.Message)); }
        catch (PostgresException exception) when (IsConflict(exception))
        { return Conflict(Problem(409, "Bank reconciliation conflict", "The request conflicts with treasury controls or a concurrent operation.")); }
        catch (DbUpdateException exception) when (exception.InnerException is PostgresException postgres && IsConflict(postgres))
        { return Conflict(Problem(409, "Bank reconciliation conflict", "The request conflicts with treasury controls or a concurrent operation.")); }
        catch (KeyNotFoundException exception)
        { return NotFound(Problem(404, "Treasury record not found", exception.Message)); }
        catch (ArgumentException exception)
        { return BadRequest(Problem(400, "Invalid treasury request", exception.Message)); }
        catch (FormatException exception)
        { return BadRequest(Problem(400, "Invalid statement format", exception.Message)); }
        catch (DbUpdateException)
        { return BadRequest(Problem(400, "Invalid treasury request", "The request violates a treasury data constraint.")); }
    }

    private static bool IsConflict(PostgresException exception) => exception.SqlState is
        PostgresErrorCodes.UniqueViolation or PostgresErrorCodes.CheckViolation or
        PostgresErrorCodes.ForeignKeyViolation or PostgresErrorCodes.SerializationFailure or
        PostgresErrorCodes.DeadlockDetected or PostgresErrorCodes.ObjectNotInPrerequisiteState or
        PostgresErrorCodes.RaiseException;
    private static ProblemDetails Problem(int status, string title, string detail)
        => new() { Status = status, Title = title, Detail = detail };
    private long TenantId() => long.TryParse(User.FindFirst("businessUnitId")?.Value, out var value) && value > 0
        ? value : throw new ArgumentException("A valid tenant claim is required.");
    private string Actor() => User.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? User.FindFirst("sub")?.Value
        ?? User.FindFirst(ClaimTypes.Email)?.Value ?? User.FindFirst("email")?.Value ?? User.Identity?.Name
        ?? throw new ArgumentException("An authenticated actor claim is required.");
    private string IdempotencyKey() => Request.Headers.TryGetValue("Idempotency-Key", out var value) &&
        !string.IsNullOrWhiteSpace(value) ? value.ToString().Trim()
        : throw new ArgumentException("Idempotency-Key header is required.");
}
