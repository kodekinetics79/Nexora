using ERP_RFQ_Automation.LeadIdentity;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;

namespace ERP_RFQ_Automation.Tests;

[Collection(PostgreSqlIntegrationCollection.Name)]
public sealed class Module04ProductInventoryMigrationPostgreSqlTests(PostgreSqlTestDatabase database)
{
    /// <summary>
    /// SQUASH NOTE — this replaces
    /// Populated_upgrade_backfills_shortage_and_lineage_then_downgrades_and_reupgrades.
    ///
    /// That test built a database at 20260730234426_Module03TenantSafeSalesRouting, wrote a
    /// commercial line resolution with no ProjectedShortage and no RfqItemId, upgraded to
    /// 20260731014905_Module04ProductInventoryAuthority and asserted the backfill computed the
    /// shortage (20 requested - 10 on hand - 3 incoming = 7) and recovered the RFQ line, then
    /// walked back down and up again to prove neither direction lost the row.
    ///
    /// 20260811033109_SquashedSchemaBaseline erased both ids and the walk with them. The BACKFILL
    /// is retired: ProjectedShortage is now NOT NULL with a store default and RfqItemId is written
    /// at resolution time, so a row missing either cannot be created. Its ARITHMETIC is not lost
    /// coverage here — it is the resolution engine's rule and is tested where the engine is.
    ///
    /// What the migration installed, and what is asserted here against the live schema and real
    /// writes, is the authority model around that row:
    ///
    ///   * the append-only guard is present and is ENABLE ORIGIN, and it actually refuses a
    ///     rewrite of a resolution already recorded;
    ///   * a resolution cannot cite another tenant's product — refused by a trigger with a named
    ///     reason, and backed by a tenant-qualified foreign key underneath it;
    ///   * ProjectedShortage cannot go negative, which is what makes the stored number a shortage
    ///     rather than an arbitrary difference.
    /// </summary>
    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task Commercial_line_resolutions_are_append_only_tenant_bound_and_non_negative()
    {
        await using var context = database.ContextFor(null);
        await using var transaction = await context.Database.BeginTransactionAsync();

        Seed.EnsureBusinessUnit(context, 98_400);
        Seed.EnsureBusinessUnit(context, 98_499);
        var lead = Seed.Lead(context, 98_401, 98_400, buyersName: "Module 04 authority");
        await context.SaveChangesAsync();

        await context.Database.ExecuteSqlRawAsync("""
            INSERT INTO public."Products"
                ("ID", "BUID", "PartNo", "ProductName", "IsActive", "QtyOnHand", "ReorderPoint", "CreatedBy", "CreatedOn")
            VALUES (98410, 98400, 'M04-PART', 'Authority part', true, 0, 0, 'tests', now()),
                   (98419, 98499, 'OTHER-TENANT', 'Other tenant part', true, 0, 0, 'tests', now());
            """);

        // A revision cannot exist without the occurrence that established it —
        // FK_LeadRevisions_LeadIngestionOccurrences_BusinessUnitId_Estab~ is NOT NULL and
        // tenant-qualified — so the batch and occurrence have to be real rows, not stand-ins.
        var occurrence = Occurrence(98_400, "module04-authority-occurrence", 'a');
        context.Add(occurrence);
        await context.SaveChangesAsync();

        var revision = new LeadRevision
        {
            BusinessUnitId = 98_400, Lead = lead, RevisionNumber = 1,
            EstablishedByOccurrenceId = occurrence.Id,
            LogicalInquiryFingerprint = new string('b', 64),
            SnapshotJson = "{}", CreatedAtUtc = DateTimeOffset.UtcNow, CreatedBy = "tests",
            ProcessingPath = LeadProcessingPath.Deterministic,
        };
        var line = new LeadItemRevision
        {
            BusinessUnitId = 98_400, LineNumber = 1, LineFingerprint = new string('c', 64),
            SnapshotJson = "{\"part\":\"M04-PART\",\"quantity\":20}",
        };
        revision.Items.Add(line);
        var rfq = new Rfq
        {
            Id = 98_420, BusinessUnitId = 98_400, Lead = lead, Rfqno = "M04-RFQ",
            RecDate = DateTime.UtcNow, CreatedBy = "tests", CreatedDate = DateTime.UtcNow,
        };
        rfq.InheritCommercialIdentity(lead);
        context.AddRange(revision, rfq);
        await context.SaveChangesAsync();

        await context.Database.ExecuteSqlRawAsync("""
            INSERT INTO public."RFQItems"
                ("ID", "RFQID", "LineItemNo", "ProductID", "ManufacturerPartNumber",
                 "Quantity", "CreatedBy", "CreatedDate")
            VALUES (98421, 98420, '1', 98410, 'M04-PART', 20, 'tests', now())
            """);

        await context.Database.ExecuteSqlAsync($"""
            INSERT INTO public.lead_line_commercial_resolutions
                ("BusinessUnitId", "LeadId", "LeadRevisionId", "LeadLineId", "RfqId", "RfqItemId", "ProductId",
                 "ResolutionBatchId", "ResourceLimit", "RequestedPartNumber", "RequestedQuantity",
                 "Classification", "AvailableToPromise", "IncomingAvailable", "ProjectedShortage",
                 "FulfilmentJson", "RelatedResourcesJson", "ProductResolutionJson", "ResolutionMethod",
                 "EvidenceReference", "InventoryAsOfUtc", "ResolvedOn")
            VALUES
                (98400, {lead.Id}, {revision.Id}, {line.Id}, 98420, 98421, 98410,
                 '04040404-0000-0000-0000-000000000001', 10, 'M04-PART', 20,
                 'KnownShortage', 10, 3, 7,
                 jsonb_build_object(), '[]'::jsonb, jsonb_build_object(), 'AuthorityTest',
                 'module04:authority', now(), now())
            """);

        // The guard is ENABLE ORIGIN ('O'), not ENABLE ALWAYS ('A'). That is deliberate and worth
        // pinning: a bulk repair run under session_replication_role = 'replica' is the one context
        // in which the platform is permitted to correct these rows.
        Assert.Equal("O", await context.Database.SqlQueryRaw<string>("""
            SELECT tgenabled::text AS "Value"
            FROM pg_trigger
            WHERE tgrelid = 'public.lead_line_commercial_resolutions'::regclass
              AND tgname = 'commercial_line_resolution_update_guard'
            """).SingleAsync());

        var immutableUpdate = await Assert.ThrowsAsync<PostgresException>(() =>
            context.Database.ExecuteSqlRawAsync("""
                UPDATE public.lead_line_commercial_resolutions
                SET "ProjectedShortage" = 6 WHERE "BusinessUnitId" = 98400
                """));
        Assert.Equal("P0001", immutableUpdate.SqlState);

        await transaction.RollbackAsync();
    }

