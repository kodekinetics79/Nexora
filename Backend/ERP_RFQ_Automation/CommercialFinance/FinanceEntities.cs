namespace ERP_RFQ_Automation.CommercialFinance;

public static class ReceivableDocumentTypes
{
    public const string Invoice = "Invoice";
    public const string CreditNote = "CreditNote";
    public const string DebitNote = "DebitNote";
}

public static class ReceivableDocumentStatuses
{
    public const string Draft = "Draft";
    public const string Cancelled = "Cancelled";
    public const string Issued = "Issued";
    public const string Void = "Void";
}

public static class CustomerPaymentStatuses
{
    public const string Posted = "Posted";
    public const string Reversed = "Reversed";
}

public static class FinanceExceptionStatuses
{
    public const string Draft = "Draft";
    public const string Approved = "Approved";
    public const string Posted = "Posted";
    public const string Released = "Released";
    public const string Cancelled = "Cancelled";
    public const string Reversed = "Reversed";
}

public static class CustomerStatementStatuses
{
    public const string Draft = "Draft";
    public const string Finalized = "Finalized";
    public const string Cancelled = "Cancelled";
    public const string Superseded = "Superseded";
}

public static class DunningCaseStatuses
{
    public const string Open = "Open";
    public const string Held = "Held";
    public const string Disputed = "Disputed";
    public const string Resolved = "Resolved";
    public const string Cancelled = "Cancelled";
}

public static class DunningNoticeStatuses
{
    public const string Draft = "Draft";
    public const string Approved = "Approved";
    public const string Released = "Released";
    public const string Delivered = "Delivered";
    public const string Failed = "Failed";
    public const string Suppressed = "Suppressed";
    public const string Cancelled = "Cancelled";
}

public static class CollectionControlTypes
{
    public const string Dispute = "Dispute";
    public const string CommunicationRestriction = "CommunicationRestriction";
    public const string LegalHold = "LegalHold";
}

public sealed class ReceivableDocument
{
    public long Id { get; set; }
    public long BusinessUnitId { get; set; }
    public long? CommercialCaseId { get; set; }
    public long CustomerId { get; set; }
    public long? OrderId { get; set; }
    public long? ParentDocumentId { get; set; }
    public string? AdjustmentReasonCode { get; set; }
    public string? AdjustmentReason { get; set; }
    public long? CurrencyId { get; set; }
    public string DocumentType { get; set; } = ReceivableDocumentTypes.Invoice;
    public string Status { get; set; } = ReceivableDocumentStatuses.Draft;
    public string? DocumentNumber { get; set; }
    public DateTime DocumentDate { get; set; }
    public DateTime DueDate { get; set; }
    public DateTime? IssuedOn { get; set; }
    public DateTime? VoidedOn { get; set; }
    public string? VoidReason { get; set; }
    public string? VoidedBy { get; set; }
    public decimal SubTotal { get; set; }
    public decimal DiscountAmount { get; set; }
    public decimal TaxAmount { get; set; }
    public decimal TotalAmount { get; set; }
    public string IdempotencyKey { get; set; } = null!;
    public string RequestHash { get; set; } = null!;
    public long Version { get; set; } = 1;
    public string CreatedBy { get; set; } = null!;
    public DateTime CreatedOn { get; set; }
    public string? IssuedBy { get; set; }

    public ICollection<ReceivableDocumentLine> Lines { get; set; } = new List<ReceivableDocumentLine>();
    public ICollection<PaymentAllocation> Allocations { get; set; } = new List<PaymentAllocation>();
}

public sealed class ReceivableDocumentLine
{
    public long Id { get; set; }
    public long BusinessUnitId { get; set; }
    public long ReceivableDocumentId { get; set; }
    public long? OrderItemId { get; set; }
    public long? ParentDocumentLineId { get; set; }
    public string Description { get; set; } = null!;
    public decimal Quantity { get; set; }
    public decimal UnitPrice { get; set; }
    public decimal DiscountAmount { get; set; }
    public decimal TaxAmount { get; set; }
    public decimal LineTotal { get; set; }

    public ReceivableDocument Document { get; set; } = null!;
}

