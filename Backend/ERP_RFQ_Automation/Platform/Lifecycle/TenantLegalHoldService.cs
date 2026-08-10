using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Platform.Models;
using ERP_RFQ_Automation.Platform.Services;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.Platform.Lifecycle;

/// <summary>
/// Places and releases preservation orders through the tenant-wide legal-hold advisory fence,
/// then takes the offboarding-row lock used by full-tenant purge. The advisory fence also spans
/// evidence-object deletion: hold-first makes retention fail closed; deletion-first keeps the hold
/// request waiting until the deletion and its immutable tombstone are complete.
/// </summary>
public sealed class TenantLegalHoldService(
    ErpRfqAutomationContext context,
    IPlatformAuditService audit)
{
    public const string PlaceAction = "tenant.legal-hold.placed";
    public const string ReleaseAction = "tenant.legal-hold.released";

    public async Task<IReadOnlyList<TenantLegalHoldDto>> ListAsync(long tenantId, CancellationToken ct)
    {
        if (!await context.Set<Tenant>().IgnoreQueryFilters().AnyAsync(t => t.Id == tenantId, ct))
            throw new TenantOffboardingNotFoundException(tenantId);

        var rows = await context.Set<TenantLegalHold>().AsNoTracking()
            .Where(h => h.TenantId == tenantId)
            .OrderByDescending(h => h.ReleasedOn == null)
            .ThenByDescending(h => h.PlacedOn)
            .ToListAsync(ct);
        return rows.Select(ToDto).ToList();
    }

    public async Task<TenantLegalHoldDto> PlaceAsync(
        long tenantId, PlaceTenantLegalHoldRequest request, ClaimsPrincipal actor,
        HttpContext? httpContext, CancellationToken ct)
    {
        var scope = Required(request?.Scope, 3, nameof(request.Scope));
        var authority = Required(request?.Authority, 3, nameof(request.Authority));
        var reason = Required(request?.Reason, TenantOffboardingService.MinimumDestructionReasonLength, nameof(request.Reason));
        var evidence = Required(request?.EvidenceReference, 3, nameof(request.EvidenceReference));
        var tenant = await context.Set<Tenant>().IgnoreQueryFilters()
            .FirstOrDefaultAsync(t => t.Id == tenantId, ct)
            ?? throw new TenantOffboardingNotFoundException(tenantId);

        TenantLegalHold? hold = null;
        await InTransactionAsync(async () =>
        {
            await TenantLegalHoldFence.AcquireTransactionAsync(context, tenantId, ct);
            var offboarding = await LockOffboardingAsync(tenantId, ct);
            if (offboarding.Stage == TenantOffboardingStage.Purged
                || offboarding.PurgeExecutedOn is not null
                || offboarding.PersonalDataErasedOn is not null)
                throw TenantOffboardingRefusedException.Conflict(
                    "A legal hold cannot be placed after destructive execution has started or completed.");

            if (await context.Set<TenantLegalHold>().AnyAsync(
                    h => h.TenantId == tenantId && h.ReleasedOn == null && h.Scope == scope, ct))
                throw TenantOffboardingRefusedException.Conflict(
                    $"Tenant {tenantId} already has an active legal hold for scope '{scope}'.");

            hold = new TenantLegalHold
            {
                TenantId = tenantId,
                Scope = scope,
                Authority = authority,
                Reason = reason,
                EvidenceReference = evidence,
                PlacedOn = DateTime.UtcNow,
                PlacedByPlatformUserId = ActorId(actor),
                PlacedBy = ActorEmail(actor)
            };
            context.Set<TenantLegalHold>().Add(hold);
            await context.SaveChangesAsync(ct);
            await audit.WriteAsync(actor, PlaceAction, nameof(TenantLegalHold), hold.Id.ToString(),
                new { tenantId, scope, authority, reason, evidenceReference = evidence },
                actAsTenantId: tenantId, httpContext: httpContext, ct: ct);
        }, ct);

        return ToDto(hold!);
    }

    public async Task<TenantLegalHoldDto> ReleaseAsync(
        long tenantId, long holdId, ReleaseTenantLegalHoldRequest request, ClaimsPrincipal actor,
        HttpContext? httpContext, CancellationToken ct)
    {
        var reason = Required(request?.Reason, TenantOffboardingService.MinimumDestructionReasonLength, nameof(request.Reason));
        TenantLegalHold? hold = null;
        await InTransactionAsync(async () =>
        {
            await TenantLegalHoldFence.AcquireTransactionAsync(context, tenantId, ct);
            await LockOffboardingAsync(tenantId, ct);
            hold = await context.Set<TenantLegalHold>()
                .SingleOrDefaultAsync(h => h.Id == holdId && h.TenantId == tenantId, ct)
                ?? throw new TenantOffboardingNotFoundException(tenantId);
            if (hold.ReleasedOn is not null)
                throw TenantOffboardingRefusedException.Conflict(
                    $"Legal hold {holdId} was already released on {hold.ReleasedOn:yyyy-MM-dd HH:mm} UTC.");
            var releasingActorId = ActorId(actor);
            if (hold.PlacedByPlatformUserId == releasingActorId)
                throw TenantOffboardingRefusedException.Conflict(
                    "A legal hold must be released by a different platform Owner from the actor who placed it.");

            hold.ReleasedOn = DateTime.UtcNow;
            hold.ReleasedByPlatformUserId = releasingActorId;
            hold.ReleasedBy = ActorEmail(actor);
            hold.ReleaseReason = reason;
            await context.SaveChangesAsync(ct);
            await audit.WriteAsync(actor, ReleaseAction, nameof(TenantLegalHold), hold.Id.ToString(),
                new { tenantId, hold.Scope, reason }, actAsTenantId: tenantId,
                httpContext: httpContext, ct: ct);
        }, ct);

        return ToDto(hold!);
    }

    private async Task<TenantOffboarding> LockOffboardingAsync(long tenantId, CancellationToken ct)
    {
        var record = await context.Set<TenantOffboarding>().SingleOrDefaultAsync(r => r.TenantId == tenantId, ct);
        if (record is null)
        {
            record = new TenantOffboarding { TenantId = tenantId, CreatedOn = DateTime.UtcNow };
            context.Set<TenantOffboarding>().Add(record);
            await context.SaveChangesAsync(ct);
        }

        var lockedOn = DateTime.UtcNow;
        await context.Set<TenantOffboarding>().Where(r => r.TenantId == tenantId)
            .ExecuteUpdateAsync(setters => setters.SetProperty(r => r.ModifiedOn, lockedOn), ct);
        await context.Entry(record).ReloadAsync(ct);
        return record;
    }

    private async Task InTransactionAsync(Func<Task> work, CancellationToken ct)
    {
        var strategy = context.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            context.ChangeTracker.Clear();
            await using var transaction = await context.Database.BeginTransactionAsync(ct);
            await work();
            await transaction.CommitAsync(ct);
        });
    }

    private static TenantLegalHoldDto ToDto(TenantLegalHold h) => new(
        h.Id, h.TenantId, h.Scope, h.Authority, h.Reason, h.EvidenceReference,
        h.PlacedOn, h.PlacedBy, h.ReleasedOn is null, h.ReleasedOn, h.ReleasedBy, h.ReleaseReason);

    private static string Required(string? value, int minimum, string field)
    {
        var trimmed = value?.Trim();
        if (string.IsNullOrWhiteSpace(trimmed) || trimmed.Length < minimum)
            throw TenantOffboardingRefusedException.BadRequest(
                $"{field} must contain at least {minimum} characters.");
        return trimmed;
    }

    private static long ActorId(ClaimsPrincipal actor)
    {
        var value = actor.FindFirst(JwtRegisteredClaimNames.Sub)?.Value
                    ?? actor.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        return long.TryParse(value, out var id) && id > 0
            ? id
            : throw new InvalidOperationException("A valid platform actor identifier is required.");
    }

    private static string ActorEmail(ClaimsPrincipal actor) =>
        actor.FindFirst(JwtRegisteredClaimNames.Email)?.Value
        ?? actor.FindFirst(ClaimTypes.Email)?.Value
        ?? throw new InvalidOperationException("A platform actor email is required.");
}
