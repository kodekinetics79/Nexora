using System.Data;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ERP_RFQ_Automation.CommercialFinance;
using ERP_RFQ_Automation.Models;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace ERP_RFQ_Automation.GeneralLedger;

public interface IGeneralLedgerService
{
    Task<LedgerBookDto> CreateBookAsync(long businessUnitId, string idempotencyKey, CreateLedgerBookRequest request, string actor);
    Task<LedgerBookDto> GetBookAsync(long businessUnitId);
    Task<LedgerBookDto> ConfigureReceivablesPostingAsync(long businessUnitId,
        ConfigureReceivablesPostingRequest request, string actor);
    Task<LedgerAccountDto> CreateAccountAsync(long businessUnitId, string idempotencyKey, CreateLedgerAccountRequest request, string actor);
    Task<LedgerAccountDto> DeactivateAccountAsync(long businessUnitId, long accountId, DeactivateLedgerAccountRequest request, string actor);
    Task<IReadOnlyList<LedgerAccountDto>> GetAccountsAsync(long businessUnitId, bool includeInactive);
    Task<AccountingPeriodDto> CreatePeriodAsync(long businessUnitId, string idempotencyKey, CreateAccountingPeriodRequest request, string actor);
    Task<AccountingPeriodDto> TransitionPeriodAsync(long businessUnitId, long periodId, string action, AccountingPeriodActionRequest request, string actor);
    Task<IReadOnlyList<AccountingPeriodDto>> GetPeriodsAsync(long businessUnitId, int? fiscalYear);
    Task<JournalEntryDto> CreateManualJournalAsync(long businessUnitId, string idempotencyKey, CreateJournalEntryRequest request, string actor);
    Task<JournalEntryDto> PostJournalAsync(long businessUnitId, long journalId, JournalActionRequest request, string actor);
    Task<JournalEntryDto> CancelJournalAsync(long businessUnitId, long journalId, JournalActionRequest request, string actor);
    Task<JournalEntryDto> ReverseJournalAsync(long businessUnitId, long journalId, string idempotencyKey, JournalActionRequest request, string actor);
    Task<JournalEntryDto> GetJournalAsync(long businessUnitId, long journalId);
    Task<IReadOnlyList<JournalEntryDto>> GetJournalsAsync(long businessUnitId, long? periodId, string? status);
    Task<TrialBalanceDto> GetTrialBalanceAsync(long businessUnitId, DateTime from, DateTime through, long functionalCurrencyId);
}

public sealed class GeneralLedgerService(ErpRfqAutomationContext context) : IGeneralLedgerService
{
    private readonly ErpRfqAutomationContext _context = context;

    public async Task<LedgerBookDto> CreateBookAsync(
        long businessUnitId, string idempotencyKey, CreateLedgerBookRequest request, string actor)
    {
        ValidateKey(idempotencyKey);
        var name = Token(request.Name, "ledger book name", 160);
        var timeZoneId = Token(request.TimeZoneId, "ledger timezone", 100);
        if (request.FiscalYearStartMonth is < 1 or > 12)
            throw new ArgumentException("Fiscal-year start month must be between 1 and 12.");
        try { _ = TimeZoneInfo.FindSystemTimeZoneById(timeZoneId); }
        catch (TimeZoneNotFoundException) { throw new ArgumentException("The ledger timezone is not recognized."); }
        var requestHash = Hash(new { name, request.FunctionalCurrencyId, timeZoneId, request.FiscalYearStartMonth });
        return await InSerializableTransactionAsync(async () =>
        {
            var existing = await _context.LedgerBooks.SingleOrDefaultAsync(x => x.BusinessUnitId == businessUnitId);
            if (existing is not null)
            {
                if (existing.IdempotencyKey != idempotencyKey)
                    throw new GeneralLedgerConflictException("This tenant already has an accounting book.");
                EnsureReplay(existing.RequestHash, requestHash);
                return Map(existing);
            }
            if (!await _context.Currencies.AnyAsync(x => x.Id == request.FunctionalCurrencyId &&
                    x.BusinessUnitId == businessUnitId && x.IsActive == true && x.IsBaseCurrency == true))
                throw new ArgumentException("The functional currency must be the tenant's active base currency.");
            var book = new LedgerBook
            {
                BusinessUnitId = businessUnitId, Name = name, FunctionalCurrencyId = request.FunctionalCurrencyId,
                TimeZoneId = timeZoneId, FiscalYearStartMonth = request.FiscalYearStartMonth,
                IdempotencyKey = idempotencyKey, RequestHash = requestHash,
                CreatedBy = Actor(actor), CreatedOn = DateTime.UtcNow
            };
            _context.LedgerBooks.Add(book);
            await _context.SaveChangesAsync();
            await EvidenceAsync(businessUnitId, "LedgerBook", book.Id, book.Version,
                "Created", actor, "finance.ledger-book.created", new { book.Id, book.FunctionalCurrencyId });
            await _context.SaveChangesAsync();
            return Map(book);
        });
    }

    public async Task<LedgerBookDto> GetBookAsync(long businessUnitId)
        => Map(await _context.LedgerBooks.SingleOrDefaultAsync(x => x.BusinessUnitId == businessUnitId)
            ?? throw new KeyNotFoundException("Ledger book not found."));

