using ERP_RFQ_Automation.Tests.Support;
using Npgsql;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// Production-dialect certification for stale-lease recovery: the grants the background runner
/// needs on the columns this feature adds, and the trigger that makes a LIVE lease untransferable
/// by any statement from any role.
///
/// <para><b>Why this cannot be a SQLite test.</b> Both properties under test are invisible on the
/// portable lane. SQLite has no roles, so a missing GRANT reads as green there and as 42501 in
/// production — the precise defect this codebase hit twice, most recently this week on
/// <c>ReportSubscriptions</c> (20260810203716). And SQLite has no trigger of this kind, so a lease
/// steal that the database refuses in production would simply succeed in the portable suite,
/// which is exactly the class of bug that produces two runners on one half-built tenant.</para>
///
/// <para><b>Why the DDL is restated here.</b> Following the pattern of
/// <see cref="ProvisioningSchemaSpecification"/>: this collection is shared and serialized, and a
/// sibling class in it drops and recreates the provisioning tables from its own specification. The
/// block below is idempotent and self-healing, so this class certifies the same schema whichever
/// order the collection runs in.</para>
/// </summary>
[Collection(PostgreSqlIntegrationCollection.Name)]
[Trait("Category", "PostgreSQL")]
public sealed class ProvisioningLeaseRecoveryPostgreSqlTests : IAsyncLifetime
{
    private readonly PostgreSqlTestDatabase _database;
    private readonly List<long> _created = [];

    public ProvisioningLeaseRecoveryPostgreSqlTests(PostgreSqlTestDatabase database)
        => _database = database;

    public async Task InitializeAsync()
    {
        await using var connection = await _database.OpenConnectionAsync();
        await ExecuteAsync(connection, ProvisioningSchemaSpecification.CreateTables);
        await ExecuteAsync(connection, ProvisioningSchemaSpecification.Grants);
        await ExecuteAsync(connection, LeaseRecoverySchema.Columns);
        await ExecuteAsync(connection, LeaseRecoverySchema.TransferFence);
        await ExecuteAsync(connection, LeaseRecoverySchema.Grants);
    }

    public async Task DisposeAsync()
    {
        if (_created.Count == 0)
            return;

        await using var connection = await _database.OpenConnectionAsync();
        await ExecuteAsync(connection,
            $"""DELETE FROM platform."ProvisioningExecutions" WHERE "Id" IN ({string.Join(',', _created)});""");
    }

    [Fact]
    public async Task The_lease_transfer_guard_is_installed_and_enabled_ALWAYS()
    {
        await using var connection = await _database.OpenConnectionAsync();

        // The migration was DISCOVERED AND APPLIED by the fixture's MigrateAsync, not merely
        // authored. This lane builds its schema from the migrations exactly as production does, so
        // a migration EF cannot find — a missing or malformed designer, a name that does not match
        // its attribute — fails here rather than on a deploy.
        Assert.Equal(1, await ScalarIntAsync(connection, """
            SELECT count(*) FROM "__EFMigrationsHistory"
             WHERE "MigrationId" = '20260810214500_ProvisioningStaleLeaseRecoveryAndOwnershipFence';
            """));

        // 'A' is ENABLE ALWAYS; 'O' is the default ENABLE ORIGIN, which does NOT fire under
        // session_replication_role = 'replica'. Asserted directly because the difference between
        // the two is invisible until somebody runs a bulk repair in replica mode and the guard
        // they were relying on turns out to have been off for exactly that statement.
        Assert.Equal("A", await ScalarStringAsync(connection, """
            SELECT t.tgenabled::text
              FROM pg_trigger t
              JOIN pg_class c ON c.oid = t.tgrelid
              JOIN pg_namespace n ON n.oid = c.relnamespace
             WHERE n.nspname = 'platform'
               AND c.relname = 'ProvisioningExecutions'
               AND t.tgname = 'provisioning_executions_lease_transfer_guard';
            """));

        // And the columns really are on the table, in the types the model expects.
        Assert.Equal("timestamp without time zone", await ScalarStringAsync(connection, """
            SELECT data_type FROM information_schema.columns
             WHERE table_schema = 'platform' AND table_name = 'ProvisioningExecutions'
               AND column_name = 'LeaseHeartbeatAt';
            """));
        Assert.Equal("integer", await ScalarStringAsync(connection, """
            SELECT data_type FROM information_schema.columns
             WHERE table_schema = 'platform' AND table_name = 'ProvisioningExecutions'
               AND column_name = 'RecoveredAttemptCount';
            """));
    }

