using ERP_RFQ_Automation.Tests.Support;
using Npgsql;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// Production-dialect certification for the operator support desk, against the schema
/// 20260807022229_PlatformAdminControlPlaneProvisioningLifecycleAndSupport actually creates. None of
/// this can be proven on the portable (SQLite) lane, which has neither roles nor privileges — and a
/// privilege defect is invisible there by construction: a table that ships without its grant fails
/// with 42501 on the first real request while every SQLite test stays green.
///
/// <list type="number">
/// <item>The support tables carry the grants <c>/api/platform</c> needs under
/// <c>nexora_pipeline_app</c>, and are withheld from the tenant and identity planes.</item>
/// <item><c>SupportTicketNotes</c> refuses UPDATE — a thread cannot be reworded after the fact —
/// while retaining DELETE, because erasure on tenant purge has to remain possible.</item>
/// <item>The tenant foreign key is RESTRICT, so a purge cannot silently take the support history.</item>
/// <item>The platform audit log the explorer reads is still append-only for that same role.</item>
/// </list>
///
/// <para><b>Nothing here creates, drops or deletes anything it did not create, and nothing at all is
/// deleted from <c>platform."PlatformAuditLogs"</c>.</b> That table now carries an
/// <c>ENABLE ALWAYS</c> append-only trigger, so even the owner cannot clean up after itself, and a
/// test that tried would fail in its own teardown. Every probe below either rolls back or removes
/// only the tenant-scoped rows it inserted.</para>
/// </summary>
[Collection(PostgreSqlIntegrationCollection.Name)]
public sealed class PlatformSupportPostgreSqlTests
{
    private readonly PostgreSqlTestDatabase _database;

    public PlatformSupportPostgreSqlTests(PostgreSqlTestDatabase database) => _database = database;

