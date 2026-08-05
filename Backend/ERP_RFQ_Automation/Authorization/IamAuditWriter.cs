using System.Security.Claims;
using System.Text.Json;
using ERP_RFQ_Automation.Models;
using Microsoft.AspNetCore.Http;

namespace ERP_RFQ_Automation.Authorization
{
    /// <inheritdoc cref="IIamAuditWriter"/>
    public sealed class IamAuditWriter : IIamAuditWriter
    {
        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
        };

        private readonly ErpRfqAutomationContext _context;
        private readonly IHttpContextAccessor? _httpContextAccessor;

        public IamAuditWriter(ErpRfqAutomationContext context, IHttpContextAccessor? httpContextAccessor = null)
        {
            _context = context;
            _httpContextAccessor = httpContextAccessor;
        }

        public IamAuditEvent Enlist(ClaimsPrincipal? principal, IamAuditEntry entry)
        {
            var actor = ActorContext.From(principal, _httpContextAccessor?.HttpContext?.TraceIdentifier);

            // Fail-closed. A tenant-less audit row sits outside the RLS policy and cannot be
            // attributed to anyone, so it is worse than useless: it would let an unattributable
            // privilege change look audited. Callers must have a validated businessUnitId claim,
            // which TenantClaimGuardMiddleware already guarantees on every non-platform route.
            if (!actor.HasTenant)
                throw new InvalidOperationException(
                    "An IAM audit event requires an authenticated businessUnitId claim.");

            var audit = new IamAuditEvent
            {
                BusinessUnitId = actor.BusinessUnitId,
                ActorUserId = actor.UserId,
                ActorRoleId = actor.RoleId,
                Action = entry.Action,
                TargetType = entry.TargetType,
                TargetId = entry.TargetId,
                TargetLabel = Truncate(entry.TargetLabel, 256),
                BeforeJson = Serialize(entry.Before),
                AfterJson = Serialize(entry.After),
                Reason = Truncate(entry.Reason, 512),
                CorrelationId = Truncate(actor.CorrelationId, 64),
                OccurredOn = DateTime.UtcNow
            };

            _context.Set<IamAuditEvent>().Add(audit);
            return audit;
        }

        public async Task<IamAuditEvent> WriteAsync(
            ClaimsPrincipal? principal, IamAuditEntry entry, CancellationToken cancellationToken = default)
        {
            var audit = Enlist(principal, entry);
            await _context.SaveChangesAsync(cancellationToken);
            return audit;
        }

        public async Task<Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction?> BeginAtomicAsync(
            CancellationToken cancellationToken = default)
        {
            if (_context.Database.CurrentTransaction is not null)
                return null;

            try
            {
                return await _context.Database.BeginTransactionAsync(cancellationToken);
            }
            catch (InvalidOperationException)
            {
                // Provider without transaction support (EF InMemory). The audit write still
                // happens; it simply is not atomic with the mutation on that provider.
                return null;
            }
        }

        private static string? Serialize(object? value)
            => value is null ? null : JsonSerializer.Serialize(value, JsonOptions);

        private static string? Truncate(string? value, int max)
            => value is null || value.Length <= max ? value : value[..max];
    }
}
