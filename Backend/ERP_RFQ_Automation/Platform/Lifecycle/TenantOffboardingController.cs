using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Platform.Auth;
using ERP_RFQ_Automation.Platform.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.Platform.Lifecycle;

/// <summary>
/// Everything that happens to a tenant AFTER it stops being a customer: hand their data back,
/// schedule the deletion, let the retention window run, destroy the records, erase the people.
///
/// <para><b>Why this is a separate controller from <c>TenantsController</c>.</b> The two halves of
/// the lifecycle have different authority. Suspend, resume, archive and restore are reversible and
/// belong to support (<c>PlatformPolicies.TenantAdmin</c> — Owner OR SupportAdmin). Nothing here
/// is reversible, so the destructive verbs require <c>PlatformPolicies.Owner</c>: a support
/// engineer must be able to take a customer off the product and must not be able to destroy them.
/// Splitting the controllers is what makes that difference impossible to lose in a merge.</para>
///
/// <para>The read endpoints stay on the class-level <c>PlatformScope</c> gate, because the whole
/// point of a retention window is that other people can see the clock running.</para>
/// </summary>
[ApiController]
[Route("api/platform/tenants")]
[Authorize(Policy = PlatformPolicies.PlatformScope)]
public sealed class TenantOffboardingController(
    TenantOffboardingService offboarding,
    TenantPurgeExecutor purge,
    ErpRfqAutomationContext context,
    ILogger<TenantOffboardingController> logger) : ControllerBase
{
    /// <summary>
    /// Where the tenant is on both lifecycle axes, what the retention clock says, what must be
    /// typed to destroy it, and the complete transition history. One read, because an offboarding
    /// screen that assembles this from four calls will eventually show three of them.
    /// </summary>
    // GET /api/platform/tenants/{tenantId}/offboarding
    [HttpGet("{tenantId:long}/offboarding")]
    public Task<ActionResult<TenantOffboardingStatusDto>> Get(long tenantId, CancellationToken ct) =>
        Execute(() => offboarding.GetStatusAsync(tenantId, ct));

    /// <summary>Every tenant whose retention clock is running, soonest first.</summary>
    // GET /api/platform/tenants/offboarding/pending
    [HttpGet("offboarding/pending")]
    public async Task<ActionResult<IReadOnlyList<PendingTenantDeletionDto>>> Pending(CancellationToken ct)
        => Ok(await offboarding.ListPendingDeletionsAsync(ct));

    /// <summary>
    /// What a purge would destroy, table by table, with row counts. Always run before the purge:
    /// it is the only place the blast radius is visible, and it reads nothing but counts.
    /// </summary>
    // GET /api/platform/tenants/{tenantId}/offboarding/purge-preview
    [HttpGet("{tenantId:long}/offboarding/purge-preview")]
    [Authorize(Policy = PlatformPolicies.Owner)]
    public Task<ActionResult<TenantPurgePreview>> PurgePreview(long tenantId, CancellationToken ct) =>
        Execute(async () =>
        {
            var tenant = await context.Set<Tenant>().IgnoreQueryFilters().AsNoTracking()
                .Where(t => t.Id == tenantId)
                .Select(t => new { t.Id, t.PrimaryBusinessUnitId })
                .FirstOrDefaultAsync(ct)
                ?? throw new TenantOffboardingNotFoundException(tenantId);

            var businessUnitId = tenant.PrimaryBusinessUnitId
                ?? throw TenantOffboardingRefusedException.Conflict(
                    $"Tenant {tenantId} has no primary business unit, so there is no way to identify "
                    + "which rows are theirs.");

            return await purge.PreviewAsync(tenant.Id, businessUnitId, ct);
        });

    /// <summary>
    /// Produces the tenant's data file. Returned as a download and deliberately not stored — the
    /// receipt (who, when, what, and the SHA-256 of the bytes) is persisted and is visible on the
    /// offboarding status. The hash also travels in a response header so a console can show it
    /// beside the download without a second call.
    ///
    /// <para><b>Owner, like everything else here.</b> It was <c>TenantAdmin</c> until a red-team
    /// audit pointed out that it was the only mutating verb on this controller a SupportAdmin
    /// could reach — and it is the one that hands over every lead, quote, order and customer the
    /// tenant has. The row-count preview beside it was already Owner-only, so support could not
    /// count the rows but could take them all.</para>
    /// </summary>
    // POST /api/platform/tenants/{tenantId}/offboarding/export
    [HttpPost("{tenantId:long}/offboarding/export")]
    [Authorize(Policy = PlatformPolicies.Owner)]
    public async Task<IActionResult> Export(
        long tenantId, [FromBody] ExportTenantDataRequest request, CancellationToken ct)
    {
        try
        {
            var (document, receipt) = await offboarding.ExportAsync(
                tenantId, request?.Reason, request?.Confirmation, User, HttpContext, ct);

            Response.Headers["X-Nexora-Export-Sha256"] = document.ContentSha256;
            Response.Headers["X-Nexora-Export-Receipt"] = receipt.Id.ToString();
            Response.Headers["X-Nexora-Export-Rows"] = document.TotalRows.ToString();

            return File(document.Content, "application/json",
                $"nexora-export-{receipt.Id}-{DateTime.UtcNow:yyyyMMddHHmmss}.json");
        }
        catch (TenantOffboardingNotFoundException)
        {
            return NotFound();
        }
        catch (TenantOffboardingRefusedException refusal)
        {
            return Problem(refusal, "tenant export");
        }
    }

    /// <summary>
    /// Starts the retention clock. Reversible, so it takes a reason and no typed confirmation —
    /// the confirmation belongs on the step that cannot be undone.
    /// </summary>
    // POST /api/platform/tenants/{tenantId}/offboarding/schedule-deletion
    [HttpPost("{tenantId:long}/offboarding/schedule-deletion")]
    [Authorize(Policy = PlatformPolicies.Owner)]
    public Task<ActionResult<TenantOffboardingStatusDto>> ScheduleDeletion(
        long tenantId, [FromBody] ScheduleTenantDeletionRequest request, CancellationToken ct) =>
        Execute(() => offboarding.ScheduleDeletionAsync(tenantId, request, User, HttpContext, ct));

    // POST /api/platform/tenants/{tenantId}/offboarding/cancel-deletion
    [HttpPost("{tenantId:long}/offboarding/cancel-deletion")]
    [Authorize(Policy = PlatformPolicies.Owner)]
    public Task<ActionResult<TenantOffboardingStatusDto>> CancelDeletion(
        long tenantId, [FromBody] CancelTenantDeletionRequest request, CancellationToken ct) =>
        Execute(() => offboarding.CancelDeletionAsync(tenantId, request, User, HttpContext, ct));

    /// <summary>
    /// Destroys the tenant's records. Requires the retention window to have elapsed, a reason long
    /// enough to explain the decision, and the tenant's exact name echoed back.
    /// </summary>
    // POST /api/platform/tenants/{tenantId}/offboarding/purge
    [HttpPost("{tenantId:long}/offboarding/purge")]
    [Authorize(Policy = PlatformPolicies.Owner)]
    public Task<ActionResult<TenantPurgeResultDto>> Purge(
        long tenantId, [FromBody] ConfirmTenantDestructionRequest request, CancellationToken ct) =>
        Execute(() => offboarding.PurgeAsync(tenantId, request, User, HttpContext, ct));

    /// <summary>
    /// Erases the personal data and keeps the commercial records — a DIFFERENT operation from the
    /// purge, available independently of it and at any point before it. See
    /// <see cref="TenantPersonalDataEraser"/> for the legal shape of the distinction.
    /// </summary>
    // POST /api/platform/tenants/{tenantId}/offboarding/erase-personal-data
    [HttpPost("{tenantId:long}/offboarding/erase-personal-data")]
    [Authorize(Policy = PlatformPolicies.Owner)]
    public Task<ActionResult<TenantErasureResultDto>> ErasePersonalData(
        long tenantId, [FromBody] ConfirmTenantDestructionRequest request, CancellationToken ct) =>
        Execute(() => offboarding.ErasePersonalDataAsync(tenantId, request, User, HttpContext, ct));

    /// <summary>
    /// One place where an offboarding failure becomes an HTTP answer, so a refusal cannot mean 409
    /// on one endpoint and 400 on another. The status comes from the exception
    /// (<see cref="TenantOffboardingRefusedException.SuggestedStatusCode"/>) rather than from the
    /// call site, mirroring the P2-A10 rule that the typed exception is the single source of the
    /// problem type, title and status.
    /// </summary>
    private async Task<ActionResult<T>> Execute<T>(Func<Task<T>> work)
    {
        try
        {
            return Ok(await work());
        }
        catch (TenantOffboardingNotFoundException)
        {
            return NotFound();
        }
        catch (TenantOffboardingRefusedException refusal)
        {
            return Problem(refusal, "tenant offboarding");
        }
        catch (Exception exception)
        {
            // Logged WITH the exception. A swallowed one is how a duplicate-key violation went
            // unnoticed through every portal provisioning attempt; the operator gets a flat
            // message, and the server keeps the only copy of why.
            logger.LogError(exception, "Tenant offboarding operation failed.");
            return StatusCode(500, new { error = "The offboarding operation failed." });
        }
    }

    private ObjectResult Problem(TenantOffboardingRefusedException refusal, string operation)
    {
        logger.LogWarning("Refused {Operation}: {Message}", operation, refusal.Message);
        return StatusCode(refusal.SuggestedStatusCode, new
        {
            error = refusal.Message,
            status = refusal.SuggestedStatusCode
        });
    }
}
