using System.Security.Claims;
using ERP_RFQ_Automation.AI;
using ERP_RFQ_Automation.Authorization;
using ERP_RFQ_Automation.PlatformGovernance;
using ERP_RFQ_Automation.Retention;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ERP_RFQ_Automation.Controllers;

[ApiController]
[Authorize]
[Route("api/platform-governance")]
public sealed class PlatformGovernanceController(
    PlatformGovernanceService artifacts,
    HumanActionService actions,
    AiTrustCenterService aiTrust,
    IAiExternalProviderTrust externalProviderTrust,
    AiExtractionReadinessService aiReadiness,
    CommercialDocumentArchiveService archive,
    EvidenceRetentionService retention,
    TenantDataControlService tenantData,
    QualityAnalyticsService quality) : ControllerBase
{
    [HttpGet("artifacts")]
    [RequireModulePermission("Users", PermissionAction.View)]
    public Task<IReadOnlyList<GovernedArtifactSummary>> ListArtifacts(
        [FromQuery] GovernedArtifactType? type,
        [FromQuery] string? search,
        CancellationToken ct) => artifacts.ListAsync(TenantId(), type, search, ct);

    [HttpGet("artifacts/{artifactId:long}")]
    [RequireModulePermission("Users", PermissionAction.View)]
    public async Task<ActionResult<GovernedArtifactDetail>> GetArtifact(long artifactId, CancellationToken ct)
    {
        try { return Ok(await artifacts.GetAsync(TenantId(), artifactId, ct)); }
        catch (PlatformGovernanceNotFoundException exception) { return NotFound(Problem(404, exception.Message)); }
    }

    [HttpPost("artifacts")]
    [RequireModulePermission("Users", PermissionAction.Edit)]
    public Task<ActionResult<ArtifactTransitionResult>> CreateArtifact(
        [FromBody] CreateGovernedArtifactCommand command, CancellationToken ct) =>
        Execute(() => artifacts.CreateAsync(TenantId(), ActorUserId(), IdempotencyKey(), command, ct));

    [HttpPost("artifacts/{artifactId:long}/versions")]
    [RequireModulePermission("Users", PermissionAction.Edit)]
    public Task<ActionResult<ArtifactTransitionResult>> CreateVersion(long artifactId,
        [FromBody] CreateGovernedArtifactVersionCommand command, CancellationToken ct) =>
        Execute(() => artifacts.CreateVersionAsync(TenantId(), artifactId, ActorUserId(),
            IdempotencyKey(), command, ct));

    [HttpPost("artifacts/{artifactId:long}/transition")]
    [RequireModulePermission("Users", PermissionAction.Edit)]
    public Task<ActionResult<ArtifactTransitionResult>> TransitionArtifact(long artifactId,
        [FromBody] TransitionGovernedArtifactCommand command, CancellationToken ct) =>
        Execute(() => artifacts.TransitionAsync(TenantId(), artifactId, ActorUserId(),
            IdempotencyKey(), command, ct));

    [HttpGet("actions")]
    [RequireModulePermission("Leads", PermissionAction.View)]
    public Task<IReadOnlyList<HumanActionItemDto>> ListActions([FromQuery] HumanActionStatus? status,
        CancellationToken ct) => actions.ListAsync(TenantId(), status, ct);

    [HttpGet("actions/{actionId:long}")]
    [RequireModulePermission("Leads", PermissionAction.View)]
    public async Task<ActionResult<HumanActionDetail>> GetAction(long actionId, CancellationToken ct)
    {
        try { return Ok(await actions.GetAsync(TenantId(), actionId, ct)); }
        catch (PlatformGovernanceNotFoundException exception) { return NotFound(Problem(404, exception.Message)); }
    }

    [HttpPost("actions")]
    [RequireModulePermission("Leads", PermissionAction.Edit)]
    public Task<ActionResult<HumanActionTransitionResult>> CreateAction(
        [FromBody] CreateHumanActionCommand command, CancellationToken ct) =>
        Execute(() => actions.CreateAsync(TenantId(), ActorUserId(), IdempotencyKey(), command, ct));

    [HttpPost("actions/{actionId:long}/transition")]
    [RequireModulePermission("Leads", PermissionAction.Edit)]
    public Task<ActionResult<HumanActionTransitionResult>> TransitionAction(long actionId,
        [FromBody] TransitionHumanActionCommand command, CancellationToken ct) =>
        Execute(() => actions.TransitionAsync(TenantId(), actionId, ActorUserId(),
            IdempotencyKey(), command, ct));

    [HttpPost("actions/bulk-transition")]
    [RequireModulePermission("Leads", PermissionAction.Edit)]
    public Task<ActionResult<BulkHumanActionTransitionResult>> BulkTransitionActions(
        [FromBody] BulkTransitionHumanActionCommand command, CancellationToken ct) =>
        Execute(() => actions.BulkTransitionAsync(TenantId(), ActorUserId(),
            IdempotencyKey(), command, ct));

    [HttpGet("ai-trust")]
    [RequireModulePermission("Users", PermissionAction.View)]
    public Task<AiTrustCenterView> GetAiTrust(CancellationToken ct) => aiTrust.GetAsync(TenantId(), ct);

    /// <summary>
    /// The tenant's external AI provider allow-list, together with the endpoint this
    /// deployment is actually configured to call and whether that endpoint is currently
    /// authorized for unstructured documents. Answering "is our AI on, against what, and
    /// who said yes?" must never again require reading source.
    /// </summary>
    [HttpGet("ai-trust/external-providers")]
    [RequireModulePermission("Users", PermissionAction.View)]
    public Task<AiExternalProviderTrustView> GetExternalProviders(CancellationToken ct) =>
        externalProviderTrust.GetAsync(TenantId(), ct);

    /// <summary>
    /// Why this tenant's documents are (or are not) being read by AI: every control in the
    /// extraction chain, in firing order, with the exact value to set and where. Read-only
    /// and carries no new authority — the remedies it names are platform Owner mutations, so
    /// a tenant admin can diagnose a dead-lettering pipeline without holding the authority to
    /// open an egress path.
    /// </summary>
    [HttpGet("ai-trust/readiness")]
    [RequireModulePermission("Users", PermissionAction.View)]
    public Task<AiExtractionReadinessReport> GetAiReadiness(CancellationToken ct) =>
        aiReadiness.EvaluateAsync(TenantId(), ct);

    [HttpGet("archive")]
    [RequireModulePermission("Users", PermissionAction.View)]
    public Task<ArchiveSearchResult> SearchArchive([FromQuery] ArchiveSearchRequest request,
        CancellationToken ct) => archive.SearchAsync(TenantId(), request, ct);

    [HttpPost("archive/{occurrenceId:long}/govern")]
    [RequireModulePermission("Users", PermissionAction.Edit)]
    public Task<ActionResult<ArchiveGovernanceResult>> GovernArchiveDocument(long occurrenceId,
        [FromBody] ArchiveGovernanceCommand command, CancellationToken ct) =>
        Execute(() => archive.GovernAsync(TenantId(), ActorUserId(), occurrenceId,
            IdempotencyKey(), command, ct));

    /// <summary>
    /// Current retention policy plus the tenant's own storage figures. Read-only and safe:
    /// nothing here can delete anything.
    /// </summary>
    [HttpGet("evidence-retention")]
    [RequireModulePermission("Users", PermissionAction.View)]
    public Task<EvidenceRetentionView> GetEvidenceRetention(CancellationToken ct) =>
        retention.GetAsync(TenantId(), ct);

    /// <summary>
    /// Sets the retention window and the opt-in switch. Saving a policy is the tenant's
    /// explicit consent to irreversible deletion — <c>POST /purge-run</c> refuses to delete
    /// anything until this has been done by a named user with a written reason.
    /// </summary>
    [HttpPut("evidence-retention/policy")]
    [RequireModulePermission("Users", PermissionAction.Edit)]
    public Task<ActionResult<EvidenceRetentionView>> UpdateEvidenceRetentionPolicy(
        [FromBody] UpdateEvidenceRetentionPolicyCommand command, CancellationToken ct) =>
        Execute(() => retention.UpdatePolicyAsync(TenantId(), ActorUserId(), IdempotencyKey(), command, ct));

    /// <summary>
    /// Runs — or simulates — a byte purge.
    ///
    /// <para>
    /// <c>dryRun</c> is defaulted to true by the binder below, so a malformed or truncated
    /// body can never be interpreted as "delete everything eligible". The destructive path
    /// has to be asked for explicitly, twice: once by enabling the policy, once by sending
    /// <c>dryRun:false</c> with a reason and an Idempotency-Key.
    /// </para>
    ///
    /// <para>
    /// This deletes stored bytes only. The document record, its SHA-256 fingerprint and
    /// every extracted field survive — and so does the personal data extracted from those
    /// files, which is why every response carries the not-erasure disclosure.
    /// </para>
    /// </summary>
    [HttpPost("evidence-retention/purge-run")]
    [RequireModulePermission("Users", PermissionAction.Edit)]
    public Task<ActionResult<EvidenceRetentionPurgeResult>> RunEvidenceRetentionPurge(
        [FromBody] EvidenceRetentionPurgeCommand? command, CancellationToken ct) =>
        Execute(() => retention.RunPurgeAsync(TenantId(), ActorUserId(), IdempotencyKey(),
            command ?? new EvidenceRetentionPurgeCommand(true, "Dry run."), ct));

    /// <summary>
    /// What the tenant can choose to clear, and what will never be touched.
    ///
    /// <para>Read-only and safe: nothing here deletes anything. It returns three buckets with a
    /// count and a byte total, plus the standing "kept, and why" panel. The bucket copy is
    /// finished product text written on the server, not codes for the client to decorate — the
    /// person reading this screen is a business owner, and he must never have to know what an
    /// "assembly" is to understand his own mail.</para>
    /// </summary>
    [HttpGet("tenant-data")]
    [RequireModulePermission("Users", PermissionAction.View)]
    public Task<TenantDataControlView> GetTenantData(CancellationToken ct) =>
        tenantData.GetAsync(TenantId(), ct);

    /// <summary>
    /// Clears — or simulates clearing — the buckets the tenant chose.
    ///
    /// <para>The same gate shape as the byte purge, for the same reasons. <c>dryRun</c> is
    /// defaulted to true by the binder below, so a malformed or truncated body can never be read
    /// as "delete everything"; the destructive path additionally needs a written reason, an
    /// Idempotency-Key, and the confirmation phrase — which is verified on the SERVER, because a
    /// phrase checked only in the browser is a decoration on a request anyone can send
    /// directly.</para>
    ///
    /// <para>Unlike the age-based purge this does NOT require the automatic-deletion policy to be
    /// switched on. That switch is consent to a STANDING rule that deletes on a schedule; this is
    /// a one-off, per-run, explicitly confirmed decision about records that produced nothing.
    /// Requiring a tenant to turn on automatic deletion of everything in order to delete four
    /// test emails the system sent to itself is the imposition this feature exists to remove.</para>
    /// </summary>
    [HttpPost("tenant-data/cleanup")]
    [RequireModulePermission("Users", PermissionAction.Edit)]
    public Task<ActionResult<TenantDataCleanupResult>> RunTenantDataCleanup(
        [FromBody] TenantDataCleanupCommand? command, CancellationToken ct) =>
        Execute(() => tenantData.RunCleanupAsync(TenantId(), ActorUserId(), IdempotencyKey(),
            command is null
                ? new TenantDataCleanupCommand(null, true, "Dry run.", null)
                : command with { DryRun = command.DryRun is false ? false : true }, ct));

    [HttpGet("quality")]
    [RequireModulePermission("Users", PermissionAction.View)]
    public Task<QualityAnalyticsView> GetQualityAnalytics([FromQuery] int windowDays = 30,
        [FromQuery] string? drilldown = null, CancellationToken ct = default) =>
        quality.GetAsync(TenantId(), windowDays, drilldown, ct);

    private async Task<ActionResult<T>> Execute<T>(Func<Task<T>> operation)
    {
        try { return Ok(await operation()); }
        catch (PlatformGovernanceNotFoundException exception) { return NotFound(Problem(404, exception.Message)); }
        catch (PlatformGovernanceConflictException exception) { return Conflict(Problem(409, exception.Message)); }
        catch (PlatformGovernanceValidationException exception) { return BadRequest(Problem(400, exception.Message)); }
        catch (UnauthorizedAccessException exception) { return Unauthorized(Problem(401, exception.Message)); }
    }

    private long TenantId() => long.TryParse(User.FindFirst("businessUnitId")?.Value, out var value) && value > 0
        ? value : throw new UnauthorizedAccessException("A valid authenticated tenant is required.");

    private long ActorUserId() => long.TryParse(User.FindFirst(ClaimTypes.NameIdentifier)?.Value
            ?? User.FindFirst("sub")?.Value, out var value) && value > 0
        ? value : throw new UnauthorizedAccessException("A valid authenticated actor is required.");

    private string IdempotencyKey()
    {
        var value = Request.Headers["Idempotency-Key"].ToString().Trim();
        if (string.IsNullOrWhiteSpace(value) || value.Length > 160 || value.Any(char.IsControl))
            throw new PlatformGovernanceValidationException(
                "Idempotency-Key is required and must not exceed 160 printable characters.");
        return value;
    }

    private static ProblemDetails Problem(int status, string detail) => new()
    {
        Status = status,
        Title = status switch { 400 => "Invalid request", 401 => "Invalid authentication context",
            404 => "Not found", 409 => "Governance conflict", _ => "Request failed" },
        Detail = detail
    };
}
