using System.Security.Cryptography;
using System.Text;
using ERP_RFQ_Automation.BankReconciliation;
using ERP_RFQ_Automation.BankReconciliation.Services;
using ERP_RFQ_Automation.GeneralLedger;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Tests.Support;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.Tests;

public sealed class BankAdjustmentServiceTests
{
    [Theory]
    [InlineData("125.00", "125.00", "0.00", "0.00", "125.00")]
    [InlineData("-125.00", "0.00", "125.00", "125.00", "0.00")]
    public async Task Approval_PostsBalancedDebitCreditJournal(string signedAmountText,
        string expectedCashDebitText, string expectedCashCreditText,
        string expectedDistributionDebitText, string expectedDistributionCreditText)
    {
        using var database = new TestDb();
        await using var db = await CreateFixtureAsync(database);
        var signedAmount = decimal.Parse(signedAmountText, System.Globalization.CultureInfo.InvariantCulture);
        var fixture = await CreateAdjustmentAsync(db, signedAmount, "posting");

        var submitted = await fixture.Service.TransitionAsync(TenantId, fixture.Adjustment.Id, "submit",
            new(fixture.Adjustment.Version), "adjustment-maker@test");
        await Assert.ThrowsAsync<BankReconciliationConflictException>(() => fixture.Service.TransitionAsync(
            TenantId, submitted.Id, "approve", new(submitted.Version), "adjustment-maker@test"));
        var posted = await fixture.Service.TransitionAsync(TenantId, submitted.Id, "approve",
            new(submitted.Version), "adjustment-checker@test");
        var approvalReplay = await fixture.Service.TransitionAsync(TenantId, submitted.Id, "approve",
            new(submitted.Version), "adjustment-checker@test");
        var journal = await db.JournalEntries.AsNoTracking().Include(x => x.Lines)
            .SingleAsync(x => x.Id == posted.JournalEntryId);
        var cash = journal.Lines.Single(x => x.LedgerAccountId == fixture.CashAccountId);
        var distribution = journal.Lines.Single(x => x.LedgerAccountId == fixture.DistributionAccountId);

        Assert.Equal(BankAdjustmentStatuses.Posted, posted.Status);
        Assert.Equal(posted.Version, approvalReplay.Version);
        Assert.Single(await db.JournalEntries.Where(x => x.SourceType == "BankAdjustment").ToListAsync());
        Assert.Equal("adjustment-checker@test", posted.ApprovedBy);
        Assert.Equal(JournalEntryStatuses.Posted, journal.Status);
        Assert.Equal("BankAdjustment", journal.SourceType);
        Assert.Equal(125m, journal.TotalDebit);
        Assert.Equal(125m, journal.TotalCredit);
        Assert.Equal(decimal.Parse(expectedCashDebitText), cash.FunctionalDebit);
        Assert.Equal(decimal.Parse(expectedCashCreditText), cash.FunctionalCredit);
        Assert.Equal(decimal.Parse(expectedDistributionDebitText), distribution.FunctionalDebit);
        Assert.Equal(decimal.Parse(expectedDistributionCreditText), distribution.FunctionalCredit);
        Assert.Equal(cash.Id, posted.BankJournalEntryLineId);
    }

    [Fact]
    public async Task Reversal_RequiresIndependentControllerAndExactlyReversesJournal()
    {
        using var database = new TestDb();
        await using var db = await CreateFixtureAsync(database);
        var fixture = await CreateAdjustmentAsync(db, -80m, "reversal");
        var submitted = await fixture.Service.TransitionAsync(TenantId, fixture.Adjustment.Id, "submit",
            new(fixture.Adjustment.Version), "adjustment-maker@test");
        var posted = await fixture.Service.TransitionAsync(TenantId, submitted.Id, "approve",
            new(submitted.Version), "adjustment-checker@test");

        await Assert.ThrowsAsync<BankReconciliationConflictException>(() => fixture.Service.TransitionAsync(
            TenantId, posted.Id, "reverse", Reversal(posted.Version), "adjustment-checker@test"));
        var reversed = await fixture.Service.TransitionAsync(TenantId, posted.Id, "reverse",
            Reversal(posted.Version), "adjustment-controller@test");
        var original = await db.JournalEntries.AsNoTracking().Include(x => x.Lines)
            .SingleAsync(x => x.Id == posted.JournalEntryId);
        var reversal = await db.JournalEntries.AsNoTracking().Include(x => x.Lines)
            .SingleAsync(x => x.Id == reversed.ReversalJournalEntryId);

        Assert.Equal(BankAdjustmentStatuses.Reversed, reversed.Status);
        Assert.Equal("adjustment-controller@test", reversed.ReversedBy);
        Assert.Equal(JournalEntryStatuses.Reversed, original.Status);
        Assert.Equal(JournalEntryStatuses.Posted, reversal.Status);
        Assert.Equal(original.Id, reversal.ReversesJournalEntryId);
        foreach (var originalLine in original.Lines)
        {
            var reversalLine = reversal.Lines.Single(x => x.Sequence == originalLine.Sequence);
            Assert.Equal(originalLine.FunctionalDebit, reversalLine.FunctionalCredit);
            Assert.Equal(originalLine.FunctionalCredit, reversalLine.FunctionalDebit);
            Assert.Equal(originalLine.TransactionDebit, reversalLine.TransactionCredit);
            Assert.Equal(originalLine.TransactionCredit, reversalLine.TransactionDebit);
        }
    }

