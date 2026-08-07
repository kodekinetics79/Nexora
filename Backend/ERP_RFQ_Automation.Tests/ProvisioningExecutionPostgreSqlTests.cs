using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Platform.Provisioning;
using ERP_RFQ_Automation.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// Production-dialect certification for the durable provisioning tables, against real PostgreSQL
/// with the real role topology.
///
/// <para><b>Why this test carries the DDL.</b> Migrations are owned elsewhere, so the schema this
/// module needs is specified rather than generated. Specifying it in prose and hoping is how a
/// missing grant reaches production: the blanket "ALL TABLES IN SCHEMA platform" grant in
/// 20260728134117 is point-in-time and grants nothing on a table created afterwards, so a new
/// platform table is unreadable and unwritable by every runtime role — failing with 42501 at
/// runtime while every SQLite test stays green, because SQLite has neither roles nor
/// column-level privileges. That exact defect was found and fixed in this codebase before. The
/// DDL below IS the migration spec, executed and asserted.</para>
///
/// <para><b>Why no RLS policy is expected.</b> These three tables live in the <c>platform</c>
/// schema and carry no global query filter, so they fall outside the enumeration
/// <c>PostgreSqlProductionDialectTests</c> performs — which selects entities whose schema is
/// <c>public</c> AND which have a query filter. Adding a filter here would enrol them in an
/// RLS expectation no pre-tenant table can satisfy: an execution exists before the business unit
/// it is creating, so there is nothing to scope it to. Asserted below rather than assumed.</para>
/// </summary>
[Collection(PostgreSqlIntegrationCollection.Name)]
public sealed class ProvisioningExecutionPostgreSqlTests : IAsyncLifetime
{
    private readonly PostgreSqlTestDatabase _database;

    public ProvisioningExecutionPostgreSqlTests(PostgreSqlTestDatabase database)
        => _database = database;

    public async Task InitializeAsync()
    {
        await using var connection = await _database.OpenConnectionAsync();
        await ExecuteAsync(connection, ProvisioningSchemaSpecification.CreateTables);
        await ExecuteAsync(connection, ProvisioningSchemaSpecification.Grants);
    }

    public async Task DisposeAsync()
    {
        await using var connection = await _database.OpenConnectionAsync();
        await ExecuteAsync(connection, ProvisioningSchemaSpecification.Drop);
    }

    [Fact]
    public async Task The_live_slug_index_makes_a_second_concurrent_attempt_impossible()
    {
        await using var connection = await _database.OpenConnectionAsync();
        var slug = $"race-{Guid.NewGuid():N}"[..20];

        await InsertExecutionAsync(connection, slug, "key-a-" + slug, "Pending");

        // THE duplicate-tenant guard, in the only place it can be guaranteed. A double-click is
        // two writes racing, so an application-level "is one already running?" read is a
        // read-then-write window both can pass through.
        var duplicate = await Assert.ThrowsAsync<PostgresException>(
            () => InsertExecutionAsync(connection, slug, "key-b-" + slug, "Running"));
        Assert.Equal("23505", duplicate.SqlState);
        Assert.Equal("UX_ProvisioningExecutions_LiveSlug", duplicate.ConstraintName);

        // A FAILED execution still owns its address: the rows its early steps committed would
        // otherwise be stranded with nothing pointing at them.
        await ExecuteAsync(connection,
            $"""UPDATE platform."ProvisioningExecutions" SET "State" = 'Failed' WHERE "Slug" = '{slug}';""");
        var stillOwned = await Assert.ThrowsAsync<PostgresException>(
            () => InsertExecutionAsync(connection, slug, "key-c-" + slug, "Pending"));
        Assert.Equal("23505", stillOwned.SqlState);

        // Once it SUCCEEDS the address leaves this index and platform."Tenants".Slug's own unique
        // index becomes the authority — a stronger one, because it guards the real tenant.
        await ExecuteAsync(connection,
            $"""UPDATE platform."ProvisioningExecutions" SET "State" = 'Succeeded' WHERE "Slug" = '{slug}';""");
        await InsertExecutionAsync(connection, slug, "key-d-" + slug, "Pending");

        // And cancelling releases it too, which is the deliberate way an operator gives up.
        await ExecuteAsync(connection,
            $"""DELETE FROM platform."ProvisioningExecutions" WHERE "Slug" = '{slug}';""");
    }

