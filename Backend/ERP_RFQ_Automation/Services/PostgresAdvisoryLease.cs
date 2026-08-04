using System;
using System.Data.Common;
using System.Globalization;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ERP_RFQ_Automation.Models;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.Services;

/// <summary>
/// A PostgreSQL SESSION-level advisory lock used as single-instance (leader) election
/// for background loops that must not run concurrently across a scaled-out
/// deployment — the IMAP poller above all, where two instances fetching the same
/// mailbox produce duplicated ingestion work and duplicated customer-visible effects.
///
/// Why an advisory lock rather than a lease table: it is released automatically when
/// the holding connection dies, so an instance that is OOM-killed or evicted mid-poll
/// cannot wedge the poller for the whole lease window. It needs no migration and no
/// clock synchronisation between instances.
///
/// The lock is taken on a connection that EF Core is told to keep open explicitly, so
/// the session survives the per-command transactions that TenantRlsCommandInterceptor
/// opens and commits. Disposing the lease unlocks and lets the connection go back to
/// the pool.
/// </summary>
public sealed class PostgresAdvisoryLease : IAsyncDisposable
{
    private readonly ErpRfqAutomationContext? _db;
    private readonly long _key;
    private bool _released;

    private PostgresAdvisoryLease(ErpRfqAutomationContext? db, long key)
    {
        _db = db;
        _key = key;
    }

    /// <summary>True when the lease is a no-op (non-PostgreSQL provider, e.g. the SQLite test suite).</summary>
    public bool IsAdvisory => _db is not null;

    /// <summary>
    /// Tries to become the single active holder of <paramref name="lockName"/>.
    /// Returns null when another instance already holds it — the caller should skip
    /// this cycle rather than wait, so the standby instance stays responsive.
    /// </summary>
    public static async Task<PostgresAdvisoryLease?> TryAcquireAsync(
        ErpRfqAutomationContext db, string lockName, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentException.ThrowIfNullOrWhiteSpace(lockName);

        // Non-PostgreSQL providers (tests) get an always-granted no-op lease.
        if (!db.Database.IsNpgsql())
            return new PostgresAdvisoryLease(null, 0);

        var key = StableKey(lockName);
        await db.Database.OpenConnectionAsync(ct);
        try
        {
            var acquired = await ExecuteBooleanAsync(
                db, "SELECT pg_try_advisory_lock(@key)", key, ct);
            if (acquired)
                return new PostgresAdvisoryLease(db, key);
        }
        catch
        {
            await SafeCloseAsync(db);
            throw;
        }

        await SafeCloseAsync(db);
        return null;
    }

    public async ValueTask DisposeAsync()
    {
        if (_released || _db is null) { _released = true; return; }
        _released = true;
        try
        {
            await ExecuteBooleanAsync(_db, "SELECT pg_advisory_unlock(@key)", _key, CancellationToken.None);
        }
        catch
        {
            // Losing the unlock is safe: the lock dies with the session, and the session
            // ends when the connection is closed or the instance goes away.
        }
        finally
        {
            await SafeCloseAsync(_db);
        }
    }

    private static async Task<bool> ExecuteBooleanAsync(
        ErpRfqAutomationContext db, string sql, long key, CancellationToken ct)
    {
        // Raw ADO on the pinned connection deliberately: the EF command interceptor
        // wraps commands in short transactions, and a SESSION-level advisory lock must
        // not be entangled with them.
        await using var command = db.Database.GetDbConnection().CreateCommand();
        command.CommandText = sql;
        var parameter = command.CreateParameter();
        parameter.ParameterName = "key";
        parameter.Value = key;
        command.Parameters.Add(parameter);
        var result = await command.ExecuteScalarAsync(ct);
        return result is bool granted && granted;
    }

    private static async Task SafeCloseAsync(ErpRfqAutomationContext db)
    {
        try { await db.Database.CloseConnectionAsync(); }
        catch (DbException) { /* connection already faulted; the pool will discard it */ }
    }

    /// <summary>
    /// FNV-1a 64. Deliberately NOT string.GetHashCode(), which is randomised per
    /// process — every instance must derive the SAME key from the same name.
    /// </summary>
    internal static long StableKey(string name)
    {
        const ulong offsetBasis = 14695981039346656037;
        const ulong prime = 1099511628211;
        var hash = offsetBasis;
        foreach (var b in Encoding.UTF8.GetBytes(name.ToLowerInvariant()))
        {
            hash ^= b;
            hash *= prime;
        }
        return unchecked((long)hash);
    }

    public static string DescribeKey(string lockName)
        => StableKey(lockName).ToString(CultureInfo.InvariantCulture);
}
