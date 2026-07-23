namespace ERP_RFQ_Automation.GeneralLedger;

public sealed record CreateLedgerAccountRequest(
    string Code, string Name, string Category, string NormalBalance, long? CurrencyId,
    bool IsControlAccount, bool AllowsManualPosting, bool IsContraAccount = false);
public sealed record CreateLedgerBookRequest(
    string Name, long FunctionalCurrencyId, string TimeZoneId, int FiscalYearStartMonth);
public sealed record ConfigureReceivablesPostingRequest(long ExpectedVersion,
    long ReceivablesControlAccountId, long UnappliedCashAccountId);
public sealed record DeactivateLedgerAccountRequest(long ExpectedVersion, string Reason);
public sealed record CreateAccountingPeriodRequest(
    int FiscalYear, int PeriodNumber, string Name, DateTime StartsOn, DateTime EndsOn);
public sealed record AccountingPeriodActionRequest(
    long ExpectedVersion, string? Reason = null, string? EvidenceReference = null);
public sealed record CreateJournalEntryRequest(
    long AccountingPeriodId, long FunctionalCurrencyId, DateTime AccountingDate,
    string Description, IReadOnlyList<CreateJournalEntryLineRequest> Lines);
public sealed record CreateJournalEntryLineRequest(
    long LedgerAccountId, string Description, long TransactionCurrencyId, decimal ExchangeRate,
    decimal Debit, decimal Credit, string? SourceReference = null);
public sealed record JournalActionRequest(
    long ExpectedVersion, string? Reason = null, string? EvidenceReference = null,
    DateTime? ReversalAccountingDate = null);

public sealed record LedgerAccountDto(
    long Id, string Code, string Name, string Category, string NormalBalance, long? CurrencyId,
    bool IsControlAccount, bool IsContraAccount, bool AllowsManualPosting, bool IsActive, long Version,
    string CreatedBy, DateTime CreatedOn, string? DeactivatedBy, DateTime? DeactivatedOn,
    string? DeactivationReason);
public sealed record LedgerBookDto(
    long Id, string Name, long FunctionalCurrencyId, string TimeZoneId, int FiscalYearStartMonth,
    long Version, string CreatedBy, DateTime CreatedOn, long? ReceivablesControlAccountId = null,
    long? UnappliedCashAccountId = null);
public sealed record AccountingPeriodDto(
    long Id, int FiscalYear, int PeriodNumber, string Name, DateTime StartsOn, DateTime EndsOn,
    string Status, long Version, string CreatedBy, DateTime CreatedOn,
    string? SoftClosedBy, DateTime? SoftClosedOn, string? ClosedBy, DateTime? ClosedOn,
    string? CloseReason, string? CloseEvidenceReference, string? CloseTrialBalanceHash,
    decimal? CloseTotalDebit, decimal? CloseTotalCredit, int? CloseJournalCount,
    string? ReopenedBy, DateTime? ReopenedOn, string? ReopenReason,
    string? ReopenEvidenceReference);
public sealed record JournalEntryLineDto(
    long Id, int Sequence, long LedgerAccountId, string AccountCode, string AccountName,
    string Description, long TransactionCurrencyId, decimal ExchangeRate,
    decimal TransactionDebit, decimal TransactionCredit, decimal FunctionalDebit,
    decimal FunctionalCredit, string? SourceReference);
public sealed record JournalEntryDto(
    long Id, long AccountingPeriodId, long FunctionalCurrencyId, string? EntryNumber,
    DateTime AccountingDate, string Status, string Description, string SourceType,
    string? SourceReference, long? SourceVersion, decimal TotalDebit, decimal TotalCredit,
    long? ReversesJournalEntryId, long Version, string CreatedBy, DateTime CreatedOn,
    string? PostedBy, DateTime? PostedOn, string? CancelledBy, DateTime? CancelledOn,
    string? CancellationReason, string? ReversedBy, DateTime? ReversedOn,
    string? ReversalReason, string? ReversalEvidenceReference,
    IReadOnlyList<JournalEntryLineDto> Lines);
public sealed record TrialBalanceLineDto(
    long LedgerAccountId, string AccountCode, string AccountName, string Category,
    string NormalBalance, decimal BeginningBalance, decimal Debit, decimal Credit,
    decimal EndingBalance, decimal EndingDebit, decimal EndingCredit, int DrillThroughCount);
public sealed record TrialBalanceDto(
    DateTime From, DateTime Through, long FunctionalCurrencyId,
    decimal TotalDebit, decimal TotalCredit, IReadOnlyList<TrialBalanceLineDto> Lines);

public sealed class GeneralLedgerConflictException(string message) : InvalidOperationException(message);