public sealed class CustomerPayment
{
    public long Id { get; set; }
    public long BusinessUnitId { get; set; }
    public long CustomerId { get; set; }
    public long? CommercialCaseId { get; set; }
    public long? CurrencyId { get; set; }
    public string ReceiptNumber { get; set; } = null!;
    public string Status { get; set; } = CustomerPaymentStatuses.Posted;
    public DateTime PaymentDate { get; set; }
    public decimal Amount { get; set; }
    public string? Method { get; set; }
    public string? BankReference { get; set; }
    public string IdempotencyKey { get; set; } = null!;
    public string RequestHash { get; set; } = null!;
    public long Version { get; set; } = 1;
    public DateTime? ReversedOn { get; set; }
    public string? ReversalReason { get; set; }
    public string CreatedBy { get; set; } = null!;
    public DateTime CreatedOn { get; set; }

    public ICollection<PaymentAllocation> Allocations { get; set; } = new List<PaymentAllocation>();
}

public sealed class PaymentAllocation
{
    public long Id { get; set; }
    public long BusinessUnitId { get; set; }
    public long CustomerPaymentId { get; set; }
    public long ReceivableDocumentId { get; set; }
    public decimal Amount { get; set; }
    public DateTime CreatedOn { get; set; }

    public CustomerPayment Payment { get; set; } = null!;
    public ReceivableDocument Document { get; set; } = null!;
}

public sealed class ReceivableWriteOff
{
    public long Id { get; set; }
    public long BusinessUnitId { get; set; }
    public long CustomerId { get; set; }
    public long? CommercialCaseId { get; set; }
    public long? CurrencyId { get; set; }
    public string? WriteOffNumber { get; set; }
    public string Status { get; set; } = FinanceExceptionStatuses.Draft;
    public DateTime AccountingDate { get; set; }
    public decimal TotalAmount { get; set; }
    public string ReasonCode { get; set; } = null!;
    public string Reason { get; set; } = null!;
    public string? EvidenceReference { get; set; }
    public string PostingStatus { get; set; } = "NotPosted";
    public string? JournalReference { get; set; }
    public string IdempotencyKey { get; set; } = null!;
    public string RequestHash { get; set; } = null!;
    public long Version { get; set; } = 1;
    public string CreatedBy { get; set; } = null!;
    public DateTime CreatedOn { get; set; }
    public string? ApprovedBy { get; set; }
    public DateTime? ApprovedOn { get; set; }
    public string? CancelledBy { get; set; }
    public DateTime? CancelledOn { get; set; }
    public string? CancellationReason { get; set; }
    public string? ReversedBy { get; set; }
    public DateTime? ReversedOn { get; set; }
    public string? ReversalReason { get; set; }
    public string? ReversalEvidenceReference { get; set; }

    public ICollection<WriteOffAllocation> Allocations { get; set; } = new List<WriteOffAllocation>();
}

public sealed class WriteOffAllocation
{
    public long Id { get; set; }
    public long BusinessUnitId { get; set; }
    public long ReceivableWriteOffId { get; set; }
    public long ReceivableDocumentId { get; set; }
    public decimal Amount { get; set; }
    public decimal BalanceBefore { get; set; }
    public decimal BalanceAfter { get; set; }

    public ReceivableWriteOff WriteOff { get; set; } = null!;
    public ReceivableDocument Document { get; set; } = null!;
}

public sealed class CustomerRefund
{
    public long Id { get; set; }
    public long BusinessUnitId { get; set; }
    public long SourcePaymentId { get; set; }
    public long CustomerId { get; set; }
    public long? CommercialCaseId { get; set; }
    public long? CurrencyId { get; set; }
    public string? RefundNumber { get; set; }
    public string Status { get; set; } = FinanceExceptionStatuses.Draft;
    public DateTime RequestedExecutionDate { get; set; }
    public decimal Amount { get; set; }
    public string Method { get; set; } = null!;
    public string DestinationReference { get; set; } = null!;
    public bool DestinationVerified { get; set; }
    public string ReasonCode { get; set; } = null!;
    public string Reason { get; set; } = null!;
    public string? EvidenceReference { get; set; }
    public string PostingStatus { get; set; } = "NotReleased";
    public string? JournalReference { get; set; }
    public string IdempotencyKey { get; set; } = null!;
    public string RequestHash { get; set; } = null!;
    public long Version { get; set; } = 1;
    public string CreatedBy { get; set; } = null!;
    public DateTime CreatedOn { get; set; }
    public string? ApprovedBy { get; set; }
    public DateTime? ApprovedOn { get; set; }
    public string? ReleasedBy { get; set; }
    public DateTime? ReleasedOn { get; set; }
    public string? DisbursementUpdatedBy { get; set; }
    public DateTime? DisbursementUpdatedOn { get; set; }
    public string? DisbursementFailureReason { get; set; }
    public string? CancelledBy { get; set; }
    public DateTime? CancelledOn { get; set; }
    public string? CancellationReason { get; set; }
    public string? ReversedBy { get; set; }
    public DateTime? ReversedOn { get; set; }
    public string? ReversalReason { get; set; }
    public string? ReversalEvidenceReference { get; set; }

