using System.Security.Cryptography;
using System.Text;
using ERP_RFQ_Automation.BankReconciliation;
using ERP_RFQ_Automation.BankReconciliation.Services;
using ERP_RFQ_Automation.GeneralLedger;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Tests.Support;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.Tests;

public sealed class BankReconciliationServiceTests
{
    [Fact]
    public async Task BankAccountCreation_ValidatesLedgerAndIsIdempotent()
    {
        using var database = new TestDb();
        await using var db = await SeedTenantAsync(database, TenantId, CurrencyId);
        var ledger = new GeneralLedgerService(db);
        var service = new BankReconciliationService(db);
        var cash = await CreateAccountAsync(ledger, TenantId, "1000", "Operating cash",
            LedgerAccountCategories.Asset, LedgerNormalBalances.Debit);
        var revenue = await CreateAccountAsync(ledger, TenantId, "4000", "Revenue",
            LedgerAccountCategories.Revenue, LedgerNormalBalances.Credit);
        var request = BankAccountRequest(CurrencyId, cash.Id, "account-one");

        var created = await service.CreateBankAccountAsync(TenantId, "bank-account", request, "treasury@test");
        var replay = await service.CreateBankAccountAsync(TenantId, "bank-account", request, "treasury@test");

        Assert.Equal(created.Id, replay.Id);
        await Assert.ThrowsAsync<BankReconciliationConflictException>(() => service.CreateBankAccountAsync(
            TenantId, "bank-account", request with { Name = "Changed account" }, "treasury@test"));
        await Assert.ThrowsAsync<ArgumentException>(() => service.CreateBankAccountAsync(TenantId,
            "bank-revenue", BankAccountRequest(CurrencyId, revenue.Id, "revenue"), "treasury@test"));
        await Assert.ThrowsAsync<ArgumentException>(() => service.CreateBankAccountAsync(TenantId,
            "bank-currency", BankAccountRequest(CurrencyId + 999, cash.Id, "currency"), "treasury@test"));
        Assert.Single(await db.BankAccounts.ToListAsync());
    }

    [Fact]
    public async Task StatementImport_RejectsBadBalanceAndDuplicateImmutableSource()
    {
        using var database = new TestDb();
        await using var db = await CreateFixtureAsync(database);
        var service = new BankReconciliationService(db);
        var account = await CreateBankAccountAsync(service, TenantId, CurrencyId, CashAccountId(db), "imports");
        var request = Statement(account.Id, "STM-IMPORT", 0m, 100m, [Line(1, 100m, "TX-IMPORT")]);

        await Assert.ThrowsAsync<ArgumentException>(() => service.ImportStatementAsync(TenantId, "bad-balance",
            request with { ClosingBalance = 99m }, "importer@test"));
        var imported = await service.ImportStatementAsync(TenantId, "statement-import", request, "importer@test");
        var replay = await service.ImportStatementAsync(TenantId, "statement-import", request, "importer@test");

        Assert.Equal(imported.Id, replay.Id);
        await Assert.ThrowsAsync<BankReconciliationConflictException>(() => service.ImportStatementAsync(
            TenantId, "statement-duplicate-source", request with { StatementReference = "STM-IMPORT-COPY" },
            "importer@test"));
        Assert.Single(await db.BankStatements.ToListAsync());
        Assert.Single(await db.BankStatementImports.ToListAsync());
    }

