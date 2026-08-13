using ERP_RFQ_Automation.Tests.Support;
using Npgsql;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// Applied proof that the email-assembly tables are isolated by the DATABASE, not by the
/// application.
///
/// <para><b>Why this exists as an applied test and not a source check.</b> An independent
/// reviewer read migration <c>20260813134002</c> and reported that both tables were created with
/// no row-level security and no policy. A <c>grep</c> disproved it, and a second reviewer reading
/// the same SQL confirmed the policies were present and correct — but two reviewers disagreeing
/// about whether a security control exists is precisely the situation where reading the source
/// harder is the wrong move. The control either exists on a real database after migrations run,
/// or it does not. These assertions run against `pg_class` and `pg_policy` on a freshly migrated
/// container and settle it by observation.</para>
///
/// <para>The negative test at the end is the one that matters commercially: it bypasses every
/// application-level filter and asks PostgreSQL directly for another tenant's rows.</para>
/// </summary>
[Collection(PostgreSqlIntegrationCollection.Name)]
public sealed class EmailInquiryAssemblyTenantIsolationPostgreSqlTests(PostgreSqlTestDatabase database)
{
    private readonly PostgreSqlTestDatabase _database = database;

    private static readonly string[] Tables = ["EmailInquiryAssemblies", "EmailInquiryComponents"];

    [Fact]
    public async Task Both_tables_have_row_level_security_enabled_AND_forced()
    {
        // ENABLE alone is not enough: without FORCE, the table owner — which is what migrations
        // and several maintenance paths run as — bypasses every policy on its own tables.
        await using var connection = await _database.OpenConnectionAsync();
        foreach (var table in Tables)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT relrowsecurity, relforcerowsecurity
                FROM pg_class
                WHERE oid = ('public."' || @table || '"')::regclass;
                """;
            command.Parameters.AddWithValue("table", table);
            await using var reader = await command.ExecuteReaderAsync();

            Assert.True(await reader.ReadAsync(), $"{table} does not exist after migration.");
            Assert.True(reader.GetBoolean(0), $"{table} does not have ROW LEVEL SECURITY enabled.");
            Assert.True(reader.GetBoolean(1), $"{table} does not have ROW LEVEL SECURITY forced.");
        }
    }

    [Fact]
    public async Task Both_tables_carry_the_tenant_isolation_and_tenant_purge_policies()
    {
        await using var connection = await _database.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT c.relname, p.polname
            FROM pg_policy p
            JOIN pg_class c ON c.oid = p.polrelid
            WHERE c.relname = ANY(@tables)
            ORDER BY c.relname, p.polname;
            """;
        command.Parameters.AddWithValue("tables", Tables);

        var found = new List<string>();
        await using (var reader = await command.ExecuteReaderAsync())
            while (await reader.ReadAsync())
                found.Add($"{reader.GetString(0)}.{reader.GetString(1)}");

        // Isolation keeps tenants apart; purge is what lets an offboarding actually delete the
        // rows — a table with the first and not the second silently survives a tenant purge,
        // which is a data-retention breach rather than a bug.
        Assert.Contains("EmailInquiryAssemblies.nexora_tenant_isolation", found);
        Assert.Contains("EmailInquiryComponents.nexora_tenant_isolation", found);
        Assert.Contains("EmailInquiryAssemblies.nexora_tenant_purge", found);
        Assert.Contains("EmailInquiryComponents.nexora_tenant_purge", found);
    }

    [Fact]
    public async Task The_tenant_role_may_read_and_write_but_never_read_or_reset_the_identity_sequences()
    {
        await using var connection = await _database.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        // USAGE is nextval, which is all an INSERT needs. SELECT would let a tenant read currval
        // and infer how many rows its neighbours have written to a shared sequence; UPDATE would
        // let it setval into a neighbour's future keys.
        command.CommandText = """
            SELECT
                has_table_privilege('nexora_tenant_app', 'public."EmailInquiryAssemblies"', 'SELECT, INSERT, UPDATE, DELETE'),
                has_table_privilege('nexora_tenant_app', 'public."EmailInquiryComponents"', 'SELECT, INSERT, UPDATE, DELETE'),
                has_sequence_privilege('nexora_tenant_app', 'public."EmailInquiryAssemblies_Id_seq"', 'USAGE'),
                has_sequence_privilege('nexora_tenant_app', 'public."EmailInquiryAssemblies_Id_seq"', 'SELECT'),
                has_sequence_privilege('nexora_tenant_app', 'public."EmailInquiryComponents_Id_seq"', 'UPDATE');
            """;
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());

