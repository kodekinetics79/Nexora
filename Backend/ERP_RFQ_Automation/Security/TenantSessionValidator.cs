using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.MultiTenancy;
using ERP_RFQ_Automation.Platform.Auth;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace ERP_RFQ_Automation.Security;

/// <summary>
/// The per-account revocation handle for the tenant plane.
///
/// <para><c>Users.SecurityStamp</c> is an opaque random value that changes whenever the account's
/// AUTHORITY changes — deactivation, role change, password change or reset, activation, erasure.
/// Every tenant token carries the stamp that was current when it was issued
/// (<see cref="ClaimType"/>), and <see cref="TenantSessionValidator"/> rejects a token whose stamp
/// no longer matches. That is what turns "deactivated" from a fact the next login will notice
/// into a fact the next REQUEST notices, instead of the previous up-to-60-minute grace.</para>
///
/// <para>Deliberately NOT <c>ModifiedOn</c>: that moves on every profile edit and would log
/// people out for changing their avatar. Deliberately NOT a session table: the platform plane
/// needs one because it tracks MFA and browser trust per session; a tenant token needs only
/// "is the account still what it was when this was minted".</para>
/// </summary>
public static class SecurityStamps
{
    /// <summary>JWT claim type carrying the stamp. Short on purpose; it rides every request.</summary>
    public const string ClaimType = "sst";

    public static string NewStamp() => Guid.NewGuid().ToString("N");
}

/// <summary>
/// Same-process eviction of a cached validation verdict, so an administrator's deactivate takes
/// effect on the victim's very next request rather than within <see cref="TenantSessionValidator.CacheTtl"/>.
/// Cross-instance staleness is bounded by the TTL alone, exactly as for impersonation sessions
/// (<see cref="ReadOnlyImpersonationMiddleware.EvictSession"/>).
/// </summary>
public interface ITenantSessionCache
{
    void Evict(long userId);
}

public interface ITenantSessionValidator
{
    /// <summary>
    /// True when the account behind <paramref name="principal"/> is still the account the token
    /// describes. False is a refusal: JwtBearer turns it into 401.
    /// </summary>
    Task<bool> IsCurrentAsync(ClaimsPrincipal principal, CancellationToken ct = default);
}

/// <summary>
/// Request-time validation for tenant JWTs, wired into the tenant scheme's
/// <c>OnTokenValidated</c> (Program.cs). Signature, issuer, audience and lifetime are still
/// owned by JwtBearer; this adds the live account check.
///
/// <para><b>Legacy tokens.</b> A token with no <see cref="SecurityStamps.ClaimType"/> claim was
/// issued by a build that predates the stamp. It is accepted as-is until it expires (at most the
/// 60-minute lifetime after the deploy that introduced this class) unless
/// <see cref="RequireSecurityStampKey"/> is true, at which point it is refused. The claim cannot
/// be forged — the signature still binds it — so the compat window costs one hour of the OLD
/// behaviour once, in exchange for a release that logs nobody out. Flip the key after that hour.</para>
///
/// <para><b>Impersonation tokens</b> (<c>impersonated=true</c>) have no user row and their own
/// revocation ledger (<see cref="ReadOnlyImpersonationMiddleware"/>); they are exempt here.</para>
///
/// <para><b>Tenant scope, and why the lookup runs in its OWN DI scope.</b> During
/// <c>OnTokenValidated</c> the HttpContext's <c>User</c> is not yet the validated principal.
/// <see cref="HttpTenantContext"/> and <see cref="TenantRlsCommandInterceptor"/> are both
/// request-scoped and both capture the tenant at construction, so resolving the request's
/// <see cref="ErpRfqAutomationContext"/> from here would build them with NO tenant — and every
/// later command in the same request would then run <c>nexora_tenant_app</c> with no
/// <c>nexora.business_unit_id</c>, read zero rows, and turn every permission check into a 403.
/// That is exactly what happened on the first cut of this class (17 authenticated HTTP tests went
/// red). So: push the tenant from the token's own <c>businessUnitId</c> claim onto the ambient
/// <see cref="ITenantScopeAccessor"/>, open a fresh scope whose tenant context therefore resolves
/// to that tenant, do the one read, dispose. The request's own scope is never touched here.</para>
///
/// <para><b>Fails closed</b> on any error, like the platform validator: an account that cannot be
/// checked is not granted.</para>
/// </summary>
public sealed class TenantSessionValidator : ITenantSessionValidator, ITenantSessionCache
{
    /// <summary>
    /// Upper bound on cross-instance staleness. THE SAME constant as the impersonation guard, by
    /// reference and not by value, so the two revocation windows cannot drift apart.
    /// </summary>
    public static readonly TimeSpan CacheTtl = ReadOnlyImpersonationMiddleware.CacheTtl;

