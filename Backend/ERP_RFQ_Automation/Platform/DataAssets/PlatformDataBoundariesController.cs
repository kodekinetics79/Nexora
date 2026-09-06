using ERP_RFQ_Automation.Platform.Auth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ERP_RFQ_Automation.Platform.DataAssets;

/// <param name="Declared">A boundary this deployment declares, exactly as the manifest resolved it.</param>
public sealed record PlatformDataBoundaryDto(
    string AssetType,
    string LogicalKey,
    string OpaqueProviderReference,
    string Region,
    string Classification,
    string Disposition,
    string BackupPolicyReference,
    int BackupPolicyVersion);

/// <param name="Configured">
/// False when this deployment has declared nothing. The console reads it to decide whether the
/// boundary is something it can register for the operator or something the operator has to type.
/// </param>
/// <param name="Defects">Declarations that were refused, with the reason. Empty is the normal case.</param>
public sealed record PlatformDataBoundaryManifestDto(
    bool Configured,
    PlatformDataBoundaryDto? PrimaryPostgreSqlScope,
    IReadOnlyList<PlatformDataBoundaryDto> Boundaries,
    IReadOnlyList<PlatformDataBoundaryDefect> Defects,
    string ConfigurationKey);

/// <summary>
/// What this deployment says its own infrastructure is.
///
/// <para>Read-only and deliberately so: these are process configuration, they are identical for
/// every tenant, and a console that could edit them would be a console that can change where a
/// customer's data is said to live without a deployment ever changing. Owner-only for the same
/// reason the registry is — it names this estate's provider references, which are opaque
/// identifiers rather than credentials but are still nobody else's business.</para>
/// </summary>
[ApiController]
[Route("api/platform/data-boundaries")]
[Authorize(Policy = PlatformPolicies.Owner)]
public sealed class PlatformDataBoundariesController(IPlatformDataBoundaryManifest manifest) : ControllerBase
{
    [HttpGet]
    public ActionResult<PlatformDataBoundaryManifestDto> Get()
    {
        var primary = manifest.For(TenantDataAssetTypes.PostgreSqlTenantScope);
        return Ok(new PlatformDataBoundaryManifestDto(
            manifest.IsConfigured,
            primary is null ? null : ToDto(primary),
            manifest.Boundaries.Select(ToDto).ToArray(),
            manifest.Defects,
            PlatformDataBoundaryManifest.SectionName));
    }

    private static PlatformDataBoundaryDto ToDto(PlatformDataBoundary boundary) => new(
        boundary.AssetType, boundary.LogicalKey, boundary.OpaqueProviderReference, boundary.Region,
        boundary.Classification, boundary.Disposition, boundary.BackupPolicyReference,
        boundary.BackupPolicyVersion);
}
