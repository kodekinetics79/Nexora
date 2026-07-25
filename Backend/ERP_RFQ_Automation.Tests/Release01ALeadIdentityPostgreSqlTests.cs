using ERP_RFQ_Automation.LeadIdentity;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;

namespace ERP_RFQ_Automation.Tests;

[Collection(PostgreSqlIntegrationCollection.Name)]
[Trait("Category", "PostgreSQL")]
public sealed class Release01ALeadIdentityPostgreSqlTests
{
    private readonly PostgreSqlTestDatabase _database;
    public Release01ALeadIdentityPostgreSqlTests(PostgreSqlTestDatabase database) => _database = database;

    [Fact]
    public async Task Populated_migration_rollback_and_reupgrade_preserve_identity_and_single_revision_one()
    {
        var databaseName = $"release01a_upgrade_{Guid.NewGuid():N}";
        var adminBuilder = new NpgsqlConnectionStringBuilder(_database.ConnectionString) { Database = "postgres" };
        var isolatedBuilder = new NpgsqlConnectionStringBuilder(_database.ConnectionString) { Database = databaseName };
        await using (var admin = new NpgsqlConnection(adminBuilder.ConnectionString))
        {
            await admin.OpenAsync();
            await using var create = admin.CreateCommand(); create.CommandText = $"CREATE DATABASE \"{databaseName}\""; await create.ExecuteNonQueryAsync();
        }
        try
        {
            await using var db = _database.ContextForConnectionString(isolatedBuilder.ConnectionString, null);
            var migrator = db.GetService<IMigrator>();
            const string previous = "20260724230121_Release01OrderLineage";
            const string release01a = "20260725010019_Release01AAiProviderClassification";
            await migrator.MigrateAsync(previous);
            await db.Database.ExecuteSqlRawAsync("""
                INSERT INTO "BusinessUnits" ("ID","BusinessUnitCode","BusinessUnitName","CreatedBy","CreatedOn")
                VALUES (99121,'R01AUP','Release 01A upgrade','tests',now());
                INSERT INTO "Email_Configurations"
                  ("ID","BusinessUnitID","ConfigurationName","EmailAddress","Protocol","Host","Port","Username","Password","UseSSL","PollingInterval","IsActive","CreatedOn")
                VALUES (99121,99121,'upgrade','upgrade@nexora.invalid','IMAP','localhost',993,'tests','tests',true,300,false,now());
                INSERT INTO "EmailIngests" ("ID","MessageID","FromEmail","EmailConfigurationID","CreatedOn")
                VALUES (99121,'release-01a-upgrade','buyer@nexora.invalid',99121,now());
                INSERT INTO "Leads" ("ID","RFQNo","RecDate","LeadSource","CreatedBy","CreatedDate","BusinessUnitID","EmailIngestsID","Clientemail")
                VALUES (99121,'CUSTOMER-RFQ-99121',now(),'MigrationTest','tests',now(),99121,99121,'buyer@nexora.invalid');
                INSERT INTO "LeadItems" ("ID","LeadID","LineItemNo","ManufacturerPartNumber","Quantity","UnitOfMeasure")
                VALUES (99121,99121,'1','PART-99121',4,'EA');
                """);
            var serial = await db.Database.SqlQueryRaw<string>("SELECT \"CommercialCaseReference\" AS \"Value\" FROM \"Leads\" WHERE \"ID\"=99121").SingleAsync();

            await migrator.MigrateAsync(release01a);
            await AssertBackfill(db, serial);
            await migrator.MigrateAsync(previous);
            Assert.Equal(serial, await db.Database.SqlQueryRaw<string>("SELECT \"CommercialCaseReference\" AS \"Value\" FROM \"Leads\" WHERE \"ID\"=99121").SingleAsync());
            await migrator.MigrateAsync(release01a);
            await AssertBackfill(db, serial);
        }
        finally
        {
            NpgsqlConnection.ClearAllPools();
            await using var admin = new NpgsqlConnection(adminBuilder.ConnectionString); await admin.OpenAsync();
            await using var drop = admin.CreateCommand(); drop.CommandText = $"DROP DATABASE IF EXISTS \"{databaseName}\" WITH (FORCE)"; await drop.ExecuteNonQueryAsync();
        }
    }

    private static async Task AssertBackfill(DbContext db, string serial)
    {
        Assert.Equal(1, await db.Database.SqlQueryRaw<int>("SELECT count(*)::int AS \"Value\" FROM \"LeadRevisions\" WHERE \"BusinessUnitId\"=99121 AND \"LeadId\"=99121 AND \"RevisionNumber\"=1").SingleAsync());
        Assert.Equal(1, await db.Database.SqlQueryRaw<int>("SELECT count(*)::int AS \"Value\" FROM \"LeadIngestionOccurrences\" WHERE \"BusinessUnitId\"=99121 AND \"LeadId\"=99121").SingleAsync());
        Assert.Equal(serial, await db.Database.SqlQueryRaw<string>("SELECT \"CommercialCaseReference\" AS \"Value\" FROM \"Leads\" WHERE \"ID\"=99121").SingleAsync());
    }

