using System.Security.Claims;
using ERP_RFQ_Automation.Authorization;
using ERP_RFQ_Automation.GeneralLedger;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace ERP_RFQ_Automation.Controllers;

[ApiController]
[Route("api/general-ledger")]
[Authorize]
public sealed class GeneralLedgerController(IGeneralLedgerService service) : ControllerBase
{
    private readonly IGeneralLedgerService _service = service;

    [HttpPost("book")]
    [RequireModulePermission("General Ledger", PermissionAction.Create)]
    public Task<IActionResult> CreateBook([FromBody] CreateLedgerBookRequest request)
        => Mutate(async () => Created("api/general-ledger/book",
            await _service.CreateBookAsync(TenantId(), IdempotencyKey(), request, Actor())));

    [HttpGet("book")]
    [RequireModulePermission("General Ledger", PermissionAction.View)]
    public async Task<IActionResult> GetBook()
        => await Read(async () => Ok(await _service.GetBookAsync(TenantId())));

    [HttpPost("book/receivables-posting")]
    [RequireModulePermission("Ledger Control", PermissionAction.Edit)]
    public Task<IActionResult> ConfigureReceivablesPosting([FromBody] ConfigureReceivablesPostingRequest request)
        => Mutate(async () => Ok(await _service.ConfigureReceivablesPostingAsync(TenantId(), request, Actor())));

    [HttpPost("accounts")]
    [RequireModulePermission("General Ledger", PermissionAction.Create)]
    public Task<IActionResult> CreateAccount([FromBody] CreateLedgerAccountRequest request)
        => Mutate(async () => Created("api/general-ledger/accounts",
            await _service.CreateAccountAsync(TenantId(), IdempotencyKey(), request, Actor())));

    [HttpPost("accounts/{accountId:long}/deactivate")]
    [RequireModulePermission("General Ledger", PermissionAction.Edit)]
    public Task<IActionResult> DeactivateAccount(long accountId, [FromBody] DeactivateLedgerAccountRequest request)
        => Mutate(async () => Ok(await _service.DeactivateAccountAsync(TenantId(), accountId, request, Actor())));

    [HttpGet("accounts")]
    [RequireModulePermission("General Ledger", PermissionAction.View)]
    public async Task<IActionResult> GetAccounts([FromQuery] bool includeInactive = false)
        => Ok(await _service.GetAccountsAsync(TenantId(), includeInactive));

    [HttpPost("periods")]
    [RequireModulePermission("Accounting Periods", PermissionAction.Create)]
    public Task<IActionResult> CreatePeriod([FromBody] CreateAccountingPeriodRequest request)
        => Mutate(async () => Created("api/general-ledger/periods",
            await _service.CreatePeriodAsync(TenantId(), IdempotencyKey(), request, Actor())));

    [HttpPost("periods/{periodId:long}/soft-close")]
    [RequireModulePermission("Accounting Periods", PermissionAction.Edit)]
    public Task<IActionResult> SoftClosePeriod(long periodId, [FromBody] AccountingPeriodActionRequest request)
        => TransitionPeriod(periodId, "soft-close", request);

    [HttpPost("periods/{periodId:long}/close")]
    [RequireModulePermission("Period Close", PermissionAction.Edit)]
    public Task<IActionResult> ClosePeriod(long periodId, [FromBody] AccountingPeriodActionRequest request)
        => TransitionPeriod(periodId, "close", request);

    [HttpPost("periods/{periodId:long}/reopen")]
    [RequireModulePermission("Ledger Control", PermissionAction.Edit)]
    public Task<IActionResult> ReopenPeriod(long periodId, [FromBody] AccountingPeriodActionRequest request)
        => TransitionPeriod(periodId, "reopen", request);

    [HttpGet("periods")]
    [RequireModulePermission("Accounting Periods", PermissionAction.View)]
    public async Task<IActionResult> GetPeriods([FromQuery] int? fiscalYear)
        => Ok(await _service.GetPeriodsAsync(TenantId(), fiscalYear));

    [HttpPost("journals")]
    [RequireModulePermission("General Ledger", PermissionAction.Create)]
    public Task<IActionResult> CreateJournal([FromBody] CreateJournalEntryRequest request)
        => Mutate(async () =>
        {
            var journal = await _service.CreateManualJournalAsync(TenantId(), IdempotencyKey(), request, Actor());
            return CreatedAtAction(nameof(GetJournal), new { journalId = journal.Id }, journal);
        });

    [HttpPost("journals/{journalId:long}/post")]
    [RequireModulePermission("General Ledger Posting", PermissionAction.Edit)]
    public Task<IActionResult> PostJournal(long journalId, [FromBody] JournalActionRequest request)
        => Mutate(async () => Ok(await _service.PostJournalAsync(TenantId(), journalId, request, Actor())));

