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

    /// <summary>
    /// SQUASH NOTE — this replaces
    /// Populated_release01c_migration_rollback_and_reupgrade_preserve_identity_history_and_cost_status.
    ///
    /// That test built a database at 20260724230121_Release01OrderLineage, planted a legacy lead
    /// with items, a customer, a contact with no tenant, an extraction job with no occurrence and
    /// an extraction run with no cost status, then walked up through
    /// 20260725022734_Release01BIntakeIdentityAcceptance and
    /// 20260725041211_Release01CTenantContactMetadata, back down and up again, asserting at every
    /// step that the backfilled revision, occurrence, contact tenant, job link and cost statuses
    /// were correct and that the lead's commercial serial never moved.
    ///
    /// 20260811033109_SquashedSchemaBaseline erased all three ids. Those BACKFILLS are retired —
    /// the columns they filled are NOT NULL now, so a row missing them cannot be written — and
    /// that is exactly the guarantee asserted here instead. It is stronger than the backfill test
    /// was: the backfill proved a legacy row COULD be repaired, this proves an unrepaired row
    /// cannot be created in the first place.
    ///
    ///   * A contact belongs to exactly one parent and carries its tenant, with a TENANT-QUALIFIED
    ///     foreign key to the supplier — a contact cannot be attached across a tenant boundary.
    ///   * An extraction run must declare BOTH cost statuses; there is no default to fall through
    ///     to, so "we do not know what this cost" has to be said out loud, which is what
    ///     HistoricalUnpriced / HistoricalUnknown said for the legacy rows.
    ///   * A cost amount without a currency is refused outright.
    ///
    /// The lead-identity chain the same migrations built — revisions, occurrences and the stable
    /// commercial serial — is exercised end to end by the two tests below and by
    /// Concurrent_duplicate_ingestion_converges_on_one_lead_and_two_occurrences, through
    /// LeadIdentityApplicationService rather than through a migration.
    /// </summary>
    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task Contact_tenancy_and_extraction_cost_status_cannot_be_left_unstated()
    {
        await using var connection = await _database.OpenConnectionAsync();

        await using (var schema = connection.CreateCommand())
        {
            schema.CommandText = """
                SELECT
                    (SELECT is_nullable FROM information_schema.columns
                     WHERE table_schema = 'public' AND table_name = 'Contacts'
                       AND column_name = 'BusinessUnitID') = 'NO',
                    EXISTS (SELECT 1 FROM pg_constraint
                        WHERE conname = 'FK_Contacts_Suppliers_SupplierID_BusinessUnitID'
                          AND contype = 'f' AND array_length(conkey, 1) = 2),
                    EXISTS (SELECT 1 FROM pg_constraint
                        WHERE conname = 'CK_Contacts_ExactlyOneParent' AND convalidated),
                    (SELECT count(*)::int FROM information_schema.columns
                     WHERE table_schema = 'public' AND table_name = 'extraction_runs'
                       AND column_name IN ('processing_cost_status', 'ocr_cost_status')
                       AND is_nullable = 'NO' AND column_default IS NULL) = 2,
                    EXISTS (SELECT 1 FROM pg_constraint
                        WHERE conname = 'ck_extraction_runs_cost' AND convalidated);
                """;
            await using var reader = await schema.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            for (var index = 0; index < 5; index++)
                Assert.True(reader.GetBoolean(index), $"Release 01 identity assertion {index + 1} failed.");
        }

        // And ONE of them is shown to bite, rather than merely to exist. Only the tenant-qualified
        // contact foreign key is exercised by a write below; the extraction-run cost-status and
        // cost-evidence constraints above are asserted on the catalogue only.
        await using var transaction = await connection.BeginTransactionAsync();
        await using (var seed = connection.CreateCommand())
        {
            seed.Transaction = transaction;
            seed.CommandText = """
                INSERT INTO "BusinessUnits"
                    ("ID", "BusinessUnitCode", "BusinessUnitName", "CreatedBy", "CreatedOn")
                VALUES (99131, 'R01A-A', 'Release 01A tenant A', 'tests', now()),
                       (99132, 'R01A-B', 'Release 01A tenant B', 'tests', now());
                INSERT INTO "Suppliers"
                    ("ID", "Name", "ContactEmail", "ImageURL", "BUID", "IsActive", "CreatedBy", "CreatedOn")
                VALUES (99133, 'Release 01A supplier', 'supplier-99133@nexora.invalid', 'n/a', 99131,
                        true, 'tests', now());
                """;
            await seed.ExecuteNonQueryAsync();
        }

        // A contact for tenant A's supplier, filed under tenant B.
        await using (var crossTenant = connection.CreateCommand())
        {
            crossTenant.Transaction = transaction;
            crossTenant.CommandText = """
                INSERT INTO "Contacts"
                    ("ID", "BusinessUnitID", "SupplierID", "FirstName", "LastName",
                     "CreatedBy", "CreatedOn", "ConcurrencyToken")
                VALUES (99134, 99132, 99133, 'Cross', 'Tenant', 'tests', now(), gen_random_uuid());
                """;
            var error = await Assert.ThrowsAsync<PostgresException>(() => crossTenant.ExecuteNonQueryAsync());
            Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, error.SqlState);
        }

        await transaction.RollbackAsync();
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
    public async Task Concurrent_unnumbered_inquiries_from_one_customer_create_one_lead_and_one_governed_match_review()
    {
        const long tenant = 99136;
        await using (var seed = _database.ContextFor(tenant))
        {
            Seed.BusinessUnit(seed, tenant);
            Seed.EmailConfig(seed, 99136, tenant);
            Seed.EmailIngest(seed, 99136, 99136, "NeedsReview");
            await seed.SaveChangesAsync();
        }

        var batch = Guid.NewGuid();
        async Task<LeadReconciliationResult> Ingest(string key, int quantity)
        {
            await using var context = _database.ContextFor(tenant);
            var lead = new Lead
            {
                Rfqno = null,
                BuyersName = "Unnumbered Race Buyer",
                Clientemail = "unnumbered-race@customer.test",
                RecDate = DateTime.UtcNow,
                LeadSource = "Email",
                CreatedBy = "tests",
                CreatedDate = DateTime.UtcNow,
                BusinessUnitId = tenant,
                EmailIngestsId = 99136
            };
            lead.LeadItems.Add(new LeadItem
            {
                LineItemNo = "1",
                ManufacturerPartNumber = "RACE-NO-RFQ-PART",
                ProductShortDescription = "Quantity-only amendment under concurrent workers",
                Quantity = quantity,
                UnitOfMeasure = "EA"
            });
            return await new LeadIdentityApplicationService(context).ReconcileAsync(lead,
                new LeadIntakeDescriptor(batch, "Email", key, key, $"email:{key}", "test",
                    lead.Clientemail, "Unnumbered RFQ", $"{key}.csv", "text/csv", 100,
                    Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                        System.Text.Encoding.UTF8.GetBytes($"{key}:{quantity}"))).ToLowerInvariant(),
                    null, null, null, DateTimeOffset.UtcNow, LeadProcessingPath.Deterministic,
                    false, 0, "Service", "tests", key));
        }

        var results = await Task.WhenAll(
            Ingest("race-no-rfq-one", 2),
            Ingest("race-no-rfq-two", 9));

        Assert.Single(results.Where(x => x.LeadId > 0).Select(x => x.LeadId).Distinct());
        Assert.Contains(results, x => x.Classification == LeadOccurrenceClassification.New);
        Assert.Contains(results, x => x.Classification == LeadOccurrenceClassification.PossibleMatchReviewRequired);
        await using var verify = _database.ContextFor(tenant);
        Assert.Equal(1, await verify.Leads.CountAsync(x => x.BusinessUnitId == tenant));
        Assert.Equal(1, await verify.Set<LeadRevision>().CountAsync(x => x.BusinessUnitId == tenant));
        Assert.Equal(2, await verify.Set<LeadIngestionOccurrence>().CountAsync(x => x.BatchId == batch));
        Assert.Equal(1, await verify.Set<LeadMatchCandidate>().CountAsync(x => x.BusinessUnitId == tenant));
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