    [Fact]
    public async Task ExactMatching_CreatesOnlyUniqueDeterministicCandidates()
    {
        using var database = new TestDb();
        await using var db = await CreateFixtureAsync(database);
        var ledger = new GeneralLedgerService(db);
        var service = new BankReconciliationService(db);
        var cashId = CashAccountId(db);
        var revenueId = RevenueAccountId(db);
        await PostJournalAsync(ledger, TenantId, "exact-100", cashId, revenueId, 100m);
        await PostJournalAsync(ledger, TenantId, "ambiguous-50-a", cashId, revenueId, 50m);
        await PostJournalAsync(ledger, TenantId, "ambiguous-50-b", cashId, revenueId, 50m);
        var account = await CreateBankAccountAsync(service, TenantId, CurrencyId, cashId, "matching");
        var statement = await service.ImportStatementAsync(TenantId, "matching-statement",
            Statement(account.Id, "STM-MATCH", 0m, 200m,
                [Line(1, 100m, "UNIQUE-100"), Line(2, 50m, "DUP-50-A"), Line(3, 50m, "DUP-50-B")]),
            "importer@test");
        var run = await service.CreateRunAsync(TenantId, "matching-run",
            new(statement.Id, Today), "preparer@test");

        var candidates = await service.GenerateExactCandidatesAsync(
            TenantId, run.Id, "exact-candidates", "matcher@test");
        var replay = await service.GenerateExactCandidatesAsync(
            TenantId, run.Id, "exact-candidates", "matcher@test");

        var candidate = Assert.Single(candidates);
        Assert.Equal(candidate.Id, Assert.Single(replay).Id);
        Assert.Equal("EXACT_AMOUNT_DIRECTION", candidate.RuleCode);
        Assert.Equal(100m, Assert.Single(candidate.Allocations).BankAmount);
    }

    [Fact]
    public async Task MatchingRuleLifecycle_RequiresMakerChecker()
    {
        using var database = new TestDb();
        await using var db = await CreateFixtureAsync(database);
        var service = new BankReconciliationService(db);
        var rule = await service.CreateMatchingRuleAsync(TenantId, "rule-maker-checker",
            ExactRule("GOVERNED_EXACT", null, 10), "rule-maker@test");

        await Assert.ThrowsAsync<BankReconciliationConflictException>(() =>
            service.TransitionMatchingRuleAsync(TenantId, rule.Id, "approve",
                RuleAction(rule.RecordVersion), "rule-maker@test"));

        var approved = await service.TransitionMatchingRuleAsync(TenantId, rule.Id, "approve",
            RuleAction(rule.RecordVersion), "rule-checker@test");
        var approvalReplay = await service.TransitionMatchingRuleAsync(TenantId, rule.Id, "approve",
            RuleAction(rule.RecordVersion), "rule-checker@test");
        await Assert.ThrowsAsync<BankReconciliationConflictException>(() =>
            service.TransitionMatchingRuleAsync(TenantId, rule.Id, "activate",
                RuleAction(approved.RecordVersion), "rule-maker@test"));

        var active = await service.TransitionMatchingRuleAsync(TenantId, rule.Id, "activate",
            RuleAction(approved.RecordVersion), "rule-checker@test");
        var activationReplay = await service.TransitionMatchingRuleAsync(TenantId, rule.Id, "activate",
            RuleAction(approved.RecordVersion), "rule-checker@test");

        Assert.Equal(BankMatchingRuleStatuses.Active, active.Status);
        Assert.Equal(approved.RecordVersion, approvalReplay.RecordVersion);
        Assert.Equal(active.RecordVersion, activationReplay.RecordVersion);
        Assert.Equal("rule-checker@test", active.ApprovedBy);
        Assert.Equal("rule-checker@test", active.ActivatedBy);
    }