    // ---- the guarantee the explorer reads under -----------------------------------------------

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task The_platform_audit_log_is_still_append_only_for_the_platform_execution_role()
    {
        // The audit explorer added a large new read surface over this table. The guarantee it reads
        // under must not have regressed — and the explorer's own half (no non-GET verb exists) is
        // asserted by reflection in PlatformSupportAuthorizationTests.
        await using var connection = await _database.OpenConnectionAsync();

        // INSERT under the role every /api/platform request runs as: still permitted, because that
        // is how audit records come to exist at all. Rolled back rather than cleaned up — a DELETE
        // afterwards is exactly what the append-only trigger now refuses.
        await using (var tx = await connection.BeginTransactionAsync())
        await using (var insert = connection.CreateCommand())
        {
            insert.Transaction = tx;
            insert.CommandText = """
                SET LOCAL ROLE nexora_pipeline_app;
                INSERT INTO platform."PlatformAuditLogs"
                    ("ActorPlatformUserId", "Action", "Metadata", "Result", "CreatedOn")
                VALUES (1, 'support.appendonly.probe', '{}', 'success', now());
                """;
            Assert.Equal(1, await insert.ExecuteNonQueryAsync());
            await tx.RollbackAsync();
        }

        // The privilege check fires before any row is examined, so these need no row to act on.
        foreach (var statement in new[]
                 {
                     """UPDATE platform."PlatformAuditLogs" SET "Action" = 'tampered' WHERE false;""",
                     """DELETE FROM platform."PlatformAuditLogs" WHERE false;""",
                     """TRUNCATE platform."PlatformAuditLogs";"""
                 })
        {
            await using var tx = await connection.BeginTransactionAsync();
            await using var command = connection.CreateCommand();
            command.Transaction = tx;
            command.CommandText = $"SET LOCAL ROLE nexora_pipeline_app;\n{statement}";
            var denied = await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync());
            Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, denied.SqlState);
            await tx.RollbackAsync();
        }
    }

    // ---- the support tables' privilege surface -------------------------------------------------

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task The_support_tables_carry_the_grants_they_need_and_notes_cannot_be_rewritten()
    {
        const long tenantId = 991_701;
        await using var connection = await _database.OpenConnectionAsync();

        try
        {
            await Execute(connection, $"""
                INSERT INTO platform."Tenants" ("Id", "Name", "Slug", "Status", "CreatedOn")
                VALUES ({tenantId}, 'Support Desk Probe', 'support-desk-probe', 'Active', now())
                ON CONFLICT ("Id") DO NOTHING;
                """);

            // Everything below runs as nexora_pipeline_app — the role /api/platform executes under
            // when no tenant scope is present (TenantRlsCommandInterceptor.ResolveDatabaseRole).
            await using (var tx = await connection.BeginTransactionAsync())
            {
                await using (var write = connection.CreateCommand())
                {
                    write.Transaction = tx;
                    write.CommandText = $"""
                        SET LOCAL ROLE nexora_pipeline_app;
                        INSERT INTO platform."SupportTickets"
                            ("TenantId", "Subject", "Body", "Severity", "Status", "Origin",
                             "CreatedAtUtc", "UpdatedAtUtc", "Version")
                        VALUES ({tenantId}, 'Cannot log in', 'Known-good password rejected',
                                'Normal', 'New', 'Operator', now(), now(), 1);
                        INSERT INTO platform."SupportTicketNotes"
                            ("SupportTicketId", "AuthorKind", "AuthorLabel", "Body", "IsInternal", "CreatedAtUtc")
                        SELECT "Id", 'Operator', 'operator@example.test', 'Certificate expired.', TRUE, now()
                        FROM platform."SupportTickets" WHERE "TenantId" = {tenantId};
                        INSERT INTO platform."SupportTicketLinks"
                            ("SupportTicketId", "Kind", "TargetKey", "LinkedByLabel", "LinkedAtUtc")
                        SELECT "Id", 'ImpersonationSession', 'probe-jti', 'operator@example.test', now()
                        FROM platform."SupportTickets" WHERE "TenantId" = {tenantId};
                        UPDATE platform."SupportTickets"
                        SET "Status" = 'Open', "Version" = "Version" + 1
                        WHERE "TenantId" = {tenantId};
                        """;
                    await write.ExecuteNonQueryAsync();
                }

                // The ticket row is mutable — it has a lifecycle. The THREAD is not.
                await using (var rewrite = connection.CreateCommand())
                {
                    rewrite.Transaction = tx;
                    rewrite.CommandText = """
                        UPDATE platform."SupportTicketNotes" SET "Body" = 'Something else entirely';
                        """;
                    var denied = await Assert.ThrowsAsync<PostgresException>(() => rewrite.ExecuteNonQueryAsync());
                    Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, denied.SqlState);
                }
                await tx.RollbackAsync();
            }

            // A fresh transaction: the refused UPDATE aborted the previous one. Erasure has to
            // remain possible, or a contractual delete obligation has no lawful implementation.
            await using (var tx = await connection.BeginTransactionAsync())
            await using (var erase = connection.CreateCommand())
            {
                erase.Transaction = tx;
                erase.CommandText = $"""
                    SET LOCAL ROLE nexora_pipeline_app;
                    INSERT INTO platform."SupportTickets"
                        ("TenantId", "Subject", "Severity", "Status", "Origin", "CreatedAtUtc", "UpdatedAtUtc", "Version")
                    VALUES ({tenantId}, 'Erasable', 'Low', 'New', 'Operator', now(), now(), 1);
                    INSERT INTO platform."SupportTicketNotes"
                        ("SupportTicketId", "AuthorKind", "AuthorLabel", "Body", "CreatedAtUtc")
                    SELECT "Id", 'Operator', 'operator@example.test', 'Personal data.', now()
                    FROM platform."SupportTickets" WHERE "Subject" = 'Erasable';
                    DELETE FROM platform."SupportTicketNotes";
                    SELECT count(*)::int FROM platform."SupportTicketNotes";
                    """;
                Assert.Equal(0, (int)(await erase.ExecuteScalarAsync())!);
                await tx.RollbackAsync();
            }

            // The customer's own plane must never reach the record of what we said about them
            // internally. REVOKEd explicitly by the migration rather than merely left ungranted, so
            // a future blanket grant cannot hand it over by accident.
            foreach (var role in new[] { "nexora_tenant_app", "nexora_identity_app" })
            {
                await using var tx = await connection.BeginTransactionAsync();
                await using var read = connection.CreateCommand();
                read.Transaction = tx;
                read.CommandText =
                    $"""SET LOCAL ROLE {role}; SELECT count(*) FROM platform."SupportTickets";""";
                var denied = await Assert.ThrowsAsync<PostgresException>(() => read.ExecuteScalarAsync());
                Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, denied.SqlState);
                await tx.RollbackAsync();
            }

            // Platform-schema tables need no row-level security: their rows describe tenants rather
            // than belonging to one, and there is no business unit in scope to key a policy on.
            // ImpersonationSessions is the precedent this follows.
            await using (var rls = connection.CreateCommand())
            {
                rls.CommandText = """
                    SELECT bool_and(NOT relrowsecurity)
                    FROM pg_class
                    JOIN pg_namespace ON pg_namespace.oid = pg_class.relnamespace
                    WHERE pg_namespace.nspname = 'platform'
                      AND pg_class.relname IN ('SupportTickets', 'SupportTicketNotes',
                                               'SupportTicketLinks', 'ImpersonationSessions');
                    """;
                Assert.True((bool)(await rls.ExecuteScalarAsync())!);
            }

            // The tenant foreign key must be RESTRICT. A purge that silently vacuumed the support
            // history would destroy the evidence a disputed offboarding needs. The referencing row
            // is COMMITTED first — every block above rolled back, and a delete with nothing pointing
            // at it would pass this assertion whatever the constraint said.
            await Execute(connection, $"""
                INSERT INTO platform."SupportTickets"
                    ("TenantId", "Subject", "Severity", "Status", "Origin", "CreatedAtUtc", "UpdatedAtUtc", "Version")
                VALUES ({tenantId}, 'Referencing row', 'Low', 'New', 'Operator', now(), now(), 1);
                """);
            await using (var tx = await connection.BeginTransactionAsync())
            await using (var delete = connection.CreateCommand())
            {
                delete.Transaction = tx;
                delete.CommandText = $"""DELETE FROM platform."Tenants" WHERE "Id" = {tenantId};""";
                var blocked = await Assert.ThrowsAsync<PostgresException>(() => delete.ExecuteNonQueryAsync());
                Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, blocked.SqlState);
                await tx.RollbackAsync();
            }
        }
        finally
        {
            // Only the rows this test created, and nothing in PlatformAuditLogs.
            await Execute(connection, $"""
                DELETE FROM platform."SupportTicketLinks"
                WHERE "SupportTicketId" IN (SELECT "Id" FROM platform."SupportTickets" WHERE "TenantId" = {tenantId});
                DELETE FROM platform."SupportTicketNotes"
                WHERE "SupportTicketId" IN (SELECT "Id" FROM platform."SupportTickets" WHERE "TenantId" = {tenantId});
                DELETE FROM platform."SupportTickets" WHERE "TenantId" = {tenantId};
                DELETE FROM platform."Tenants" WHERE "Id" = {tenantId};
                """);
        }
    }

    private static async Task Execute(NpgsqlConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }
}