    public CustomerPayment SourcePayment { get; set; } = null!;
}

public sealed class FinanceCommunicationContact
{
    public long Id { get; set; }
    public long BusinessUnitId { get; set; }
    public long CustomerId { get; set; }
    public string Purpose { get; set; } = null!;
    public string Channel { get; set; } = "Email";
    public string DestinationToken { get; set; } = null!;
    public string MaskedDestination { get; set; } = null!;
    public bool IsVerified { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTime EffectiveFrom { get; set; }
    public DateTime? EffectiveTo { get; set; }
    public string VerificationEvidenceReference { get; set; } = null!;
    public Guid VerificationProviderEventId { get; set; }
    public string? ProviderSignature { get; set; }
    public string IdempotencyKey { get; set; } = null!;
    public string RequestHash { get; set; } = null!;
    public long Version { get; set; } = 1;
    public string CreatedBy { get; set; } = null!;
    public DateTime CreatedOn { get; set; }
    public string? DeactivatedBy { get; set; }
    public DateTime? DeactivatedOn { get; set; }
    public string? DeactivationReason { get; set; }
}

public sealed class CustomerStatement
{
    public long Id { get; set; }
    public long BusinessUnitId { get; set; }
    public long CustomerId { get; set; }
    public long? CurrencyId { get; set; }
    public long? SupersedesStatementId { get; set; }
    public string? StatementNumber { get; set; }
    public string Status { get; set; } = CustomerStatementStatuses.Draft;
    public DateTime PeriodStart { get; set; }
    public DateTime CutoffAt { get; set; }
    public DateTime CapturedOn { get; set; }
    public int Revision { get; set; } = 1;
    public decimal OpeningBalance { get; set; }
    public decimal DebitTotal { get; set; }
    public decimal CreditTotal { get; set; }
    public decimal UnappliedCash { get; set; }
    public decimal ClosingBalance { get; set; }
    public decimal NetCustomerPosition { get; set; }
    public decimal AgingCurrent { get; set; }
    public decimal Aging1To30 { get; set; }
    public decimal Aging31To60 { get; set; }
    public decimal Aging61To90 { get; set; }
    public decimal AgingOver90 { get; set; }
    public string SourceFingerprint { get; set; } = null!;
    public string SnapshotHash { get; set; } = null!;
    public string ArtifactHash { get; set; } = null!;
    public string? ArtifactReference { get; set; }
    public string ArtifactMediaType { get; set; } = "text/html";
    public string ArtifactContent { get; set; } = null!;
    public string GeneratorVersion { get; set; } = null!;
    public string TemplateVersion { get; set; } = null!;
    public string IssuerNameSnapshot { get; set; } = null!;
    public string CustomerNameSnapshot { get; set; } = null!;
    public string BillingAddressSnapshot { get; set; } = null!;
    public string IdempotencyKey { get; set; } = null!;
    public string RequestHash { get; set; } = null!;
    public long Version { get; set; } = 1;
    public string CreatedBy { get; set; } = null!;
    public DateTime CreatedOn { get; set; }
    public string? FinalizedBy { get; set; }
    public DateTime? FinalizedOn { get; set; }
    public string? CancelledBy { get; set; }
    public DateTime? CancelledOn { get; set; }
    public string? CancellationReason { get; set; }
    public string? CorrectionReason { get; set; }