    [Fact]
    public async Task ReconciliationRun_RetainsImmutableRuleSnapshotAndMatchProvenance()
    {
        using var database = new TestDb();
        await using var db = await CreateFixtureAsync(database);
        var ledger = new GeneralLedgerService(db);
        var service = new BankReconciliationService(db);
        var cashId = CashAccountId(db);
        await PostJournalAsync(ledger, TenantId, "snapshot-journal", cashId, RevenueAccountId(db), 100m);
        var account = await CreateBankAccountAsync(service, TenantId, CurrencyId, cashId, "snapshot");
        var versionOne = await ActivateRuleAsync(service, "snapshot-rule-v1",
            ExactRule("SNAPSHOT_EXACT", account.Id, 10), "rule-maker@test", "rule-checker@test");
        var statement = await service.ImportStatementAsync(TenantId, "snapshot-statement",
            Statement(account.Id, "STM-SNAPSHOT", 0m, 100m, [Line(1, 100m, "SNAPSHOT-100")]),
            "importer@test");
        var run = await service.CreateRunAsync(TenantId, "snapshot-run",
            new(statement.Id, Today), "preparer@test");
        var snapshot = await db.ReconciliationRunRules.AsNoTracking().SingleAsync(x =>
            x.BusinessUnitId == TenantId && x.ReconciliationRunId == run.Id);

        var versionTwo = await ActivateRuleAsync(service, "snapshot-rule-v2",
            ExactRule("SNAPSHOT_EXACT", account.Id, 20), "second-maker@test", "second-checker@test");
        var candidate = Assert.Single(await service.GenerateExactCandidatesAsync(TenantId, run.Id,
            "snapshot-candidates", "matcher@test"));
        var persistedMatch = await db.ReconciliationMatches.AsNoTracking().SingleAsync(x => x.Id == candidate.Id);
        var persistedRun = await db.ReconciliationRuns.AsNoTracking().SingleAsync(x => x.Id == run.Id);

        Assert.Equal(versionOne.Id, snapshot.BankMatchingRuleId);
        Assert.Equal(versionOne.DefinitionHash, snapshot.DefinitionHash);
        Assert.Equal(versionOne.Id, candidate.BankMatchingRuleId);
        Assert.Equal(versionOne.RuleVersion, candidate.RuleVersion);
        Assert.Equal(versionOne.DefinitionHash, persistedMatch.RuleDefinitionHash);
        Assert.NotEqual(versionTwo.DefinitionHash, persistedMatch.RuleDefinitionHash);
        Assert.Equal(Hash(versionOne.DefinitionHash), persistedRun.RuleSetHash);
    }

    [Fact]
    public async Task ManualMatch_SupportsSplitAllocationAndRejectsAggregateOverAllocation()
    {
        using var database = new TestDb();
        await using var db = await CreateFixtureAsync(database);
        var ledger = new GeneralLedgerService(db);
        var service = new BankReconciliationService(db);
        var cashId = CashAccountId(db);
        var revenueId = RevenueAccountId(db);
        var journal60 = await PostJournalAsync(ledger, TenantId, "split-60", cashId, revenueId, 60m);
        var journal40 = await PostJournalAsync(ledger, TenantId, "split-40", cashId, revenueId, 40m);
        var journal110 = await PostJournalAsync(ledger, TenantId, "over-110", cashId, revenueId, 110m);
        var account = await CreateBankAccountAsync(service, TenantId, CurrencyId, cashId, "split");
        var statement = await service.ImportStatementAsync(TenantId, "split-statement",
            Statement(account.Id, "STM-SPLIT", 0m, 200m, [Line(1, 200m, "SPLIT-200")]), "importer@test");
        var bankLineId = Assert.Single(statement.Lines).Id;
        var run = await service.CreateRunAsync(TenantId, "split-run", new(statement.Id, Today), "preparer@test");

        var split = await service.CreateMatchAsync(TenantId, "split-match",
            ManualMatch(run.Id,
            [
                Allocation(bankLineId, CashLine(journal60).Id, 60m),
                Allocation(bankLineId, CashLine(journal40).Id, 40m)
            ]), "matcher@test");
        var confirmed = await service.ConfirmMatchAsync(TenantId, split.Id,
            new(split.Version), "matcher@test");
        Assert.Equal(BankMatchStatuses.Confirmed, confirmed.Status);
        Assert.Equal(2, confirmed.Allocations.Count);

        var excessive = await service.CreateMatchAsync(TenantId, "excessive-match",
            ManualMatch(run.Id, [Allocation(bankLineId, CashLine(journal110).Id, 101m)]), "matcher@test");
        await Assert.ThrowsAsync<BankReconciliationConflictException>(() => service.ConfirmMatchAsync(
            TenantId, excessive.Id, new(excessive.Version), "matcher@test"));
    }

