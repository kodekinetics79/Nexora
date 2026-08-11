using ERP_RFQ_Automation.Infrastructure;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;
using Testcontainers.PostgreSql;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// A database owned by a role that is bound by its own row-level security: one container, one
/// baseline, applied by that role. Shared across the class as a fixture rather than rebuilt per
/// test, because the execution roles are cluster-scoped and the baseline takes long enough that
/// six copies of it would be paid for in wall-clock on every run.
/// </summary>
public sealed class ForcedRowSecurityOwnerDatabase : IAsyncLifetime
{
    public const string OwnerRole = "nexora_schema_owner";
    private const string OwnerPassword = "nexora-owner";
    private const string OwnedDatabase = "nexora_provisioning_owner_lane";

    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:16-alpine")
        .WithDatabase("nexora_bootstrap")
        .WithUsername("nexora")
        .WithPassword("nexora-tests")
        .Build();

    /// <summary>The identity every assertion is checked through. Never the one that wrote.</summary>
    public string SuperuserConnectionString { get; private set; } = null!;

    /// <summary>The schema owner: NOSUPERUSER, NOBYPASSRLS, NOINHERIT.</summary>
    public string OwnerConnectionString { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        AppContext.SetSwitch("Npgsql.EnableLegacyTimestampBehavior", true);
        await _container.StartAsync();
        var bootstrap = _container.GetConnectionString();

        // CREATEROLE because this role runs the baseline and the baseline creates the three
        // execution roles. NOINHERIT / NOSUPERUSER / NOBYPASSRLS because that is what
        // Program.cs ValidateRuntimeDatabaseRoleAsync requires of the role the runtime connects
        // as, and ResolveDirectMigrationConnection makes that same role the schema owner.
        await ExecuteAsync(bootstrap, $"""
            CREATE ROLE {OwnerRole} LOGIN PASSWORD '{OwnerPassword}'
                NOSUPERUSER NOCREATEDB CREATEROLE NOINHERIT NOBYPASSRLS;
            """);

        // Created by the superuser rather than by the baseline, and that is not a shortcut:
        // PostgreSQL 16 forbids a CREATEROLE role from creating a role with an attribute it does
        // not itself hold, and two of these are BYPASSRLS while the owner deliberately is not — so
        // a NOBYPASSRLS owner CANNOT create them, and a database in this shape necessarily got
        // them out of band. Declared exactly as MigrationsBaseline/Sql/00_execution_roles.sql
        // declares them, so its IF NOT EXISTS guards find them and skip.
        await ExecuteAsync(bootstrap, $"""
            CREATE ROLE nexora_tenant_app   NOLOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT NOBYPASSRLS;
            CREATE ROLE nexora_identity_app NOLOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT BYPASSRLS;
            CREATE ROLE nexora_pipeline_app NOLOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT BYPASSRLS;
            GRANT nexora_tenant_app, nexora_identity_app, nexora_pipeline_app
                TO {OwnerRole} WITH ADMIN OPTION;
            """);
        await ExecuteAsync(bootstrap, $"CREATE DATABASE {OwnedDatabase} OWNER {OwnerRole};");

        OwnerConnectionString = new NpgsqlConnectionStringBuilder(bootstrap)
        {
            Username = OwnerRole,
            Password = OwnerPassword,
            Database = OwnedDatabase
        }.ConnectionString;

        SuperuserConnectionString = new NpgsqlConnectionStringBuilder(bootstrap)
        {
            Database = OwnedDatabase
        }.ConnectionString;

        // ManagedPostgresMigrationCommandInterceptor is the production configuration on the
        // managed target (render.yaml sets Database__AllowManagedOwnerRoleMigrationCompatibility),
        // and it is required here for a narrower reason: 00_execution_roles.sql ends with
        // `ALTER ROLE <current_user> NOINHERIT`, which in PostgreSQL 16 needs ADMIN OPTION on
        // oneself. The role is already NOINHERIT, so the rewrite changes nothing that matters.
        var options = new DbContextOptionsBuilder<ErpRfqAutomationContext>()
            .UseNpgsql(OwnerConnectionString, npgsql => npgsql.CommandTimeout(180))
            .AddInterceptors(new ManagedPostgresMigrationCommandInterceptor())
            .ConfigureWarnings(warnings => warnings.Ignore(RelationalEventId.PendingModelChangesWarning))
            .EnableDetailedErrors()
            .Options;
        await using var context = new ErpRfqAutomationContext(options, new StubTenant(null));
        await context.Database.MigrateAsync();
    }

