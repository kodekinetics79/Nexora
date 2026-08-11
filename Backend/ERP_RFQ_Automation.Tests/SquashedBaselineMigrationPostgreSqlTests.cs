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
/// The one surviving migration walk.
///
/// Before 20260811033109_SquashedSchemaBaseline, eleven tests walked a populated database up to a
/// migration, back down, and up again. A squash makes that arithmetically impossible: there is one
/// migration, so there is nowhere to walk to. What a squash does NOT remove is the baseline's own
/// Down — 384 lines of hand-generated DROP statements in MigrationsBaseline/Sql/90_down.sql that
/// nothing else in the suite executes.
///
/// WHY THIS IS WORTH A TEST AND NOT THEATRE.
///   No operator will ever run this Down against a production database with data in it: a
///   baseline's Down is "drop the entire schema", and the recovery path for a bad deploy is a
///   restore, not a rollback. Judged only as a rollback rehearsal, this would be theatre.
///
///   It earns its place as a COMPLETENESS check on the baseline itself. Up() replays twelve
///   generated SQL files; Down() drops a list that was generated separately. Nothing keeps the two
///   in step. Add an object to the Up scripts and forget the Down script and the residue
///   assertions below catch it — the drop list, not the create list, is what this catches. It also
///   keeps `dotnet ef migrations script` honest in the down direction, which is otherwise generated
///   and never executed.
///
///   WHERE THAT SIGNAL LIVES NOW. It used to live in the second Up failing on "already exists".
///   It does not any more: the Up scripts were made idempotent so the deploy could apply the
///   baseline to a database that already carried the schema (see
///   SquashedBaselineIdempotencyPostgreSqlTests for why), and an idempotent Up steps over residue
///   silently instead of erroring. What still catches a forgotten DROP is the block of
///   Assert.Equal(0, afterDown.*) checks below — they run BEFORE the second Up and fail on the
///   leftover object directly, which is a better error than "already exists" ever was. Do not
///   weaken them into "greater than" or "roughly"; they are now the whole check.
///
///   Its value is bounded and the bound is worth stating: this proves Up and Down agree with each
///   other. It does not prove either agrees with the 134-migration chain — that is what
///   Support/schema-parity-queries.sql proved, once, against DB_A.
///
/// It runs in a DEDICATED container, not the shared collection fixture, and that is not incidental:
/// Down ends with DROP ROLE nexora_tenant_app / nexora_identity_app / nexora_pipeline_app. Roles are
/// cluster-scoped, not database-scoped, so running this inside the shared test cluster would either
/// fail on the grants those roles still hold in the fixture's own database, or — if it did not —
/// take every other PostgreSQL test in the run down with it.
/// </summary>
public sealed class SquashedBaselineMigrationPostgreSqlTests
{
    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task Baseline_down_reverts_exactly_what_its_up_creates_and_up_replays_identically()
    {
        AppContext.SetSwitch("Npgsql.EnableLegacyTimestampBehavior", true);
        await using var container = new PostgreSqlBuilder("postgres:16-alpine")
            .WithDatabase("nexora_baseline_walk")
            .WithUsername("nexora")
            .WithPassword("nexora-tests")
            .Build();
        await container.StartAsync();

        var options = new DbContextOptionsBuilder<ErpRfqAutomationContext>()
            .UseNpgsql(container.GetConnectionString())
            .ConfigureWarnings(warnings => warnings.Ignore(RelationalEventId.PendingModelChangesWarning))
            .EnableDetailedErrors()
            .Options;
        await using var context = new ErpRfqAutomationContext(options, new StubTenant(null));
        var migrator = context.GetService<IMigrator>();

        await migrator.MigrateAsync();
        var afterFirstUp = await FingerprintAsync(context);
        // A fingerprint that is trivially small would pass this test while proving nothing, so the
        // shape of the schema is asserted before it is compared with itself.
        Assert.True(afterFirstUp.Tables > 200, $"Baseline created only {afterFirstUp.Tables} tables.");
        Assert.True(afterFirstUp.Policies > 200, $"Baseline created only {afterFirstUp.Policies} policies.");
        Assert.True(afterFirstUp.Functions > 100, $"Baseline created only {afterFirstUp.Functions} functions.");
        Assert.Equal(3, afterFirstUp.ExecutionRoles);

        // "0" is EF's name for the empty database: migrate down past every migration.
        await migrator.MigrateAsync("0");

        // Down is complete: no application table, no application function, no platform schema and
        // no execution role is left behind. Residue here is what makes the second Up fail, and
        // naming it explicitly turns "CREATE TABLE ... already exists" into a readable failure.
        var afterDown = await FingerprintAsync(context);
        Assert.Equal(0, afterDown.Tables);
        Assert.Equal(0, afterDown.Functions);
        Assert.Equal(0, afterDown.Policies);
        Assert.Equal(0, afterDown.Triggers);
        Assert.Equal(0, afterDown.PlatformObjects);
        Assert.Equal(0, afterDown.ExecutionRoles);

        // And Up replays onto the ground Down left. Byte-equal catalogue digests over tables and
        // their RLS flags, policies and their predicates, triggers and their enable modes,
        // functions, indexes, constraints and grants: an Up that is not idempotent, or a Down that
        // silently kept something, changes this string.
        await migrator.MigrateAsync();
        var afterSecondUp = await FingerprintAsync(context);
        Assert.Equal(afterFirstUp.Digest, afterSecondUp.Digest);
        Assert.Equal(afterFirstUp.Tables, afterSecondUp.Tables);
        Assert.Equal(afterFirstUp.Policies, afterSecondUp.Policies);
    }