    public async Task<LedgerBookDto> ConfigureReceivablesPostingAsync(long businessUnitId,
        ConfigureReceivablesPostingRequest request, string actor)
    {
        return await InSerializableTransactionAsync(async () =>
        {
            var book = await RequireBookAsync(businessUnitId);
            if (book.Version != request.ExpectedVersion)
                throw new GeneralLedgerConflictException("The ledger book changed; reload it before configuring receivables.");
            if (request.ReceivablesControlAccountId == request.UnappliedCashAccountId)
                throw new ArgumentException("Receivables control and unapplied cash require distinct accounts.");
            var accounts = await _context.LedgerAccounts.Where(x => x.BusinessUnitId == businessUnitId &&
                (x.Id == request.ReceivablesControlAccountId || x.Id == request.UnappliedCashAccountId))
                .ToDictionaryAsync(x => x.Id);
            if (accounts.Count != 2) throw new ArgumentException("Both receivables posting accounts must belong to this tenant.");
            var receivables = accounts[request.ReceivablesControlAccountId];
            var unapplied = accounts[request.UnappliedCashAccountId];
            if (!receivables.IsActive || !receivables.IsControlAccount ||
                receivables.Category != LedgerAccountCategories.Asset)
                throw new ArgumentException("Receivables requires an active asset control account.");
            if (!unapplied.IsActive || unapplied.IsControlAccount ||
                unapplied.Category != LedgerAccountCategories.Liability)
                throw new ArgumentException("Unapplied cash requires an active non-control liability account.");
            book.ReceivablesControlAccountId = receivables.Id; book.UnappliedCashAccountId = unapplied.Id;
            book.Version++;
            await EvidenceAsync(businessUnitId, "LedgerBook", book.Id, book.Version,
                "ReceivablesConfigured", actor, "finance.ledger-book.receivables-configured",
                new { book.Id, book.ReceivablesControlAccountId, book.UnappliedCashAccountId });
            await _context.SaveChangesAsync(); return Map(book);
        });
    }

    public async Task<LedgerAccountDto> CreateAccountAsync(
        long businessUnitId, string idempotencyKey, CreateLedgerAccountRequest request, string actor)
    {
        ValidateKey(idempotencyKey);
        var code = Token(request.Code, "account code", 30).ToUpperInvariant();
        var name = Token(request.Name, "account name", 160);
        var category = Category(request.Category);
        var normal = NormalBalance(request.NormalBalance);
        var categoryNormallyDebit = category is LedgerAccountCategories.Asset or LedgerAccountCategories.Expense;
        if ((normal == LedgerNormalBalances.Debit) != (categoryNormallyDebit ^ request.IsContraAccount))
            throw new ArgumentException("The normal balance does not agree with the ledger account category.");
        var requestHash = Hash(new { code, name, category, normal, request.CurrencyId,
            request.IsControlAccount, request.AllowsManualPosting, request.IsContraAccount });
        return await InSerializableTransactionAsync(async () =>
        {
            var replay = await _context.LedgerAccounts.SingleOrDefaultAsync(x =>
                x.BusinessUnitId == businessUnitId && x.IdempotencyKey == idempotencyKey);
            if (replay is not null) { EnsureReplay(replay.RequestHash, requestHash); return Map(replay); }
            if (request.CurrencyId.HasValue && !await _context.Currencies.AnyAsync(x =>
                    x.Id == request.CurrencyId && x.BusinessUnitId == businessUnitId && x.IsActive == true))
                throw new ArgumentException("The account currency is not an active tenant currency.");
            var account = new LedgerAccount
            {
                BusinessUnitId = businessUnitId, Code = code, Name = name, Category = category,
                NormalBalance = normal, CurrencyId = request.CurrencyId,
                IsControlAccount = request.IsControlAccount,
                IsContraAccount = request.IsContraAccount,
                AllowsManualPosting = request.IsControlAccount ? false : request.AllowsManualPosting,
                IdempotencyKey = idempotencyKey, RequestHash = requestHash,
                CreatedBy = Actor(actor), CreatedOn = DateTime.UtcNow
            };
            _context.LedgerAccounts.Add(account);
            await _context.SaveChangesAsync();
            await EvidenceAsync(businessUnitId, "LedgerAccount", account.Id, account.Version,
                "Created", actor, "finance.ledger-account.created", new { account.Id, account.Code, account.Category });
            await _context.SaveChangesAsync();
            return Map(account);
        });
    }

    public async Task<LedgerAccountDto> DeactivateAccountAsync(
        long businessUnitId, long accountId, DeactivateLedgerAccountRequest request, string actor)
    {
        var reason = Reason(request.Reason, "account deactivation");
        return await InSerializableTransactionAsync(async () =>
        {
            var account = await LockAccountAsync(accountId, businessUnitId);
            if (!account.IsActive) return Map(account);
            Expected(account.Version, request.ExpectedVersion, "account");
            account.IsActive = false; account.DeactivatedBy = Actor(actor);
            account.DeactivatedOn = DateTime.UtcNow; account.DeactivationReason = reason; account.Version++;
            await EvidenceAsync(businessUnitId, "LedgerAccount", account.Id, account.Version,
                "Deactivated", actor, "finance.ledger-account.deactivated", new { account.Id, reason });
            await _context.SaveChangesAsync();
            return Map(account);
        });
    }

    public async Task<IReadOnlyList<LedgerAccountDto>> GetAccountsAsync(long businessUnitId, bool includeInactive)
    {
        var query = _context.LedgerAccounts.Where(x => x.BusinessUnitId == businessUnitId);
        if (!includeInactive) query = query.Where(x => x.IsActive);
        return (await query.OrderBy(x => x.Code).ToListAsync()).Select(Map).ToArray();
    }

    public async Task<AccountingPeriodDto> CreatePeriodAsync(
        long businessUnitId, string idempotencyKey, CreateAccountingPeriodRequest request, string actor)
    {
        ValidateKey(idempotencyKey);
        var starts = request.StartsOn.Date; var ends = request.EndsOn.Date;
        if (request.FiscalYear is < 2000 or > 2200 || request.PeriodNumber is < 1 or > 99 || starts > ends)
            throw new ArgumentException("The accounting period range is invalid.");
        if (starts.Year != request.FiscalYear && ends.Year != request.FiscalYear)
            throw new ArgumentException("The period must intersect its fiscal year.");
        var name = Token(request.Name, "period name", 80);
        var requestHash = Hash(new { request.FiscalYear, request.PeriodNumber, name, starts, ends });
        return await InSerializableTransactionAsync(async () =>
        {
            _ = await RequireBookAsync(businessUnitId);
            var replay = await _context.AccountingPeriods.SingleOrDefaultAsync(x =>
                x.BusinessUnitId == businessUnitId && x.IdempotencyKey == idempotencyKey);
            if (replay is not null) { EnsureReplay(replay.RequestHash, requestHash); return Map(replay); }
            if (await _context.AccountingPeriods.AnyAsync(x => x.BusinessUnitId == businessUnitId &&
                x.StartsOn <= ends && x.EndsOn >= starts))
                throw new GeneralLedgerConflictException("Accounting periods cannot overlap.");
            if (await _context.AccountingPeriods.AnyAsync(x => x.BusinessUnitId == businessUnitId &&
                x.Status == AccountingPeriodStatuses.Closed && x.EndsOn >= starts))
                throw new GeneralLedgerConflictException("A period cannot be inserted before or within a certified close horizon.");
            var period = new AccountingPeriod
            {
                BusinessUnitId = businessUnitId, FiscalYear = request.FiscalYear,
                PeriodNumber = request.PeriodNumber, Name = name, StartsOn = starts, EndsOn = ends,
                IdempotencyKey = idempotencyKey, RequestHash = requestHash,
                CreatedBy = Actor(actor), CreatedOn = DateTime.UtcNow
            };
            _context.AccountingPeriods.Add(period);
            await _context.SaveChangesAsync();
            await EvidenceAsync(businessUnitId, "AccountingPeriod", period.Id, period.Version,
                "Created", actor, "finance.accounting-period.created", new { period.Id, period.FiscalYear, period.PeriodNumber });
            await _context.SaveChangesAsync();
            return Map(period);
        });
    }

