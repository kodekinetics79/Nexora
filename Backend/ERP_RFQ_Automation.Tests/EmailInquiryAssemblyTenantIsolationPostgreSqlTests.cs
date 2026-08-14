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
        // The column default was deliberately REMOVED. EF's HasDefaultValue marks the property
        // ValueGenerated.OnAdd, so an assembly constructed with ManifestContractVersion = 0 - a
        // forgotten assignment - was silently stored as 1 rather than as an obviously wrong 0,
        // which would defeat the very mismatch detector the column exists to feed.
        Assert.True(await reader.IsDBNullAsync(1),
            "ManifestContractVersion must have no column default, so an unset value stays visibly 0.");
    }

    [Fact]
    public async Task A_tenant_cannot_read_or_write_another_tenants_assembly_with_filters_bypassed()
    {
        // THE negative test, and the previous version could not fail.
        //
        // It swallowed the foreign-key violation from an unseeded parent, so tenant A's row was
        // never inserted and `count(*) = 0` held for trivial reasons — it passed with row-level
        // security dropped entirely. The fix is to prove the row genuinely EXISTS as the owner
        // first, so a zero under tenant B means isolation and nothing else.
        const long tenantA = 918_001;
        const long tenantB = 918_002;

        await using var connection = await _database.OpenConnectionAsync();
        var (ingestId, assemblyId) = await SeedAssemblyAsync(connection, tenantA);

        // 1. The owner can see it. If this is 0 the test is broken, not the database.
        Assert.Equal(1L, await ScalarAsync(connection,
            $"SELECT count(*) FROM public.\"EmailInquiryAssemblies\" WHERE \"Id\" = {assemblyId};"));

        // 2. Tenant B cannot READ it — raw SQL as the tenant role, EF entirely bypassed.
        Assert.Equal(0L, await AsTenantAsync(connection, tenantB,
            $"SELECT count(*) FROM public.\"EmailInquiryAssemblies\" WHERE \"Id\" = {assemblyId};"));

        // 3. Tenant B cannot UPDATE it. A policy that only filters reads still lets a neighbour
        //    corrupt rows it cannot see.
        Assert.Equal(0L, await AsTenantAsync(connection, tenantB,
            $"WITH u AS (UPDATE public.\"EmailInquiryAssemblies\" SET \"Status\" = 'NoInquiry' "
            + $"WHERE \"Id\" = {assemblyId} RETURNING 1) SELECT count(*) FROM u;"));

        // 4. Tenant B cannot DELETE it.
        Assert.Equal(0L, await AsTenantAsync(connection, tenantB,
            $"WITH d AS (DELETE FROM public.\"EmailInquiryAssemblies\" WHERE \"Id\" = {assemblyId} "
            + "RETURNING 1) SELECT count(*) FROM d;"));

        // 5. And the row is untouched by any of it.
        Assert.Equal(1L, await ScalarAsync(connection,
            $"SELECT count(*) FROM public.\"EmailInquiryAssemblies\" "
            + $"WHERE \"Id\" = {assemblyId} AND \"Status\" = 'Captured';"));

        await CleanupAsync(connection, assemblyId, ingestId);
    }

    [Fact]
    public async Task A_tenant_cannot_reach_another_tenants_components()
    {
        const long tenantA = 918_011;
        const long tenantB = 918_012;

        await using var connection = await _database.OpenConnectionAsync();
        var (ingestId, assemblyId) = await SeedAssemblyAsync(connection, tenantA);

        await using (var seed = connection.CreateCommand())
        {
            seed.CommandText = $"""
                INSERT INTO public."EmailInquiryComponents"
                    ("BusinessUnitId","AssemblyId","ComponentKey","Kind","Ordinal","Status",
                     "NestingDepth","ConcurrencyVersion","CreatedAtUtc","UpdatedAtUtc")
                VALUES ({tenantA}, {assemblyId}, 'email:isolation:part:1', 'Attachment', 0,
                        'Pending', 0, 0, now(), now());
                """;
            await seed.ExecuteNonQueryAsync();
        }

        Assert.Equal(1L, await ScalarAsync(connection,
            $"SELECT count(*) FROM public.\"EmailInquiryComponents\" WHERE \"AssemblyId\" = {assemblyId};"));
        Assert.Equal(0L, await AsTenantAsync(connection, tenantB,
            $"SELECT count(*) FROM public.\"EmailInquiryComponents\" WHERE \"AssemblyId\" = {assemblyId};"));

        await CleanupAsync(connection, assemblyId, ingestId);
    }

    /// <summary>Seeds the real parent chain so an insert cannot fail for unrelated reasons.</summary>
    private static async Task<(long IngestId, long AssemblyId)> SeedAssemblyAsync(
        NpgsqlConnection connection, long businessUnitId)
    {
        var suffix = businessUnitId;
        await using (var seed = connection.CreateCommand())
        {
            seed.CommandText = $"""
                INSERT INTO public."BusinessUnits" ("ID","BusinessUnitCode","BusinessUnitName","IsActive","CreatedBy","CreatedOn")
                VALUES ({businessUnitId}, 'ISO{suffix}', 'Isolation {suffix}', true, 'test', now())
                ON CONFLICT DO NOTHING;

                INSERT INTO public."Email_Configurations"
                    ("ID","BusinessUnitID","ConfigurationName","EmailAddress","Protocol","Host","Port",
                     "Username","Password","UseSSL","PollingInterval","IsActive","CreatedOn")
                VALUES ({businessUnitId}, {businessUnitId}, 'Inbound', 'rfq{suffix}@nexora.example',
                        'IMAP', 'imap.secureserver.net', 993, 'rfq{suffix}@nexora.example',
                        'not-a-real-credential', true, 5, true, now())
                ON CONFLICT DO NOTHING;
                """;
            await seed.ExecuteNonQueryAsync();
        }

        long ingestId;
        await using (var ingest = connection.CreateCommand())
        {
            ingest.CommandText = $"""
                INSERT INTO public."EmailIngests" ("MessageID","FromEmail","EmailConfigurationID","CreatedOn")
                VALUES ('isolation-{suffix}@customer.example', 'buyer@customer.example', {businessUnitId}, now())
                RETURNING "ID";
                """;
            ingestId = Convert.ToInt64(await ingest.ExecuteScalarAsync());
        }

        long assemblyId;
        await using (var assembly = connection.CreateCommand())
        {
            assembly.CommandText = $"""
                INSERT INTO public."EmailInquiryAssemblies"
                    ("BusinessUnitId","EmailIngestId","EmailConfigurationId","MessageKey",
                     "ManifestContractVersion","ExpectedComponentCount","CompletedComponentCount",
                     "Status","ConcurrencyVersion","CreatedAtUtc","UpdatedAtUtc")
                VALUES ({businessUnitId}, {ingestId}, {businessUnitId}, 'isolation-{suffix}@customer.example',
                        1, 0, 0, 'Captured', 0, now(), now())
                RETURNING "Id";
                """;
            assemblyId = Convert.ToInt64(await assembly.ExecuteScalarAsync());
        }

        return (ingestId, assemblyId);
    }

    private static async Task<long> ScalarAsync(NpgsqlConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    /// <summary>Runs a statement as <c>nexora_tenant_app</c> with the tenant GUC set.</summary>
    private static async Task<long> AsTenantAsync(
        NpgsqlConnection connection, long businessUnitId, string sql)
    {
        await using var transaction = await connection.BeginTransactionAsync();
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            $"SET LOCAL ROLE nexora_tenant_app; SET LOCAL nexora.business_unit_id = '{businessUnitId}'; {sql}";
        var value = Convert.ToInt64(await command.ExecuteScalarAsync());
        await transaction.RollbackAsync();
        return value;
    }

    private static async Task CleanupAsync(NpgsqlConnection connection, long assemblyId, long ingestId)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            DELETE FROM public."EmailInquiryComponents" WHERE "AssemblyId" = {assemblyId};
            DELETE FROM public."EmailInquiryAssemblies" WHERE "Id" = {assemblyId};
            DELETE FROM public."EmailIngests" WHERE "ID" = {ingestId};
            """;
        await command.ExecuteNonQueryAsync();
    }
}
