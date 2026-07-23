namespace ERP_RFQ_Automation.CommercialFinance;

public sealed record CreateFinanceCommunicationContactRequest(
    long CustomerId, string Purpose, string Channel, string DestinationToken,
    string MaskedDestination, DateTime EffectiveFrom, DateTime? EffectiveTo,
    string VerificationEvidenceReference, Guid VerificationProviderEventId,
    string ProviderSignature);
public sealed record DeactivateFinanceCommunicationContactRequest(long ExpectedVersion, string Reason);
public sealed record FinanceCommunicationContactDto(
    long Id, long CustomerId, string Purpose, string Channel, string MaskedDestination,
    bool IsVerified, bool IsActive, DateTime EffectiveFrom, DateTime? EffectiveTo,
    long Version, DateTime CreatedOn, DateTime? DeactivatedOn);

public sealed record CreateCustomerStatementRequest(
    long CustomerId, long? CurrencyId, DateTime PeriodStart, DateTime CutoffAt,
    long? SupersedesStatementId, string TemplateVersion, string? CorrectionReason = null);
public sealed record StatementActionRequest(long ExpectedVersion, string? Reason = null);
public sealed record CustomerStatementArtifactDto(
    long StatementId, string MediaType, string Content, string ArtifactHash, string? ArtifactReference);
public sealed record CustomerStatementLineDto(
    int Sequence, string SourceType, string SourceNumber, long? CommercialCaseId,
    DateTime ActivityDate, DateTime? DueDate, string Description, decimal DebitAmount,
    decimal CreditAmount, decimal AppliedAmount, decimal OutstandingAmount,
    string AgingBucket, decimal RunningBalance);
public sealed record CustomerStatementDto(
    long Id, long CustomerId, long? CurrencyId, string? CurrencyCode, long? SupersedesStatementId,
    string? StatementNumber, string Status, DateTime PeriodStart, DateTime CutoffAt,
    DateTime CapturedOn, int Revision, decimal OpeningBalance, decimal DebitTotal,
    decimal CreditTotal, decimal UnappliedCash, decimal ClosingBalance,
    decimal NetCustomerPosition, decimal AgingCurrent, decimal Aging1To30,
    decimal Aging31To60, decimal Aging61To90, decimal AgingOver90,
    string SnapshotHash, string ArtifactHash, string? ArtifactReference,
    string GeneratorVersion, string TemplateVersion, string IssuerName,
    string CustomerName, string BillingAddress, long Version, string CreatedBy,
    DateTime CreatedOn, string? FinalizedBy, DateTime? FinalizedOn,
    string? CancelledBy, DateTime? CancelledOn, string? CancellationReason,
    IReadOnlyList<CustomerStatementLineDto> Lines);

public sealed record DunningPolicyStepRequest(
    int Stage, int MinimumDaysPastDue, decimal MinimumAmount,
    int WaitDaysAfterPriorStage, string Channel, string TemplateVersion,
    bool RequiresApproval, string EscalationRole, int MaximumAttempts);
public sealed record CreateDunningPolicyRequest(
    string Name, string JurisdictionCode, string TimeZoneId, int GraceDays,
    int CadenceDays, decimal MinimumOverdueAmount, int QuietHoursStart,
    int QuietHoursEnd, string TemplateVersion, IReadOnlyList<DunningPolicyStepRequest> Steps);
public sealed record DunningPolicyActionRequest(long ExpectedVersion);
public sealed record DunningPolicyStepDto(
    long Id, int Stage, int MinimumDaysPastDue, decimal MinimumAmount,
    int WaitDaysAfterPriorStage, string Channel, string TemplateVersion,
    bool RequiresApproval, string EscalationRole, int MaximumAttempts);
public sealed record DunningPolicyDto(
    long Id, int PolicyVersion, string Name, string Status, string JurisdictionCode,
    string TimeZoneId, int GraceDays, int CadenceDays, int MaximumStage,
    decimal MinimumOverdueAmount, int QuietHoursStart, int QuietHoursEnd,
    string TemplateVersion, long Version, string CreatedBy, DateTime CreatedOn,
    string? ApprovedBy, DateTime? ApprovedOn, string? RetiredBy, DateTime? RetiredOn,
    IReadOnlyList<DunningPolicyStepDto> Steps,
    string? ActivatedBy = null, DateTime? ActivatedOn = null);

public sealed record UpsertCustomerCollectionProfileRequest(
    long CustomerId, long? CurrencyId, long DunningPolicyId,
    long? FinanceCommunicationContactId, string Locale, string TimeZoneId,
    string? Collector, bool AutomaticDeliveryAllowed, long? ExpectedVersion);
public sealed record CustomerCollectionProfileDto(
    long Id, long CustomerId, long? CurrencyId, long DunningPolicyId,
    long? FinanceCommunicationContactId, string Locale, string TimeZoneId,
    string? Collector, bool AutomaticDeliveryAllowed, bool IsOnHold,
    string? HoldReason, long Version);