    [Fact]
    public async Task The_pipeline_role_can_read_and_write_every_recovery_column()
    {
        await using var connection = await _database.OpenConnectionAsync();
        var id = await InsertAsync(connection, "Running", leaseSeconds: -3600, heartbeatSeconds: -3600);

        // The runner, the platform plane and the recovery service all execute as
        // nexora_pipeline_app — TenantRlsCommandInterceptor routes /api/platform there when there
        // is no tenant scope, and a worker with no HttpContext lands on the same role. A column
        // added by a later migration is covered by the table-level grant, but this project has
        // been bitten by point-in-time grants twice and the assertion costs nothing.
        await ExecuteAsync(connection, "SET ROLE nexora_pipeline_app;");
        await ExecuteAsync(connection, $"""
            UPDATE platform."ProvisioningExecutions"
               SET "State" = 'Pending',
                   "LeaseOwner" = NULL, "LeaseToken" = NULL, "LeaseUntil" = NULL,
                   "LeaseHeartbeatAt" = NULL,
                   "RecoveredAttemptCount" = "RecoveredAttemptCount" + 1,
                   "LastRecoveredOn" = (now() AT TIME ZONE 'utc'),
                   "LastRecoveredBy" = 'owner@nexora.app',
                   "LastRecoveryReason" = 'Node evicted mid-step.',
                   "Version" = "Version" + 1
             WHERE "Id" = {id};
            """);
        Assert.Equal(1, await ScalarIntAsync(connection, $"""
            SELECT "RecoveredAttemptCount" FROM platform."ProvisioningExecutions" WHERE "Id" = {id};
            """));
        await ExecuteAsync(connection, "RESET ROLE;");

        // The tenant and identity planes reach none of it. An execution row now also records who
        // declared a customer's provisioning attempt dead and why — internal correspondence about
        // a customer, not tenant data.
        foreach (var role in new[] { "nexora_tenant_app", "nexora_identity_app" })
        {
            await ExecuteAsync(connection, $"SET ROLE {role};");
            var denied = await Assert.ThrowsAsync<PostgresException>(() => ScalarAsync(connection,
                """SELECT "LastRecoveredBy" FROM platform."ProvisioningExecutions" LIMIT 1;"""));
            Assert.Equal("42501", denied.SqlState);
            await ExecuteAsync(connection, "RESET ROLE;");
        }
    }

    [Fact]
    public async Task A_live_lease_cannot_be_stolen_by_any_statement_or_any_role()
    {
        await using var connection = await _database.OpenConnectionAsync();
        var id = await InsertAsync(connection, "Running", leaseSeconds: 600, heartbeatSeconds: -5);

        // THE invariant. A live lease means a runner is presumed to be mid-step; a second runner
        // on the same half-built tenant writes the same rows twice, and the steps are idempotent
        // against themselves but not against a rival running at the same instant.
        foreach (var steal in new[]
                 {
                     // Straight takeover.
                     $"""
                      UPDATE platform."ProvisioningExecutions"
                         SET "LeaseOwner" = 'thief:9', "LeaseToken" = gen_random_uuid(),
                             "LeaseUntil" = (now() AT TIME ZONE 'utc') + interval '5 minutes'
                       WHERE "Id" = {id};
                      """,
                     // The two-step version: release now, claim a moment later. Refused at the
                     // first statement, because a release that does not END the attempt is
                     // indistinguishable from clearing the way for a steal.
                     $"""
                      UPDATE platform."ProvisioningExecutions"
                         SET "LeaseOwner" = NULL, "LeaseToken" = NULL, "LeaseUntil" = NULL
                       WHERE "Id" = {id};
                      """,
                     // Shortening the lease so it lapses immediately is the same steal, staged.
                     $"""
                      UPDATE platform."ProvisioningExecutions"
                         SET "LeaseUntil" = (now() AT TIME ZONE 'utc') - interval '1 minute'
                       WHERE "Id" = {id};
                      """,
                     // Retry's shape — Pending with the lease cleared — is refused too while the
                     // lease is live, whatever the caller believes it is doing.
                     $"""
                      UPDATE platform."ProvisioningExecutions"
                         SET "State" = 'Pending', "LeaseOwner" = NULL, "LeaseToken" = NULL,
                             "LeaseUntil" = NULL
                       WHERE "Id" = {id};
                      """
                 })
        {
            foreach (var role in new[] { "nexora_pipeline_app", null })
            {
                if (role is not null)
                    await ExecuteAsync(connection, $"SET ROLE {role};");

                var refused = await Assert.ThrowsAsync<PostgresException>(
                    () => ExecuteAsync(connection, steal));
                // 55006 object_in_use, raised by the BEFORE UPDATE guard.
                Assert.Equal("55006", refused.SqlState);
                Assert.Contains("cannot be changed", refused.MessageText);

                await ExecuteAsync(connection, "RESET ROLE;");
            }
        }

        // Nothing moved. The owner connection is included above deliberately: the runtime login
        // role also OWNS these tables, and a table owner is exempt from its own privileges — a
        // guard that only bound grantees would not bind the connection most likely to be used for
        // a hand-written fix.
        Assert.Equal("node-alpha:1", await ScalarStringAsync(connection,
            $"""SELECT "LeaseOwner" FROM platform."ProvisioningExecutions" WHERE "Id" = {id};"""));
    }

