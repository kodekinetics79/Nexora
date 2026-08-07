using ERP_RFQ_Automation.Tests.Support;
using Npgsql;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// Production-dialect certification of 20260805105320_HardenPlatformGrantsAndBillingImmutability.
///
/// Two independent-review findings are pinned here because neither can be proven on the
/// portable (SQLite) lane:
///   1. The tenant/identity execution roles must be able to read exactly the columns the
///      suspension, entitlement, and impersonation-revocation checks need — and nothing
///      more. A silently missing grant fails enforcement OPEN; a silently widened grant
///      exposes the customer directory to any tenant-plane filter bug.
///   2. Finalized billing statements must be immutable at the database layer, not merely
///      in service code.
/// </summary>
[Collection(PostgreSqlIntegrationCollection.Name)]
public sealed class PlatformControlPlaneHardeningPostgreSqlTests
{
    private readonly PostgreSqlTestDatabase _database;

    public PlatformControlPlaneHardeningPostgreSqlTests(PostgreSqlTestDatabase database)
        => _database = database;

    [Theory]
    [Trait("Category", "PostgreSQL")]
    // Enforcement reads these and would fail open without them.
    [InlineData("nexora_tenant_app", "Tenants", "PrimaryBusinessUnitId", true)]
    [InlineData("nexora_tenant_app", "Tenants", "Status", true)]
    [InlineData("nexora_tenant_app", "Tenants", "PlanId", true)]
    [InlineData("nexora_tenant_app", "Plans", "MaxSeats", true)]
    [InlineData("nexora_tenant_app", "Plans", "MaxDocsPerMonth", true)]
    [InlineData("nexora_tenant_app", "Plans", "MaxConcurrentExtractionJobs", true)]
    [InlineData("nexora_tenant_app", "Plans", "Weight", true)]
    [InlineData("nexora_tenant_app", "ImpersonationSessions", "Jti", true)]
    [InlineData("nexora_tenant_app", "ImpersonationSessions", "RevokedAtUtc", true)]
    [InlineData("nexora_tenant_app", "ImpersonationSessions", "ExpiresAtUtc", true)]
    [InlineData("nexora_identity_app", "Tenants", "Status", true)]
    [InlineData("nexora_identity_app", "Plans", "MaxSeats", true)]
    // Confidential columns the checks never read: customer directory, internal status
    // reasons, support-impersonation context, and the revoking operator's identity.
    [InlineData("nexora_tenant_app", "Tenants", "Name", false)]
    [InlineData("nexora_tenant_app", "Tenants", "Slug", false)]
    [InlineData("nexora_tenant_app", "Tenants", "StatusReason", false)]
    [InlineData("nexora_tenant_app", "Tenants", "CreatedBy", false)]
    [InlineData("nexora_tenant_app", "ImpersonationSessions", "Reason", false)]
    [InlineData("nexora_tenant_app", "ImpersonationSessions", "RevokedBy", false)]
    [InlineData("nexora_tenant_app", "ImpersonationSessions", "ActorPlatformUserId", false)]
    [InlineData("nexora_identity_app", "Tenants", "Name", false)]
    [InlineData("nexora_identity_app", "Tenants", "StatusReason", false)]
    public async Task Platform_read_surface_for_tenant_plane_roles_is_exactly_the_enforcement_columns(
        string role, string table, string column, bool expected)
    {
        await using var connection = await _database.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT has_column_privilege(@role, @table, @column, 'SELECT');";
        command.Parameters.AddWithValue("role", role);
        command.Parameters.AddWithValue("table", $"platform.\"{table}\"");
        command.Parameters.AddWithValue("column", column);

        var granted = (bool)(await command.ExecuteScalarAsync())!;

        Assert.Equal(expected, granted);
    }

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task Tenant_plane_roles_cannot_write_the_platform_control_plane()
    {
        await using var connection = await _database.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT bool_or(
                       has_table_privilege(role_name, table_name, 'INSERT')
                    OR has_table_privilege(role_name, table_name, 'UPDATE')
                    OR has_table_privilege(role_name, table_name, 'DELETE'))
            FROM unnest(ARRAY['nexora_tenant_app', 'nexora_identity_app']) AS role_name,
                 unnest(ARRAY['platform."Tenants"', 'platform."Plans"',
                              'platform."ImpersonationSessions"']) AS table_name;
            """;

        Assert.False((bool)(await command.ExecuteScalarAsync())!);
    }

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task Finalized_billing_statements_reject_update_and_delete_at_the_database()
    {
        await using var connection = await _database.OpenConnectionAsync();
        var (statementId, rateCardId) = await SeedFinalStatementAsync(connection, status: "Final");

        var update = await Assert.ThrowsAsync<PostgresException>(async () =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText =
                """UPDATE platform."BillingStatements" SET "TotalAmount" = 1 WHERE "Id" = @id;""";
            command.Parameters.AddWithValue("id", statementId);
            await command.ExecuteNonQueryAsync();
        });
        Assert.Equal("55000", update.SqlState);

        var delete = await Assert.ThrowsAsync<PostgresException>(async () =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """DELETE FROM platform."BillingStatements" WHERE "Id" = @id;""";
            command.Parameters.AddWithValue("id", statementId);
            await command.ExecuteNonQueryAsync();
        });
        Assert.Equal("55000", delete.SqlState);

        var line = await Assert.ThrowsAsync<PostgresException>(async () =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO platform."BillingStatementLines"
                    ("BillingStatementId", "MeterKey", "Description", "MeteredQuantity",
                     "IncludedQuantity", "BillableQuantity", "UnitPrice", "Amount", "SourceNote")
                VALUES (@id, 'documents', 'forged', 1, 0, 1, 1, 1, 'forged');
                """;
            command.Parameters.AddWithValue("id", statementId);
            await command.ExecuteNonQueryAsync();
        });
        Assert.Equal("55000", line.SqlState);

        await CleanupAsync(connection, statementId, rateCardId, wasFinal: true);
    }

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task Draft_billing_statements_remain_mutable_so_recompute_still_works()
    {
        await using var connection = await _database.OpenConnectionAsync();
        var (statementId, rateCardId) = await SeedFinalStatementAsync(connection, status: "Draft");

        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                UPDATE platform."BillingStatements" SET "TotalAmount" = 42 WHERE "Id" = @id;
                """;
            command.Parameters.AddWithValue("id", statementId);
            Assert.Equal(1, await command.ExecuteNonQueryAsync());
        }

        // Draft -> Final is the one transition a Final row is reached by, and it must pass.
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                UPDATE platform."BillingStatements" SET "Status" = 'Final' WHERE "Id" = @id;
                """;
            command.Parameters.AddWithValue("id", statementId);
            Assert.Equal(1, await command.ExecuteNonQueryAsync());
        }

        await CleanupAsync(connection, statementId, rateCardId, wasFinal: true);
    }

    private static async Task<(long StatementId, long RateCardId)> SeedFinalStatementAsync(
        NpgsqlConnection connection, string status)
    {
        long tenantId;
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                INSERT INTO platform."Tenants" ("Name", "Slug", "Status", "CreatedOn", "CreatedBy")
                VALUES ('Hardening probe', @slug, 'Active', now(), 'tests')
                RETURNING "Id";
                """;
            command.Parameters.AddWithValue("slug", $"hardening-probe-{Guid.NewGuid():N}");
            tenantId = (long)(await command.ExecuteScalarAsync())!;
        }

        long rateCardId;
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                INSERT INTO platform."RateCards"
                    ("Code", "Currency", "EffectiveFromUtc", "IsActive", "CreatedOn", "CreatedBy", "Version")
                VALUES (@code, 'USD', now(), true, now(), 'tests', 1)
                RETURNING "Id";
                """;
            command.Parameters.AddWithValue("code", $"probe-{Guid.NewGuid():N}");
            rateCardId = (long)(await command.ExecuteScalarAsync())!;
        }

        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                INSERT INTO platform."BillingStatements"
                    ("TenantId", "PeriodStartUtc", "PeriodEndUtc", "RateCardId", "Currency",
                     "Status", "TotalAmount", "ComputedAtUtc", "Version")
                VALUES (@tenantId, timestamp '2026-01-01', timestamp '2026-02-01', @rateCardId,
                        'USD', @status, 100, now(), 1)
                RETURNING "Id";
                """;
            command.Parameters.AddWithValue("tenantId", tenantId);
            command.Parameters.AddWithValue("rateCardId", rateCardId);
            command.Parameters.AddWithValue("status", status);
            var statementId = (long)(await command.ExecuteScalarAsync())!;
            return (statementId, rateCardId);
        }
    }

    private static async Task CleanupAsync(
        NpgsqlConnection connection, long statementId, long rateCardId, bool wasFinal)
    {
        await using var command = connection.CreateCommand();
        // The guard trigger is the point of the test, so the fixture-hygiene delete has to get past
        // it — and there is now exactly one way to do that.
        //
        // This teardown used to lean on `SET session_replication_role = replica`, which suspended
        // the guard because it was an ordinary trigger. 20260807022229 promoted it to ENABLE
        // ALWAYS, so replica mode no longer suspends it — that promotion is the whole point, since
        // the tenant purge runs in replica mode on the owner connection and could otherwise have
        // rewritten the record of what a customer was charged. Nor can the row be demoted to Draft
        // first: the guard refuses EVERY write to a Final row, the status change included.
        //
        // Suspending the trigger by name is what remains, and it states the shipped property
        // accurately: removing a finalized statement now requires an operator who can ALTER the
        // table, not merely one who can reach the database. Re-enabled unconditionally afterwards
        // so a failure here cannot leave the guard disarmed for every later test in the container.
        command.CommandText = $"""
            {(wasFinal ? """
                ALTER TABLE platform."BillingStatements" DISABLE TRIGGER billing_statements_guard_update;
                ALTER TABLE platform."BillingStatements" DISABLE TRIGGER billing_statements_guard_delete;
                ALTER TABLE platform."BillingStatementLines" DISABLE TRIGGER billing_statement_lines_guard_write;
                """ : string.Empty)}
            DELETE FROM platform."BillingStatementLines" WHERE "BillingStatementId" = @statementId;
            DELETE FROM platform."BillingStatements" WHERE "Id" = @statementId;
            DELETE FROM platform."RateCards" WHERE "Id" = @rateCardId;
            {(wasFinal ? """
                ALTER TABLE platform."BillingStatements" ENABLE ALWAYS TRIGGER billing_statements_guard_update;
                ALTER TABLE platform."BillingStatements" ENABLE ALWAYS TRIGGER billing_statements_guard_delete;
                ALTER TABLE platform."BillingStatementLines" ENABLE ALWAYS TRIGGER billing_statement_lines_guard_write;
                """ : string.Empty)}
            """;
        command.Parameters.AddWithValue("statementId", statementId);
        command.Parameters.AddWithValue("rateCardId", rateCardId);
        await command.ExecuteNonQueryAsync();
    }
}
