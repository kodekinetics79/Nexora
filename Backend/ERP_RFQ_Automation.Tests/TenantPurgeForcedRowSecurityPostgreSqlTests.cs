using ERP_RFQ_Automation.Infrastructure;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Platform.Lifecycle;
using ERP_RFQ_Automation.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;
using Testcontainers.PostgreSql;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// The tenant purge against a database whose OWNER is bound by its own row-level security.
///
/// <para><b>Why this needs a container of its own, and why nothing in the existing PostgreSQL lane
/// could ever have caught the defect it covers.</b> <c>PostgreSqlTestDatabase</c> connects as the
/// container's <c>POSTGRES_USER</c>, which Testcontainers creates as a SUPERUSER, and a superuser
/// bypasses every row-level-security policy unconditionally. Under that role the purge works, the
/// suite is green, and the property under test here does not exist. What production actually has —
/// where <c>ConnectionStrings:MigrationConnection</c> is absent and
/// <c>Program.cs ResolveDirectMigrationConnection</c> reuses the runtime username — is a schema
/// owner that is <c>NOSUPERUSER</c>, <c>NOBYPASSRLS</c> and <c>NOINHERIT</c>, exactly as
/// <c>ValidateRuntimeDatabaseRoleAsync</c> demands. This class builds that, by owning its own
/// container: the execution roles are cluster-scoped, so it cannot share one.</para>
///
/// <para><b>The defect.</b> 110 of 232 tables in this schema are declared
/// <c>FORCE ROW LEVEL SECURITY</c>, which makes the owner subject to its own policies; 100 of them
/// are tables the purge sweeps. Every tenant policy is written <c>TO nexora_tenant_app</c>, and
/// PostgreSQL matches a policy's role list with <c>has_privs_of_role()</c>, which for a
/// <c>NOINHERIT</c> role does not include roles it is merely a member of. So on those 100 tables
/// the owner's <c>DELETE</c> matched no policy, was denied by default, affected zero rows, and
/// raised nothing. <c>TenantPurgeExecutor</c> recorded a table only when <c>rows &gt; 0</c>, so
/// they were absent from the report rather than reported as failures — while
/// <c>public."BusinessUnits"</c>, which is not forced, WAS deleted. The tenant disappeared from
/// every screen and their data stayed.</para>
///
/// <para><b>The reason the owner can enter replica mode here.</b> A plain non-superuser cannot set
/// <c>session_replication_role</c> at all — verified, it answers 42501 — so the fixture grants
/// <c>SET ON PARAMETER</c> explicitly. That is not a contrivance to make the bug appear: it is the
/// portable spelling of what every managed provider hands its schema owner without superuser and
/// without BYPASSRLS (<c>rds_superuser</c>, <c>azure_pg_admin</c>, <c>cloudsqlsuperuser</c>).
/// Without it the purge fails loudly at its first statement, which is a different and much less
/// interesting failure.</para>
/// </summary>
public sealed class TenantPurgeForcedRowSecurityPostgreSqlTests : IAsyncLifetime
{
    private const string OwnerRole = "nexora_schema_owner";
    private const string OwnerPassword = "nexora-owner";
    private const string OwnedDatabase = "nexora_owner_lane";

    /// <summary>Purged.</summary>
    private const long TenantABusinessUnit = 90_001;

    /// <summary>The control. Every assertion about it is "unchanged".</summary>
    private const long TenantBBusinessUnit = 90_002;

    private const long TenantAId = 90_001;

    /// <summary>
    /// One forced and one enable-only table, both real, both swept by the catalogue-driven target
    /// query. The pair is the whole point: before the fix the first returned <c>DELETE 0</c> and
    /// the second <c>DELETE 1</c> in the same transaction, which is why the failure was invisible.
    /// </summary>
    private const string ForcedTable = "public.\"CommercialMatchingPolicies\"";
    private const string EnableOnlyTable = "public.\"QuoteConfiguration\"";

    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:16-alpine")
        .WithDatabase("nexora_bootstrap")
        .WithUsername("nexora")
        .WithPassword("nexora-tests")
        .Build();