    public async Task<AccountingPeriodDto> TransitionPeriodAsync(
        long businessUnitId, long periodId, string action, AccountingPeriodActionRequest request, string actor)
    {
        action = action.Trim().ToLowerInvariant();
        return await InSerializableTransactionAsync(async () =>
        {
            var period = await LockPeriodAsync(periodId, businessUnitId);
            Expected(period.Version, request.ExpectedVersion, "accounting period");
            var now = DateTime.UtcNow; var trustedActor = Actor(actor);
            if (action == "soft-close")
            {
                if (period.Status != AccountingPeriodStatuses.Open)
                    throw new GeneralLedgerConflictException("Only an open period can be soft-closed.");
                if (Same(period.CreatedBy, trustedActor))
                    throw new GeneralLedgerConflictException("The period creator cannot soft-close it.");
                if (await _context.JournalEntries.AnyAsync(x => x.BusinessUnitId == businessUnitId &&
                    x.AccountingPeriodId == period.Id && x.Status == JournalEntryStatuses.Draft))
                    throw new GeneralLedgerConflictException("Draft journals must be posted or cancelled before soft close.");
                period.Status = AccountingPeriodStatuses.SoftClosed;
                period.SoftClosedBy = trustedActor; period.SoftClosedOn = now;
            }
            else if (action == "close")
            {
                if (period.Status != AccountingPeriodStatuses.SoftClosed)
                    throw new GeneralLedgerConflictException("Only a soft-closed period can be closed.");
                if (Same(period.CreatedBy, trustedActor) || Same(period.SoftClosedBy, trustedActor))
                    throw new GeneralLedgerConflictException("Period close requires an independent controller.");
                var snapshot = await BuildCloseSnapshotAsync(businessUnitId, period);
                period.Status = AccountingPeriodStatuses.Closed;
                period.ClosedBy = trustedActor; period.ClosedOn = now;
                period.CloseReason = Reason(request.Reason, "period close");
                period.CloseEvidenceReference = Evidence(request.EvidenceReference);
                period.CloseTrialBalanceHash = snapshot.Hash;
                period.CloseTotalDebit = snapshot.TotalDebit;
                period.CloseTotalCredit = snapshot.TotalCredit;
                period.CloseJournalCount = snapshot.JournalCount;
            }
            else if (action == "reopen")
            {
                if (period.Status != AccountingPeriodStatuses.SoftClosed)
                    throw new GeneralLedgerConflictException("Only a soft-closed period can be reopened.");
                if (Same(period.SoftClosedBy, trustedActor))
                    throw new GeneralLedgerConflictException("The soft-close operator cannot approve reopening.");
                period.Status = AccountingPeriodStatuses.Open;
                period.ReopenedBy = trustedActor; period.ReopenedOn = now;
                period.ReopenReason = Reason(request.Reason, "period reopening");
                period.ReopenEvidenceReference = Evidence(request.EvidenceReference);
            }
            else throw new ArgumentException("Unsupported accounting-period action.");
            period.Version++;
            await EvidenceAsync(businessUnitId, "AccountingPeriod", period.Id, period.Version,
                action, actor, $"finance.accounting-period.{action}", new { period.Id, period.Status });
            await _context.SaveChangesAsync();
            if (_context.Database.IsNpgsql()) await _context.Entry(period).ReloadAsync();
            return Map(period);
        });
    }

    public async Task<IReadOnlyList<AccountingPeriodDto>> GetPeriodsAsync(long businessUnitId, int? fiscalYear)
    {
        var query = _context.AccountingPeriods.Where(x => x.BusinessUnitId == businessUnitId);
        if (fiscalYear.HasValue) query = query.Where(x => x.FiscalYear == fiscalYear.Value);
        return (await query.OrderByDescending(x => x.FiscalYear).ThenBy(x => x.PeriodNumber).ToListAsync()).Select(Map).ToArray();
    }