    public const string RequireSecurityStampKey = "Auth:RequireSecurityStamp";

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IMemoryCache _cache;
    private readonly ITenantScopeAccessor _tenantScope;
    private readonly bool _requireStamp;
    private readonly ILogger<TenantSessionValidator> _logger;

    public TenantSessionValidator(
        IServiceScopeFactory scopeFactory,
        IMemoryCache cache,
        ITenantScopeAccessor tenantScope,
        IConfiguration configuration,
        ILogger<TenantSessionValidator> logger)
    {
        _scopeFactory = scopeFactory;
        _cache = cache;
        _tenantScope = tenantScope;
        _requireStamp = configuration.GetValue(RequireSecurityStampKey, false);
        _logger = logger;
    }

    public async Task<bool> IsCurrentAsync(ClaimsPrincipal principal, CancellationToken ct = default)
    {
        if (string.Equals(principal.FindFirst(PlatformAuthConstants.ImpersonatedClaim)?.Value, "true",
                StringComparison.OrdinalIgnoreCase))
            return true;

        var stamp = principal.FindFirst(SecurityStamps.ClaimType)?.Value;
        if (string.IsNullOrWhiteSpace(stamp))
        {
            if (_requireStamp)
                _logger.LogWarning("Tenant token without a security stamp refused ({Key}=true).", RequireSecurityStampKey);
            return !_requireStamp;
        }

        var sub = principal.FindFirst(JwtRegisteredClaimNames.Sub)?.Value
                  ?? principal.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (!long.TryParse(sub, out var userId) || userId <= 0)
            return false;
        if (!long.TryParse(principal.FindFirst("businessUnitId")?.Value, out var businessUnitId) || businessUnitId <= 0)
            return false;
        var claimedRole = principal.FindFirst("roleId")?.Value;

        var snapshot = await SnapshotAsync(userId, businessUnitId, ct);
        if (snapshot is null || snapshot.IsActive != true)
            return false;
        if (!string.Equals(snapshot.SecurityStamp, stamp, StringComparison.Ordinal))
            return false;
        // Belt-and-braces: rotation on role change already invalidates the stamp, but a writer
        // that forgets to rotate must not silently keep an old role alive in a live token.
        var currentRole = snapshot.RoleId?.ToString() ?? "none";
        return string.Equals(currentRole, claimedRole ?? "none", StringComparison.Ordinal);
    }

    public void Evict(long userId) => _cache.Remove(CacheKey(userId));

    private async Task<AccountSnapshot?> SnapshotAsync(long userId, long businessUnitId, CancellationToken ct)
    {
        var key = CacheKey(userId);
        if (_cache.TryGetValue(key, out AccountSnapshot? cached))
            return cached;

        using var tenant = _tenantScope.Push(businessUnitId);
        await using var scope = _scopeFactory.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<ErpRfqAutomationContext>();
        var snapshot = await context.Users.IgnoreQueryFilters().AsNoTracking()
            .Where(u => u.Id == userId && u.Buid == businessUnitId)
            .Select(u => new AccountSnapshot(u.IsActive, u.RoleId, u.SecurityStamp))
            .SingleOrDefaultAsync(ct);

        // A missing row is cached too: a deleted account must not cost a query per request.
        _cache.Set(key, snapshot, CacheTtl);
        return snapshot;
    }

    private static string CacheKey(long userId) => $"tenant-session:{userId}";

    private sealed record AccountSnapshot(bool? IsActive, long? RoleId, string SecurityStamp);
}
