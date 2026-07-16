using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Platform.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;

namespace ERP_RFQ_Automation.Platform.Auth;

public interface IPlatformAuthService
{
    Task<PlatformLoginResponse> LoginAsync(PlatformLoginRequest request);

    /// <summary>
    /// Mint the short-lived, read-only TENANT token used for support impersonation.
    /// It is signed/validated by the tenant scheme (audience "RFQ") — NOT the
    /// platform scheme — and is stamped act_sub / impersonated / read-only.
    /// </summary>
    (string Token, DateTime ExpiresAtUtc) IssueImpersonationToken(
        long actingPlatformUserId, Tenant tenant, long businessUnitId, string reason);
}

/// <summary>
/// Issues platform tokens (audience <c>nexora-platform</c>, <c>scope=platform</c>,
/// <c>platformRole</c>, and NO tenant claim) and the short-lived read-only tenant
/// token for impersonation. Mirrors the JWT construction style of
/// <c>AuthRepository</c>. (ADR-0005 §3)
/// </summary>
public class PlatformAuthService : IPlatformAuthService
{
    private readonly ErpRfqAutomationContext _context;
    private readonly IConfiguration _config;
    private readonly ILogger<PlatformAuthService> _logger;

    public PlatformAuthService(
        ErpRfqAutomationContext context, IConfiguration config, ILogger<PlatformAuthService> logger)
    {
        _context = context;
        _config = config;
        _logger = logger;
    }

    public async Task<PlatformLoginResponse> LoginAsync(PlatformLoginRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Email) || string.IsNullOrWhiteSpace(request.Password))
            throw new UnauthorizedAccessException("Email and password are required.");

        var user = await _context.Set<PlatformUser>()
            .AsNoTracking()
            .FirstOrDefaultAsync(u => u.Email.ToLower() == request.Email.ToLower());

        if (user == null)
        {
            _logger.LogWarning("Platform login: user not found for {Email}", request.Email);
            throw new UnauthorizedAccessException("Invalid email or password.");
        }

        if (!BCrypt.Net.BCrypt.Verify(request.Password, user.PasswordHash))
        {
            _logger.LogWarning("Platform login: bad password for {Email}", request.Email);
            throw new UnauthorizedAccessException("Invalid email or password.");
        }

        if (!user.IsActive)
        {
            _logger.LogWarning("Platform login: inactive account {Email}", request.Email);
            throw new UnauthorizedAccessException("Platform account is not active.");
        }

        // Best-effort LastLogin bump (tracked update on a fresh entity).
        try
        {
            var tracked = new PlatformUser { Id = user.Id };
            _context.Set<PlatformUser>().Attach(tracked);
            tracked.LastLogin = DateTime.UtcNow;
            _context.Entry(tracked).Property(u => u.LastLogin).IsModified = true;
            await _context.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Platform login: could not update LastLogin for {Email}", request.Email);
        }

        var (token, expires) = GeneratePlatformToken(user);
        _logger.LogInformation("Platform login successful for {Email} ({Role})", user.Email, user.PlatformRole);

        return new PlatformLoginResponse
        {
            Id = user.Id,
            Email = user.Email,
            PlatformRole = user.PlatformRole.ToString(),
            Token = token,
            ExpiresAtUtc = expires
        };
    }

    private (string Token, DateTime ExpiresAtUtc) GeneratePlatformToken(PlatformUser user)
    {
        var signingKey = _config["Jwt:PlatformKey"];
        if (string.IsNullOrWhiteSpace(signingKey))
            signingKey = _config["Jwt:Key"];

        var issuer = _config["Jwt:PlatformIssuer"] ?? _config["Jwt:Issuer"];
        var audience = _config["Jwt:PlatformAudience"] ?? PlatformAuthConstants.Audience;
        var minutes = double.TryParse(_config["Jwt:PlatformExpiryMinutes"], out var m) ? m : 30;

        // NOTE: deliberately NO businessUnitId / tenantId / roleId claim — a platform
        // token must never satisfy the tenant scope or tenant RBAC handlers.
        var claims = new[]
        {
            new Claim(JwtRegisteredClaimNames.Sub, user.Id.ToString()),
            new Claim(JwtRegisteredClaimNames.Email, user.Email),
            new Claim(PlatformAuthConstants.ScopeClaim, PlatformAuthConstants.PlatformScopeValue),
            new Claim(PlatformAuthConstants.PlatformRoleClaim, user.PlatformRole.ToString()),
            new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString())
        };

        var creds = new SigningCredentials(
            new SymmetricSecurityKey(Encoding.UTF8.GetBytes(signingKey!)), SecurityAlgorithms.HmacSha256);
        var expires = DateTime.UtcNow.AddMinutes(minutes);

        var token = new JwtSecurityToken(
            issuer: issuer, audience: audience, claims: claims, expires: expires, signingCredentials: creds);

        return (new JwtSecurityTokenHandler().WriteToken(token), expires);
    }

    public (string Token, DateTime ExpiresAtUtc) IssueImpersonationToken(
        long actingPlatformUserId, Tenant tenant, long businessUnitId, string reason)
    {
        // Impersonation mints a TENANT token: tenant signing key + tenant audience
        // so it is accepted by the DEFAULT scheme and scoped by the tenant query
        // filters (businessUnitId claim). It carries NO platform scope, and NO
        // roleId — so tenant RBAC permission checks (which require roleId) fail,
        // making the session effectively read-only. (ADR-0005 §3)
        var signingKey = _config["Jwt:Key"];
        var issuer = _config["Jwt:Issuer"];
        var audience = _config["Jwt:Audience"];
        var minutes = double.TryParse(_config["Jwt:ImpersonationExpiryMinutes"], out var m) ? m : 15;

        var claims = new[]
        {
            new Claim(JwtRegisteredClaimNames.Sub, $"impersonation:{tenant.Id}"),
            new Claim("businessUnitId", businessUnitId.ToString()),
            new Claim(PlatformAuthConstants.ScopeClaim, PlatformAuthConstants.TenantScopeValue),
            new Claim(PlatformAuthConstants.ActSubClaim, actingPlatformUserId.ToString()),
            new Claim(PlatformAuthConstants.ImpersonatedClaim, "true"),
            new Claim(PlatformAuthConstants.ReadOnlyClaim, "true"),
            new Claim(PlatformAuthConstants.ImpersonationReasonClaim, reason),
            new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString())
        };

        var creds = new SigningCredentials(
            new SymmetricSecurityKey(Encoding.UTF8.GetBytes(signingKey!)), SecurityAlgorithms.HmacSha256);
        var expires = DateTime.UtcNow.AddMinutes(minutes);

        var token = new JwtSecurityToken(
            issuer: issuer, audience: audience, claims: claims, expires: expires, signingCredentials: creds);

        return (new JwtSecurityTokenHandler().WriteToken(token), expires);
    }
}
