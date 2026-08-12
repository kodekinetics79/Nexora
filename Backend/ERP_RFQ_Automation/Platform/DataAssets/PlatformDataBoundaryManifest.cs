namespace ERP_RFQ_Automation.Platform.DataAssets;

/// <summary>
/// One boundary as the DEPLOYMENT declares it, before anything has been validated.
///
/// <para>Bound straight from <c>Platform:DataBoundaries:{AssetType}</c>. Every field is nullable
/// so a half-filled section reads as half-filled rather than as a set of defaults nobody chose —
/// see <see cref="PlatformDataBoundaryManifest"/> for why an invalid entry is refused instead of
/// being repaired.</para>
/// </summary>
public sealed class PlatformDataBoundaryEntry
{
    /// <summary>
    /// Optional. Defaults to the conventional key for the asset type. The primary PostgreSQL scope
    /// is pinned to <see cref="TenantDataAssetRegistryService.PostgreSqlLogicalKey"/> by the
    /// registry itself, and a manifest that disagrees is refused rather than silently corrected.
    /// </summary>
    public string? LogicalKey { get; set; }

    /// <summary>The provider's own identifier for this boundary. Never a URL, connection string or credential.</summary>
    public string? OpaqueProviderReference { get; set; }

    /// <summary>Must equal the tenant's contractual <c>DataRegion</c>, or registration is refused.</summary>
    public string? Region { get; set; }

    public string? BackupPolicyReference { get; set; }

    public int BackupPolicyVersion { get; set; }

    /// <summary>Optional override of the per-type default in <see cref="PlatformDataBoundaryDefaults"/>.</summary>
    public string? Classification { get; set; }

    /// <summary>Optional override of the per-type default in <see cref="PlatformDataBoundaryDefaults"/>.</summary>
    public string? Disposition { get; set; }
}

/// <summary>
/// One boundary after validation: every field present, every enumerated value known.
/// </summary>
public sealed record PlatformDataBoundary(
    string AssetType,
    string LogicalKey,
    string OpaqueProviderReference,
    string Region,
    string Classification,
    string Disposition,
    string BackupPolicyReference,
    int BackupPolicyVersion);

/// <summary>
/// The conventional logical key, classification and disposition per boundary type.
///
/// <para><b>These are POLICY, not infrastructure.</b> They say what a cache is for and what has to
/// happen to it when a tenant is purged — statements about the product, identical in every
/// deployment — and they are the same values the registry already forces for the primary
/// PostgreSQL scope. What they deliberately do NOT contain is a provider reference, a region or a
/// backup policy: those are facts about one deployment's estate, they differ between deployments,
/// and a default for any of them would be this file inventing an answer to a question only the
/// deployment can answer. A boundary the manifest does not declare stays unregistered.</para>
/// </summary>
public static class PlatformDataBoundaryDefaults
{
    private sealed record Shape(string LogicalKey, string Classification, string Disposition);

    private static readonly Dictionary<string, Shape> Shapes = new(StringComparer.Ordinal)
    {
        [TenantDataAssetTypes.PostgreSqlTenantScope] = new(
            TenantDataAssetRegistryService.PostgreSqlLogicalKey,
            TenantDataAssetClassifications.CustomerData,
            TenantDataAssetDispositions.BackupRetainedUntilExpiryThenDestroy),

        // Customer documents as uploaded. Destroyed with the tenant rather than retained: unlike
        // the database there is no point-in-time recovery obligation attached to them.
        [TenantDataAssetTypes.ObjectStorage] = new(
            "objectstorage.primary",
            TenantDataAssetClassifications.CustomerData,
            TenantDataAssetDispositions.DestroyOnTenantPurge),

        [TenantDataAssetTypes.SearchIndex] = new(
            "search.primary",
            TenantDataAssetClassifications.DerivedCustomerData,
            TenantDataAssetDispositions.DestroyOnTenantPurge),

        [TenantDataAssetTypes.EmbeddingStore] = new(
            "embeddings.primary",
            TenantDataAssetClassifications.DerivedCustomerData,
            TenantDataAssetDispositions.DestroyOnTenantPurge),

        [TenantDataAssetTypes.Cache] = new(
            "cache.primary",
            TenantDataAssetClassifications.DerivedCustomerData,
            TenantDataAssetDispositions.DestroyOnTenantPurge),

        [TenantDataAssetTypes.QueuePayload] = new(
            "queue.primary",
            TenantDataAssetClassifications.DerivedCustomerData,
            TenantDataAssetDispositions.DestroyOnTenantPurge),

        [TenantDataAssetTypes.GeneratedExport] = new(
            "export.primary",
            TenantDataAssetClassifications.DerivedCustomerData,
            TenantDataAssetDispositions.DestroyOnTenantPurge),

        // Somebody else's estate. Nexora cannot destroy it; it can only ask and record the answer,
        // which is exactly what ProviderDeletionRequired means to the certification decision.
        [TenantDataAssetTypes.AiOcrProvider] = new(
            "ai-ocr.primary",
            TenantDataAssetClassifications.CustomerData,
            TenantDataAssetDispositions.ProviderDeletionRequired),

        [TenantDataAssetTypes.Subprocessor] = new(
            "subprocessor.primary",
            TenantDataAssetClassifications.CustomerData,
            TenantDataAssetDispositions.ProviderDeletionRequired)
    };

