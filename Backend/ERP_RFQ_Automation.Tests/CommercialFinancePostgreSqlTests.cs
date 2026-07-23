using ERP_RFQ_Automation.CommercialFinance;
using ERP_RFQ_Automation.BankReconciliation;
using ERP_RFQ_Automation.GeneralLedger;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using System.Text.Json;

namespace ERP_RFQ_Automation.Tests;

[Collection(PostgreSqlIntegrationCollection.Name)]
public sealed class CommercialFinancePostgreSqlTests(PostgreSqlTestDatabase database)
{
    private readonly PostgreSqlTestDatabase _database = database;

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task FinanceExceptions_EnforceConcurrentCeilingsLegalNumbersAndDatabaseGovernance()
    {
        long invoiceId;
        long firstWriteOffId;
        long secondWriteOffId;
        long paymentId;
        long firstRefundId;
        long secondRefundId;
        await using (var seed = _database.ContextFor(null))
        {
            Seed.EnsureBusinessUnit(seed, ExceptionBusinessUnitId);
            Seed.Customer(seed, ExceptionCustomerId, ExceptionBusinessUnitId, "Finance exception customer");
            seed.Currencies.Add(new Currency
            {
                Id = ExceptionCurrencyId, Code = "FXE", CurrencyName = "Finance exception currency", Symbol = "F",
                ExchangeRate = 1m, IsBaseCurrency = true, IsActive = true, CreatedBy = "tests",
                CreatedOn = DateTime.UtcNow, BusinessUnitId = ExceptionBusinessUnitId
            });
            SeedCashPosting(seed, ExceptionBusinessUnitId, ExceptionCurrencyId, 96_400_100);
            seed.Products.Add(new Product
            {
                Id = ExceptionProductId, ProductName = "Finance exception product", PartNo = "FXE-1",
                Buid = ExceptionBusinessUnitId, CreatedBy = "tests", CreatedOn = DateTime.UtcNow, IsActive = true
            });
            seed.SetupMasters.Add(new SetupMaster
            {
                SetupId = ExceptionStatusId, SetupType = "OrderStatus", SetupCode = "CONFIRMED",
                SetupValue = "Confirmed", BusinessUnitId = ExceptionBusinessUnitId, IsActive = true,
                CreatedBy = "tests", CreatedOn = DateTime.UtcNow
            });
            seed.Orders.Add(new Order
            {
                Id = ExceptionOrderId, OrderNo = "ORD-FINANCE-EXCEPTION", CustomerId = ExceptionCustomerId,
                BusinessUnitId = ExceptionBusinessUnitId, StatusId = ExceptionStatusId,
                CurrencyId = ExceptionCurrencyId, OrderDate = DateTime.UtcNow, SubTotal = 100m,
                TotalAmount = 100m, BalanceAmount = 100m, CreatedBy = "tests", CreatedOn = DateTime.UtcNow,
                IsActive = true, OrderItems = [new OrderItem
                {
                    Id = ExceptionOrderLineId, ProductId = ExceptionProductId, Description = "Finance exception product",
                    Quantity = 1m, UnitPrice = 100m, TotalAmount = 100m, CreatedBy = "tests",
                    CreatedDate = DateTime.UtcNow, IsActive = true
                }]
            });
            await seed.SaveChangesAsync();

            var service = new CommercialFinanceApplicationService(seed);
            var draft = await service.CreateInvoiceAsync(ExceptionBusinessUnitId, ExceptionOrderId,
                "pg-finance-exception-invoice", new(null, null, null), "invoice-maker");
            var invoice = await service.IssueAsync(ExceptionBusinessUnitId, draft.Id,
                new(draft.Version), "invoice-checker");
            invoiceId = invoice.Id;
            firstWriteOffId = (await service.CreateWriteOffAsync(ExceptionBusinessUnitId, "pg-write-off-race-1",
                new(null, "BAD_DEBT", "Customer debt is no longer collectible.", "case://wo-1",
                    [new(invoice.Id, 80m)]), "write-off-maker-1")).Id;
            secondWriteOffId = (await service.CreateWriteOffAsync(ExceptionBusinessUnitId, "pg-write-off-race-2",
                new(null, "BAD_DEBT", "Customer debt is no longer collectible.", "case://wo-2",
                    [new(invoice.Id, 80m)]), "write-off-maker-2")).Id;

            paymentId = (await service.PostPaymentAsync(ExceptionBusinessUnitId, "pg-refund-source",
                new(ExceptionCustomerId, null, ExceptionCurrencyId, null, 100m, "BankTransfer", "BANK-PG", []),
                "cashier")).Id;
            firstRefundId = (await service.CreateRefundAsync(ExceptionBusinessUnitId, "pg-refund-race-1",
                new(paymentId, null, 80m, "BankTransfer", "token:acct_pg_1200", true, "OVERPAYMENT",
                    "Verified customer overpayment requires return.", "case://refund-1"), "refund-maker-1")).Id;
            secondRefundId = (await service.CreateRefundAsync(ExceptionBusinessUnitId, "pg-refund-race-2",
                new(paymentId, null, 80m, "BankTransfer", "token:acct_pg_1200", true, "OVERPAYMENT",
                    "Verified customer overpayment requires return.", "case://refund-2"), "refund-maker-2")).Id;
        }

        var writeOffRace = await Task.WhenAll(
            CaptureWriteOffPostAsync(firstWriteOffId), CaptureWriteOffPostAsync(secondWriteOffId));
        var postedWriteOff = Assert.Single(writeOffRace, x => x.Result is not null).Result!;
        Assert.IsType<FinanceConflictException>(Assert.Single(writeOffRace, x => x.Error is not null).Error);
        Assert.Matches($"^WOF-{DateTime.UtcNow.Year}-[0-9]{{6}}$", postedWriteOff.WriteOffNumber!);

        var refundRace = await Task.WhenAll(
            CaptureRefundApprovalAsync(firstRefundId), CaptureRefundApprovalAsync(secondRefundId));
        var approvedRefund = Assert.Single(refundRace, x => x.Result is not null).Result!;
        Assert.IsType<FinanceConflictException>(Assert.Single(refundRace, x => x.Error is not null).Error);
        await using (var release = _database.ContextFor(ExceptionBusinessUnitId))
        {
            approvedRefund = await new CommercialFinanceApplicationService(release).ReleaseRefundAsync(
                ExceptionBusinessUnitId, approvedRefund.Id, new(approvedRefund.Version), "refund-releaser");
        }
        Assert.Matches($"^RFD-{DateTime.UtcNow.Year}-[0-9]{{6}}$", approvedRefund.RefundNumber!);
        await using (var reconcile = _database.ContextFor(ExceptionBusinessUnitId))
        {
            approvedRefund = await new CommercialFinanceApplicationService(reconcile).FailRefundDisbursementAsync(
                ExceptionBusinessUnitId, approvedRefund.Id,
                new(approvedRefund.Version, "provider:failed-pg-1001",
                    "Provider rejected the submitted refund transfer."), "refund-reconciler");
        }
        Assert.Equal("Failed", approvedRefund.PostingStatus);

        await using (var verify = _database.ContextFor(ExceptionBusinessUnitId))
        {
            var service = new CommercialFinanceApplicationService(verify);
            Assert.Equal(20m, (await service.GetWriteOffEligibilityAsync(ExceptionBusinessUnitId, invoiceId)).CurrentBalance);
            Assert.Equal(20m, (await service.GetRefundEligibilityAsync(ExceptionBusinessUnitId, paymentId)).AvailableAmount);
            await Assert.ThrowsAsync<FinanceConflictException>(() => service.ReversePaymentAsync(
                ExceptionBusinessUnitId, paymentId, new(1, "Receipt cannot be reversed after refund release."), "controller"));
        }

        long crossWriteOffId;
        long reversalRacePaymentId;
        long reversalRaceRefundId;
        await using (var prepareRaces = _database.ContextFor(ExceptionBusinessUnitId))
        {
            var service = new CommercialFinanceApplicationService(prepareRaces);
            crossWriteOffId = (await service.CreateWriteOffAsync(ExceptionBusinessUnitId, "pg-payment-write-off-race",
                new(null, "SMALL_BALANCE", "Final collectible balance approved for write-off.", "case://cross-race",
                    [new(invoiceId, 20m)]), "cross-race-maker")).Id;
            reversalRacePaymentId = (await service.PostPaymentAsync(ExceptionBusinessUnitId,
                "pg-refund-reversal-race-source", new(ExceptionCustomerId, null, ExceptionCurrencyId, null,
                    100m, "BankTransfer", "BANK-RACE", []), "race-cashier")).Id;
            reversalRaceRefundId = (await service.CreateRefundAsync(ExceptionBusinessUnitId,
                "pg-refund-reversal-race", new(reversalRacePaymentId, null, 80m, "BankTransfer",
                    "token:acct_race_8080", true, "OVERPAYMENT",
                    "Verified customer overpayment requires return.", "case://refund-race"), "race-refund-maker")).Id;
        }

        var paymentWriteOffRace = await Task.WhenAll(
            CaptureWriteOffPostOutcomeAsync(crossWriteOffId), CapturePaymentAllocationAsync(invoiceId));
        Assert.Single(paymentWriteOffRace, x => x.Succeeded);
        Assert.Single(paymentWriteOffRace, x => x.Error is not null);

        var reversalApprovalRace = await Task.WhenAll(
            CaptureRefundApprovalOutcomeAsync(reversalRaceRefundId),
            CapturePaymentReversalOutcomeAsync(reversalRacePaymentId));
        Assert.Single(reversalApprovalRace, x => x.Succeeded);
        Assert.Single(reversalApprovalRace, x => x.Error is not null);

        await using var connection = await _database.OpenConnectionAsync();
        await using var modules = connection.CreateCommand();
        modules.CommandText = "SELECT count(*) FROM \"Module\" WHERE \"ModuleName\" IN ('Receivable Write-offs', 'Customer Refunds')";
        Assert.Equal(2L, (long)(await modules.ExecuteScalarAsync())!);

        await using var forcedRls = connection.CreateCommand();
        forcedRls.CommandText = """
            SELECT count(*) FROM pg_class
            WHERE relname IN ('CustomerPayments', 'ReceivableWriteOffs', 'WriteOffAllocations', 'CustomerRefunds')
              AND relrowsecurity AND relforcerowsecurity
            """;
        Assert.Equal(4L, (long)(await forcedRls.ExecuteScalarAsync())!);

        await using var mutateAllocation = connection.CreateCommand();
        mutateAllocation.CommandText = "UPDATE \"WriteOffAllocations\" SET \"Amount\" = 1 WHERE \"ReceivableWriteOffId\" = @id";
        mutateAllocation.Parameters.AddWithValue("id", postedWriteOff.Id);
        Assert.Equal(PostgresErrorCodes.ObjectNotInPrerequisiteState,
            (await Assert.ThrowsAsync<PostgresException>(() => mutateAllocation.ExecuteNonQueryAsync())).SqlState);

        await using var evidence = connection.CreateCommand();
        evidence.CommandText = """
            SELECT count(*) FROM "FinanceOutboxMessages"
            WHERE "BusinessUnitId" = @tenant
              AND "EventType" IN ('finance.write-off.posted', 'finance.refund.released')
            """;
        evidence.Parameters.AddWithValue("tenant", ExceptionBusinessUnitId);
        Assert.InRange((long)(await evidence.ExecuteScalarAsync())!, 2L, 3L);

        await using var tenantTransaction = await connection.BeginTransactionAsync();
        await using var crossTenantRead = connection.CreateCommand();
        crossTenantRead.Transaction = tenantTransaction;
        crossTenantRead.CommandText = $"""
            SET LOCAL ROLE nexora_tenant_app;
            SET LOCAL nexora.business_unit_id = '{ExceptionBusinessUnitId + 1}';
            SELECT (SELECT count(*) FROM "ReceivableWriteOffs") +
                   (SELECT count(*) FROM "CustomerRefunds");
            """;
        Assert.Equal(0L, (long)(await crossTenantRead.ExecuteScalarAsync())!);
        await tenantTransaction.RollbackAsync();
    }

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task CashBridge_RejectsWrongOffsetAndExcessSourceJournalLineAtCommit()
    {
        await using (var seed = _database.ContextFor(null))
        {
            Seed.EnsureBusinessUnit(seed, CashBridgeTenantId);
            Seed.Customer(seed, CashBridgeCustomerId, CashBridgeTenantId, "Cash bridge adversarial customer");
            seed.Currencies.Add(new Currency
            {
                Id = CashBridgeCurrencyId, BusinessUnitId = CashBridgeTenantId, Code = "CBX",
                CurrencyName = "Cash bridge currency", Symbol = "C", ExchangeRate = 1m,
                IsBaseCurrency = true, IsActive = true, CreatedBy = "tests", CreatedOn = DateTime.UtcNow
            });
            SeedCashPosting(seed, CashBridgeTenantId, CashBridgeCurrencyId, CashBridgeIdBase);
            seed.LedgerAccounts.Add(new LedgerAccount
            {
                Id = CashBridgeWrongOffsetId, BusinessUnitId = CashBridgeTenantId, Code = "WRONG-OFFSET",
                Name = "Wrong payment offset", Category = LedgerAccountCategories.Revenue,
                NormalBalance = LedgerNormalBalances.Credit, IsControlAccount = false,
                AllowsManualPosting = true, IdempotencyKey = "pg-cash-bridge-wrong-offset",
                RequestHash = new string('8', 64), CreatedBy = "tests", CreatedOn = DateTime.UtcNow
            });
            await seed.SaveChangesAsync();
        }

        await using var connection = await _database.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO "CustomerPayments"
                ("Id","BusinessUnitId","CustomerId","CurrencyId","ReceiptNumber","Status","PaymentDate",
                 "Amount","Method","BankReference","BankAccountId","JournalEntryId","IdempotencyKey",
                 "RequestHash","Version","CreatedBy","CreatedOn")
            VALUES (@payment,@tenant,@customer,@currency,'RCPT-CASH-BRIDGE-BAD','Posted',current_date,
                    100,'BankTransfer','BANK-BAD',@bank,NULL,'pg-cash-bridge-bad',repeat('9',64),1,'cashier@test',now());

            INSERT INTO "JournalEntries"
                ("Id","BusinessUnitId","AccountingPeriodId","FunctionalCurrencyId","AccountingDate","Status",
                 "Description","SourceType","SourceReference","SourceVersion","TotalDebit","TotalCredit",
                 "IdempotencyKey","RequestHash","Version","CreatedBy","CreatedOn")
            VALUES (@journal,@tenant,@period,@currency,current_date,'Draft','Malformed payment source journal',
                    'CustomerPayment',@payment::text,1,100,100,'pg-cash-bridge-bad-journal',repeat('a',64),1,
                    'system:customerpayment',now());

            INSERT INTO "JournalEntryLines"
                ("Id","BusinessUnitId","JournalEntryId","Sequence","LedgerAccountId","Description",
                 "TransactionCurrencyId","ExchangeRate","TransactionDebit","TransactionCredit",
                 "FunctionalDebit","FunctionalCredit","SourceReference")
            VALUES
                (@line1,@tenant,@journal,1,@cash,'Cash',@currency,1,100,0,100,0,'PAY:' || @payment::text || ':BANK'),
                (@line2,@tenant,@journal,2,@wrong,'Wrong offset',@currency,1,0,99,0,99,'PAY:' || @payment::text || ':UNAPPLIED'),
                (@line3,@tenant,@journal,3,@wrong,'Excess line',@currency,1,0,1,0,1,'PAY:' || @payment::text || ':EXCESS');

            UPDATE "JournalEntries" SET "Status" = 'Posted', "PostedBy" = 'journal-checker@test',
                "PostedOn" = now(), "Version" = "Version" + 1 WHERE "Id" = @journal;
            UPDATE "CustomerPayments" SET "JournalEntryId" = @journal WHERE "Id" = @payment;
            """;
        command.Parameters.AddWithValue("payment", CashBridgePaymentId);
        command.Parameters.AddWithValue("journal", CashBridgeJournalId);
        command.Parameters.AddWithValue("line1", CashBridgeJournalId + 1);
        command.Parameters.AddWithValue("line2", CashBridgeJournalId + 2);
        command.Parameters.AddWithValue("line3", CashBridgeJournalId + 3);
        command.Parameters.AddWithValue("tenant", CashBridgeTenantId);
        command.Parameters.AddWithValue("customer", CashBridgeCustomerId);
        command.Parameters.AddWithValue("currency", CashBridgeCurrencyId);
        command.Parameters.AddWithValue("bank", CashBridgeIdBase + 6);
        command.Parameters.AddWithValue("period", CashBridgeIdBase + 5);
        command.Parameters.AddWithValue("cash", CashBridgeIdBase + 1);
        command.Parameters.AddWithValue("wrong", CashBridgeWrongOffsetId);
        await command.ExecuteNonQueryAsync();

        var failure = await Assert.ThrowsAsync<PostgresException>(() => transaction.CommitAsync());
        Assert.Equal(PostgresErrorCodes.CheckViolation, failure.SqlState);
        Assert.Contains("customer payment journal provenance is invalid", failure.MessageText);
    }

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task AccountingBridgeRequired_DirectSqlCannotOptOutOrDowngradePostedPayment()
    {
        await SeedBridgeTenantAsync(BridgeFlagTenantId, BridgeFlagCustomerId, BridgeFlagCurrencyId,
            BridgeFlagIdBase, "BFG");
        await using var connection = await _database.OpenConnectionAsync();
        await using var insert = connection.CreateCommand();
        insert.CommandText = """
            INSERT INTO "CustomerPayments"
                ("BusinessUnitId","CustomerId","CurrencyId","ReceiptNumber","Status","PaymentDate","Amount",
                 "Method","AccountingBridgeRequired","IdempotencyKey","RequestHash","Version","CreatedBy","CreatedOn")
            VALUES (@tenant,@customer,@currency,'RCPT-BRIDGE-OPT-OUT','Posted',current_date,10,
                    'Cash',false,'pg-bridge-opt-out',repeat('a',64),1,'cashier@test',now())
            """;
        insert.Parameters.AddWithValue("tenant", BridgeFlagTenantId);
        insert.Parameters.AddWithValue("customer", BridgeFlagCustomerId);
        insert.Parameters.AddWithValue("currency", BridgeFlagCurrencyId);
        var insertFailure = await Record.ExceptionAsync(() => insert.ExecuteNonQueryAsync());

        long paymentId;
        await using (var context = _database.ContextFor(null))
        {
            paymentId = (await new CommercialFinanceApplicationService(context).PostPaymentAsync(
                BridgeFlagTenantId, "pg-bridge-required-payment",
                new(BridgeFlagCustomerId, null, BridgeFlagCurrencyId, null, 25m,
                    "BankTransfer", "BANK-BRIDGE-TRUE", []), "cashier@test")).Id;
        }
        await using var downgrade = connection.CreateCommand();
        downgrade.CommandText = "UPDATE \"CustomerPayments\" SET \"AccountingBridgeRequired\" = false WHERE \"Id\" = @id";
        downgrade.Parameters.AddWithValue("id", paymentId);
        var downgradeFailure = await Record.ExceptionAsync(() => downgrade.ExecuteNonQueryAsync());

        Assert.Equal(PostgresErrorCodes.CheckViolation, Assert.IsType<PostgresException>(insertFailure).SqlState);
        Assert.Contains(Assert.IsType<PostgresException>(downgradeFailure).SqlState,
            new[] { PostgresErrorCodes.CheckViolation, PostgresErrorCodes.ObjectNotInPrerequisiteState });
    }

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task PaymentReversal_AuditAndOutboxCarryControllerAndJournalBridgeEvidence()
    {
        await SeedBridgeTenantAsync(AuditBridgeTenantId, AuditBridgeCustomerId, AuditBridgeCurrencyId,
            AuditBridgeIdBase, "ABR");
        CustomerPaymentDto reversed;
        await using (var context = _database.ContextFor(null))
        {
            var service = new CommercialFinanceApplicationService(context);
            var payment = await service.PostPaymentAsync(AuditBridgeTenantId, "pg-audit-bridge-payment",
                new(AuditBridgeCustomerId, null, AuditBridgeCurrencyId, null, 75m,
                    "BankTransfer", "BANK-AUDIT-BRIDGE", []), "cashier@test");
            context.ChangeTracker.Clear();
            reversed = await service.ReversePaymentAsync(AuditBridgeTenantId, payment.Id,
                new(payment.Version, "Independent controller reversed duplicate receipt"), "controller@test");
        }

        await using var verify = _database.ContextFor(null);
        var audit = await verify.CommercialFinanceAudits.AsNoTracking().SingleAsync(x =>
            x.BusinessUnitId == AuditBridgeTenantId && x.AggregateType == "CustomerPayment" &&
            x.AggregateId == reversed.Id && x.Action == "Reversed");
        var outbox = await verify.FinanceOutboxMessages.AsNoTracking().SingleAsync(x =>
            x.BusinessUnitId == AuditBridgeTenantId && x.AggregateType == "CustomerPayment" &&
            x.AggregateId == reversed.Id && x.EventType == "finance.payment.reversed");
        using var auditDetail = JsonDocument.Parse(audit.DetailJson);
        using var outboxDetail = JsonDocument.Parse(outbox.Payload);

        Assert.Equal("controller@test", audit.Actor);
        AssertBridgeEvidence(auditDetail.RootElement, reversed);
        AssertBridgeEvidence(outboxDetail.RootElement, reversed);
    }

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task CashBridge_RejectsPaymentAndRefundTransactionCurrencyMismatch()
    {
        await SeedBridgeTenantAsync(CurrencyBridgeTenantId, CurrencyBridgeCustomerId,
            CurrencyBridgeCurrencyId, CurrencyBridgeIdBase, "CBP");
        await using (var seed = _database.ContextFor(null))
        {
            seed.Currencies.Add(new Currency
            {
                Id = CurrencyBridgeOtherCurrencyId, BusinessUnitId = CurrencyBridgeTenantId, Code = "CBO",
                CurrencyName = "Other bridge currency", Symbol = "O", ExchangeRate = 1m,
                IsBaseCurrency = false, IsActive = true, CreatedBy = "tests", CreatedOn = DateTime.UtcNow
            });
            seed.LedgerAccounts.Add(new LedgerAccount
            {
                Id = CurrencyBridgeLooseCashId, BusinessUnitId = CurrencyBridgeTenantId, Code = "LOOSE-CASH",
                Name = "Currency-neutral bank cash", Category = LedgerAccountCategories.Asset,
                NormalBalance = LedgerNormalBalances.Debit, CurrencyId = null, IsControlAccount = false,
                AllowsManualPosting = true, IdempotencyKey = "pg-currency-loose-cash",
                RequestHash = new string('b', 64), CreatedBy = "tests", CreatedOn = DateTime.UtcNow
            });
            await seed.SaveChangesAsync();
            seed.BankAccounts.Add(new BankAccount
            {
                Id = CurrencyBridgeLooseBankId, BusinessUnitId = CurrencyBridgeTenantId,
                Name = "Loose currency bank", InstitutionName = "Integration bank", MaskedAccountNumber = "****8830",
                AccountFingerprint = new string('c', 64), CurrencyId = CurrencyBridgeCurrencyId,
                LedgerAccountId = CurrencyBridgeLooseCashId, Status = BankAccountStatuses.Active,
                OpeningDate = DateTime.UtcNow.Date.AddYears(-1), IdempotencyKey = "pg-currency-loose-bank",
                RequestHash = new string('d', 64), CreatedBy = "tests", CreatedOn = DateTime.UtcNow
            });
            await seed.SaveChangesAsync();
        }

        var paymentFailure = await CommitCurrencyMismatchedPaymentAsync();

        CustomerRefundDto released;
        await using (var context = _database.ContextFor(null))
        {
            var service = new CommercialFinanceApplicationService(context);
            var payment = await service.PostPaymentAsync(CurrencyBridgeTenantId, "pg-currency-refund-source",
                new(CurrencyBridgeCustomerId, null, CurrencyBridgeCurrencyId, null, 60m,
                    "BankTransfer", "BANK-CURRENCY-SOURCE", [], CurrencyBridgeIdBase + 6), "cashier@test");
            var draft = await service.CreateRefundAsync(CurrencyBridgeTenantId, "pg-currency-refund",
                new(payment.Id, null, 30m, "BankTransfer", "token:currency_refund_8830", true,
                    "OVERPAYMENT", "Verified overpayment requires customer refund.", "case://currency-refund",
                    CurrencyBridgeIdBase + 6),
                "refund-maker@test");
            var approved = await service.ApproveRefundAsync(CurrencyBridgeTenantId, draft.Id,
                new(draft.Version), "refund-checker@test");
            released = await service.ReleaseRefundAsync(CurrencyBridgeTenantId, approved.Id,
                new(approved.Version), "refund-releaser@test");
        }
        var refundFailure = await CommitCurrencyMismatchedRefundAsync(released);

        Assert.NotNull(paymentFailure);
        Assert.NotNull(refundFailure);
        Assert.Equal(PostgresErrorCodes.CheckViolation, paymentFailure.SqlState);
        Assert.Equal(PostgresErrorCodes.CheckViolation, refundFailure.SqlState);
    }

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task ReceivableAdjustments_EnforceLegalNumbersNetArCreditCeilingAndAuditPrivileges()
    {
        ReceivableDocumentDto invoice;
        ReceivableDocumentDto credit;
        ReceivableDocumentDto debit;
        long overCreditDraftId;
        long overCreditDraftLineId;
        long firstRaceCreditDraftId;
        long secondRaceCreditDraftId;
        await using (var context = _database.ContextFor(null))
        {
            Seed.EnsureBusinessUnit(context, AdjustmentBusinessUnitId);
            Seed.Customer(context, AdjustmentCustomerId, AdjustmentBusinessUnitId, "Adjustment customer");
            context.Currencies.Add(new Currency
            {
                Id = AdjustmentCurrencyId, Code = "ADJ", CurrencyName = "Adjustment currency", Symbol = "A",
                ExchangeRate = 1m, IsBaseCurrency = true, IsActive = true, CreatedBy = "tests",
                CreatedOn = DateTime.UtcNow, BusinessUnitId = AdjustmentBusinessUnitId
            });
            SeedCashPosting(context, AdjustmentBusinessUnitId, AdjustmentCurrencyId, 96_300_100);
            context.Products.Add(new Product
            {
                Id = AdjustmentProductId, ProductName = "Adjustment product", PartNo = "ADJ-1",
                Buid = AdjustmentBusinessUnitId, CreatedBy = "tests", CreatedOn = DateTime.UtcNow, IsActive = true
            });
            context.SetupMasters.Add(new SetupMaster
            {
                SetupId = AdjustmentStatusId, SetupType = "OrderStatus", SetupCode = "CONFIRMED",
                SetupValue = "Confirmed", BusinessUnitId = AdjustmentBusinessUnitId, IsActive = true,
                CreatedBy = "tests", CreatedOn = DateTime.UtcNow
            });
            var order = new Order
            {
                Id = AdjustmentOrderId, OrderNo = "ORD-ADJUSTMENT-PG", CustomerId = AdjustmentCustomerId,
                BusinessUnitId = AdjustmentBusinessUnitId, StatusId = AdjustmentStatusId,
                CurrencyId = AdjustmentCurrencyId, OrderDate = DateTime.UtcNow, SubTotal = 200m,
                DiscountAmount = 10m, TaxAmount = 19m, TotalAmount = 209m, BalanceAmount = 209m,
                CreatedBy = "tests", CreatedOn = DateTime.UtcNow, IsActive = true,
                OrderItems = [new OrderItem
                {
                    Id = AdjustmentOrderLineId, ProductId = AdjustmentProductId, Description = "Adjustment product",
                    Quantity = 2m, UnitPrice = 100m, Discount = 10m, TaxAmount = 19m, TotalAmount = 209m,
                    CreatedBy = "tests", CreatedDate = DateTime.UtcNow, IsActive = true
                }]
            };
            context.Orders.Add(order);
            await context.SaveChangesAsync();
            var service = new CommercialFinanceApplicationService(context);
            var invoiceDraft = await service.CreateInvoiceAsync(AdjustmentBusinessUnitId, order.Id,
                "pg-adjustment-invoice", new CreateInvoiceRequest(null, null, null), "invoice-maker");
            invoice = await service.IssueAsync(AdjustmentBusinessUnitId, invoiceDraft.Id,
                new(invoiceDraft.Version), "invoice-checker");
            var parentLineId = invoice.Lines.Single().Id;
            var creditDraft = await service.CreateAdjustmentAsync(AdjustmentBusinessUnitId, invoice.Id,
                "pg-adjustment-credit", new(ReceivableDocumentTypes.CreditNote, null, null, "RETURN",
                    "Partial return", [new(parentLineId, 1m)]), "credit-maker");
            credit = await service.IssueAdjustmentAsync(AdjustmentBusinessUnitId, creditDraft.Id,
                new(creditDraft.Version), "credit-checker");
            var debitDraft = await service.CreateAdjustmentAsync(AdjustmentBusinessUnitId, invoice.Id,
                "pg-adjustment-debit", new(ReceivableDocumentTypes.DebitNote, null, null, "CORRECTION",
                    "Underbilling correction", [new(parentLineId, 1m)]), "debit-maker");
            debit = await service.IssueAdjustmentAsync(AdjustmentBusinessUnitId, debitDraft.Id,
                new(debitDraft.Version), "debit-checker");
            var overCredit = await service.CreateAdjustmentAsync(AdjustmentBusinessUnitId, invoice.Id,
                "pg-adjustment-over-credit", new(ReceivableDocumentTypes.CreditNote, null, null, "RETURN",
                    "Excess return", [new(parentLineId, 2m)]), "other-maker");
            overCreditDraftId = overCredit.Id;
            overCreditDraftLineId = overCredit.Lines.Single().Id;

            var open = await service.GetOpenItemsAsync(AdjustmentBusinessUnitId, DateTime.UtcNow);
            Assert.Contains(open, x => x.DocumentId == invoice.Id && x.OutstandingAmount == 104.50m);
            Assert.Contains(open, x => x.DocumentId == debit.Id && x.OutstandingAmount == 104.50m);
            await service.PostPaymentAsync(AdjustmentBusinessUnitId, "pg-adjustment-debit-payment",
                new(AdjustmentCustomerId, null, AdjustmentCurrencyId, null, 104.50m, "BankTransfer", null,
                    [new(debit.Id, 104.50m)]), "collector");
            firstRaceCreditDraftId = (await service.CreateAdjustmentAsync(AdjustmentBusinessUnitId, invoice.Id,
                "pg-adjustment-credit-race-1", new(ReceivableDocumentTypes.CreditNote, null, null, "RETURN",
                    "Concurrent return one", [new(parentLineId, 1m)]), "race-maker-one")).Id;
            secondRaceCreditDraftId = (await service.CreateAdjustmentAsync(AdjustmentBusinessUnitId, invoice.Id,
                "pg-adjustment-credit-race-2", new(ReceivableDocumentTypes.CreditNote, null, null, "RETURN",
                    "Concurrent return two", [new(parentLineId, 1m)]), "race-maker-two")).Id;
        }

        var creditRace = await Task.WhenAll(
            CaptureAdjustmentIssueAsync(firstRaceCreditDraftId),
            CaptureAdjustmentIssueAsync(secondRaceCreditDraftId));
        Assert.Single(creditRace, x => x.Document is not null);
        Assert.IsType<FinanceConflictException>(Assert.Single(creditRace, x => x.Error is not null).Error);

        Assert.Matches($"^CRN-{DateTime.UtcNow.Year}-[0-9]{{6}}$", credit.DocumentNumber!);
        Assert.Matches($"^DBN-{DateTime.UtcNow.Year}-[0-9]{{6}}$", debit.DocumentNumber!);
        await using var connection = await _database.OpenConnectionAsync();
        await using var directOverCredit = connection.CreateCommand();
        directOverCredit.CommandText = """
            UPDATE "ReceivableDocuments" SET "Status" = 'Issued', "IssuedOn" = now(),
                "IssuedBy" = 'direct-checker', "Version" = "Version" + 1
            WHERE "Id" = @id
            """;
        directOverCredit.Parameters.AddWithValue("id", overCreditDraftId);
        Assert.Equal(PostgresErrorCodes.CheckViolation,
            (await Assert.ThrowsAsync<PostgresException>(() => directOverCredit.ExecuteNonQueryAsync())).SqlState);

        await using var mutateDraft = connection.CreateCommand();
        mutateDraft.CommandText = "UPDATE \"ReceivableDocuments\" SET \"CreatedBy\" = 'forged-maker' WHERE \"Id\" = @id";
        mutateDraft.Parameters.AddWithValue("id", overCreditDraftId);
        Assert.Equal(PostgresErrorCodes.ObjectNotInPrerequisiteState,
            (await Assert.ThrowsAsync<PostgresException>(() => mutateDraft.ExecuteNonQueryAsync())).SqlState);

        await using var mutateDraftLine = connection.CreateCommand();
        mutateDraftLine.CommandText = "UPDATE \"ReceivableDocumentLines\" SET \"Description\" = 'forged source' WHERE \"Id\" = @id";
        mutateDraftLine.Parameters.AddWithValue("id", overCreditDraftLineId);
        Assert.Equal(PostgresErrorCodes.ObjectNotInPrerequisiteState,
            (await Assert.ThrowsAsync<PostgresException>(() => mutateDraftLine.ExecuteNonQueryAsync())).SqlState);

        await using var auditTransaction = await connection.BeginTransactionAsync();
        await using var forgedAudit = connection.CreateCommand();
        forgedAudit.Transaction = auditTransaction;
        forgedAudit.CommandText = $"""
            SET LOCAL ROLE nexora_tenant_app;
            SET LOCAL nexora.business_unit_id = '{AdjustmentBusinessUnitId}';
            INSERT INTO "CommercialFinanceAudits"
                ("BusinessUnitId", "AggregateType", "AggregateId", "Action", "Actor", "OccurredOn", "DetailJson")
            VALUES ({AdjustmentBusinessUnitId}, 'ReceivableDocument', {invoice.Id}, 'Forged', 'attacker', now(), jsonb_build_object())
            """;
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege,
            (await Assert.ThrowsAsync<PostgresException>(() => forgedAudit.ExecuteNonQueryAsync())).SqlState);
        await auditTransaction.RollbackAsync();

        await using var writerTransaction = await connection.BeginTransactionAsync();
        await using var forgedWriterCall = connection.CreateCommand();
        forgedWriterCall.Transaction = writerTransaction;
        forgedWriterCall.CommandText = $"""
            SET LOCAL ROLE nexora_tenant_app;
            SET LOCAL nexora.business_unit_id = '{AdjustmentBusinessUnitId}';
            SELECT public.nexora_write_finance_audit({AdjustmentBusinessUnitId}, 'ReceivableDocument',
                {invoice.Id}, 'Issued', 'attacker', jsonb_build_object('forged', true), now()::timestamp without time zone)
            """;
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege,
            (await Assert.ThrowsAsync<PostgresException>(() => forgedWriterCall.ExecuteNonQueryAsync())).SqlState);
        await writerTransaction.RollbackAsync();
    }

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

        await using var finalizedOutbox = connection.CreateCommand();
        finalizedOutbox.CommandText = """
            SELECT count(*) FROM "FinanceOutboxMessages"
            WHERE "BusinessUnitId" = @tenant
              AND "EventType" IN ('finance.receivable.issued', 'finance.receivable.cancelled')
            """;
        finalizedOutbox.Parameters.AddWithValue("tenant", BusinessUnitId);
        Assert.Equal(5L, (long)(await finalizedOutbox.ExecuteScalarAsync())!);

        await using var rewriteOutbox = connection.CreateCommand();
        rewriteOutbox.CommandText = """
            UPDATE "FinanceOutboxMessages" SET "Payload" = '{}'::jsonb
            WHERE "BusinessUnitId" = @tenant AND "EventType" = 'finance.receivable.issued'
            """;
        rewriteOutbox.Parameters.AddWithValue("tenant", BusinessUnitId);
        Assert.Equal("55000", (await Assert.ThrowsAsync<PostgresException>(
            () => rewriteOutbox.ExecuteNonQueryAsync())).SqlState);

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
        wrongOrderItem.Parameters.AddWithValue("documentId", directOverInvoiceDraftId);
        wrongOrderItem.Parameters.AddWithValue("orderItemId", wrongOrderItemId);
        Assert.Equal(PostgresErrorCodes.ObjectNotInPrerequisiteState,
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

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task FinanceOutbox_ClaimsConcurrentlyReclaimsExpiryAndDeniesTenantStateWrites()
    {
        var otherTenantId = OutboxTenantId + 1;
        await using (var seed = _database.ContextFor(null))
        {
            Seed.EnsureBusinessUnit(seed, OutboxTenantId);
            Seed.EnsureBusinessUnit(seed, otherTenantId);
            var now = DateTime.UtcNow;
            seed.FinanceOutboxMessages.AddRange(Enumerable.Range(1, 4).Select(index => new FinanceOutboxMessage
            {
                BusinessUnitId = OutboxTenantId,
                AggregateType = "ReceivableDocument",
                AggregateId = OutboxAggregateBase + index,
                AggregateVersion = 1,
                EventType = "finance.test.ready",
                Payload = "{}",
                OccurredOn = now,
                AvailableOn = now
            }));
            seed.FinanceOutboxMessages.Add(new FinanceOutboxMessage
            {
                BusinessUnitId = otherTenantId,
                AggregateType = "ReceivableDocument",
                AggregateId = OutboxAggregateBase + 100,
                AggregateVersion = 1,
                EventType = "finance.test.other-tenant",
                Payload = "{}",
                OccurredOn = now,
                AvailableOn = now
            });
            await seed.SaveChangesAsync();
        }

        await using var contextA = _database.ContextFor(OutboxTenantId);
        await using var contextB = _database.ContextFor(OutboxTenantId);
        var storeA = new FinanceOutboxStore(contextA);
        var storeB = new FinanceOutboxStore(contextB);
        var claims = await Task.WhenAll(
            storeA.ClaimAsync("pg-worker-a", 4, TimeSpan.FromMinutes(1), default),
            storeB.ClaimAsync("pg-worker-b", 4, TimeSpan.FromMinutes(1), default));
        Assert.Equal(4, claims.SelectMany(x => x).Select(x => x.Id).Distinct().Count());
        Assert.Empty(claims[0].Select(x => x.Id).Intersect(claims[1].Select(x => x.Id)));

        var expiring = claims.SelectMany(x => x).First();
        await using (var expire = _database.ContextFor(OutboxTenantId))
        {
            await expire.FinanceOutboxMessages.Where(x => x.Id == expiring.Id)
                .ExecuteUpdateAsync(setters => setters.SetProperty(x => x.LeaseUntil, DateTime.UtcNow.AddMinutes(-1)));
        }
        await using (var reclaimContext = _database.ContextFor(OutboxTenantId))
        {
            var reclaimed = await new FinanceOutboxStore(reclaimContext)
                .ClaimAsync("pg-worker-c", 4, TimeSpan.FromMinutes(1), default);
            Assert.Contains(reclaimed, x => x.Id == expiring.Id && x.LeaseToken != expiring.LeaseToken);
        }

        await using var connection = await _database.OpenConnectionAsync();
        await using (var tenantRead = await connection.BeginTransactionAsync())
        {
            await using var command = connection.CreateCommand();
            command.Transaction = tenantRead;
            command.CommandText = $"""
                SET LOCAL ROLE nexora_tenant_app;
                SET LOCAL nexora.business_unit_id = '{OutboxTenantId}';
                SELECT count(*) FROM "FinanceOutboxMessages";
                """;
            Assert.Equal(4L, (long)(await command.ExecuteScalarAsync())!);
            await tenantRead.RollbackAsync();
        }

        await AssertTenantOutboxWriteDeniedAsync(connection,
            "UPDATE \"FinanceOutboxMessages\" SET \"ProcessedOn\" = now() WHERE \"BusinessUnitId\" = " + OutboxTenantId);
        await AssertTenantOutboxWriteDeniedAsync(connection, $"""
            INSERT INTO "FinanceOutboxMessages"
                ("BusinessUnitId", "EventId", "AggregateType", "AggregateId", "AggregateVersion",
                 "EventType", "Payload", "SchemaVersion", "OccurredOn", "AvailableOn", "AttemptCount")
            VALUES ({OutboxTenantId}, gen_random_uuid(), 'ReceivableDocument', {OutboxAggregateBase + 999}, 1,
                'finance.receivable.issued', jsonb_build_object(), 1, now(), now(), 0)
            """);
    }

    private static async Task AssertTenantOutboxWriteDeniedAsync(NpgsqlConnection connection, string statement)
    {
        await using var transaction = await connection.BeginTransactionAsync();
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"""
            SET LOCAL ROLE nexora_tenant_app;
            SET LOCAL nexora.business_unit_id = '{OutboxTenantId}';
            {statement};
            """;
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege,
            (await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync())).SqlState);
        await transaction.RollbackAsync();
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

    private async Task<(ReceivableDocumentDto? Document, Exception? Error)> CaptureAdjustmentIssueAsync(long documentId)
    {
        try
        {
            await using var context = _database.ContextFor(AdjustmentBusinessUnitId);
            var document = await new CommercialFinanceApplicationService(context).IssueAdjustmentAsync(
                AdjustmentBusinessUnitId, documentId, new IssueDocumentRequest(1), "race-checker");
            return (document, null);
        }
        catch (Exception exception) { return (null, exception); }
    }

    private async Task<(ReceivableWriteOffDto? Result, Exception? Error)> CaptureWriteOffPostAsync(long id)
    {
        try
        {
            await using var context = _database.ContextFor(ExceptionBusinessUnitId);
            var result = await new CommercialFinanceApplicationService(context).PostWriteOffAsync(
                ExceptionBusinessUnitId, id, new(1), "write-off-checker");
            return (result, null);
        }
        catch (Exception exception) { return (null, exception); }
    }

    private async Task<(CustomerRefundDto? Result, Exception? Error)> CaptureRefundApprovalAsync(long id)
    {
        try
        {
            await using var context = _database.ContextFor(ExceptionBusinessUnitId);
            var result = await new CommercialFinanceApplicationService(context).ApproveRefundAsync(
                ExceptionBusinessUnitId, id, new(1), "refund-approver");
            return (result, null);
        }
        catch (Exception exception) { return (null, exception); }
    }

    private async Task<(bool Succeeded, Exception? Error)> CapturePaymentAllocationAsync(long documentId)
    {
        try
        {
            await using var context = _database.ContextFor(ExceptionBusinessUnitId);
            await new CommercialFinanceApplicationService(context).PostPaymentAsync(ExceptionBusinessUnitId,
                "pg-payment-write-off-race-payment", new(ExceptionCustomerId, null, ExceptionCurrencyId, null,
                    20m, "BankTransfer", "BANK-CROSS-RACE", [new(documentId, 20m)]), "cross-race-cashier");
            return (true, null);
        }
        catch (Exception exception) { return (false, exception); }
    }

    private async Task<(bool Succeeded, Exception? Error)> CaptureWriteOffPostOutcomeAsync(long writeOffId)
    {
        try
        {
            await using var context = _database.ContextFor(ExceptionBusinessUnitId);
            await new CommercialFinanceApplicationService(context).PostWriteOffAsync(
                ExceptionBusinessUnitId, writeOffId, new(1), "cross-race-checker");
            return (true, null);
        }
        catch (Exception exception) { return (false, exception); }
    }

    private async Task<(bool Succeeded, Exception? Error)> CaptureRefundApprovalOutcomeAsync(long refundId)
    {
        try
        {
            await using var context = _database.ContextFor(ExceptionBusinessUnitId);
            await new CommercialFinanceApplicationService(context).ApproveRefundAsync(
                ExceptionBusinessUnitId, refundId, new(1), "race-refund-approver");
            return (true, null);
        }
        catch (Exception exception) { return (false, exception); }
    }

    private async Task<(bool Succeeded, Exception? Error)> CapturePaymentReversalOutcomeAsync(long paymentId)
    {
        try
        {
            await using var context = _database.ContextFor(ExceptionBusinessUnitId);
            await new CommercialFinanceApplicationService(context).ReversePaymentAsync(
                ExceptionBusinessUnitId, paymentId, new(1, "Concurrent refund approval reversal request"),
                "race-payment-reverser");
            return (true, null);
        }
        catch (Exception exception) { return (false, exception); }
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
        SeedCashPosting(db, BusinessUnitId, CurrencyId, 96_001_100);
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

    private static void SeedCashPosting(
        ErpRfqAutomationContext db,
        long businessUnitId,
        long currencyId,
        long idBase)
    {
        var cashAccountId = idBase + 1;
        var receivablesAccountId = idBase + 2;
        var unappliedCashAccountId = idBase + 3;
        db.LedgerAccounts.AddRange(
            new LedgerAccount
            {
                Id = cashAccountId, BusinessUnitId = businessUnitId, Code = $"CASH-{businessUnitId}",
                Name = "Operating cash", Category = LedgerAccountCategories.Asset,
                NormalBalance = LedgerNormalBalances.Debit, CurrencyId = currencyId,
                IsControlAccount = true, AllowsManualPosting = false,
                IdempotencyKey = $"pg-cash-{businessUnitId}", RequestHash = new string('1', 64),
                CreatedBy = "tests", CreatedOn = DateTime.UtcNow
            },
            new LedgerAccount
            {
                Id = receivablesAccountId, BusinessUnitId = businessUnitId, Code = $"AR-{businessUnitId}",
                Name = "Trade receivables", Category = LedgerAccountCategories.Asset,
                NormalBalance = LedgerNormalBalances.Debit, IsControlAccount = true,
                AllowsManualPosting = false, IdempotencyKey = $"pg-ar-{businessUnitId}",
                RequestHash = new string('2', 64), CreatedBy = "tests", CreatedOn = DateTime.UtcNow
            },
            new LedgerAccount
            {
                Id = unappliedCashAccountId, BusinessUnitId = businessUnitId, Code = $"UNAP-{businessUnitId}",
                Name = "Unapplied cash", Category = LedgerAccountCategories.Liability,
                NormalBalance = LedgerNormalBalances.Credit, IsControlAccount = false,
                AllowsManualPosting = false, IdempotencyKey = $"pg-unapplied-{businessUnitId}",
                RequestHash = new string('3', 64), CreatedBy = "tests", CreatedOn = DateTime.UtcNow
            });
        db.LedgerBooks.Add(new LedgerBook
        {
            Id = idBase + 4, BusinessUnitId = businessUnitId, Name = "Finance integration ledger",
            FunctionalCurrencyId = currencyId, TimeZoneId = "UTC", FiscalYearStartMonth = 1,
            ReceivablesControlAccountId = receivablesAccountId,
            UnappliedCashAccountId = unappliedCashAccountId,
            IdempotencyKey = $"pg-book-{businessUnitId}", RequestHash = new string('4', 64),
            CreatedBy = "tests", CreatedOn = DateTime.UtcNow
        });
        db.SaveChanges();
        db.AccountingPeriods.Add(new AccountingPeriod
        {
            Id = idBase + 5, BusinessUnitId = businessUnitId, FiscalYear = DateTime.UtcNow.Year,
            PeriodNumber = 1, Name = "Finance integration period",
            StartsOn = DateTime.UtcNow.Date.AddYears(-2), EndsOn = DateTime.UtcNow.Date.AddYears(2),
            Status = AccountingPeriodStatuses.Open, IdempotencyKey = $"pg-period-{businessUnitId}",
            RequestHash = new string('5', 64), CreatedBy = "tests", CreatedOn = DateTime.UtcNow
        });
        db.BankAccounts.Add(new BankAccount
        {
            Id = idBase + 6, BusinessUnitId = businessUnitId, Name = "Operating bank",
            InstitutionName = "Integration bank", MaskedAccountNumber = "****4242",
            AccountFingerprint = new string('6', 64), CurrencyId = currencyId,
            LedgerAccountId = cashAccountId, Status = BankAccountStatuses.Active,
            OpeningDate = DateTime.UtcNow.Date.AddYears(-2),
            IdempotencyKey = $"pg-bank-{businessUnitId}", RequestHash = new string('7', 64),
            CreatedBy = "tests", CreatedOn = DateTime.UtcNow
        });
    }

    private async Task SeedBridgeTenantAsync(long tenantId, long customerId, long currencyId,
        long idBase, string currencyCode)
    {
        await using var db = _database.ContextFor(null);
        Seed.EnsureBusinessUnit(db, tenantId);
        Seed.Customer(db, customerId, tenantId, $"Bridge customer {tenantId}");
        db.Currencies.Add(new Currency
        {
            Id = currencyId, BusinessUnitId = tenantId, Code = currencyCode,
            CurrencyName = $"Bridge currency {tenantId}", Symbol = currencyCode[..1], ExchangeRate = 1m,
            IsBaseCurrency = true, IsActive = true, CreatedBy = "tests", CreatedOn = DateTime.UtcNow
        });
        SeedCashPosting(db, tenantId, currencyId, idBase);
        await db.SaveChangesAsync();
    }

    private static void AssertBridgeEvidence(JsonElement payload, CustomerPaymentDto payment)
    {
        Assert.Equal(payment.BankAccountId, payload.GetProperty("BankAccountId").GetInt64());
        Assert.Equal(payment.JournalEntryId, payload.GetProperty("JournalEntryId").GetInt64());
        Assert.Equal(payment.ReversalJournalEntryId, payload.GetProperty("ReversalJournalEntryId").GetInt64());
    }

    private async Task<PostgresException?> CommitCurrencyMismatchedPaymentAsync()
    {
        await using var connection = await _database.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        try
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO "CustomerPayments"
                    ("Id","BusinessUnitId","CustomerId","CurrencyId","ReceiptNumber","Status","PaymentDate",
                     "Amount","Method","BankAccountId","JournalEntryId","AccountingBridgeRequired",
                     "IdempotencyKey","RequestHash","Version","CreatedBy","CreatedOn")
                VALUES (@payment,@tenant,@customer,@currency,'RCPT-CURRENCY-MISMATCH','Posted',current_date,
                        40,'BankTransfer',@bank,NULL,true,'pg-currency-mismatch-payment',repeat('e',64),1,'cashier@test',now());
                INSERT INTO "JournalEntries"
                    ("Id","BusinessUnitId","AccountingPeriodId","FunctionalCurrencyId","AccountingDate","Status",
                     "Description","SourceType","SourceReference","SourceVersion","TotalDebit","TotalCredit",
                     "IdempotencyKey","RequestHash","Version","CreatedBy","CreatedOn")
                VALUES (@journal,@tenant,@period,@currency,current_date,'Draft','Currency mismatched payment',
                        'CustomerPayment',@payment::text,1,40,40,'pg-currency-mismatch-payment-journal',
                        repeat('f',64),1,'system:customerpayment',now());
                INSERT INTO "JournalEntryLines"
                    ("Id","BusinessUnitId","JournalEntryId","Sequence","LedgerAccountId","Description",
                     "TransactionCurrencyId","ExchangeRate","TransactionDebit","TransactionCredit",
                     "FunctionalDebit","FunctionalCredit","SourceReference")
                VALUES
                    (@line1,@tenant,@journal,1,@cash,'Cash',@otherCurrency,1,40,0,40,0,
                     'PAY:' || @payment::text || ':BANK'),
                    (@line2,@tenant,@journal,2,@unapplied,'Unapplied',@otherCurrency,1,0,40,0,40,
                     'PAY:' || @payment::text || ':UNAPPLIED');
                UPDATE "JournalEntries" SET "Status"='Posted',"PostedBy"='journal-checker@test',
                    "PostedOn"=now(),"Version"="Version"+1 WHERE "Id"=@journal;
                UPDATE "CustomerPayments" SET "JournalEntryId"=@journal WHERE "Id"=@payment;
                """;
            command.Parameters.AddWithValue("payment", CurrencyBridgePaymentId);
            command.Parameters.AddWithValue("journal", CurrencyBridgePaymentJournalId);
            command.Parameters.AddWithValue("line1", CurrencyBridgePaymentJournalId + 1);
            command.Parameters.AddWithValue("line2", CurrencyBridgePaymentJournalId + 2);
            command.Parameters.AddWithValue("tenant", CurrencyBridgeTenantId);
            command.Parameters.AddWithValue("customer", CurrencyBridgeCustomerId);
            command.Parameters.AddWithValue("currency", CurrencyBridgeCurrencyId);
            command.Parameters.AddWithValue("otherCurrency", CurrencyBridgeOtherCurrencyId);
            command.Parameters.AddWithValue("bank", CurrencyBridgeLooseBankId);
            command.Parameters.AddWithValue("cash", CurrencyBridgeLooseCashId);
            command.Parameters.AddWithValue("unapplied", CurrencyBridgeIdBase + 3);
            command.Parameters.AddWithValue("period", CurrencyBridgeIdBase + 5);
            await command.ExecuteNonQueryAsync();
            await transaction.CommitAsync();
            return null;
        }
        catch (PostgresException exception) { return exception; }
    }

    private async Task<PostgresException?> CommitCurrencyMismatchedRefundAsync(CustomerRefundDto refund)
    {
        await using var connection = await _database.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        try
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO "JournalEntries"
                    ("Id","BusinessUnitId","AccountingPeriodId","FunctionalCurrencyId","AccountingDate","Status",
                     "Description","SourceType","SourceReference","SourceVersion","TotalDebit","TotalCredit",
                     "IdempotencyKey","RequestHash","Version","CreatedBy","CreatedOn")
                VALUES (@journal,@tenant,@period,@currency,current_date,'Draft','Currency mismatched refund',
                        'CustomerRefund',@refund::text,@sourceVersion,@amount,@amount,
                        'pg-currency-mismatch-refund-journal',repeat('1',64),1,'system:customerrefund',now());
                INSERT INTO "JournalEntryLines"
                    ("Id","BusinessUnitId","JournalEntryId","Sequence","LedgerAccountId","Description",
                     "TransactionCurrencyId","ExchangeRate","TransactionDebit","TransactionCredit",
                     "FunctionalDebit","FunctionalCredit","SourceReference")
                VALUES
                    (@line1,@tenant,@journal,1,@unapplied,'Refund liability',@otherCurrency,1,@amount,0,@amount,0,
                     'REF:' || @refund::text || ':UNAPPLIED'),
                    (@line2,@tenant,@journal,2,@cash,'Refund cash',@otherCurrency,1,0,@amount,0,@amount,
                     'REF:' || @refund::text || ':BANK');
                UPDATE "JournalEntries" SET "Status"='Posted',"PostedBy"='refund-reconciler@test',
                    "PostedOn"=now(),"Version"="Version"+1 WHERE "Id"=@journal;
                UPDATE "CustomerRefunds" SET "PostingStatus"='Settled',"JournalReference"='provider:currency-mismatch',
                    "BankAccountId"=@bank,"JournalEntryId"=@journal,"DisbursementUpdatedBy"='refund-reconciler@test',
                    "DisbursementUpdatedOn"=now(),"Version"="Version"+1 WHERE "Id"=@refund;
                """;
            command.Parameters.AddWithValue("journal", CurrencyBridgeRefundJournalId);
            command.Parameters.AddWithValue("line1", CurrencyBridgeRefundJournalId + 1);
            command.Parameters.AddWithValue("line2", CurrencyBridgeRefundJournalId + 2);
            command.Parameters.AddWithValue("tenant", CurrencyBridgeTenantId);
            command.Parameters.AddWithValue("currency", CurrencyBridgeCurrencyId);
            command.Parameters.AddWithValue("otherCurrency", CurrencyBridgeOtherCurrencyId);
            command.Parameters.AddWithValue("refund", refund.Id);
            command.Parameters.AddWithValue("sourceVersion", refund.Version + 1);
            command.Parameters.AddWithValue("amount", refund.Amount);
            command.Parameters.AddWithValue("bank", CurrencyBridgeLooseBankId);
            command.Parameters.AddWithValue("cash", CurrencyBridgeLooseCashId);
            command.Parameters.AddWithValue("unapplied", CurrencyBridgeIdBase + 3);
            command.Parameters.AddWithValue("period", CurrencyBridgeIdBase + 5);
            await command.ExecuteNonQueryAsync();
            await transaction.CommitAsync();
            return null;
        }
        catch (PostgresException exception) { return exception; }
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
    private const long OutboxTenantId = 96_100;
    private const long CashBridgeTenantId = 96_800;
    private const long CashBridgeCustomerId = 96_801;
    private const long CashBridgeCurrencyId = 96_802;
    private const long CashBridgeIdBase = 96_800_100;
    private const long CashBridgeWrongOffsetId = 96_800_107;
    private const long CashBridgePaymentId = 96_800_201;
    private const long CashBridgeJournalId = 96_800_301;
    private const long BridgeFlagTenantId = 96_810;
    private const long BridgeFlagCustomerId = 96_811;
    private const long BridgeFlagCurrencyId = 96_812;
    private const long BridgeFlagIdBase = 96_810_100;
    private const long AuditBridgeTenantId = 96_820;
    private const long AuditBridgeCustomerId = 96_821;
    private const long AuditBridgeCurrencyId = 96_822;
    private const long AuditBridgeIdBase = 96_820_100;
    private const long CurrencyBridgeTenantId = 96_830;
    private const long CurrencyBridgeCustomerId = 96_831;
    private const long CurrencyBridgeCurrencyId = 96_832;
    private const long CurrencyBridgeOtherCurrencyId = 96_833;
    private const long CurrencyBridgeIdBase = 96_830_100;
    private const long CurrencyBridgeLooseCashId = 96_830_107;
    private const long CurrencyBridgeLooseBankId = 96_830_108;
    private const long CurrencyBridgePaymentId = 96_830_201;
    private const long CurrencyBridgePaymentJournalId = 96_830_301;
    private const long CurrencyBridgeRefundJournalId = 96_830_401;
    private const long OutboxAggregateBase = 96_200;
    private const long AdjustmentBusinessUnitId = 96_300;
    private const long AdjustmentCustomerId = 96_301;
    private const long AdjustmentCurrencyId = 96_302;
    private const long AdjustmentProductId = 96_303;
    private const long AdjustmentStatusId = 96_304;
    private const long AdjustmentOrderId = 96_305;
    private const long AdjustmentOrderLineId = 96_306;
    private const long ExceptionBusinessUnitId = 96_400;
    private const long ExceptionCustomerId = 96_401;
    private const long ExceptionCurrencyId = 96_402;
    private const long ExceptionProductId = 96_403;
    private const long ExceptionStatusId = 96_404;
    private const long ExceptionOrderId = 96_405;
    private const long ExceptionOrderLineId = 96_406;
}
