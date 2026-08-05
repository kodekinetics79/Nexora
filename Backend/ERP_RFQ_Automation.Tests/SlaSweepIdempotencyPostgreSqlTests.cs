using ERP_RFQ_Automation.Services;
using ERP_RFQ_Automation.Sla;
using ERP_RFQ_Automation.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// Production-dialect proof for the SLA send-once fix. The SQLite suite proves the
/// claim protocol; this proves the thing the protocol actually depends on in
/// production: PostgreSQL rejects the second claim with SQLSTATE 23505, the migration
/// really created the unique index, and the worker's detection recognises the real
/// exception rather than a hand-made one.
/// </summary>
[Collection(PostgreSqlIntegrationCollection.Name)]
public sealed class SlaSweepIdempotencyPostgreSqlTests(PostgreSqlTestDatabase database)
{
    private const long TenantA = 97_301;
    private const long TenantB = 97_302;

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task Migration_created_the_unique_send_once_index()
    {
        await using var connection = await database.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT indexdef
            FROM pg_indexes
            WHERE schemaname = 'public'
              AND tablename = 'SlaEvents'
              AND indexname = 'UX_SlaEvents_BU_DedupKey';
            """;
        var definition = (string?)await command.ExecuteScalarAsync();

        Assert.NotNull(definition);
        Assert.Contains("CREATE UNIQUE INDEX", definition!, StringComparison.Ordinal);
        Assert.Contains("\"BusinessUnitId\"", definition!, StringComparison.Ordinal);
        Assert.Contains("\"DedupKey\"", definition!, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task Second_instance_claiming_the_same_alert_gets_23505_and_does_not_send()
    {
        await using (var seed = database.ContextFor(null))
        {
            Seed.EnsureBusinessUnit(seed, TenantA);
            await seed.SaveChangesAsync();
        }

        await using var instanceA = database.ContextFor(TenantA);
        await using var instanceB = database.ContextFor(TenantA);
        var key = SlaEvent.BuildDedupKey("lead", 5_001, "overdue");

        // Straight to the INSERT on both: the interleaving where both instances looked
        // first and both saw nothing, which is what produced the duplicate emails.
        var winner = await SlaSweepWorker.InsertClaimAsync(
            instanceA, TenantA, "lead", 5_001, "overdue", key, default);
        var loser = await SlaSweepWorker.InsertClaimAsync(
            instanceB, TenantA, "lead", 5_001, "overdue", key, default);

        Assert.NotNull(winner);
        Assert.Null(loser);

        await using var verify = database.ContextFor(TenantA);
        Assert.Equal(1, await verify.Set<SlaEvent>()
            .CountAsync(e => e.BusinessUnitId == TenantA && e.EntityId == 5_001));

        await CleanupAsync(TenantA);
    }

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task The_raw_postgres_violation_is_sqlstate_23505_and_is_recognised()
    {
        await using (var seed = database.ContextFor(null))
        {
            Seed.EnsureBusinessUnit(seed, TenantB);
            await seed.SaveChangesAsync();
        }

        await using var context = database.ContextFor(TenantB);
        Assert.NotNull(await SlaSweepWorker.TryClaimEventAsync(
            context, TenantB, "approval", 6_001, "escalated", null, default));

        // Bypass the worker helper and let the raw violation surface.
        await using var duplicateContext = database.ContextFor(TenantB);
        duplicateContext.Set<SlaEvent>().Add(new SlaEvent
        {
            BusinessUnitId = TenantB,
            EntityType = "approval",
            EntityId = 6_001,
            Level = "escalated",
            DedupKey = SlaEvent.BuildDedupKey("approval", 6_001, "escalated"),
            CreatedOn = DateTime.UtcNow
        });

        var failure = await Assert.ThrowsAsync<DbUpdateException>(
            () => duplicateContext.SaveChangesAsync());

        var postgres = Assert.IsType<PostgresException>(failure.InnerException);
        Assert.Equal("23505", postgres.SqlState);
        Assert.Equal("UX_SlaEvents_BU_DedupKey", postgres.ConstraintName);
        Assert.True(SlaSweepWorker.IsUniqueViolation(failure));

        await CleanupAsync(TenantB);
    }

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task Folder_retry_ledger_is_row_level_secured()
    {
        // The retry counter moved off the ephemeral per-instance disk into the database,
        // so the new table must carry the same tenant boundary as everything else.
        await using var connection = await database.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT c.relrowsecurity,
                   (SELECT count(*) FROM pg_policies p
                    WHERE p.schemaname = 'public'
                      AND p.tablename = 'FolderIngestionRetryStates'
                      AND p.policyname = 'nexora_tenant_isolation')
            FROM pg_class c
            JOIN pg_namespace n ON n.oid = c.relnamespace
            WHERE n.nspname = 'public' AND c.relname = 'FolderIngestionRetryStates';
            """;
        await using var reader = await command.ExecuteReaderAsync();

        Assert.True(await reader.ReadAsync());
        Assert.True(reader.GetBoolean(0), "FolderIngestionRetryStates must have RLS enabled.");
        Assert.Equal(1, reader.GetInt64(1));
    }