    [Fact]
    public async Task Creation_RejectsControlAccountDistribution()
    {
        using var database = new TestDb();
        await using var db = await CreateFixtureAsync(database);
        var ledger = new GeneralLedgerService(db);
        var control = await ledger.CreateAccountAsync(TenantId, "adjustment-control-account",
            new("1100", "Receivables control", LedgerAccountCategories.Asset,
                LedgerNormalBalances.Debit, null, true, false), "controller@test");
        var setup = await CreateStatementAsync(db, 50m, "control-rejection");
        var service = new BankAdjustmentService(db, new InternalSourceJournalPostingService(db));

        await Assert.ThrowsAsync<ArgumentException>(() => service.CreateAsync(TenantId,
            "adjustment-control-rejection", AdjustmentRequest(setup, control.Id, 50m),
            "adjustment-maker@test"));
        Assert.Empty(await db.BankAdjustments.ToListAsync());
    }

    private static BankAdjustmentActionRequest Reversal(long version)
        => new(version, "Independent controller reversal after evidence review", "evidence/reversal-review");

    private static async Task<AdjustmentFixture> CreateAdjustmentAsync(ErpRfqAutomationContext db,
        decimal signedAmount, string key)
    {
        var setup = await CreateStatementAsync(db, signedAmount, key);
        var distributionId = db.LedgerAccounts.Single(x => x.BusinessUnitId == TenantId && x.Code == "6000").Id;
        var service = new BankAdjustmentService(db, new InternalSourceJournalPostingService(db));
        var adjustment = await service.CreateAsync(TenantId, $"adjustment-{key}",
            AdjustmentRequest(setup, distributionId, Math.Abs(signedAmount)), "adjustment-maker@test");
        return new(service, adjustment, setup.CashAccountId, distributionId);
    }

    private static CreateBankAdjustmentRequest AdjustmentRequest(StatementFixture setup,
        long distributionAccountId, decimal amount)
        => new(setup.BankAccountId, setup.StatementLineId, setup.PeriodId, Today, "BankFee",
            "Governed bank statement adjustment", amount, "evidence/bank-adjustment",
            [new(distributionAccountId, amount, "Adjustment distribution")]);

    private static async Task<StatementFixture> CreateStatementAsync(ErpRfqAutomationContext db,
        decimal signedAmount, string key)
    {
        var ledger = new GeneralLedgerService(db);
        var reconciliation = new BankReconciliationService(db);
        var cashId = db.LedgerAccounts.Single(x => x.BusinessUnitId == TenantId && x.Code == "1000").Id;
        var periods = await ledger.GetPeriodsAsync(TenantId, Today.Year);
        var period = periods.SingleOrDefault() ?? await ledger.CreatePeriodAsync(TenantId, "adjustment-period",
            new(Today.Year, Today.Month, "Adjustment test period", new(Today.Year, Today.Month, 1),
                new DateTime(Today.Year, Today.Month, 1).AddMonths(1).AddDays(-1)), "period-maker@test");
        var bank = await reconciliation.CreateBankAccountAsync(TenantId, $"adjustment-bank-{key}",
            new($"Adjustment bank {key}", "Test Bank", "****4321", $"adjustment-{key}", CurrencyId,
                cashId, Today.AddYears(-1)), "treasury@test");
        var statement = await reconciliation.ImportStatementAsync(TenantId, $"adjustment-statement-{key}",
            new(bank.Id, "CSV", $"{key}.csv", $"evidence/{key}", Hash(key), "test-v1", $"STM-{key}",
                Today, Today, 0m, signedAmount,
                [new(1, Today, Today, signedAmount, signedAmount.ToString("0.00"), $"TX-{key}",
                    $"REF-{key}", "ADJ", "Test counterparty", "Adjustment evidence")]), "importer@test");
        return new(bank.Id, Assert.Single(statement.Lines).Id, period.Id, cashId);
    }

    private static async Task<ErpRfqAutomationContext> CreateFixtureAsync(TestDb database)
    {
        var db = database.ContextFor(TenantId);
        await db.Database.ExecuteSqlRawAsync("PRAGMA ignore_check_constraints = ON;");
        Seed.EnsureBusinessUnit(db, TenantId);
        db.Currencies.Add(new Currency
        {
            Id = CurrencyId, BusinessUnitId = TenantId, Code = "USD", CurrencyName = "US Dollar",
            Symbol = "$", ExchangeRate = 1m, IsBaseCurrency = true, IsActive = true,
            CreatedBy = "test", CreatedOn = DateTime.UtcNow
        });
        await db.SaveChangesAsync();
        var ledger = new GeneralLedgerService(db);
        await ledger.CreateBookAsync(TenantId, "adjustment-book",
            new("Adjustment test book", CurrencyId, "UTC", 1), "controller@test");
        await ledger.CreateAccountAsync(TenantId, "adjustment-cash",
            new("1000", "Operating cash", LedgerAccountCategories.Asset, LedgerNormalBalances.Debit,
                null, false, true), "controller@test");
        await ledger.CreateAccountAsync(TenantId, "adjustment-expense",
            new("6000", "Bank expense", LedgerAccountCategories.Expense, LedgerNormalBalances.Debit,
                null, false, true), "controller@test");
        return db;
    }

    private static string Hash(string value)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static DateTime Today => DateTime.UtcNow.Date;
    private const long TenantId = 98_101;
    private const long CurrencyId = 98_102;

    private sealed record StatementFixture(long BankAccountId, long StatementLineId, long PeriodId,
        long CashAccountId);
    private sealed record AdjustmentFixture(BankAdjustmentService Service, BankAdjustmentDto Adjustment,
        long CashAccountId, long DistributionAccountId);
}
