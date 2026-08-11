using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using Testcontainers.PostgreSql;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// The test whose absence cost a production deploy.
///
/// WHAT WENT WRONG.
///   20260811033109_SquashedSchemaBaseline replays a `pg_dump --schema-only`. A dump assumes an
///   empty database. The deployed database was not empty. Render was still serving a container 40
///   commits old, so production sat at the PRE-squash head - the whole schema materialised, 134
///   __EFMigrationsHistory rows, and NO row naming the baseline. Program.cs defaults
///   Database:ApplyMigrationsOnStartup to true in Production and calls MigrateAsync() uncaught
///   before the app serves, so EF replayed the baseline onto a database that already had every
///   object in it. Replaying the pre-fix baseline onto that state raises 3,014 errors - 1,520
///   42P07 duplicate_table, 1,083 42710 duplicate_object, 266 42P16, 142 42723, 2 42701 and one
///   42P06 - the first of which killed the process at boot. The deploy was marked failed, the old
///   container kept serving, and the fix for the problem could not deploy because of the problem.
///
/// WHAT THE SUITE COULD NOT SEE.
///   SquashedBaselineMigrationPostgreSqlTests walks the baseline Up, Down and Up again. That
///   second Up lands on the ground Down cleared, which is EMPTY - the one starting state where a
///   bare CREATE cannot collide. Every other PostgreSQL test starts from an empty container too.
///   Nothing in 4,466 tests ever pointed a migration at a database that already had the schema,
///   which is the only state production was ever going to be in.
///
/// WHAT THIS TEST PINS.
///   Applying the baseline to a database that already carries its schema must SUCCEED, and must
///   be a genuine no-op: same catalogue digest, and the tenant-isolation controls still present in
///   exactly the numbers the 134 migrations produced. A guard that silently skips a policy that
///   SHOULD have been created is the failure mode worth fearing here - it leaves a table readable
///   across tenants and raises nothing - so the counts are asserted as equalities, not floors.
///
/// It runs in a DEDICATED container for the same reason the Down/Up walk does: the baseline
/// creates cluster-scoped roles, and it rewrites __EFMigrationsHistory, neither of which any other
/// test in the shared fixture would survive.
/// </summary>
public sealed class SquashedBaselineIdempotencyPostgreSqlTests
{
    private const string Baseline = "20260811033109_SquashedSchemaBaseline";

