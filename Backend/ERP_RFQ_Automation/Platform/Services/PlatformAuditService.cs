using System.Security.Claims;
using System.Text.Json;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Platform.Auth;
using ERP_RFQ_Automation.Platform.Models;
using System.IdentityModel.Tokens.Jwt;

namespace ERP_RFQ_Automation.Platform.Services;

public interface IPlatformAuditService
{
    /// <summary>Append one immutable audit record (result = "success"). Persistence failures propagate to the caller.</summary>
    Task WriteAsync(
        ClaimsPrincipal actor,
        string action,
        string? targetType = null,
        string? targetId = null,
        object? metadata = null,
        long? actAsTenantId = null,
        HttpContext? httpContext = null,
        CancellationToken ct = default);

    /// <summary>
    /// Append one immutable audit record with an explicit outcome
    /// (<see cref="PlatformAuditResults.Success"/> / <see cref="PlatformAuditResults.Failure"/>).
    /// The default interface implementation delegates to the legacy overload so existing
    /// implementations (including test doubles) remain source-compatible.
    /// </summary>
    Task WriteAsync(
        ClaimsPrincipal actor,
        string action,
        string? targetType,
        string? targetId,
        object? metadata,
        long? actAsTenantId,
        HttpContext? httpContext,
        string result,
        CancellationToken ct = default)
        => WriteAsync(actor, action, targetType, targetId, metadata, actAsTenantId, httpContext, ct);
}

/// <summary>
/// Writes append-only <see cref="PlatformAuditLog"/> rows for privileged actions.
/// Resolves the acting platform user from the "Platform" token's <c>sub</c> claim.
///
/// ACTOR INVARIANT: a SUCCESS record always requires a positive platform actor id —
/// there is no anonymous privileged mutation. The single deliberate relaxation is
/// FAILURE records for pre-authentication events (e.g. <c>platform.login.failed</c>),
/// which have no authenticated actor and are attributed to the reserved system actor
/// <see cref="SystemActorId"/> (0). No <c>PlatformUsers</c> row ever has id 0.
/// (ADR-0005 §3, §4)
/// </summary>
public class PlatformAuditService : IPlatformAuditService
{
    /// <summary>Reserved pseudo-actor for unauthenticated FAILURE records only.</summary>
    public const long SystemActorId = 0;

    private readonly ErpRfqAutomationContext _context;
    private readonly ILogger<PlatformAuditService> _logger;

    public PlatformAuditService(ErpRfqAutomationContext context, ILogger<PlatformAuditService> logger)
    {
        _context = context;
        _logger = logger;
    }

    public Task WriteAsync(
        ClaimsPrincipal actor,
        string action,
        string? targetType = null,
        string? targetId = null,
        object? metadata = null,
        long? actAsTenantId = null,
        HttpContext? httpContext = null,
        CancellationToken ct = default)
        => WriteAsync(actor, action, targetType, targetId, metadata, actAsTenantId, httpContext,
            PlatformAuditResults.Success, ct);

    public async Task WriteAsync(
        ClaimsPrincipal actor,
        string action,
        string? targetType,
        string? targetId,
        object? metadata,
        long? actAsTenantId,
        HttpContext? httpContext,
        string result,
        CancellationToken ct = default)
    {
        try
        {
            var normalizedResult = NormalizeResult(result);

            var sub = actor.FindFirst(JwtRegisteredClaimNames.Sub)?.Value
                      ?? actor.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            long actorId;
            if (long.TryParse(sub, out var parsed) && parsed > 0)
            {
                actorId = parsed;
            }
            else if (normalizedResult == PlatformAuditResults.Failure)
            {
                // Deliberate, documented relaxation of the positive-actor invariant:
                // only FAILURE records may lack an authenticated platform actor
                // (pre-auth events such as failed platform logins). They are pinned
                // to the reserved system actor 0 so they remain queryable and can
                // never masquerade as a real operator.
                actorId = SystemActorId;
            }
            else
            {
                throw new InvalidOperationException(
                    "A valid platform actor identifier is required to write a privileged audit record.");
            }

            var entry = new PlatformAuditLog
            {
                ActorPlatformUserId = actorId,
                ActAsTenantId = actAsTenantId,
                Action = action,
                TargetType = targetType,
                TargetId = targetId,
                Metadata = metadata is null ? null : JsonSerializer.Serialize(metadata),
                Ip = httpContext?.Connection?.RemoteIpAddress?.ToString(),
                Result = normalizedResult,
                CreatedOn = DateTime.UtcNow
            };

            _context.Set<PlatformAuditLog>().Add(entry);
            await _context.SaveChangesAsync(ct);
        }
        catch (Exception)
        {
            _logger.LogError("Failed to write platform audit log for action {Action}", action);
            throw;
        }
    }

    private static string NormalizeResult(string? result)
    {
        var normalized = string.IsNullOrWhiteSpace(result)
            ? PlatformAuditResults.Success
            : result.Trim().ToLowerInvariant();
        if (normalized is not (PlatformAuditResults.Success or PlatformAuditResults.Failure))
            throw new InvalidOperationException(
                $"Audit result must be '{PlatformAuditResults.Success}' or '{PlatformAuditResults.Failure}'.");
        return normalized;
    }
}
