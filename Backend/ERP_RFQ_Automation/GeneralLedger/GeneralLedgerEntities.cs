namespace ERP_RFQ_Automation.GeneralLedger;

public static class LedgerAccountCategories
{
    public const string Asset = "Asset";
    public const string Liability = "Liability";
    public const string Equity = "Equity";
    public const string Revenue = "Revenue";
    public const string Expense = "Expense";
}

public static class LedgerNormalBalances
{
    public const string Debit = "Debit";
    public const string Credit = "Credit";
}

public static class AccountingPeriodStatuses
{
    public const string Open = "Open";
    public const string SoftClosed = "SoftClosed";
    public const string Closed = "Closed";
}

public static class JournalEntryStatuses
{
    public const string Draft = "Draft";
    public const string Posted = "Posted";
    public const string Cancelled = "Cancelled";
    public const string Reversed = "Reversed";
}

public sealed class LedgerBook
{
    public long Id { get; set; }
    public long BusinessUnitId { get; set; }
    public string Name { get; set; } = null!;
    public long FunctionalCurrencyId { get; set; }
    public string TimeZoneId { get; set; } = "UTC";
    public int FiscalYearStartMonth { get; set; } = 1;
    public long? ReceivablesControlAccountId { get; set; }
    public long? UnappliedCashAccountId { get; set; }
    public string IdempotencyKey { get; set; } = null!;
    public string RequestHash { get; set; } = null!;
    public long Version { get; set; } = 1;
    public string CreatedBy { get; set; } = null!;
    public DateTime CreatedOn { get; set; }
}

public sealed class LedgerAccount
{
    public long Id { get; set; }
    public long BusinessUnitId { get; set; }
    public string Code { get; set; } = null!;
    public string Name { get; set; } = null!;
    public string Category { get; set; } = null!;
    public string NormalBalance { get; set; } = null!;
    public long? CurrencyId { get; set; }
    public bool IsControlAccount { get; set; }
    public bool IsContraAccount { get; set; }
    public bool AllowsManualPosting { get; set; } = true;
    public bool IsActive { get; set; } = true;
    public string IdempotencyKey { get; set; } = null!;
    public string RequestHash { get; set; } = null!;
    public long Version { get; set; } = 1;
    public string CreatedBy { get; set; } = null!;
    public DateTime CreatedOn { get; set; }
    public string? DeactivatedBy { get; set; }
    public DateTime? DeactivatedOn { get; set; }
    public string? DeactivationReason { get; set; }

    public ICollection<JournalEntryLine> JournalLines { get; set; } = new List<JournalEntryLine>();
}

public sealed class AccountingPeriod
{
    public long Id { get; set; }
    public long BusinessUnitId { get; set; }
    public int FiscalYear { get; set; }
    public int PeriodNumber { get; set; }
    public string Name { get; set; } = null!;
    public DateTime StartsOn { get; set; }
    public DateTime EndsOn { get; set; }
    public string Status { get; set; } = AccountingPeriodStatuses.Open;
    public string IdempotencyKey { get; set; } = null!;
    public string RequestHash { get; set; } = null!;
    public long Version { get; set; } = 1;
    public string CreatedBy { get; set; } = null!;
    public DateTime CreatedOn { get; set; }
    public string? SoftClosedBy { get; set; }
    public DateTime? SoftClosedOn { get; set; }
    public string? ClosedBy { get; set; }
    public DateTime? ClosedOn { get; set; }
    public string? CloseReason { get; set; }
    public string? CloseEvidenceReference { get; set; }
    public string? CloseTrialBalanceHash { get; set; }
    public decimal? CloseTotalDebit { get; set; }
    public decimal? CloseTotalCredit { get; set; }
    public int? CloseJournalCount { get; set; }
    public string? ReopenedBy { get; set; }
    public DateTime? ReopenedOn { get; set; }
    public string? ReopenReason { get; set; }
    public string? ReopenEvidenceReference { get; set; }

    public ICollection<JournalEntry> JournalEntries { get; set; } = new List<JournalEntry>();
}

public sealed class JournalEntry
{
    public long Id { get; set; }
    public long BusinessUnitId { get; set; }
    public long AccountingPeriodId { get; set; }
    public long FunctionalCurrencyId { get; set; }
    public string? EntryNumber { get; set; }
    public DateTime AccountingDate { get; set; }
    public string Status { get; set; } = JournalEntryStatuses.Draft;
    public string Description { get; set; } = null!;
    public string SourceType { get; set; } = "Manual";
    public string? SourceReference { get; set; }
    public long? SourceVersion { get; set; }
    public decimal TotalDebit { get; set; }
    public decimal TotalCredit { get; set; }
    public long? ReversesJournalEntryId { get; set; }
    public string IdempotencyKey { get; set; } = null!;
    public string RequestHash { get; set; } = null!;
    public long Version { get; set; } = 1;
    public string CreatedBy { get; set; } = null!;
    public DateTime CreatedOn { get; set; }
    public string? PostedBy { get; set; }
    public DateTime? PostedOn { get; set; }
    public string? CancelledBy { get; set; }
    public DateTime? CancelledOn { get; set; }
    public string? CancellationReason { get; set; }
    public string? ReversedBy { get; set; }
    public DateTime? ReversedOn { get; set; }
    public string? ReversalReason { get; set; }
    public string? ReversalEvidenceReference { get; set; }

    public AccountingPeriod Period { get; set; } = null!;
    public JournalEntry? ReversesJournalEntry { get; set; }
    public JournalEntry? ReversalJournalEntry { get; set; }
    public ICollection<JournalEntryLine> Lines { get; set; } = new List<JournalEntryLine>();
}

public sealed class JournalEntryLine
{
    public long Id { get; set; }
    public long BusinessUnitId { get; set; }
    public long JournalEntryId { get; set; }
    public int Sequence { get; set; }
    public long LedgerAccountId { get; set; }
    public string Description { get; set; } = null!;
    public long TransactionCurrencyId { get; set; }
    public decimal ExchangeRate { get; set; }
    public decimal TransactionDebit { get; set; }
    public decimal TransactionCredit { get; set; }
    public decimal FunctionalDebit { get; set; }
    public decimal FunctionalCredit { get; set; }
    public string? SourceReference { get; set; }

    public JournalEntry JournalEntry { get; set; } = null!;
    public LedgerAccount Account { get; set; } = null!;
}
