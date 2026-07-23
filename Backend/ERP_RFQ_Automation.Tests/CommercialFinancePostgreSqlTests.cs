using ERP_RFQ_Automation.CommercialFinance;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace ERP_RFQ_Automation.Tests;

[Collection(PostgreSqlIntegrationCollection.Name)]
public sealed class CommercialFinancePostgreSqlTests(PostgreSqlTestDatabase database)
{
    private readonly PostgreSqlTestDatabase _database = database;

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task FinanceLedger_ControlsConcurrentNumbersImmutabilityAndTenantForeignKeys()
    {
        long firstDraftId;
        long secondDraftId;
        long cancellationDraftId;
        long raceDraftId;
        long directIssueDraftId;
        long directOverInvoiceDraftId;
        await using (var seed = _database.ContextFor(null))
        {
            SeedParents(seed);
            var firstOrder = NewOrder(OrderOneId, "ORD-PG-AR-1");
            var secondOrder = NewOrder(OrderTwoId, "ORD-PG-AR-2");
            var replayOrder = NewOrder(OrderThreeId, "ORD-PG-AR-3");
            var conflictingReplayOrder = NewOrder(OrderFourId, "ORD-PG-AR-4");
            var cancellationOrder = NewOrder(OrderFiveId, "ORD-PG-AR-5");
            var raceOrder = NewOrder(OrderSixId, "ORD-PG-AR-6");
            var directIssueOrder = NewOrder(OrderSevenId, "ORD-PG-AR-7");
            seed.Orders.AddRange(firstOrder, secondOrder, replayOrder, conflictingReplayOrder, cancellationOrder, raceOrder, directIssueOrder);
            await seed.SaveChangesAsync();
            var service = new CommercialFinanceApplicationService(seed);
            firstDraftId = (await service.CreateInvoiceAsync(
                BusinessUnitId, firstOrder.Id, "pg-finance-draft-1", new CreateInvoiceRequest(null, null, null), "tests")).Id;
            secondDraftId = (await service.CreateInvoiceAsync(
                BusinessUnitId, secondOrder.Id, "pg-finance-draft-2", new CreateInvoiceRequest(null, null, null), "tests")).Id;
            cancellationDraftId = (await service.CreateInvoiceAsync(
                BusinessUnitId, cancellationOrder.Id, "pg-finance-cancel", new CreateInvoiceRequest(null, null, null), "tests")).Id;
            raceDraftId = (await service.CreateInvoiceAsync(
                BusinessUnitId, raceOrder.Id, "pg-finance-issue-cancel-race", new CreateInvoiceRequest(null, null, null), "tests")).Id;
            directIssueDraftId = (await service.CreateInvoiceAsync(
                BusinessUnitId, directIssueOrder.Id, "pg-finance-direct-issue", new CreateInvoiceRequest(null, null, null), "tests")).Id;
            directOverInvoiceDraftId = (await service.CreateInvoiceAsync(
                BusinessUnitId, directIssueOrder.Id, "pg-finance-direct-over-invoice", new CreateInvoiceRequest(null, null, null), "tests")).Id;
        }

        var concurrentReplay = await Task.WhenAll(
            CreateDraftAsync(OrderThreeId, "pg-finance-concurrent-replay"),
            CreateDraftAsync(OrderThreeId, "pg-finance-concurrent-replay"));
        Assert.Equal(concurrentReplay[0].Id, concurrentReplay[1].Id);

        var conflictingReplay = await Task.WhenAll(
            CaptureDraftAsync(OrderThreeId, "pg-finance-cross-order-key"),
            CaptureDraftAsync(OrderFourId, "pg-finance-cross-order-key"));
        Assert.Single(conflictingReplay, x => x.Document is not null);
        Assert.IsType<FinanceConflictException>(Assert.Single(conflictingReplay, x => x.Error is not null).Error);

        var issued = await Task.WhenAll(IssueAsync(firstDraftId), IssueAsync(secondDraftId));
        Assert.Equal(2, issued.Select(x => x.DocumentNumber).Distinct().Count());
        Assert.Equal(new[] { "INV-" + DateTime.UtcNow.Year + "-000001", "INV-" + DateTime.UtcNow.Year + "-000002" },
            issued.Select(x => x.DocumentNumber).Order().ToArray());

        var cancelled = await CancelAsync(cancellationDraftId);
        Assert.Equal(ReceivableDocumentStatuses.Cancelled, cancelled.Status);
        Assert.Null(cancelled.DocumentNumber);
        Assert.NotNull(cancelled.VoidedOn);

        var race = await Task.WhenAll(CaptureIssueAsync(raceDraftId), CaptureCancelAsync(raceDraftId));
        Assert.Single(race, x => x.Document is not null);
        Assert.IsType<FinanceConflictException>(Assert.Single(race, x => x.Error is not null).Error);
        await using (var verifyRace = _database.ContextFor(BusinessUnitId))
        {
            var final = await verifyRace.ReceivableDocuments.SingleAsync(x => x.Id == raceDraftId);
            Assert.Contains(final.Status, new[] { ReceivableDocumentStatuses.Issued, ReceivableDocumentStatuses.Cancelled });
        }

        long paymentId;
        await using (var paymentContext = _database.ContextFor(BusinessUnitId))
        {
            var payment = await new CommercialFinanceApplicationService(paymentContext).PostPaymentAsync(
                BusinessUnitId, "pg-finance-payment", new PostPaymentRequest(
                    CustomerId, null, CurrencyId, DateTime.UtcNow, 1m, "BankTransfer", "PG-REF", []), "tests");
            paymentId = payment.Id;
        }

        await using var connection = await _database.OpenConnectionAsync();
        await using var moduleCount = connection.CreateCommand();
        moduleCount.CommandText = "SELECT count(*) FROM \"Module\" WHERE \"ModuleName\" IN ('Accounts Receivable', 'Customer Payments')";
        Assert.Equal(2L, (long)(await moduleCount.ExecuteScalarAsync())!);
        await using var rewriteDocument = connection.CreateCommand();
        rewriteDocument.CommandText = "UPDATE \"ReceivableDocuments\" SET \"TotalAmount\" = 1 WHERE \"Id\" = @id";
        rewriteDocument.Parameters.AddWithValue("id", issued[0].Id);
        Assert.Equal("55000", (await Assert.ThrowsAsync<PostgresException>(
            () => rewriteDocument.ExecuteNonQueryAsync())).SqlState);

        await using var rewriteCancelledDocument = connection.CreateCommand();
        rewriteCancelledDocument.CommandText = "UPDATE \"ReceivableDocuments\" SET \"VoidReason\" = 'forged' WHERE \"Id\" = @id";
        rewriteCancelledDocument.Parameters.AddWithValue("id", cancelled.Id);
        Assert.Equal("55000", (await Assert.ThrowsAsync<PostgresException>(
            () => rewriteCancelledDocument.ExecuteNonQueryAsync())).SqlState);

        await using var governedDirectIssue = connection.CreateCommand();
        governedDirectIssue.CommandText = """
            UPDATE "ReceivableDocuments"
            SET "Status" = 'Issued', "DocumentNumber" = 'FORGED-999', "IssuedOn" = now(),
                "IssuedBy" = 'database-control-test', "Version" = "Version" + 1
            WHERE "Id" = @id
            RETURNING "DocumentNumber"
            """;
        governedDirectIssue.Parameters.AddWithValue("id", directIssueDraftId);
        var databaseNumber = (string)(await governedDirectIssue.ExecuteScalarAsync())!;
        Assert.NotEqual("FORGED-999", databaseNumber);
        Assert.Matches($"^INV-{DateTime.UtcNow.Year}-[0-9]{{6}}$", databaseNumber);

        await using var directOverInvoice = connection.CreateCommand();
        directOverInvoice.CommandText = """
            UPDATE "ReceivableDocuments"
            SET "Status" = 'Issued', "DocumentNumber" = 'FORGED-OVER', "IssuedOn" = now(),
                "IssuedBy" = 'database-control-test', "Version" = "Version" + 1
            WHERE "Id" = @id
            """;
        directOverInvoice.Parameters.AddWithValue("id", directOverInvoiceDraftId);
        Assert.Equal(PostgresErrorCodes.CheckViolation,
            (await Assert.ThrowsAsync<PostgresException>(() => directOverInvoice.ExecuteNonQueryAsync())).SqlState);

        await using var transitionAudits = connection.CreateCommand();
        transitionAudits.CommandText = """
            SELECT count(*) FROM "CommercialFinanceAudits"
            WHERE "BusinessUnitId" = @tenant AND
                (("AggregateId" = @cancelled AND "Action" = 'DraftCancelled' AND "Actor" = 'tests') OR
                 ("AggregateId" = @directIssue AND "Action" = 'Issued' AND "Actor" = 'database-control-test'))
            """;
        transitionAudits.Parameters.AddWithValue("tenant", BusinessUnitId);
        transitionAudits.Parameters.AddWithValue("cancelled", cancelled.Id);
        transitionAudits.Parameters.AddWithValue("directIssue", directIssueDraftId);
        Assert.Equal(2L, (long)(await transitionAudits.ExecuteScalarAsync())!);

        await using var rewriteAudit = connection.CreateCommand();
        rewriteAudit.CommandText = "UPDATE \"CommercialFinanceAudits\" SET \"Action\" = 'Forged' WHERE \"AggregateId\" = @id";
        rewriteAudit.Parameters.AddWithValue("id", issued[0].Id);
        Assert.Equal("55000", (await Assert.ThrowsAsync<PostgresException>(
            () => rewriteAudit.ExecuteNonQueryAsync())).SqlState);

        await using var crossTenantLine = connection.CreateCommand();
        crossTenantLine.CommandText = """
            INSERT INTO "ReceivableDocumentLines"
                ("BusinessUnitId", "ReceivableDocumentId", "Description", "Quantity", "UnitPrice", "DiscountAmount", "TaxAmount", "LineTotal")
            VALUES (@otherTenant, @documentId, 'forged', 1, 1, 0, 0, 1)
            """;
        crossTenantLine.Parameters.AddWithValue("otherTenant", BusinessUnitId + 1);
        crossTenantLine.Parameters.AddWithValue("documentId", issued[0].Id);
        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation,
            (await Assert.ThrowsAsync<PostgresException>(() => crossTenantLine.ExecuteNonQueryAsync())).SqlState);