public sealed record CreateCollectionControlRequest(
    long CustomerId, long? CurrencyId, long? ReceivableDocumentId,
    string ControlType, decimal? DisputedAmount, string ReasonCode, string Reason,
    string EvidenceReference, DateTime? EffectiveFrom, DateTime? ReviewOn, DateTime? ExpiresOn);
public sealed record ResolveCollectionControlRequest(
    long ExpectedVersion, string Reason, string EvidenceReference);
public sealed record CollectionControlDto(
    long Id, long CustomerId, long? CurrencyId, long? ReceivableDocumentId,
    string ControlType, string Status, decimal? DisputedAmount, string ReasonCode,
    string Reason, string EvidenceReference, DateTime EffectiveFrom, DateTime? ReviewOn,
    DateTime? ExpiresOn, long Version, string CreatedBy, DateTime CreatedOn,
    string? ResolvedBy, DateTime? ResolvedOn, string? ResolutionReason);

public sealed record OpenDunningCaseRequest(
    long CustomerStatementId, long DunningPolicyId, string? AssignedTo);
public sealed record DunningCaseActionRequest(
    long ExpectedVersion, string Reason, string EvidenceReference);
public sealed record AssignDunningCaseRequest(long ExpectedVersion, string AssignedTo);
public sealed record CreatePromiseToPayRequest(
    long ExpectedCaseVersion, decimal Amount, DateTime DueOn, string EvidenceReference);
public sealed record ClosePromiseToPayRequest(
    long ExpectedVersion, string Status, string EvidenceReference, long? MatchedPaymentId = null);
public sealed record PromiseToPayDto(
    long Id, decimal Amount, DateTime PromisedOn, DateTime DueOn, string Status,
    string EvidenceReference, long Version, string CreatedBy, DateTime CreatedOn,
    string? ClosedBy, DateTime? ClosedOn, long? MatchedPaymentId = null,
    decimal? MatchedAmount = null);
public sealed record DunningCaseDto(
    long Id, long CustomerId, long? CurrencyId, string? CurrencyCode,
    long DunningPolicyId, long CustomerStatementId, string Status, int CurrentStage,
    decimal ExposureAtOpen, decimal CurrentExposure, DateTime OldestDueDate,
    DateTime NextActionOn, string? AssignedTo, decimal? PromiseAmount,
    DateTime? PromiseDueOn, long Version, string CreatedBy, DateTime CreatedOn,
    string? UpdatedBy, DateTime? UpdatedOn, string? StatusReason,
    IReadOnlyList<PromiseToPayDto> Promises);

public sealed record CreateDunningNoticeRequest(
    long DunningCaseId, long FinanceCommunicationContactId);
public sealed record DunningNoticeActionRequest(
    long ExpectedVersion, string? Reason = null, string? EvidenceReference = null);
public sealed record DunningDeliveryResultRequest(
    long ExpectedVersion, Guid ProviderEventId, string ProviderReference,
    DateTime ProviderOccurredOn, string SignedEvidenceReference, string? FailureCode = null,
    string ProviderSignature = "");
public sealed record DunningDeliveryAttemptDto(
    long Id, Guid ProviderEventId, int AttemptNumber, string Status,
    string MaskedDestination, string ArtifactHash, string TemplateVersion,
    string? ProviderReference, string? FailureCode, DateTime OccurredOn,
    DateTime? ProviderOccurredOn = null, string? SignedEvidenceReference = null);
public sealed record DunningNoticeDto(
    long Id, long DunningCaseId, long CustomerStatementId,
    long FinanceCommunicationContactId, int Stage, string Status,
    decimal SnapshotExposure, string SnapshotHash, string TemplateVersion,
    long Version, string CreatedBy, DateTime CreatedOn, string? ApprovedBy,
    DateTime? ApprovedOn, string? ReleasedBy, DateTime? ReleasedOn,
    DateTime? DeliveryUpdatedOn, string? ProviderReference, string? FailureCode,
    string? SuppressionReason, IReadOnlyList<DunningDeliveryAttemptDto> DeliveryAttempts,
    string? Locale = null, string? Subject = null, string? ArtifactMediaType = null,
    string? ArtifactHash = null, string? ArtifactContent = null,
    string? CancellationEvidenceReference = null);

public sealed record CreateDunningRunRequest(long DunningPolicyId, DateTime CutoffAt);
public sealed record DunningRunDecisionDto(
    long Id, long DunningRunId, long CustomerId, long? CurrencyId,
    long? CustomerStatementId, long? DunningCaseId, long? DunningNoticeId,
    string Outcome, string ReasonCode, string EvidenceHash, DateTime CreatedOn,
    long? CustomerCollectionProfileId = null);
public sealed record DunningRunDto(
    long Id, long DunningPolicyId, DateTime CutoffAt, string Status,
    int CandidateCount, int NoticeCount, int SuppressedCount, int FailedCount,
    long Version, string CreatedBy, DateTime CreatedOn, DateTime? CompletedOn,
    string? CompletionEvidenceReference = null, string? FailureReason = null,
    string? FailureEvidenceReference = null,
    IReadOnlyList<DunningRunDecisionDto> Decisions = null!);
