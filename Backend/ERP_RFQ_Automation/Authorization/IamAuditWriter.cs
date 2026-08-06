using Microsoft.EntityFrameworkCore;
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

        public async Task ExecuteAtomicAsync(Func<Task> work, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(work);

            // Already inside someone else's transaction: just run. Opening a nested one, or
            // re-entering the execution strategy, would both be wrong.
            if (_context.Database.CurrentTransaction is not null)
            {
                await work();
                return;
            }

            var strategy = _context.Database.CreateExecutionStrategy();
            try
            {
                await strategy.ExecuteAsync(async () =>
                {
                    await using var transaction = await _context.Database.BeginTransactionAsync(cancellationToken);
                    await work();
                    await transaction.CommitAsync(cancellationToken);
                });
            }
            catch (InvalidOperationException) when (_context.Database.CurrentTransaction is null
                                                    && !_context.Database.ProviderName!.Contains("Npgsql", StringComparison.OrdinalIgnoreCase)
                                                    && !_context.Database.ProviderName!.Contains("Sqlite", StringComparison.OrdinalIgnoreCase))
            {
                // Providers without transaction support (EF InMemory). The work still happens; it
                // simply is not atomic there. Deliberately narrow — a relational provider must
                // never reach this path, because that is precisely the bug this method fixes.
                await work();
            }
        }

        private static string? Serialize(object? value)
            => value is null ? null : JsonSerializer.Serialize(value, JsonOptions);

        private static string? Truncate(string? value, int max)
            => value is null || value.Length <= max ? value : value[..max];
    }
}
