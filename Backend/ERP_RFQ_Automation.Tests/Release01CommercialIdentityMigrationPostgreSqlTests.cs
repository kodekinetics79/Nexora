using ERP_RFQ_Automation.Tests.Support;
using Npgsql;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// SQUASH NOTE — both tests in this file used to drive migrations.
///
/// PopulatedUpgrade_RejectsUnsafeLegacyIdentity built a database at
/// 20260724004000_AuthoritativeEvidenceIngestion, planted one of three unsafe legacy shapes, and
/// asserted that 20260724223932_Release01CommercialIdentity (or, for the order case,
/// 20260724230121_Release01OrderLineage) REFUSED to apply, with a named SQLSTATE, rather than
/// upgrading a database whose commercial identity could not be trusted.
///
/// PopulatedUpgrade_BackfillsAndProtectsNexoraSerialLineage upgraded a safe database and asserted
/// the serial propagated from lead to RFQ to quote to order, then exercised the guards that keep it
/// there, the tenant-role RLS boundary, and the governed quote lifecycle.
///
/// 20260811033109_SquashedSchemaBaseline erased those ids. "The migration aborts rather than
/// corrupt" is migration-time behaviour and cannot survive a squash — but every unsafe shape it
/// aborted on is refused by the LIVE schema, and that is the guarantee that still protects a
/// running system. The serial BACKFILL is retired (an RFQ, quote or order without an inherited
/// serial cannot be written at all now, which the second test proves); none of its guards are.
///
/// WHAT CHANGED IN THE SCENARIO SET, precisely — an earlier revision of this file quietly lost a
/// scenario and mislabelled the rest, so the accounting is spelled out:
///   * cross_tenant and null_customer are the ORIGINAL two, asserted forward, same SQLSTATEs.
///   * order_from_quote_without_customer is the original THIRD scenario
///     (order_quote_null_customer). Its shape is unchanged — an order sourced from a quote that
///     has no resolved customer — but its SQLSTATE moved from 23514 to 23503, because at head the
///     refusal comes from the nexora_validate_order_commercial_identity TRIGGER rather than from a
///     CHECK the migration was in the middle of adding. The shape is what matters and the shape is
///     unchanged.
///   * order_claiming_a_quote_it_cannot_name is ADDED to keep a 23514 order refusal in the set:
///     CK_Orders_SourceIdentity refuses SourceType = 'LEGACY_QUOTE' with no QuoteID.
///   * cross_tenant_customer is RELOCATED, not new — it is the crossTenantLead assertion that used
///     to sit in the second test in this file. It is counted here so it is not counted twice.
///
/// Only one assertion from the original pair is deliberately NOT restored: the count of six seeded
/// QuoteStatus codes for the upgraded tenant. That was one-time per-tenant reference data written
/// by the migration into Setup_Master for the tenants that existed at that moment. It cannot run
/// again, the baseline's 11_reference_data.sql seeds only public."Module", and nothing else seeds
/// it — see the report accompanying this change, which raises that as a finding rather than
/// encoding it here as an assertion that would pass vacuously.
/// </summary>
[Collection(PostgreSqlIntegrationCollection.Name)]
public sealed class Release01CommercialIdentityMigrationPostgreSqlTests(PostgreSqlTestDatabase database)
{
    private const long TenantOne = 94_911;
    private const long TenantTwo = 94_912;