    public async Task<JournalEntryDto> CreateManualJournalAsync(
        long businessUnitId, string idempotencyKey, CreateJournalEntryRequest request, string actor)
    {
        ValidateKey(idempotencyKey);
        if (request.Lines is null || request.Lines.Count < 2) throw new ArgumentException("A journal requires at least two lines.");
        var normalized = NormalizeLines(request.Lines);
        var requestHash = Hash(new { request.AccountingPeriodId, request.FunctionalCurrencyId,
            AccountingDate = request.AccountingDate.Date, Description = Token(request.Description, "journal description", 500), Lines = normalized });
        return await InSerializableTransactionAsync(async () =>
        {
            var replay = await _context.JournalEntries.Include(x => x.Lines).ThenInclude(x => x.Account)
                .SingleOrDefaultAsync(x => x.BusinessUnitId == businessUnitId && x.IdempotencyKey == idempotencyKey);
            if (replay is not null) { EnsureReplay(replay.RequestHash, requestHash); return Map(replay); }
            var period = await LockPeriodAsync(request.AccountingPeriodId, businessUnitId);
            EnsureOpenDate(period, request.AccountingDate);
            var book = await RequireBookAsync(businessUnitId);
            if (request.FunctionalCurrencyId != book.FunctionalCurrencyId)
                throw new ArgumentException("The journal functional currency must match the tenant accounting book.");
            var accountIds = normalized.Select(x => x.LedgerAccountId).Distinct().ToArray();
            var accounts = await _context.LedgerAccounts.Where(x => x.BusinessUnitId == businessUnitId &&
                accountIds.Contains(x.Id)).ToDictionaryAsync(x => x.Id);
            if (accounts.Count != accountIds.Length) throw new ArgumentException("One or more ledger accounts do not exist.");
            foreach (var line in normalized)
            {
                var account = accounts[line.LedgerAccountId];
                if (!account.IsActive || !account.AllowsManualPosting || account.IsControlAccount)
                    throw new GeneralLedgerConflictException($"Account {account.Code} does not permit manual posting.");
                if (account.CurrencyId.HasValue && account.CurrencyId != line.TransactionCurrencyId)
                    throw new GeneralLedgerConflictException($"Account {account.Code} requires its configured currency.");
                if (!await _context.Currencies.AnyAsync(x => x.Id == line.TransactionCurrencyId &&
                    x.BusinessUnitId == businessUnitId && x.IsActive == true))
                    throw new ArgumentException("A transaction currency is not active for this tenant.");
                if (line.TransactionCurrencyId == request.FunctionalCurrencyId && line.ExchangeRate != 1m)
                    throw new ArgumentException("Functional-currency journal lines require an exchange rate of one.");
            }
            var debit = Round(normalized.Sum(x => x.FunctionalDebit));
            var credit = Round(normalized.Sum(x => x.FunctionalCredit));
            if (debit <= 0 || debit != credit) throw new ArgumentException("Journal functional debits and credits must balance exactly.");
            if (normalized.GroupBy(x => x.TransactionCurrencyId)
                .Any(group => Round(group.Sum(x => x.TransactionDebit)) != Round(group.Sum(x => x.TransactionCredit))))
                throw new ArgumentException("Journal transaction debits and credits must balance within each currency.");
            var journal = new JournalEntry
            {
                BusinessUnitId = businessUnitId, AccountingPeriodId = period.Id,
                FunctionalCurrencyId = request.FunctionalCurrencyId, AccountingDate = request.AccountingDate.Date,
                Description = Token(request.Description, "journal description", 500), SourceType = "Manual",
                TotalDebit = debit, TotalCredit = credit, IdempotencyKey = idempotencyKey,
                RequestHash = requestHash, CreatedBy = Actor(actor), CreatedOn = DateTime.UtcNow,
                Lines = normalized.Select((line, index) => new JournalEntryLine
                {
                    BusinessUnitId = businessUnitId, Sequence = index + 1,
                    LedgerAccountId = line.LedgerAccountId, Description = line.Description,
                    TransactionCurrencyId = line.TransactionCurrencyId, ExchangeRate = line.ExchangeRate,
                    TransactionDebit = line.TransactionDebit, TransactionCredit = line.TransactionCredit,
                    FunctionalDebit = line.FunctionalDebit, FunctionalCredit = line.FunctionalCredit,
                    SourceReference = line.SourceReference
                }).ToList()
            };
            _context.JournalEntries.Add(journal);
            await _context.SaveChangesAsync();
            await EvidenceAsync(businessUnitId, "JournalEntry", journal.Id, journal.Version,
                "DraftCreated", actor, "finance.journal.draft-created", new { journal.Id, debit, credit });
            await _context.SaveChangesAsync();
            return Map(journal);
        });
    }

    public Task<JournalEntryDto> PostJournalAsync(long businessUnitId, long journalId, JournalActionRequest request, string actor)
        => TransitionDraftJournalAsync(businessUnitId, journalId, request, actor, post: true);

    public Task<JournalEntryDto> CancelJournalAsync(long businessUnitId, long journalId, JournalActionRequest request, string actor)
        => TransitionDraftJournalAsync(businessUnitId, journalId, request, actor, post: false);

    private async Task<JournalEntryDto> TransitionDraftJournalAsync(
        long businessUnitId, long journalId, JournalActionRequest request, string actor, bool post)
    {
        return await InSerializableTransactionAsync(async () =>
        {
            var journal = await LockJournalAsync(journalId, businessUnitId);
            if (post && journal.Status == JournalEntryStatuses.Posted) return Map(journal);
            if (!post && journal.Status == JournalEntryStatuses.Cancelled) return Map(journal);
            if (journal.SourceType != "Manual")
                throw new GeneralLedgerConflictException("Source-owned journals must transition through their owning module.");
            Expected(journal.Version, request.ExpectedVersion, "journal");
            if (journal.Status != JournalEntryStatuses.Draft)
                throw new GeneralLedgerConflictException("Only a draft journal can transition.");
            var period = await LockPeriodAsync(journal.AccountingPeriodId, businessUnitId);
            var now = DateTime.UtcNow; var trustedActor = Actor(actor);
            if (post)
            {
                EnsureOpenDate(period, journal.AccountingDate);
                if (Same(journal.CreatedBy, trustedActor))
                    throw new GeneralLedgerConflictException("The journal creator cannot post the same journal.");
                ValidateJournalGraph(journal);
                if (!_context.Database.IsNpgsql())
                    journal.EntryNumber = await AllocateJournalNumberAsync(businessUnitId, period.FiscalYear);
                journal.Status = JournalEntryStatuses.Posted; journal.PostedBy = trustedActor; journal.PostedOn = now;
            }
            else
            {
                journal.Status = JournalEntryStatuses.Cancelled; journal.CancelledBy = trustedActor;
                journal.CancelledOn = now; journal.CancellationReason = Reason(request.Reason, "journal cancellation");
            }
            journal.Version++;
            var action = post ? "Posted" : "Cancelled";
            await EvidenceAsync(businessUnitId, "JournalEntry", journal.Id, journal.Version,
                action, actor, $"finance.journal.{action.ToLowerInvariant()}", new { journal.Id, journal.EntryNumber });
            await _context.SaveChangesAsync();
            if (_context.Database.IsNpgsql()) await _context.Entry(journal).ReloadAsync();
            return Map(journal);
        });
    }

