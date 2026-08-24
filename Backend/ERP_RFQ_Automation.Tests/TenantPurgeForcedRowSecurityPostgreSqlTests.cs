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

    /// <summary>
    /// DEFECT P0-1. The tables whose business unit column is spelled snake_case.
    ///
    /// <para>The sweep discovered its targets with <c>lower(column_name) IN ('businessunitid',
    /// 'buid')</c>, and eleven evidence and extraction tables carry <c>business_unit_id</c> —
    /// <c>source_documents</c>, <c>document_corpora</c>, <c>canonical_inquiries</c>,
    /// <c>field_evidence</c> and seven more. They matched neither spelling, so they were never
    /// targets, never counted in the operator's preview, and never named in the report. Against
    /// production that left 380 rows for one business unit and 515 for another, under an
    /// offboarding that had already reported success.</para>
    ///
    /// <para>Counted through the SUPERUSER, as everything in this class is: asking the purge's own
    /// identity whether the purge worked is the failure mode the class exists to close.</para>
    /// </summary>
    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task Purge_destroys_rows_in_snake_case_business_unit_tables()
    {
        await SeedAsync();
        await SeedSnakeCaseEvidenceAsync();

        var executor = TenantLifecycleHarness.PurgeExecutor(_ownerConnectionString);
        var preview = await executor.PreviewAsync(TenantAId, TenantABusinessUnit, CancellationToken.None);

        // The confirmation screen counts them too. A preview that under-reports is not a cosmetic
        // fault: it is the operator authorising destruction against a floor presented as a total.
        Assert.Contains(preview.Tables, t => t.Table.Contains("source_documents"));
        Assert.Contains(preview.Tables, t => t.Table.Contains("document_corpora"));

        var attempt = await ClaimPurgeAsync();
        var outcome = await executor.ExecuteAsync(
            TenantAId, TenantABusinessUnit, attempt, CancellationToken.None);

        Assert.Equal(0, await CountAsSuperuserAsync("public.source_documents", "business_unit_id", TenantABusinessUnit));
        Assert.Equal(0, await CountAsSuperuserAsync("public.document_corpora", "business_unit_id", TenantABusinessUnit));

        // Tenant B keeps everything.
        Assert.Equal(1, await CountAsSuperuserAsync("public.source_documents", "business_unit_id", TenantBBusinessUnit));
        Assert.Equal(1, await CountAsSuperuserAsync("public.document_corpora", "business_unit_id", TenantBBusinessUnit));

        Assert.Contains(outcome.Deleted, d => d.Table.Contains("source_documents"));
        Assert.Contains(outcome.Deleted, d => d.Table.Contains("document_corpora"));
    }

    /// <summary>
    /// DEFECT P0-2. <c>public."EmailIngests"</c> has no business unit column at all — it reaches a
    /// tenant only through <c>Email_Configurations</c>.
    ///
    /// <para>103 rows survived a completed purge in production, and survived it ORPHANED: the
    /// parent <c>Email_Configurations</c> row was destroyed in the same transaction, and because
    /// the purge runs under <c>session_replication_role = 'replica'</c> the foreign key that would
    /// have objected was suspended. The rows held the raw-message pointer, the sender address and
    /// the subject line of every message the tenant ever received.</para>
    ///
    /// <para>The orphan assertion is the one that matters and is asserted separately from the
    /// count. "No rows left" and "no rows left pointing at a parent that no longer exists" are the
    /// same fact only when the sweep worked; when it did not, the second is the one that shows
    /// it.</para>
    /// </summary>
    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task Purge_destroys_rows_reached_only_through_a_parent()
    {
        await SeedAsync();
        await SeedIndirectMailAsync();
        await SeedPolymorphicAttachmentsAsync();

        var executor = TenantLifecycleHarness.PurgeExecutor(_ownerConnectionString);
        var attempt = await ClaimPurgeAsync();
        var outcome = await executor.ExecuteAsync(
            TenantAId, TenantABusinessUnit, attempt, CancellationToken.None);

        Assert.Equal(0, await ScalarAsSuperuserAsync(
            $"""
             SELECT count(*)::bigint FROM public."EmailIngests"
             WHERE "MessageID" = 'purge-a@tests';
             """));

        // And nothing was left dangling. This is the production signature, asserted directly.
        Assert.Equal(0, await ScalarAsSuperuserAsync(
            """
            SELECT count(*)::bigint FROM public."EmailIngests" i
            WHERE NOT EXISTS (
                SELECT 1 FROM public."Email_Configurations" c WHERE c."ID" = i."EmailConfigurationID");
            """));

        // Tenant B's mailbox and its message are untouched.
        Assert.Equal(1, await ScalarAsSuperuserAsync(
            $"""
             SELECT count(*)::bigint FROM public."EmailIngests"
             WHERE "MessageID" = 'purge-b@tests';
             """));
        Assert.Equal(1, await CountAsSuperuserAsync(
            "public.\"Email_Configurations\"", "BusinessUnitID", TenantBBusinessUnit));

        Assert.Contains(outcome.Deleted, d => d.Table.Contains("EmailIngests"));

        // The polymorphic case, where the predicate has to read (ParentType, ParentID) together.
        Assert.Equal(0, await ScalarAsSuperuserAsync(
            """SELECT count(*)::bigint FROM public."Attachments" WHERE "FileName" = 'purge-a.pdf';"""));
        Assert.Equal(1, await ScalarAsSuperuserAsync(
            """SELECT count(*)::bigint FROM public."Attachments" WHERE "FileName" = 'purge-b.pdf';"""));
        Assert.Contains(outcome.Deleted, d => d.Table.Contains("Attachments"));
    }

    /// <summary>
    /// THE POST-CONDITION, proved against a tenant-scoped table the sweep never visits.
    ///
    /// <para>This is the half of P0-1 that matters more than the eleven tables. The old check
    /// re-counted the tables the sweep had just swept, so it could only ever confirm that the
    /// sweep did what the sweep did — a verification structurally incapable of catching the
    /// omission it existed to catch. The probe here is invisible to the sweep for a reason the
    /// executor's own comment names: target discovery reads <c>information_schema</c>, which is
    /// filtered by the caller's privileges, so a table the schema owner holds no privilege on is
    /// simply not there. The independent check reads <c>pg_class</c>, which is not filtered, and
    /// sees it.</para>
    ///
    /// <para>Note what is asserted about the outcome: not merely that it threw, but that NOTHING
    /// was committed. A purge that discovers it is incomplete must leave the tenant intact and
    /// re-runnable, not half-destroyed.</para>
    /// </summary>
    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task Purge_refuses_when_a_tenant_scoped_table_was_never_swept()
    {
        await SeedAsync();

        // Created and owned by the SUPERUSER, granted to the purge role and to nobody else. The
        // schema owner therefore cannot see it in information_schema, which is exactly how a
        // table added out of band — or by a migration run under a different role — would look.
        await ExecuteAsync(_superuserConnectionString, $"""
            DROP TABLE IF EXISTS public.purge_probe_unswept;
            CREATE TABLE public.purge_probe_unswept (
                id bigint GENERATED BY DEFAULT AS IDENTITY PRIMARY KEY,
                business_unit_id bigint NOT NULL);
            ALTER TABLE public.purge_probe_unswept ENABLE ROW LEVEL SECURITY;
            CREATE POLICY nexora_tenant_purge ON public.purge_probe_unswept
                AS PERMISSIVE FOR ALL TO nexora_purge_app
                USING (business_unit_id = NULLIF(current_setting('nexora.purge_business_unit_id', true), '')::bigint);
            GRANT SELECT, DELETE ON public.purge_probe_unswept TO nexora_purge_app;
            INSERT INTO public.purge_probe_unswept (business_unit_id) VALUES ({TenantABusinessUnit});
            """);
        try
        {
            var executor = TenantLifecycleHarness.PurgeExecutor(_ownerConnectionString);
            var attempt = await ClaimPurgeAsync();

            var failure = await Assert.ThrowsAsync<InvalidOperationException>(
                () => executor.ExecuteAsync(TenantAId, TenantABusinessUnit, attempt, CancellationToken.None));

            Assert.Contains("purge_probe_unswept", failure.Message);
            Assert.Contains("never visited it", failure.Message);
            Assert.Contains("derived from the schema rather than from the sweep's own list", failure.Message);

            // Nothing committed. Not the probe's row, and not the rows the sweep DID reach.
            Assert.Equal(1, await ScalarAsSuperuserAsync(
                $"SELECT count(*)::bigint FROM public.purge_probe_unswept WHERE business_unit_id = {TenantABusinessUnit};"));
            Assert.Equal(1, await CountAsSuperuserAsync(ForcedTable, "BusinessUnitId", TenantABusinessUnit));
            Assert.Equal(1, await CountAsSuperuserAsync("public.\"BusinessUnits\"", "ID", TenantABusinessUnit));
            Assert.Equal(0, await ScalarAsSuperuserAsync(
                $"""
                 SELECT count(*)::bigint FROM platform."TenantOffboardings"
                 WHERE "TenantId" = {TenantAId} AND "PurgeExecutedOn" IS NOT NULL;
                 """));
        }
        finally
        {
            await ExecuteAsync(_superuserConnectionString, "DROP TABLE IF EXISTS public.purge_probe_unswept;");
        }
    }

    /// <summary>
    /// The other half of the same guard: a table the DATABASE says holds one tenant's rows, that
    /// nobody has taught the purge how to select.
    ///
    /// <para>Every tenant table in this schema carries a <c>nexora_tenant_isolation</c> policy —
    /// the request path has always known <c>EmailIngests</c> belongs to a tenant, and only the
    /// purge did not. Reading that policy catalogue is what turns "add a table and forget the
    /// sweep" from a silent data-retention failure into a refusal that names the table.</para>
    /// </summary>
    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task Purge_refuses_when_the_schema_declares_a_tenant_table_the_map_does_not()
    {
        await SeedAsync();

        // No business unit column, so no column rule can find it; a tenant-isolation policy, so
        // the database says it is one tenant's data. That is EmailIngests, exactly.
        await ExecuteAsync(_superuserConnectionString, """
            DROP TABLE IF EXISTS public.purge_probe_unclassified;
            CREATE TABLE public.purge_probe_unclassified (
                id bigint GENERATED BY DEFAULT AS IDENTITY PRIMARY KEY,
                owner_ref bigint NOT NULL);
            ALTER TABLE public.purge_probe_unclassified ENABLE ROW LEVEL SECURITY;
            CREATE POLICY nexora_tenant_isolation ON public.purge_probe_unclassified
                TO nexora_tenant_app USING (true);
            """);
        try
        {
            var failure = await Assert.ThrowsAsync<InvalidOperationException>(
                () => TenantLifecycleHarness.PurgeExecutor(_ownerConnectionString)
                    .PreviewAsync(TenantAId, TenantABusinessUnit, CancellationToken.None));

            Assert.Contains("purge_probe_unclassified", failure.Message);
            Assert.Contains("nexora_tenant_isolation", failure.Message);
            Assert.Contains("TenantPlaneDataMap", failure.Message);
        }
        finally
        {
            await ExecuteAsync(_superuserConnectionString,
                "DROP TABLE IF EXISTS public.purge_probe_unclassified;");
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
    /// One corpus and one document per tenant, in the two snake_case tables. Both carry
    /// <c>business_unit_id</c>, which is the whole point: nothing else about them differs from the
    /// PascalCase tables the sweep always found.
    /// </summary>
    private async Task SeedSnakeCaseEvidenceAsync()
    {
        await ExecuteAsync(_superuserConnectionString, $"""
            SET session_replication_role = replica;

            DELETE FROM public.source_documents WHERE business_unit_id IN ({TenantABusinessUnit}, {TenantBBusinessUnit});
            DELETE FROM public.document_corpora  WHERE business_unit_id IN ({TenantABusinessUnit}, {TenantBBusinessUnit});

            INSERT INTO public.document_corpora
                (id, business_unit_id, batch_id, source_type, status, created_on, updated_on)
            VALUES (900001, {TenantABusinessUnit}, gen_random_uuid(), 'Upload', 'Complete', now(), now()),
                   (900002, {TenantBBusinessUnit}, gen_random_uuid(), 'Upload', 'Complete', now(), now());

            INSERT INTO public.source_documents
                (id, business_unit_id, corpus_id, content_hash, original_file_name,
                 detected_mime_type, object_bucket, object_key, object_version, byte_size,
                 page_count, security_status, processing_status, created_on, updated_on)
            VALUES (900001, {TenantABusinessUnit}, 900001, repeat('a', 64), 'a.pdf', 'application/pdf',
                    'NexoraBucket', 'Evidence/tenants/{TenantABusinessUnit}/cleared/sha256/aa/a.pdf',
                    'v1', 10, 1, 'Cleared', 'Complete', now(), now()),
                   (900002, {TenantBBusinessUnit}, 900002, repeat('b', 64), 'b.pdf', 'application/pdf',
                    'NexoraBucket', 'Evidence/tenants/{TenantBBusinessUnit}/cleared/sha256/bb/b.pdf',
                    'v1', 10, 1, 'Cleared', 'Complete', now(), now());

            SET session_replication_role = origin;
            """);
    }

    /// <summary>
    /// A mailbox per tenant and one accepted message under each. <c>public."EmailIngests"</c>
    /// carries no business unit column; the message is the tenant's only through its mailbox.
    /// </summary>
    private async Task SeedIndirectMailAsync()
    {
        await ExecuteAsync(_superuserConnectionString, $"""
            SET session_replication_role = replica;

            DELETE FROM public."EmailIngests" WHERE "MessageID" IN ('purge-a@tests', 'purge-b@tests');
            DELETE FROM public."Email_Configurations" WHERE "ID" IN (900001, 900002);

            INSERT INTO public."Email_Configurations"
                ("ID", "BusinessUnitID", "ConfigurationName", "EmailAddress", "Protocol", "Host",
                 "Port", "Username", "Password")
            VALUES (900001, {TenantABusinessUnit}, 'A', 'a@tenant-a.test', 'IMAP', 'imap.test', 993, 'a', 'x'),
                   (900002, {TenantBBusinessUnit}, 'B', 'b@tenant-b.test', 'IMAP', 'imap.test', 993, 'b', 'x');

            INSERT INTO public."EmailIngests"
                ("MessageID", "EmailSubject", "FromEmail", "EmailConfigurationID", "RawEmailPath")
            VALUES ('purge-a@tests', 'RFQ for tenant A', 'buyer@customer.test', 900001,
                    'Evidence/tenants/{TenantABusinessUnit}/raw-mail/sha256/aa/a.eml'),
                   ('purge-b@tests', 'RFQ for tenant B', 'buyer@customer.test', 900002,
                    'Evidence/tenants/{TenantBBusinessUnit}/raw-mail/sha256/bb/b.eml');

            SET session_replication_role = origin;
            """);
    }

    /// <summary>
    /// A lead per tenant with an attachment filed against it.
    ///
    /// <para><c>public."Attachments"</c> is the hardest predicate in the map and the only one with
    /// no foreign key at all: it is polymorphic on <c>(ParentType, ParentID)</c>, so a wrong
    /// predicate deletes nothing and says nothing, and a careless one deletes another tenant's
    /// file because a lead somewhere shares an id with a material lot. Both tenants get a lead so
    /// the boundary is asserted rather than assumed.</para>
    /// </summary>
    private async Task SeedPolymorphicAttachmentsAsync()
    {
        await ExecuteAsync(_superuserConnectionString, $"""
            SET session_replication_role = replica;

            DELETE FROM public."Attachments" WHERE "FileName" IN ('purge-a.pdf', 'purge-b.pdf');
            DELETE FROM public."Leads" WHERE "ID" IN (900001, 900002);

            INSERT INTO public."Leads"
                ("ID", "RecDate", "LeadSource", "CreatedBy", "BusinessUnitID",
                 "CommercialCaseId", "CommercialCaseReference")
            VALUES (900001, now(), 'tests', 'tests', {TenantABusinessUnit}, 900001, 'CASE-A'),
                   (900002, now(), 'tests', 'tests', {TenantBBusinessUnit}, 900002, 'CASE-B');

            INSERT INTO public."Attachments"
                ("ParentType", "ParentID", "FileName", "FilePath", "CreatedOn")
            VALUES ('Lead', 900001, 'purge-a.pdf',
                    'Evidence/tenants/{TenantABusinessUnit}/cleared/sha256/aa/a.pdf', now()),
                   ('Lead', 900002, 'purge-b.pdf',
                    'Evidence/tenants/{TenantBBusinessUnit}/cleared/sha256/bb/b.pdf', now());

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