    /// <summary>
    /// The head the 134 pre-baseline migrations ended on, and therefore the last row production's
    /// __EFMigrationsHistory holds. Same constant stamp-existing-database.sql refuses to act
    /// without.
    /// </summary>
    private const string PreSquashHead = "20260810233008_PlatformMfaPolicyAndBrowserTrust";

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task Baseline_applies_to_a_database_that_already_has_the_schema_and_changes_nothing()
    {
        AppContext.SetSwitch("Npgsql.EnableLegacyTimestampBehavior", true);
        await using var container = new PostgreSqlBuilder("postgres:16-alpine")
            .WithDatabase("nexora_baseline_idempotency")
            .WithUsername("nexora")
            .WithPassword("nexora-tests")
            .Build();
        await container.StartAsync();

        await using var context = ContextFor(container.GetConnectionString());
        var migrator = context.GetService<IMigrator>();

        // ---- 1. build the schema the deployed database already has -------------------------
        // Migrate to the baseline and no further. Pinning here rather than at head is what keeps
        // the counts below meaningful: they are the counts the 134 migrations produced, and they
        // must not have to be edited every time a migration lands on top.
        await migrator.MigrateAsync(Baseline);
        var built = await FingerprintAsync(context);
        AssertTenantIsolationControls(built, "after building the schema from empty");

        // ---- 2. rewind the bookkeeping to production's -------------------------------------
        // The schema stays. Only the history moves back to the pre-squash head, which is the exact
        // state Render's database was in: every object present, and no row naming the baseline, so
        // EF has no way to know the schema is already there and will replay the whole dump.
        await context.Database.ExecuteSqlRawAsync(
            $"""
             DELETE FROM public."__EFMigrationsHistory";
             INSERT INTO public."__EFMigrationsHistory" ("MigrationId", "ProductVersion")
             VALUES ('{PreSquashHead}', '9.0.9');
             """);
        Assert.False(
            (await AppliedMigrationsAsync(context)).Contains(Baseline),
            "The rewind must leave the baseline UNAPPLIED, or this test proves nothing.");

        // ---- 3. the boot that failed -------------------------------------------------------
        // Before the baseline SQL was made idempotent this line threw
        // Npgsql.PostgresException 42P06 / 42P07 and took the process down with it.
        var replay = await Record.ExceptionAsync(() => migrator.MigrateAsync(Baseline));
        Assert.True(
            replay is null,
            "Replaying the baseline onto a database that already has its schema must succeed. " +
            "This is the deploy Render could not complete: " + replay);

        // ---- 4. and it changed nothing ------------------------------------------------------
        var replayed = await FingerprintAsync(context);
        AssertTenantIsolationControls(replayed, "after replaying the baseline onto the same schema");
        Assert.Equal(built.Digest, replayed.Digest);

        // EF records the baseline against a history that still holds the stale pre-squash row. It
        // is not required to be tidy, only to be true: the schema IS at the baseline.
        var afterReplay = await AppliedMigrationsAsync(context);
        Assert.Contains(Baseline, afterReplay);
        Assert.Contains(PreSquashHead, afterReplay);

        // ---- 5. the rest of the chain still applies on top ----------------------------------
        // Production does not stop at the baseline; the migrations that landed after it have to go
        // on next, onto that same untidy history. This is the actual production upgrade path.
        var upgrade = await Record.ExceptionAsync(() => migrator.MigrateAsync());
        Assert.True(upgrade is null, "Migrations after the baseline must apply on top: " + upgrade);

        // ---- 6. and it lands where a fresh deploy lands -------------------------------------
        // A separate database in the same cluster, migrated from empty to the same head. If the
        // upgraded database and the fresh one differ by a single policy, trigger, grant, index or
        // constraint, the digests differ.
        var freshConnection = await CreateSiblingDatabaseAsync(container.GetConnectionString(), "nexora_fresh_head");
        await using var fresh = ContextFor(freshConnection);
        await fresh.GetService<IMigrator>().MigrateAsync();

        var upgraded = await FingerprintAsync(context);
        var fromEmpty = await FingerprintAsync(fresh);
        Assert.Equal(fromEmpty.Digest, upgraded.Digest);
        Assert.Equal(fromEmpty.Tables, upgraded.Tables);
        Assert.Equal(fromEmpty.Policies, upgraded.Policies);
        Assert.Equal(fromEmpty.ForcedTables, upgraded.ForcedTables);
        Assert.Equal(fromEmpty.Triggers, upgraded.Triggers);
        Assert.Equal(fromEmpty.Functions, upgraded.Functions);
        Assert.Equal(fromEmpty.ExcludeConstraints, upgraded.ExcludeConstraints);
    }

    /// <summary>
    /// The tenant-isolation boundary, counted. Equalities and not floors: the point of a guarded
    /// CREATE is that it can silently do nothing, and a policy that quietly failed to appear is a
    /// cross-tenant read, not a test failure anyone would notice later.
    /// </summary>
    private static void AssertTenantIsolationControls(BaselineFingerprint f, string when)
    {
        Assert.Equal(232, f.Policies);
        Assert.Equal(232, f.RowSecurityTables);
        Assert.Equal(110, f.ForcedTables);
        Assert.Equal(300, f.Triggers);
        Assert.Equal(32, f.AlwaysEnabledTriggers);
        Assert.Equal(142, f.Functions);
        Assert.Equal(36, f.SecurityDefinerFunctions);
        Assert.Equal(2, f.ExcludeConstraints);
        Assert.Equal(266, f.Tables);
        Assert.Equal(3, f.ExecutionRoles);
        Assert.True(f.Digest.Length == 32, $"Empty catalogue digest {when}.");
    }

