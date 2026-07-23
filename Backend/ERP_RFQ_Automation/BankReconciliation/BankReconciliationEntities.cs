namespace ERP_RFQ_Automation.BankReconciliation;

public static class BankAccountStatuses { public const string Active = "Active"; public const string Suspended = "Suspended"; public const string Closed = "Closed"; }
public static class BankImportStatuses { public const string Validated = "Validated"; public const string Rejected = "Rejected"; }
public static class ReconciliationStatuses { public const string Draft = "Draft"; public const string InReview = "InReview"; public const string Approved = "Approved"; public const string Reopened = "Reopened"; }
public static class BankMatchStatuses { public const string Proposed = "Proposed"; public const string Confirmed = "Confirmed"; public const string Voided = "Voided"; }
public static class BankMatchingRuleTypes { public const string ExactAmountDirection = "ExactAmountDirection"; }
public static class BankMatchingRuleStatuses { public const string Draft = "Draft"; public const string Approved = "Approved"; public const string Active = "Active"; public const string Retired = "Retired"; }
public static class BankMatchingReferenceModes { public const string Ignore = "Ignore"; public const string NormalizedExact = "NormalizedExact"; }
public static class BankAdjustmentStatuses { public const string Draft = "Draft"; public const string InReview = "InReview"; public const string Posted = "Posted"; public const string Rejected = "Rejected"; public const string Cancelled = "Cancelled"; public const string Reversed = "Reversed"; }

public sealed class BankAdjustment
{
    public long Id { get; set; }
    public long BusinessUnitId { get; set; }
    public long BankAccountId { get; set; }
    public long BankStatementLineId { get; set; }
    public long AccountingPeriodId { get; set; }
    public DateTime AccountingDate { get; set; }
    public string AdjustmentType { get; set; } = null!;
    public string Description { get; set; } = null!;
    public decimal Amount { get; set; }
    public string EvidenceReference { get; set; } = null!;
    public string Status { get; set; } = BankAdjustmentStatuses.Draft;
    public long? JournalEntryId { get; set; }
    public long? BankJournalEntryLineId { get; set; }
    public long? ReversalJournalEntryId { get; set; }
    public long? ReversalBankJournalEntryLineId { get; set; }
    public string IdempotencyKey { get; set; } = null!;
    public string RequestHash { get; set; } = null!;
    public long Version { get; set; } = 1;
    public string PreparedBy { get; set; } = null!;
    public DateTime PreparedOn { get; set; }
    public string? SubmittedBy { get; set; }
    public DateTime? SubmittedOn { get; set; }
    public string? ApprovedBy { get; set; }
    public DateTime? ApprovedOn { get; set; }
    public string? RejectedBy { get; set; }
    public DateTime? RejectedOn { get; set; }
    public string? RejectionReason { get; set; }
    public string? CancelledBy { get; set; }
    public DateTime? CancelledOn { get; set; }
    public string? CancellationReason { get; set; }
    public string? ReversedBy { get; set; }
    public DateTime? ReversedOn { get; set; }
    public string? ReversalReason { get; set; }
    public string? ReversalEvidenceReference { get; set; }
    public BankAccount BankAccount { get; set; } = null!;
    public BankStatementLine BankStatementLine { get; set; } = null!;
    public ICollection<BankAdjustmentDistribution> Distributions { get; set; } = new List<BankAdjustmentDistribution>();
}

public sealed class BankAdjustmentDistribution
{
    public long Id { get; set; }
    public long BusinessUnitId { get; set; }
    public long BankAdjustmentId { get; set; }
    public int Sequence { get; set; }
    public long LedgerAccountId { get; set; }
    public decimal Amount { get; set; }
    public string Description { get; set; } = null!;
    public BankAdjustment Adjustment { get; set; } = null!;
}