    public ICollection<CustomerStatementLine> Lines { get; set; } = new List<CustomerStatementLine>();
}

public sealed class CustomerStatementLine
{
    public long Id { get; set; }
    public long BusinessUnitId { get; set; }
    public long CustomerStatementId { get; set; }
    public int Sequence { get; set; }
    public string SourceType { get; set; } = null!;
    public long SourceId { get; set; }
    public long SourceVersion { get; set; }
    public string SourceNumber { get; set; } = null!;
    public long? CommercialCaseId { get; set; }
    public DateTime ActivityDate { get; set; }
    public DateTime? DueDate { get; set; }
    public string Description { get; set; } = null!;
    public decimal DebitAmount { get; set; }
    public decimal CreditAmount { get; set; }
    public decimal AppliedAmount { get; set; }
    public decimal OutstandingAmount { get; set; }
    public string AgingBucket { get; set; } = null!;
    public decimal RunningBalance { get; set; }

    public CustomerStatement Statement { get; set; } = null!;
}

public sealed class DunningPolicy
{
    public long Id { get; set; }
    public long BusinessUnitId { get; set; }
    public int PolicyVersion { get; set; }
    public string Name { get; set; } = null!;
    public string Status { get; set; } = "Draft";
    public string JurisdictionCode { get; set; } = null!;
    public string TimeZoneId { get; set; } = "UTC";
    public int GraceDays { get; set; }
    public int CadenceDays { get; set; }
    public int MaximumStage { get; set; }
    public decimal MinimumOverdueAmount { get; set; }
    public int QuietHoursStart { get; set; }
    public int QuietHoursEnd { get; set; }
    public string TemplateVersion { get; set; } = null!;
    public string IdempotencyKey { get; set; } = null!;
    public string RequestHash { get; set; } = null!;
    public long Version { get; set; } = 1;
    public string CreatedBy { get; set; } = null!;
    public DateTime CreatedOn { get; set; }
    public string? ApprovedBy { get; set; }
    public DateTime? ApprovedOn { get; set; }
    public string? ActivatedBy { get; set; }
    public DateTime? ActivatedOn { get; set; }
    public string? RetiredBy { get; set; }
    public DateTime? RetiredOn { get; set; }

    public ICollection<DunningPolicyStep> Steps { get; set; } = new List<DunningPolicyStep>();
}

public sealed class DunningPolicyStep
{
    public long Id { get; set; }
    public long BusinessUnitId { get; set; }
    public long DunningPolicyId { get; set; }
    public int Stage { get; set; }
    public int MinimumDaysPastDue { get; set; }
    public decimal MinimumAmount { get; set; }
    public int WaitDaysAfterPriorStage { get; set; }
    public string Channel { get; set; } = "Email";
    public string TemplateVersion { get; set; } = null!;
    public bool RequiresApproval { get; set; }
    public string EscalationRole { get; set; } = null!;
    public int MaximumAttempts { get; set; }

    public DunningPolicy Policy { get; set; } = null!;
}

public sealed class CustomerCollectionProfile
{
    public long Id { get; set; }
    public long BusinessUnitId { get; set; }
    public long CustomerId { get; set; }
    public long? CurrencyId { get; set; }
    public long DunningPolicyId { get; set; }
    public long? FinanceCommunicationContactId { get; set; }
    public string Locale { get; set; } = "en";
    public string TimeZoneId { get; set; } = "UTC";
    public string? Collector { get; set; }
    public bool AutomaticDeliveryAllowed { get; set; }
    public bool IsOnHold { get; set; }
    public string? HoldReason { get; set; }
    public string? HoldEvidenceReference { get; set; }
    public long Version { get; set; } = 1;
    public string CreatedBy { get; set; } = null!;
    public DateTime CreatedOn { get; set; }
    public string? ModifiedBy { get; set; }
    public DateTime? ModifiedOn { get; set; }

