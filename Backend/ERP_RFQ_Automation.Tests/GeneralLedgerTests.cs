using ERP_RFQ_Automation.Authorization;
using ERP_RFQ_Automation.Controllers;
using ERP_RFQ_Automation.GeneralLedger;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Tests.Support;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.Tests;

public sealed class GeneralLedgerTests
{
    [Fact]
    public void Controller_UsesDedicatedLedgerPermissions()
    {
        AssertPermission(nameof(GeneralLedgerController.CreateAccount), "General Ledger", PermissionAction.Create);
        AssertPermission(nameof(GeneralLedgerController.CreateBook), "General Ledger", PermissionAction.Create);
        AssertPermission(nameof(GeneralLedgerController.GetBook), "General Ledger", PermissionAction.View);
        AssertPermission(nameof(GeneralLedgerController.DeactivateAccount), "General Ledger", PermissionAction.Edit);
        AssertPermission(nameof(GeneralLedgerController.GetAccounts), "General Ledger", PermissionAction.View);
        AssertPermission(nameof(GeneralLedgerController.CreatePeriod), "Accounting Periods", PermissionAction.Create);
        AssertPermission(nameof(GeneralLedgerController.SoftClosePeriod), "Accounting Periods", PermissionAction.Edit);
        AssertPermission(nameof(GeneralLedgerController.ClosePeriod), "Period Close", PermissionAction.Edit);
        AssertPermission(nameof(GeneralLedgerController.ReopenPeriod), "Ledger Control", PermissionAction.Edit);
        AssertPermission(nameof(GeneralLedgerController.GetPeriods), "Accounting Periods", PermissionAction.View);
        AssertPermission(nameof(GeneralLedgerController.CreateJournal), "General Ledger", PermissionAction.Create);
        AssertPermission(nameof(GeneralLedgerController.PostJournal), "General Ledger Posting", PermissionAction.Edit);
        AssertPermission(nameof(GeneralLedgerController.ReverseJournal), "Ledger Control", PermissionAction.Edit);
        AssertPermission(nameof(GeneralLedgerController.GetTrialBalance), "General Ledger", PermissionAction.View);
    }

    [Fact]
    public async Task AccountCreation_IsIdempotentAndEnforcesAccountingClassification()
    {
        using var database = new TestDb();
        await using var db = await SeedAsync(database);
        var service = new GeneralLedgerService(db);
        var request = Account("1000", "Operating cash", LedgerAccountCategories.Asset, LedgerNormalBalances.Debit);

        var created = await service.CreateAccountAsync(BusinessUnitId, "account-cash", request, "controller@test");
        var replay = await service.CreateAccountAsync(BusinessUnitId, "account-cash", request, "controller@test");

        Assert.Equal(created.Id, replay.Id);
        await Assert.ThrowsAsync<GeneralLedgerConflictException>(() => service.CreateAccountAsync(
            BusinessUnitId, "account-cash", request with { Name = "Altered cash" }, "controller@test"));
        await Assert.ThrowsAsync<ArgumentException>(() => service.CreateAccountAsync(BusinessUnitId, "account-invalid",
            Account("1100", "Invalid asset", LedgerAccountCategories.Asset, LedgerNormalBalances.Credit), "controller@test"));
        Assert.Single(await db.LedgerAccounts.ToListAsync());
    }