    [Fact]
    public async Task Approval_RequiresIndependentCheckerAndProducesCertificate()
    {
        using var database = new TestDb();
        await using var db = await CreateFixtureAsync(database);
        var (service, run) = await CreateCertifiableRunAsync(db, "approval");
        var submitted = await service.SubmitRunAsync(TenantId, run.Id,
            new(run.Version), "preparer@test");

        await Assert.ThrowsAsync<BankReconciliationConflictException>(() => service.ApproveRunAsync(TenantId,
            run.Id, new(submitted.Version, "Approved after complete reconciliation review", "EVIDENCE-APPROVAL"),
            "preparer@test"));
        var approved = await service.ApproveRunAsync(TenantId, run.Id,
            new(submitted.Version, "Approved after complete reconciliation review", "EVIDENCE-APPROVAL"),
            "checker@test");

        Assert.Equal(ReconciliationStatuses.Approved, approved.Status);
        Assert.Equal("checker@test", approved.ApprovedBy);
        Assert.Equal(64, approved.CertificateHash!.Length);
        Assert.Equal(1, approved.CertificateLineCount);
        Assert.Equal(1, approved.CertificateJournalCount);
    }

    [Fact]
    public async Task Reopen_PreservesApprovalCertificate()
    {
        using var database = new TestDb();
        await using var db = await CreateFixtureAsync(database);
        var (service, run) = await CreateCertifiableRunAsync(db, "reopen");
        var submitted = await service.SubmitRunAsync(TenantId, run.Id, new(run.Version), "preparer@test");
        var approved = await service.ApproveRunAsync(TenantId, run.Id,
            new(submitted.Version, "Approved after complete reconciliation review", "EVIDENCE-REOPEN-APPROVAL"),
            "checker@test");

        await Assert.ThrowsAsync<BankReconciliationConflictException>(() => service.ReopenRunAsync(TenantId,
            run.Id, new(approved.Version, "Reopened for an independently evidenced correction",
                "EVIDENCE-REOPEN"), "checker@test"));
        var reopened = await service.ReopenRunAsync(TenantId, run.Id,
            new(approved.Version, "Reopened for an independently evidenced correction", "EVIDENCE-REOPEN"),
            "controller@test");

        Assert.Equal(ReconciliationStatuses.Reopened, reopened.Status);
        Assert.Equal(approved.CertificateHash, reopened.CertificateHash);
        Assert.Equal(approved.CertificateLineCount, reopened.CertificateLineCount);
        Assert.Equal(approved.CertificateJournalCount, reopened.CertificateJournalCount);
    }

    [Fact]
    public async Task TenantScope_DoesNotExposeAnotherTenantsBankAccount()
    {
        using var database = new TestDb();
        long firstAccountId;
        await using (var first = await CreateFixtureAsync(database, TenantId, CurrencyId))
        {
            var service = new BankReconciliationService(first);
            firstAccountId = (await CreateBankAccountAsync(service, TenantId, CurrencyId,
                CashAccountId(first), "tenant-one")).Id;
        }

        const long otherTenantId = TenantId + 100;
        const long otherCurrencyId = CurrencyId + 100;
        long otherAccountId;
        await using (var second = await CreateFixtureAsync(database, otherTenantId, otherCurrencyId))
        {
            var service = new BankReconciliationService(second);
            otherAccountId = (await CreateBankAccountAsync(service, otherTenantId, otherCurrencyId,
                CashAccountId(second, otherTenantId), "tenant-two")).Id;
        }

        await using var scoped = database.ContextFor(TenantId);
        var scopedService = new BankReconciliationService(scoped);
        var visible = await scopedService.GetBankAccountsAsync(TenantId, true);

        Assert.Equal(firstAccountId, Assert.Single(visible).Id);
        await Assert.ThrowsAsync<KeyNotFoundException>(() => scopedService.GetBankAccountAsync(
            otherTenantId, otherAccountId));
    }