    public DunningPolicy Policy { get; set; } = null!;
    public FinanceCommunicationContact? Contact { get; set; }
}

public sealed class CollectionControl
{
    public long Id { get; set; }
    public long BusinessUnitId { get; set; }
    public long CustomerId { get; set; }
    public long? CurrencyId { get; set; }
    public long? ReceivableDocumentId { get; set; }
    public string ControlType { get; set; } = null!;
    public string Status { get; set; } = "Active";
    public decimal? DisputedAmount { get; set; }
    public string ReasonCode { get; set; } = null!;
    public string Reason { get; set; } = null!;
    public string EvidenceReference { get; set; } = null!;
    public DateTime EffectiveFrom { get; set; }
    public DateTime? ReviewOn { get; set; }
    public DateTime? ExpiresOn { get; set; }
    public string IdempotencyKey { get; set; } = null!;
    public string RequestHash { get; set; } = null!;
    public long Version { get; set; } = 1;
    public string CreatedBy { get; set; } = null!;
    public DateTime CreatedOn { get; set; }
    public string? ResolvedBy { get; set; }
    public DateTime? ResolvedOn { get; set; }
    public string? ResolutionReason { get; set; }
    public string? ResolutionEvidenceReference { get; set; }
}

public sealed class DunningCase
{
    public long Id { get; set; }
    public long BusinessUnitId { get; set; }
    public long CustomerId { get; set; }
    public long? CurrencyId { get; set; }
    public long DunningPolicyId { get; set; }
    public long CustomerStatementId { get; set; }
    public string Status { get; set; } = DunningCaseStatuses.Open;
    public int CurrentStage { get; set; }
    public decimal ExposureAtOpen { get; set; }
    public decimal CurrentExposure { get; set; }
    public DateTime OldestDueDate { get; set; }
    public DateTime NextActionOn { get; set; }
    public string? AssignedTo { get; set; }
    public decimal? PromiseAmount { get; set; }
    public DateTime? PromiseDueOn { get; set; }
    public string IdempotencyKey { get; set; } = null!;
    public string RequestHash { get; set; } = null!;
    public long Version { get; set; } = 1;
    public string CreatedBy { get; set; } = null!;
    public DateTime CreatedOn { get; set; }
    public string? UpdatedBy { get; set; }
    public DateTime? UpdatedOn { get; set; }
    public string? StatusReason { get; set; }
    public string? EvidenceReference { get; set; }

    public DunningPolicy Policy { get; set; } = null!;
    public CustomerStatement Statement { get; set; } = null!;
    public ICollection<DunningNotice> Notices { get; set; } = new List<DunningNotice>();
    public ICollection<PromiseToPay> Promises { get; set; } = new List<PromiseToPay>();
}

public sealed class PromiseToPay
{
    public long Id { get; set; }
    public long BusinessUnitId { get; set; }
    public long DunningCaseId { get; set; }
    public decimal Amount { get; set; }
    public DateTime PromisedOn { get; set; }
    public DateTime DueOn { get; set; }
    public string Status { get; set; } = "Open";
    public string EvidenceReference { get; set; } = null!;
    public string IdempotencyKey { get; set; } = null!;
    public string RequestHash { get; set; } = null!;
    public long Version { get; set; } = 1;
    public string CreatedBy { get; set; } = null!;
    public DateTime CreatedOn { get; set; }
    public string? ClosedBy { get; set; }
    public DateTime? ClosedOn { get; set; }
    public string? ClosureEvidenceReference { get; set; }
    public long? MatchedPaymentId { get; set; }
    public decimal? MatchedAmount { get; set; }

    public DunningCase Case { get; set; } = null!;
    public CustomerPayment? MatchedPayment { get; set; }
}

public sealed class DunningRun
{
    public long Id { get; set; }
    public long BusinessUnitId { get; set; }
    public long DunningPolicyId { get; set; }
    public DateTime CutoffAt { get; set; }
    public string Status { get; set; } = "Pending";
    public int CandidateCount { get; set; }
    public int NoticeCount { get; set; }
    public int SuppressedCount { get; set; }
    public int FailedCount { get; set; }
    public string IdempotencyKey { get; set; } = null!;
    public string RequestHash { get; set; } = null!;
    public string? LeaseOwner { get; set; }
    public Guid? LeaseToken { get; set; }
    public DateTime? LeaseUntil { get; set; }
    public long Version { get; set; } = 1;
    public string CreatedBy { get; set; } = null!;
    public DateTime CreatedOn { get; set; }
    public DateTime? CompletedOn { get; set; }
    public string? CompletionEvidenceReference { get; set; }
    public string? FailureReason { get; set; }
    public string? FailureEvidenceReference { get; set; }