    [Fact]
    public async Task ManualJournal_RequiresDualCurrencyBalanceAndMakerChecker()
    {
        using var database = new TestDb();
        await using var db = await SeedAsync(database, includeSecondCurrency: true);
        var service = new GeneralLedgerService(db);
        var period = await CreatePeriodAsync(service);
        var debit = await CreateAccountAsync(service, "1200", "Trade clearing", LedgerAccountCategories.Asset, LedgerNormalBalances.Debit);
        var credit = await CreateAccountAsync(service, "4000", "Service revenue", LedgerAccountCategories.Revenue, LedgerNormalBalances.Credit);

        var unbalancedCurrency = Journal(period.Id, debit.Id, credit.Id,
        [
            new(debit.Id, "USD debit", CurrencyId, 1m, 100m, 0m),
            new(credit.Id, "EUR credit", SecondCurrencyId, 1m, 0m, 100m)
        ]);
        await Assert.ThrowsAsync<ArgumentException>(() => service.CreateManualJournalAsync(
            BusinessUnitId, "journal-currency-mismatch", unbalancedCurrency, "maker@test"));

        var draft = await service.CreateManualJournalAsync(BusinessUnitId, "journal-balanced",
            Journal(period.Id, debit.Id, credit.Id), "maker@test");
        await Assert.ThrowsAsync<GeneralLedgerConflictException>(() => service.PostJournalAsync(
            BusinessUnitId, draft.Id, new JournalActionRequest(draft.Version), "maker@test"));

        var posted = await service.PostJournalAsync(
            BusinessUnitId, draft.Id, new JournalActionRequest(draft.Version), "checker@test");

        Assert.Equal(JournalEntryStatuses.Posted, posted.Status);
        Assert.Matches($"^JRN-{DateTime.UtcNow.Year}-[0-9]{{8}}$", posted.EntryNumber!);
        Assert.Equal(100m, posted.TotalDebit);
        Assert.Equal(posted.TotalDebit, posted.TotalCredit);
        Assert.Equal(2, posted.Lines.Count);
    }

    [Fact]
    public async Task ControlAccount_CannotReceiveManualJournal()
    {
        using var database = new TestDb();
        await using var db = await SeedAsync(database);
        var service = new GeneralLedgerService(db);
        var period = await CreatePeriodAsync(service);
        var control = await service.CreateAccountAsync(BusinessUnitId, "account-control",
            new("1300", "Accounts receivable control", LedgerAccountCategories.Asset,
                LedgerNormalBalances.Debit, null, true, true), "controller@test");
        var revenue = await CreateAccountAsync(service, "4100", "Revenue", LedgerAccountCategories.Revenue, LedgerNormalBalances.Credit);

        await Assert.ThrowsAsync<GeneralLedgerConflictException>(() => service.CreateManualJournalAsync(
            BusinessUnitId, "journal-control", Journal(period.Id, control.Id, revenue.Id), "maker@test"));
    }

    [Fact]
    public async Task ContraAccount_UsesOppositeNormalBalanceAndBookCurrencyIsMandatory()
    {
        using var database = new TestDb();
        await using var db = await SeedAsync(database, includeSecondCurrency: true);
        var service = new GeneralLedgerService(db);
        var contra = await service.CreateAccountAsync(BusinessUnitId, "account-contra",
            new("1099", "Allowance against cash", LedgerAccountCategories.Asset,
                LedgerNormalBalances.Credit, null, false, true, true), "controller@test");
        Assert.True(contra.IsContraAccount);

        await Assert.ThrowsAsync<ArgumentException>(() => service.CreateAccountAsync(BusinessUnitId, "account-bad-contra",
            new("1098", "Invalid contra", LedgerAccountCategories.Asset,
                LedgerNormalBalances.Debit, null, false, true, true), "controller@test"));

        var period = await CreatePeriodAsync(service);
        var revenue = await CreateAccountAsync(service, "4400", "Book currency revenue",
            LedgerAccountCategories.Revenue, LedgerNormalBalances.Credit);
        await Assert.ThrowsAsync<ArgumentException>(() => service.CreateManualJournalAsync(BusinessUnitId,
            "journal-wrong-book-currency", new(period.Id, SecondCurrencyId, DateTime.UtcNow.Date,
                "Journal with a non-book functional currency",
                [
                    new(contra.Id, "Debit", SecondCurrencyId, 1m, 100m, 0m),
                    new(revenue.Id, "Credit", SecondCurrencyId, 1m, 0m, 100m)
                ]), "maker@test"));
    }