public sealed class BankMatchingRule
{
    public long Id { get; set; }
    public long BusinessUnitId { get; set; }
    public long? BankAccountId { get; set; }
    public string Code { get; set; } = null!;
    public int RuleVersion { get; set; }
    public string Name { get; set; } = null!;
    public string EvaluatorType { get; set; } = BankMatchingRuleTypes.ExactAmountDirection;
    public int Priority { get; set; }
    public decimal AmountTolerance { get; set; }
    public int BookingDateToleranceDays { get; set; }
    public string ReferenceMode { get; set; } = BankMatchingReferenceModes.Ignore;
    public bool RequireUniquePair { get; set; } = true;
    public string DefinitionHash { get; set; } = null!;
    public long? SupersedesRuleId { get; set; }
    public string Status { get; set; } = BankMatchingRuleStatuses.Draft;
    public string IdempotencyKey { get; set; } = null!;
    public string RequestHash { get; set; } = null!;
    public long RecordVersion { get; set; } = 1;
    public string CreatedBy { get; set; } = null!;
    public DateTime CreatedOn { get; set; }
    public string? ApprovedBy { get; set; }
    public DateTime? ApprovedOn { get; set; }
    public string? ActivatedBy { get; set; }
    public DateTime? ActivatedOn { get; set; }
    public string? RetiredBy { get; set; }
    public DateTime? RetiredOn { get; set; }
    public string? LifecycleReason { get; set; }
    public string? EvidenceReference { get; set; }
    public BankAccount? BankAccount { get; set; }
    public BankMatchingRule? SupersedesRule { get; set; }
}

public sealed class ReconciliationRunRule
{
    public long Id { get; set; }
    public long BusinessUnitId { get; set; }
    public long ReconciliationRunId { get; set; }
    public long BankMatchingRuleId { get; set; }
    public int EvaluationOrder { get; set; }
    public string DefinitionHash { get; set; } = null!;
    public ReconciliationRun Run { get; set; } = null!;
    public BankMatchingRule Rule { get; set; } = null!;
}

public sealed class BankAccount
{
    public long Id { get; set; }
    public long BusinessUnitId { get; set; }
    public string Name { get; set; } = null!;
    public string InstitutionName { get; set; } = null!;
    public string MaskedAccountNumber { get; set; } = null!;
    public string AccountFingerprint { get; set; } = null!;
    public long CurrencyId { get; set; }
    public long LedgerAccountId { get; set; }
    public string Status { get; set; } = BankAccountStatuses.Active;
    public DateTime OpeningDate { get; set; }
    public string IdempotencyKey { get; set; } = null!;
    public string RequestHash { get; set; } = null!;
    public long Version { get; set; } = 1;
    public string CreatedBy { get; set; } = null!;
    public DateTime CreatedOn { get; set; }
    public string? StatusChangedBy { get; set; }
    public DateTime? StatusChangedOn { get; set; }
    public string? StatusReason { get; set; }
}

public sealed class BankStatementImport
{
    public long Id { get; set; }
    public long BusinessUnitId { get; set; }
    public long BankAccountId { get; set; }
    public string SourceType { get; set; } = null!;
    public string OriginalFileName { get; set; } = null!;
    public string RawObjectReference { get; set; } = null!;
    public byte[]? RawPayload { get; set; }
    public string SourceHash { get; set; } = null!;
    public string ParserVersion { get; set; } = null!;
    public string Status { get; set; } = BankImportStatuses.Validated;
    public string IdempotencyKey { get; set; } = null!;
    public string RequestHash { get; set; } = null!;
    public string ImportedBy { get; set; } = null!;
    public DateTime ImportedOn { get; set; }
    public BankStatement Statement { get; set; } = null!;
}

public sealed class BankStatement
{
    public long Id { get; set; }
    public long BusinessUnitId { get; set; }
    public long BankStatementImportId { get; set; }
    public long BankAccountId { get; set; }
    public long CurrencyId { get; set; }
    public string StatementReference { get; set; } = null!;
    public DateTime PeriodStart { get; set; }
    public DateTime PeriodEnd { get; set; }
    public decimal OpeningBalance { get; set; }
    public decimal ClosingBalance { get; set; }
    public decimal CalculatedClosingBalance { get; set; }
    public string ContentHash { get; set; } = null!;
    public long Version { get; set; } = 1;
    public ICollection<BankStatementLine> Lines { get; set; } = new List<BankStatementLine>();
}

