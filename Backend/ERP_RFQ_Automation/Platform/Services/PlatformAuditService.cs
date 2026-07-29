using System.Security.Claims;
using System.Text.Json;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Platform.Auth;
using ERP_RFQ_Automation.Platform.Models;
using System.IdentityModel.Tokens.Jwt;

namespace ERP_RFQ_Automation.Platform.Services;

public interface IPlatformAuditService
{
    /// <summary>Append one immutable audit record. Persistence failures propagate to the caller.</summary>
    Task WriteAsync(
        ClaimsPrincipal actor,
        string action,
        string? targetType = null,
        string? targetId = null,
        object? metadata = null,
        long? actAsTenantId = null,
        HttpContext? httpContext = null,
        CancellationToken ct = default);
}

/// <summary>
/// Writes append-only <see cref="PlatformAuditLog"/> rows for privileged actions.
/// Resolves the acting platform user from the "Platform" token's <c>sub</c> claim.
/// (ADR-0005 §3, §4)
/// </summary>
public class PlatformAuditService : IPlatformAuditService
{
    private readonly ErpRfqAutomationContext _context;
    private readonly ILogger<PlatformAuditService> _logger;

    public PlatformAuditService(ErpRfqAutomationContext context, ILogger<PlatformAuditService> logger)
    {
        _context = context;
        _logger = logger;
    }

    public async Task WriteAsync(
        ClaimsPrincipal actor,
        string action,
        string? targetType = null,
        string? targetId = null,
        object? metadata = null,
        long? actAsTenantId = null,
        HttpContext? httpContext = null,
        CancellationToken ct = default)
    {
        try
        {
            var sub = actor.FindFirst(JwtRegisteredClaimNames.Sub)?.Value
                      ?? actor.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (!long.TryParse(sub, out var actorId) || actorId <= 0)
                throw new InvalidOperationException(
                    "A valid platform actor identifier is required to write a privileged audit record.");

            var entry = new PlatformAuditLog
            {
                ActorPlatformUserId = actorId,
                ActAsTenantId = actAsTenantId,
                Action = action,
                TargetType = targetType,
                TargetId = targetId,
                Metadata = metadata is null ? null : JsonSerializer.Serialize(metadata),
                Ip = httpContext?.Connection?.RemoteIpAddress?.ToString(),
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
}