    private string _superuserConnectionString = null!;
    private string _ownerConnectionString = null!;

    public async Task InitializeAsync()
    {
        AppContext.SetSwitch("Npgsql.EnableLegacyTimestampBehavior", true);
        await _container.StartAsync();
        _superuserConnectionString = _container.GetConnectionString();

        // ---- the role topology, built by the superuser exactly once -------------------------
        // CREATEROLE because this role runs the baseline, and the baseline creates the three
        // execution roles. NOINHERIT / NOSUPERUSER / NOBYPASSRLS because that is what
        // Program.cs ValidateRuntimeDatabaseRoleAsync requires of the role the runtime connects
        // as, and ResolveDirectMigrationConnection makes that same role the schema owner.
        await ExecuteAsync(_superuserConnectionString, $"""
            CREATE ROLE {OwnerRole} LOGIN PASSWORD '{OwnerPassword}'
                NOSUPERUSER NOCREATEDB CREATEROLE NOINHERIT NOBYPASSRLS;
            GRANT SET ON PARAMETER session_replication_role TO {OwnerRole};
            """);

        // The three execution roles are created by the superuser rather than by the baseline, and
        // that is not a shortcut. PostgreSQL 16 forbids a CREATEROLE role from creating a role
        // with an attribute it does not itself hold, and two of these are BYPASSRLS while the
        // owner deliberately is not — so a NOBYPASSRLS owner CANNOT create them, and a database
        // in this shape necessarily got them out of band. Declared exactly as
        // MigrationsBaseline/Sql/00_execution_roles.sql declares them, so the IF NOT EXISTS
        // guards in the baseline find them and skip.
        await ExecuteAsync(_superuserConnectionString, $"""
            CREATE ROLE nexora_tenant_app   NOLOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT NOBYPASSRLS;
            CREATE ROLE nexora_identity_app NOLOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT BYPASSRLS;
            CREATE ROLE nexora_pipeline_app NOLOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT BYPASSRLS;
            GRANT nexora_tenant_app, nexora_identity_app, nexora_pipeline_app
                TO {OwnerRole} WITH ADMIN OPTION;
            """);
        await ExecuteAsync(_superuserConnectionString, $"CREATE DATABASE {OwnedDatabase} OWNER {OwnerRole};");

        _ownerConnectionString = new NpgsqlConnectionStringBuilder(_superuserConnectionString)
        {
            Username = OwnerRole,
            Password = OwnerPassword,
            Database = OwnedDatabase
        }.ConnectionString;

        _superuserConnectionString = new NpgsqlConnectionStringBuilder(_superuserConnectionString)
        {
            Database = OwnedDatabase
        }.ConnectionString;

        // ---- the schema, created BY the owner -----------------------------------------------
        // ManagedPostgresMigrationCommandInterceptor is the production configuration on the
        // managed target (render.yaml sets Database__AllowManagedOwnerRoleMigrationCompatibility),
        // and it is required here for a narrower reason: 00_execution_roles.sql ends with
        // `ALTER ROLE <current_user> NOINHERIT`, which in PostgreSQL 16 needs ADMIN OPTION on
        // oneself. The role is already NOINHERIT, so the rewrite changes nothing that matters.
        var options = new DbContextOptionsBuilder<ErpRfqAutomationContext>()
            .UseNpgsql(_ownerConnectionString, npgsql => npgsql.CommandTimeout(180))
            .AddInterceptors(new ManagedPostgresMigrationCommandInterceptor())
            .ConfigureWarnings(warnings => warnings.Ignore(RelationalEventId.PendingModelChangesWarning))
            .EnableDetailedErrors()
            .Options;
        await using var context = new ErpRfqAutomationContext(options, new StubTenant(null));
        await context.Database.MigrateAsync();
    }

    public Task DisposeAsync() => _container.DisposeAsync().AsTask();

    // ------------------------------------------------------------------------------- the tests