    public Task DisposeAsync() => _container.DisposeAsync().AsTask();

    public static async Task ExecuteAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }
}

/// <summary>
/// Creating a tenant on a database whose OWNER is bound by its own row-level security.
///
/// <para><b>THE DEFECT.</b> Tenant creation failed outright — not partially, not silently, but
/// with a 42501 that aborted the transaction — the moment the schema owner was
/// <c>NOSUPERUSER NOBYPASSRLS</c>:</para>
///
/// <code>
/// INSERT INTO public."BusinessUnits" ...
///   -&gt; trigger business_units_create_ai_policy
///   -&gt; nexora_create_default_ai_policy()          [SECURITY DEFINER]
///   -&gt; INSERT INTO public."AiProcessingPolicies"  [FORCE ROW LEVEL SECURITY]
///   -&gt; ERROR 42501: new row violates row-level security policy
/// </code>
///
/// <para>and the identical break on the platform half of the same journey, where
/// <c>platform."Tenants"</c>'s AFTER INSERT trigger seeds the sixteen meter source policies that
/// decide what a tenant may be billed for.</para>
///
/// <para><b>Why nothing in the ordinary PostgreSQL lane could ever have caught this.</b>
/// <c>PostgreSqlTestDatabase</c> connects as the container's <c>POSTGRES_USER</c>, which
/// Testcontainers creates as a SUPERUSER, and that role also OWNS the schema. SECURITY DEFINER
/// makes the effective role the FUNCTION OWNER, so the seeder ran as a superuser and row-level
/// security was never evaluated for its INSERT at all. What production actually has — where
/// <c>ConnectionStrings:MigrationConnection</c> is absent and
/// <c>Program.cs ResolveDirectMigrationConnection</c> reuses the runtime username — is a schema
/// owner that is <c>NOSUPERUSER</c>, <c>NOBYPASSRLS</c> and <c>NOINHERIT</c>, exactly as
/// <c>ValidateRuntimeDatabaseRoleAsync</c> demands, and exactly what every managed provider hands
/// out. This class builds that, by owning its own container: the execution roles are
/// cluster-scoped, so it cannot share one. It is the same fixture shape
/// <c>TenantPurgeForcedRowSecurityPostgreSqlTests</c> established, for the same reason.</para>
///
/// <para><b>What makes the caller irrelevant, and why that is worth restating here.</b> The
/// refusal was measured to be byte-identical whether the caller was the owner directly, held
/// <c>SET ROLE nexora_pipeline_app</c> (BYPASSRLS), held <c>SET ROLE nexora_tenant_app</c> with
/// the tenant GUC set, or was a SUPERUSER. There was no configuration of the application that
/// avoided it, which is why this is a schema fix and not a code fix.</para>
///
/// <para><b>What the fix is.</b> 20260811210000_TenantProvisioningSeedsUnderForcedRowSecurity
/// gives each seeder a GUC naming the one tenant it is creating, and each target table a pair of
/// policies — <c>FOR SELECT</c> for the <c>ON CONFLICT</c> arbiter, <c>FOR INSERT</c> for the row
/// — admitting the function's owner and only for that tenant. Nothing gains BYPASSRLS and FORCE
/// stays on, so the assertions below are as much about what the owner STILL cannot do as about
/// what it now can.</para>
/// </summary>
public sealed class TenantProvisioningForcedRowSecurityPostgreSqlTests(ForcedRowSecurityOwnerDatabase database)
    : IClassFixture<ForcedRowSecurityOwnerDatabase>
{
    private const long OwnerPathBusinessUnit = 96_001;
    private const long PipelineRolePathBusinessUnit = 96_002;
    private const long ScopeProbeBusinessUnit = 96_003;
    private const long NeighbourBusinessUnit = 96_004;
    private const long GucRestoreBusinessUnit = 96_005;
    private const long ConflictBranchBusinessUnit = 96_006;

    private const long PlatformTenantId = 96_101;

    // ------------------------------------------------------------------------------- the tests

    /// <summary>
    /// The regression. Before the fix this INSERT raised 42501 from inside
    /// <c>nexora_create_default_ai_policy()</c> and no business unit was created at all.
    /// </summary>
    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task Creating_a_business_unit_seeds_its_fail_closed_ai_policy()
    {
        await AssertTheOwnerIsBoundByRowLevelSecurityAsync();

        await CreateBusinessUnitAsync(OwnerPathBusinessUnit, "PROV-OWNER");

        // Counted through a SUPERUSER connection, deliberately. Asking the identity that did the
        // write whether the write happened is the failure mode this whole class exists to close.
        Assert.Equal(1L, await CountAsSuperuserAsync(
            "public.\"AiProcessingPolicies\"", "BusinessUnitId", OwnerPathBusinessUnit));

        // The defaults are the point of the row, not a detail of it: an AI governance row that
        // arrives open is worse than one that never arrives.
        Assert.Equal("tenant-provisioning|30|TenantApprovedRegion|RedactedFieldsOnly|false|true",
            await StringAsync(database.SuperuserConnectionString, $"""
                SELECT "UpdatedBy" || '|' || "RetentionDays" || '|' || "DataResidency"
                       || '|' || "EgressPolicy" || '|' || "ExternalProcessingAllowed"
                       || '|' || "IsEnabled"
                FROM public."AiProcessingPolicies"
                WHERE "BusinessUnitId" = {OwnerPathBusinessUnit};
                """));
    }

    /// <summary>
    /// The same journey entered the way provisioning actually enters it.
    /// <c>ProvisioningStepExecutor</c> runs as <c>nexora_pipeline_app</c>, and its BYPASSRLS is
    /// exactly the thing that does NOT help: SECURITY DEFINER discards the caller's identity at the
    /// function boundary. Asserted separately from the owner path so that a fix which worked for
    /// only one of them cannot pass.
    /// </summary>
    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task Creating_a_business_unit_as_the_pipeline_role_seeds_its_ai_policy_too()
    {
        await ForcedRowSecurityOwnerDatabase.ExecuteAsync(database.OwnerConnectionString, $"""
            SET ROLE nexora_pipeline_app;
            INSERT INTO public."BusinessUnits" ("ID", "BusinessUnitCode", "BusinessUnitName", "CreatedBy")
            VALUES ({PipelineRolePathBusinessUnit}, 'PROV-PIPE', 'Pipeline path', 'provisioning-tests');
            """);

        Assert.Equal(1L, await CountAsSuperuserAsync(
            "public.\"AiProcessingPolicies\"", "BusinessUnitId", PipelineRolePathBusinessUnit));
    }

    /// <summary>
    /// The platform half of the same tenant-creation journey, and a second instance of the same
    /// defect, found by sweeping the catalogue for SECURITY DEFINER functions that write FORCE
    /// tables. <c>platform."TenantMeterSourcePolicies"</c> carries one policy and it is
    /// <c>TO nexora_pipeline_app</c> rather than <c>TO nexora_tenant_app</c> — which changes
    /// nothing, because the effective role inside the seeder is the owner either way.
    /// </summary>
    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task Creating_a_platform_tenant_seeds_its_meter_source_policies()
    {
        await ForcedRowSecurityOwnerDatabase.ExecuteAsync(database.OwnerConnectionString, $"""
            INSERT INTO platform."Tenants" ("Id", "Name", "Slug", "Status", "CreatedOn")
            VALUES ({PlatformTenantId}, 'Provisioning Co', 'provisioning-co', 'Active', now());
            """);

        Assert.Equal(16L, await CountAsSuperuserAsync(
            "platform.\"TenantMeterSourcePolicies\"", "TenantId", PlatformTenantId));

        // The billing-relevant half of what was seeded. If these arrived as anything else the row
        // count above would still be 16 and the tenant would be billed on the wrong basis.
        Assert.Equal(4L, await ScalarAsync(database.SuperuserConnectionString, $"""
            SELECT count(*)::bigint FROM platform."TenantMeterSourcePolicies"
            WHERE "TenantId" = {PlatformTenantId} AND "Mode" = 'LegacyAuthoritative';
            """));
    }

    /// <summary>
    /// FORCE is still real for the owner, which is the half of the fix that is easy to lose.
    ///
    /// <para>The seeder policies admit the owner for ONE tenant, named by a GUC. With no GUC set
    /// the owner reads nothing at all from either table — not the row it just created, not any
    /// other tenant's. A fix that had simply admitted the owner, or turned FORCE off, or handed
    /// something BYPASSRLS, fails here rather than in some later audit.</para>
    /// </summary>
    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task The_owner_reads_nothing_without_the_provisioning_scope_and_one_tenant_with_it()
    {
        await CreateBusinessUnitAsync(ScopeProbeBusinessUnit, "PROV-SCOPE");
        await CreateBusinessUnitAsync(NeighbourBusinessUnit, "PROV-NEIGHBOUR");

        Assert.Equal(0L, await ScalarAsync(database.OwnerConnectionString, """
            SELECT count(*)::bigint FROM public."AiProcessingPolicies";
            """));
        Assert.Equal(0L, await ScalarAsync(database.OwnerConnectionString, """
            SELECT count(*)::bigint FROM platform."TenantMeterSourcePolicies";
            """));

        // With the scope set, exactly the one tenant it names — and demonstrably not the
        // neighbouring tenant, which also has a row.
        await using var connection = new NpgsqlConnection(database.OwnerConnectionString);
        await connection.OpenAsync();
        await using (var scope = connection.CreateCommand())
        {
            scope.CommandText =
                $"SELECT set_config('nexora.provisioning_business_unit_id', '{ScopeProbeBusinessUnit}', false);";
            await scope.ExecuteNonQueryAsync();
        }

        await using var visible = connection.CreateCommand();
        visible.CommandText = """
            SELECT count(*)::text || '|' || coalesce(min("BusinessUnitId")::text, '-')
            FROM public."AiProcessingPolicies";
            """;
        Assert.Equal($"1|{ScopeProbeBusinessUnit}", (await visible.ExecuteScalarAsync()) as string);
    }

    /// <summary>
    /// The seeder puts back the scope it borrowed.
    ///
    /// <para>The GUC could not be declared in the function's own SET clause — a custom placeholder
    /// there is validated by <c>validate_option_array_item</c>, which requires SUPERUSER, and this
    /// owner is not one; it answers <c>permission denied to set parameter</c> at CREATE FUNCTION
    /// time. So the restore is written by hand, and a hand-written restore is exactly the kind of
    /// thing that silently stops happening. A leaked scope would leave the rest of the caller's
    /// transaction able to read one tenant's AI governance row through a policy nobody remembered
    /// was open.</para>
    /// </summary>
    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task The_seeder_restores_the_provisioning_scope_it_borrowed()
    {
        await using var connection = new NpgsqlConnection(database.OwnerConnectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();

        await using (var borrow = connection.CreateCommand())
        {
            borrow.CommandText = $"""
                SELECT set_config('nexora.provisioning_business_unit_id', 'SENTINEL', true);
                INSERT INTO public."BusinessUnits" ("ID", "BusinessUnitCode", "BusinessUnitName", "CreatedBy")
                VALUES ({GucRestoreBusinessUnit}, 'PROV-GUC', 'Guc restore', 'provisioning-tests');
                """;
            await borrow.ExecuteNonQueryAsync();
        }

        await using var observed = connection.CreateCommand();
        observed.CommandText = "SELECT current_setting('nexora.provisioning_business_unit_id', true);";
        Assert.Equal("SENTINEL", (await observed.ExecuteScalarAsync()) as string);

        await transaction.RollbackAsync();
    }

    /// <summary>
    /// The seeder is idempotent, which is the property the <c>ON CONFLICT DO NOTHING</c> exists for
    /// and the property that needed a READ-permitting policy to keep.
    ///
    /// <para>An <c>ON CONFLICT</c> arbiter check reads the target. Measured with only the
    /// <c>FOR INSERT</c> policy in place: the plain INSERT passed its WITH CHECK and reached the
    /// foreign key, while the identical INSERT with <c>ON CONFLICT</c> was still refused with "new
    /// row violates row-level security policy". This drives the conflicting branch directly rather
    /// than trusting that it is covered.</para>
    /// </summary>
    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task The_seeder_takes_the_on_conflict_branch_without_being_refused()
    {
        await CreateBusinessUnitAsync(ConflictBranchBusinessUnit, "PROV-CONFLICT");

        // The seeder's own statement, replayed over the row it already created.
        await ForcedRowSecurityOwnerDatabase.ExecuteAsync(database.OwnerConnectionString, $"""
            SELECT set_config('nexora.provisioning_business_unit_id', '{ConflictBranchBusinessUnit}', false);
            INSERT INTO public."AiProcessingPolicies"
                ("BusinessUnitId", "IsEnabled", "ExternalProcessingAllowed", "AllowedPurposes",
                 "Version", "UpdatedOn", "UpdatedBy")
            VALUES ({ConflictBranchBusinessUnit}, TRUE, FALSE, 'RfqExtraction,BoqDraft',
                    1, now(), 'tenant-provisioning')
            ON CONFLICT ("BusinessUnitId") DO NOTHING;
            """);

        Assert.Equal(1L, await CountAsSuperuserAsync(
            "public.\"AiProcessingPolicies\"", "BusinessUnitId", ConflictBranchBusinessUnit));
    }

    // ------------------------------------------------------------------------------- assertions

    /// <summary>
    /// Proves the fixture reproduces the production shape before any test leans on it. If these
    /// drift, every assertion above becomes vacuous rather than false — which is the same class of
    /// silence the defect itself was.
    /// </summary>
    private async Task AssertTheOwnerIsBoundByRowLevelSecurityAsync()
    {
        Assert.Equal(1L, await ScalarAsync(database.SuperuserConnectionString, $"""
            SELECT count(*)::bigint FROM pg_roles
            WHERE rolname = '{ForcedRowSecurityOwnerDatabase.OwnerRole}'
              AND NOT rolsuper AND NOT rolbypassrls AND NOT rolinherit;
            """));

        // Both seeder targets are still FORCE, and both are still owned by that role. A fix that
        // reached for `ALTER TABLE ... NO FORCE` fails right here.
        Assert.Equal(2L, await ScalarAsync(database.SuperuserConnectionString, $"""
            SELECT count(*)::bigint FROM pg_class c
            WHERE c.oid IN ('public."AiProcessingPolicies"'::regclass,
                            'platform."TenantMeterSourcePolicies"'::regclass)
              AND c.relrowsecurity AND c.relforcerowsecurity
              AND pg_get_userbyid(c.relowner) = '{ForcedRowSecurityOwnerDatabase.OwnerRole}';
            """));

        // Both seeders are still SECURITY DEFINER and still owned by that role — the two facts
        // that make the effective role for their INSERTs the owner rather than the caller.
        Assert.Equal(2L, await ScalarAsync(database.SuperuserConnectionString, $"""
            SELECT count(*)::bigint FROM pg_proc p
            WHERE p.oid IN ('public.nexora_create_default_ai_policy()'::regprocedure,
                            'platform.nexora_seed_tenant_meter_source_policies()'::regprocedure)
              AND p.prosecdef
              AND pg_get_userbyid(p.proowner) = '{ForcedRowSecurityOwnerDatabase.OwnerRole}';
            """));

        // The owner does not inherit the role its own tenant policies name. This single fact is
        // why nexora_tenant_isolation could never have admitted the seeder: PostgreSQL matches a
        // policy's role list with has_privs_of_role(), which for a NOINHERIT role does not include
        // roles it is merely a member of.
        Assert.Equal(0L, await ScalarAsync(database.SuperuserConnectionString, $"""
            SELECT count(*)::bigint
            WHERE pg_has_role('{ForcedRowSecurityOwnerDatabase.OwnerRole}', 'nexora_tenant_app', 'USAGE');
            """));

        // And nothing acquired BYPASSRLS on the way to the fix. The two roles that carry it are
        // the two that carried it before, from 00_execution_roles.sql. The pattern is
        // 'nexora\_%' rather than 'nexora%' on purpose: the container's own POSTGRES_USER is
        // called `nexora` and is a superuser, so the looser pattern would fold the test harness's
        // login into an assertion about the application's roles.
        Assert.Equal("nexora_identity_app, nexora_pipeline_app",
            await StringAsync(database.SuperuserConnectionString, """
                SELECT string_agg(rolname, ', ' ORDER BY rolname) FROM pg_roles
                WHERE rolbypassrls AND rolname LIKE 'nexora\_%';
                """));
    }

    // ---------------------------------------------------------------------------------- fixture

    private Task CreateBusinessUnitAsync(long id, string code)
        => ForcedRowSecurityOwnerDatabase.ExecuteAsync(database.OwnerConnectionString, $"""
            INSERT INTO public."BusinessUnits" ("ID", "BusinessUnitCode", "BusinessUnitName", "CreatedBy")
            VALUES ({id}, '{code}', '{code}', 'provisioning-tests');
            """);

    private Task<long> CountAsSuperuserAsync(string table, string column, long scope)
        => ScalarAsync(database.SuperuserConnectionString,
            $"""SELECT count(*)::bigint FROM {table} WHERE "{column}" = {scope};""");

    private static async Task<long> ScalarAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        return (long)(await command.ExecuteScalarAsync())!;
    }

    private static async Task<string?> StringAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        return (await command.ExecuteScalarAsync()) as string;
    }
}
