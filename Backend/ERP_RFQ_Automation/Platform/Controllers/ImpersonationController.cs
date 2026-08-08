using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Platform.Auth;
using ERP_RFQ_Automation.Platform.Models;
using ERP_RFQ_Automation.Platform.Services;
using ERP_RFQ_Automation.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace ERP_RFQ_Automation.Platform.Controllers;

/// <summary>
/// Support impersonation. Mints a short-lived, read-only-by-default TENANT token
/// stamped <c>act_sub</c> / <c>impersonated=true</c>; fully audited with both
/// actors. Every issued token is recorded as a revocable
/// <see cref="ImpersonationSession"/> keyed by its <c>jti</c>, enforced by
/// <c>ReadOnlyImpersonationMiddleware</c>. The token is a tenant token (audience
/// "RFQ") so it grants NO platform-plane access. (ADR-0005 §3)
/// </summary>
[ApiController]
[Route("api/platform")]
[Authorize(Policy = PlatformPolicies.PlatformScope)]
public class ImpersonationController : ControllerBase
{
    private readonly ErpRfqAutomationContext _context;
    private readonly IPlatformAuthService _authService;
    private readonly IPlatformAuditService _audit;
    private readonly IMemoryCache? _revocationCache;

    public ImpersonationController(
        ErpRfqAutomationContext context, IPlatformAuthService authService, IPlatformAuditService audit,
        IMemoryCache? revocationCache = null)
    {
        _context = context;
        _authService = authService;
        _audit = audit;
        _revocationCache = revocationCache;
    }

