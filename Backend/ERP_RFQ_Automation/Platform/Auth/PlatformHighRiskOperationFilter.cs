using System.IdentityModel.Tokens.Jwt;
using ERP_RFQ_Automation.Platform.Models;
using ERP_RFQ_Automation.Platform.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.Platform.Auth;

/// <summary>
/// Marks an operation that must never be reachable from a platform session which has proved nothing
/// but a password.
///
/// <para><b>What it adds, and what it deliberately does not.</b> On an ordinary MFA-bound session it
/// adds nothing at all: the second factor is the step-up, the existing typed confirmations and
/// approval controls are untouched, and no request contract changes. It only engages when the
/// session carries no <c>amr=mfa</c> — which is reachable exclusively while the server-authoritative
/// policy has relaxed enforcement, and therefore exclusively on non-production infrastructure. There
/// it demands a password re-authentication inside a short window.</para>
///
/// <para><b>Why this shape.</b> The alternative was a password field on every destructive request
/// body, which would have meant editing the contract of the purge, the export, the legal-hold
/// release and the invoice finalisation — and would have put a plaintext password into four more
/// request logs. An attribute plus a session-side timestamp adds the control to a new operation in
/// one line and keeps the password on exactly one endpoint.</para>
///
/// <para><b>Why it is not simply "always require step-up".</b> Because a control that fires on every
/// destructive action in normal operation is a control operators route around. The threat being
/// answered here is specific: with MFA enforcement relaxed, a stolen platform password alone would
/// otherwise reach a tenant purge.</para>
/// </summary>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Class, AllowMultiple = false)]
public sealed class PlatformHighRiskOperationAttribute : Attribute, IFilterFactory
{
    public PlatformHighRiskOperationAttribute(string operation) => Operation = operation;

    /// <summary>Stable name for the audit record, e.g. <c>tenant.purge</c>.</summary>
    public string Operation { get; }

    public bool IsReusable => false;

    public IFilterMetadata CreateInstance(IServiceProvider serviceProvider) =>
        new PlatformHighRiskOperationFilter(
            Operation,
            serviceProvider.GetRequiredService<ERP_RFQ_Automation.Models.ErpRfqAutomationContext>(),
            serviceProvider.GetRequiredService<PlatformMfaPolicyOptions>(),
            serviceProvider.GetRequiredService<IPlatformAuditService>());
}

/// <summary>Enforces <see cref="PlatformHighRiskOperationAttribute"/>. See that type for why.</summary>
public sealed class PlatformHighRiskOperationFilter : IAsyncAuthorizationFilter
{
    public const string ReauthenticationRequiredAction = "platform.security.reauth.required";

    private readonly string _operation;
    private readonly ERP_RFQ_Automation.Models.ErpRfqAutomationContext _context;
    private readonly PlatformMfaPolicyOptions _options;
    private readonly IPlatformAuditService _audit;

    public PlatformHighRiskOperationFilter(
        string operation,
        ERP_RFQ_Automation.Models.ErpRfqAutomationContext context,
        PlatformMfaPolicyOptions options,
        IPlatformAuditService audit)
    {
        _operation = operation;
        _context = context;
        _options = options;
        _audit = audit;
    }

    public async Task OnAuthorizationAsync(AuthorizationFilterContext context)
    {
        var user = context.HttpContext.User;

        // An MFA-bound session already carries a second factor. Nothing changes for it — this is what
        // keeps production behaviour, and every existing journey through these endpoints, identical.
        if (user.HasClaim(PlatformAuthConstants.AuthenticationMethodClaim,
                PlatformAuthConstants.MfaAuthenticationMethod))
            return;

        var jti = user.FindFirst(JwtRegisteredClaimNames.Jti)?.Value;
        var ct = context.HttpContext.RequestAborted;
        var now = DateTime.UtcNow;
        var floor = now - _options.PasswordReauthWindow;

        var proved = !string.IsNullOrWhiteSpace(jti)
                     && await _context.Set<PlatformSession>().AsNoTracking()
                         .AnyAsync(session => session.Jti == jti
                                              && session.RevokedAtUtc == null
                                              && session.ExpiresAtUtc > now
                                              && session.LastPasswordReauthAtUtc != null
                                              && session.LastPasswordReauthAtUtc > floor, ct);
        if (proved) return;

        await _audit.WriteAsync(user, ReauthenticationRequiredAction, "PlatformSession", jti,
            new
            {
                operation = _operation,
                environment = _options.EnvironmentName,
                environmentClass = _options.EnvironmentClass.ToString(),
                windowMinutes = _options.PasswordReauthWindowMinutes,
                correlationId = context.HttpContext.TraceIdentifier
            },
            actAsTenantId: null, context.HttpContext, PlatformAuditResults.Failure, ct);

        // 403 with the remedy named. A bare "Forbidden" on a purge, from a session that IS an Owner,
        // is the kind of refusal an operator reports as a broken screen.
        context.Result = new ObjectResult(new
        {
            error = "This operation needs you to re-enter your platform password first. MFA enforcement is " +
                    "relaxed on this deployment, so a password-only session must step up before a high-risk " +
                    "action. POST your current password to /api/platform/auth/reauthenticate, then retry " +
                    $"within {_options.PasswordReauthWindowMinutes} minutes.",
            reauthenticationRequired = true,
            operation = _operation
        })
        {
            StatusCode = StatusCodes.Status403Forbidden
        };
    }
}