    [Fact]
    public async Task Runtime_role_cannot_read_other_tenant_or_delete_history()
    {
        const long tenant = 99101;
        await using var owner = await _database.OpenConnectionAsync();
        await using (var setup = new NpgsqlCommand("INSERT INTO \"BusinessUnits\" (\"ID\",\"BusinessUnitCode\",\"BusinessUnitName\",\"IsActive\",\"CreatedBy\",\"CreatedOn\") VALUES (@id,'R01A','Release 01A',true,'test',now()) ON CONFLICT DO NOTHING", owner))
        { setup.Parameters.AddWithValue("id", tenant); await setup.ExecuteNonQueryAsync(); }
        var batchId = Guid.NewGuid();
        await using (var runtime = _database.TenantContextWithRls(tenant))
        {
            runtime.Add(new LeadIngestionBatch { Id = batchId, BusinessUnitId = tenant, SourceChannel = "Test", CreatedBy = "tests", CreatedAtUtc = DateTimeOffset.UtcNow, UpdatedAtUtc = DateTimeOffset.UtcNow });
            await runtime.SaveChangesAsync();
            Assert.Single(await runtime.Set<LeadIngestionBatch>().IgnoreQueryFilters().Where(x => x.Id == batchId).ToListAsync());
            runtime.Remove(await runtime.Set<LeadIngestionBatch>().SingleAsync(x => x.Id == batchId));
            var denied = await Assert.ThrowsAsync<DbUpdateException>(() => runtime.SaveChangesAsync());
            Assert.Equal("42501", Assert.IsType<PostgresException>(denied.InnerException).SqlState);
        }
        await using var otherTenant = _database.TenantContextWithRls(tenant + 1);
        Assert.Empty(await otherTenant.Set<LeadIngestionBatch>().IgnoreQueryFilters().Where(x => x.Id == batchId).ToListAsync());

        await using (var create = _database.ContextFor(tenant))
        {
            Seed.EmailConfig(create, 99101, tenant); Seed.EmailIngest(create, 99101, 99101, "NeedsReview"); await create.SaveChangesAsync();
            var lead = new Lead { Rfqno = "RLS-RFQ", Clientemail = "rls@customer.test", RecDate = DateTime.UtcNow, LeadSource = "Test",
                CreatedBy = "tests", CreatedDate = DateTime.UtcNow, BusinessUnitId = tenant, EmailIngestsId = 99101 };
            lead.LeadItems.Add(new LeadItem { LineItemNo = "1", ManufacturerPartNumber = "RLS-PART", Quantity = 1 });
            await new LeadIdentityApplicationService(create).ReconcileAsync(lead, new LeadIntakeDescriptor(batchId, "API", "rls-occurrence", null, null,
                "test", lead.Clientemail, null, "rls.json", "application/json", 10, new string('b', 64), null, null, null, DateTimeOffset.UtcNow,
                LeadProcessingPath.Deterministic, false, 0, "Test", "tests", "rls-test"));
        }
        Assert.Empty(await otherTenant.Set<LeadIngestionOccurrence>().IgnoreQueryFilters().Where(x => x.BusinessUnitId == tenant).ToListAsync());
        Assert.Empty(await otherTenant.Set<LeadRevision>().IgnoreQueryFilters().Where(x => x.BusinessUnitId == tenant).ToListAsync());
        Assert.Empty(await otherTenant.Set<LeadItemRevision>().IgnoreQueryFilters().Where(x => x.BusinessUnitId == tenant).ToListAsync());
        Assert.Empty(await otherTenant.Set<LeadIdentityAuditEvent>().IgnoreQueryFilters().Where(x => x.BusinessUnitId == tenant).ToListAsync());

        await using var sameTenant = _database.TenantContextWithRls(tenant);
        var occurrence = await sameTenant.Set<LeadIngestionOccurrence>().SingleAsync(x => x.IdempotencyKey == "rls-occurrence");
        occurrence.ProcessingPath = LeadProcessingPath.ExternalModel;
        var immutable = await Assert.ThrowsAsync<DbUpdateException>(() => sameTenant.SaveChangesAsync());
        Assert.Equal("P0001", Assert.IsType<PostgresException>(immutable.InnerException).SqlState);
    }

    [Fact]
    public async Task Concurrent_duplicate_ingestion_converges_on_one_lead_and_two_occurrences()
    {
        const long tenant = 99131;
        await using (var seed = _database.ContextFor(tenant))
        {
            Seed.BusinessUnit(seed, tenant); Seed.EmailConfig(seed, 99131, tenant); Seed.EmailIngest(seed, 99131, 99131, "NeedsReview");
            await seed.SaveChangesAsync();
        }
        var batch = Guid.NewGuid();
        async Task<LeadReconciliationResult> Ingest(string key)
        {
            await using var context = _database.ContextFor(tenant);
            var lead = new Lead { Rfqno = "RACE-RFQ", BuyersName = "Race Buyer", Clientemail = "race@customer.test", RecDate = DateTime.UtcNow,
                LeadSource = "Test", CreatedBy = "tests", CreatedDate = DateTime.UtcNow, BusinessUnitId = tenant, EmailIngestsId = 99131 };
            lead.LeadItems.Add(new LeadItem { LineItemNo = "1", ManufacturerPartNumber = "RACE-PART", Quantity = 2, UnitOfMeasure = "EA" });
            return await new LeadIdentityApplicationService(context).ReconcileAsync(lead,
                new LeadIntakeDescriptor(batch, "API", key, null, null, "test", lead.Clientemail, null, "race.json", "application/json", 100,
                    new string('a', 64), null, null, null, DateTimeOffset.UtcNow, LeadProcessingPath.Deterministic, false, 0, "Test", "tests", key));
        }
        var results = await Task.WhenAll(Ingest("race-one"), Ingest("race-two"));
        Assert.Single(results.Select(x => x.LeadId).Distinct());
        Assert.Contains(results, x => x.Classification == LeadOccurrenceClassification.New);
        Assert.Contains(results, x => x.Classification == LeadOccurrenceClassification.ExactDuplicate);
        await using var verify = _database.ContextFor(tenant);
        Assert.Equal(1, await verify.Leads.CountAsync(x => x.Rfqno == "RACE-RFQ"));
        Assert.Equal(2, await verify.Set<LeadIngestionOccurrence>().CountAsync(x => x.BatchId == batch));
    }
}
