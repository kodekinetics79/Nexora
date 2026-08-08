using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Platform.Models;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.Platform.Auth;

public interface IPlatformSessionValidator
{
    Task<bool> IsCurrentAsync(ClaimsPrincipal principal, CancellationToken ct = default);
}

/// <summary>
/// Request-time validation for normal platform JWTs. Signature and lifetime are
/// still owned by JwtBearer; this gate adds live account, role, generation, and
/// revocation checks against the control-plane database.
/// </summary>
public sealed class PlatformSessionValidator : IPlatformSessionValidator
{
    private readonly ErpRfqAutomationContext _context;

    public PlatformSessionValidator(ErpRfqAutomationContext context) => _context = context;

    public async Task<bool> IsCurrentAsync(ClaimsPrincipal principal, CancellationToken ct = default)
    {
        var sub = principal.FindFirst(JwtRegisteredClaimNames.Sub)?.Value
                  ?? principal.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        var jti = principal.FindFirst(JwtRegisteredClaimNames.Jti)?.Value;
        var role = principal.FindFirst(PlatformAuthConstants.PlatformRoleClaim)?.Value;
        var generationValue = principal.FindFirst(PlatformAuthConstants.SessionGenerationClaim)?.Value;
        var mfaMethod = principal.FindFirst(PlatformAuthConstants.AuthenticationMethodClaim)?.Value;
        var mfaTimeValue = principal.FindFirst(PlatformAuthConstants.MfaAuthenticatedAtClaim)?.Value;

        if (!long.TryParse(sub, out var platformUserId) || platformUserId <= 0
            || string.IsNullOrWhiteSpace(jti) || string.IsNullOrWhiteSpace(role)
            || !long.TryParse(generationValue, out var generation) || generation <= 0)
            return false;

        var now = DateTime.UtcNow;
        var state = await _context.Set<PlatformSession>().AsNoTracking()
            .Where(session => session.Jti == jti
                              && session.PlatformUserId == platformUserId
                              && session.SessionGeneration == generation
                              && session.RevokedAtUtc == null
                              && session.ExpiresAtUtc > now)
            .Select(session => new
            {
                session.PlatformUser.IsActive,
                session.PlatformUser.PlatformRole,
                session.PlatformUser.SessionGeneration,
                session.MfaAuthenticatedAtUtc
            })
            .SingleOrDefaultAsync(ct);

        if (state is null || !state.IsActive || state.SessionGeneration != generation
            || !string.Equals(state.PlatformRole.ToString(), role, StringComparison.Ordinal))
            return false;

        if (!string.Equals(mfaMethod, PlatformAuthConstants.MfaAuthenticationMethod, StringComparison.Ordinal))
            return state.MfaAuthenticatedAtUtc is null && string.IsNullOrWhiteSpace(mfaTimeValue);
        if (state.MfaAuthenticatedAtUtc is not DateTime serverMfaAt
            || !long.TryParse(mfaTimeValue, out var mfaEpoch))
            return false;
        var claimedMfaAt = DateTimeOffset.FromUnixTimeSeconds(mfaEpoch).UtcDateTime;
        return Math.Abs((serverMfaAt - claimedMfaAt).TotalSeconds) < 1;
    }
}