        await using var secondOrderItem = connection.CreateCommand();
        secondOrderItem.CommandText = "SELECT \"OrderItemId\" FROM \"ReceivableDocumentLines\" WHERE \"ReceivableDocumentId\" = @id";
        secondOrderItem.Parameters.AddWithValue("id", issued[1].Id);
        var wrongOrderItemId = (long)(await secondOrderItem.ExecuteScalarAsync())!;
        await using var wrongOrderItem = connection.CreateCommand();
        wrongOrderItem.CommandText = """
            INSERT INTO "ReceivableDocumentLines"
                ("BusinessUnitId", "ReceivableDocumentId", "OrderItemId", "Description", "Quantity", "UnitPrice", "DiscountAmount", "TaxAmount", "LineTotal")
            VALUES (@tenant, @documentId, @orderItemId, 'forged', 1, 1, 0, 0, 1)
            """;
        wrongOrderItem.Parameters.AddWithValue("tenant", BusinessUnitId);
        wrongOrderItem.Parameters.AddWithValue("documentId", issued[0].Id);
        wrongOrderItem.Parameters.AddWithValue("orderItemId", wrongOrderItemId);
        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation,
            (await Assert.ThrowsAsync<PostgresException>(() => wrongOrderItem.ExecuteNonQueryAsync())).SqlState);

        await using var overAllocate = connection.CreateCommand();
        overAllocate.CommandText = """
            INSERT INTO "PaymentAllocations"
                ("BusinessUnitId", "CustomerPaymentId", "ReceivableDocumentId", "Amount", "CreatedOn")
            VALUES (@tenant, @paymentId, @documentId, 2, now())
            """;
        overAllocate.Parameters.AddWithValue("tenant", BusinessUnitId);
        overAllocate.Parameters.AddWithValue("paymentId", paymentId);
        overAllocate.Parameters.AddWithValue("documentId", issued[0].Id);
        Assert.Equal(PostgresErrorCodes.CheckViolation,
            (await Assert.ThrowsAsync<PostgresException>(() => overAllocate.ExecuteNonQueryAsync())).SqlState);

        await using var forgedReversal = connection.CreateCommand();
        forgedReversal.CommandText = """
            UPDATE "CustomerPayments"
            SET "Status" = 'Reversed', "Version" = "Version" + 1, "ReversedOn" = now(),
                "ReversalReason" = 'forged', "Method" = 'Cash'
            WHERE "Id" = @paymentId
            """;
        forgedReversal.Parameters.AddWithValue("paymentId", paymentId);
        Assert.Equal("55000", (await Assert.ThrowsAsync<PostgresException>(
            () => forgedReversal.ExecuteNonQueryAsync())).SqlState);
    }

    private async Task<ReceivableDocumentDto> IssueAsync(long documentId)
    {
        await using var context = _database.ContextFor(BusinessUnitId);
        return await new CommercialFinanceApplicationService(context)
            .IssueAsync(BusinessUnitId, documentId, new IssueDocumentRequest(1), "tests");
    }

    private async Task<ReceivableDocumentDto> CancelAsync(long documentId)
    {
        await using var context = _database.ContextFor(BusinessUnitId);
        return await new CommercialFinanceApplicationService(context)
            .CancelAsync(BusinessUnitId, documentId, new CancelDocumentRequest(1, "Duplicate draft"), "tests");
    }

    private async Task<(ReceivableDocumentDto? Document, Exception? Error)> CaptureIssueAsync(long documentId)
    {
        try { return (await IssueAsync(documentId), null); }
        catch (Exception exception) { return (null, exception); }
    }

    private async Task<(ReceivableDocumentDto? Document, Exception? Error)> CaptureCancelAsync(long documentId)
    {
        try { return (await CancelAsync(documentId), null); }
        catch (Exception exception) { return (null, exception); }
    }

    private async Task<ReceivableDocumentDto> CreateDraftAsync(long orderId, string idempotencyKey)
    {
        await using var context = _database.ContextFor(BusinessUnitId);
        return await new CommercialFinanceApplicationService(context).CreateInvoiceAsync(
            BusinessUnitId, orderId, idempotencyKey, new CreateInvoiceRequest(null, null, null), "tests");
    }

    private async Task<(ReceivableDocumentDto? Document, Exception? Error)> CaptureDraftAsync(
        long orderId, string idempotencyKey)
    {
        try
        {
            return (await CreateDraftAsync(orderId, idempotencyKey), null);
        }
        catch (Exception exception)
        {
            return (null, exception);
        }
    }

    private static void SeedParents(ErpRfqAutomationContext db)
    {
        Seed.EnsureBusinessUnit(db, BusinessUnitId);
        Seed.Customer(db, CustomerId, BusinessUnitId, "PG AR Customer");
        db.Currencies.Add(new Currency
        {
            Id = CurrencyId,
            Code = "PGAR",
            CurrencyName = "PG AR Currency",
            Symbol = "PGA",
            ExchangeRate = 1m,
            IsBaseCurrency = true,
            IsActive = true,
            CreatedBy = "tests",
            CreatedOn = DateTime.UtcNow,
            BusinessUnitId = BusinessUnitId
        });
        db.Products.Add(new Product
        {
            Id = ProductId,
            ProductName = "PG AR Product",
            PartNo = "PG-AR-1",
            Buid = BusinessUnitId,
            CreatedBy = "tests",
            CreatedOn = DateTime.UtcNow,
            IsActive = true
        });
        db.SetupMasters.Add(new SetupMaster
        {
            SetupId = StatusId,
            SetupType = "OrderStatus",
            SetupCode = "CONFIRMED",
            SetupValue = "Confirmed",
            BusinessUnitId = BusinessUnitId,
            IsActive = true,
            CreatedBy = "tests",
            CreatedOn = DateTime.UtcNow
        });
        db.SaveChanges();
    }

    private static Order NewOrder(long id, string number) => new()
    {
        Id = id,
        OrderNo = number,
        CustomerId = CustomerId,
        BusinessUnitId = BusinessUnitId,
        StatusId = StatusId,
        CurrencyId = CurrencyId,
        OrderDate = DateTime.UtcNow,
        SubTotal = 100m,
        TaxAmount = 5m,
        TotalAmount = 105m,
        BalanceAmount = 105m,
        CreatedBy = "tests",
        CreatedOn = DateTime.UtcNow,
        IsActive = true,
        OrderItems =
        [
            new OrderItem
            {
                ProductId = ProductId,
                Description = "PG AR Product",
                Quantity = 1m,
                UnitPrice = 100m,
                TaxAmount = 5m,
                TotalAmount = 105m,
                CreatedBy = "tests",
                CreatedDate = DateTime.UtcNow,
                IsActive = true
            }
        ]
    };

    private const long BusinessUnitId = 96_001;
    private const long CustomerId = 96_002;
    private const long CurrencyId = 96_003;
    private const long ProductId = 96_004;
    private const long StatusId = 96_005;
    private const long OrderOneId = 96_006;
    private const long OrderTwoId = 96_007;
    private const long OrderThreeId = 96_008;
    private const long OrderFourId = 96_009;
    private const long OrderFiveId = 96_010;
    private const long OrderSixId = 96_011;
    private const long OrderSevenId = 96_012;
}
