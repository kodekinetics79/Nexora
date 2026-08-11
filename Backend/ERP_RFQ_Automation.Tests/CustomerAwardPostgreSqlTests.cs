using ERP_RFQ_Automation.OrderToCash;
using ERP_RFQ_Automation.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace ERP_RFQ_Automation.Tests;

[Collection(PostgreSqlIntegrationCollection.Name)]
public sealed class CustomerAwardPostgreSqlTests(PostgreSqlTestDatabase database)
{
    private const long BusinessUnitId = 889_001;
    private const long CustomerId = 880_002;
    private const long CurrencyId = 880_003;
    private const long QuoteId = 880_011;
    private const long QuoteItemId = 880_012;
    private readonly PostgreSqlTestDatabase _database = database;

    [Fact]
    [Trait("Category", "PostgreSQL")]
    // Squash note: this method opened by asserting that '20260723190000_AddCustomerAwards' was
    // present in "__EFMigrationsHistory". 20260811033109_SquashedSchemaBaseline erased that id, so
    // the check could only ever be 0. It was never the coverage — the six tenant policies and the
    // order-to-cash grant shape below are — and both are asserted against the live catalogue.
    public async Task OrderToCashSchemaHasTenantPoliciesAndLeastPrivilegeGrants()
    {
        await using var connection = await _database.OpenConnectionAsync();

        await using var policies = connection.CreateCommand();
        policies.CommandText = """
            SELECT count(*)
            FROM pg_policies
            WHERE schemaname = 'public' AND policyname = 'nexora_tenant_isolation'
              AND tablename = ANY(ARRAY[
                'CustomerPurchaseOrders', 'CustomerPurchaseOrderLines', 'CustomerAwards',
                'CustomerAwardLineAllocations', 'OrderToCashAuditEvents', 'OrderToCashDocumentCounters'])
            """;
        Assert.Equal(6L, (long)(await policies.ExecuteScalarAsync())!);

        await using var grants = connection.CreateCommand();
        grants.CommandText = """
            SELECT has_table_privilege('nexora_tenant_app', 'public."CustomerAwards"', 'SELECT,INSERT,UPDATE,DELETE')
               AND has_table_privilege('nexora_tenant_app', 'public."OrderToCashAuditEvents"', 'SELECT')
               AND NOT has_table_privilege('nexora_tenant_app', 'public."OrderToCashAuditEvents"', 'INSERT')
               AND NOT has_table_privilege('nexora_tenant_app', 'public."OrderToCashAuditEvents"', 'UPDATE')
               AND NOT has_table_privilege('nexora_tenant_app', 'public."OrderToCashAuditEvents"', 'DELETE')
            """;
        Assert.True((bool)(await grants.ExecuteScalarAsync())!);
    }

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task CustomerAwards_EnforceCanonicalIdentityConcurrencyOutboxAndImmutableSources()
    {
        long commercialCaseId;
        await using (var seed = _database.ContextFor(null))
        {
            CustomerAwardTestFixture.SeedGraph(seed, BusinessUnitId, twoQuoteLines: true);
            commercialCaseId = await seed.Leads.IgnoreQueryFilters()
                .Where(x => x.Id == 880_009).Select(x => x.CommercialCaseId).SingleAsync();
        }

        CustomerPurchaseOrderView purchaseOrder;
        CustomerAwardView firstAward;
        CustomerAwardView secondAward;
        await using (var context = _database.ContextFor(BusinessUnitId))
        {
            var service = new CustomerAwardApplicationService(context);
            purchaseOrder = await service.CreatePurchaseOrderAsync(BusinessUnitId, "pg-po-create",
                "pg-corr-po-create", PurchaseOrderCommand(commercialCaseId, "  Buyer PO   2026 / 77  "), "tests");
            var duplicate = PurchaseOrderCommand(commercialCaseId, "buyer po 2026 / 77");
            await Assert.ThrowsAsync<CustomerAwardConflictException>(() => service.CreatePurchaseOrderAsync(
                BusinessUnitId, "pg-po-canonical-duplicate", "pg-corr-po-duplicate", duplicate, "tests"));

            firstAward = await service.CreateAwardAsync(BusinessUnitId, "pg-award-one", "pg-corr-award-one",
                AwardCommand(purchaseOrder, 6m), "tests");
            secondAward = await service.CreateAwardAsync(BusinessUnitId, "pg-award-two", "pg-corr-award-two",
                AwardCommand(purchaseOrder, 6m), "tests");
        }

        var race = await Task.WhenAll(
            CaptureConfirmAsync(firstAward.Id, "pg-confirm-one"),
            CaptureConfirmAsync(secondAward.Id, "pg-confirm-two"));
        var winner = Assert.Single(race, x => x.Award is not null).Award!;
        var loserError = Assert.Single(race, x => x.Error is not null).Error!;
        Assert.True(loserError is CustomerAwardConflictException
            || loserError is PostgresException { SqlState: "40001" or "23514" }
            || loserError.InnerException is PostgresException { SqlState: "40001" or "23514" },
            $"Unexpected race failure: {loserError.GetType().Name}: {loserError.Message}");

        long orderId;
        await using (var convertContext = _database.ContextFor(BusinessUnitId))
        {
            var service = new CustomerAwardApplicationService(convertContext);
            var order = await service.ConvertToOrderAsync(BusinessUnitId, winner.Id, "pg-convert",
                "pg-corr-convert", new(winner.Version), "tests");
            var replay = await service.ConvertToOrderAsync(BusinessUnitId, winner.Id, "pg-convert",
                "pg-corr-convert-replay", new(winner.Version), "tests");
            Assert.Equal(order, replay);
            orderId = order.Id;
        }

        await using var connection = await _database.OpenConnectionAsync();
        await using var statuses = connection.CreateCommand();
        statuses.CommandText = """
            SELECT count(*) FILTER (WHERE "Status" IN ('CONFIRMED','ORDERED')),
                   count(*) FILTER (WHERE "Status" = 'DRAFT')
            FROM "CustomerAwards" WHERE "BusinessUnitId" = @tenant
            """;
        statuses.Parameters.AddWithValue("tenant", BusinessUnitId);
        await using (var reader = await statuses.ExecuteReaderAsync())
        {
            Assert.True(await reader.ReadAsync());
            Assert.Equal(1L, reader.GetInt64(0));
            Assert.Equal(1L, reader.GetInt64(1));
        }

        await using var outbox = connection.CreateCommand();
        outbox.CommandText = """
            SELECT count(*) FROM "FinanceOutboxMessages"
            WHERE "BusinessUnitId" = @tenant
              AND "EventType" LIKE 'order-to-cash.%'
            """;
        outbox.Parameters.AddWithValue("tenant", BusinessUnitId);
        Assert.True((long)(await outbox.ExecuteScalarAsync())! >= 6L);

        await using var canonicalDuplicate = connection.CreateCommand();
        canonicalDuplicate.CommandText = """
            INSERT INTO "CustomerPurchaseOrders"
                ("BusinessUnitId", "CommercialCaseId", "CustomerId", "InternalNumber", "ExternalPoNumber",
                 "NormalizedExternalPoNumber", "PoDate", "ReceivedOn", "CurrencyId", "Status", "Version",
                 "CreatedOn", "CreatedBy")
            SELECT "BusinessUnitId", "CommercialCaseId", "CustomerId", 'CPO-DATABASE-DUPLICATE',
                   lower("ExternalPoNumber"), "NormalizedExternalPoNumber", "PoDate", "ReceivedOn", "CurrencyId",
                   'CONFIRMED', 1, now(), 'tests'
            FROM "CustomerPurchaseOrders" WHERE "Id" = @poId
            """;
        canonicalDuplicate.Parameters.AddWithValue("poId", purchaseOrder.Id);
        Assert.Equal(PostgresErrorCodes.UniqueViolation,
            (await Assert.ThrowsAsync<PostgresException>(() => canonicalDuplicate.ExecuteNonQueryAsync())).SqlState);

        await using var immutableAudit = connection.CreateCommand();
        immutableAudit.CommandText = """
            UPDATE "OrderToCashAuditEvents" SET "Actor" = 'forged'
            WHERE "Id" = (SELECT "Id" FROM "OrderToCashAuditEvents" WHERE "BusinessUnitId" = @tenant LIMIT 1)
            """;
        immutableAudit.Parameters.AddWithValue("tenant", BusinessUnitId);
        Assert.Equal("55000", (await Assert.ThrowsAsync<PostgresException>(() => immutableAudit.ExecuteNonQueryAsync())).SqlState);

        await using var forgedAuditTransaction = await connection.BeginTransactionAsync();
        await using var forgedAudit = connection.CreateCommand();
        forgedAudit.Transaction = forgedAuditTransaction;
        forgedAudit.CommandText = $"""
            SET LOCAL ROLE nexora_tenant_app;
            SET LOCAL nexora.business_unit_id = '{BusinessUnitId}';
            INSERT INTO "OrderToCashAuditEvents"
                ("BusinessUnitId", "AggregateType", "AggregateId", "AggregateVersion", "CommandType",
                 "PreviousState", "NewState", "Actor", "RequestHash", "IdempotencyKey", "ResultJson",
                 "CorrelationId", "OccurredOn")
            VALUES ({BusinessUnitId}, 'CUSTOMER_AWARD', {winner.Id}, {winner.Version}, 'CONFIRM_AWARD',
                    'DRAFT', 'CONFIRMED', 'forged', repeat('0', 64), 'forged-audit', jsonb_build_object(),
                    'forged-correlation', now())
            """;
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege,
            (await Assert.ThrowsAsync<PostgresException>(() => forgedAudit.ExecuteNonQueryAsync())).SqlState);
        await forgedAuditTransaction.RollbackAsync();

