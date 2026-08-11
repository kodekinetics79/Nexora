using ERP_RFQ_Automation.Agent.Models;
using ERP_RFQ_Automation.Procurement;
using ERP_RFQ_Automation.SupplierQuotes;
using ERP_RFQ_Automation.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace ERP_RFQ_Automation.Tests;

[Collection(PostgreSqlIntegrationCollection.Name)]
public sealed class V2Gate04SupplierNegotiationPostgreSqlTests(PostgreSqlTestDatabase database)
{
    private const long TenantA = 98_401;
    private const long TenantB = 98_402;
    private const long OffsetA = 200_000;
    private const long OffsetB = 210_000;

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task Negotiation_schema_is_rls_forced_append_only_and_least_privilege()
    {
        await using var connection = await database.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        // Squash note: dropped the leading id check for
        // '20260729120629_V2Gate04SupplierNegotiationIntelligence'.
        // 20260811033109_SquashedSchemaBaseline erased that id. Everything the migration was
        // being held to — forced RLS, the policy predicate, the grant shape, the append-only and
        // truncate-rejecting triggers, the seeded Module row, the jsonb column type, the
        // tenant-qualified foreign key and the permission-parity rule — is asserted below.
        command.CommandText = """
            SELECT
                (SELECT relrowsecurity AND relforcerowsecurity FROM pg_class
                 WHERE oid = 'public.supplier_negotiation_decisions'::regclass),
                EXISTS (SELECT 1 FROM pg_policies
                    WHERE schemaname = 'public' AND tablename = 'supplier_negotiation_decisions'
                      AND policyname = 'nexora_tenant_isolation'
                      AND position('nexora.business_unit_id' in qual) > 0
                      AND position('nexora.business_unit_id' in with_check) > 0),
                has_table_privilege('nexora_tenant_app', 'public.supplier_negotiation_decisions', 'SELECT,INSERT')
                    AND NOT has_table_privilege('nexora_tenant_app',
                        'public.supplier_negotiation_decisions', 'UPDATE,DELETE,TRUNCATE'),
                (SELECT count(*) FROM pg_trigger WHERE NOT tgisinternal
                    AND tgrelid = 'public.supplier_negotiation_decisions'::regclass
                    AND tgname = ANY(ARRAY['supplier_negotiation_decisions_append_only',
                        'supplier_negotiation_decisions_reject_truncate'])) = 2,
                EXISTS (SELECT 1 FROM public."Module" WHERE "ModuleName" = 'Supplier Negotiation'),
                (SELECT format_type(a.atttypid, a.atttypmod) = 'jsonb'
                 FROM pg_attribute a
                 WHERE a.attrelid = 'public.supplier_negotiation_decisions'::regclass
                   AND a.attname = 'EvidenceSnapshotJson'),
                EXISTS (SELECT 1 FROM pg_constraint
                    WHERE conrelid = 'public.supplier_negotiation_decisions'::regclass
                      AND contype = 'f'
                      AND position('("BusinessUnitId", "SupplierQuoteId", "SupplierQuoteRevisionId")'
                          in pg_get_constraintdef(oid)) > 0),
                NOT EXISTS (
                    SELECT 1
                    FROM public."RolePermissions" history_permission
                    JOIN public."Module" history_module ON history_module."ID" = history_permission."ModuleID"
                    WHERE history_module."ModuleName" = 'Supplier History'
                      AND NOT EXISTS (
                          SELECT 1
                          FROM public."RolePermissions" negotiation_permission
                          JOIN public."Module" negotiation_module
                            ON negotiation_module."ID" = negotiation_permission."ModuleID"
                          WHERE negotiation_module."ModuleName" = 'Supplier Negotiation'
                            AND negotiation_permission."BusinessUnitID" = history_permission."BusinessUnitID"
                            AND negotiation_permission."RoleID" IS NOT DISTINCT FROM history_permission."RoleID"));
            """;
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        for (var index = 0; index < 8; index++)
            Assert.True(reader.GetBoolean(index), $"Negotiation schema assertion {index + 1} failed.");
    }

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task Negotiation_ledger_rejects_cross_tenant_and_mutation_paths()
    {
        var tenantA = await SeedAsync(TenantA, OffsetA, "a");
        var tenantB = await SeedAsync(TenantB, OffsetB, "b");
        var sameTenantOther = await SeedSecondaryQuoteAsync(TenantA, OffsetA, "a-other");

        await using var connection = await database.OpenConnectionAsync();
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            await using var scope = connection.CreateCommand();
            scope.Transaction = transaction;
            scope.CommandText = $"""
                SET LOCAL ROLE nexora_tenant_app;
                SET LOCAL nexora.business_unit_id = '{TenantA}';
                SELECT
                    (SELECT count(*) FROM supplier_negotiation_decisions
                        WHERE "BusinessUnitId" = {TenantA}),
                    (SELECT count(*) FROM supplier_negotiation_decisions
                        WHERE "BusinessUnitId" = {TenantB});
                """;
            await using (var reader = await scope.ExecuteReaderAsync())
            {
                Assert.True(await reader.ReadAsync());
                Assert.Equal(1L, reader.GetInt64(0));
                Assert.Equal(0L, reader.GetInt64(1));
            }

            await using var forged = connection.CreateCommand();
            forged.Transaction = transaction;
            forged.CommandText = InsertSql(TenantB, tenantB.QuoteId, tenantB.RevisionId,
                "forged-runtime", "forged-runtime");
            var rlsError = await Assert.ThrowsAsync<PostgresException>(() => forged.ExecuteNonQueryAsync());
            Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, rlsError.SqlState);
            await transaction.RollbackAsync();
        }

        await using (var crossTenant = connection.CreateCommand())
        {
            crossTenant.CommandText = InsertSql(TenantA, tenantB.QuoteId, tenantB.RevisionId,
                "forged-lineage", "forged-lineage");
            var foreignKeyError = await Assert.ThrowsAsync<PostgresException>(() =>
                crossTenant.ExecuteNonQueryAsync());
            Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, foreignKeyError.SqlState);
        }

        await using (var mismatchedRevision = connection.CreateCommand())
        {
            mismatchedRevision.CommandText = InsertSql(TenantA, tenantA.QuoteId,
                sameTenantOther.RevisionId, "forged-same-tenant-lineage", "forged-same-tenant-lineage");
            var foreignKeyError = await Assert.ThrowsAsync<PostgresException>(() =>
                mismatchedRevision.ExecuteNonQueryAsync());
            Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, foreignKeyError.SqlState);
        }

        await using (var noTenantTransaction = await connection.BeginTransactionAsync())
        {
            await using var noTenant = connection.CreateCommand();
            noTenant.Transaction = noTenantTransaction;
            noTenant.CommandText = "SET LOCAL ROLE nexora_tenant_app; SELECT count(*) FROM supplier_negotiation_decisions;";
            Assert.Equal(0L, (long)(await noTenant.ExecuteScalarAsync())!);

            await using var insertWithoutTenant = connection.CreateCommand();
            insertWithoutTenant.Transaction = noTenantTransaction;
            insertWithoutTenant.CommandText = InsertSql(TenantA, tenantA.QuoteId, tenantA.RevisionId,
                "missing-tenant", "missing-tenant");
            var rlsError = await Assert.ThrowsAsync<PostgresException>(() =>
                insertWithoutTenant.ExecuteNonQueryAsync());
            Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, rlsError.SqlState);
            await noTenantTransaction.RollbackAsync();
        }

        await using (var runtimeMutation = await connection.BeginTransactionAsync())
        {
            await using var deniedDelete = connection.CreateCommand();
            deniedDelete.Transaction = runtimeMutation;
            deniedDelete.CommandText = $"SET LOCAL ROLE nexora_tenant_app; SET LOCAL nexora.business_unit_id = '{TenantA}'; DELETE FROM supplier_negotiation_decisions WHERE \"Id\" = {tenantA.DecisionId};";
            var denied = await Assert.ThrowsAsync<PostgresException>(() => deniedDelete.ExecuteNonQueryAsync());
            Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, denied.SqlState);
            await runtimeMutation.RollbackAsync();
        }

        await using (var runtimeTruncate = await connection.BeginTransactionAsync())
        {
            await using var deniedTruncate = connection.CreateCommand();
            deniedTruncate.Transaction = runtimeTruncate;
            deniedTruncate.CommandText = "SET LOCAL ROLE nexora_tenant_app; TRUNCATE supplier_negotiation_decisions;";
            var denied = await Assert.ThrowsAsync<PostgresException>(() => deniedTruncate.ExecuteNonQueryAsync());
            Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, denied.SqlState);
            await runtimeTruncate.RollbackAsync();
        }

        await using (var rewrite = connection.CreateCommand())
        {
            rewrite.CommandText = $"""
                UPDATE supplier_negotiation_decisions SET "Reason" = 'rewritten'
                WHERE "BusinessUnitId" = {TenantA} AND "Id" = {tenantA.DecisionId};
                """;
            var appendOnlyError = await Assert.ThrowsAsync<PostgresException>(() =>
                rewrite.ExecuteNonQueryAsync());
            Assert.Equal("55000", appendOnlyError.SqlState);
        }

        await using (var truncate = connection.CreateCommand())
        {
            truncate.CommandText = "TRUNCATE supplier_negotiation_decisions;";
            var appendOnlyError = await Assert.ThrowsAsync<PostgresException>(() =>
                truncate.ExecuteNonQueryAsync());
            Assert.Equal("55000", appendOnlyError.SqlState);
        }
    }

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task Concurrent_decisions_allow_exactly_one_quote_version_winner()
    {
        var seed = await SeedAsync(98_403, 220_000, "concurrency");
        async Task<Exception?> AttemptAsync(string key)
        {
            try
            {
                await using var context = database.ContextFor(98_403);
                await new SupplierNegotiationService(context).DecideAsync(new SupplierNegotiationCommand(
                    98_403, seed.QuoteId, 1,
                    SupplierNegotiationRecommendationCodes.FreightInclusiveOffer,
                    SupplierNegotiationDispositions.Prepared, "Request an inclusive freight offer.",
                    key, "qa", key));
                return null;
            }
            catch (Exception error)
            {
                return error;
            }
        }

        var results = await Task.WhenAll(AttemptAsync("concurrent-a"), AttemptAsync("concurrent-b"));
        Assert.Single(results, result => result is null);
        var failed = Assert.Single(results, result => result is not null);
        Assert.True(failed is SupplierQuoteConflictException, failed!.ToString());

        await using var verify = database.ContextFor(98_403);
        Assert.Equal(2, await verify.SupplierQuotes.Where(x => x.Id == seed.QuoteId)
            .Select(x => x.Version).SingleAsync());
        Assert.Equal(2, await verify.SupplierNegotiationDecisions.CountAsync(x =>
            x.SupplierQuoteId == seed.QuoteId));
    }

    private async Task<(long QuoteId, long RevisionId, long DecisionId)> SeedAsync(
        long tenantId, long offset, string suffix)
    {
        await using var context = database.ContextFor(null);
        ProcurementTestData.SeedGraph(context, tenantId, offset);
        var demand = new CommercialDemandLine
        {
            Id = 2_000_000 + offset, BusinessUnitId = tenantId,
            RfqId = ProcurementTestData.Rfq + offset,
            RfqItemId = ProcurementTestData.RfqItem + offset,
            NexoraSerial = $"NXR-PG-NEG-{suffix}", IdentityKey = $"pg-neg-{suffix}",
            CreatedBy = "qa", CreatedOn = DateTime.UtcNow
        };
        var sourcingCase = new SourcingCase
        {
            Id = 2_100_000 + offset, BusinessUnitId = tenantId,
            CommercialDemandLineId = demand.Id, RfqId = demand.RfqId,
            RfqItemId = demand.RfqItemId, ProductId = ProcurementTestData.Product + offset,
            NexoraSerial = demand.NexoraSerial, Description = "Negotiation test component",
            RequestedQuantity = 10, StockQuantity = 0, UnfulfilledQuantity = 10,
            SearchLimit = 10, Status = SourcingCaseStatuses.ComparisonReady,
            NextAction = "Review offers", ShortageDecisionKey = $"pg-neg-shortage-{suffix}",
            IdempotencyKey = $"pg-neg-case-{suffix}", RequestHash = new string('A', 64),
            CreatedBy = "qa", UpdatedBy = "qa", CreatedOn = DateTime.UtcNow,
            UpdatedOn = DateTime.UtcNow
        };
        var solicitation = new SupplierSolicitation
        {
            Id = 2_200_000 + offset, BusinessUnitId = tenantId,
            RfqId = demand.RfqId, SupplierId = ProcurementTestData.Supplier + offset,
            SourcingCaseId = sourcingCase.Id, CommercialDemandLineId = demand.Id,
            NexoraSerial = demand.NexoraSerial, SupplierRfqNumber = $"SRFQ-PG-NEG-{suffix}",
            IdempotencyKey = $"pg-neg-sol-{suffix}", RequestHash = new string('B', 64),
            RequestedRfqItemIdsJson = $"[{demand.RfqItemId}]", Status = SolicitationStatus.Responded,
            SentOn = DateTime.UtcNow.AddDays(-1), RespondedOn = DateTime.UtcNow,
            CreatedOn = DateTime.UtcNow.AddDays(-1), UpdatedOn = DateTime.UtcNow
        };
        var quote = new SupplierQuote
        {
            Id = 2_300_000 + offset, BusinessUnitId = tenantId,
            SupplierId = solicitation.SupplierId, SupplierSolicitationId = solicitation.Id,
            SourcingCaseId = sourcingCase.Id, RfqId = demand.RfqId,
            NexoraSerial = demand.NexoraSerial, SupplierQuoteReference = $"SQ-PG-NEG-{suffix}",
            CurrentRevisionNumber = 1, InboxStatus = SupplierQuoteInboxStatuses.ReadyForComparison,
            Version = 1, CreatedBy = "qa", UpdatedBy = "qa",
            CreatedOn = DateTime.UtcNow, UpdatedOn = DateTime.UtcNow
        };
        var revision = new SupplierQuoteRevision
        {
            Id = 2_400_000 + offset, BusinessUnitId = tenantId, SupplierQuoteId = quote.Id,
            RevisionNumber = 1, CaptureChannel = SupplierQuoteCaptureChannels.Manual,
            SourceIdentity = $"pg-neg-source-{suffix}", SourceSha256 = new string('C', 64),
            CurrencyId = ProcurementTestData.Currency + offset,
            ValidUntil = DateTime.UtcNow.AddDays(30), Incoterms = "FCA",
            PaymentTerms = "NET 30", FreightAmount = 10m,
            IdempotencyKey = $"pg-neg-revision-{suffix}", RequestHash = new string('D', 64),
            CapturedOn = DateTime.UtcNow, CapturedBy = "qa", CorrelationId = $"pg-neg-{suffix}",
            SupplierQuote = quote
        };
        revision.Lines.Add(new SupplierQuoteLine
        {
            Id = 2_500_000 + offset, BusinessUnitId = tenantId,
            SupplierQuoteRevisionId = revision.Id, LineNumber = 1,
            RfqItemId = demand.RfqItemId, CommercialDemandLineId = demand.Id,
            PartNumber = "PG-NEG", Description = "Negotiation component", Quantity = 10m,
            AvailableQuantity = 10m, UnitOfMeasure = "EA", UnitPrice = 10m,
            LeadTimeDays = 5, AvailabilityType = "IN_STOCK"
        });
        var decision = new SupplierNegotiationDecision
        {
            BusinessUnitId = tenantId, SupplierQuoteId = quote.Id,
            SupplierQuoteRevisionId = revision.Id, RecommendationCode = "BEST_AND_FINAL_PRICE",
            Disposition = SupplierNegotiationDispositions.Prepared, Reason = "Prepare negotiation",
            EvidenceSnapshotJson = "{}", PolicyVersion = "qa-v1", ExpectedQuoteVersion = 1,
            IdempotencyKey = $"pg-neg-decision-{suffix}", RequestHash = new string('E', 64),
            Actor = "qa", DecidedOn = DateTime.UtcNow, CorrelationId = $"pg-neg-{suffix}"
        };
        quote.Revisions.Add(revision);
        context.CommercialDemandLines.Add(demand);
        context.SourcingCases.Add(sourcingCase);
        context.Set<SupplierSolicitation>().Add(solicitation);
        context.SupplierQuotes.Add(quote);
        context.SupplierNegotiationDecisions.Add(decision);
        await context.SaveChangesAsync();
        return (quote.Id, revision.Id, decision.Id);
    }

    private async Task<(long QuoteId, long RevisionId)> SeedSecondaryQuoteAsync(
        long tenantId, long offset, string suffix)
    {
        await using var context = database.ContextFor(null);
        var quote = new SupplierQuote
        {
            Id = 2_350_000 + offset, BusinessUnitId = tenantId,
            SupplierId = ProcurementTestData.Supplier + offset,
            SupplierSolicitationId = 2_200_000 + offset,
            SourcingCaseId = 2_100_000 + offset,
            RfqId = ProcurementTestData.Rfq + offset,
            NexoraSerial = $"NXR-PG-NEG-{suffix}", SupplierQuoteReference = $"SQ-PG-NEG-{suffix}",
            CurrentRevisionNumber = 1, InboxStatus = SupplierQuoteInboxStatuses.ReadyForComparison,
            Version = 1, CreatedBy = "qa", UpdatedBy = "qa",
            CreatedOn = DateTime.UtcNow, UpdatedOn = DateTime.UtcNow
        };
        var revision = new SupplierQuoteRevision
        {
            Id = 2_450_000 + offset, BusinessUnitId = tenantId, SupplierQuoteId = quote.Id,
            RevisionNumber = 1, CaptureChannel = SupplierQuoteCaptureChannels.Manual,
            SourceIdentity = $"pg-neg-source-{suffix}", SourceSha256 = new string('A', 64),
            CurrencyId = ProcurementTestData.Currency + offset,
            IdempotencyKey = $"pg-neg-revision-{suffix}", RequestHash = new string('B', 64),
            CapturedOn = DateTime.UtcNow, CapturedBy = "qa", CorrelationId = $"pg-neg-{suffix}",
            SupplierQuote = quote
        };
        quote.Revisions.Add(revision);
        context.SupplierQuotes.Add(quote);
        await context.SaveChangesAsync();
        return (quote.Id, revision.Id);
    }

    private static string InsertSql(long businessUnitId, long quoteId, long revisionId,
        string idempotencyKey, string correlationId) => $"""
        INSERT INTO supplier_negotiation_decisions
            ("BusinessUnitId", "SupplierQuoteId", "SupplierQuoteRevisionId", "RecommendationCode",
             "Disposition", "Reason", "EvidenceSnapshotJson", "PolicyVersion",
             "ExpectedQuoteVersion", "IdempotencyKey", "RequestHash", "Actor", "DecidedOn",
             "CorrelationId")
        VALUES ({businessUnitId}, {quoteId}, {revisionId}, 'BEST_AND_FINAL_PRICE', 'PREPARED',
            'qa', jsonb_build_object('schema', 'qa'), 'qa-v1', 1,
            '{idempotencyKey}', repeat('F', 64), 'qa', now(),
            '{correlationId}');
        """;
}
