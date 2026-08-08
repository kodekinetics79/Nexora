namespace ERP_RFQ_Automation.Platform.DataAssets;

public static class TenantDataRecoveryEvidenceTypes
{
    public const string BackupSetObserved = "BackupSetObserved";
    public const string RestoreDrillCompleted = "RestoreDrillCompleted";
    public const string TombstoneReapplied = "TombstoneReapplied";
    public const string BackupDestructionConfirmed = "BackupDestructionConfirmed";
    public const string SubprocessorDeletionRequested = "SubprocessorDeletionRequested";
    public const string SubprocessorDeletionConfirmed = "SubprocessorDeletionConfirmed";
    public const string ResidencyVerified = "ResidencyVerified";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
    {
        BackupSetObserved, RestoreDrillCompleted, TombstoneReapplied,
        BackupDestructionConfirmed, SubprocessorDeletionRequested,
        SubprocessorDeletionConfirmed, ResidencyVerified
    };
}

/// <summary>
/// Append-only evidence from a provider or recovery operator. Provider references are opaque and
/// evidence bytes remain in the governed evidence store; this table retains their immutable hash.
/// There is intentionally no tenant FK so recovery/deletion proof survives tenant destruction.
/// </summary>
public sealed class TenantDataRecoveryEvidence
{
    public long Id { get; set; }
    public long TenantId { get; set; }
    public long? TenantDataAssetId { get; set; }
    public string ScopeKey { get; set; } = null!;
    public string EvidenceType { get; set; } = null!;
    public string OpaqueProviderReference { get; set; } = null!;
    public string? OpaqueBackupSetReference { get; set; }
    public DateTime? RecoveryPointUtc { get; set; }
    public DateTime? OperationStartedUtc { get; set; }
    public DateTime CompletedUtc { get; set; }
    public int? ConfiguredRpoSeconds { get; set; }
    public int? ConfiguredRtoSeconds { get; set; }
    public int? ActualRecoverySeconds { get; set; }
    public DateTime? RetainUntilUtc { get; set; }
    public long? CustomerRowsObserved { get; set; }
    public string EvidenceReference { get; set; } = null!;
    public string EvidenceSha256 { get; set; } = null!;
    public string CorrelationId { get; set; } = null!;
    public string IdempotencyKey { get; set; } = null!;
    public long ActorPlatformUserId { get; set; }
    public string ActorEmail { get; set; } = null!;
    public string Reason { get; set; } = null!;
    public DateTime RecordedUtc { get; set; }
}

/// <summary>Immutable certificate issued only after every required data boundary is resolved.</summary>
public sealed class TenantDeletionCertificate
{
    public long Id { get; set; }
    public long TenantId { get; set; }
    public string TenantSlug { get; set; } = null!;
    public DateTime PurgedUtc { get; set; }
    public DateTime CertifiedUtc { get; set; }
    public long ActorPlatformUserId { get; set; }
    public string ActorEmail { get; set; } = null!;
    public string EvidenceManifestSha256 { get; set; } = null!;
    public string EvidenceIdsJson { get; set; } = null!;
    public string Reason { get; set; } = null!;
}