    /// <summary>
    /// The regression. On the pre-fix executor tenant A's row in the forced table is still there
    /// afterwards and the purge has already reported success.
    /// </summary>
    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task Purge_destroys_rows_in_forced_row_security_tables_and_leaves_the_other_tenant_intact()
    {
        await AssertTheOwnerIsBoundByRowLevelSecurityAsync();
        await SeedAsync();

        var executor = TenantLifecycleHarness.PurgeExecutor(_ownerConnectionString);
        var attempt = await ClaimPurgeAsync();
        var outcome = await executor.ExecuteAsync(
            TenantAId, TenantABusinessUnit, attempt, CancellationToken.None);

        // Counted through a SUPERUSER connection, deliberately. Asking the purge's own identity
        // whether the purge worked is the failure mode this whole class exists to close.
        Assert.Equal(0, await CountAsSuperuserAsync(ForcedTable, "BusinessUnitId", TenantABusinessUnit));
        Assert.Equal(0, await CountAsSuperuserAsync(EnableOnlyTable, "BusinessUnitId", TenantABusinessUnit));
        Assert.Equal(0, await CountAsSuperuserAsync("public.\"BusinessUnits\"", "ID", TenantABusinessUnit));

        // Tenant B is untouched, and it is untouched in the forced table too — which is the one
        // place a fix that simply widened the purge's reach could have gone wrong.
        Assert.Equal(1, await CountAsSuperuserAsync(ForcedTable, "BusinessUnitId", TenantBBusinessUnit));
        Assert.Equal(1, await CountAsSuperuserAsync(EnableOnlyTable, "BusinessUnitId", TenantBBusinessUnit));
        Assert.Equal(1, await CountAsSuperuserAsync("public.\"BusinessUnits\"", "ID", TenantBBusinessUnit));

        // The report names the forced table rather than omitting it, and says how many tables were
        // swept and proved empty — the two facts a reader needs to tell "nothing was there" from
        // "nothing was reachable".
        Assert.Contains(outcome.Deleted, d => d.Table.Contains("CommercialMatchingPolicies"));
        Assert.True(outcome.TablesSwept > 100, $"Only {outcome.TablesSwept} table(s) swept.");
        Assert.Equal(outcome.TablesSwept, outcome.TablesVerifiedEmpty);
    }

    /// <summary>
    /// The number on the confirmation dialog. Asserted separately from the execution because it is
    /// a separate failure with separate consequences: the preview counted on the bare owner
    /// connection, so every forced table counted zero and was dropped by the executor's own
    /// <c>rows &gt; 0</c> filter. An operator authorising destruction was reading a floor and being
    /// shown it as a total — and the tables missing from it were exactly the tables the execution
    /// was also about to miss.
    /// </summary>
    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task Purge_preview_counts_the_rows_held_in_forced_row_security_tables()
    {
        await SeedAsync();

        var preview = await TenantLifecycleHarness.PurgeExecutor(_ownerConnectionString)
            .PreviewAsync(TenantAId, TenantABusinessUnit, CancellationToken.None);

        Assert.Contains(preview.Tables, t => t.Table.Contains("CommercialMatchingPolicies"));
        Assert.Contains(preview.Tables, t => t.Table.Contains("QuoteConfiguration"));

        // Scoped to the tenant being previewed and no further: tenant B holds a row in both of
        // those tables and neither may be counted here.
        Assert.Equal(1, preview.Tables.Single(t => t.Table.Contains("CommercialMatchingPolicies")).Rows);
        Assert.Equal(1, preview.Tables.Single(t => t.Table.Contains("QuoteConfiguration")).Rows);
    }