    public async Task<JournalEntryDto> ReverseJournalAsync(
        long businessUnitId, long journalId, string idempotencyKey, JournalActionRequest request, string actor)
    {
        ValidateKey(idempotencyKey);
        var reason = Reason(request.Reason, "journal reversal"); var evidence = Evidence(request.EvidenceReference);
        return await InSerializableTransactionAsync(async () =>
        {
            var replay = await _context.JournalEntries.Include(x => x.Lines).ThenInclude(x => x.Account)
                .SingleOrDefaultAsync(x => x.BusinessUnitId == businessUnitId && x.IdempotencyKey == idempotencyKey);
            if (replay is not null)
            {
                if (replay.ReversesJournalEntryId != journalId) throw new GeneralLedgerConflictException("Idempotency key belongs to another reversal.");
                var replayDate = (request.ReversalAccountingDate ?? replay.AccountingDate).Date;
                EnsureReplay(replay.RequestHash, Hash(new
                {
                    OriginalJournalId = journalId,
                    OriginalVersion = request.ExpectedVersion,
                    ReversalDate = replayDate,
                    Reason = reason,
                    Evidence = evidence
                }));
                return Map(replay);
            }
            var original = await LockJournalAsync(journalId, businessUnitId);
            if (original.SourceType != "Manual")
                throw new GeneralLedgerConflictException("Source-owned journals must be reversed through their owning module.");
            Expected(original.Version, request.ExpectedVersion, "journal");
            if (original.Status != JournalEntryStatuses.Posted)
                throw new GeneralLedgerConflictException("Only a posted journal can be reversed.");
            var trustedActor = Actor(actor);
            if (Same(original.CreatedBy, trustedActor) || Same(original.PostedBy, trustedActor))
                throw new GeneralLedgerConflictException("Journal reversal requires an independent controller.");
            var reversalDate = (request.ReversalAccountingDate ?? DateTime.UtcNow).Date;
            var reversalPeriod = await _context.AccountingPeriods.Where(x => x.BusinessUnitId == businessUnitId &&
                x.Status == AccountingPeriodStatuses.Open && x.StartsOn <= reversalDate && x.EndsOn >= reversalDate)
                .OrderBy(x => x.PeriodNumber).FirstOrDefaultAsync()
                ?? throw new GeneralLedgerConflictException("No open accounting period contains the reversal date.");
            var reversal = new JournalEntry
            {
                BusinessUnitId = businessUnitId, AccountingPeriodId = reversalPeriod.Id,
                FunctionalCurrencyId = original.FunctionalCurrencyId, AccountingDate = reversalDate,
                Description = Limit($"Reversal of {original.EntryNumber}: {reason}", 500), SourceType = "JournalReversal",
                SourceReference = original.EntryNumber, SourceVersion = original.Version,
                TotalDebit = original.TotalCredit, TotalCredit = original.TotalDebit,
                ReversesJournalEntryId = original.Id, IdempotencyKey = idempotencyKey,
                RequestHash = Hash(new
                {
                    OriginalJournalId = original.Id,
                    OriginalVersion = original.Version,
                    ReversalDate = reversalDate,
                    Reason = reason,
                    Evidence = evidence
                }),
                CreatedBy = "system:journal-reversal", CreatedOn = DateTime.UtcNow,
                Lines = original.Lines.OrderBy(x => x.Sequence).Select(x => new JournalEntryLine
                {
                    BusinessUnitId = businessUnitId, Sequence = x.Sequence, LedgerAccountId = x.LedgerAccountId,
                    Description = Limit($"Reversal: {x.Description}", 500), TransactionCurrencyId = x.TransactionCurrencyId,
                    ExchangeRate = x.ExchangeRate, TransactionDebit = x.TransactionCredit,
                    TransactionCredit = x.TransactionDebit, FunctionalDebit = x.FunctionalCredit,
                    FunctionalCredit = x.FunctionalDebit, SourceReference = x.SourceReference
                }).ToList()
            };
            _context.JournalEntries.Add(reversal);
            await _context.SaveChangesAsync();
            if (!_context.Database.IsNpgsql())
                reversal.EntryNumber = await AllocateJournalNumberAsync(businessUnitId, reversalPeriod.FiscalYear);
            reversal.Status = JournalEntryStatuses.Posted; reversal.PostedBy = trustedActor;
            reversal.PostedOn = DateTime.UtcNow; reversal.Version++;
            original.Status = JournalEntryStatuses.Reversed; original.ReversedBy = trustedActor;
            original.ReversedOn = reversal.PostedOn; original.ReversalReason = reason;
            original.ReversalEvidenceReference = evidence; original.Version++;
            await EvidenceAsync(businessUnitId, "JournalEntry", original.Id, original.Version,
                "Reversed", actor, "finance.journal.reversed", new { original.Id, ReversalJournalId = reversal.Id });
            await EvidenceAsync(businessUnitId, "JournalEntry", reversal.Id, reversal.Version,
                "Posted", actor, "finance.journal.reversal-posted",
                new { ReversalJournalId = reversal.Id, OriginalJournalId = original.Id });
            await _context.SaveChangesAsync();
            if (_context.Database.IsNpgsql()) await _context.Entry(reversal).ReloadAsync();
            return Map(reversal);
        });
    }

    public async Task<JournalEntryDto> GetJournalAsync(long businessUnitId, long journalId)
        => Map(await _context.JournalEntries.Include(x => x.Lines).ThenInclude(x => x.Account)
            .SingleOrDefaultAsync(x => x.BusinessUnitId == businessUnitId && x.Id == journalId)
            ?? throw new KeyNotFoundException("Journal not found."));

