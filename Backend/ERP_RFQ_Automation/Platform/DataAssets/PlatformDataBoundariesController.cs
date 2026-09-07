using System.ComponentModel.DataAnnotations;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Platform.Auth;
using ERP_RFQ_Automation.Platform.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.Platform.DataAssets;

/// <param name="Declared">A boundary this deployment declares, exactly as it resolved.</param>
public sealed record PlatformDataBoundaryDto(
    string AssetType,
    string LogicalKey,
    string OpaqueProviderReference,
    string Region,
    string Classification,
    string Disposition,
    string BackupPolicyReference,
    int BackupPolicyVersion);

/// <summary>What the process can read about its own database, offered as a suggestion to confirm.</summary>
public sealed record DatabaseSelfObservationDto(
    string? Host,
    string? ProviderName,
    string? OpaqueProviderReference,
    string? Region,
    string Basis,
    bool IsUsable);

/// <param name="Source">console · configuration · none — see <see cref="DataBoundarySources"/>.</param>
/// <param name="Observation">
/// Never null. When it carries a provider reference and a region the console can offer them for
/// confirmation; when it does not, it still says what host was read and why nothing could be taken
/// from it, which is the sentence an operator needs in order to know what to type instead.
/// </param>
public sealed record PlatformDataBoundaryManifestDto(
    bool Configured,
    string Source,
    PlatformDataBoundaryDto? PrimaryPostgreSqlScope,
    IReadOnlyList<PlatformDataBoundaryDto> Boundaries,
    IReadOnlyList<PlatformDataBoundaryDefect> Defects,
    DatabaseSelfObservationDto Observation,
    string? RecordedBy,
    DateTime? RecordedOn,
    string? RecordedBasis,
    string ConfigurationKey);

/// <summary>
/// What an Owner is confirming or entering. Provider reference and region are optional: omitted
/// means "use what the process observed", which is the one-click path and is recorded as
/// <c>observed-and-confirmed</c>.
/// </summary>
public sealed class RecordPlatformDataBoundaryRequest
{
    [StringLength(256)]
    public string? OpaqueProviderReference { get; set; }

    [StringLength(64)]
    public string? Region { get; set; }

    [Required, StringLength(128)]
    public string BackupPolicyReference { get; set; } = null!;

    [Range(1, int.MaxValue)]
    public int BackupPolicyVersion { get; set; } = 1;

    [StringLength(1000)]
    public string? Reason { get; set; }
}