    public DunningPolicy Policy { get; set; } = null!;
    public ICollection<DunningRunDecision> Decisions { get; set; } = new List<DunningRunDecision>();
}

public sealed class DunningRunDecision
{
    public long Id { get; set; }
    public long BusinessUnitId { get; set; }
    public long DunningRunId { get; set; }
    public long? CustomerCollectionProfileId { get; set; }
    public long CustomerId { get; set; }
    public long? CurrencyId { get; set; }
    public long? CustomerStatementId { get; set; }
    public long? DunningCaseId { get; set; }
    public long? DunningNoticeId { get; set; }
    public string Outcome { get; set; } = null!;
    public string ReasonCode { get; set; } = null!;
    public string EvidenceHash { get; set; } = null!;
    public DateTime CreatedOn { get; set; }

    public DunningRun Run { get; set; } = null!;
    public CustomerCollectionProfile? Profile { get; set; }
    public CustomerStatement? Statement { get; set; }
    public DunningCase? Case { get; set; }
    public DunningNotice? Notice { get; set; }
}

public sealed class DunningNotice
{
    public long Id { get; set; }
    public long BusinessUnitId { get; set; }
    public long DunningCaseId { get; set; }
    public long CustomerStatementId { get; set; }
    public long FinanceCommunicationContactId { get; set; }
    public int Stage { get; set; }
    public string Status { get; set; } = DunningNoticeStatuses.Draft;
    public decimal SnapshotExposure { get; set; }
    public string SnapshotHash { get; set; } = null!;
    public string TemplateVersion { get; set; } = null!;
    public string Locale { get; set; } = "en";
    public string Subject { get; set; } = null!;
    public string ArtifactMediaType { get; set; } = "text/html";
    public string ArtifactContent { get; set; } = null!;
    public string ArtifactHash { get; set; } = null!;
    public string IdempotencyKey { get; set; } = null!;
    public string RequestHash { get; set; } = null!;
    public long Version { get; set; } = 1;
    public string CreatedBy { get; set; } = null!;
    public DateTime CreatedOn { get; set; }
    public string? ApprovedBy { get; set; }
    public DateTime? ApprovedOn { get; set; }
    public string? ReleasedBy { get; set; }
    public DateTime? ReleasedOn { get; set; }
    public string? DeliveryUpdatedBy { get; set; }
    public DateTime? DeliveryUpdatedOn { get; set; }
    public string? ProviderReference { get; set; }
    public string? FailureCode { get; set; }
    public string? SuppressionReason { get; set; }
    public string? CancelledBy { get; set; }
    public DateTime? CancelledOn { get; set; }
    public string? CancellationReason { get; set; }
    public string? CancellationEvidenceReference { get; set; }

    public DunningCase Case { get; set; } = null!;
    public CustomerStatement Statement { get; set; } = null!;
    public FinanceCommunicationContact Contact { get; set; } = null!;
    public ICollection<DunningDeliveryAttempt> DeliveryAttempts { get; set; } = new List<DunningDeliveryAttempt>();
}

public sealed class DunningDeliveryAttempt
{
    public long Id { get; set; }
    public long BusinessUnitId { get; set; }
    public long DunningNoticeId { get; set; }
    public Guid ProviderEventId { get; set; }
    public int AttemptNumber { get; set; }
    public string Status { get; set; } = null!;
    public string MaskedDestination { get; set; } = null!;
    public string ArtifactHash { get; set; } = null!;
    public string TemplateVersion { get; set; } = null!;
    public string? ProviderReference { get; set; }
    public string? FailureCode { get; set; }
    public DateTime ProviderOccurredOn { get; set; }
    public string SignedEvidenceReference { get; set; } = null!;
    public string? ProviderSignature { get; set; }
    public DateTime OccurredOn { get; set; }
    public string RecordedBy { get; set; } = null!;

    public DunningNotice Notice { get; set; } = null!;
}

public sealed class LegalDocumentCounter
{
    public long BusinessUnitId { get; set; }
    public string DocumentType { get; set; } = null!;
    public int FiscalYear { get; set; }
    public long NextNumber { get; set; } = 1;
}

public sealed class CommercialFinanceAudit
{
    public long Id { get; set; }
    public long BusinessUnitId { get; set; }
    public string AggregateType { get; set; } = null!;
    public long AggregateId { get; set; }
    public string Action { get; set; } = null!;
    public string Actor { get; set; } = null!;
    public DateTime OccurredOn { get; set; }
    public string DetailJson { get; set; } = "{}";
}
