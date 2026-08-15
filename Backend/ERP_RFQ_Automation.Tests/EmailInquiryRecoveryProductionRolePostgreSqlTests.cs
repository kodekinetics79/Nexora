using ERP_RFQ_Automation.Infrastructure;
using ERP_RFQ_Automation.Ingestion.Assembly;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.MultiTenancy;
using ERP_RFQ_Automation.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Testcontainers.PostgreSql;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// The email→Lead pipeline against PRODUCTION ROLE TOPOLOGY.
///
/// <para><b>Why this needs its own container.</b> <c>PostgreSqlTestDatabase</c> connects as the
/// container's <c>POSTGRES_USER</c>, which Testcontainers creates as a SUPERUSER, and a superuser
/// bypasses every row-level-security policy unconditionally. Every existing test on this path
/// therefore runs with RLS effectively off — the policies are never evaluated once, and the
/// <c>nexora_tenant_app</c> / <c>nexora_pipeline_app</c> boundary does not exist. The execution
/// roles are cluster-scoped, so this lane cannot share a container.</para>
///
/// <para><b>The specific risk it closes.</b> The recovery sweep puts the assembler, the
/// <c>LeadPersister</c>, lead identity and routing under <c>nexora_tenant_app</c> for the FIRST
/// time — the live worker path has always run that code as <c>nexora_pipeline_app</c>, which is
/// <c>BYPASSRLS</c>. Any table on that write path whose policy is join-derived rather than
/// column-derived, or that a catalogue sweep missed, behaves differently under recovery than
/// under the worker: a <c>WITH CHECK</c> violation or a silently invisible row, on production
/// role topology only, in a code path with no other coverage.</para>
///
/// <para>The lane is a GATE, not an architecture. It runs the real journey once under the real
/// roles, proves isolation, and carries a mutation control so a passing run cannot be vacuous.</para>
/// </summary>
public sealed class EmailInquiryRecoveryProductionRolePostgreSqlTests : IAsyncLifetime
{
    private const string OwnerRole = "nexora_schema_owner";
    private const string OwnerPassword = "nexora-owner";
    private const string OwnedDatabase = "nexora_email_role_lane";

    private const long TenantA = 942_001;
    private const long TenantB = 942_002;

    /// <summary>
    /// The PLATFORM tenant that owns <see cref="TenantA"/>. Deliberately a different number from
    /// the business unit: usage is metered against this identifier, so an assertion on it cannot
    /// pass by coincidence if the Tenants lookup silently returns nothing.
    /// </summary>
    private const long PlatformTenantA = 942_901;

    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:16-alpine")
        .WithDatabase("nexora_bootstrap")
        .WithUsername("nexora")
        .WithPassword("nexora-tests")
        .Build();

    private readonly string _storageRoot =
        Path.Combine(Path.GetTempPath(), "nexora-role-" + Guid.NewGuid().ToString("N")[..12]);

    private string _ownerConnectionString = null!;
    private string _superuserConnectionString = null!;

    public async Task InitializeAsync()
    {
        AppContext.SetSwitch("Npgsql.EnableLegacyTimestampBehavior", true);
        await _container.StartAsync();
        _superuserConnectionString = _container.GetConnectionString();

        // NOSUPERUSER / NOBYPASSRLS / NOINHERIT is what ValidateRuntimeDatabaseRoleAsync demands
        // of the production runtime identity, and it is what makes FORCE ROW LEVEL SECURITY bind
        // the owner to its own policies. CREATEROLE because this role runs the baseline, and the
        // baseline creates the execution roles.
        await ExecuteAsync(_superuserConnectionString, $"""
            CREATE ROLE {OwnerRole} LOGIN PASSWORD '{OwnerPassword}'
                NOSUPERUSER NOCREATEDB CREATEROLE NOINHERIT NOBYPASSRLS;
            GRANT SET ON PARAMETER session_replication_role TO {OwnerRole};
            """);
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
            Username = OwnerRole, Password = OwnerPassword, Database = OwnedDatabase
        }.ConnectionString;
        _superuserConnectionString = new NpgsqlConnectionStringBuilder(_superuserConnectionString)
        {
            Database = OwnedDatabase
        }.ConnectionString;

