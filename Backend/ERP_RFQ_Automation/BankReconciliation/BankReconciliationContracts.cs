namespace ERP_RFQ_Automation.BankReconciliation;

public sealed record CreateBankAccountRequest(string Name, string InstitutionName, string MaskedAccountNumber,
    string AccountIdentifier, long CurrencyId, long LedgerAccountId, DateTime OpeningDate);
public sealed record BankAccountActionRequest(long ExpectedVersion, string Reason);
public sealed record CreateBankMatchingRuleRequest(string Code, long? BankAccountId, string Name,
    string EvaluatorType, int Priority, decimal AmountTolerance, int BookingDateToleranceDays,
    string ReferenceMode, bool RequireUniquePair);
public sealed record BankMatchingRuleActionRequest(long ExpectedVersion, string Reason, string EvidenceReference);
public sealed record CreateBankAdjustmentRequest(long BankAccountId, long BankStatementLineId,
    long AccountingPeriodId, DateTime AccountingDate, string AdjustmentType, string Description,
    decimal Amount, string EvidenceReference, IReadOnlyList<BankAdjustmentDistributionRequest> Distributions);
public sealed record BankAdjustmentDistributionRequest(long LedgerAccountId, decimal Amount, string Description);
public sealed record BankAdjustmentActionRequest(long ExpectedVersion, string? Reason = null,
    string? EvidenceReference = null);
public sealed record ImportBankStatementRequest(long BankAccountId, string SourceType, string OriginalFileName,
    string RawObjectReference, string SourceHash, string ParserVersion, string StatementReference,
    DateTime PeriodStart, DateTime PeriodEnd, decimal OpeningBalance, decimal ClosingBalance,
    IReadOnlyList<ImportBankStatementLineRequest> Lines);
public sealed record ImportBankStatementLineRequest(int SourceOrdinal, DateTime BookingDate, DateTime ValueDate,
    decimal SignedAmount, string OriginalAmountText, string? ExternalTransactionId, string? BankReference,
    string? TransactionCode, string? Counterparty, string? RemittanceText);
public sealed record CreateReconciliationRunRequest(long BankStatementId, DateTime ReconciliationThrough);
public sealed record CreateReconciliationMatchRequest(long ReconciliationRunId, string Reason,
    string EvidenceReference, IReadOnlyList<ReconciliationAllocationRequest> Allocations);
public sealed record ReconciliationAllocationRequest(long BankStatementLineId, long JournalEntryLineId,
    decimal BankAmount, decimal FunctionalAmount);
public sealed record ReconciliationActionRequest(long ExpectedVersion, string? Reason = null, string? EvidenceReference = null);
public sealed record MatchActionRequest(long ExpectedVersion, string? Reason = null);

public sealed record BankAccountDto(long Id, string Name, string InstitutionName, string MaskedAccountNumber,
    long CurrencyId, long LedgerAccountId, string Status, DateTime OpeningDate, long Version);
public sealed record BankMatchingRuleDto(long Id, string Code, int RuleVersion, long? BankAccountId,
    string Name, string EvaluatorType, int Priority, decimal AmountTolerance,
    int BookingDateToleranceDays, string ReferenceMode, bool RequireUniquePair, string DefinitionHash,
    string Status, long RecordVersion, string CreatedBy, DateTime CreatedOn, string? ApprovedBy,
    string? ActivatedBy, string? RetiredBy);
public sealed record BankAdjustmentDistributionDto(long Id, int Sequence, long LedgerAccountId,
    decimal Amount, string Description);
public sealed record BankAdjustmentDto(long Id, long BankAccountId, long BankStatementLineId,
    long AccountingPeriodId, DateTime AccountingDate, string AdjustmentType, string Description,
    decimal Amount, string EvidenceReference, string Status, long? JournalEntryId,
    long? BankJournalEntryLineId, long? ReversalJournalEntryId, long Version, string PreparedBy,
    string? SubmittedBy, string? ApprovedBy, string? ReversedBy,
    IReadOnlyList<BankAdjustmentDistributionDto> Distributions);
public sealed record BankStatementLineDto(long Id, int SourceOrdinal, DateTime BookingDate, DateTime ValueDate,
    decimal SignedAmount, string Direction, string? ExternalTransactionId, string? BankReference,
    string? TransactionCode, string? Counterparty, string? RemittanceText, string? NormalizedReference,
    string LineFingerprint);
public sealed record BankStatementDto(long Id, long ImportId, long BankAccountId, long CurrencyId,
    string StatementReference, DateTime PeriodStart, DateTime PeriodEnd, decimal OpeningBalance,
    decimal ClosingBalance, decimal CalculatedClosingBalance, string ContentHash, IReadOnlyList<BankStatementLineDto> Lines);
public sealed record BankStatementSourceDto(string FileName, string SourceType, string SourceHash, byte[] Payload);
public sealed record ReconciliationAllocationDto(long Id, long BankStatementLineId, long JournalEntryLineId,
    decimal BankAmount, decimal FunctionalAmount);
public sealed record ReconciliationMatchDto(long Id, string MatchType, decimal Confidence, string RuleCode,
    int RuleVersion, long? BankMatchingRuleId, string Status, long Version, string CreatedBy, string? ConfirmedBy,
    IReadOnlyList<ReconciliationAllocationDto> Allocations);
public sealed record ReconciliationRunDto(long Id, long BankAccountId, long BankStatementId,
    DateTime ReconciliationThrough, string Status, decimal BankClosingBalance, decimal BookClosingBalance,
    decimal MatchedAmount, decimal UnexplainedDifference, long Version, string PreparedBy,
    string? SubmittedBy, string? ApprovedBy, string? CertificateHash, int? CertificateLineCount,
    int? CertificateJournalCount, IReadOnlyList<ReconciliationMatchDto> Matches);

public sealed class BankReconciliationConflictException(string message) : InvalidOperationException(message);