    public async Task<IReadOnlyList<JournalEntryDto>> GetJournalsAsync(long businessUnitId, long? periodId, string? status)
    {
        var query = _context.JournalEntries.Include(x => x.Lines).ThenInclude(x => x.Account)
            .Where(x => x.BusinessUnitId == businessUnitId);
        if (periodId.HasValue) query = query.Where(x => x.AccountingPeriodId == periodId.Value);
        if (!string.IsNullOrWhiteSpace(status)) query = query.Where(x => x.Status == status.Trim());
        return (await query.OrderByDescending(x => x.AccountingDate).ThenByDescending(x => x.Id).Take(500).ToListAsync()).Select(Map).ToArray();
    }

    public async Task<TrialBalanceDto> GetTrialBalanceAsync(
        long businessUnitId, DateTime from, DateTime through, long functionalCurrencyId)
    {
        from = from.Date; through = through.Date;
        if (from > through) throw new ArgumentException("Trial-balance start cannot follow its end.");
        var book = await RequireBookAsync(businessUnitId);
        if (functionalCurrencyId != book.FunctionalCurrencyId)
            throw new ArgumentException("Trial balance currency must match the tenant accounting book.");
        var rows = await _context.JournalEntryLines.AsNoTracking()
            .Where(x => x.BusinessUnitId == businessUnitId &&
                x.JournalEntry.FunctionalCurrencyId == functionalCurrencyId &&
                (x.JournalEntry.Status == JournalEntryStatuses.Posted || x.JournalEntry.Status == JournalEntryStatuses.Reversed) &&
                x.JournalEntry.AccountingDate <= through)
            .GroupBy(x => new { x.LedgerAccountId, x.Account.Code, x.Account.Name, x.Account.Category, x.Account.NormalBalance })
            .Select(x => new
            {
                x.Key,
                BeginningDebit = x.Sum(y => y.JournalEntry.AccountingDate < from ? y.FunctionalDebit : 0m),
                BeginningCredit = x.Sum(y => y.JournalEntry.AccountingDate < from ? y.FunctionalCredit : 0m),
                Debit = x.Sum(y => y.JournalEntry.AccountingDate >= from ? y.FunctionalDebit : 0m),
                Credit = x.Sum(y => y.JournalEntry.AccountingDate >= from ? y.FunctionalCredit : 0m),
                DrillThroughCount = x.Count(y => y.JournalEntry.AccountingDate >= from)
            })
            .OrderBy(x => x.Key.Code).ToListAsync();
        var lines = rows.Select(x =>
        {
            var sign = x.Key.NormalBalance == LedgerNormalBalances.Debit ? 1m : -1m;
            var beginning = Round((x.BeginningDebit - x.BeginningCredit) * sign);
            var ending = Round(beginning + ((x.Debit - x.Credit) * sign));
            var rawEnding = Round((x.BeginningDebit + x.Debit) - (x.BeginningCredit + x.Credit));
            return new TrialBalanceLineDto(x.Key.LedgerAccountId, x.Key.Code, x.Key.Name,
                x.Key.Category, x.Key.NormalBalance, beginning, Round(x.Debit), Round(x.Credit), ending,
                rawEnding > 0 ? rawEnding : 0m, rawEnding < 0 ? -rawEnding : 0m, x.DrillThroughCount);
        }).ToArray();
        return new TrialBalanceDto(from, through, functionalCurrencyId, Round(lines.Sum(x => x.Debit)),
            Round(lines.Sum(x => x.Credit)), lines);
    }

    private sealed record CloseSnapshot(string Hash, decimal TotalDebit, decimal TotalCredit, int JournalCount);

    private async Task<CloseSnapshot> BuildCloseSnapshotAsync(long businessUnitId, AccountingPeriod period)
    {
        var book = await RequireBookAsync(businessUnitId);
        var journals = _context.JournalEntries.Where(x => x.BusinessUnitId == businessUnitId &&
            x.FunctionalCurrencyId == book.FunctionalCurrencyId && x.AccountingDate <= period.EndsOn &&
            (x.Status == JournalEntryStatuses.Posted || x.Status == JournalEntryStatuses.Reversed));
        var totals = await journals.GroupBy(_ => 1).Select(group => new
        {
            Debit = group.Sum(x => x.TotalDebit), Credit = group.Sum(x => x.TotalCredit), Count = group.Count()
        }).SingleOrDefaultAsync();
        var balances = await _context.JournalEntryLines.AsNoTracking()
            .Where(x => x.BusinessUnitId == businessUnitId && x.JournalEntry.FunctionalCurrencyId == book.FunctionalCurrencyId &&
                x.JournalEntry.AccountingDate <= period.EndsOn &&
                (x.JournalEntry.Status == JournalEntryStatuses.Posted || x.JournalEntry.Status == JournalEntryStatuses.Reversed))
            .GroupBy(x => x.LedgerAccountId)
            .Select(group => new { AccountId = group.Key, Debit = group.Sum(x => x.FunctionalDebit), Credit = group.Sum(x => x.FunctionalCredit) })
            .OrderBy(x => x.AccountId).ToListAsync();
        var canonical = string.Join('|', balances.Select(x => string.Create(CultureInfo.InvariantCulture,
            $"{x.AccountId}:{Round(x.Debit):F2}:{Round(x.Credit):F2}")));
        var debit = Round(totals?.Debit ?? 0m); var credit = Round(totals?.Credit ?? 0m);
        if (debit != credit) throw new GeneralLedgerConflictException("The ledger must balance before period close.");
        return new CloseSnapshot(HashText(canonical), debit, credit, totals?.Count ?? 0);
    }

    private async Task<LedgerAccount> LockAccountAsync(long id, long businessUnitId)
    {
        IQueryable<LedgerAccount> query = _context.LedgerAccounts;
        if (_context.Database.IsNpgsql()) query = _context.LedgerAccounts.FromSqlInterpolated(
            $"SELECT * FROM \"LedgerAccounts\" WHERE \"BusinessUnitId\" = {businessUnitId} AND \"Id\" = {id} FOR UPDATE");
        return await query.SingleOrDefaultAsync(x => x.BusinessUnitId == businessUnitId && x.Id == id)
            ?? throw new KeyNotFoundException("Ledger account not found.");
    }

    private async Task<LedgerBook> RequireBookAsync(long businessUnitId)
        => await _context.LedgerBooks.SingleOrDefaultAsync(x => x.BusinessUnitId == businessUnitId)
            ?? throw new GeneralLedgerConflictException("Create the tenant accounting book before using the general ledger.");

