using System.Security.Claims;
using ERP_RFQ_Automation.AI;
using ERP_RFQ_Automation.Authorization;
using ERP_RFQ_Automation.PlatformGovernance;
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
    CommercialDocumentArchiveService archive,
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

    [HttpPut("ai-trust/policy")]
    [RequireModulePermission("Users", PermissionAction.Edit)]
    public Task<ActionResult<AiTrustPolicyMutationResult>> UpdateAiTrustPolicy(
        [FromBody] UpdateAiTrustPolicyCommand command, CancellationToken ct) =>
        Execute(() => aiTrust.UpdateAsync(TenantId(), ActorUserId(), IdempotencyKey(), command, ct));

    [HttpPost("ai-trust/policy/rollback")]
    [RequireModulePermission("Users", PermissionAction.Edit)]
    public Task<ActionResult<AiTrustPolicyMutationResult>> RollbackAiTrustPolicy(
        [FromBody] RollbackAiTrustPolicyCommand command, CancellationToken ct) =>
        Execute(() => aiTrust.RollbackAsync(TenantId(), ActorUserId(), IdempotencyKey(), command, ct));

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
    /// Authorize ONE external inference endpoint for this tenant. Opt-in only: no tenant
    /// has an authorization until a named user creates one here with a written
    /// justification, and <c>unstructuredDocumentsAllowed</c> defaults to false.
    /// </summary>
    [HttpPost("ai-trust/external-providers")]
    [RequireModulePermission("Users", PermissionAction.Edit)]
    public Task<ActionResult<AiExternalProviderMutationResult>> AuthorizeExternalProvider(
        [FromBody] AuthorizeAiExternalProviderCommand command, CancellationToken ct) =>
        Execute(() => externalProviderTrust.AuthorizeAsync(
            TenantId(), ActorUserId(), IdempotencyKey(), command, ct));

    /// <summary>Revoke an authorization. Takes effect on the next extraction, no restart.</summary>
    [HttpPost("ai-trust/external-providers/revoke")]
    [RequireModulePermission("Users", PermissionAction.Edit)]
    public Task<ActionResult<AiExternalProviderMutationResult>> RevokeExternalProvider(
        [FromBody] RevokeAiExternalProviderCommand command, CancellationToken ct) =>
        Execute(() => externalProviderTrust.RevokeAsync(
            TenantId(), ActorUserId(), IdempotencyKey(), command, ct));

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