    private static async Task<(BankReconciliationService Service, ReconciliationRunDto Run)>
        CreateCertifiableRunAsync(ErpRfqAutomationContext db, string key)
    {
        var ledger = new GeneralLedgerService(db);
        var service = new BankReconciliationService(db);
        var cashId = CashAccountId(db);
        var journal = await PostJournalAsync(ledger, TenantId, $"{key}-journal", cashId,
            RevenueAccountId(db), 100m);
        var account = await CreateBankAccountAsync(service, TenantId, CurrencyId, cashId, key);
        var statement = await service.ImportStatementAsync(TenantId, $"{key}-statement",
            Statement(account.Id, $"STM-{key.ToUpperInvariant()}", 0m, 100m,
                [Line(1, 100m, $"TX-{key.ToUpperInvariant()}")]), "importer@test");
        var run = await service.CreateRunAsync(TenantId, $"{key}-run", new(statement.Id, Today), "preparer@test");
        var match = await service.CreateMatchAsync(TenantId, $"{key}-match",
            ManualMatch(run.Id, [Allocation(Assert.Single(statement.Lines).Id, CashLine(journal).Id, 100m)]),
            "matcher@test");
        await service.ConfirmMatchAsync(TenantId, match.Id, new(match.Version), "matcher@test");
        return (service, await service.GetRunAsync(TenantId, run.Id));
    }

    private static async Task<ErpRfqAutomationContext> CreateFixtureAsync(TestDb database,
        long tenantId = TenantId, long currencyId = CurrencyId)
    {
        var db = await SeedTenantAsync(database, tenantId, currencyId);
        var ledger = new GeneralLedgerService(db);
        await CreateAccountAsync(ledger, tenantId, "1000", "Operating cash",
            LedgerAccountCategories.Asset, LedgerNormalBalances.Debit);
        await CreateAccountAsync(ledger, tenantId, "4000", "Revenue",
            LedgerAccountCategories.Revenue, LedgerNormalBalances.Credit);
        return db;
    }

    private static async Task<ErpRfqAutomationContext> SeedTenantAsync(TestDb database, long tenantId, long currencyId)
    {
        var db = database.ContextFor(tenantId);
        // SQLite persists decimals as text, so its numeric Confidence check rejects valid values.
        await db.Database.ExecuteSqlRawAsync("PRAGMA ignore_check_constraints = ON;");
        Seed.EnsureBusinessUnit(db, tenantId);
        db.Currencies.Add(new Currency
        {
            Id = currencyId,
            BusinessUnitId = tenantId,
            Code = "USD",
            CurrencyName = "US Dollar",
            Symbol = "$",
            ExchangeRate = 1m,
            IsBaseCurrency = true,
            IsActive = true,
            CreatedBy = "test",
            CreatedOn = DateTime.UtcNow
        });
        await db.SaveChangesAsync();
        await new GeneralLedgerService(db).CreateBookAsync(tenantId, $"book-{tenantId}",
            new($"Primary book {tenantId}", currencyId, "UTC", 1), "controller@test");
        return db;
    }

    private static Task<LedgerAccountDto> CreateAccountAsync(GeneralLedgerService ledger, long tenantId,
        string code, string name, string category, string normalBalance)
        => ledger.CreateAccountAsync(tenantId, $"account-{tenantId}-{code}",
            new(code, name, category, normalBalance, null, false, true), "controller@test");

