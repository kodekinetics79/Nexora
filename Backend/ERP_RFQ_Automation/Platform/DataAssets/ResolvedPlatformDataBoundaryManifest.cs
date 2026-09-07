using ERP_RFQ_Automation.Models;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.Platform.DataAssets;

/// <summary>Where a resolved boundary came from, for the console and the audit record.</summary>
public static class DataBoundarySources
{
    /// <summary>An Owner recorded it in the console. Outranks configuration.</summary>
    public const string Console = "console";

    /// <summary><c>Platform:DataBoundaries</c> in the deployment's own configuration.</summary>
    public const string Configuration = "configuration";

    /// <summary>Nothing has been declared anywhere.</summary>
    public const string None = "none";
}

/// <summary>
/// The manifest as the rest of the system reads it: the console's answer first, then the
/// deployment's configuration, then nothing.
///
/// <para><b>Why the console outranks configuration, and not the other way round.</b> Both are
/// deliberate statements, so this is not a precedence puzzle about trust — it is about who can fix
/// a wrong one. A value recorded in the console is changed in the console, by the Owner who is
/// standing there, with a reason attached and an audit row. A value in configuration is changed by
/// a deploy. If configuration won, an operator who noticed the region was wrong would have no way
/// to correct it and no way to find out why their correction had no effect.</para>
///
/// <para><b>Scoped, not singleton.</b> It reads a table, so it lives with a DbContext — unlike
/// <see cref="PlatformDataBoundaryManifest"/>, which is process configuration and is registered
/// once. The row is read at most once per instance: every consumer resolves the boundary two or
/// three times in a single operation and none of them should each cost a query.</para>
/// </summary>
public sealed class ResolvedPlatformDataBoundaryManifest(
    ErpRfqAutomationContext db,
    PlatformDataBoundaryManifest configuration) : IPlatformDataBoundaryManifest
{
    private bool _loaded;
    private PlatformDataBoundarySettings? _settings;

    /// <summary>The console row, or null when none has been recorded.</summary>
    public PlatformDataBoundarySettings? Settings
    {
        get
        {
            if (_loaded) return _settings;
            _loaded = true;
            try
            {
                _settings = db.Set<PlatformDataBoundarySettings>().AsNoTracking()
                    .SingleOrDefault(x => x.Id == PlatformDataBoundarySettings.SingletonId);
            }
            // A deployment whose migration has not run yet still has a working configuration path
            // and a working manual path, and failing here would take both down over a table that is
            // allowed not to exist. That forgiveness stops at the edge of somebody else's
            // transaction: PostgreSQL aborts the WHOLE transaction on a failed statement, so
            // swallowing the error inside the provisioning step's unit of work would turn one
            // legible failure — a missing table, a missing GRANT — into a cascade of unrelated
            // "current transaction is aborted" errors several steps later, with nothing naming the
            // cause. Inside a transaction the error is left to travel.
            catch (Exception) when (db.Database.CurrentTransaction is null)
            {
                _settings = null;
            }

            return _settings;
        }
    }

    /// <summary>Which of the two answered, for the console to explain itself with.</summary>
    public string Source =>
        Settings is not null ? DataBoundarySources.Console
        : configuration.IsConfigured ? DataBoundarySources.Configuration
        : DataBoundarySources.None;

    public bool IsConfigured => Settings is not null || configuration.IsConfigured;

    public IReadOnlyList<PlatformDataBoundary> Boundaries
    {
        get
        {
            var primary = FromSettings();
            if (primary is null) return configuration.Boundaries;

            // The console row describes the PRIMARY database only. Any other boundary type the
            // deployment declared in configuration is kept: the two are not alternatives, and
            // dropping the object store because somebody filled in the database form would take
            // deletion certification backwards.
            return configuration.Boundaries
                .Where(x => x.AssetType != TenantDataAssetTypes.PostgreSqlTenantScope)
                .Append(primary)
                .OrderBy(x => x.AssetType, StringComparer.Ordinal)
                .ToArray();
        }
    }

    public IReadOnlyList<PlatformDataBoundaryDefect> Defects => configuration.Defects;

    public PlatformDataBoundary? For(string assetType) =>
        assetType == TenantDataAssetTypes.PostgreSqlTenantScope && FromSettings() is { } primary
            ? primary
            : configuration.For(assetType);

    public string? DefectFor(string assetType) =>
        assetType == TenantDataAssetTypes.PostgreSqlTenantScope && FromSettings() is not null
            ? null
            : configuration.DefectFor(assetType);

    private PlatformDataBoundary? FromSettings()
    {
        if (Settings is not { } s) return null;
        return new PlatformDataBoundary(
            TenantDataAssetTypes.PostgreSqlTenantScope,
            TenantDataAssetRegistryService.PostgreSqlLogicalKey,
            s.OpaqueProviderReference,
            s.Region.ToLowerInvariant(),
            PlatformDataBoundaryDefaults.Classification(TenantDataAssetTypes.PostgreSqlTenantScope),
            PlatformDataBoundaryDefaults.Disposition(TenantDataAssetTypes.PostgreSqlTenantScope),
            s.BackupPolicyReference,
            s.BackupPolicyVersion);
    }
}
