using System.Security.Claims;
using ERP_RFQ_Automation.Authorization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ERP_RFQ_Automation.OrderToCash;

/// <summary>
/// HTTP surface for the per-tenant commercial policy: how much supplier input tax is recoverable,
/// the output tax rate, and the customer-PO price/quantity tolerances.
///
/// <para>Before this existed the row was unreachable — no controller, no service write, no frontend
/// reference — while the product owner had explicitly asked for it to be customer-settable. Follows
/// the SlaController shape: BU from the <c>businessUnitId</c> claim, GET open to any authenticated
/// user in the tenant, writes restricted to a manager or admin.</para>
///
/// <para>The write takes an <c>Idempotency-Key</c> header because it appends to the tenant
/// governance ledger, and a retried save must record one change, not two.</para>
/// </summary>
[ApiController]
[Route("api/commercial-policy")]
[Authorize]
public sealed class CommercialPolicyController(CommercialMatchingPolicyService policies) : ControllerBase
{
    /// <summary>
    /// The tenant's policy, or the defaults it is currently running on. Never 404s: absence of a
    /// row means defaults, and the screen has to be able to show them before the first save.
    /// </summary>
    [HttpGet]
    public async Task<ActionResult<CommercialMatchingPolicyView>> Get(CancellationToken ct) =>
        await Execute(() => policies.GetAsync(TenantId(), ct));

    /// <summary>
    /// Changes the policy. Manager/admin only, and a reason is mandatory: this row re-bases every
    /// landed cost and every derived tax computed after it.
    /// </summary>
    [HttpPut]
    [RequireManagerRole]
    public async Task<ActionResult<CommercialMatchingPolicyView>> Put(
        [FromBody] UpdateCommercialMatchingPolicyCommand command, CancellationToken ct) =>
        await Execute(() => policies.UpdateAsync(TenantId(), ActorUserId(), Actor(),
            IdempotencyKey(), command, ct));

    private async Task<ActionResult<T>> Execute<T>(Func<Task<T>> operation)
    {
        try { return Ok(await operation()); }
        catch (CommercialPolicyValidationException exception)
        { return BadRequest(Problem(400, exception.Message)); }
        catch (PlatformGovernance.PlatformGovernanceValidationException exception)
        { return BadRequest(Problem(400, exception.Message)); }
        // Version is a concurrency token, so two people editing this policy at once is a real
        // outcome and not a server fault. Reported as a conflict with something the user can act
        // on, rather than as the 500 an unhandled EF exception would have produced.
        catch (Microsoft.EntityFrameworkCore.DbUpdateConcurrencyException)
        {
            return Conflict(Problem(409,
                "Someone else changed this commercial policy while you were editing it. " +
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

    // The governance ledger records WHO by user id; the policy row records who by name, because a
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
            throw new CommercialPolicyValidationException(
                "Idempotency-Key is required and must not exceed 160 printable characters.");
        return value;
    }

    private static ProblemDetails Problem(int status, string detail) => new()
    {
        Status = status,
        Title = status switch
        {
            401 => "Invalid authentication context",
            409 => "Commercial policy conflict",
            _ => "Invalid request"
        },
        Detail = detail
    };
}
