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
    /// platform token FAILS on the tenant scheme. Production requires a dedicated
    /// <c>Jwt:PlatformKey</c>; Development and Testing may fall back to
    /// <c>Jwt:Key</c> to preserve local development usability.
    /// </summary>
    public static AuthenticationBuilder AddPlatformJwtBearer(
        this AuthenticationBuilder builder, IConfiguration config)
    {
        var environment = config["ASPNETCORE_ENVIRONMENT"]
                          ?? config["DOTNET_ENVIRONMENT"]
                          ?? config[HostDefaults.EnvironmentKey]
                          ?? Environments.Production;
        var allowsLocalFallback = environment.Equals(Environments.Development, StringComparison.OrdinalIgnoreCase)
                                  || environment.Equals("Testing", StringComparison.OrdinalIgnoreCase);
        var signingKey = config["Jwt:PlatformKey"];

        if (!allowsLocalFallback)
        {
            if (string.IsNullOrWhiteSpace(signingKey))
                throw new InvalidOperationException(
                    "Jwt:PlatformKey is required outside Development and Testing and cannot fall back to Jwt:Key.");

            var tenantSigningKey = config["Jwt:Key"];
            if (!string.IsNullOrWhiteSpace(tenantSigningKey)
                && string.Equals(signingKey, tenantSigningKey, StringComparison.Ordinal))
                throw new InvalidOperationException(
                    "Jwt:PlatformKey must be distinct from Jwt:Key outside Development and Testing.");
        }
        else if (string.IsNullOrWhiteSpace(signingKey))
        {
            signingKey = config["Jwt:Key"];
        }

        if (string.IsNullOrWhiteSpace(signingKey) || Encoding.UTF8.GetByteCount(signingKey) < 32)
            throw new InvalidOperationException(
                $"The platform JWT signing key for {environment} is missing or shorter than 256 bits (32 bytes).");

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
