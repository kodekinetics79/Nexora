using System.Security.Claims;
using ERP_RFQ_Automation.Authorization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ERP_RFQ_Automation.SupplierEvaluation;

/// <summary>
/// HTTP surface for the per-tenant supplier comparison weights: how much price, lead time, warranty
/// and payment terms are each worth in the weighted score shown on the quote comparison.
///
/// <para>Follows the <c>CommercialPolicyController</c> shape exactly: BU from the
/// <c>businessUnitId</c> claim, GET open to any authenticated user in the tenant, writes restricted
/// to a manager or admin. There is no new Setup route — the settings screen mounts this beside the
/// customer PO tolerances it already edits.</para>
///
/// <para>The write takes an <c>Idempotency-Key</c> header because it appends to the tenant
/// governance ledger, and a retried save must record one change, not two.</para>
/// </summary>
[ApiController]
// Route name agreed with the settings screen (Frontend/src/api/services/supplierScoringWeightsService.ts).
[Route("api/supplier-scoring-weights")]
[Authorize]
public sealed class SupplierComparisonWeightsController(SupplierComparisonWeightsService weights)
    : ControllerBase
{
    /// <summary>
    /// The tenant's weights, or the defaults it is currently running on. Never 404s: absence of a
    /// row means defaults, and the screen has to be able to show them before the first save.
    /// </summary>
    [HttpGet]
    public async Task<ActionResult<SupplierComparisonWeightsView>> Get(CancellationToken ct) =>
        await Execute(() => weights.GetAsync(TenantId(), ct));

    /// <summary>
    /// Replaces the weight set. Manager/admin only, all four sent together, must total 100, and a
    /// reason is mandatory: this decides which supplier is recommended on every line compared after
    /// the change.
    /// </summary>
    [HttpPut]
    [RequireManagerRole]
    public async Task<ActionResult<SupplierComparisonWeightsView>> Put(
        [FromBody] UpdateSupplierComparisonWeightsCommand command, CancellationToken ct) =>
        await Execute(() => weights.UpdateAsync(TenantId(), ActorUserId(), Actor(),
            IdempotencyKey(), command, ct));

    private async Task<ActionResult<T>> Execute<T>(Func<Task<T>> operation)
    {
        try { return Ok(await operation()); }
        catch (SupplierComparisonWeightsValidationException exception)
        { return BadRequest(Problem(400, exception.Message)); }
        catch (PlatformGovernance.PlatformGovernanceValidationException exception)
        { return BadRequest(Problem(400, exception.Message)); }
        // Version is a concurrency token, so two people editing the weights at once is a real
        // outcome and not a server fault.
        catch (Microsoft.EntityFrameworkCore.DbUpdateConcurrencyException)
        {
            return Conflict(Problem(409,
                "Someone else changed the supplier comparison weights while you were editing them. " +
                "Reload the page to see their change, then reapply yours."));
        }
        catch (UnauthorizedAccessException exception)
        { return Unauthorized(Problem(401, exception.Message)); }
    }

    private long TenantId() => long.TryParse(User.FindFirst("businessUnitId")?.Value, out var value) && value > 0
        ? value : throw new UnauthorizedAccessException("A valid authenticated tenant is required.");

    private long ActorUserId() => long.TryParse(User.FindFirst(ClaimTypes.NameIdentifier)?.Value
            ?? User.FindFirst("sub")?.Value, out var value) && value > 0
        ? value : throw new UnauthorizedAccessException("A valid authenticated actor is required.");

    // The governance ledger records WHO by user id; the weights row records who by name, because a
    // person reading "changed by 4471" a year later learns nothing.
    private string Actor() => User.FindFirst(ClaimTypes.Email)?.Value
        ?? User.FindFirst("email")?.Value
        ?? User.FindFirst(ClaimTypes.NameIdentifier)?.Value
        ?? User.Identity?.Name
        ?? throw new UnauthorizedAccessException("An authenticated actor claim is required.");

    private string IdempotencyKey()
    {
        var value = Request.Headers["Idempotency-Key"].ToString().Trim();
        if (string.IsNullOrWhiteSpace(value) || value.Length > 160 || value.Any(char.IsControl))
            throw new SupplierComparisonWeightsValidationException(
                "Idempotency-Key is required and must not exceed 160 printable characters.");
        return value;
    }

    private static ProblemDetails Problem(int status, string detail) => new()
    {
        Status = status,
        Title = status switch
        {
            401 => "Invalid authentication context",
            409 => "Supplier comparison weights conflict",
            _ => "Invalid request"
        },
        Detail = detail
    };
}