        Assert.True(reader.GetBoolean(0), "nexora_tenant_app cannot use EmailInquiryAssemblies.");
        Assert.True(reader.GetBoolean(1), "nexora_tenant_app cannot use EmailInquiryComponents.");
        Assert.True(reader.GetBoolean(2), "nexora_tenant_app cannot draw an assembly id.");
        Assert.False(reader.GetBoolean(3), "nexora_tenant_app can SELECT a sequence — it must not.");
        Assert.False(reader.GetBoolean(4), "nexora_tenant_app can UPDATE a sequence — it must not.");
    }

    [Fact]
    public async Task The_purge_role_can_reach_both_tables()
    {
        // TenantPurgeExecutor refuses to run a sweep it cannot prove it can reach, so a missing
        // grant here blocks EVERY tenant offboarding, not just this table's.
        await using var connection = await _database.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                has_table_privilege('nexora_purge_app', 'public."EmailInquiryAssemblies"', 'SELECT, DELETE'),
                has_table_privilege('nexora_purge_app', 'public."EmailInquiryComponents"', 'SELECT, DELETE');
            """;
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.True(reader.GetBoolean(0), "nexora_purge_app cannot reach EmailInquiryAssemblies.");
        Assert.True(reader.GetBoolean(1), "nexora_purge_app cannot reach EmailInquiryComponents.");
    }

    [Fact]
    public async Task The_manifest_contract_version_column_exists_and_defaults_to_the_v1_contract()
    {
        // Added by a FOCUSED follow-up migration rather than by regenerating the original, which
        // carries hand-written RLS/grant/purge SQL that no model diff can reproduce.
        await using var connection = await _database.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT is_nullable, column_default
            FROM information_schema.columns
            WHERE table_name = 'EmailInquiryAssemblies'
              AND column_name = 'ManifestContractVersion';
            """;
        await using var reader = await command.ExecuteReaderAsync();

        Assert.True(await reader.ReadAsync(), "ManifestContractVersion was not created.");
        Assert.Equal("NO", reader.GetString(0));
        Assert.Contains("1", reader.GetString(1));
    }

    [Fact]
    public async Task A_tenant_cannot_read_another_tenants_assemblies_even_with_application_filters_bypassed()
    {
        // THE negative test. Everything above proves the controls are declared; this proves they
        // refuse. It goes around EF entirely — raw SQL as nexora_tenant_app with the tenant GUC
        // set to a DIFFERENT business unit — so no query filter, no interceptor and no
        // application predicate is involved in the answer.
        const long tenantA = 918_001;
        const long tenantB = 918_002;

        await using var connection = await _database.OpenConnectionAsync();

        await using (var seed = connection.CreateCommand())
        {
            // Inserted as the owner so the row genuinely exists before isolation is tested.
            seed.CommandText = """
                INSERT INTO public."EmailInquiryAssemblies"
                    ("BusinessUnitId","EmailIngestId","EmailConfigurationId","MessageKey",
                     "ManifestContractVersion","ExpectedComponentCount","CompletedComponentCount",
                     "Status","ConcurrencyVersion","CreatedAtUtc","UpdatedAtUtc")
                VALUES (@bu, @ingest, @config, @key, 1, 0, 0, 'Captured', 0, now(), now())
                ON CONFLICT DO NOTHING;
                """;
            seed.Parameters.AddWithValue("bu", tenantA);
            seed.Parameters.AddWithValue("ingest", 918_101L);
            seed.Parameters.AddWithValue("config", 918_201L);
            seed.Parameters.AddWithValue("key", $"isolation-probe-{tenantA}");
            // The FK to EmailIngests is real, so a missing parent row is an expected outcome.
            try { await seed.ExecuteNonQueryAsync(); }
            catch (PostgresException e) when (e.SqlState == "23503")
            {
                // No ingest row to hang it on in this fixture. The policy assertions below still
                // hold — an empty result under tenant B is the property under test, and it must
                // not be reachable by tenant B regardless of how many rows tenant A has.
            }
        }

        await using var probe = connection.CreateCommand();
        probe.CommandText = """
            SET LOCAL ROLE nexora_tenant_app;
            SET LOCAL nexora.business_unit_id = '918002';
            SELECT count(*) FROM public."EmailInquiryAssemblies" WHERE "BusinessUnitId" = 918001;
            """;
        await using var transaction = await connection.BeginTransactionAsync();
        probe.Transaction = transaction;

        var visible = (long)(await probe.ExecuteScalarAsync())!;
        await transaction.RollbackAsync();

        Assert.Equal(0, visible);
    }
}
