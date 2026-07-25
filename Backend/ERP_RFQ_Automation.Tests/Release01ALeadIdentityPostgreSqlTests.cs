using ERP_RFQ_Automation.LeadIdentity;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Services;
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
    public async Task Populated_release01c_migration_rollback_and_reupgrade_preserve_identity_history_and_cost_status()
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
            const string release01b = "20260725022734_Release01BIntakeIdentityAcceptance";
            const string release01c = "20260725041211_Release01CTenantContactMetadata";
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
                INSERT INTO "Customers" ("ID","Name","ContactEmail","ImageURL","BUID","CreatedBy","CreatedOn")
                VALUES (99121,'Release 01B Customer','buyer-99121@nexora.invalid','',99121,'tests',now());
                INSERT INTO "Contacts" ("ID","CustomerID","FirstName","LastName","Email","CreatedBy","CreatedOn")
                VALUES (99121,99121,'Release','Contact','contact-99121@nexora.invalid','tests',now());
                INSERT INTO "ExtractionJobs"
                  ("Id","BatchId","BusinessUnitId","SourceType","ContentHash","StoragePath","FileName","FileType","Status","Priority","SchedulerTag","Attempts","MaxAttempts","NextAttemptAt","CreatedOn","UpdatedOn")
                VALUES (99121,'99121000-0000-0000-0000-000000000001',99121,'ManualUpload',repeat('c',64),'evidence://99121','rfq.csv','csv','Succeeded',0,0,1,5,now(),now(),now());
                INSERT INTO document_corpora (id,business_unit_id,batch_id,source_type,status,created_on,updated_on)
                VALUES (99121,99121,'99121000-0000-0000-0000-000000000001','ManualUpload','Completed',now(),now());
                INSERT INTO source_documents
                  (id,business_unit_id,corpus_id,extraction_job_id,content_hash,original_file_name,detected_mime_type,object_bucket,object_key,object_version,byte_size,page_count,security_status,processing_status,created_on,updated_on)
                VALUES (99121,99121,99121,99121,repeat('c',64),'rfq.csv','text/csv','acceptance','99121','v1',10,1,'Cleared','Completed',now(),now());
                INSERT INTO source_document_occurrences
                  (id,business_unit_id,source_document_id,corpus_id,extraction_job_id,idempotency_key,source_metadata,received_on)
                VALUES (99121,99121,99121,99121,99121,'release-01b-migration','{{}}'::jsonb,now());
                """);
            var serial = await db.Database.SqlQueryRaw<string>("SELECT \"CommercialCaseReference\" AS \"Value\" FROM \"Leads\" WHERE \"ID\"=99121").SingleAsync();

            await migrator.MigrateAsync(release01b);
            await AssertBackfill(db, serial);
            await db.Database.ExecuteSqlRawAsync("""
                INSERT INTO extraction_runs
                  (business_unit_id,source_document_id,run_id,extraction_job_id,attempt_number,parser_version,schema_version,status,
                   started_on,completed_on,page_count,region_count,inquiry_count,line_item_count,evidence_count,finding_count,created_on,updated_on)
                VALUES (99121,99121,'99121000-0000-0000-0000-000000000002',99121,1,'historical/v1','historical/v1','Completed',
                        now(),now(),1,0,1,1,0,0,now(),now())
                ON CONFLICT DO NOTHING;
                """);
            await migrator.MigrateAsync(release01c);
            await AssertRelease01C(db);
            await migrator.MigrateAsync(release01b);
            Assert.Equal(serial, await db.Database.SqlQueryRaw<string>("SELECT \"CommercialCaseReference\" AS \"Value\" FROM \"Leads\" WHERE \"ID\"=99121").SingleAsync());
            await migrator.MigrateAsync(release01c);
            await AssertBackfill(db, serial);
            await AssertRelease01C(db);
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
        Assert.Equal(99121, await db.Database.SqlQueryRaw<long>("SELECT \"BusinessUnitID\" AS \"Value\" FROM \"Contacts\" WHERE \"ID\"=99121").SingleAsync());
        Assert.Equal(99121, await db.Database.SqlQueryRaw<long>("SELECT \"SourceDocumentOccurrenceId\" AS \"Value\" FROM \"ExtractionJobs\" WHERE \"Id\"=99121").SingleAsync());
        Assert.Equal("Resolved", await db.Database.SqlQueryRaw<string>("SELECT intake_status AS \"Value\" FROM source_document_occurrences WHERE id=99121").SingleAsync());
    }

    private static async Task AssertRelease01C(DbContext db)
    {
        Assert.Equal("HistoricalUnpriced", await db.Database.SqlQueryRaw<string>(
            "SELECT processing_cost_status AS \"Value\" FROM extraction_runs WHERE extraction_job_id=99121").SingleAsync());
        Assert.Equal("HistoricalUnknown", await db.Database.SqlQueryRaw<string>(
            "SELECT ocr_cost_status AS \"Value\" FROM extraction_runs WHERE extraction_job_id=99121").SingleAsync());
        Assert.True(await db.Database.SqlQueryRaw<bool>("""
            SELECT EXISTS (
                SELECT 1 FROM pg_constraint
                WHERE conname = 'FK_Contacts_Suppliers_SupplierID_BusinessUnitID') AS "Value"
            """).SingleAsync());
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

    [Fact]
    public async Task Concurrent_quote_impact_resolution_appends_one_event_per_impact_and_is_idempotent()
    {
        const long tenant = 99141;
        long quoteId;
        await using (var seed = _database.ContextFor(tenant))
        {
            Seed.BusinessUnit(seed, tenant); Seed.EmailConfig(seed, 99141, tenant); Seed.EmailIngest(seed, 99141, 99141, "NeedsReview");
            await seed.SaveChangesAsync();
            var lead = new Lead
            {
                Rfqno = "IMPACT-RFQ", Clientemail = "impact@customer.test", RecDate = DateTime.UtcNow,
                LeadSource = "Test", CreatedBy = "tests", CreatedDate = DateTime.UtcNow,
                BusinessUnitId = tenant, EmailIngestsId = 99141
            };
            lead.LeadItems.Add(new LeadItem { LineItemNo = "1", ManufacturerPartNumber = "IMPACT-PART", Quantity = 2, UnitOfMeasure = "EA" });
            var reconciliation = await new LeadIdentityApplicationService(seed).ReconcileAsync(lead,
                new LeadIntakeDescriptor(Guid.NewGuid(), "API", "impact-seed", null, null, "test", lead.Clientemail, null,
                    "impact.json", "application/json", 100, new string('d', 64), null, null, null, DateTimeOffset.UtcNow,
                    LeadProcessingPath.Deterministic, false, 0, "Test", "tests", "impact-seed"));
            var revisionId = await seed.Set<LeadRevision>().Where(x => x.LeadId == reconciliation.LeadId).Select(x => x.Id).SingleAsync();
            var quote = new Quote
            {
                QuoteNo = "QT-IMPACT-99141", BusinessUnitId = tenant,
                CreatedBy = "tests", CreatedDate = DateTime.UtcNow
            };
            seed.Quotes.Add(quote);
            await seed.SaveChangesAsync();
            quoteId = quote.Id;
            seed.AddRange(
                new LeadRevisionImpact { BusinessUnitId = tenant, LeadId = reconciliation.LeadId, LeadRevisionId = revisionId, AggregateType = "QUOTE", AggregateId = quoteId, ImpactType = "STALE_A", Status = "OPEN", DetailsJson = "{}" },
                new LeadRevisionImpact { BusinessUnitId = tenant, LeadId = reconciliation.LeadId, LeadRevisionId = revisionId, AggregateType = "QUOTE", AggregateId = quoteId, ImpactType = "STALE_B", Status = "OPEN", DetailsJson = "{}" });
            await seed.SaveChangesAsync();
        }

        async Task Resolve(string key)
        {
            await using var context = _database.ContextFor(tenant);
            await new QuoteService(context, null!, null!).ResolveRevisionImpactAsync(quoteId, tenant, "reviewer", key);
        }

        await Task.WhenAll(Resolve("impact-resolution-a"), Resolve("impact-resolution-b"));
        await Resolve("impact-resolution-a");

        await using var verify = _database.ContextFor(tenant);
        var events = await verify.Set<LeadIdentityAuditEvent>()
            .Where(x => x.EventType == "REVISION_IMPACT_RESOLVED" && x.CorrelationId.StartsWith("quote-impact:"))
            .ToListAsync();
        Assert.Equal(2, events.Count);
        Assert.Equal(2, events.Select(x => x.CorrelationId).Distinct().Count());
    }
}