    public static string LogicalKey(string assetType) =>
        Shapes.TryGetValue(assetType, out var shape) ? shape.LogicalKey : assetType.ToLowerInvariant();

    public static string Classification(string assetType) =>
        Shapes.TryGetValue(assetType, out var shape)
            ? shape.Classification
            : TenantDataAssetClassifications.CustomerData;

    public static string Disposition(string assetType) =>
        Shapes.TryGetValue(assetType, out var shape)
            ? shape.Disposition
            : TenantDataAssetDispositions.DestroyOnTenantPurge;
}

/// <summary>
/// A declared boundary that could not be used, and the sentence explaining why.
/// </summary>
public sealed record PlatformDataBoundaryDefect(string AssetType, string Reason);

public interface IPlatformDataBoundaryManifest
{
    /// <summary>True when the deployment has declared at least one usable boundary.</summary>
    bool IsConfigured { get; }

    /// <summary>Every usable boundary, ordered by asset type.</summary>
    IReadOnlyList<PlatformDataBoundary> Boundaries { get; }

    /// <summary>Every declared boundary this manifest refused, with the reason. Empty is the normal case.</summary>
    IReadOnlyList<PlatformDataBoundaryDefect> Defects { get; }

    /// <summary>The usable boundary for one asset type, or null when the deployment has not declared it.</summary>
    PlatformDataBoundary? For(string assetType);

    /// <summary>Why the boundary for this asset type is unusable, or null when it is usable or undeclared.</summary>
    string? DefectFor(string assetType);
}

/// <summary>
/// What this deployment's own infrastructure IS, per data-boundary type, read from configuration.
///
/// <para><b>The defect this closes.</b> Nexora hosts its own estate: every tenant's
/// <c>postgresql.primary</c> scope is the same Neon database behind per-tenant row-level security,
/// and the cache, queue, search index, embedding store and object store are the platform's and are
/// identical for every tenant. The activation gate and the deletion-certification gate nevertheless
/// required an operator to type all of that in by hand, per tenant, from memory — the platform
/// asking a human to describe the platform. Nine boundary types times one form each is how a
/// tenant sat unactivatable while somebody worked out what to put in "opaque provider reference".</para>
///
/// <para><b>Configuration, not constants.</b> These facts belong to the DEPLOYMENT — a self-hosted
/// installation's database is not Nexora's, and its region, provider reference and backup policy
/// are its own. Hard-coding them here would make every deployment claim Nexora's estate as its
/// own, which is worse than asking.</para>
///
/// <para><b>Absent configuration degrades; it does not invent.</b> A deployment that declares
/// nothing gets <see cref="IsConfigured"/> false, nothing is registered automatically, and the
/// manual register-then-verify path is exactly what it was. A deployment that declares a boundary
/// BADLY gets a recorded defect and that boundary stays unregistered — the automation refuses to
/// guess a provider reference, and the control it would have satisfied stays blocking. Neither is
/// a host that fails to start: a mistyped backup-policy version must not take the product down.</para>
/// </summary>
public sealed class PlatformDataBoundaryManifest : IPlatformDataBoundaryManifest
{
    public const string SectionName = "Platform:DataBoundaries";

    private readonly Dictionary<string, PlatformDataBoundary> _boundaries = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _defects = new(StringComparer.Ordinal);

