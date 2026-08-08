namespace ERP_RFQ_Automation.Platform.DataAssets;

public sealed record RecordTenantDataRecoveryEvidenceRequest(
    long? TenantDataAssetId,
    string ScopeKey,
    string EvidenceType,
    string OpaqueProviderReference,
    string? OpaqueBackupSetReference,
    DateTime? RecoveryPointUtc,
    DateTime? OperationStartedUtc,
    DateTime CompletedUtc,
    int? ConfiguredRpoSeconds,
    int? ConfiguredRtoSeconds,
    DateTime? RetainUntilUtc,
    long? CustomerRowsObserved,
    string EvidenceReference,
    string EvidenceSha256,
    string CorrelationId,
    string IdempotencyKey,
    string Reason);

public sealed record TenantDataRecoveryEvidenceDto(
    long Id, long TenantId, long? TenantDataAssetId, string ScopeKey, string EvidenceType,
    string OpaqueProviderReference, string? OpaqueBackupSetReference, DateTime? RecoveryPointUtc,
    DateTime? OperationStartedUtc, DateTime CompletedUtc, int? ConfiguredRpoSeconds,
    int? ConfiguredRtoSeconds, int? ActualRecoverySeconds, DateTime? RetainUntilUtc,
    long? CustomerRowsObserved, string EvidenceReference, string EvidenceSha256,
    string CorrelationId, string ActorEmail, string Reason, DateTime RecordedUtc);

public sealed record TenantDeletionCertificationDecisionDto(
    long TenantId,
    bool Ready,
    IReadOnlyList<string> Blockers,
    IReadOnlyList<long> EvidenceIds,
    DateTime EvaluatedUtc,
    string Boundary);

public sealed record CreateTenantDeletionCertificateRequest(string Reason);

public sealed record TenantDeletionCertificateDto(
    long Id, long TenantId, string TenantSlug, DateTime PurgedUtc, DateTime CertifiedUtc,
    string ActorEmail, string EvidenceManifestSha256, IReadOnlyList<long> EvidenceIds, string Reason);
