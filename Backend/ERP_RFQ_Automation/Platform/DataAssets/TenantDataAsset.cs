namespace ERP_RFQ_Automation.Platform.DataAssets;

public static class TenantDataAssetTypes
{
    public const string PostgreSqlTenantScope = "PostgreSqlTenantScope";
}

public static class TenantDataAssetClassifications
{
    public const string CustomerData = "CustomerData";
}

public static class TenantDataAssetDispositions
{
    public const string BackupRetainedUntilExpiryThenDestroy = "BackupRetainedUntilExpiryThenDestroy";
}

public static class TenantDataAssetStatuses
{
    public const string Registered = "Registered";
    public const string Verified = "Verified";
}

/// <summary>
/// Platform-owned inventory of one logical tenant data boundary. Provider references are opaque
/// identifiers, never connection strings or credentials. Verification records evidence that an
/// external probe observed the expected tenant scope; it does not grant application access.
/// </summary>
public sealed class TenantDataAsset
{
    public long Id { get; set; }
    public long TenantId { get; set; }
    public string LogicalKey { get; set; } = null!;
    public string AssetType { get; set; } = TenantDataAssetTypes.PostgreSqlTenantScope;
    public string OpaqueProviderReference { get; set; } = null!;
    public string Region { get; set; } = null!;
    public string Classification { get; set; } = TenantDataAssetClassifications.CustomerData;
    public string Disposition { get; set; } = TenantDataAssetDispositions.BackupRetainedUntilExpiryThenDestroy;
    public string BackupPolicyReference { get; set; } = null!;
    public int BackupPolicyVersion { get; set; }
    public string Status { get; set; } = TenantDataAssetStatuses.Registered;
    public long? VerifiedBusinessUnitId { get; set; }
    public string? VerificationEvidenceReference { get; set; }
    public string? VerificationEvidenceSha256 { get; set; }
    public int VerificationVersion { get; set; }
    public DateTime? VerifiedOn { get; set; }
    public string? VerifiedBy { get; set; }
    public long Version { get; set; } = 1;
    public DateTime CreatedOn { get; set; }
    public string CreatedBy { get; set; } = null!;
    public DateTime? ModifiedOn { get; set; }
    public string? ModifiedBy { get; set; }
}