    private async Task<AccountingPeriod> LockPeriodAsync(long id, long businessUnitId)
    {
        IQueryable<AccountingPeriod> query = _context.AccountingPeriods;
        if (_context.Database.IsNpgsql()) query = _context.AccountingPeriods.FromSqlInterpolated(
            $"SELECT * FROM \"AccountingPeriods\" WHERE \"BusinessUnitId\" = {businessUnitId} AND \"Id\" = {id} FOR UPDATE");
        return await query.SingleOrDefaultAsync(x => x.BusinessUnitId == businessUnitId && x.Id == id)
            ?? throw new KeyNotFoundException("Accounting period not found.");
    }

    private async Task<JournalEntry> LockJournalAsync(long id, long businessUnitId)
    {
        IQueryable<JournalEntry> query = _context.JournalEntries.Include(x => x.Lines).ThenInclude(x => x.Account);
        if (_context.Database.IsNpgsql()) query = _context.JournalEntries.FromSqlInterpolated(
            $"SELECT * FROM \"JournalEntries\" WHERE \"BusinessUnitId\" = {businessUnitId} AND \"Id\" = {id} FOR UPDATE")
            .Include(x => x.Lines).ThenInclude(x => x.Account);
        return await query.SingleOrDefaultAsync(x => x.BusinessUnitId == businessUnitId && x.Id == id)
            ?? throw new KeyNotFoundException("Journal not found.");
    }

