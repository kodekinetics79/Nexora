using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Platform.Models;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.Platform.Lifecycle;

/// <summary>
/// One database-wide ordering boundary for tenant legal-hold placement and irreversible work
/// that cannot itself participate in a database transaction (for example, deleting an S3 object).
/// The key is a bijection over the tenant id, namespaced by a fixed salt, so unrelated tenants do
/// not block one another.
/// </summary>
public static class TenantLegalHoldFence
{
    private const long AdvisoryKeySalt = unchecked((long)0x4E45584F5241484CUL); // "NEXORAHL"

    public static long AdvisoryKey(long platformTenantId) => platformTenantId ^ AdvisoryKeySalt;

    /// <summary>Transaction-scoped side used by legal-hold placement and release.</summary>
    public static Task AcquireTransactionAsync(
        ErpRfqAutomationContext context, long platformTenantId, CancellationToken ct) =>
        context.Database.IsNpgsql()
            ? context.Database.ExecuteSqlInterpolatedAsync(
                $"SELECT pg_advisory_xact_lock({AdvisoryKey(platformTenantId)});", ct)
            : Task.CompletedTask;

    /// <summary>
    /// Session-scoped side used while irreversible object deletion runs between database
    /// transactions. The returned lease keeps the same physical connection open and always
    /// releases the advisory lock before returning it to the pool.
    /// </summary>
    public static async Task<IAsyncDisposable> AcquireSessionAsync(
        ErpRfqAutomationContext context, long platformTenantId, CancellationToken ct)
    {
        if (!context.Database.IsNpgsql())
            return NoopLease.Instance;

        await context.Database.OpenConnectionAsync(ct);
        var key = AdvisoryKey(platformTenantId);
        try
        {
            await context.Database.ExecuteSqlInterpolatedAsync(
                $"SELECT pg_advisory_lock({key});", ct);
            return new PostgreSqlLease(context, key);
        }
        catch
        {
            await context.Database.CloseConnectionAsync();
            throw;
        }
    }

    public static Task<bool> HasActiveAsync(
        ErpRfqAutomationContext context, long platformTenantId, CancellationToken ct) =>
        context.Set<TenantLegalHold>().AsNoTracking().AnyAsync(
            hold => hold.TenantId == platformTenantId && hold.ReleasedOn == null, ct);

    /// <summary>
    /// Resolves the platform tenant from the business-unit identity used by tenant-plane services.
    /// Null is deliberately distinguishable from "no hold": destructive callers must fail closed
    /// when the two identity planes cannot be joined.
    /// </summary>
    public static Task<long?> ResolvePlatformTenantIdAsync(
        ErpRfqAutomationContext context, long businessUnitId, CancellationToken ct) =>
        context.Set<Tenant>().IgnoreQueryFilters().AsNoTracking()
            .Where(tenant => tenant.PrimaryBusinessUnitId == businessUnitId)
            .Select(tenant => (long?)tenant.Id)
            .SingleOrDefaultAsync(ct);

    private sealed class NoopLease : IAsyncDisposable
    {
        public static readonly NoopLease Instance = new();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class PostgreSqlLease(ErpRfqAutomationContext context, long key) : IAsyncDisposable
    {
        private bool _disposed;

        public async ValueTask DisposeAsync()
        {
            if (_disposed) return;
            _disposed = true;
            try
            {
                await context.Database.ExecuteSqlInterpolatedAsync(
                    $"SELECT pg_advisory_unlock({key});", CancellationToken.None);
            }
            finally
            {
                await context.Database.CloseConnectionAsync();
            }
        }
    }
}