    private static async Task<JournalEntryDto> PostJournalAsync(GeneralLedgerService ledger, long tenantId,
        string key, long cashAccountId, long revenueAccountId, decimal amount)
    {
        var periods = await ledger.GetPeriodsAsync(tenantId, Today.Year);
        var period = periods.SingleOrDefault() ?? await ledger.CreatePeriodAsync(tenantId, $"period-{tenantId}",
            new(Today.Year, Today.Month, "Current test period", new(Today.Year, Today.Month, 1),
                new DateTime(Today.Year, Today.Month, 1).AddMonths(1).AddDays(-1)), "period-maker@test");
        var currencyId = tenantId == TenantId ? CurrencyId : CurrencyId + 100;
        var draft = await ledger.CreateManualJournalAsync(tenantId, key,
            new(period.Id, currencyId, Today, $"Bank reconciliation journal {key}",
            [
                new(cashAccountId, "Cash movement", currencyId, 1m, amount, 0m),
                new(revenueAccountId, "Balancing movement", currencyId, 1m, 0m, amount)
            ]), "journal-maker@test");
        return await ledger.PostJournalAsync(tenantId, draft.Id, new(draft.Version), "journal-checker@test");
    }

    private static Task<BankAccountDto> CreateBankAccountAsync(BankReconciliationService service,
        long tenantId, long currencyId, long ledgerAccountId, string key)
        => service.CreateBankAccountAsync(tenantId, $"bank-{key}",
            BankAccountRequest(currencyId, ledgerAccountId, key), "treasury@test");

    private static CreateBankAccountRequest BankAccountRequest(long currencyId, long ledgerAccountId, string key)
        => new($"Bank account {key}", "Test Bank", "****1234", $"account-{key}", currencyId,
            ledgerAccountId, Today.AddYears(-1));

    private static CreateBankMatchingRuleRequest ExactRule(string code, long? bankAccountId, int priority)
        => new(code, bankAccountId, $"Exact rule {code}", BankMatchingRuleTypes.ExactAmountDirection,
            priority, 0m, 31, BankMatchingReferenceModes.Ignore, true);

    private static BankMatchingRuleActionRequest RuleAction(long version)
        => new(version, "Governed matching rule lifecycle approval", "evidence/rule-review");

    private static async Task<BankMatchingRuleDto> ActivateRuleAsync(BankReconciliationService service,
        string key, CreateBankMatchingRuleRequest request, string maker, string checker)
    {
        var draft = await service.CreateMatchingRuleAsync(TenantId, key, request, maker);
        var approved = await service.TransitionMatchingRuleAsync(TenantId, draft.Id, "approve",
            RuleAction(draft.RecordVersion), checker);
        return await service.TransitionMatchingRuleAsync(TenantId, draft.Id, "activate",
            RuleAction(approved.RecordVersion), checker);
    }

    private static ImportBankStatementRequest Statement(long bankAccountId, string reference,
        decimal opening, decimal closing, IReadOnlyList<ImportBankStatementLineRequest> lines)
        => new(bankAccountId, "CSV", $"{reference}.csv", $"evidence/{reference}", Hash(reference), "test-v1",
            reference, Today, Today, opening, closing, lines);

    private static ImportBankStatementLineRequest Line(int ordinal, decimal amount, string reference)
        => new(ordinal, Today, Today, amount, amount.ToString("0.00"), reference, reference,
            "TEST", "Test counterparty", reference);

    private static CreateReconciliationMatchRequest ManualMatch(long runId,
        IReadOnlyList<ReconciliationAllocationRequest> allocations)
        => new(runId, "Manual reconciliation approved from test evidence",
            "evidence/manual-match", allocations);

    private static ReconciliationAllocationRequest Allocation(long bankLineId, long journalLineId, decimal amount)
        => new(bankLineId, journalLineId, amount, amount);

    private static JournalEntryLineDto CashLine(JournalEntryDto journal)
        => journal.Lines.Single(x => x.FunctionalDebit > 0m);

    private static long CashAccountId(ErpRfqAutomationContext db, long tenantId = TenantId)
        => db.LedgerAccounts.Single(x => x.BusinessUnitId == tenantId && x.Code == "1000").Id;

    private static long RevenueAccountId(ErpRfqAutomationContext db)
        => db.LedgerAccounts.Single(x => x.BusinessUnitId == TenantId && x.Code == "4000").Id;

    private static string Hash(string value)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static DateTime Today => DateTime.UtcNow.Date;
    private const long TenantId = 98_001;
    private const long CurrencyId = 98_002;
}