    private static async Task<CatalogueFingerprint> FingerprintAsync(DbContext context) =>
        await context.Database.SqlQueryRaw<CatalogueFingerprint>(FingerprintSql).SingleAsync();

    /// <summary>
    /// A miniature of Support/schema-parity-queries.sql: enough of the catalogue that a dropped
    /// policy, a trigger that came back as ENABLE ORIGIN instead of ENABLE ALWAYS, a revoked grant
    /// or a missing index all move the digest. Every fragment is ordered explicitly so the digest
    /// never depends on the planner.
    ///
    /// EXTENSION-OWNED OBJECTS ARE EXCLUDED, throughout, via pg_depend deptype 'e'. This is not a
    /// convenience: citext, pgcrypto and btree_gist install 269 functions into public, and
    /// 90_down.sql deliberately does NOT drop the extensions, because on a managed PostgreSQL
    /// instance they may pre-date the application and be shared. Counting them would make the
    /// residue assertion below fail on objects the baseline never created, and would make the
    /// digest move whenever a Postgres image bumps an extension version. What this test is about is
    /// what the baseline's Up creates and its Down claims to drop, and that is exactly the
    /// application-owned set: 130 functions in public plus 12 inside the platform schema.
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
            -- prokind = 'f' only, and the filter is repeated INSIDE the projection as a CASE.
            -- pg_get_functiondef() raises 42809 on an aggregate, citext installs min/max
            -- aggregates into public, and a WHERE clause is not a guarantee that the planner
            -- evaluates the qual before the target list. CASE is short-circuiting and is.
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
            SELECT n.nspname, c.relname, con.conname, pg_get_constraintdef(con.oid) AS definition
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
            (SELECT count(*)::int FROM policies) AS "Policies",
            (SELECT count(*)::int FROM triggers) AS "Triggers",
            (SELECT count(*)::int FROM routines) AS "Functions",
            (SELECT count(*)::int FROM tables WHERE nspname = 'platform')
                + (SELECT count(*)::int FROM routines WHERE nspname = 'platform') AS "PlatformObjects",
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

    private sealed record CatalogueFingerprint(
        int Tables,
        int Policies,
        int Triggers,
        int Functions,
        int PlatformObjects,
        int ExecutionRoles,
        string Digest);
}
