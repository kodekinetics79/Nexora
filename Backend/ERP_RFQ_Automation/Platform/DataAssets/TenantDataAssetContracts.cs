namespace ERP_RFQ_Automation.Platform.DataAssets;

public sealed record RegisterTenantDataAssetRequest(
    string LogicalKey,
    string OpaqueProviderReference,
    string Region,
    string Classification,
    string Disposition,
    string BackupPolicyReference,
    int BackupPolicyVersion,
    string Reason);

public sealed record VerifyTenantDataAssetRequest(
    long ExpectedVersion,
    long ObservedBusinessUnitId,
    string ObservedRegion,
    string EvidenceReference,
    string EvidenceSha256,
    string Reason);

public sealed record TenantDataAssetDto(
    long Id,
    long TenantId,
    string LogicalKey,
    string AssetType,
    string OpaqueProviderReference,
    string Region,
    string Classification,
    string Disposition,
    string BackupPolicyReference,
    int BackupPolicyVersion,
    string Status,
    long? VerifiedBusinessUnitId,
    string? VerificationEvidenceReference,
    string? VerificationEvidenceSha256,
    int VerificationVersion,
    DateTime? VerifiedOn,
    string? VerifiedBy,
    long Version);

public sealed record TenantActivationDataDecisionDto(
    long TenantId,
    bool DataGateReady,
    string Decision,
    IReadOnlyList<string> Blockers,
    TenantDataAssetDto? PostgreSqlTenantScope,
    string Boundary);

public sealed class TenantDataAssetNotFoundException(string message) : Exception(message);
public sealed class TenantDataAssetValidationException(string message) : Exception(message);
public sealed class TenantDataAssetConflictException(string message) : Exception(message);