    [Fact]
    public async Task Renewing_a_live_lease_and_standing_down_from_one_are_both_allowed()
    {
        await using var connection = await _database.OpenConnectionAsync();
        var id = await InsertAsync(connection, "Running", leaseSeconds: 600, heartbeatSeconds: -5);

        await ExecuteAsync(connection, "SET ROLE nexora_pipeline_app;");

        // The runner marking a step: same owner, same token, lease pushed out. This happens
        // several times per execution and must never be mistaken for a transfer.
        await ExecuteAsync(connection, $"""
            UPDATE platform."ProvisioningExecutions"
               SET "LeaseUntil" = (now() AT TIME ZONE 'utc') + interval '10 minutes',
                   "LeaseHeartbeatAt" = (now() AT TIME ZONE 'utc'),
                   "CurrentStep" = 'baseline-seed', "Version" = "Version" + 1
             WHERE "Id" = {id};
            """);

        // The holder finishing: all three lease columns released together, and the execution
        // parked where nothing runs again without a fresh decision.
        await ExecuteAsync(connection, $"""
            UPDATE platform."ProvisioningExecutions"
               SET "State" = 'Failed', "LeaseOwner" = NULL, "LeaseToken" = NULL,
                   "LeaseUntil" = NULL, "LeaseHeartbeatAt" = NULL,
                   "FailedStep" = 'baseline-seed', "Version" = "Version" + 1
             WHERE "Id" = {id};
            """);

        await ExecuteAsync(connection, "RESET ROLE;");

        Assert.Equal("Failed", await ScalarStringAsync(connection,
            $"""SELECT "State" FROM platform."ProvisioningExecutions" WHERE "Id" = {id};"""));
    }