    [Theory]
    // An RFQ in one tenant reaching for a lead in another.
    [InlineData("cross_tenant", "23503")]
    // A lead naming a customer while its match status still says it has not resolved one.
    [InlineData("null_customer", "23514")]
    // A lead VERIFIED against a customer belonging to a different tenant.
    [InlineData("cross_tenant_customer", "23503")]
    // An order sourced from a quote that never resolved a customer — the original third scenario.
    [InlineData("order_from_quote_without_customer", "23503")]
    // An order that claims to come from a quote while naming no quote at all.
    [InlineData("order_claiming_a_quote_it_cannot_name", "23514")]
    [Trait("Category", "PostgreSQL")]
    public async Task Unsafe_commercial_identity_is_refused_by_the_database(
        string scenario, string expectedSqlState)
    {
        var needsOrderChain = scenario.StartsWith("order_", StringComparison.Ordinal);

        await using var connection = await database.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();

        await using (var seed = connection.CreateCommand())
        {
            seed.Transaction = transaction;
            seed.CommandText = $"""
                INSERT INTO "BusinessUnits"
                    ("ID", "BusinessUnitCode", "BusinessUnitName", "CreatedBy", "CreatedOn")
                VALUES ({TenantOne}, 'R1G1', 'Guard tenant one', 'tests', now()),
                       ({TenantTwo}, 'R1G2', 'Guard tenant two', 'tests', now());
                INSERT INTO "Customers"
                    ("ID", "Name", "ContactEmail", "ImageURL", "BUID", "CreatedBy", "CreatedOn", "ConcurrencyToken")
                VALUES ({TenantOne}, 'Guard customer', 'buyer@nexora.invalid', '', {TenantOne},
                        'tests', now(), gen_random_uuid());
                INSERT INTO "Leads"
                    ("ID", "RFQNo", "RecDate", "LeadSource", "CreatedBy", "CreatedDate",
                     "BusinessUnitID", "Clientemail")
                VALUES ({TenantTwo}, 'CROSS-LEAD', now(), 'MigrationTest', 'tests', now(),
                        {TenantTwo}, 'unknown@nexora.invalid');
                {GovernedLineageSql(TenantTwo, TenantTwo, "cross")}
                """;
            await seed.ExecuteNonQueryAsync();
        }

        if (needsOrderChain)
        {
            // A lead → RFQ → quote chain in tenant one that NEVER resolved a customer. Every link
            // inherits the case and serial faithfully; the customer is the thing that is missing,
            // which is exactly the legacy shape the migration refused to carry forward.
            await using var chain = connection.CreateCommand();
            chain.Transaction = transaction;
            chain.CommandText = $"""
                INSERT INTO "Leads"
                    ("ID", "RFQNo", "RecDate", "LeadSource", "CreatedBy", "CreatedDate",
                     "BusinessUnitID", "Clientemail")
                VALUES ({TenantOne}, 'ORDER-LEAD', now(), 'MigrationTest', 'tests', now(),
                        {TenantOne}, 'unknown@nexora.invalid');
                {GovernedLineageSql(TenantOne, TenantOne, "order")}
                INSERT INTO "RFQ"
                    ("ID", "RFQNo", "RecDate", "BusinessUnitID", "LeadID", "CommercialCaseID",
                     "NexoraSerial", "PromotionId", "SourceLeadRevisionId", "ParticipationDecisionId",
                     "CreatedBy", "CreatedDate")
                SELECT {TenantOne}, 'ORDER-RFQ', now(), {TenantOne}, {TenantOne},
                       lead."CommercialCaseId", lead."CommercialCaseReference",
                       {TenantOne + 4_000_000}, {TenantOne + 1_000_000}, {TenantOne + 3_000_000},
                       'tests', now()
                FROM "Leads" lead WHERE lead."ID" = {TenantOne};
                INSERT INTO "Quotes"
                    ("ID", "QuoteNo", "BusinessUnitID", "RFQID", "CommercialCaseID", "NexoraSerial",
                     "CreatedBy", "CreatedDate")
                SELECT {TenantOne}, 'ORDER-QUOTE', {TenantOne}, {TenantOne},
                       rfq."CommercialCaseID", rfq."NexoraSerial", 'tests', now()
                FROM "RFQ" rfq WHERE rfq."ID" = {TenantOne};
                INSERT INTO "Setup_Master"
                    ("SetupID", "SetupType", "SetupCode", "SetupValue", "BusinessUnitID", "IsActive",
                     "CreatedBy", "CreatedOn")
                VALUES (94920, 'QuoteStatus', 'DRAFT', 'Draft', {TenantOne}, true, 'tests', now());
                """;
            await chain.ExecuteNonQueryAsync();
        }

        var unsafeWrite = scenario switch
        {
            // Inherits the FOREIGN lead's case and serial faithfully, and is still refused: the
            // guard is about the tenant boundary, not about a mismatched string.
            "cross_tenant" => $"""
                INSERT INTO "RFQ"
                    ("ID", "RFQNo", "RecDate", "BusinessUnitID", "LeadID", "CommercialCaseID",
                     "NexoraSerial", "PromotionId", "SourceLeadRevisionId", "ParticipationDecisionId",
                     "CreatedBy", "CreatedDate")
                SELECT {TenantOne}, 'CROSS-RFQ', now(), {TenantOne}, {TenantTwo},
                       lead."CommercialCaseId", lead."CommercialCaseReference",
                       {TenantTwo + 4_000_000}, {TenantTwo + 1_000_000}, {TenantTwo + 3_000_000},
                       'tests', now()
                FROM "Leads" lead WHERE lead."ID" = {TenantTwo};
                """,
            "null_customer" => $"""
                INSERT INTO "Leads"
                    ("ID", "RFQNo", "RecDate", "LeadSource", "CreatedBy", "CreatedDate",
                     "BusinessUnitID", "Clientemail", "CustomerID")
                VALUES (94913, 'NULL-CUSTOMER-LEAD', now(), 'MigrationTest', 'tests', now(),
                        {TenantOne}, 'unknown@nexora.invalid', {TenantOne});
                """,
            "cross_tenant_customer" => $"""
                INSERT INTO "Leads"
                    ("ID", "RFQNo", "RecDate", "LeadSource", "CreatedBy", "CreatedDate",
                     "BusinessUnitID", "Clientemail", "CustomerID", "CustomerMatchStatus")
                VALUES (94914, 'CROSS-CUSTOMER-LEAD', now(), 'MigrationTest', 'tests', now(),
                        {TenantTwo}, 'unknown@nexora.invalid', {TenantOne}, 'VERIFIED');
                """,
            // Orders."CustomerID" is NOT NULL, so an order off a customer-less quote must invent
            // one. The trigger compares the order's identity tuple with its quote's and refuses.
            "order_from_quote_without_customer" => $"""
                INSERT INTO "Orders"
                    ("ID", "OrderNo", "QuoteID", "LeadID", "RFQID", "CustomerID", "BusinessUnitID",
                     "SourceType", "StatusID", "OrderDate", "TotalAmount", "PaidAmount",
                     "CreatedBy", "CreatedOn", "CommercialCaseID", "NexoraSerial")
                SELECT {TenantOne}, 'ORDER-QUOTE-NULL-CUSTOMER', {TenantOne}, {TenantOne}, {TenantOne},
                       {TenantOne}, {TenantOne}, 'LEGACY_QUOTE', 94920, now(), 100, 0, 'tests', now(),
                       quote."CommercialCaseID", quote."NexoraSerial"
                FROM "Quotes" quote WHERE quote."ID" = {TenantOne};
                """,
            "order_claiming_a_quote_it_cannot_name" => $"""
                INSERT INTO "Orders"
                    ("ID", "OrderNo", "QuoteID", "LeadID", "RFQID", "CustomerID", "BusinessUnitID",
                     "SourceType", "StatusID", "OrderDate", "TotalAmount", "PaidAmount",
                     "CreatedBy", "CreatedOn")
                VALUES (94915, 'ORDER-NO-QUOTE', NULL, {TenantOne}, {TenantOne}, {TenantOne},
                        {TenantOne}, 'LEGACY_QUOTE', 94920, now(), 100, 0, 'tests', now());
                """,
            _ => throw new ArgumentOutOfRangeException(nameof(scenario), scenario, null)
        };

        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = unsafeWrite;
            var failure = await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync());
            Assert.Equal(expectedSqlState, failure.SqlState);
        }

        await transaction.RollbackAsync();
    }

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task Commercial_serial_lineage_is_inherited_through_orders_and_then_immutable()
    {
        await using var connection = await database.OpenConnectionAsync();

        // The lifecycle ledger accepts the quote aggregate. This was the migration's own
        // widening of CK_lifecycle_events_AggregateType and is asserted on the deployed
        // constraint text, not on migration source.
        await using (var lifecycle = connection.CreateCommand())
        {
            lifecycle.CommandText = """
                SELECT pg_get_constraintdef(oid) FROM pg_constraint
                WHERE conname = 'CK_lifecycle_events_AggregateType';
                """;
            var definition = Assert.IsType<string>(await lifecycle.ExecuteScalarAsync());
            Assert.Contains("'Quote'", definition, StringComparison.Ordinal);
        }

        await using var transaction = await connection.BeginTransactionAsync();
        await using (var seed = connection.CreateCommand())
        {
            seed.Transaction = transaction;
            seed.CommandText = $"""
                INSERT INTO "BusinessUnits"
                    ("ID", "BusinessUnitCode", "BusinessUnitName", "CreatedBy", "CreatedOn")
                VALUES (94901, 'REL01', 'Release 01 identity', 'tests', now());
                INSERT INTO "Customers"
                    ("ID", "Name", "ContactEmail", "ImageURL", "BUID", "CreatedBy", "CreatedOn",
                     "ConcurrencyToken")
                VALUES (94901, 'Release 01 customer', 'buyer@nexora.invalid', '', 94901, 'tests',
                        now(), gen_random_uuid());
                INSERT INTO "Contacts"
                    ("ID", "BusinessUnitID", "CustomerID", "FirstName", "LastName",
                     "CreatedBy", "CreatedOn", "ConcurrencyToken")
                VALUES (94901, 94901, 94901, 'Release', 'Buyer', 'tests', now(), gen_random_uuid());
                INSERT INTO "Leads"
                    ("ID", "RFQNo", "RecDate", "LeadSource", "CreatedBy", "CreatedDate",
                     "BusinessUnitID", "Clientemail", "CustomerID", "ContactID", "CustomerMatchStatus")
                VALUES (94901, 'CUSTOMER-RFQ-94901', now(), 'MigrationTest', 'tests', now(), 94901,
                        'buyer@nexora.invalid', 94901, 94901, 'VERIFIED');
                {GovernedLineageSql(94901, 94901, "lineage")}
                INSERT INTO "RFQ"
                    ("ID", "RFQNo", "RecDate", "BusinessUnitID", "LeadID", "CommercialCaseID",
                     "NexoraSerial", "CustomerID", "ContactID", "PromotionId",
                     "SourceLeadRevisionId", "ParticipationDecisionId", "CreatedBy", "CreatedDate")
                SELECT 94901, 'NEXORA-RFQ-94901', now(), 94901, 94901,
                       lead."CommercialCaseId", lead."CommercialCaseReference",
                       lead."CustomerID", lead."ContactID", 4094901, 1094901, 3094901,
                       'tests', now()
                FROM "Leads" lead WHERE lead."ID" = 94901;
                INSERT INTO "Quotes"
                    ("ID", "QuoteNo", "BusinessUnitID", "RFQID", "CommercialCaseID", "NexoraSerial",
                     "CustomerID", "ContactID", "CreatedBy", "CreatedDate")
                SELECT 94901, 'QUOTE-94901', 94901, 94901, rfq."CommercialCaseID", rfq."NexoraSerial",
                       rfq."CustomerID", rfq."ContactID", 'tests', now()
                FROM "RFQ" rfq WHERE rfq."ID" = 94901;
                INSERT INTO "Setup_Master"
                    ("SetupID", "SetupType", "SetupCode", "SetupValue", "BusinessUnitID", "IsActive",
                     "CreatedBy", "CreatedOn")
                VALUES (94910, 'QuoteStatus', 'DRAFT', 'Draft', 94901, true, 'tests', now());
                INSERT INTO "Orders"
                    ("ID", "OrderNo", "QuoteID", "LeadID", "RFQID", "CustomerID", "ContactID",
                     "BusinessUnitID", "SourceType", "StatusID", "OrderDate", "TotalAmount",
                     "PaidAmount", "CreatedBy", "CreatedOn", "CommercialCaseID", "NexoraSerial")
                SELECT 94901, 'ORDER-94901', 94901, 94901, 94901, quote."CustomerID", quote."ContactID",
                       94901, 'LEGACY_QUOTE', 94910, now(), 100, 0, 'tests', now(),
                       quote."CommercialCaseID", quote."NexoraSerial"
                FROM "Quotes" quote WHERE quote."ID" = 94901;
                """;
            await seed.ExecuteNonQueryAsync();
        }

        // The serial the case allocated to the lead is the one the RFQ, the quote AND the order
        // carry — all four read together, so a guard that merely accepted anything non-null would
        // not pass. The order leg is the one the original test ended on and is the reason
        // Release01OrderLineage existed.
        await using (var lineage = connection.CreateCommand())
        {
            lineage.Transaction = transaction;
            lineage.CommandText = """
                SELECT lead."CommercialCaseReference", rfq."NexoraSerial", quote."NexoraSerial",
                       "order"."NexoraSerial"
                FROM "Leads" lead
                JOIN "RFQ" rfq ON rfq."ID" = 94901
                JOIN "Quotes" quote ON quote."ID" = 94901
                JOIN "Orders" "order" ON "order"."ID" = 94901
                WHERE lead."ID" = 94901;
                """;
            await using var reader = await lineage.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            var serial = reader.GetString(0);
            Assert.NotEmpty(serial);
            Assert.Equal(serial, reader.GetString(1));
            Assert.Equal(serial, reader.GetString(2));
            Assert.Equal(serial, reader.GetString(3));
        }

        // Once assigned it cannot be rewritten — on the quote…
        await AssertRejectedAsync(connection, transaction,
            """UPDATE "Quotes" SET "NexoraSerial" = 'FORGED' WHERE "ID" = 94901;""");

        // …nor on the ORDER. nexora_validate_order_commercial_identity is a separate trigger from
        // the one guarding RFQ and Quotes, and this is its only negative test: the whole point of a
        // database trigger over a C# guard is that it holds against raw SQL like this.
        await AssertRejectedAsync(connection, transaction,
            """UPDATE "Orders" SET "NexoraSerial" = 'FORGED' WHERE "ID" = 94901;""");

        // The order's customer is equally sealed once assigned.
        await AssertRejectedAsync(connection, transaction,
            """UPDATE "Orders" SET "CustomerID" = 94902 WHERE "ID" = 94901;""");

        // And the quote's status cannot be moved outside the governed lifecycle command, which is
        // what keeps the lifecycle ledger above a complete record rather than a partial one.
        await AssertRejectedAsync(connection, transaction,
            """UPDATE "Quotes" SET "StatusID" = 1 WHERE "ID" = 94901;""");

        await transaction.RollbackAsync();
    }

    private static string GovernedLineageSql(long tenantId, long leadId, string suffix)
    {
        var occurrenceId = leadId + 5_000_000;
        var revisionId = leadId + 1_000_000;
        var fitId = leadId + 2_000_000;
        var decisionId = leadId + 3_000_000;
        var promotionId = leadId + 4_000_000;
        var batchId = $"00000000-0000-0000-0000-{leadId:D12}";
        return $"""
            INSERT INTO "LeadIngestionBatches"
                ("Id", "BusinessUnitId", "SourceChannel", "CreatedBy", "CreatedAtUtc", "UpdatedAtUtc", "Version")
            VALUES ('{batchId}'::uuid, {tenantId}, 'MigrationTest', 'tests', now(), now(), 1);
            INSERT INTO "LeadIngestionOccurrences"
                ("Id", "RecordKind", "BusinessUnitId", "BatchId", "LeadId", "SourceChannel",
                 "IdempotencyKey", "LogicalInquiryFingerprint", "Classification", "Confidence",
                 "DecisionReasonsJson", "PolicyVersion", "ProcessingPath", "ExternalAiUsed",
                 "IngestedAtUtc", "CreatedAtUtc", "ActorType", "ActorId", "CorrelationId", "Version")
            VALUES ({occurrenceId}, 'Inquiry', {tenantId}, '{batchId}'::uuid, {leadId}, 'MigrationTest',
                    'release01-occurrence-{suffix}', repeat('a', 64), 'New', 1,
                    '[]'::jsonb, 'release01-fixture/v1', 'Deterministic', false,
                    now(), now(), 'TestFixture', 'tests', 'release01-{suffix}', 1);
            INSERT INTO "LeadRevisions"
                ("Id", "BusinessUnitId", "LeadId", "RevisionNumber", "EstablishedByOccurrenceId",
                 "LogicalInquiryFingerprint", "SnapshotJson", "CreatedAtUtc", "CreatedBy",
                 "ProcessingPath", "ExternalAiUsed")
            VALUES ({revisionId}, {tenantId}, {leadId}, 1, {occurrenceId}, repeat('b', 64),
                    jsonb_build_object(), now(), 'tests', 'Deterministic', false);
            UPDATE "Leads" SET "CurrentRevisionId" = {revisionId}, "CurrentRevisionNumber" = 1
            WHERE "BusinessUnitID" = {tenantId} AND "ID" = {leadId};
            INSERT INTO "LeadFitAssessments"
                ("Id", "BusinessUnitId", "LeadId", "LeadRevisionId", "Sequence", "PolicyVersion",
                 "Recommendation", "IsActionable", "AssessmentJson", "IdempotencyKey", "RequestHash",
                 "AssessedBy", "AssessedAtUtc")
            VALUES ({fitId}, {tenantId}, {leadId}, {revisionId}, 1, 'release01-fixture/v1',
                    'FIT', true, jsonb_build_object(), 'release01-fit-{suffix}', repeat('c', 64), 'tests', now());
            INSERT INTO "LeadParticipationDecisions"
                ("Id", "BusinessUnitId", "LeadId", "LeadRevisionId", "FitAssessmentId", "Sequence",
                 "IsCommitted", "Outcome", "Notes", "IdempotencyKey", "RequestHash", "DecidedBy", "DecidedAtUtc")
            VALUES ({decisionId}, {tenantId}, {leadId}, {revisionId}, {fitId}, 1, true, 'FullBid',
                    'Governed relational fixture.', 'release01-decision-{suffix}', repeat('d', 64), 'tests', now());
            INSERT INTO "RfqPromotions"
                ("Id", "BusinessUnitId", "LeadId", "LeadRevisionId", "ParticipationDecisionId",
                 "IdempotencyKey", "RequestHash", "PromotedBy", "PromotedAtUtc")
            VALUES ({promotionId}, {tenantId}, {leadId}, {revisionId}, {decisionId},
                    'release01-promotion-{suffix}', repeat('e', 64), 'tests', now());
            """;
    }

    /// <summary>
    /// The tenant execution role's boundary on the customer master.
    ///
    /// RESTORED. An earlier revision of this file dropped this block entirely when the migration
    /// walk around it was removed. It is the only test in the suite that reads "Customers" and
    /// "Contacts" under nexora_tenant_app, and a cross-tenant write refusal on the customer master
    /// is the single guarantee the whole tenant-isolation design exists to provide — so losing it
    /// silently was the worst of the removals. It never needed a migration: it is a property of the
    /// deployed policies, and it is asserted here directly against them.
    ///
    /// The visibility counts are scoped to the two tenants this test creates because the shared
    /// fixture database carries rows from every other test in the collection. The scoping does not
    /// weaken the assertion: tenant two's customer and contact are inside the scope, and the point
    /// is precisely that they are not counted.
    /// </summary>
    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task Tenant_role_sees_only_its_own_customer_master_and_cannot_write_across_the_boundary()
    {
        const long tenantA = 94_931;
        const long tenantB = 94_932;

        await using var connection = await database.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();

        await using (var seed = connection.CreateCommand())
        {
            seed.Transaction = transaction;
            seed.CommandText = $"""
                INSERT INTO "BusinessUnits"
                    ("ID", "BusinessUnitCode", "BusinessUnitName", "CreatedBy", "CreatedOn")
                VALUES ({tenantA}, 'R1RLS-A', 'Release 01 RLS A', 'tests', now()),
                       ({tenantB}, 'R1RLS-B', 'Release 01 RLS B', 'tests', now());
                INSERT INTO "Customers"
                    ("ID", "Name", "ContactEmail", "ImageURL", "BUID", "CreatedBy", "CreatedOn",
                     "ConcurrencyToken")
                VALUES ({tenantA}, 'Tenant A customer', 'a-94931@nexora.invalid', '', {tenantA},
                        'tests', now(), gen_random_uuid()),
                       ({tenantB}, 'Tenant B customer', 'b-94932@nexora.invalid', '', {tenantB},
                        'tests', now(), gen_random_uuid());
                INSERT INTO "Contacts"
                    ("ID", "BusinessUnitID", "CustomerID", "FirstName", "LastName",
                     "CreatedBy", "CreatedOn", "ConcurrencyToken")
                VALUES ({tenantA}, {tenantA}, {tenantA}, 'A', 'Buyer', 'tests', now(), gen_random_uuid()),
                       ({tenantB}, {tenantB}, {tenantB}, 'B', 'Buyer', 'tests', now(), gen_random_uuid());
                """;
            await seed.ExecuteNonQueryAsync();
        }

        await using (var scoped = connection.CreateCommand())
        {
            scoped.Transaction = transaction;
            scoped.CommandText = $"""
                SET LOCAL ROLE nexora_tenant_app;
                SET LOCAL nexora.business_unit_id = '{tenantA}';
                SELECT (SELECT count(*) FROM "Customers" WHERE "BUID" IN ({tenantA}, {tenantB})),
                       (SELECT count(*) FROM "Contacts" WHERE "BusinessUnitID" IN ({tenantA}, {tenantB}));
                """;
            await using var reader = await scoped.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.Equal(1L, reader.GetInt64(0));
            Assert.Equal(1L, reader.GetInt64(1));
        }

        // Still inside the tenant-A session: writing a customer into tenant B is refused by the
        // policy's WITH CHECK, not by anything the application chose to do.
        await using (var forged = connection.CreateCommand())
        {
            forged.Transaction = transaction;
            forged.CommandText = $"""
                INSERT INTO "Customers"
                    ("ID", "Name", "ContactEmail", "ImageURL", "BUID", "CreatedBy", "CreatedOn",
                     "ConcurrencyToken")
                VALUES (94933, 'Forged customer', 'forged-94933@nexora.invalid', '', {tenantB},
                        'tests', now(), gen_random_uuid());
                """;
            var denied = await Assert.ThrowsAsync<PostgresException>(() => forged.ExecuteNonQueryAsync());
            Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, denied.SqlState);
        }

        await transaction.RollbackAsync();
    }

    /// <summary>55000 — object_not_in_prerequisite_state, raised by both identity guards.</summary>
    private static async Task AssertRejectedAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, string sql)
    {
        await transaction.SaveAsync("guard");
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        var error = await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync());
        Assert.Equal(PostgresErrorCodes.ObjectNotInPrerequisiteState, error.SqlState);
        await transaction.RollbackAsync("guard");
    }
}