    // POST /api/platform/tenants/{id}/impersonate
    [HttpPost("tenants/{id:long}/impersonate")]
    [Authorize(Policy = PlatformPolicies.Impersonate)]
    public async Task<ActionResult<ImpersonationResponse>> Impersonate(
        long id, [FromBody] ImpersonationRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request?.Reason))
            return BadRequest(new { error = "A reason is required to impersonate." });

        var tenant = await _context.Set<Tenant>().IgnoreQueryFilters().AsNoTracking()
            .FirstOrDefaultAsync(t => t.Id == id, ct);
        if (tenant is null)
            return NotFound();

        if (tenant.Status != TenantStatus.Active)
            return Conflict(new { error = $"Cannot impersonate into a {tenant.Status} tenant." });

        if (tenant.PrimaryBusinessUnitId is not long buId)
            return Conflict(new { error = "Tenant has no primary business unit to scope the session to." });

        var subClaim = User.FindFirst("sub")?.Value
                       ?? User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
        long.TryParse(subClaim, out var actingPlatformUserId);

        var issuedAt = DateTime.UtcNow;
        var (token, expires, jti) = _authService.IssueImpersonationToken(
            actingPlatformUserId, tenant, buId, request.Reason);

        // Sec3: the revocable session row and its audit record commit ATOMICALLY, and
        // the token leaves the process only after that commit — there is no window in
        // which a live token exists without its jti row (middleware would reject it
        // as unknown) or without an audit trail.
        var strategy = _context.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            _context.ChangeTracker.Clear();
            await using var tx = await _context.Database.BeginTransactionAsync(ct);

            _context.Set<ImpersonationSession>().Add(new ImpersonationSession
            {
                Jti = jti,
                TenantId = tenant.Id,
                ActorPlatformUserId = actingPlatformUserId,
                Reason = request.Reason,
                IssuedAtUtc = issuedAt,
                ExpiresAtUtc = expires
            });
            await _context.SaveChangesAsync(ct);

            await _audit.WriteAsync(User, "impersonate.issue", nameof(Tenant), tenant.Id.ToString(),
                new { tenant.Slug, businessUnitId = buId, reason = request.Reason, readOnly = true, expiresAtUtc = expires, jti },
                actAsTenantId: tenant.Id, httpContext: HttpContext, ct: ct);

            await tx.CommitAsync(ct);
        });

        return Ok(new ImpersonationResponse
        {
            TenantId = tenant.Id,
            BusinessUnitId = buId,
            Token = token,
            ReadOnly = true,
            ExpiresAtUtc = expires,
            Jti = jti
        });
    }

    // GET /api/platform/impersonation/sessions — active + recent (last 7 days)
    [HttpGet("impersonation/sessions")]
    [Authorize(Policy = PlatformPolicies.Impersonate)]
    public async Task<ActionResult<IEnumerable<ImpersonationSessionDto>>> Sessions(CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var recentCutoff = now.AddDays(-7);

        var sessions = await _context.Set<ImpersonationSession>().AsNoTracking()
            .Where(s => s.IssuedAtUtc >= recentCutoff ||
                        (s.RevokedAtUtc == null && s.ExpiresAtUtc > now))
            .OrderByDescending(s => s.IssuedAtUtc)
            .Take(200)
            .ToListAsync(ct);

        var tenantIds = sessions.Select(s => s.TenantId).Distinct().ToArray();
        var actorIds = sessions.Select(s => s.ActorPlatformUserId).Distinct().ToArray();
        var tenants = await _context.Set<Tenant>().IgnoreQueryFilters().AsNoTracking()
            .Where(t => tenantIds.Contains(t.Id)).ToDictionaryAsync(t => t.Id, ct);
        var actors = await _context.Set<PlatformUser>().AsNoTracking()
            .Where(u => actorIds.Contains(u.Id)).ToDictionaryAsync(u => u.Id, ct);

        return Ok(sessions.Select(s => ToDto(s, now,
            tenants.TryGetValue(s.TenantId, out var tenant) ? tenant : null,
            actors.TryGetValue(s.ActorPlatformUserId, out var actor) ? actor : null)));
    }

    // POST /api/platform/impersonation/{jti}/revoke
    [HttpPost("impersonation/{jti}/revoke")]
    [Authorize(Policy = PlatformPolicies.Impersonate)]
    public async Task<ActionResult<ImpersonationSessionDto>> Revoke(string jti, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(jti))
            return BadRequest(new { error = "A session jti is required." });

        var session = await _context.Set<ImpersonationSession>().AsNoTracking()
            .FirstOrDefaultAsync(s => s.Jti == jti, ct);
        if (session is null)
            return NotFound();

        var now = DateTime.UtcNow;
        if (session.RevokedAtUtc is null)
        {
            var strategy = _context.Database.CreateExecutionStrategy();
            session = await strategy.ExecuteAsync(async () =>
            {
                _context.ChangeTracker.Clear();
                await using var transaction = await _context.Database.BeginTransactionAsync(ct);
                var current = await _context.Set<ImpersonationSession>()
                    .SingleAsync(value => value.Jti == jti, ct);
                if (current.RevokedAtUtc is null)
                {
                    current.RevokedAtUtc = now;
                    current.RevokedBy = User.FindFirst("email")?.Value ?? "platform";
                    await _context.SaveChangesAsync(ct);
                    await _audit.WriteAsync(User, "impersonate.revoke", nameof(ImpersonationSession), current.Jti,
                        new { current.TenantId, current.ActorPlatformUserId, revokedAtUtc = current.RevokedAtUtc },
                        actAsTenantId: current.TenantId, httpContext: HttpContext, ct: ct);
                }
                await transaction.CommitAsync(ct);
                return current;
            });

            // P2-A12: evict the middleware's cached validity decision so revocation is
            // IMMEDIATE on this node; other instances converge within the 30s TTL.
            if (_revocationCache is not null)
                ReadOnlyImpersonationMiddleware.EvictSession(_revocationCache, session.Jti);
        }

        return Ok(ToDto(session, now, null, null));
    }

    private static ImpersonationSessionDto ToDto(
        ImpersonationSession s, DateTime now, Tenant? tenant, PlatformUser? actor) => new()
    {
        Jti = s.Jti,
        TenantId = s.TenantId,
        TenantName = tenant?.Name,
        ActorPlatformUserId = s.ActorPlatformUserId,
        ActorEmail = actor?.Email,
        Reason = s.Reason,
        IssuedAtUtc = s.IssuedAtUtc,
        ExpiresAtUtc = s.ExpiresAtUtc,
        RevokedAtUtc = s.RevokedAtUtc,
        RevokedBy = s.RevokedBy,
        Status = s.RevokedAtUtc != null ? "revoked" : s.ExpiresAtUtc <= now ? "expired" : "active"
    };
}