    /// <summary>
    /// The purge refuses, loudly and by name, when a target is out of its reach — and commits
    /// nothing.
    ///
    /// <para>The break is a dropped <c>nexora_tenant_purge</c> policy, which is exactly what a
    /// table added by a later migration would look like. Note what the assertion is NOT: it is not
    /// "the post-condition count caught it". Verified against a live database, with the policy
    /// dropped the DELETE affects zero rows AND the post-condition count returns zero, because
    /// both are filtered by the same policy. A check that cannot see the rows cannot notice they
    /// are still there, which is why reachability is established from the catalogue first.</para>
    /// </summary>
    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task Purge_refuses_and_rolls_back_when_a_target_is_unreachable()
    {
        await SeedAsync();
        await ExecuteAsync(_superuserConnectionString,
            $"DROP POLICY nexora_tenant_purge ON {ForcedTable};");
        try
        {
            var executor = TenantLifecycleHarness.PurgeExecutor(_ownerConnectionString);
            var attempt = await ClaimPurgeAsync();

            var failure = await Assert.ThrowsAsync<InvalidOperationException>(
                () => executor.ExecuteAsync(TenantAId, TenantABusinessUnit, attempt, CancellationToken.None));
            Assert.Contains("CommercialMatchingPolicies", failure.Message);
            Assert.Contains("nexora_tenant_purge", failure.Message);

            // Nothing committed: not the row it could not reach, and not the rows it could.
            Assert.Equal(1, await CountAsSuperuserAsync(ForcedTable, "BusinessUnitId", TenantABusinessUnit));
            Assert.Equal(1, await CountAsSuperuserAsync(EnableOnlyTable, "BusinessUnitId", TenantABusinessUnit));
            Assert.Equal(1, await CountAsSuperuserAsync("public.\"BusinessUnits\"", "ID", TenantABusinessUnit));

            // And the offboarding record does NOT say the purge executed.
            Assert.Equal(0, await ScalarAsSuperuserAsync(
                $"""
                 SELECT count(*)::bigint FROM platform."TenantOffboardings"
                 WHERE "TenantId" = {TenantAId} AND "PurgeExecutedOn" IS NOT NULL;
                 """));
        }
        finally
        {
            await ExecuteAsync(_superuserConnectionString, $"""
                CREATE POLICY nexora_tenant_purge ON {ForcedTable}
                    AS PERMISSIVE FOR ALL TO nexora_purge_app
                    USING ("BusinessUnitId" = NULLIF(current_setting('nexora.purge_business_unit_id', true), '')::bigint);
                """);
        }
    }

    // ------------------------------------------------------------------------------- assertions

    /// <summary>
    /// Proves the fixture reproduces the production shape before any test leans on it. If any of
    /// these four drift, every assertion above becomes vacuous rather than false — which is the
    /// same class of silence the defect itself was.
    /// </summary>
    private async Task AssertTheOwnerIsBoundByRowLevelSecurityAsync()
    {
        Assert.Equal(1, await ScalarAsSuperuserAsync($"""
            SELECT count(*)::bigint FROM pg_roles
            WHERE rolname = '{OwnerRole}'
              AND NOT rolsuper AND NOT rolbypassrls AND NOT rolinherit;
            """));

        // The schema really is owned by that role, and really does force RLS on the table under
        // test — a baseline applied by anyone else would make this suite prove nothing.
        Assert.Equal(1, await ScalarAsSuperuserAsync($"""
            SELECT count(*)::bigint FROM pg_class c
            WHERE c.oid = '{ForcedTable}'::regclass
              AND c.relforcerowsecurity
              AND pg_get_userbyid(c.relowner) = '{OwnerRole}';
            """));
        Assert.Equal(1, await ScalarAsSuperuserAsync($"""
            SELECT count(*)::bigint FROM pg_class c
            WHERE c.oid = '{EnableOnlyTable}'::regclass
              AND c.relrowsecurity AND NOT c.relforcerowsecurity;
            """));

        // And the owner does not inherit the role its own policies name. This single fact is the
        // mechanism: membership without inheritance is not a policy match.
        Assert.Equal(0, await ScalarAsSuperuserAsync($"""
            SELECT count(*)::bigint
            WHERE pg_has_role('{OwnerRole}', 'nexora_tenant_app', 'USAGE');
            """));

        // The fix must not have handed a request-path role a way into the purge scope. None of the
        // three execution roles is a MEMBER of nexora_purge_app, so none of them can SET ROLE into
        // it whatever GUC they set — the scope GUC alone authorises nothing.
        Assert.Equal(0, await ScalarAsSuperuserAsync("""
            SELECT count(*)::bigint FROM pg_roles r
            WHERE r.rolname IN ('nexora_tenant_app', 'nexora_identity_app', 'nexora_pipeline_app')
              AND pg_has_role(r.rolname, 'nexora_purge_app', 'MEMBER');
            """));

        // And the purge role itself holds no bypass. Its reach is the policies and nothing else.
        Assert.Equal(1, await ScalarAsSuperuserAsync("""
            SELECT count(*)::bigint FROM pg_roles
            WHERE rolname = 'nexora_purge_app'
              AND NOT rolsuper AND NOT rolbypassrls AND NOT rolcanlogin AND NOT rolinherit;
            """));
    }