    private static ErpRfqAutomationContext ContextFor(string connectionString) =>
        new(new DbContextOptionsBuilder<ErpRfqAutomationContext>()
                .UseNpgsql(connectionString)
                .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning))
                .EnableDetailedErrors()
                .Options,
            new StubTenant(null));

    private static async Task<string> CreateSiblingDatabaseAsync(string connectionString, string name)
    {
        var admin = new NpgsqlConnectionStringBuilder(connectionString);
        await using (var connection = new NpgsqlConnection(admin.ConnectionString))
        {
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand($"CREATE DATABASE \"{name}\"", connection);
            await command.ExecuteNonQueryAsync();
        }

        return new NpgsqlConnectionStringBuilder(connectionString) { Database = name }.ConnectionString;
    }

    private static async Task<HashSet<string>> AppliedMigrationsAsync(DbContext context) =>
        (await context.Database.GetAppliedMigrationsAsync()).ToHashSet(StringComparer.Ordinal);

    private static async Task<BaselineFingerprint> FingerprintAsync(DbContext context) =>
        await context.Database.SqlQueryRaw<BaselineFingerprint>(FingerprintSql).SingleAsync();

    /// <summary>
    /// Catalogue counts plus one digest over everything the squash was supposed to preserve:
    /// tables and their RLS/FORCE flags, policies and their predicates, triggers and their enable
    /// modes, function bodies and SECURITY DEFINER flags, indexes, constraint definitions and the
    /// grants held by the three execution roles.
    ///
    /// Extension-owned objects are excluded via pg_depend deptype 'e' throughout, for the reason
    /// SquashedBaselineMigrationPostgreSqlTests documents: citext, pgcrypto and btree_gist install
    /// 269 functions into public that the baseline never created and must never be counted as
    /// though it had.
    /// </summary>
    private const string FingerprintSql = """
        WITH scope(nspname) AS (VALUES ('public'), ('platform')),
        tables AS (
            SELECT n.nspname, c.relname, c.relrowsecurity, c.relforcerowsecurity
            FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
            JOIN scope ON scope.nspname = n.nspname
            WHERE c.relkind = 'r' AND c.relname <> '__EFMigrationsHistory'
              AND NOT EXISTS (SELECT 1 FROM pg_depend d
                              WHERE d.objid = c.oid AND d.classid = 'pg_class'::regclass
                                AND d.deptype = 'e')
        ),
        policies AS (
            SELECT n.nspname, c.relname, p.polname,
                   pg_get_expr(p.polqual, p.polrelid) AS qual,
                   pg_get_expr(p.polwithcheck, p.polrelid) AS with_check
            FROM pg_policy p JOIN pg_class c ON c.oid = p.polrelid
            JOIN pg_namespace n ON n.oid = c.relnamespace
            JOIN scope ON scope.nspname = n.nspname
        ),
        triggers AS (
            SELECT n.nspname, c.relname, t.tgname, t.tgenabled::text AS enabled
            FROM pg_trigger t JOIN pg_class c ON c.oid = t.tgrelid
            JOIN pg_namespace n ON n.oid = c.relnamespace
            JOIN scope ON scope.nspname = n.nspname
            WHERE NOT t.tgisinternal
        ),
        routines AS (
            SELECT n.nspname, p.proname, p.prosecdef,
                   md5(CASE WHEN p.prokind = 'f' THEN pg_get_functiondef(p.oid) ELSE '' END) AS body
            FROM pg_proc p JOIN pg_namespace n ON n.oid = p.pronamespace
            JOIN scope ON scope.nspname = n.nspname
            WHERE p.prokind = 'f'
              AND NOT EXISTS (SELECT 1 FROM pg_depend d
                              WHERE d.objid = p.oid AND d.classid = 'pg_proc'::regclass
                                AND d.deptype = 'e')
        ),
        indexes AS (
            SELECT i.schemaname, i.tablename, i.indexname, i.indexdef
            FROM pg_indexes i JOIN scope ON scope.nspname = i.schemaname
            JOIN pg_class c ON c.relname = i.indexname
            JOIN pg_namespace n ON n.oid = c.relnamespace AND n.nspname = i.schemaname
            WHERE NOT EXISTS (SELECT 1 FROM pg_depend d
                              WHERE d.objid = c.oid AND d.classid = 'pg_class'::regclass
                                AND d.deptype = 'e')
        ),
        constraints AS (
            SELECT n.nspname, c.relname, con.conname, con.contype::text AS contype,
                   pg_get_constraintdef(con.oid) AS definition
            FROM pg_constraint con JOIN pg_class c ON c.oid = con.conrelid
            JOIN pg_namespace n ON n.oid = c.relnamespace
            JOIN scope ON scope.nspname = n.nspname
        ),
        grants AS (
            SELECT table_schema, table_name, grantee, privilege_type
            FROM information_schema.table_privileges
            WHERE grantee IN ('nexora_tenant_app', 'nexora_identity_app', 'nexora_pipeline_app')
        )
        SELECT
            (SELECT count(*)::int FROM tables) AS "Tables",
            (SELECT count(*)::int FROM tables WHERE relrowsecurity) AS "RowSecurityTables",
            (SELECT count(*)::int FROM tables WHERE relforcerowsecurity) AS "ForcedTables",
            (SELECT count(*)::int FROM policies) AS "Policies",
            (SELECT count(*)::int FROM triggers) AS "Triggers",
            (SELECT count(*)::int FROM triggers WHERE enabled = 'A') AS "AlwaysEnabledTriggers",
            (SELECT count(*)::int FROM routines) AS "Functions",
            (SELECT count(*)::int FROM routines WHERE prosecdef) AS "SecurityDefinerFunctions",
            (SELECT count(*)::int FROM constraints WHERE contype = 'x') AS "ExcludeConstraints",
            (SELECT count(*)::int FROM pg_roles
             WHERE rolname IN ('nexora_tenant_app', 'nexora_identity_app', 'nexora_pipeline_app')) AS "ExecutionRoles",
            md5(concat_ws('|',
                (SELECT string_agg(format('%s.%s:%s:%s', nspname, relname, relrowsecurity, relforcerowsecurity), E'\n'
                        ORDER BY nspname, relname) FROM tables),
                (SELECT string_agg(format('%s.%s:%s:%s:%s', nspname, relname, polname, coalesce(qual, ''), coalesce(with_check, '')), E'\n'
                        ORDER BY nspname, relname, polname) FROM policies),
                (SELECT string_agg(format('%s.%s:%s:%s', nspname, relname, tgname, enabled), E'\n'
                        ORDER BY nspname, relname, tgname) FROM triggers),
                (SELECT string_agg(format('%s.%s:%s:%s', nspname, proname, prosecdef, body), E'\n'
                        ORDER BY nspname, proname, body) FROM routines),
                (SELECT string_agg(format('%s.%s:%s:%s', schemaname, tablename, indexname, indexdef), E'\n'
                        ORDER BY schemaname, tablename, indexname) FROM indexes),
                (SELECT string_agg(format('%s.%s:%s:%s', nspname, relname, conname, definition), E'\n'
                        ORDER BY nspname, relname, conname) FROM constraints),
                (SELECT string_agg(format('%s.%s:%s:%s', table_schema, table_name, grantee, privilege_type), E'\n'
                        ORDER BY table_schema, table_name, grantee, privilege_type) FROM grants)
            )) AS "Digest"
        """;

    private sealed record BaselineFingerprint(
        int Tables,
        int RowSecurityTables,
        int ForcedTables,
        int Policies,
        int Triggers,
        int AlwaysEnabledTriggers,
        int Functions,
        int SecurityDefinerFunctions,
        int ExcludeConstraints,
        int ExecutionRoles,
        string Digest);
}