    [Fact]
    public async Task PeriodLifecycle_BlocksDraftsAndRequiresIndependentControllers()
    {
        using var database = new TestDb();
        await using var db = await SeedAsync(database);
        var service = new GeneralLedgerService(db);
        var period = await CreatePeriodAsync(service);
        var debit = await CreateAccountAsync(service, "1400", "Prepayments", LedgerAccountCategories.Asset, LedgerNormalBalances.Debit);
        var credit = await CreateAccountAsync(service, "4200", "Other revenue", LedgerAccountCategories.Revenue, LedgerNormalBalances.Credit);
        var draft = await service.CreateManualJournalAsync(BusinessUnitId, "journal-close-gate",
            Journal(period.Id, debit.Id, credit.Id), "maker@test");

        await Assert.ThrowsAsync<GeneralLedgerConflictException>(() => service.TransitionPeriodAsync(
            BusinessUnitId, period.Id, "soft-close", new AccountingPeriodActionRequest(period.Version), "closer@test"));
        var cancelled = await service.CancelJournalAsync(BusinessUnitId, draft.Id,
            new JournalActionRequest(draft.Version, "Duplicate journal cancelled before close"), "maker@test");
        var refreshed = Assert.Single(await service.GetPeriodsAsync(BusinessUnitId, DateTime.UtcNow.Year));
        await Assert.ThrowsAsync<GeneralLedgerConflictException>(() => service.TransitionPeriodAsync(
            BusinessUnitId, period.Id, "soft-close", new AccountingPeriodActionRequest(refreshed.Version), "period-maker@test"));

        var softClosed = await service.TransitionPeriodAsync(BusinessUnitId, period.Id, "soft-close",
            new AccountingPeriodActionRequest(refreshed.Version), "period-checker@test");
        await Assert.ThrowsAsync<GeneralLedgerConflictException>(() => service.TransitionPeriodAsync(
            BusinessUnitId, period.Id, "close", new AccountingPeriodActionRequest(softClosed.Version), "period-checker@test"));
        var closed = await service.TransitionPeriodAsync(BusinessUnitId, period.Id, "close",
            new AccountingPeriodActionRequest(softClosed.Version,
                "Approved monthly ledger close certification", "CLOSE-PACKAGE-001"), "controller@test");

        Assert.Equal(JournalEntryStatuses.Cancelled, cancelled.Status);
        Assert.Equal(AccountingPeriodStatuses.Closed, closed.Status);
        Assert.NotNull(closed.CloseTrialBalanceHash);
        Assert.Equal(64, closed.CloseTrialBalanceHash!.Length);
        Assert.Equal(0, closed.CloseJournalCount);
    }