    // ---------------------------------------------------------------------------------- fixture

    private async Task SeedAsync()
    {
        // session_replication_role = replica keeps the seed to the rows this suite is about. It
        // used to be load-bearing for a second reason: nexora_create_default_ai_policy() is a
        // SECURITY DEFINER AFTER INSERT trigger owned by the schema owner, so its INSERT into the
        // FORCE-protected "AiProcessingPolicies" was evaluated as that owner and refused with "new
        // row violates row-level security policy" — provisioning a business unit was broken in
        // this configuration, independently of the purge. That was reported separately and fixed
        // by 20260811210000_TenantProvisioningSeedsUnderForcedRowSecurity, which
        // TenantProvisioningForcedRowSecurityPostgreSqlTests covers on a fixture of this same
        // shape. Replica mode stays because it is still what suspends the append-only guards and
        // the foreign keys for the DELETEs above.
        await ExecuteAsync(_superuserConnectionString, $"""
            SET session_replication_role = replica;

            DELETE FROM {ForcedTable}     WHERE "BusinessUnitId" IN ({TenantABusinessUnit}, {TenantBBusinessUnit});
            DELETE FROM {EnableOnlyTable} WHERE "BusinessUnitId" IN ({TenantABusinessUnit}, {TenantBBusinessUnit});
            DELETE FROM public."BusinessUnits" WHERE "ID" IN ({TenantABusinessUnit}, {TenantBBusinessUnit});
            DELETE FROM platform."TenantOffboardings" WHERE "TenantId" = {TenantAId};

            INSERT INTO public."BusinessUnits" ("ID", "BusinessUnitCode", "BusinessUnitName", "CreatedBy")
            VALUES ({TenantABusinessUnit}, 'PURGE-A', 'Tenant A', 'tests'),
                   ({TenantBBusinessUnit}, 'PURGE-B', 'Tenant B', 'tests');

            INSERT INTO {ForcedTable} ("BusinessUnitId")
            VALUES ({TenantABusinessUnit}), ({TenantBBusinessUnit});

            INSERT INTO {EnableOnlyTable} ("BusinessUnitId", "CompanyEmail")
            VALUES ({TenantABusinessUnit}, 'a@tenant-a.test'),
                   ({TenantBBusinessUnit}, 'b@tenant-b.test');

            SET session_replication_role = origin;
            """);
    }

    /// <summary>
    /// The fence the executor locks and checks before it deletes anything: an offboarding at
    /// PendingDeletion, with a started-but-not-executed attempt and no legal hold.
    /// </summary>
    private async Task<Guid> ClaimPurgeAsync()
    {
        var attempt = Guid.NewGuid();
        await ExecuteAsync(_superuserConnectionString, $"""
            SET session_replication_role = replica;
            DELETE FROM platform."TenantOffboardings" WHERE "TenantId" = {TenantAId};
            INSERT INTO platform."TenantOffboardings"
                ("TenantId", "Stage", "PurgeStartedOn", "PurgeAttemptId", "CreatedOn")
            VALUES ({TenantAId}, 'PendingDeletion', now(), '{attempt}', now());
            SET session_replication_role = origin;
            """);
        return attempt;
    }

    private Task<long> CountAsSuperuserAsync(string table, string column, long scope)
        => ScalarAsSuperuserAsync($"""SELECT count(*)::bigint FROM {table} WHERE "{column}" = {scope};""");

    private async Task<long> ScalarAsSuperuserAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(_superuserConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        return (long)(await command.ExecuteScalarAsync())!;
    }

    private static async Task ExecuteAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }
}