    [Fact]
    public async Task A_lapsed_lease_is_claimable_because_that_is_the_runner_working_as_designed()
    {
        await using var connection = await _database.OpenConnectionAsync();
        var id = await InsertAsync(connection, "Running", leaseSeconds: -60, heartbeatSeconds: -60);

        await ExecuteAsync(connection, "SET ROLE nexora_pipeline_app;");

        // Deliberately NOT refused. Two runners racing for an abandoned execution is settled by
        // the Version concurrency token, which produces exactly one winner; refusing it here would
        // break the ordinary recovery path that keeps a dead node from parking a tenant forever.
        await ExecuteAsync(connection, $"""
            UPDATE platform."ProvisioningExecutions"
               SET "LeaseOwner" = 'node-beta:2', "LeaseToken" = gen_random_uuid(),
                   "LeaseUntil" = (now() AT TIME ZONE 'utc') + interval '5 minutes',
                   "LeaseHeartbeatAt" = (now() AT TIME ZONE 'utc'),
                   "AttemptCount" = "AttemptCount" + 1, "Version" = "Version" + 1
             WHERE "Id" = {id};
            """);

        await ExecuteAsync(connection, "RESET ROLE;");

        Assert.Equal("node-beta:2", await ScalarStringAsync(connection,
            $"""SELECT "LeaseOwner" FROM platform."ProvisioningExecutions" WHERE "Id" = {id};"""));

        // ...and the moment it is live again, it is protected again.
        var refused = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(connection, $"""
            UPDATE platform."ProvisioningExecutions"
               SET "LeaseOwner" = 'thief:9' WHERE "Id" = {id};
            """));
        Assert.Equal("55006", refused.SqlState);
    }

    [Fact]
    public async Task The_guard_fires_under_replica_mode_where_ordinary_triggers_do_not()
    {
        await using var connection = await _database.OpenConnectionAsync();
        var id = await InsertAsync(connection, "Running", leaseSeconds: 600, heartbeatSeconds: -5);

        // A bulk repair or the tenant purge path runs with session_replication_role = 'replica',
        // where an ENABLE ORIGIN trigger silently does not fire. ENABLE ALWAYS is what makes this
        // guard hold in the one mode somebody reaches for when they are trying to force something
        // through — the same reasoning as the append-only guards in 20260807022229.
        await ExecuteAsync(connection, "SET session_replication_role = 'replica';");
        var refused = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(connection, $"""
            UPDATE platform."ProvisioningExecutions"
               SET "LeaseOwner" = 'thief:9', "LeaseToken" = gen_random_uuid()
             WHERE "Id" = {id};
            """));
        Assert.Equal("55006", refused.SqlState);
        await ExecuteAsync(connection, "SET session_replication_role = 'origin';");
    }

    // ---- helpers -------------------------------------------------------------------------------

    private async Task<long> InsertAsync(
        NpgsqlConnection connection, string state, int leaseSeconds, int heartbeatSeconds)
    {
        var slug = $"lease-{Guid.NewGuid():N}"[..20];
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO platform."ProvisioningExecutions"
                ("IdempotencyKey", "RequestFingerprint", "RequestPayload", "AdminPasswordHash",
                 "Slug", "Name", "AdminEmail", "AdminActivation", "State", "FailureIsTerminal",
                 "CorrelationId", "RequestedBy", "CreatedOn", "StartedOn", "AttemptCount", "Version",
                 "LeaseOwner", "LeaseToken", "LeaseUntil", "LeaseHeartbeatAt", "RecoveredAttemptCount")
            VALUES (@key, repeat('a', 64), '{}'::jsonb, 'hash', @slug, @slug, @email, 'invite',
                    @state, false, @correlation, 'tests',
                    (now() AT TIME ZONE 'utc') - interval '1 hour',
                    (now() AT TIME ZONE 'utc') - interval '1 hour', 1, 0,
                    'node-alpha:1', gen_random_uuid(),
                    (now() AT TIME ZONE 'utc') + make_interval(secs => @lease),
                    (now() AT TIME ZONE 'utc') + make_interval(secs => @heartbeat), 0)
            RETURNING "Id";
            """;
        command.Parameters.AddWithValue("key", $"key-{slug}");
        command.Parameters.AddWithValue("slug", slug);
        command.Parameters.AddWithValue("email", $"{slug}@example.test");
        command.Parameters.AddWithValue("state", state);
        command.Parameters.AddWithValue("correlation", Guid.NewGuid().ToString("N"));
        command.Parameters.AddWithValue("lease", (double)leaseSeconds);
        command.Parameters.AddWithValue("heartbeat", (double)heartbeatSeconds);

        var id = (long)(await command.ExecuteScalarAsync())!;
        _created.Add(id);
        return id;
    }

    private static async Task ExecuteAsync(NpgsqlConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<object?> ScalarAsync(NpgsqlConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return await command.ExecuteScalarAsync();
    }

    private static async Task<string?> ScalarStringAsync(NpgsqlConnection connection, string sql)
        => await ScalarAsync(connection, sql) as string;

    private static async Task<int> ScalarIntAsync(NpgsqlConnection connection, string sql)
        => Convert.ToInt32(await ScalarAsync(connection, sql));
}

/// <summary>
/// The schema that migration 20260810214500_ProvisioningStaleLeaseRecoveryAndOwnershipFence
/// installs, restated so it can be executed and asserted rather than described — the same
/// discipline <see cref="ProvisioningSchemaSpecification"/> follows, and for the same reason: a
/// grant or a trigger that exists only in prose is one that reaches production missing.
/// </summary>
public static class LeaseRecoverySchema
{
    public const string Columns = """
        ALTER TABLE platform."ProvisioningExecutions"
            ADD COLUMN IF NOT EXISTS "LeaseHeartbeatAt"      timestamp without time zone,
            ADD COLUMN IF NOT EXISTS "RecoveredAttemptCount" integer NOT NULL DEFAULT 0,
            ADD COLUMN IF NOT EXISTS "LastRecoveredOn"       timestamp without time zone,
            ADD COLUMN IF NOT EXISTS "LastRecoveredBy"       character varying(320),
            ADD COLUMN IF NOT EXISTS "LastRecoveryReason"    character varying(1000);
        """;

    /// <summary>
    /// The one invariant no application guard can hold: a LIVE lease is untransferable, from any
    /// connection, including the owner's. The single exception is the holder standing down, which
    /// releases all three lease columns together AND parks the execution where nothing runs again
    /// without a fresh decision.
    /// </summary>
    public const string TransferFence = """
        CREATE OR REPLACE FUNCTION platform.nexora_guard_provisioning_lease_transfer()
        RETURNS trigger LANGUAGE plpgsql AS $function$
        BEGIN
            IF OLD."State" <> 'Running'
               OR OLD."LeaseToken" IS NULL
               OR OLD."LeaseUntil" IS NULL
               OR OLD."LeaseUntil" <= (now() AT TIME ZONE 'utc') THEN
                RETURN NEW;
            END IF;

            IF NEW."LeaseOwner" IS NOT DISTINCT FROM OLD."LeaseOwner"
               AND NEW."LeaseToken" IS NOT DISTINCT FROM OLD."LeaseToken"
               AND NEW."LeaseUntil" >= OLD."LeaseUntil" THEN
                RETURN NEW;
            END IF;

            IF NEW."LeaseOwner" IS NULL
               AND NEW."LeaseToken" IS NULL
               AND NEW."LeaseUntil" IS NULL
               AND NEW."State" IN ('Succeeded', 'Failed', 'Cancelled') THEN
                RETURN NEW;
            END IF;

            RAISE EXCEPTION
                'Provisioning execution % is leased by % until % (UTC) and its ownership '
                'cannot be changed: a live lease means a runner is presumed to be mid-step, '
                'and a second runner on the same half-built tenant would write the same rows '
                'twice. Wait for the lease to lapse, then recover it through '
                'IProvisioningLeaseRecovery so the transfer carries evidence and an audit '
                'record.',
                OLD."Id", OLD."LeaseOwner", OLD."LeaseUntil"
                USING ERRCODE = '55006';
        END
        $function$;

        REVOKE ALL ON FUNCTION platform.nexora_guard_provisioning_lease_transfer() FROM PUBLIC;

        DROP TRIGGER IF EXISTS provisioning_executions_lease_transfer_guard
            ON platform."ProvisioningExecutions";

        CREATE TRIGGER provisioning_executions_lease_transfer_guard
            BEFORE UPDATE ON platform."ProvisioningExecutions"
            FOR EACH ROW
            EXECUTE FUNCTION platform.nexora_guard_provisioning_lease_transfer();

        ALTER TABLE platform."ProvisioningExecutions"
            ENABLE ALWAYS TRIGGER provisioning_executions_lease_transfer_guard;
        """;

    public const string Grants = """
        DO $provisioning_recovery_grants$
        BEGIN
            IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'nexora_pipeline_app') THEN
                RETURN;
            END IF;

            GRANT SELECT, INSERT, UPDATE, DELETE ON TABLE
                platform."ProvisioningExecutions", platform."ProvisioningSteps"
                TO nexora_pipeline_app;

            GRANT SELECT, INSERT ON TABLE platform."PlatformAuditLogs" TO nexora_pipeline_app;
            REVOKE UPDATE, DELETE, TRUNCATE ON TABLE platform."PlatformAuditLogs"
                FROM nexora_pipeline_app;

            GRANT USAGE, SELECT, UPDATE ON SEQUENCE
                platform."ProvisioningExecutions_Id_seq",
                platform."ProvisioningSteps_Id_seq"
                TO nexora_pipeline_app;

            REVOKE ALL PRIVILEGES ON TABLE
                platform."ProvisioningExecutions", platform."ProvisioningSteps"
                FROM nexora_tenant_app, nexora_identity_app;
        END
        $provisioning_recovery_grants$;
        """;
}