public sealed class BankStatementLine
{
    public long Id { get; set; }
    public long BusinessUnitId { get; set; }
    public long BankStatementId { get; set; }
    public long BankAccountId { get; set; }
    public int SourceOrdinal { get; set; }
    public DateTime BookingDate { get; set; }
    public DateTime ValueDate { get; set; }
    public decimal SignedAmount { get; set; }
    public string Direction { get; set; } = null!;
    public string OriginalAmountText { get; set; } = null!;
    public string? ExternalTransactionId { get; set; }
    public string? BankReference { get; set; }
    public string? TransactionCode { get; set; }
    public string? Counterparty { get; set; }
    public string? RemittanceText { get; set; }
    public string? NormalizedReference { get; set; }
    public string LineFingerprint { get; set; } = null!;
    public BankStatement Statement { get; set; } = null!;
}

public sealed class ReconciliationRun
{
    public long Id { get; set; }
    public long BusinessUnitId { get; set; }
    public long BankAccountId { get; set; }
    public long BankStatementId { get; set; }
    public DateTime ReconciliationThrough { get; set; }
    public string Status { get; set; } = ReconciliationStatuses.Draft;
    public decimal BankClosingBalance { get; set; }
    public decimal BookClosingBalance { get; set; }
    public decimal MatchedAmount { get; set; }
    public decimal UnexplainedDifference { get; set; }
    public string IdempotencyKey { get; set; } = null!;
    public string RequestHash { get; set; } = null!;
    public long Version { get; set; } = 1;
    public string PreparedBy { get; set; } = null!;
    public DateTime PreparedOn { get; set; }
    public string? SubmittedBy { get; set; }
    public DateTime? SubmittedOn { get; set; }
    public string? ApprovedBy { get; set; }
    public DateTime? ApprovedOn { get; set; }
    public string? ApprovalReason { get; set; }
    public string? EvidenceReference { get; set; }
    public string? CertificateHash { get; set; }
    public int? CertificateLineCount { get; set; }
    public int? CertificateJournalCount { get; set; }
    public string RuleSetHash { get; set; } = null!;
    public DateTime RuleSetSnapshotOn { get; set; }
    public string? ReopenedBy { get; set; }
    public DateTime? ReopenedOn { get; set; }
    public string? ReopenReason { get; set; }
    public string? ReopenEvidenceReference { get; set; }
    public ICollection<ReconciliationMatch> Matches { get; set; } = new List<ReconciliationMatch>();
    public ICollection<ReconciliationRunRule> Rules { get; set; } = new List<ReconciliationRunRule>();
}

public sealed class ReconciliationMatch
{
    public long Id { get; set; }
    public long BusinessUnitId { get; set; }
    public long ReconciliationRunId { get; set; }
    public string MatchType { get; set; } = "Manual";
    public decimal Confidence { get; set; }
    public string RuleCode { get; set; } = null!;
    public int RuleVersion { get; set; }
    public long? BankMatchingRuleId { get; set; }
    public string? RuleDefinitionHash { get; set; }
    public string? MatchReason { get; set; }
    public string? EvidenceReference { get; set; }
    public string Status { get; set; } = BankMatchStatuses.Proposed;
    public string IdempotencyKey { get; set; } = null!;
    public string RequestHash { get; set; } = null!;
    public long Version { get; set; } = 1;
    public string CreatedBy { get; set; } = null!;
    public DateTime CreatedOn { get; set; }
    public string? ConfirmedBy { get; set; }
    public DateTime? ConfirmedOn { get; set; }
    public string? VoidedBy { get; set; }
    public DateTime? VoidedOn { get; set; }
    public string? VoidReason { get; set; }
    public ReconciliationRun Run { get; set; } = null!;
    public BankMatchingRule? MatchingRule { get; set; }
    public ICollection<ReconciliationAllocation> Allocations { get; set; } = new List<ReconciliationAllocation>();
}

public sealed class ReconciliationAllocation
{
    public long Id { get; set; }
    public long BusinessUnitId { get; set; }
    public long ReconciliationMatchId { get; set; }
    public long BankStatementLineId { get; set; }
    public long JournalEntryLineId { get; set; }
    public decimal BankAmount { get; set; }
    public decimal FunctionalAmount { get; set; }
    public ReconciliationMatch Match { get; set; } = null!;
}