    public PlatformDataBoundaryManifest(IConfiguration configuration, ILogger<PlatformDataBoundaryManifest>? log = null)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        foreach (var section in configuration.GetSection(SectionName).GetChildren())
        {
            var assetType = section.Key?.Trim();
            if (string.IsNullOrWhiteSpace(assetType)) continue;

            // Matched case-insensitively so "postgreSqlTenantScope" in a JSON file resolves, then
            // normalised to the canonical spelling the registry validates against.
            var canonical = TenantDataAssetTypes.All
                .FirstOrDefault(known => string.Equals(known, assetType, StringComparison.OrdinalIgnoreCase));
            if (canonical is null)
            {
                Refuse(assetType,
                    $"'{assetType}' is not a known tenant data-boundary type. Known types: "
                    + string.Join(", ", TenantDataAssetTypes.All.Order()) + ".", log);
                continue;
            }

            var entry = section.Get<PlatformDataBoundaryEntry>() ?? new PlatformDataBoundaryEntry();
            if (Resolve(canonical, entry, out var boundary, out var reason))
                _boundaries[canonical] = boundary!;
            else
                Refuse(canonical, reason!, log);
        }

        Boundaries = _boundaries.Values.OrderBy(x => x.AssetType, StringComparer.Ordinal).ToArray();
        Defects = _defects.Select(x => new PlatformDataBoundaryDefect(x.Key, x.Value))
            .OrderBy(x => x.AssetType, StringComparer.Ordinal).ToArray();
    }

    public bool IsConfigured => _boundaries.Count > 0;

    public IReadOnlyList<PlatformDataBoundary> Boundaries { get; }

    public IReadOnlyList<PlatformDataBoundaryDefect> Defects { get; }

    public PlatformDataBoundary? For(string assetType) =>
        _boundaries.GetValueOrDefault(assetType);

    public string? DefectFor(string assetType) =>
        _defects.GetValueOrDefault(assetType);

    private void Refuse(string assetType, string reason, ILogger? log)
    {
        _defects[assetType] = reason;
        log?.LogWarning(
            "Platform data-boundary manifest entry '{AssetType}' was refused and will not be "
            + "registered automatically: {Reason} The boundary stays on the manual registration path.",
            assetType, reason);
    }

    private static bool Resolve(
        string assetType, PlatformDataBoundaryEntry entry,
        out PlatformDataBoundary? boundary, out string? reason)
    {
        boundary = null;
        reason = null;

        var logicalKey = Trimmed(entry.LogicalKey) ?? PlatformDataBoundaryDefaults.LogicalKey(assetType);
        var provider = Trimmed(entry.OpaqueProviderReference);
        var region = Trimmed(entry.Region);
        var backupPolicy = Trimmed(entry.BackupPolicyReference);
        var classification = Trimmed(entry.Classification) ?? PlatformDataBoundaryDefaults.Classification(assetType);
        var disposition = Trimmed(entry.Disposition) ?? PlatformDataBoundaryDefaults.Disposition(assetType);

        if (provider is null)
        {
            reason = "OpaqueProviderReference is missing. There is no safe default for it — it names "
                     + "a specific piece of this deployment's estate — so the boundary is left unregistered.";
            return false;
        }

        if (region is null)
        {
            reason = "Region is missing. It has to agree with each tenant's contractual data region, "
                     + "and a guessed region would be a residency claim nobody made.";
            return false;
        }

        if (backupPolicy is null)
        {
            reason = "BackupPolicyReference is missing.";
            return false;
        }

        if (entry.BackupPolicyVersion <= 0)
        {
            reason = $"BackupPolicyVersion must be a positive integer; '{entry.BackupPolicyVersion}' is not.";
            return false;
        }

        if (!TenantDataAssetClassifications.All.Contains(classification))
        {
            reason = $"Classification '{classification}' is not one of "
                     + string.Join(", ", TenantDataAssetClassifications.All.Order()) + ".";
            return false;
        }

        if (!TenantDataAssetDispositions.All.Contains(disposition))
        {
            reason = $"Disposition '{disposition}' is not one of "
                     + string.Join(", ", TenantDataAssetDispositions.All.Order()) + ".";
            return false;
        }

        // Pinned rather than corrected. The registry refuses any other key for this type, and a
        // manifest that names a different one is a deployment that believes something untrue about
        // its own database — worth a refusal an operator can read, not a silent rewrite.
        if (assetType == TenantDataAssetTypes.PostgreSqlTenantScope
            && !string.Equals(logicalKey, TenantDataAssetRegistryService.PostgreSqlLogicalKey,
                StringComparison.OrdinalIgnoreCase))
        {
            reason = $"LogicalKey must be '{TenantDataAssetRegistryService.PostgreSqlLogicalKey}' for the "
                     + $"primary PostgreSQL tenant scope; '{logicalKey}' was declared.";
            return false;
        }

        boundary = new PlatformDataBoundary(
            assetType, logicalKey.ToLowerInvariant(), provider, region.ToLowerInvariant(),
            classification, disposition, backupPolicy, entry.BackupPolicyVersion);
        return true;
    }

    private static string? Trimmed(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
