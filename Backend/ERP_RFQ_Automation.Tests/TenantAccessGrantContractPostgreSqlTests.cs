using ERP_RFQ_Automation.Platform.Entitlements;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Testcontainers.PostgreSql;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// <see cref="TenantAccessGrantContract"/> run against the ROLE TOPOLOGY PRODUCTION ACTUALLY HAS,
/// rather than as a superuser.
///
/// <para><b>Why this exists.</b> The contract is a boot gate: <c>Program.cs</c> calls it before the
/// first request and does not catch it, so anything it throws is a process that will not start. On
/// 2026-08-11 it threw on every boot of the production service — not because a grant was missing,
/// but because THE CHECK COULD NOT SEE THE GRANTS. It died on its first statement with
/// <c>42501: permission denied for schema platform</c>, and the container exited 139. The service
/// stayed 42 commits stale for a day, three Platform Admin screens 404'd against endpoints that
/// only existed on the undeployed commits, and a tenant could not be provisioned.</para>
///
/// <para><b>The mechanism, which is the part worth keeping.</b> The runtime connects as
/// <c>nexora_runtime</c>: LOGIN, <c>NOINHERIT</c>, no ambient privileges, a member of the execution
/// roles and reaching the tenant plane only through the <c>SET LOCAL ROLE</c> the RLS interceptor
/// issues per command. USAGE on <c>platform</c> is granted to the <c>*_app</c> execution roles and
/// deliberately NOT to the login role — that is the design, not an oversight. The contract opens a
/// RAW <see cref="NpgsqlConnection"/> and issues no <c>SET ROLE</c>, so it runs with exactly those
/// zero privileges. Its original comment claimed this was fine: "any role may run this — the
/// privilege functions are asked ABOUT a role by name, they do not require being it." Asking about
/// another role is indeed free. Naming the table is not: <c>to_regclass('platform."Tenants"')</c>
/// and the text-table overload of <c>has_column_privilege</c> both resolve the identifier in the
/// CALLER's context, and that resolution demands USAGE on the schema before any privilege question
/// is evaluated. Fixed by resolving OIDs through <c>pg_catalog</c> — readable by everyone, no USAGE
/// required — and passing the OID to the <c>regclass</c> overload, which resolves nothing.</para>
///
/// <para><b>Why no existing test caught it.</b> <c>TenantAccessFailClosedTests</c> reflects over
/// <see cref="TenantAccessGrantContract.RequiredColumns"/> and never executes the check against a
/// database at all. Every PostgreSQL fixture in the suite connects as the container's
/// <c>POSTGRES_USER</c>, which Testcontainers creates as a SUPERUSER — and a superuser has USAGE on
/// everything, so the failing statement succeeds and the defect cannot exist in that lane. The
/// property under test here is reachable only from a role that is NOT the owner and NOT a
/// superuser, which is why this class builds its own topology.</para>
/// </summary>
public sealed class TenantAccessGrantContractPostgreSqlTests : IAsyncLifetime
{
    private const string LoginRole = "nexora_runtime";
    private const string LoginPassword = "nexora-runtime-tests";

    /// <summary>Granted to the execution roles. Deliberately NOT granted to <see cref="LoginRole"/>,
    /// and never projected by the tenant-access query — the control column.</summary>
    private const string UngrantedColumn = "Name";

    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:16-alpine")
        .WithDatabase("nexora_grant_contract")
        .WithUsername("nexora")
        .WithPassword("nexora-tests")
        .Build();

    private string _superuserConnectionString = null!;
    private string _loginConnectionString = null!;

    public async Task InitializeAsync()
    {
        await _container.StartAsync();
        _superuserConnectionString = _container.GetConnectionString();

        // The execution roles exactly as MigrationsBaseline/Sql/00_execution_roles.sql declares
        // them, and the login role exactly as ValidateRuntimeDatabaseRoleAsync demands: NOINHERIT,
        // NOSUPERUSER, NOBYPASSRLS, a MEMBER of the execution roles and nothing more.
        await ExecuteAsync(_superuserConnectionString, $"""
            CREATE ROLE nexora_tenant_app   NOLOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT NOBYPASSRLS;
            CREATE ROLE nexora_identity_app NOLOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT BYPASSRLS;
            CREATE ROLE {LoginRole} LOGIN PASSWORD '{LoginPassword}'
                NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT NOBYPASSRLS;
            GRANT nexora_tenant_app, nexora_identity_app TO {LoginRole};
            """);

        await ExecuteAsync(_superuserConnectionString, $"""
            CREATE SCHEMA platform;
            CREATE TABLE platform."Tenants" (
                "Id" bigint PRIMARY KEY,
                "PrimaryBusinessUnitId" bigint,
                "Status" text NOT NULL,
                "PlanId" bigint,
                -- Projected by CoreQuery since 20260818013530. This fixture asserts privileges,
                -- not storage shape, so no default or NOT NULL here — and its absence is what the
                -- 42703 branch in CanSelectAsync turned into a reported missing grant rather than
                -- a crash, which is the drift this whole contract exists to catch.
                "Entitlements" jsonb);
            CREATE TABLE platform."Plans" (
                "Id" bigint PRIMARY KEY,
                "Code" text NOT NULL,
                "{UngrantedColumn}" text NOT NULL,
                "Weight" integer NOT NULL,
                "MaxConcurrentExtractionJobs" integer NOT NULL,
                "MaxDocsPerMonth" integer NOT NULL,
                "MaxSeats" integer NOT NULL,
                "Features" text);
            """);

        // THE TOPOLOGY UNDER TEST. USAGE goes to the execution roles and to nobody else — most
        // importantly not to the login role the contract will connect as, and not to PUBLIC.
        await ExecuteAsync(_superuserConnectionString, """
            REVOKE ALL ON SCHEMA platform FROM PUBLIC;
            GRANT USAGE ON SCHEMA platform TO nexora_tenant_app, nexora_identity_app;
            """);

        await GrantRequiredColumnsAsync(TenantAccessGrantContract.RequiredColumns);

        _loginConnectionString = new NpgsqlConnectionStringBuilder(_superuserConnectionString)
        {
            Username = LoginRole,
            Password = LoginPassword
        }.ConnectionString;
    }