    [Theory]
    [Trait("Category", "PostgreSQL")]
    [InlineData("FxRates")]
    [InlineData("FxRateSnapshots")]
    public async Task Fx_authority_tables_are_row_level_secured(string table)
    {
        // FxRates/FxRateSnapshots decide what rate a customer's quote is converted at, and
        // shipped with NEITHER isolation layer: no EF query filter (documented in
        // Models/ErpRfqAutomationContext.Fx.cs) and no RLS policy. 20260804203000 adds the
        // database layer. The EF filter is still owned by ErpRfqAutomationContext.Tenancy.cs.
        await using var connection = await database.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT c.relrowsecurity,
                   pg_get_expr(p.polqual, p.polrelid),
                   pg_get_expr(p.polwithcheck, p.polrelid),
                   (SELECT r.oid = ANY(p.polroles) FROM pg_roles r WHERE r.rolname = 'nexora_tenant_app')
            FROM pg_class c
            JOIN pg_namespace n ON n.oid = c.relnamespace
            LEFT JOIN pg_policy p ON p.polrelid = c.oid AND p.polname = 'nexora_tenant_isolation'
            WHERE n.nspname = 'public' AND c.relname = @table;
            """;
        var parameter = command.CreateParameter();
        parameter.ParameterName = "table";
        parameter.Value = table;
        command.Parameters.Add(parameter);

        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync(), $"{table} does not exist.");
        Assert.True(reader.GetBoolean(0), $"{table} must have row-level security enabled.");
        Assert.False(await reader.IsDBNullAsync(1), $"{table} policy must have a USING clause.");
        Assert.False(await reader.IsDBNullAsync(2), $"{table} policy must have a WITH CHECK clause.");
        Assert.Contains("nexora.business_unit_id", reader.GetString(1), StringComparison.Ordinal);
        Assert.Contains("nexora.business_unit_id", reader.GetString(2), StringComparison.Ordinal);
        Assert.True(reader.GetBoolean(3), $"{table} policy must be bound to nexora_tenant_app.");
    }

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task Only_one_instance_can_hold_the_email_poller_lock_at_a_time()
    {
        // Defect: EmailBackgroundService had no lease or lock, so N instances polled the
        // same IMAP mailbox concurrently. The cycle now runs only inside this lock.
        await using var instanceA = database.ContextFor(null);
        await using var instanceB = database.ContextFor(null);

        var leaseA = await PostgresAdvisoryLease.TryAcquireAsync(
            instanceA, EmailBackgroundService.PollLockName, default);
        Assert.NotNull(leaseA);
        Assert.True(leaseA!.IsAdvisory);

        // The standby instance must NOT poll.
        Assert.Null(await PostgresAdvisoryLease.TryAcquireAsync(
            instanceB, EmailBackgroundService.PollLockName, default));

        // Once the leader releases, the standby takes over.
        await leaseA.DisposeAsync();
        var leaseB = await PostgresAdvisoryLease.TryAcquireAsync(
            instanceB, EmailBackgroundService.PollLockName, default);

        Assert.NotNull(leaseB);
        await leaseB!.DisposeAsync();
    }

    private async Task CleanupAsync(long businessUnitId)
    {
        await using var context = database.ContextFor(null);
        await context.Set<SlaEvent>().IgnoreQueryFilters()
            .Where(e => e.BusinessUnitId == businessUnitId)
            .ExecuteDeleteAsync();
    }
}