/// <summary>
/// What this deployment says its own infrastructure is — and, when nobody has said, what the
/// running process can read off its own database connection.
///
/// <para><b>Why this is a console screen and not four environment variables.</b> The person who
/// meets <c>data.residency-isolation</c> is an operator onboarding a customer. Telling them to set
/// <c>Platform__DataBoundaries__PostgreSqlTenantScope__OpaqueProviderReference</c> on the API
/// service asks them for a deploy, a dashboard they may not have, and a value — the Neon endpoint
/// id — that nothing in their world contains. The process, meanwhile, is holding an open
/// connection to precisely that database. So the server reads its own address, the console shows
/// it, and an Owner confirms it once for the whole platform. Configuration still works, and still
/// wins for infrastructure-as-code deployments that prefer it; it is no longer the only door.</para>
/// </summary>
[ApiController]
[Route("api/platform/data-boundaries")]
[Authorize(Policy = PlatformPolicies.Owner)]
public sealed class PlatformDataBoundariesController(
    ErpRfqAutomationContext db,
    IPlatformDataBoundaryManifest manifest,
    IDatabaseSelfObserver observer,
    IPlatformAuditService audit,
    ILogger<PlatformDataBoundariesController> logger) : ControllerBase
{
    public const string RecordAction = "platform.data-boundary.record";

    /// <summary>Same floor as every other governed platform statement.</summary>
    private const int MinimumReasonLength = 15;

    private const string ConfirmationReason =
        "Confirmed the database this deployment is connected to, read from the live connection.";

    [HttpGet]
    public ActionResult<PlatformDataBoundaryManifestDto> Get()
    {
        var observation = observer.Observe(db);
        var primary = manifest.For(TenantDataAssetTypes.PostgreSqlTenantScope);
        var resolved = manifest as ResolvedPlatformDataBoundaryManifest;
        var settings = resolved?.Settings;

        return Ok(new PlatformDataBoundaryManifestDto(
            manifest.IsConfigured,
            resolved?.Source ?? (manifest.IsConfigured ? DataBoundarySources.Configuration : DataBoundarySources.None),
            primary is null ? null : ToDto(primary),
            manifest.Boundaries.Select(ToDto).ToArray(),
            manifest.Defects,
            new DatabaseSelfObservationDto(
                observation.Host, observation.ProviderName, observation.OpaqueProviderReference,
                observation.Region, observation.Basis, observation.IsUsable),
            settings?.RecordedBy,
            settings?.RecordedOn,
            settings?.Basis,
            PlatformDataBoundaryManifest.SectionName));
    }

    /// <summary>
    /// Records what this deployment's database is, for every tenant. Owner-only and audited: it is
    /// the statement every tenant's residency evidence is measured against.
    /// </summary>
    [HttpPut]
    public async Task<ActionResult<PlatformDataBoundaryManifestDto>> Record(
        [FromBody] RecordPlatformDataBoundaryRequest request, CancellationToken ct)
    {
        if (!ModelState.IsValid) return BadRequest(ModelState);
        ArgumentNullException.ThrowIfNull(request);

        var observation = observer.Observe(db);

        // Omitted means "what the process observed". Both must resolve from the SAME source: a
        // typed region beside an observed provider reference is two statements about two possibly
        // different databases wearing one row.
        var typedProvider = Trim(request.OpaqueProviderReference);
        var typedRegion = Trim(request.Region);
        var confirming = typedProvider is null && typedRegion is null;

        var provider = typedProvider ?? observation.OpaqueProviderReference;
        var region = typedRegion ?? observation.Region;

        if (string.IsNullOrWhiteSpace(provider) || string.IsNullOrWhiteSpace(region))
            return BadRequest(new
            {
                error = confirming
                    ? "This deployment's database host does not say what its provider reference and "
                      + $"region are, so they have to be stated. {observation.Basis}"
                    : "Both a provider reference and a region are required."
            });

        if (Invalid(provider, out var providerProblem))
            return BadRequest(new { error = $"The provider reference {providerProblem}" });

        var reason = Trim(request.Reason) ?? (confirming ? ConfirmationReason : null);
        if (reason is null || reason.Length < MinimumReasonLength)
            return BadRequest(new
            {
                error = $"A reason of at least {MinimumReasonLength} characters is required. This row is "
                        + "what every tenant's residency evidence is measured against."
            });

        // Refused rather than reconciled, for the same reason the tenant data-region endpoint
        // refuses: the registered assets are the evidence and this row is the claim about them.
        // Rewriting the claim under assets that already carry the old one is how a residency
        // control gets satisfied by editing a string.
        var conflicting = await db.Set<TenantDataAsset>().AsNoTracking()
            .Where(a => a.LogicalKey == TenantDataAssetRegistryService.PostgreSqlLogicalKey)
            .Select(a => new { a.TenantId, a.Region, a.OpaqueProviderReference })
            .ToListAsync(ct);
        var mismatched = conflicting
            .Where(a => !string.Equals(a.Region, region, StringComparison.OrdinalIgnoreCase)
                        || !string.Equals(a.OpaqueProviderReference, provider, StringComparison.Ordinal))
            .ToList();
        if (mismatched.Count > 0)
            return Conflict(new
            {
                error = "Tenants are already registered against a different database, and this row only "
                        + "claims what those registrations prove. Re-register or move them first: "
                        + string.Join("; ", mismatched.Take(5).Select(a =>
                            $"tenant {a.TenantId} is on '{a.OpaqueProviderReference}' in '{a.Region}'"))
                        + (mismatched.Count > 5 ? $"; and {mismatched.Count - 5} more." : ".")
            });

        var now = DateTime.UtcNow;
        var actor = User.FindFirst("email")?.Value ?? "platform";
        var basis = confirming ? ProvenanceBases.ObservedAndConfirmed : ProvenanceBases.Entered;

        var strategy = db.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            db.ChangeTracker.Clear();
            await using var tx = await db.Database.BeginTransactionAsync(ct);

            var row = await db.Set<PlatformDataBoundarySettings>()
                .SingleOrDefaultAsync(x => x.Id == PlatformDataBoundarySettings.SingletonId, ct);
            var previous = row is null ? null : new { row.OpaqueProviderReference, row.Region, row.BackupPolicyReference, row.BackupPolicyVersion };

            if (row is null)
            {
                row = new PlatformDataBoundarySettings { Id = PlatformDataBoundarySettings.SingletonId, Version = 1 };
                db.Set<PlatformDataBoundarySettings>().Add(row);
            }
            else
            {
                row.Version += 1;
            }

            row.OpaqueProviderReference = provider;
            row.Region = region.ToLowerInvariant();
            row.BackupPolicyReference = Trim(request.BackupPolicyReference)!;
            row.BackupPolicyVersion = request.BackupPolicyVersion;
            row.Basis = basis;
            row.ObservedHost = observation.Host;
            row.Reason = reason;
            row.RecordedBy = actor;
            row.RecordedOn = now;
            await db.SaveChangesAsync(ct);

            await audit.WriteAsync(User, RecordAction, nameof(PlatformDataBoundarySettings),
                PlatformDataBoundarySettings.SingletonId.ToString(),
                new
                {
                    from = previous,
                    to = new { row.OpaqueProviderReference, row.Region, row.BackupPolicyReference, row.BackupPolicyVersion },
                    basis,
                    observedHost = observation.Host,
                    observationBasis = observation.Basis,
                    reason
                }, null, HttpContext, ct);

            await tx.CommitAsync(ct);
        });

        logger.LogInformation(
            "Platform data boundary recorded by {Actor} as {Basis}: provider {Provider} in {Region}.",
            actor, basis, provider, region);

        return Get();
    }

    /// <summary>
    /// The registry's own rule, applied before the row is stored rather than at the first
    /// registration — so a bad value is refused on the screen where it was typed.
    /// </summary>
    private static bool Invalid(string value, out string problem)
    {
        problem = string.Empty;
        if (value.Contains("://", StringComparison.Ordinal)
            || value.Any(char.IsWhiteSpace)
            || value.Contains('@') || value.Contains('=') || value.Contains('?'))
        {
            problem = "must be an identifier only — no URL, connection string, credential, "
                      + "whitespace, @, = or ?.";
            return true;
        }

        return false;
    }

    private static string? Trim(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static PlatformDataBoundaryDto ToDto(PlatformDataBoundary boundary) => new(
        boundary.AssetType, boundary.LogicalKey, boundary.OpaqueProviderReference, boundary.Region,
        boundary.Classification, boundary.Disposition, boundary.BackupPolicyReference,
        boundary.BackupPolicyVersion);
}