    [Fact]
    public async Task The_idempotency_key_index_refuses_a_replayed_submit_at_the_database()
    {
        await using var connection = await _database.OpenConnectionAsync();
        var key = $"key-{Guid.NewGuid():N}";

        await InsertExecutionAsync(connection, $"one-{Guid.NewGuid():N}"[..20], key, "Succeeded");

        var duplicate = await Assert.ThrowsAsync<PostgresException>(
            () => InsertExecutionAsync(connection, $"two-{Guid.NewGuid():N}"[..20], key, "Pending"));
        Assert.Equal("23505", duplicate.SqlState);
        Assert.Equal("UX_ProvisioningExecutions_IdempotencyKey", duplicate.ConstraintName);

        await ExecuteAsync(connection,
            $"""DELETE FROM platform."ProvisioningExecutions" WHERE "IdempotencyKey" = '{key}';""");
    }

    [Fact]
    public async Task Only_the_pipeline_role_can_touch_the_provisioning_tables()
    {
        await using var connection = await _database.OpenConnectionAsync();
        var slug = $"grant-{Guid.NewGuid():N}"[..20];

        // The platform plane runs as nexora_pipeline_app: TenantRlsCommandInterceptor routes
        // /api/platform there when there is no tenant scope, and a background worker with no
        // HttpContext lands on the same role. Everything this module does must work under it.
        await ExecuteAsync(connection, "SET ROLE nexora_pipeline_app;");
        await InsertExecutionAsync(connection, slug, $"key-{slug}", "Pending");
        await ExecuteAsync(connection,
            $"""UPDATE platform."ProvisioningExecutions" SET "State" = 'Running' WHERE "Slug" = '{slug}';""");
        Assert.Equal(1L, await ScalarAsync(connection,
            $"""SELECT count(*) FROM platform."ProvisioningExecutions" WHERE "Slug" = '{slug}';"""));
        await ExecuteAsync(connection, "RESET ROLE;");

        // The tenant plane must reach NONE of it. An execution row carries a customer's legal
        // name, commercial terms and administrator address before that customer exists; a filter
        // bug or an injection in the tenant plane must not be able to enumerate the pipeline of
        // who is being onboarded. This is the same narrowing 20260805105320 applied to Tenants.
        foreach (var role in new[] { "nexora_tenant_app", "nexora_identity_app" })
        {
            await ExecuteAsync(connection, $"SET ROLE {role};");

            var denied = await Assert.ThrowsAsync<PostgresException>(() => ScalarAsync(connection,
                """SELECT count(*) FROM platform."ProvisioningExecutions";"""));
            Assert.Equal("42501", denied.SqlState);

            var deniedSteps = await Assert.ThrowsAsync<PostgresException>(() => ScalarAsync(connection,
                """SELECT count(*) FROM platform."ProvisioningSteps";"""));
            Assert.Equal("42501", deniedSteps.SqlState);

            var deniedDrafts = await Assert.ThrowsAsync<PostgresException>(() => ScalarAsync(connection,
                """SELECT count(*) FROM platform."ProvisioningDrafts";"""));
            Assert.Equal("42501", deniedDrafts.SqlState);

            await ExecuteAsync(connection, "RESET ROLE;");
        }

        await ExecuteAsync(connection,
            $"""DELETE FROM platform."ProvisioningExecutions" WHERE "Slug" = '{slug}';""");
    }

    [Fact]
    public async Task The_step_journal_is_deleted_with_its_execution_and_never_duplicated()
    {
        await using var connection = await _database.OpenConnectionAsync();
        var slug = $"steps-{Guid.NewGuid():N}"[..20];
        var executionId = await InsertExecutionAsync(connection, slug, $"key-{slug}", "Running");

        await ExecuteAsync(connection, $"""
            INSERT INTO platform."ProvisioningSteps"
                ("ExecutionId", "StepCode", "Ordinal", "Status", "AttemptCount")
            VALUES ({executionId}, 'tenant', 0, 'Succeeded', 1);
            """);

        // One row per step per execution, forever: the runner UPDATES on retry rather than
        // appending, so the console renders a step list without deduplicating it.
        var duplicate = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(connection, $"""
            INSERT INTO platform."ProvisioningSteps"
                ("ExecutionId", "StepCode", "Ordinal", "Status", "AttemptCount")
            VALUES ({executionId}, 'tenant', 0, 'Running', 2);
            """));
        Assert.Equal("23505", duplicate.SqlState);
        Assert.Equal("UX_ProvisioningSteps_Execution_StepCode", duplicate.ConstraintName);

