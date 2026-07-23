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
        Assert.Contains("20260723031900_AddTenantRowLevelSecurity", applied);

        await using var connection = await _database.OpenConnectionAsync();
        await using var roleCommand = connection.CreateCommand();
        roleCommand.CommandText = """
            SELECT NOT rolcanlogin AND NOT rolsuper AND NOT rolbypassrls
            FROM pg_roles WHERE rolname = 'nexora_tenant_app';
            """;
        Assert.True((bool)(await roleCommand.ExecuteScalarAsync())!);

        await using var policyCommand = connection.CreateCommand();
        policyCommand.CommandText = """
            SELECT count(*)
            FROM pg_class
            WHERE relrowsecurity
              AND relname = ANY (ARRAY[
                  'Leads', 'RFQ', 'Quotes', 'Orders', 'Shipments', 'CommercialCases',
                  'LeadStatusHistories', 'commercial_lifecycle_events',
                  'lifecycle_outbox_messages', 'document_corpora', 'source_documents',
                  'document_pages', 'document_regions', 'canonical_inquiries',
                  'canonical_line_items', 'field_evidence']);
            """;
        Assert.Equal(16L, (long)(await policyCommand.ExecuteScalarAsync())!);
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
            await Assert.ThrowsAsync<PostgresException>(() => tenantContext.Database.ExecuteSqlRawAsync("""
                INSERT INTO "Leads"
                    ("RFQNo", "RecDate", "LeadSource", "CreatedBy", "CreatedDate", "BusinessUnitID", "EmailIngestsID")
                VALUES ('rls-forged', now(), 'IntegrationTest', 'tests', now(), 93002, 93002);
                """));
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
                ("RFQNo", "RecDate", "LeadSource", "CreatedBy", "CreatedDate", "BusinessUnitID", "EmailIngestsID")
            VALUES
                (@rfqOne, now(), 'IntegrationTest', 'tests', now(), @tenantOne, @tenantOne),
                (@rfqTwo, now(), 'IntegrationTest', 'tests', now(), @tenantTwo, @tenantTwo);
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