    public async Task DisposeAsync() => await _container.DisposeAsync();

    /// <summary>
    /// The regression. Every grant the contract requires is present, so it must return quietly —
    /// while connected as a role that cannot name a single table in the schema it is checking.
    /// </summary>
    [Fact]
    public async Task Passes_when_every_grant_is_present_even_though_the_login_role_has_no_schema_usage()
    {
        // The premise of the whole test: prove the login role really is privilege-less here, so a
        // future GRANT USAGE that quietly made this pass for the wrong reason fails loudly instead.
        await using (var connection = new NpgsqlConnection(_loginConnectionString))
        {
            await connection.OpenAsync();
            await using var probe = new NpgsqlCommand("SELECT has_schema_privilege('platform', 'USAGE');", connection);
            Assert.False((bool)(await probe.ExecuteScalarAsync())!,
                $"{LoginRole} was granted USAGE on platform, so this test can no longer observe the defect.");
        }

        // Before the fix this threw PostgresException 42501 rather than returning.
        await TenantAccessGrantContract.AssertReadableAsync(_loginConnectionString, NullLogger.Instance);
    }

    /// <summary>
    /// The teeth. Making the check survive a privilege-less caller is worthless if it stopped
    /// detecting the thing it exists for, so revoke the column whose absence caused the original
    /// production incident and require the same fatal, remedy-naming throw.
    /// </summary>
    [Fact]
    public async Task Still_fails_closed_and_names_the_grant_when_a_required_column_is_revoked()
    {
        await ExecuteAsync(_superuserConnectionString,
            "REVOKE SELECT (\"Features\") ON platform.\"Plans\" FROM nexora_tenant_app;");
        try
        {
            var thrown = await Assert.ThrowsAsync<InvalidOperationException>(
                () => TenantAccessGrantContract.AssertReadableAsync(_loginConnectionString, NullLogger.Instance));

            Assert.Contains("GRANT SELECT (\"Features\") ON TABLE platform.\"Plans\" TO nexora_tenant_app;",
                thrown.Message, StringComparison.Ordinal);
            // The other role still holds it, so only one line should be reported.
            Assert.DoesNotContain("TO nexora_identity_app;", thrown.Message, StringComparison.Ordinal);
        }
        finally
        {
            await ExecuteAsync(_superuserConnectionString,
                "GRANT SELECT (\"Features\") ON platform.\"Plans\" TO nexora_tenant_app;");
        }
    }

    /// <summary>
    /// A column the query does not project stays ungranted, and the contract stays silent about it.
    /// Guards the opposite failure: a check that demands blanket SELECT would pass here while
    /// quietly undoing the column-level narrowing 20260805105320 introduced.
    /// </summary>
    [Fact]
    public async Task Does_not_require_columns_the_tenant_access_query_never_projects()
    {
        await using var connection = new NpgsqlConnection(_loginConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            """
            SELECT has_column_privilege('nexora_tenant_app', c.oid::regclass, @column, 'SELECT')
            FROM pg_catalog.pg_class c
            JOIN pg_catalog.pg_namespace n ON n.oid = c.relnamespace
            WHERE n.nspname = 'platform' AND c.relname = 'Plans';
            """, connection);
        command.Parameters.AddWithValue("column", UngrantedColumn);

        Assert.False((bool)(await command.ExecuteScalarAsync())!);
        await TenantAccessGrantContract.AssertReadableAsync(_loginConnectionString, NullLogger.Instance);
    }

    private async Task GrantRequiredColumnsAsync(
        IReadOnlyList<TenantAccessGrantContract.RequiredColumn> columns)
    {
        // Driven off the contract's own list, so a column added there without a grant here shows up
        // as a failure in the test that asserts the happy path rather than as silent drift.
        var grants = columns
            .SelectMany(column => TenantAccessGrantContract.ExecutionRoles.Select(role =>
                $"GRANT SELECT (\"{column.Column}\") ON TABLE {column.QualifiedTable} TO {role};"));
        await ExecuteAsync(_superuserConnectionString, string.Join('\n', grants));
    }

    private static async Task ExecuteAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }
}