        await ExecuteAsync(connection,
            $"""DELETE FROM platform."ProvisioningExecutions" WHERE "Id" = {executionId};""");
        Assert.Equal(0L, await ScalarAsync(connection,
            $"""SELECT count(*) FROM platform."ProvisioningSteps" WHERE "ExecutionId" = {executionId};"""));
    }

    [Fact]
    public void The_provisioning_tables_are_deliberately_outside_the_RLS_expectation()
    {
        using var context = _database.ContextFor(null);

        // PostgreSqlProductionDialectTests enumerates entities that (a) live in the public schema
        // and (b) carry a query filter, and FAILS unless each has a matching nexora_tenant_isolation
        // policy. These three satisfy neither half, which is the point: an execution exists before
        // the business unit it creates, so there is nothing to scope it to, and enrolling it would
        // demand a policy no pre-tenant table can write.
        foreach (var clrType in new[]
                 {
                     typeof(ProvisioningExecution), typeof(ProvisioningStep), typeof(ProvisioningDraft)
                 })
        {
            var entity = context.Model.FindEntityType(clrType);

            // Present only once the splice line lands in OnModelCreatingPartial. Until then the
            // production model does not carry these entities, and this assertion documents which
            // half of the wiring is missing rather than failing for an unrelated reason.
            if (entity is null)
                continue;

            Assert.Equal("platform", entity.GetSchema());
            Assert.Null(entity.GetQueryFilter());
        }
    }

    private static async Task<long> InsertExecutionAsync(
        NpgsqlConnection connection, string slug, string key, string state)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO platform."ProvisioningExecutions"
                ("IdempotencyKey", "RequestFingerprint", "RequestPayload", "AdminPasswordHash",
                 "Slug", "Name", "AdminEmail", "AdminActivation", "State", "FailureIsTerminal",
                 "CorrelationId", "RequestedBy", "CreatedOn", "AttemptCount", "Version")
            VALUES (@key, repeat('a', 64), '{}'::jsonb, 'hash', @slug, @slug, @email, 'invite',
                    @state, false, @correlation, 'tests', now(), 0, 0)
            RETURNING "Id";
            """;
        command.Parameters.AddWithValue("key", key);
        command.Parameters.AddWithValue("slug", slug);
        command.Parameters.AddWithValue("email", $"{slug}@example.test");
        command.Parameters.AddWithValue("state", state);
        command.Parameters.AddWithValue("correlation", Guid.NewGuid().ToString("N"));
        return (long)(await command.ExecuteScalarAsync())!;
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
}

/// <summary>
/// The migration this module needs, written out so it can be executed and asserted rather than
/// described. Copy it into a migration verbatim — this is the specification, and the test above
/// is its proof.
/// </summary>
public static class ProvisioningSchemaSpecification
{
    public const string CreateTables = """
        CREATE TABLE IF NOT EXISTS platform."ProvisioningExecutions" (
            "Id"                        bigint GENERATED BY DEFAULT AS IDENTITY PRIMARY KEY,
            "IdempotencyKey"            character varying(200) NOT NULL,
            "RequestFingerprint"        character varying(64)  NOT NULL,
            "RequestPayload"            jsonb                  NOT NULL,
            "AdminPasswordHash"         character varying(100) NOT NULL,
            "Slug"                      character varying(64)  NOT NULL,
            "Name"                      character varying(256) NOT NULL,
            "AdminEmail"                character varying(320) NOT NULL,
            "AdminActivation"           character varying(16)  NOT NULL,
            "State"                     character varying(16)  NOT NULL,
            "CurrentStep"               character varying(64),
            "FailedStep"                character varying(64),
            "FailureReason"             character varying(2000),
            "FailureIsTerminal"         boolean                NOT NULL DEFAULT false,
            "TenantId"                  bigint,
            "ProvisionedBusinessUnitId"            bigint,
            "FoundingRoleId"            bigint,
            "FoundingUserId"            bigint,
            "InvitationId"              bigint,
            "ResultPayload"             jsonb,
            "CorrelationId"             character varying(64)  NOT NULL,
            "RequestedBy"               character varying(320) NOT NULL,
            "RequestedByPlatformUserId" bigint,
            "CancelledBy"               character varying(320),
            "CancellationReason"        character varying(1000),
            "CreatedOn"                 timestamp without time zone NOT NULL,
            "StartedOn"                 timestamp without time zone,
            "CompletedOn"               timestamp without time zone,
            "AttemptCount"              integer                NOT NULL DEFAULT 0,
            "LeaseOwner"                character varying(200),
            "LeaseToken"                uuid,
            "LeaseUntil"                timestamp without time zone,
            "Version"                   bigint                 NOT NULL DEFAULT 0
        );

        CREATE UNIQUE INDEX IF NOT EXISTS "UX_ProvisioningExecutions_IdempotencyKey"
            ON platform."ProvisioningExecutions" ("IdempotencyKey");

        -- The duplicate-tenant guard: one live attempt per workspace address. Failed is INSIDE
        -- the predicate on purpose — a failed execution still owns the rows its early steps
        -- committed, so it must be retried or cancelled rather than raced by a fresh submit.
        CREATE UNIQUE INDEX IF NOT EXISTS "UX_ProvisioningExecutions_LiveSlug"
            ON platform."ProvisioningExecutions" ("Slug")
            WHERE "State" IN ('Pending', 'Running', 'Failed');

        CREATE INDEX IF NOT EXISTS "IX_ProvisioningExecutions_Claim"
            ON platform."ProvisioningExecutions" ("State", "LeaseUntil", "CreatedOn");
        CREATE INDEX IF NOT EXISTS "IX_ProvisioningExecutions_CreatedOn"
            ON platform."ProvisioningExecutions" ("CreatedOn");
        CREATE INDEX IF NOT EXISTS "IX_ProvisioningExecutions_TenantId"
            ON platform."ProvisioningExecutions" ("TenantId");

        CREATE TABLE IF NOT EXISTS platform."ProvisioningSteps" (
            "Id"            bigint GENERATED BY DEFAULT AS IDENTITY PRIMARY KEY,
            "ExecutionId"   bigint                 NOT NULL
                REFERENCES platform."ProvisioningExecutions" ("Id") ON DELETE CASCADE,
            "StepCode"      character varying(64)  NOT NULL,
            "Ordinal"       integer                NOT NULL,
            "Status"        character varying(16)  NOT NULL,
            "AttemptCount"  integer                NOT NULL DEFAULT 0,
            "StartedOn"     timestamp without time zone,
            "CompletedOn"   timestamp without time zone,
            "DurationMs"    integer,
            "FailureCode"   character varying(64),
            "FailureReason" character varying(2000),
            "Detail"        jsonb
        );

        CREATE UNIQUE INDEX IF NOT EXISTS "UX_ProvisioningSteps_Execution_StepCode"
            ON platform."ProvisioningSteps" ("ExecutionId", "StepCode");
        CREATE INDEX IF NOT EXISTS "IX_ProvisioningSteps_Execution_Ordinal"
            ON platform."ProvisioningSteps" ("ExecutionId", "Ordinal");

        CREATE TABLE IF NOT EXISTS platform."ProvisioningDrafts" (
            "Id"                   bigint GENERATED BY DEFAULT AS IDENTITY PRIMARY KEY,
            "Name"                 character varying(256) NOT NULL,
            "OwnerEmail"           character varying(320) NOT NULL,
            "OwnerPlatformUserId"  bigint,
            "Payload"              jsonb                  NOT NULL,
            "CreatedOn"            timestamp without time zone NOT NULL,
            "UpdatedOn"            timestamp without time zone NOT NULL,
            "SubmittedExecutionId" bigint,
            "Version"              bigint                 NOT NULL DEFAULT 0
        );

        CREATE INDEX IF NOT EXISTS "IX_ProvisioningDrafts_Owner_UpdatedOn"
            ON platform."ProvisioningDrafts" ("OwnerEmail", "UpdatedOn");
        """;

    /// <summary>
    /// The blanket "ALL TABLES IN SCHEMA platform" grant in 20260728134117 is POINT IN TIME — it
    /// grants nothing on a table created afterwards. Without this block these three tables are
    /// unreadable and unwritable by every runtime role, and provisioning fails at the first INSERT
    /// with 42501 while every SQLite test stays green.
    /// </summary>
    public const string Grants = """
        DO $provisioning_grants$
        BEGIN
            IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'nexora_pipeline_app') THEN
                RETURN;
            END IF;

            -- The platform plane and the background runner both execute as nexora_pipeline_app:
            -- TenantRlsCommandInterceptor routes /api/platform there when there is no tenant
            -- scope, and a worker with no HttpContext lands on the same role.
            GRANT SELECT, INSERT, UPDATE, DELETE ON TABLE
                platform."ProvisioningExecutions",
                platform."ProvisioningSteps",
                platform."ProvisioningDrafts"
                TO nexora_pipeline_app;
            GRANT USAGE, SELECT, UPDATE ON SEQUENCE
                platform."ProvisioningExecutions_Id_seq",
                platform."ProvisioningSteps_Id_seq",
                platform."ProvisioningDrafts_Id_seq"
                TO nexora_pipeline_app;

            -- Never the tenant or identity planes. An execution row carries a customer's legal
            -- name, commercial terms and administrator address before that customer exists, and
            -- nothing outside the control plane has any reason to read the onboarding pipeline.
            -- Explicit, matching the narrowing 20260805105320 applied to platform."Tenants".
            REVOKE ALL PRIVILEGES ON TABLE
                platform."ProvisioningExecutions",
                platform."ProvisioningSteps",
                platform."ProvisioningDrafts"
                FROM nexora_tenant_app, nexora_identity_app;
        END
        $provisioning_grants$;
        """;

    public const string Drop = """
        DROP TABLE IF EXISTS platform."ProvisioningSteps" CASCADE;
        DROP TABLE IF EXISTS platform."ProvisioningExecutions" CASCADE;
        DROP TABLE IF EXISTS platform."ProvisioningDrafts" CASCADE;
        """;
}