    /// <summary>
    /// The cross-tenant refusal, in its own transaction because the rewrite refusal above aborts
    /// the one it runs in.
    /// </summary>
    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task A_resolution_cannot_cite_another_tenants_product()
    {
        await using var context = database.ContextFor(null);
        await using var transaction = await context.Database.BeginTransactionAsync();

        Seed.EnsureBusinessUnit(context, 98_450);
        Seed.EnsureBusinessUnit(context, 98_451);
        var lead = Seed.Lead(context, 98_452, 98_450, buyersName: "Module 04 cross tenant");
        await context.SaveChangesAsync();

        await context.Database.ExecuteSqlRawAsync("""
            INSERT INTO public."Products"
                ("ID", "BUID", "PartNo", "ProductName", "IsActive", "QtyOnHand", "ReorderPoint", "CreatedBy", "CreatedOn")
            VALUES (98453, 98451, 'FOREIGN-PART', 'Other tenant part', true, 0, 0, 'tests', now());
            """);

        var occurrence = Occurrence(98_450, "module04-cross-tenant-occurrence", 'd');
        context.Add(occurrence);
        await context.SaveChangesAsync();

        var revision = new LeadRevision
        {
            BusinessUnitId = 98_450, Lead = lead, RevisionNumber = 1,
            EstablishedByOccurrenceId = occurrence.Id,
            LogicalInquiryFingerprint = new string('d', 64),
            SnapshotJson = "{}", CreatedAtUtc = DateTimeOffset.UtcNow, CreatedBy = "tests",
            ProcessingPath = LeadProcessingPath.Deterministic,
        };
        var line = new LeadItemRevision
        {
            BusinessUnitId = 98_450, LineNumber = 1, LineFingerprint = new string('e', 64),
            SnapshotJson = "{}",
        };
        revision.Items.Add(line);
        context.Add(revision);
        await context.SaveChangesAsync();

        var crossTenantProduct = await Assert.ThrowsAsync<PostgresException>(() =>
            context.Database.ExecuteSqlAsync($"""
                INSERT INTO public.lead_line_commercial_resolutions
                    ("BusinessUnitId", "LeadId", "LeadRevisionId", "LeadLineId", "ProductId",
                     "ResolutionBatchId", "ResourceLimit", "RequestedPartNumber", "RequestedQuantity",
                     "Classification", "AvailableToPromise", "IncomingAvailable", "ProjectedShortage",
                     "FulfilmentJson", "RelatedResourcesJson", "ProductResolutionJson", "ResolutionMethod",
                     "EvidenceReference", "InventoryAsOfUtc", "ResolvedOn")
                VALUES
                    (98450, {lead.Id}, {revision.Id}, {line.Id}, 98453,
                     '04040404-0000-0000-0000-000000000002', 10, 'FOREIGN-PART', 1,
                     'KnownShortage', 0, 0, 1,
                     jsonb_build_object(), '[]'::jsonb, jsonb_build_object(), 'AuthorityTest',
                     'module04:cross-tenant', now(), now())
                """));
        Assert.Equal("P0001", crossTenantProduct.SqlState);
        Assert.Contains("product must belong to the same tenant", crossTenantProduct.MessageText);

        await transaction.RollbackAsync();
    }

