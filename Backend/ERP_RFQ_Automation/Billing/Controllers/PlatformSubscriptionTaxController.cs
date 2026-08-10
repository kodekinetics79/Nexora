using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using ERP_RFQ_Automation.Platform.Auth;
using ERP_RFQ_Automation.Platform.Services;
using ERP_RFQ_Automation.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.Billing.Controllers;

[ApiController]
[Route("api/platform/billing/tax-rules")]
[Authorize(Policy = PlatformPolicies.Owner)]
[Authorize(Policy = PlatformPolicies.Mfa)]
public sealed class PlatformSubscriptionTaxController(
    ErpRfqAutomationContext db, SubscriptionTaxService tax, IPlatformAuditService audit) : ControllerBase
{
    [HttpPost]
    public async Task<IActionResult> Propose([FromBody] ProposeTaxRuleRequest request, CancellationToken ct)
    {
        try
        {
            var value = await InTransactionAsync(async () =>
            {
                var proposed = await tax.ProposeAsync(new(request.JurisdictionCode, request.BuyerCountryCode,
                    request.Currency, request.Treatment, request.RatePercent, request.LegalAuthorityReference,
                    request.EvidenceSha256, request.EffectiveFromUtc, request.EffectiveToUtc), ActorId(), ct);
                await audit.WriteAsync(User, "billing.tax-rule.propose", nameof(SubscriptionTaxRule),
                    proposed.Id.ToString(), new { proposed.JurisdictionCode, proposed.BuyerCountryCode, proposed.Currency,
                        proposed.RatePercent, proposed.EvidenceSha256 }, httpContext: HttpContext, ct: ct);
                return proposed;
            }, ct);
            return Ok(ToDto(value));
        }
        catch (BillingConflictException ex) { return Conflict(new { error = ex.Message }); }
    }

    [HttpPost("{id:long}/approve")]
    public async Task<IActionResult> Approve(long id, CancellationToken ct)
    {
        try
        {
            var value = await InTransactionAsync(async () =>
            {
                var approved = await tax.ApproveAsync(id, ActorId(), ct);
                await audit.WriteAsync(User, "billing.tax-rule.approve", nameof(SubscriptionTaxRule),
                    approved.Id.ToString(), new { approved.JurisdictionCode, approved.Version, approved.ApprovedByPlatformUserId },
                    httpContext: HttpContext, ct: ct);
                return approved;
            }, ct);
            return Ok(ToDto(value));
        }
        catch (BillingNotFoundException ex) { return NotFound(new { error = ex.Message }); }
        catch (BillingConflictException ex) { return Conflict(new { error = ex.Message }); }
    }

    private long ActorId() => long.TryParse(User.FindFirst("sub")?.Value
            ?? User.FindFirst(ClaimTypes.NameIdentifier)?.Value, out var id) && id > 0
        ? id : throw new UnauthorizedAccessException("A stable platform actor is required.");

    private static object ToDto(SubscriptionTaxRule x) => new
    {
        id = x.Id.ToString(), x.JurisdictionCode, x.BuyerCountryCode, x.Currency, x.Treatment,
        x.RatePercent, x.LegalAuthorityReference, x.EvidenceSha256, x.EffectiveFromUtc,
        x.EffectiveToUtc, x.Status, x.Version, x.ProposedByPlatformUserId, x.ProposedAtUtc,
        x.ApprovedByPlatformUserId, x.ApprovedAtUtc
    };

    private async Task<T> InTransactionAsync<T>(Func<Task<T>> operation, CancellationToken ct)
    {
        if (!db.Database.IsRelational()) return await operation();
        var strategy = db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            db.ChangeTracker.Clear();
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            var result = await operation();
            await transaction.CommitAsync(ct);
            return result;
        });
    }
}

public sealed record ProposeTaxRuleRequest(
    [Required, StringLength(64)] string JurisdictionCode,
    [Required, StringLength(2, MinimumLength = 2)] string BuyerCountryCode,
    [Required, StringLength(3, MinimumLength = 3)] string Currency,
    [Required, StringLength(128)] string Treatment,
    [Range(0, 100)] decimal RatePercent,
    [Required, StringLength(1000)] string LegalAuthorityReference,
    [Required, StringLength(64, MinimumLength = 64)] string EvidenceSha256,
    DateTime EffectiveFromUtc,
    DateTime? EffectiveToUtc);