    [Fact]
    public async Task Reversal_IsExactAndTrialBalanceReturnsReproducibleZeroEnding()
    {
        using var database = new TestDb();
        await using var db = await SeedAsync(database);
        var service = new GeneralLedgerService(db);
        var period = await CreatePeriodAsync(service);
        var debit = await CreateAccountAsync(service, "1500", "Clearing", LedgerAccountCategories.Asset, LedgerNormalBalances.Debit);
        var credit = await CreateAccountAsync(service, "4300", "Consulting revenue", LedgerAccountCategories.Revenue, LedgerNormalBalances.Credit);
        var draft = await service.CreateManualJournalAsync(BusinessUnitId, "journal-reverse",
            Journal(period.Id, debit.Id, credit.Id), "maker@test");
        var posted = await service.PostJournalAsync(BusinessUnitId, draft.Id,
            new JournalActionRequest(draft.Version), "checker@test");

        await Assert.ThrowsAsync<GeneralLedgerConflictException>(() => service.ReverseJournalAsync(
            BusinessUnitId, posted.Id, "journal-reverse-action", new JournalActionRequest(posted.Version,
                "Approved correction of duplicate accounting entry", "CASE-GL-REV-001", DateTime.UtcNow), "checker@test"));
        var reversal = await service.ReverseJournalAsync(BusinessUnitId, posted.Id, "journal-reverse-action",
            new JournalActionRequest(posted.Version, "Approved correction of duplicate accounting entry",
                "CASE-GL-REV-001", DateTime.UtcNow), "controller@test");
        var replay = await service.ReverseJournalAsync(BusinessUnitId, posted.Id, "journal-reverse-action",
            new JournalActionRequest(posted.Version, "Approved correction of duplicate accounting entry",
                "CASE-GL-REV-001", reversal.AccountingDate), "controller@test");
        await Assert.ThrowsAsync<GeneralLedgerConflictException>(() => service.ReverseJournalAsync(
            BusinessUnitId, posted.Id, "journal-reverse-action",
            new JournalActionRequest(posted.Version, "A different approved correction explanation",
                "CASE-GL-REV-001", reversal.AccountingDate), "controller@test"));
        var original = await service.GetJournalAsync(BusinessUnitId, posted.Id);
        var trialBalance = await service.GetTrialBalanceAsync(
            BusinessUnitId, DateTime.UtcNow.Date.AddDays(-1), DateTime.UtcNow.Date.AddDays(1), CurrencyId);

        Assert.Equal(JournalEntryStatuses.Posted, reversal.Status);
        Assert.Equal(reversal.Id, replay.Id);
        Assert.Equal(JournalEntryStatuses.Reversed, original.Status);
        Assert.Equal(posted.Id, reversal.ReversesJournalEntryId);
        Assert.All(reversal.Lines, line =>
        {
            var source = posted.Lines.Single(x => x.Sequence == line.Sequence);
            Assert.Equal(source.FunctionalCredit, line.FunctionalDebit);
            Assert.Equal(source.FunctionalDebit, line.FunctionalCredit);
        });
        Assert.Equal(0m, trialBalance.TotalDebit - trialBalance.TotalCredit);
        Assert.All(trialBalance.Lines, line => Assert.Equal(0m, line.EndingBalance));
        Assert.All(trialBalance.Lines, line => Assert.Equal(2, line.DrillThroughCount));
    }

    [Fact]
    public async Task Reversal_TruncatesGeneratedTextWithoutLosingAccountingContent()
    {
        using var database = new TestDb();
        await using var db = await SeedAsync(database);
        var service = new GeneralLedgerService(db);
        var period = await CreatePeriodAsync(service);
        var debit = await CreateAccountAsync(service, "1510", "Long description debit",
            LedgerAccountCategories.Asset, LedgerNormalBalances.Debit);
        var credit = await CreateAccountAsync(service, "4310", "Long description revenue",
            LedgerAccountCategories.Revenue, LedgerNormalBalances.Credit);
        var longText = new string('x', 500);
        var draft = await service.CreateManualJournalAsync(BusinessUnitId, "journal-long-text",
            new(period.Id, CurrencyId, DateTime.UtcNow.Date, longText,
            [
                new(debit.Id, longText, CurrencyId, 1m, 100m, 0m),
                new(credit.Id, longText, CurrencyId, 1m, 0m, 100m)
            ]), "maker@test");
        var posted = await service.PostJournalAsync(BusinessUnitId, draft.Id,
            new JournalActionRequest(draft.Version), "checker@test");
        var reversal = await service.ReverseJournalAsync(BusinessUnitId, posted.Id, "journal-long-text-reversal",
            new JournalActionRequest(posted.Version, new string('r', 500), "LONG-TEXT-EVIDENCE", DateTime.UtcNow),
            "controller@test");

        Assert.Equal(500, reversal.Description.Length);
        Assert.All(reversal.Lines, line => Assert.Equal(500, line.Description.Length));
        Assert.Equal(posted.TotalDebit, reversal.TotalCredit);
        Assert.Equal(posted.TotalCredit, reversal.TotalDebit);
    }