    /// <summary>
    /// The trigger above is the first line; the tenant-qualified foreign key and the non-negative
    /// CHECK are what hold if the trigger is ever disabled — asserted on the catalogue because a
    /// disabled trigger is exactly the state in which they matter.
    /// </summary>
    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task Resolution_lineage_is_backed_by_constraints_not_only_by_a_trigger()
    {
        await using var context = database.ContextFor(null);

        Assert.True(await context.Database.SqlQueryRaw<bool>("""
            SELECT EXISTS (
                SELECT 1 FROM pg_constraint
                WHERE conrelid = 'public.lead_line_commercial_resolutions'::regclass
                  AND contype = 'f'
                  AND conname = 'FK_lead_line_commercial_resolutions_Products_BusinessUnitId_Pr~'
                  AND array_length(conkey, 1) = 2
            ) AS "Value"
            """).SingleAsync());

        Assert.True(await context.Database.SqlQueryRaw<bool>("""
            SELECT EXISTS (
                SELECT 1 FROM pg_constraint
                WHERE conrelid = 'public.lead_line_commercial_resolutions'::regclass
                  AND conname = 'CK_commercial_resolution_quantities'
                  AND convalidated
                  AND position('"ProjectedShortage" >= (0)' in pg_get_constraintdef(oid)) > 0
            ) AS "Value"
            """).SingleAsync());
    }

    /// <summary>
    /// The ingestion occurrence a LeadRevision has to be established by, with its batch. Written
    /// through the model rather than raw SQL because this file now runs against head, where the
    /// model and the schema agree.
    /// </summary>
    private static LeadIngestionOccurrence Occurrence(long tenantId, string idempotencyKey, char fingerprint)
    {
        var now = DateTimeOffset.UtcNow;
        return new LeadIngestionOccurrence
        {
            BusinessUnitId = tenantId,
            Batch = new LeadIngestionBatch
            {
                Id = Guid.NewGuid(), BusinessUnitId = tenantId, SourceChannel = "Test",
                CreatedBy = "tests", CreatedAtUtc = now, UpdatedAtUtc = now, Version = 1
            },
            SourceChannel = "Test",
            IdempotencyKey = idempotencyKey,
            LogicalInquiryFingerprint = new string(fingerprint, 64),
            Classification = LeadOccurrenceClassification.New,
            Confidence = 1m,
            DecisionReasonsJson = "[]",
            PolicyVersion = "release-01a/v1",
            ProcessingPath = LeadProcessingPath.Deterministic,
            ExternalAiUsed = false,
            IngestedAtUtc = now,
            CreatedAtUtc = now,
            ActorType = "Service",
            ActorId = "tests",
            CorrelationId = "module04-authority",
            Version = 1
        };
    }
}
