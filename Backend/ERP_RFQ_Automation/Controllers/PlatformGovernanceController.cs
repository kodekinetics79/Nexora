using System.Security.Claims;
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
    ReleaseSimulationService simulations,
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

    [HttpGet("connector-sdk")]
    [RequireModulePermission("Users", PermissionAction.View)]
    public ActionResult<ConnectorSdkContract> GetConnectorSdk() => Ok(new ConnectorSdkContract(
        "1.0",
        ["Microsoft365", "IMAP/SMTP", "Dynamics365", "SAP", "Oracle", "NetSuite",
            "Salesforce", "HubSpot", "REST", "Webhook", "CSV/SFTP", "ERPInventory",
            "ProcurementHandoff", "OperationalStatus"],
        ["OAuth2", "OIDC", "ApiKeyReference", "mTLSReference", "None"],
        ["CustomerUpsert", "ContactUpsert", "InventoryRead", "SupplierRead", "RfqExport",
            "SupplierRfqSend", "QuoteImport", "OrderExport", "ProcurementHandoff",
            "OperationalStatusRead"],
        ["connectorType", "contractVersion", "authMode", "credentialReference", "actions",
            "eventTriggers", "webhooks", "polling", "fieldMappings", "idempotency",
            "retryPolicy", "deadLetterPolicy", "rateLimit", "health", "sandbox"],
        ["Tenant-scoped idempotency", "Bounded retry", "Dead-letter retention",
            "Explicit replay", "Rate-limit compliance", "Immutable lifecycle audit"],
        "{eventId,tenantReference,eventType,contractVersion,occurredOn,payloadHash,payload}",
        "Persist secret or certificate references only; credentials are resolved from approved runtime configuration."));

    [HttpPost("test-suites/{testSuiteId:long}/simulate")]
    [RequireModulePermission("Users", PermissionAction.Edit)]
    public Task<ActionResult<ReleaseSimulationResult>> SimulateTestSuite(long testSuiteId,
        CancellationToken ct) => Execute(() => simulations.RunAsync(TenantId(), ActorUserId(),
            testSuiteId, IdempotencyKey(), ct));

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