        await using var immutableSource = connection.CreateCommand();
        immutableSource.CommandText = "UPDATE \"Orders\" SET \"SourceType\" = 'MANUAL' WHERE \"ID\" = @id";
        immutableSource.Parameters.AddWithValue("id", orderId);
        Assert.Equal("55000", (await Assert.ThrowsAsync<PostgresException>(() => immutableSource.ExecuteNonQueryAsync())).SqlState);

        await using var immutableLineSource = connection.CreateCommand();
        immutableLineSource.CommandText = """
            UPDATE "OrderItems" SET "CustomerAwardLineAllocationID" = NULL WHERE "OrderID" = @id
            """;
        immutableLineSource.Parameters.AddWithValue("id", orderId);
        Assert.Equal("55000",
            (await Assert.ThrowsAsync<PostgresException>(() => immutableLineSource.ExecuteNonQueryAsync())).SqlState);

        await using var tenantRead = await connection.BeginTransactionAsync();
        await using var rls = connection.CreateCommand();
        rls.Transaction = tenantRead;
        rls.CommandText = $"""
            SET LOCAL ROLE nexora_tenant_app;
            SET LOCAL nexora.business_unit_id = '{BusinessUnitId + 1}';
            SELECT count(*) FROM "CustomerAwards" WHERE "BusinessUnitId" = {BusinessUnitId};
            """;
        Assert.Equal(0L, (long)(await rls.ExecuteScalarAsync())!);
        await tenantRead.RollbackAsync();
    }

    private async Task<(CustomerAwardView? Award, Exception? Error)> CaptureConfirmAsync(long awardId, string key)
    {
        try
        {
            await using var context = _database.ContextFor(BusinessUnitId);
            var award = await new CustomerAwardApplicationService(context).ConfirmAwardAsync(
                BusinessUnitId, awardId, key, $"corr-{key}", new(1), "tests");
            return (award, null);
        }
        catch (Exception exception)
        {
            return (null, exception);
        }
    }

    private static CreateCustomerPurchaseOrderCommand PurchaseOrderCommand(long commercialCaseId, string externalNumber)
        => new(QuoteId, commercialCaseId, CustomerId, CurrencyId, externalNumber,
            new DateTime(2026, 7, 23), new DateTime(2026, 7, 23), 0,
            [new("1", 880_004, "Awarded widget", 10m, null, 100m, 1_000m)]);

    private static CreateCustomerAwardCommand AwardCommand(CustomerPurchaseOrderView purchaseOrder, decimal quantity)
        => new(purchaseOrder.Id, QuoteId, 0, purchaseOrder.Version, 1,
            [new(purchaseOrder.Lines.Single().Id, QuoteItemId, quantity)]);
}