        var options = new DbContextOptionsBuilder<ErpRfqAutomationContext>()
            .UseNpgsql(_ownerConnectionString, npgsql => npgsql.CommandTimeout(180))
            .AddInterceptors(new ManagedPostgresMigrationCommandInterceptor())
            .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning))
            .Options;
        await using var context = new ErpRfqAutomationContext(options, new StubTenant(null));
        await context.Database.MigrateAsync();
    }

    public async Task DisposeAsync()
    {
        try { if (Directory.Exists(_storageRoot)) Directory.Delete(_storageRoot, recursive: true); }
        catch (IOException) { }
        await _container.DisposeAsync();
    }

    // =====================================================================================

    [Fact]
    public async Task The_whole_journey_runs_under_the_real_execution_roles_and_recovery_builds_the_Lead()
    {
        await SeedTenantAsync(TenantA, "role-lane-0001@buyer.example");

        // ---- The connection really is bound by RLS, before anything else is claimed. ----
        Assert.False(await ScalarBoolAsync(_ownerConnectionString,
            "SELECT rolbypassrls FROM pg_roles WHERE rolname = current_user;"),
            "The lane's own premise is broken: this role bypasses RLS, so nothing below is a test.");

        long assemblyId;

        // ---- 1. Capture, schedule and drain under the real roles, crashing at assembly. ----
        await using (var crashing = BuildGraph(services =>
                     services.AddScoped<IEmailInquiryLeadAssembler, ThrowingAssembler>()))
        {
            var (_, id, schedule) = await EmailToLeadHarness.CaptureAndScheduleAsync(
                crashing, TenantA, EmailToLeadHarness.BuildMessage("role-lane-0001@buyer.example"));
            assemblyId = id;

            // Capture and scheduling wrote assemblies, components, evidence rows, source
            // documents, occurrences and queue rows — every one of them as nexora_tenant_app
            // under FORCE row-level security.
            Assert.Equal(3, schedule.Scheduled);
            Assert.Equal(0, schedule.Held);

            await EmailToLeadHarness.DrainQueueAsync(
                crashing, TenantA, assertNoFailures: true, waitForAssemblySettlement: false);
        }

        Assert.Equal("ReadyForAssembly", await StatusAsync(assemblyId));
        Assert.Equal(0, await CountLeadsAsync(TenantA));

        // ---- 2. Recovery: enumeration as nexora_pipeline_app, the write path as
        //         nexora_tenant_app. THIS is the combination that had no coverage. ----
        await using (var recovering = BuildGraph())
        {
            var result = await SweepAsync(recovering);
            Assert.Equal(1, result.Candidates);
            Assert.Equal(0, result.Failed);
            Assert.Equal(1, result.Recovered);
        }

        // The full write path completed: identity, persistence, the Lead and its lines, the
        // component outcomes and the assembly transition — all under the tenant role.
        Assert.Equal("Assembled", await StatusAsync(assemblyId));
        Assert.Equal(1, await CountLeadsAsync(TenantA));
        Assert.Equal(3, await ScalarAsync(_superuserConnectionString, $"""
            SELECT count(*) FROM public."EmailInquiryComponents"
            WHERE "AssemblyId" = {assemblyId} AND "Status" = 'Completed';
            """));
        Assert.Equal(5, await ScalarAsync(_superuserConnectionString, $"""
            SELECT count(*) FROM public."LeadItems" i
            JOIN public."Leads" l ON l."ID" = i."LeadID"
            WHERE l."BusinessUnitID" = {TenantA};
            """));

        var leadId = await ScalarAsync(_superuserConnectionString, $"""
            SELECT COALESCE("AssembledLeadId", 0) FROM public."EmailInquiryAssemblies"
            WHERE "Id" = {assemblyId};
            """);
        Assert.True(leadId > 0, "The assembly must name the Lead recovery built.");
    }

    [Fact]
    public async Task The_applications_own_connection_switches_role_and_sets_the_tenant_GUC()
    {
        // Stated directly rather than inferred. The journey succeeding IS strong evidence the
        // switch happened — a NOINHERIT owner is denied by policies written TO nexora_tenant_app,
        // which is the tenant-purge defect — but "the app ran as the tenant role" is the lane's
        // central claim and deserves to be observed, not deduced.
        await using var services = BuildGraph();
        using var scope = services.CreateScope();
        using var tenant = scope.ServiceProvider
            .GetRequiredService<ITenantScopeAccessor>().Push(TenantA);
        var context = scope.ServiceProvider.GetRequiredService<ErpRfqAutomationContext>();

        var observed = await context.Database
            .SqlQuery<string>($"SELECT current_user || '|' || current_setting('nexora.business_unit_id', true)")
            .ToListAsync();

        Assert.Equal($"nexora_tenant_app|{TenantA}", Assert.Single(observed));
    }

    [Fact]
    public async Task The_unscoped_enumeration_runs_as_the_pipeline_role()
    {
        // The other half of the boundary: the sweep's platform-wide tenant enumeration must run
        // as nexora_pipeline_app, not as a tenant and not as the login role.
        await using var services = BuildGraph();
        using var scope = services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<ErpRfqAutomationContext>();

        var observed = await context.Database.SqlQuery<string>($"SELECT current_user").ToListAsync();
        Assert.Equal("nexora_pipeline_app", Assert.Single(observed));
    }

    [Fact]
    public async Task The_tenant_role_cannot_see_a_neighbours_assembly_and_the_policy_is_what_stops_it()
    {
        await SeedTenantAsync(TenantA, "role-lane-0002@buyer.example");
        await SeedTenantAsync(TenantB, "role-lane-0003@buyer.example");

        await using (var crashing = BuildGraph(services =>
                     services.AddScoped<IEmailInquiryLeadAssembler, ThrowingAssembler>()))
        {
            await EmailToLeadHarness.CaptureAndScheduleAsync(
                crashing, TenantA, EmailToLeadHarness.BuildMessage("role-lane-0002@buyer.example"));
        }

        // As nexora_tenant_app with tenant B's GUC, tenant A's rows are not there — not filtered
        // by EF, which is bypassed entirely here, but by the database.
        Assert.Equal(0, await AsTenantAsync(TenantB,
            $"""SELECT count(*) FROM public."EmailInquiryAssemblies" WHERE "BusinessUnitId" = {TenantA};"""));
        Assert.Equal(0, await AsTenantAsync(TenantB,
            $"""SELECT count(*) FROM public."EmailInquiryComponents" WHERE "BusinessUnitId" = {TenantA};"""));

        // The owner sees them, so the zeros above are isolation and not absence.
        Assert.Equal(1, await ScalarAsync(_superuserConnectionString,
            $"""SELECT count(*) FROM public."EmailInquiryAssemblies" WHERE "BusinessUnitId" = {TenantA};"""));
    }

    [Fact]
    public async Task Revoking_the_tenant_grant_breaks_the_journey_which_is_why_the_passing_run_means_something()
    {
        // THE MUTATION CONTROL. Without it, "the journey passed under production roles" is
        // consistent with the roles never being applied at all.
        await SeedTenantAsync(TenantA, "role-lane-0004@buyer.example");

        await ExecuteAsync(_superuserConnectionString,
            """REVOKE INSERT ON TABLE public."EmailInquiryComponentResults" FROM nexora_tenant_app;""");
        try
        {
            await using var crashing = BuildGraph(services =>
                services.AddScoped<IEmailInquiryLeadAssembler, ThrowingAssembler>());

            await EmailToLeadHarness.CaptureAndScheduleAsync(
                crashing, TenantA, EmailToLeadHarness.BuildMessage("role-lane-0004@buyer.example"));

            // The worker cannot write a component result without the grant, so no component
            // completes and the message never becomes ready. assertNoFailures is OFF because
            // failing is the point.
            await EmailToLeadHarness.DrainQueueAsync(
                crashing, TenantA, assertNoFailures: false, waitForAssemblySettlement: false);

            Assert.Equal(0, await ScalarAsync(_superuserConnectionString,
                """SELECT count(*) FROM public."EmailInquiryComponentResults";"""));
            Assert.Equal(0, await CountLeadsAsync(TenantA));
        }
        finally
        {
            await ExecuteAsync(_superuserConnectionString,
                """GRANT INSERT ON TABLE public."EmailInquiryComponentResults" TO nexora_tenant_app;""");
        }
    }

    [Fact]
    public async Task Extraction_meters_its_usage_under_the_tenant_role_without_opening_the_billing_plane_to_it()
    {
        // THE DEFECT THIS CLOSES. The persist transaction runs as nexora_tenant_app — the tenant
        // scope ProcessOnceAsync pushes routes it there through the interceptor, and
        // ExtractionQueue's own SET LOCAL ROLE puts it there again when RenewLeaseAsync opens the
        // transaction. That role holds column-level SELECT on six platform."Tenants" columns and
        // nothing else on the platform plane: not Tenants."RateCardId", which the metering lookup
        // projects, and nothing at all on platform."UsageEvents". Every extraction failed and
        // re-leased until it dead-lettered. Verified by reverting the fix and reading the queue:
        // all three jobs recorded
        //     DeadLetter: 42501: permission denied for table Tenants
        //
        // It survived because NO test graph registered UsageMeteringService, so LeadPersister's
        // optional dependency was null and the block never executed anywhere. Registering it in
        // the shared harness is the other half of the fix — and it makes the ordinary superuser
        // lanes reproduce this too, because the queue's role switch needs no RLS configuration.
        // The assertion this lane adds on top of theirs is the last block below: that the fix did
        // not buy the meter by handing the tenant role the billing plane.
        await SeedTenantAsync(TenantA, "role-lane-0005@buyer.example");
        await SeedPlatformTenantAsync(PlatformTenantA, TenantA);

        await using var services = BuildGraph();
        await EmailToLeadHarness.CaptureAndScheduleAsync(
            services, TenantA, EmailToLeadHarness.BuildMessage("role-lane-0005@buyer.example"));
        await EmailToLeadHarness.DrainQueueAsync(services, TenantA, assertNoFailures: true);

        // The journey finished, which is already the regression: without the fix DrainQueueAsync
        // fails with the 42501 in LastError. The meters below prove it finished WITH billing
        // rather than by skipping it — unbilled usage is the failure that pays no one.
        Assert.Equal(1, await CountLeadsAsync(TenantA));
        Assert.Equal(3, await ScalarAsync(_superuserConnectionString, $"""
            SELECT count(*) FROM platform."UsageEvents"
            WHERE "TenantId" = {PlatformTenantA} AND "EventType" = 'documents';
            """));

        // The meter is keyed on the PLATFORM tenant, not the business unit, so this also proves
        // the Tenants lookup by PrimaryBusinessUnitId returned a row rather than being skipped:
        // the two identifiers are deliberately different numbers.
        Assert.Equal(0, await ScalarAsync(_superuserConnectionString, $"""
            SELECT count(*) FROM platform."UsageEvents" WHERE "TenantId" = {TenantA};
            """));

        // The rest of the metering write path, which is the part that could not have been fixed
        // by a grant on Tenants alone: a rating row per event and the minute rollup.
        Assert.Equal(3, await ScalarAsync(_superuserConnectionString, $"""
            SELECT count(*) FROM platform."UsageEventRatings" WHERE "TenantId" = {PlatformTenantA};
            """));
        Assert.True(await ScalarAsync(_superuserConnectionString, $"""
            SELECT count(*) FROM platform."UsageMinuteAggregates"
            WHERE "TenantId" = {PlatformTenantA} AND "EventType" = 'documents';
            """) > 0, "The minute rollup did not run, so the metering transaction stopped early.");

        // AND THE TENANT ROLE STILL CANNOT REACH ANY OF IT. This is the assertion that
        // distinguishes the fix that was chosen from the one that was not: metering works because
        // those statements execute as nexora_pipeline_app, NOT because the tenant role was granted
        // the billing ledger. A tenant-writable meter is a tenant-editable invoice.
        Assert.False(await ScalarBoolAsync(_superuserConnectionString, """
            SELECT has_column_privilege('nexora_tenant_app', 'platform."Tenants"', 'RateCardId', 'SELECT');
            """), "The tenant role was granted a Tenants column it does not need.");
        foreach (var (table, privilege) in new[]
                 {
                     ("UsageEvents", "SELECT,INSERT,UPDATE,DELETE"),
                     ("UsageEventRatings", "SELECT,INSERT,UPDATE,DELETE"),
                     ("UsageMinuteAggregates", "SELECT,INSERT,UPDATE,DELETE"),
                     ("RateCards", "SELECT,INSERT,UPDATE,DELETE")
                 })
        {
            Assert.False(await ScalarBoolAsync(_superuserConnectionString, $"""
                SELECT has_table_privilege('nexora_tenant_app', 'platform."{table}"', '{privilege}');
                """), $"The tenant role reaches platform.\"{table}\". The billing plane was widened.");
        }
    }

    [Fact]
    public async Task The_platform_plane_block_switches_role_for_its_statements_and_gives_the_tenant_role_back()
    {
        // The mechanism, observed directly rather than inferred from the journey above. Both
        // halves matter: the block must reach the platform role, and — the part a leak would come
        // from — the connection must be back on nexora_tenant_app with its GUC the moment the
        // block closes, because the fenced queue completion runs immediately after it.
        await using var services = BuildGraph();
        using var scope = services.CreateScope();
        using var tenant = scope.ServiceProvider
            .GetRequiredService<ITenantScopeAccessor>().Push(TenantA);
        var context = scope.ServiceProvider.GetRequiredService<ErpRfqAutomationContext>();

        using (PlatformPlaneExecution.Enter())
        {
            var inside = await context.Database.SqlQuery<string>($"SELECT current_user").ToListAsync();
            Assert.Equal("nexora_pipeline_app", Assert.Single(inside));
        }

        var after = await context.Database
            .SqlQuery<string>($"SELECT current_user || '|' || current_setting('nexora.business_unit_id', true)")
            .ToListAsync();
        Assert.Equal($"nexora_tenant_app|{TenantA}", Assert.Single(after));
    }

    [Fact]
    public async Task Every_email_assembly_table_is_forced_and_carries_its_tenant_and_purge_policies()
    {
        // The grants and policies the journey above depends on, asserted directly so a failure
        // names the missing control rather than surfacing as an unexplained empty result.
        foreach (var table in new[]
                 { "EmailInquiryAssemblies", "EmailInquiryComponents", "EmailInquiryComponentResults" })
        {
            Assert.True(await ScalarBoolAsync(_superuserConnectionString, $"""
                SELECT relrowsecurity AND relforcerowsecurity FROM pg_class
                WHERE oid = 'public."{table}"'::regclass;
                """), $"{table} is not ENABLE + FORCE row level security.");

            Assert.Equal(1, await ScalarAsync(_superuserConnectionString, $"""
                SELECT count(*) FROM pg_policy p JOIN pg_class c ON c.oid = p.polrelid
                WHERE c.relname = '{table}' AND p.polname = 'nexora_tenant_isolation';
                """));

            Assert.True(await ScalarBoolAsync(_superuserConnectionString, $"""
                SELECT has_table_privilege('nexora_tenant_app', 'public."{table}"', 'SELECT,INSERT,UPDATE,DELETE');
                """), $"nexora_tenant_app lacks a table privilege on {table}.");

            Assert.True(await ScalarBoolAsync(_superuserConnectionString, $"""
                SELECT has_table_privilege('nexora_pipeline_app', 'public."{table}"', 'SELECT,INSERT,UPDATE');
                """), $"nexora_pipeline_app lacks a table privilege on {table}.");
        }
    }

    // ------------------------------------------------------------------ harness

    private ServiceProvider BuildGraph(Action<IServiceCollection>? configure = null) =>
        EmailToLeadHarness.BuildGraph(
            _ownerConnectionString, _storageRoot, new EmailToLeadHarness.RefusingLlm(),
            configure, withRlsInterceptor: true);

    private sealed class ThrowingAssembler : IEmailInquiryLeadAssembler
    {
        public Task<long?> AssembleAsync(long businessUnitId, long assemblyId, CancellationToken ct = default)
            => throw new InvalidOperationException("Simulated process loss before assembly.");
    }

    private static async Task<EmailInquiryRecoverySweepResult> SweepAsync(ServiceProvider services)
    {
        using var scope = services.CreateScope();
        return await scope.ServiceProvider
            .GetRequiredService<IEmailInquiryAssemblyRecoveryService>().SweepOnceAsync();
    }

    private async Task SeedTenantAsync(long businessUnitId, string messageId)
    {
        await using var connection = new NpgsqlConnection(_superuserConnectionString);
        await connection.OpenAsync();
        await EmailToLeadHarness.SeedTenantAsync(connection, businessUnitId, messageId);
    }

    /// <summary>
    /// Seeds the platform-plane row that makes this business unit BILLABLE. Written as the
    /// superuser because provisioning is a platform operation; the point of the test is what the
    /// extraction worker can reach afterwards, not who created the tenant.
    /// </summary>
    private Task SeedPlatformTenantAsync(long tenantId, long primaryBusinessUnitId) =>
        ExecuteAsync(_superuserConnectionString, $"""
            INSERT INTO platform."Tenants"
                ("Id", "Name", "Slug", "Status", "PrimaryBusinessUnitId", "CreatedOn", "BillingMode")
            VALUES ({tenantId}, 'Role lane tenant {tenantId}', 'role-lane-{tenantId}', 'Active',
                    {primaryBusinessUnitId}, now(), 'Billable')
            ON CONFLICT DO NOTHING;
            """);

    private Task<int> CountLeadsAsync(long businessUnitId) => ScalarAsync(
        _superuserConnectionString,
        $"""SELECT count(*) FROM public."Leads" WHERE "BusinessUnitID" = {businessUnitId};""");

    private async Task<string> StatusAsync(long assemblyId)
    {
        await using var connection = new NpgsqlConnection(_superuserConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            $"""SELECT "Status" FROM public."EmailInquiryAssemblies" WHERE "Id" = {assemblyId};""";
        return (string)(await command.ExecuteScalarAsync())!;
    }

    /// <summary>Runs a statement as <c>nexora_tenant_app</c> with the tenant GUC set.</summary>
    private async Task<int> AsTenantAsync(long businessUnitId, string sql)
    {
        await using var connection = new NpgsqlConnection(_ownerConnectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            $"SET LOCAL ROLE nexora_tenant_app; SET LOCAL nexora.business_unit_id = '{businessUnitId}'; {sql}";
        var value = Convert.ToInt32(await command.ExecuteScalarAsync());
        await transaction.RollbackAsync();
        return value;
    }

    private static async Task<int> ScalarAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt32(await command.ExecuteScalarAsync());
    }

    private static async Task<bool> ScalarBoolAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToBoolean(await command.ExecuteScalarAsync());
    }

    private static async Task ExecuteAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }
}