    private async Task<string> AllocateJournalNumberAsync(long businessUnitId, int fiscalYear)
    {
        await _context.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "LegalDocumentCounters" ("BusinessUnitId", "DocumentType", "FiscalYear", "NextNumber")
            VALUES ({businessUnitId}, {"Journal"}, {fiscalYear}, 1)
            ON CONFLICT ("BusinessUnitId", "DocumentType", "FiscalYear") DO NOTHING
            """);
        IQueryable<LegalDocumentCounter> query = _context.LegalDocumentCounters;
        if (_context.Database.IsNpgsql()) query = _context.LegalDocumentCounters.FromSqlInterpolated($"""
            SELECT * FROM "LegalDocumentCounters" WHERE "BusinessUnitId" = {businessUnitId}
              AND "DocumentType" = {"Journal"} AND "FiscalYear" = {fiscalYear} FOR UPDATE
            """);
        var counter = await query.SingleAsync(x => x.BusinessUnitId == businessUnitId &&
            x.DocumentType == "Journal" && x.FiscalYear == fiscalYear);
        var number = counter.NextNumber++;
        return $"JRN-{fiscalYear}-{number:D8}";
    }

    private static void ValidateJournalGraph(JournalEntry journal)
    {
        if (journal.Lines.Count < 2) throw new GeneralLedgerConflictException("A journal requires at least two immutable lines.");
        if (journal.Lines.Any(x => !x.Account.IsActive ||
            (journal.SourceType == "Manual" && (!x.Account.AllowsManualPosting || x.Account.IsControlAccount))))
            throw new GeneralLedgerConflictException("A journal line account is no longer eligible for posting.");
        var debit = Round(journal.Lines.Sum(x => x.FunctionalDebit));
        var credit = Round(journal.Lines.Sum(x => x.FunctionalCredit));
        if (debit != journal.TotalDebit || credit != journal.TotalCredit || debit <= 0 || debit != credit)
            throw new GeneralLedgerConflictException("Journal lines no longer reconcile to the balanced header.");
    }

    private async Task EvidenceAsync(long businessUnitId, string aggregateType, long aggregateId,
        long version, string action, string actor, string eventType, object detail)
    {
        if (_context.Database.IsNpgsql()) return;
        var now = DateTime.UtcNow;
        _context.CommercialFinanceAudits.Add(new CommercialFinanceAudit
        {
            BusinessUnitId = businessUnitId, AggregateType = aggregateType, AggregateId = aggregateId,
            Action = action, Actor = Actor(actor), OccurredOn = now, DetailJson = JsonSerializer.Serialize(detail)
        });
        _context.FinanceOutboxMessages.Add(new FinanceOutboxMessage
        {
            BusinessUnitId = businessUnitId, AggregateType = aggregateType, AggregateId = aggregateId,
            AggregateVersion = version, EventType = eventType, Payload = JsonSerializer.Serialize(detail),
            OccurredOn = now, AvailableOn = now
        });
    }

    private async Task<T> InSerializableTransactionAsync<T>(Func<Task<T>> action)
    {
        for (var attempt = 1; ; attempt++)
        {
            await using var transaction = await _context.Database.BeginTransactionAsync(IsolationLevel.Serializable);
            try { var result = await action(); await transaction.CommitAsync(); return result; }
            catch (Exception ex) when (attempt < 3 && IsRetryable(ex))
            {
                await transaction.RollbackAsync(); _context.ChangeTracker.Clear();
                await Task.Delay(TimeSpan.FromMilliseconds(20 * attempt));
            }
        }
    }

    private static bool IsRetryable(Exception exception)
        => exception is DbUpdateException { InnerException: PostgresException p } &&
               p.SqlState is PostgresErrorCodes.SerializationFailure or PostgresErrorCodes.DeadlockDetected
           || exception is PostgresException direct &&
               direct.SqlState is PostgresErrorCodes.SerializationFailure or PostgresErrorCodes.DeadlockDetected;

    private sealed record NormalizedLine(long LedgerAccountId, string Description, long TransactionCurrencyId,
        decimal ExchangeRate, decimal TransactionDebit, decimal TransactionCredit,
        decimal FunctionalDebit, decimal FunctionalCredit, string? SourceReference);

    private static NormalizedLine[] NormalizeLines(IReadOnlyList<CreateJournalEntryLineRequest> lines)
        => lines.Select(line =>
        {
            var debit = Round(line.Debit); var credit = Round(line.Credit);
            var rate = decimal.Round(line.ExchangeRate, 10, MidpointRounding.AwayFromZero);
            if (rate <= 0 || debit < 0 || credit < 0 || (debit > 0) == (credit > 0))
                throw new ArgumentException("Each journal line requires one positive debit or credit and a positive exchange rate.");
            return new NormalizedLine(line.LedgerAccountId, Token(line.Description, "journal line description", 500),
                line.TransactionCurrencyId, rate, debit, credit, Round(debit * rate), Round(credit * rate),
                Optional(line.SourceReference, 200));
        }).ToArray();

    private static void EnsureOpenDate(AccountingPeriod period, DateTime date)
    {
        if (period.Status != AccountingPeriodStatuses.Open || date.Date < period.StartsOn || date.Date > period.EndsOn)
            throw new GeneralLedgerConflictException("The accounting date is not in an open period.");
    }

    private static LedgerBookDto Map(LedgerBook x) => new(x.Id, x.Name, x.FunctionalCurrencyId,
        x.TimeZoneId, x.FiscalYearStartMonth, x.Version, x.CreatedBy, x.CreatedOn,
        x.ReceivablesControlAccountId, x.UnappliedCashAccountId);
    private static LedgerAccountDto Map(LedgerAccount x) => new(x.Id, x.Code, x.Name, x.Category,
        x.NormalBalance, x.CurrencyId, x.IsControlAccount, x.IsContraAccount, x.AllowsManualPosting, x.IsActive,
        x.Version, x.CreatedBy, x.CreatedOn, x.DeactivatedBy, x.DeactivatedOn, x.DeactivationReason);
    private static AccountingPeriodDto Map(AccountingPeriod x) => new(x.Id, x.FiscalYear,
        x.PeriodNumber, x.Name, x.StartsOn, x.EndsOn, x.Status, x.Version, x.CreatedBy, x.CreatedOn,
        x.SoftClosedBy, x.SoftClosedOn, x.ClosedBy, x.ClosedOn, x.CloseReason,
        x.CloseEvidenceReference, x.CloseTrialBalanceHash, x.CloseTotalDebit, x.CloseTotalCredit,
        x.CloseJournalCount, x.ReopenedBy, x.ReopenedOn,
        x.ReopenReason, x.ReopenEvidenceReference);
    private static JournalEntryDto Map(JournalEntry x) => new(x.Id, x.AccountingPeriodId,
        x.FunctionalCurrencyId, x.EntryNumber, x.AccountingDate, x.Status, x.Description,
        x.SourceType, x.SourceReference, x.SourceVersion, x.TotalDebit, x.TotalCredit,
        x.ReversesJournalEntryId, x.Version, x.CreatedBy, x.CreatedOn, x.PostedBy, x.PostedOn,
        x.CancelledBy, x.CancelledOn, x.CancellationReason, x.ReversedBy, x.ReversedOn,
        x.ReversalReason, x.ReversalEvidenceReference, x.Lines.OrderBy(line => line.Sequence)
            .Select(line => new JournalEntryLineDto(line.Id, line.Sequence, line.LedgerAccountId,
                line.Account.Code, line.Account.Name, line.Description, line.TransactionCurrencyId,
                line.ExchangeRate, line.TransactionDebit, line.TransactionCredit,
                line.FunctionalDebit, line.FunctionalCredit, line.SourceReference)).ToArray());

    private static string Actor(string actor) => Token(actor, "authenticated actor", 255);
    private static string Token(string value, string field, int max)
    {
        value = value?.Trim() ?? string.Empty;
        if (value.Length == 0 || value.Length > max) throw new ArgumentException($"A valid {field} is required.");
        return value;
    }
    private static string Reason(string? value, string field)
    {
        var reason = Token(value ?? string.Empty, field, 500);
        if (reason.Length < 20) throw new ArgumentException($"{field} requires at least 20 characters.");
        return reason;
    }
    private static string Evidence(string? value)
    {
        var evidence = Token(value ?? string.Empty, "evidence reference", 500);
        if (evidence.Length < 8) throw new ArgumentException("An evidence reference of at least eight characters is required.");
        return evidence;
    }
    private static string? Optional(string? value, int max)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        value = value.Trim(); if (value.Length > max) throw new ArgumentException("The optional value is too long.");
        return value;
    }
    private static string Limit(string value, int max) => value.Length <= max ? value : value[..max];
    private static string Category(string value) => value.Trim() switch
    {
        LedgerAccountCategories.Asset => LedgerAccountCategories.Asset,
        LedgerAccountCategories.Liability => LedgerAccountCategories.Liability,
        LedgerAccountCategories.Equity => LedgerAccountCategories.Equity,
        LedgerAccountCategories.Revenue => LedgerAccountCategories.Revenue,
        LedgerAccountCategories.Expense => LedgerAccountCategories.Expense,
        _ => throw new ArgumentException("Unsupported ledger account category.")
    };
    private static string NormalBalance(string value) => value.Trim() switch
    {
        LedgerNormalBalances.Debit => LedgerNormalBalances.Debit,
        LedgerNormalBalances.Credit => LedgerNormalBalances.Credit,
        _ => throw new ArgumentException("Unsupported normal balance.")
    };
    private static decimal Round(decimal value) => decimal.Round(value, 2, MidpointRounding.AwayFromZero);
    private static bool Same(string? left, string? right) => string.Equals(left?.Trim(), right?.Trim(), StringComparison.OrdinalIgnoreCase);
    private static void Expected(long actual, long expected, string aggregate)
    { if (actual != expected) throw new GeneralLedgerConflictException($"The {aggregate} changed; reload it before continuing."); }
    private static void ValidateKey(string key)
    { if (string.IsNullOrWhiteSpace(key) || key.Trim().Length > 128) throw new ArgumentException("A valid Idempotency-Key is required."); }
    private static void EnsureReplay(string stored, string current)
    { if (!CryptographicOperations.FixedTimeEquals(Convert.FromHexString(stored), Convert.FromHexString(current))) throw new GeneralLedgerConflictException("Idempotency key was already used for a different request."); }
    private static string Hash(object value) => Convert.ToHexString(SHA256.HashData(
        Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value)))).ToLowerInvariant();
    private static string HashText(string value) => Convert.ToHexString(SHA256.HashData(
        Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}