    [Fact]
    public async Task TenantFilter_DoesNotExposeAnotherTenantsLedger()
    {
        using var database = new TestDb();
        await using (var first = await SeedAsync(database))
        {
            var service = new GeneralLedgerService(first);
            await CreateAccountAsync(service, "1600", "Tenant one account", LedgerAccountCategories.Asset, LedgerNormalBalances.Debit);
        }
        const long otherTenant = BusinessUnitId + 100;
        await using (var unscoped = database.ContextFor(null))
        {
            Seed.EnsureBusinessUnit(unscoped, otherTenant);
            unscoped.LedgerAccounts.Add(new LedgerAccount
            {
                BusinessUnitId = otherTenant, Code = "1600", Name = "Tenant two account",
                Category = LedgerAccountCategories.Asset, NormalBalance = LedgerNormalBalances.Debit,
                IdempotencyKey = "tenant-two-account", RequestHash = new string('a', 64),
                CreatedBy = "test", CreatedOn = DateTime.UtcNow
            });
            await unscoped.SaveChangesAsync();
        }
        await using var scoped = database.ContextFor(BusinessUnitId);
        var rows = await new GeneralLedgerService(scoped).GetAccountsAsync(BusinessUnitId, true);
        Assert.Single(rows);
        Assert.Equal("Tenant one account", rows[0].Name);
    }

    private static async Task<ErpRfqAutomationContext> SeedAsync(TestDb database, bool includeSecondCurrency = false)
    {
        var db = database.ContextFor(BusinessUnitId);
        Seed.EnsureBusinessUnit(db, BusinessUnitId);
        db.Currencies.Add(new Currency
        {
            Id = CurrencyId, BusinessUnitId = BusinessUnitId, Code = "USD", CurrencyName = "US Dollar",
            Symbol = "$", ExchangeRate = 1m, IsBaseCurrency = true, IsActive = true,
            CreatedBy = "test", CreatedOn = DateTime.UtcNow
        });
        if (includeSecondCurrency)
            db.Currencies.Add(new Currency
            {
                Id = SecondCurrencyId, BusinessUnitId = BusinessUnitId, Code = "EUR", CurrencyName = "Euro",
                Symbol = "EUR", ExchangeRate = 1m, IsBaseCurrency = false, IsActive = true,
                CreatedBy = "test", CreatedOn = DateTime.UtcNow
            });
        await db.SaveChangesAsync();
        await new GeneralLedgerService(db).CreateBookAsync(BusinessUnitId, "ledger-book",
            new("Primary accounting book", CurrencyId, "UTC", 1), "controller@test");
        return db;
    }

    private static Task<AccountingPeriodDto> CreatePeriodAsync(GeneralLedgerService service)
        => service.CreatePeriodAsync(BusinessUnitId, "period-current",
            new(DateTime.UtcNow.Year, DateTime.UtcNow.Month, "Current test period",
                new DateTime(DateTime.UtcNow.Year, DateTime.UtcNow.Month, 1),
                new DateTime(DateTime.UtcNow.Year, DateTime.UtcNow.Month, 1).AddMonths(1).AddDays(-1)),
            "period-maker@test");

    private static Task<LedgerAccountDto> CreateAccountAsync(GeneralLedgerService service, string code, string name,
        string category, string normalBalance)
        => service.CreateAccountAsync(BusinessUnitId, $"account-{code}",
            Account(code, name, category, normalBalance), "controller@test");

    private static CreateLedgerAccountRequest Account(string code, string name, string category, string normalBalance)
        => new(code, name, category, normalBalance, null, false, true);

    private static CreateJournalEntryRequest Journal(long periodId, long debitAccountId, long creditAccountId,
        IReadOnlyList<CreateJournalEntryLineRequest>? lines = null)
        => new(periodId, CurrencyId, DateTime.UtcNow.Date, "Governed manual test journal",
            lines ??
            [
                new(debitAccountId, "Debit line", CurrencyId, 1m, 100m, 0m),
                new(creditAccountId, "Credit line", CurrencyId, 1m, 0m, 100m)
            ]);

    private static void AssertPermission(string methodName, string moduleName, PermissionAction action)
    {
        var attribute = typeof(GeneralLedgerController).GetMethod(methodName)!
            .GetCustomAttributes(typeof(RequireModulePermissionAttribute), true)
            .Cast<RequireModulePermissionAttribute>().Single();
        Assert.Equal(moduleName, attribute.ModuleName);
        Assert.Equal(action, attribute.Action);
    }

    private const long BusinessUnitId = 96_001;
    private const long CurrencyId = 96_002;
    private const long SecondCurrencyId = 96_003;
}
