using System.Text;
using ERP_RFQ_Automation.Platform.Models;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.IdentityModel.Tokens;

namespace ERP_RFQ_Automation.Platform.Auth;

/// <summary>
/// Central registration helpers for the platform security boundary so Program.cs
/// changes stay to two one-liners (see WIRING.md). Registers the SECOND JWT
/// bearer scheme and the default-deny <c>PlatformScope</c> policy + role
/// sub-policies. (ADR-0005 §3)
/// </summary>
public static class PlatformAuthExtensions
{
    /// <summary>
    /// Adds the "Platform" JWT bearer scheme. It validates audience
    /// <c>nexora-platform</c> so a tenant token (audience "RFQ") FAILS here, and a
    /// platform token FAILS on the tenant scheme. Uses a dedicated signing key
    /// <c>Jwt:PlatformKey</c> when configured (recommended), else falls back to
    /// <c>Jwt:Key</c> — the audience check alone already enforces the boundary.
    /// </summary>
    public static AuthenticationBuilder AddPlatformJwtBearer(
        this AuthenticationBuilder builder, IConfiguration config)
    {
        var signingKey = config["Jwt:PlatformKey"];
        if (string.IsNullOrWhiteSpace(signingKey))
            signingKey = config["Jwt:Key"];

        if (string.IsNullOrWhiteSpace(signingKey) || Encoding.UTF8.GetByteCount(signingKey) < 32)
            throw new InvalidOperationException(
                "Jwt:PlatformKey (or fallback Jwt:Key) is missing or shorter than 256 bits (32 bytes).");

        var issuer = config["Jwt:PlatformIssuer"] ?? config["Jwt:Issuer"] ?? "";
        var audience = config["Jwt:PlatformAudience"] ?? PlatformAuthConstants.Audience;

        return builder.AddJwtBearer(PlatformAuthConstants.Scheme, options =>
        {
            options.TokenValidationParameters = new TokenValidationParameters
            {
                ValidateIssuer = true,
                ValidateAudience = true,
                ValidateLifetime = true,
                ValidateIssuerSigningKey = true,
                ValidIssuer = issuer,
                ValidAudience = audience,
                IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(signingKey))
            };
        });
    }

    /// <summary>
    /// Registers the default-deny <c>PlatformScope</c> policy and role sub-policies.
    /// Every policy pins the "Platform" scheme, so a tenant token is never even
    /// authenticated against them (second + first gate combined), and requires the
    /// <c>scope=platform</c> claim.
    /// </summary>
    public static void AddPlatformPolicies(this AuthorizationOptions options)
    {
        options.AddPolicy(PlatformPolicies.PlatformScope, p => p
            .AddAuthenticationSchemes(PlatformAuthConstants.Scheme)
            .RequireAuthenticatedUser()
            .RequireClaim(PlatformAuthConstants.ScopeClaim, PlatformAuthConstants.PlatformScopeValue));

        options.AddPolicy(PlatformPolicies.Owner, p => p
            .AddAuthenticationSchemes(PlatformAuthConstants.Scheme)
            .RequireAuthenticatedUser()
            .RequireClaim(PlatformAuthConstants.ScopeClaim, PlatformAuthConstants.PlatformScopeValue)
            .RequireClaim(PlatformAuthConstants.PlatformRoleClaim, nameof(PlatformRole.Owner)));

        options.AddPolicy(PlatformPolicies.TenantAdmin, p => p
            .AddAuthenticationSchemes(PlatformAuthConstants.Scheme)
            .RequireAuthenticatedUser()
            .RequireClaim(PlatformAuthConstants.ScopeClaim, PlatformAuthConstants.PlatformScopeValue)
            .RequireClaim(PlatformAuthConstants.PlatformRoleClaim,
                nameof(PlatformRole.Owner), nameof(PlatformRole.SupportAdmin)));

        options.AddPolicy(PlatformPolicies.Billing, p => p
            .AddAuthenticationSchemes(PlatformAuthConstants.Scheme)
            .RequireAuthenticatedUser()
            .RequireClaim(PlatformAuthConstants.ScopeClaim, PlatformAuthConstants.PlatformScopeValue)
            .RequireClaim(PlatformAuthConstants.PlatformRoleClaim,
                nameof(PlatformRole.Owner), nameof(PlatformRole.BillingAdmin)));

        options.AddPolicy(PlatformPolicies.Impersonate, p => p
            .AddAuthenticationSchemes(PlatformAuthConstants.Scheme)
            .RequireAuthenticatedUser()
            .RequireClaim(PlatformAuthConstants.ScopeClaim, PlatformAuthConstants.PlatformScopeValue)
            .RequireClaim(PlatformAuthConstants.PlatformRoleClaim,
                nameof(PlatformRole.Owner), nameof(PlatformRole.SupportAdmin)));
    }
}