    [HttpPost("journals/{journalId:long}/cancel")]
    [RequireModulePermission("General Ledger", PermissionAction.Edit)]
    public Task<IActionResult> CancelJournal(long journalId, [FromBody] JournalActionRequest request)
        => Mutate(async () => Ok(await _service.CancelJournalAsync(TenantId(), journalId, request, Actor())));

    [HttpPost("journals/{journalId:long}/reverse")]
    [RequireModulePermission("Ledger Control", PermissionAction.Edit)]
    public Task<IActionResult> ReverseJournal(long journalId, [FromBody] JournalActionRequest request)
        => Mutate(async () => Ok(await _service.ReverseJournalAsync(
            TenantId(), journalId, IdempotencyKey(), request, Actor())));

    [HttpGet("journals/{journalId:long}")]
    [RequireModulePermission("General Ledger", PermissionAction.View)]
    public async Task<IActionResult> GetJournal(long journalId)
        => await Read(async () => Ok(await _service.GetJournalAsync(TenantId(), journalId)));

    [HttpGet("journals")]
    [RequireModulePermission("General Ledger", PermissionAction.View)]
    public async Task<IActionResult> GetJournals([FromQuery] long? periodId, [FromQuery] string? status)
        => Ok(await _service.GetJournalsAsync(TenantId(), periodId, status));

    [HttpGet("trial-balance")]
    [RequireModulePermission("General Ledger", PermissionAction.View)]
    public async Task<IActionResult> GetTrialBalance(
        [FromQuery] DateTime from, [FromQuery] DateTime through, [FromQuery] long functionalCurrencyId)
        => await Read(async () => Ok(await _service.GetTrialBalanceAsync(TenantId(), from, through, functionalCurrencyId)));

    private async Task<IActionResult> Mutate(Func<Task<IActionResult>> action) => await Execute(action);
    private async Task<IActionResult> Read(Func<Task<IActionResult>> action) => await Execute(action);
    private Task<IActionResult> TransitionPeriod(long periodId, string action, AccountingPeriodActionRequest request)
        => Mutate(async () => Ok(await _service.TransitionPeriodAsync(TenantId(), periodId, action, request, Actor())));

    private static bool IsConflict(PostgresException exception) => exception.SqlState is
        PostgresErrorCodes.UniqueViolation or PostgresErrorCodes.CheckViolation or
        PostgresErrorCodes.ExclusionViolation or PostgresErrorCodes.SerializationFailure or
        PostgresErrorCodes.DeadlockDetected or PostgresErrorCodes.RaiseException;

    private async Task<IActionResult> Execute(Func<Task<IActionResult>> action)
    {
        try { return await action(); }
        catch (GeneralLedgerConflictException exception)
        {
            return Conflict(new ProblemDetails { Status = 409, Title = "General ledger conflict", Detail = exception.Message });
        }
        catch (PostgresException exception) when (IsConflict(exception))
        {
            return Conflict(new ProblemDetails { Status = 409, Title = "General ledger conflict",
                Detail = "The request conflicts with ledger controls or a concurrent operation. Reload and try again." });
        }
        catch (DbUpdateException exception) when (exception.InnerException is PostgresException postgres && IsConflict(postgres))
        {
            return Conflict(new ProblemDetails { Status = 409, Title = "General ledger conflict",
                Detail = "The request conflicts with ledger controls or a concurrent operation. Reload and try again." });
        }
        catch (DbUpdateException)
        {
            return BadRequest(new ProblemDetails { Status = 400, Title = "Invalid ledger request",
                Detail = "The request violates an accounting data constraint." });
        }
        catch (KeyNotFoundException exception)
        {
            return NotFound(new ProblemDetails { Status = 404, Title = "Ledger record not found", Detail = exception.Message });
        }
        catch (ArgumentException exception)
        {
            return BadRequest(new ProblemDetails { Status = 400, Title = "Invalid ledger request", Detail = exception.Message });
        }
    }

    private long TenantId()
    {
        if (!long.TryParse(User.FindFirst("businessUnitId")?.Value, out var tenantId) || tenantId <= 0)
            throw new ArgumentException("A valid tenant claim is required.");
        return tenantId;
    }

    private string Actor()
        => User.FindFirst(ClaimTypes.NameIdentifier)?.Value
            ?? User.FindFirst("sub")?.Value
            ?? User.FindFirst(ClaimTypes.Email)?.Value
            ?? User.FindFirst("email")?.Value
            ?? User.Identity?.Name
            ?? throw new ArgumentException("An authenticated actor claim is required.");

    private string IdempotencyKey()
        => Request.Headers.TryGetValue("Idempotency-Key", out var value) && !string.IsNullOrWhiteSpace(value)
            ? value.ToString().Trim()
            : throw new ArgumentException("Idempotency-Key header is required.");
}
