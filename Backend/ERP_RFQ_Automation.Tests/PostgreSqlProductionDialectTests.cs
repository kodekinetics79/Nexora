using ERP_RFQ_Automation.Extraction;
using ERP_RFQ_Automation.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace ERP_RFQ_Automation.Tests;

[Collection(PostgreSqlIntegrationCollection.Name)]
public sealed class PostgreSqlProductionDialectTests
{
    private readonly PostgreSqlTestDatabase _database;

    public PostgreSqlProductionDialectTests(PostgreSqlTestDatabase database)
        => _database = database;

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task AllMigrationsApplyToAnEmptyPostgreSqlDatabase()
    {
        await using var context = _database.ContextFor(null);

        var pending = await context.Database.GetPendingMigrationsAsync();
        var applied = await context.Database.GetAppliedMigrationsAsync();

        Assert.Empty(pending);
        Assert.Contains("20260723120000_CompleteTenantRlsCoverage", applied);

        await using var connection = await _database.OpenConnectionAsync();
        await using var roleCommand = connection.CreateCommand();
        roleCommand.CommandText = """
            SELECT NOT rolcanlogin AND NOT rolsuper AND NOT rolbypassrls
            FROM pg_roles WHERE rolname = 'nexora_tenant_app';
            """;
        Assert.True((bool)(await roleCommand.ExecuteScalarAsync())!);

        var filteredTables = context.Model.GetEntityTypes()
            .Where(entity => entity.GetQueryFilter() is not null && (entity.GetSchema() ?? "public") == "public")
            .Select(entity => entity.GetTableName())
            .Where(table => table is not null)
            .Concat(new[]
            {
                "Attachments", "Contacts", "EmailIngests", "LeadItems", "OrderItems",
                "ProductAttachments", "QuoteItems", "RFQItems", "ShipmentItems",
                "ShipmentStatusHistory", "SupplierPurchaseHistory"
            })
            .Distinct(StringComparer.Ordinal)
            .OrderBy(table => table, StringComparer.Ordinal)
            .ToArray()!;

        await using var policyCommand = connection.CreateCommand();
        policyCommand.CommandText = """
            WITH expected(table_name) AS (SELECT unnest(@tables::text[]))
            SELECT string_agg(expected.table_name, ', ' ORDER BY expected.table_name)
            FROM expected
            LEFT JOIN pg_class table_definition ON table_definition.relname = expected.table_name
            LEFT JOIN pg_namespace schema_definition
                ON schema_definition.oid = table_definition.relnamespace
               AND schema_definition.nspname = 'public'
            LEFT JOIN pg_policy policy
                ON policy.polrelid = table_definition.oid
               AND policy.polname = 'nexora_tenant_isolation'
            LEFT JOIN pg_roles tenant_role ON tenant_role.rolname = 'nexora_tenant_app'
            WHERE schema_definition.oid IS NULL
               OR NOT table_definition.relrowsecurity
               OR policy.oid IS NULL
               OR policy.polqual IS NULL
               OR policy.polwithcheck IS NULL
               OR NOT tenant_role.oid = ANY(policy.polroles)
               OR position('nexora.business_unit_id' in pg_get_expr(policy.polqual, policy.polrelid)) = 0
               OR position('nexora.business_unit_id' in pg_get_expr(policy.polwithcheck, policy.polrelid)) = 0;
            """;
        policyCommand.Parameters.AddWithValue("tables", filteredTables);
        Assert.Null((await policyCommand.ExecuteScalarAsync()) as string);

        await using var tenantColumnCommand = connection.CreateCommand();
        tenantColumnCommand.CommandText = """
            SELECT string_agg(columns.table_name, ', ' ORDER BY columns.table_name)
            FROM information_schema.columns columns
            JOIN pg_class table_definition ON table_definition.relname = columns.table_name
            JOIN pg_namespace schema_definition
              ON schema_definition.oid = table_definition.relnamespace
             AND schema_definition.nspname = columns.table_schema
            WHERE columns.table_schema = 'public'
              AND columns.column_name = ANY(ARRAY[
                  'BusinessUnitID', 'BusinessUnitId', 'business_unit_id',
                  'BUID', 'Buid', 'buid'])
              AND NOT table_definition.relrowsecurity;
            """;
        Assert.Null((await tenantColumnCommand.ExecuteScalarAsync()) as string);

        await using var privilegeCommand = connection.CreateCommand();
        privilegeCommand.CommandText = """
            SELECT string_agg(table_definition.relname, ', ' ORDER BY table_definition.relname)
            FROM pg_class table_definition
            JOIN pg_namespace schema_definition ON schema_definition.oid = table_definition.relnamespace
            WHERE schema_definition.nspname = 'public'
              AND table_definition.relkind IN ('r', 'p')
              AND NOT table_definition.relrowsecurity
              AND (
                  has_table_privilege('nexora_tenant_app', table_definition.oid, 'SELECT')
                  OR has_table_privilege('nexora_tenant_app', table_definition.oid, 'INSERT')
                  OR has_table_privilege('nexora_tenant_app', table_definition.oid, 'UPDATE')
                  OR has_table_privilege('nexora_tenant_app', table_definition.oid, 'DELETE'));
            """;
        Assert.Null((await privilegeCommand.ExecuteScalarAsync()) as string);

        await using var deniedTableCommand = connection.CreateCommand();
        deniedTableCommand.CommandText = """
            SELECT table_definition.relrowsecurity,
                   has_table_privilege('nexora_tenant_app', 'public."__EFMigrationsHistory"', 'SELECT')
            FROM pg_class table_definition
            JOIN pg_namespace schema_definition ON schema_definition.oid = table_definition.relnamespace
            WHERE schema_definition.nspname = 'public' AND table_definition.relname = 'SetCountry';
            """;
        await using var privilegeReader = await deniedTableCommand.ExecuteReaderAsync();
        Assert.True(await privilegeReader.ReadAsync());
        Assert.True(privilegeReader.GetBoolean(0));
        Assert.False(privilegeReader.GetBoolean(1));
        await privilegeReader.DisposeAsync();

        await using var futureTableCommand = connection.CreateCommand();
        futureTableCommand.CommandText = """
            CREATE TABLE public.rls_privilege_canary (id bigint PRIMARY KEY);
            SELECT has_table_privilege('nexora_tenant_app', 'public.rls_privilege_canary', 'SELECT, INSERT, UPDATE, DELETE');
            """;
        Assert.False((bool)(await futureTableCommand.ExecuteScalarAsync())!);
        await using var dropFutureTableCommand = connection.CreateCommand();
        dropFutureTableCommand.CommandText = "DROP TABLE public.rls_privilege_canary;";
        await dropFutureTableCommand.ExecuteNonQueryAsync();

        await using var sequencePrivilegeCommand = connection.CreateCommand();
        sequencePrivilegeCommand.CommandText = """
            SELECT string_agg(sequence_definition.relname, ', ' ORDER BY sequence_definition.relname)
            FROM pg_class sequence_definition
            JOIN pg_namespace schema_definition ON schema_definition.oid = sequence_definition.relnamespace
            WHERE schema_definition.nspname = 'public'
              AND sequence_definition.relkind = 'S'
              AND sequence_definition.relname <> 'CommercialCaseReferenceSequence'
              AND CASE WHEN sequence_definition.relkind = 'S' THEN has_sequence_privilege(
                      'nexora_tenant_app',
                      format('%I.%I', schema_definition.nspname, sequence_definition.relname),
                      'USAGE, SELECT, UPDATE')
                  ELSE false END
              AND NOT EXISTS (
                  SELECT 1
                  FROM pg_depend dependency
                  JOIN pg_class table_definition ON table_definition.oid = dependency.refobjid
                  WHERE dependency.objid = sequence_definition.oid
                    AND dependency.deptype IN ('a', 'i')
                    AND table_definition.relrowsecurity);
            """;
        Assert.Null((await sequencePrivilegeCommand.ExecuteScalarAsync()) as string);

        await using var mutableSequenceCommand = connection.CreateCommand();
        mutableSequenceCommand.CommandText = """
            SELECT string_agg(sequence_definition.relname, ', ' ORDER BY sequence_definition.relname)
            FROM pg_class sequence_definition
            JOIN pg_namespace schema_definition ON schema_definition.oid = sequence_definition.relnamespace
            WHERE schema_definition.nspname = 'public'
              AND sequence_definition.relkind = 'S'
              AND CASE WHEN sequence_definition.relkind = 'S' THEN
                  has_sequence_privilege(
                      'nexora_tenant_app',
                      format('%I.%I', schema_definition.nspname, sequence_definition.relname),
                      'SELECT, UPDATE')
                  ELSE false END;
            """;
        Assert.Null((await mutableSequenceCommand.ExecuteScalarAsync()) as string);

        await using var futureSequenceCommand = connection.CreateCommand();
        futureSequenceCommand.CommandText = """
            CREATE SEQUENCE public.rls_sequence_canary;
            SELECT has_sequence_privilege('nexora_tenant_app', 'public.rls_sequence_canary', 'USAGE, SELECT, UPDATE');
            """;
        Assert.False((bool)(await futureSequenceCommand.ExecuteScalarAsync())!);
        await using var dropFutureSequenceCommand = connection.CreateCommand();
        dropFutureSequenceCommand.CommandText = "DROP SEQUENCE public.rls_sequence_canary;";
        await dropFutureSequenceCommand.ExecuteNonQueryAsync();
    }

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task ConcurrentWorkersClaimDistinctJobsAndRespectTenantCap()
    {
        var marker = Guid.NewGuid().ToString("N");
        const long businessUnitId = 91_001;

        await using (var seed = _database.ContextFor(null))
        {
            var queue = NewQueue(seed);
            for (var index = 0; index < 5; index++)
            {
                var result = await queue.EnqueueAsync(new EnqueueExtractionRequest
                {
                    BusinessUnitId = businessUnitId,
                    SourceType = ExtractionSourceType.ManualUpload,
                    StoragePath = $"test://{marker}/{index}",
                    ContentHash = $"{marker}{index}",
                    FileName = $"rfq-{index}.pdf",
                    FileType = "pdf"
                });
                Assert.Equal(EnqueueOutcome.Enqueued, result.Outcome);
            }
        }

        var claims = await Task.WhenAll(Enumerable.Range(0, 4).Select(async index =>
        {
            await using var context = _database.ContextFor(null);
            return await NewQueue(context).ClaimAsync($"worker-{marker}-{index}", TimeSpan.FromMinutes(5), 4);
        }));

        Assert.All(claims, claim => Assert.NotNull(claim));
        Assert.Equal(4, claims.Select(claim => claim!.Id).Distinct().Count());

        await using var capContext = _database.ContextFor(null);
        var cappedClaim = await NewQueue(capContext).ClaimAsync($"worker-{marker}-capped", TimeSpan.FromMinutes(5), 4);
        Assert.Null(cappedClaim);
        Assert.Equal(1, await capContext.Set<ExtractionJob>()
            .CountAsync(job => job.BusinessUnitId == businessUnitId && job.Status == ExtractionStatus.Pending));
    }

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task CommercialCaseReferencesAreServerGeneratedUniqueAndImmutable()
    {
        var marker = Guid.NewGuid().ToString("N");
        const long businessUnitId = 92_001;
        const long emailIngestId = 92_001;

        await using (var connection = await _database.OpenConnectionAsync())
        await using (var seed = connection.CreateCommand())
        {
            seed.CommandText = """
                INSERT INTO "BusinessUnits" ("ID", "BusinessUnitCode", "BusinessUnitName", "CreatedBy", "CreatedOn")
                VALUES (92001, 'PGCERT', 'PostgreSQL Certification', 'tests', now());

                INSERT INTO "Email_Configurations"
                    ("ID", "BusinessUnitID", "ConfigurationName", "EmailAddress", "Protocol", "Host", "Port", "Username", "Password", "UseSSL", "PollingInterval", "IsActive", "CreatedOn")
                VALUES (92001, 92001, 'tests', 'tests@nexora.invalid', 'IMAP', 'localhost', 993, 'tests', 'tests', true, 300, false, now());

                INSERT INTO "EmailIngests"
                    ("ID", "MessageID", "FromEmail", "EmailConfigurationID", "CreatedOn")
                VALUES (92001, 'postgres-certification', 'buyer@nexora.invalid', 92001, now());
                """;
            await seed.ExecuteNonQueryAsync();
        }

        var inserts = Enumerable.Range(0, 12).Select(async index =>
        {
            await using var connection = await _database.OpenConnectionAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO "Leads"
                    ("RFQNo", "RecDate", "LeadSource", "CreatedBy", "CreatedDate", "BusinessUnitID", "EmailIngestsID")
                VALUES (@rfq, now(), 'IntegrationTest', 'tests', now(), @businessUnitId, @emailIngestId)
                RETURNING "ID", "CommercialCaseReference";
                """;
            command.Parameters.AddWithValue("rfq", $"{marker}-{index}");
            command.Parameters.AddWithValue("businessUnitId", businessUnitId);
            command.Parameters.AddWithValue("emailIngestId", emailIngestId);
            await using var reader = await command.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            return (Id: reader.GetInt64(0), Reference: reader.GetString(1));
        });

        var leads = await Task.WhenAll(inserts);

        Assert.Equal(12, leads.Select(lead => lead.Reference).Distinct().Count());
        Assert.All(leads, lead => Assert.StartsWith("NXR-", lead.Reference));

        await using var immutableConnection = await _database.OpenConnectionAsync();
        await using var immutableCommand = immutableConnection.CreateCommand();
        immutableCommand.CommandText = "UPDATE \"Leads\" SET \"CommercialCaseReference\" = 'FORGED' WHERE \"ID\" = @id;";
        immutableCommand.Parameters.AddWithValue("id", leads[0].Id);
        await Assert.ThrowsAsync<PostgresException>(() => immutableCommand.ExecuteNonQueryAsync());
    }

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task TenantRoleAndCommandInterceptorEnforceRowLevelIsolation()
    {
        var marker = Guid.NewGuid().ToString("N");
        const long tenantOne = 93_001;
        const long tenantTwo = 93_002;

        await SeedRlsLeadsAsync(marker, tenantOne, tenantTwo);

        await using (var tenantContext = _database.TenantContextWithRls(tenantOne))
        {
            var visible = await tenantContext.Leads.IgnoreQueryFilters()
                .Where(lead => lead.Rfqno != null && lead.Rfqno.StartsWith(marker))
                .Select(lead => lead.BusinessUnitId)
                .ToListAsync();

            Assert.Equal(new[] { tenantOne }, visible);

            var visibleChildRows = await tenantContext.LeadItems.IgnoreQueryFilters()
                .CountAsync(item => item.LeadId == tenantOne || item.LeadId == tenantTwo);
            Assert.Equal(1, visibleChildRows);
        }

        // The RLS test pool has MaxPoolSize=1, so this second scope reuses the same
        // physical connection and proves transaction-local tenant/role state cannot leak.
        await using (var tenantContext = _database.TenantContextWithRls(tenantTwo))
        {
            var visible = await tenantContext.Leads.IgnoreQueryFilters()
                .Where(lead => lead.Rfqno != null && lead.Rfqno.StartsWith(marker))
                .Select(lead => lead.BusinessUnitId)
                .ToListAsync();

            Assert.Equal(new[] { tenantTwo }, visible);
        }

        await using (var tenantContext = _database.TenantContextWithRls(tenantOne))
        await using (var transaction = await tenantContext.Database.BeginTransactionAsync())
        {
            var visibleInsideServiceTransaction = await tenantContext.Leads.IgnoreQueryFilters()
                .CountAsync(lead => lead.Rfqno != null && lead.Rfqno.StartsWith(marker));
            Assert.Equal(1, visibleInsideServiceTransaction);
            await transaction.CommitAsync();
        }

        await using (var tenantContext = _database.TenantContextWithRls(tenantOne))
        {
            var childException = await Assert.ThrowsAsync<PostgresException>(() =>
                tenantContext.Database.ExecuteSqlRawAsync("""
                    INSERT INTO "LeadItems" ("LeadID", "Quantity") VALUES (93002, 1);
                    """));
            Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, childException.SqlState);

            var exception = await Assert.ThrowsAsync<PostgresException>(() => tenantContext.Database.ExecuteSqlRawAsync("""
                INSERT INTO "Leads"
                    ("RFQNo", "RecDate", "LeadSource", "CreatedBy", "CreatedDate", "BusinessUnitID", "EmailIngestsID")
                VALUES ('rls-forged', now(), 'IntegrationTest', 'tests', now(), 93002, 93002);
                """));
            Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, exception.SqlState);
        }

        await using var connection = await _database.OpenConnectionAsync();
        await using var transactionWithoutTenant = await connection.BeginTransactionAsync();
        await using var noTenantCommand = connection.CreateCommand();
        noTenantCommand.Transaction = transactionWithoutTenant;
        noTenantCommand.CommandText = """
            SET LOCAL ROLE nexora_tenant_app;
            SELECT count(*) FROM "Leads" WHERE "RFQNo" LIKE @marker;
            """;
        noTenantCommand.Parameters.AddWithValue("marker", marker + "%");
        Assert.Equal(0L, (long)(await noTenantCommand.ExecuteScalarAsync())!);
        await transactionWithoutTenant.RollbackAsync();
    }

    private async Task SeedRlsLeadsAsync(string marker, long tenantOne, long tenantTwo)
    {
        await using var connection = await _database.OpenConnectionAsync();
        await using var seed = connection.CreateCommand();
        seed.CommandText = """
            INSERT INTO "BusinessUnits" ("ID", "BusinessUnitCode", "BusinessUnitName", "CreatedBy", "CreatedOn")
            VALUES
                (@tenantOne, 'RLS1', 'RLS Tenant One', 'tests', now()),
                (@tenantTwo, 'RLS2', 'RLS Tenant Two', 'tests', now());

            INSERT INTO "Email_Configurations"
                ("ID", "BusinessUnitID", "ConfigurationName", "EmailAddress", "Protocol", "Host", "Port", "Username", "Password", "UseSSL", "PollingInterval", "IsActive", "CreatedOn")
            VALUES
                (@tenantOne, @tenantOne, 'rls-1', 'rls1@nexora.invalid', 'IMAP', 'localhost', 993, 'tests', 'tests', true, 300, false, now()),
                (@tenantTwo, @tenantTwo, 'rls-2', 'rls2@nexora.invalid', 'IMAP', 'localhost', 993, 'tests', 'tests', true, 300, false, now());

            INSERT INTO "EmailIngests" ("ID", "MessageID", "FromEmail", "EmailConfigurationID", "CreatedOn")
            VALUES
                (@tenantOne, @messageOne, 'buyer1@nexora.invalid', @tenantOne, now()),
                (@tenantTwo, @messageTwo, 'buyer2@nexora.invalid', @tenantTwo, now());

            INSERT INTO "Leads"
                ("ID", "RFQNo", "RecDate", "LeadSource", "CreatedBy", "CreatedDate", "BusinessUnitID", "EmailIngestsID")
            VALUES
                (@tenantOne, @rfqOne, now(), 'IntegrationTest', 'tests', now(), @tenantOne, @tenantOne),
                (@tenantTwo, @rfqTwo, now(), 'IntegrationTest', 'tests', now(), @tenantTwo, @tenantTwo);

            INSERT INTO "LeadItems" ("LeadID", "Quantity")
            VALUES (@tenantOne, 1), (@tenantTwo, 1);
            """;
        seed.Parameters.AddWithValue("tenantOne", tenantOne);
        seed.Parameters.AddWithValue("tenantTwo", tenantTwo);
        seed.Parameters.AddWithValue("messageOne", marker + "-message-1");
        seed.Parameters.AddWithValue("messageTwo", marker + "-message-2");
        seed.Parameters.AddWithValue("rfqOne", marker + "-rfq-1");
        seed.Parameters.AddWithValue("rfqTwo", marker + "-rfq-2");
        await seed.ExecuteNonQueryAsync();
    }

    private static ExtractionQueue NewQueue(ERP_RFQ_Automation.Models.ErpRfqAutomationContext context)
        => new(context, new NoopLogger<ExtractionQueue>());
}
