using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using ERP_RFQ_Automation.Platform.Auth;
using ERP_RFQ_Automation.Platform.Models;
using ERP_RFQ_Automation.Platform.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ERP_RFQ_Automation.Platform.Provisioning;

/// <summary>
/// Durable tenant provisioning: submit, watch, retry, cancel, and the drafts an operator saves
/// on the way there.
///
/// <para><b>Guarded exactly like <c>TenantsController</c>.</b> The default-deny
/// <see cref="PlatformPolicies.PlatformScope"/> sits at the class level so a tenant token — which
/// carries audience "RFQ" and scope "tenant", including an impersonation token — cannot reach any
/// of it; every mutation additionally requires <see cref="PlatformPolicies.TenantAdmin"/> (Owner
/// or SupportAdmin), which is the same authority the synchronous provisioning endpoint demands.
/// Creating a tenant is not a billing operation, so it deliberately does NOT sit behind the
/// Billing policy: a BillingAdmin decides what a customer is charged, not whether they exist.</para>
///
/// <para><b>Every privileged action is audited through <see cref="IPlatformAuditService"/></b>
/// with the actor, the target, the reason where one is demanded, the outcome, and the execution's
/// correlation id — so a submit, its retries and its eventual success or cancellation can be
/// pulled out of the audit log as one story rather than four unrelated rows.</para>
/// </summary>
[ApiController]
[Route("api/platform/provisioning")]
[Authorize(Policy = PlatformPolicies.PlatformScope)]
public class TenantProvisioningController : ControllerBase
{
    /// <summary>
    /// The conventional header name. Accepted alongside the body field so an HTTP client library
    /// that sets it automatically gets replay protection without the caller doing anything.
    /// </summary>
    public const string IdempotencyKeyHeader = "Idempotency-Key";

    private readonly ITenantProvisioningService _provisioning;
    private readonly IProvisioningDraftService _drafts;
    private readonly IPlatformAuditService _audit;
    private readonly ILogger<TenantProvisioningController> _logger;
    private readonly IProvisioningDiagnosticsService _diagnostics;

    public TenantProvisioningController(
        ITenantProvisioningService provisioning,
        IProvisioningDraftService drafts,
        IPlatformAuditService audit,
        ILogger<TenantProvisioningController> logger,
        IProvisioningDiagnosticsService diagnostics)
    {
        _provisioning = provisioning;
        _drafts = drafts;
        _audit = audit;
        _logger = logger;
        _diagnostics = diagnostics;
    }

    // ---- submit ---------------------------------------------------------------------------

    /// <summary>
    /// Accepts a provisioning request and returns immediately with an execution to watch.
    ///
    /// <para>202 rather than 201: nothing has been created yet when this returns. That is the
    /// point — the operator gets a progress list instead of a spinner, and a request that would
    /// previously have timed out ambiguously now has a durable record they can come back to.</para>
    /// </summary>
    [HttpPost("tenants")]
    [Authorize(Policy = PlatformPolicies.TenantAdmin)]
    public async Task<ActionResult<SubmitProvisioningResponse>> Submit(
        [FromBody] SubmitProvisioningRequest request, CancellationToken ct)
    {
        if (!ModelState.IsValid)
            return BadRequest(ModelState);

        // Sec9. This door and TenantsController.Provision take the SAME ProvisionTenantRequest, so
        // a commercial-authority rule enforced on one of them is a rule the other silently does not
        // have — which is why the check lives in PlatformCommercialAuthority rather than inline.
        var levers = PlatformCommercialAuthority.CommercialLeversIn(
            request.Tenant?.PlanId, request.Tenant?.BillingMode, request.Tenant?.BillingModeReason,
            request.Tenant?.RateCardId, request.Tenant?.BillingStartsOn, request.Tenant?.TrialEndsOn);
        if (levers.Count > 0 && !PlatformCommercialAuthority.HoldsCommercialAuthority(User))
            return StatusCode(StatusCodes.Status403Forbidden,
                new { error = PlatformCommercialAuthority.DescribeRefusal(levers) });

        // Same shape, same reason: this door is TenantAdmin and the profile decides which
        // production prerequisites may be deferred, which is Owner authority everywhere else.
        PlatformDeploymentProfileAuthority.Validate(
            request.Tenant?.DeploymentProfile, request.Tenant?.DeploymentProfileReason, User,
            out var profileRefusal, out var profileForbidden);
        if (profileRefusal is not null)
            return profileForbidden
                ? StatusCode(StatusCodes.Status403Forbidden, new { error = profileRefusal })
                : BadRequest(new { error = profileRefusal });

        // The header wins where both are present. A client library that sets it automatically is
        // the more reliable source than a field a caller had to remember to populate.
        var key = Request.Headers.TryGetValue(IdempotencyKeyHeader, out var header)
                  && !string.IsNullOrWhiteSpace(header.ToString())
            ? header.ToString().Trim()
            : request.IdempotencyKey;

        var actor = Actor();
        var result = await _provisioning.SubmitAsync(
            request.Tenant, key, actor, ActorPlatformUserId(), ct);

        switch (result.Outcome)
        {
            case ProvisioningSubmitOutcome.Invalid:
                await AuditAsync("provisioning.submit.rejected", targetId: null,
                    new { reason = result.Error, request.Tenant.Name },
                    PlatformAuditResults.Failure, ct);
                return BadRequest(new { error = result.Error });

            case ProvisioningSubmitOutcome.SlugRefused:
                await AuditAsync("provisioning.submit.rejected", targetId: null,
                    new { reason = result.Error, slugReason = result.SlugReason.ToString(), request.Tenant.Name },
                    PlatformAuditResults.Failure, ct);
                return BadRequest(new { error = result.Error, reason = result.SlugReason.ToString() });

            case ProvisioningSubmitOutcome.Conflict:
                await AuditAsync("provisioning.submit.rejected", result.Execution?.Id.ToString(),
                    new { reason = result.Error, request.Tenant.Name },
                    PlatformAuditResults.Failure, ct);
                return Conflict(new
                {
                    error = result.Error,
                    executionId = result.Execution?.Id
                });

            case ProvisioningSubmitOutcome.Replayed:
            {
                var replayed = result.Execution!;
                // Audited as a distinct action rather than as a second submit. A replay is a
                // client retrying, and recording it as a provisioning attempt would make the log
                // claim the operator asked for two tenants.
                await AuditAsync("provisioning.submit.replayed", replayed.Id.ToString(),
                    new { replayed.Slug, replayed.CorrelationId, state = replayed.State.ToString() },
                    PlatformAuditResults.Success, ct);

                return Ok(new SubmitProvisioningResponse
                {
                    Execution = ProvisioningProjection.ToDto(replayed),
                    Created = false,
                    SecretNotice =
                        "This request had already been accepted under the same idempotency key, so " +
                        "the original execution is returned unchanged. Any one-time credential or " +
                        "activation link was issued with the FIRST response and is not retrievable: " +
                        "only hashes are stored. Reissue the invitation if the link was lost."
                });
            }

            case ProvisioningSubmitOutcome.Created:
            {
                var created = result.Execution!;
                await AuditAsync("provisioning.submit", created.Id.ToString(),
                    new
                    {
                        created.Slug, created.Name, created.CorrelationId,
                        created.AdminEmail, created.AdminActivation,
                        // Recorded so a support engineer can see WHICH replay guard applied,
                        // without the key itself becoming a searchable secret.
                        idempotencyKeySupplied = key is not null,
                        passwordGenerated = result.GeneratedPassword is not null
                    },
                    PlatformAuditResults.Success, ct);

                if (request.FromDraftId is long draftId)
                    await _drafts.MarkSubmittedAsync(draftId, actor, created.Id, ct);

                // AcceptedAtAction, so the 202 carries a Location pointing at the poll endpoint.
                // A client that knows nothing about this API can follow the header rather than
                // having to construct the status URL from an id buried in the body.
                return AcceptedAtAction(nameof(Get), new { id = created.Id }, new SubmitProvisioningResponse
                {
                    Execution = ProvisioningProjection.ToDto(created),
                    Created = true,
                    GeneratedPassword = result.GeneratedPassword,
                    SecretNotice = created.AdminActivation == AdminActivationMethods.Invite
                        ? "The activation link is emailed to the administrator when the invitation " +
                          "step runs and is never returned by this API — the submit that could have " +
                          "carried it returns before the step exists. If the customer receives " +
                          "nothing, reissue the invitation from the tenant's invitations screen."
                        : result.GeneratedPassword is null
                            ? null
                            : "The generated password appears in THIS response and nowhere else, " +
                              "ever. Only a BCrypt hash is stored."
                });
            }

            default:
                _logger.LogError(
                    "Unhandled provisioning submit outcome {Outcome} for '{Name}'.",
                    result.Outcome, request.Tenant.Name);
                return StatusCode(500, new { error = "Provisioning could not be accepted." });
        }
    }

    // ---- watch ----------------------------------------------------------------------------

    /// <summary>The poll endpoint. Read-only, so PlatformScope is sufficient — a SupportAdmin
    /// watching a colleague's provision needs no mutation authority to do it.</summary>
    [HttpGet("executions/{id:long}")]
    public async Task<ActionResult<ProvisioningExecutionDto>> Get(long id, CancellationToken ct)
    {
        var execution = await _provisioning.GetAsync(id, ct);
        return execution is null ? NotFound() : Ok(ProvisioningProjection.ToDto(execution));
    }

    /// <summary>Recent executions, newest first. The console's "in flight" panel.</summary>
    [HttpGet("executions")]
    public async Task<ActionResult<IEnumerable<ProvisioningExecutionDto>>> List(
        [FromQuery] string? state, [FromQuery] int take = 50, CancellationToken ct = default)
    {
        ProvisioningExecutionState? wanted = null;
        if (!string.IsNullOrWhiteSpace(state))
        {
            if (!Enum.TryParse<ProvisioningExecutionState>(state, ignoreCase: true, out var parsed))
                return BadRequest(new
                {
                    error = $"state '{state}' is not recognised; use one of " +
                            $"{string.Join(", ", Enum.GetNames<ProvisioningExecutionState>())}."
                });
            wanted = parsed;
        }

        var executions = await _provisioning.ListAsync(wanted, take, ct);
        return Ok(executions.Select(ProvisioningProjection.ToDto));
    }

    // ---- diagnose -------------------------------------------------------------------------

    /// <summary>
    /// Why a tenant is not finished, in the four terms somebody can act on: what failed, whose
    /// problem it is, what is missing, and what may safely be pressed.
    ///
    /// <para><b>Read-only, and therefore only PlatformScope.</b> Diagnosing a broken tenant must be
    /// available to whoever is on call, from a session with no mutation authority, without a reason
    /// prompt — otherwise the first thing somebody does under pressure is the thing that changes
    /// state. Every mutation this screen leads to keeps its own TenantAdmin gate.</para>
    /// </summary>
    [HttpGet("tenants/{tenantId:long}/diagnostics")]
    public async Task<ActionResult<TenantProvisioningDiagnostics>> Diagnose(
        long tenantId, CancellationToken ct)
    {
        var diagnostics = await _diagnostics.ForTenantAsync(tenantId, ct);
        return diagnostics is null ? NotFound() : Ok(diagnostics);
    }

    /// <summary>
    /// The same read model keyed by execution, so an attempt that failed BEFORE it created a
    /// tenant row is still diagnosable. That case has no tenant page to open, and it is exactly
    /// the case the old "Provisioning failed." toast was most useless for.
    /// </summary>
    [HttpGet("executions/{id:long}/diagnostics")]
    public async Task<ActionResult<TenantProvisioningDiagnostics>> DiagnoseExecution(
        long id, CancellationToken ct)
    {
        var diagnostics = await _diagnostics.ForExecutionAsync(id, ct);
        return diagnostics is null ? NotFound() : Ok(diagnostics);
    }

    // ---- repair ---------------------------------------------------------------------------

    /// <summary>
    /// Puts a failed execution back in the queue, optionally rewinding one step.
    ///
    /// <para>A reason is required and audited. A retry re-executes privileged writes against a
    /// customer's data, and "somebody clicked it again" is not an acceptable answer three months
    /// later when the customer asks why they have two of something.</para>
    /// </summary>
    [HttpPost("executions/{id:long}/retry")]
    [Authorize(Policy = PlatformPolicies.TenantAdmin)]
    public async Task<ActionResult<ProvisioningExecutionDto>> Retry(
        long id, [FromBody] RetryProvisioningRequest request, CancellationToken ct)
    {
        if (!ModelState.IsValid)
            return BadRequest(ModelState);

        var result = await _provisioning.RetryAsync(id, request.Step, Actor(), ct);

        if (result.Outcome != ProvisioningCommandOutcome.Accepted)
            await AuditAsync("provisioning.retry", id.ToString(),
                new { reason = request.Reason, request.Step, refusal = result.Error },
                PlatformAuditResults.Failure, ct);

        switch (result.Outcome)
        {
            case ProvisioningCommandOutcome.NotFound:
                return NotFound();
            case ProvisioningCommandOutcome.UnknownStep:
                return BadRequest(new { error = result.Error });
            case ProvisioningCommandOutcome.InvalidState:
            case ProvisioningCommandOutcome.Busy:
                return Conflict(new { error = result.Error });
        }

        var execution = result.Execution!;
        await AuditAsync("provisioning.retry", id.ToString(),
            new
            {
                reason = request.Reason, step = request.Step, execution.CorrelationId,
                execution.Slug, execution.TenantId
            },
            PlatformAuditResults.Success, ct);

        return Ok(ProvisioningProjection.ToDto(execution));
    }

    /// <summary>
    /// Abandons an attempt.
    ///
    /// <para><b>Cancellation does not undo committed work, and the response says so.</b> A tenant
    /// and a business unit created by earlier steps remain, in <c>Provisioning</c> status, with
    /// their ids on the execution record. Deleting a partially built tenant is a destructive
    /// operation with its own review and its own endpoint; quietly doing it under a "cancel"
    /// button would be the most dangerous thing on this controller.</para>
    /// </summary>
    [HttpPost("executions/{id:long}/cancel")]
    [Authorize(Policy = PlatformPolicies.TenantAdmin)]
    public async Task<ActionResult<ProvisioningExecutionDto>> Cancel(
        long id, [FromBody] CancelProvisioningRequest request, CancellationToken ct)
    {
        if (!ModelState.IsValid)
            return BadRequest(ModelState);

        var result = await _provisioning.CancelAsync(id, request.Reason, Actor(), ct);

        if (result.Outcome != ProvisioningCommandOutcome.Accepted)
        {
            await AuditAsync("provisioning.cancel", id.ToString(),
                new { reason = request.Reason, refusal = result.Error },
                PlatformAuditResults.Failure, ct);

            return result.Outcome switch
            {
                ProvisioningCommandOutcome.NotFound => NotFound(),
                _ => Conflict(new { error = result.Error })
            };
        }

        var execution = result.Execution!;
        await AuditAsync("provisioning.cancel", id.ToString(),
            new
            {
                reason = request.Reason, execution.CorrelationId, execution.Slug,
                // The ids are the whole point of auditing a cancellation: they name what was
                // left behind, so nobody has to reconstruct it from a step list later.
                orphanedTenantId = execution.TenantId,
                orphanedBusinessUnitId = execution.ProvisionedBusinessUnitId,
                orphanedUserId = execution.FoundingUserId
            },
            PlatformAuditResults.Success, ct);

        return Ok(ProvisioningProjection.ToDto(execution));
    }

    // ---- the wizard's address check ---------------------------------------------------------

    /// <summary>
    /// Whether a workspace address may be used, and why not when it may not. Called as the
    /// operator types, which is the only moment changing it is free — after provisioning the
    /// address is permanent on the tenant's business unit code and on every commercial-case
    /// reference derived from it.
    /// </summary>
    [HttpGet("slug-check")]
    public async Task<ActionResult<SlugAvailabilityDto>> CheckSlug(
        [FromQuery] string? slug, [FromQuery] string? name, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(slug) && string.IsNullOrWhiteSpace(name))
            return BadRequest(new { error = "Supply either a slug or a name to derive one from." });

        return Ok(await _provisioning.CheckSlugAsync(slug, name, ct));
    }

    /// <summary>
    /// The full reserved list, so the console can explain a refusal without a round trip and a
    /// test can hold it against the live route table.
    /// </summary>
    [HttpGet("reserved-slugs")]
    public ActionResult<IEnumerable<string>> ReservedSlugs()
        => Ok(ReservedTenantSlugs.All.OrderBy(slug => slug, StringComparer.Ordinal));

    // ---- drafts ---------------------------------------------------------------------------

    [HttpGet("drafts")]
    [Authorize(Policy = PlatformPolicies.TenantAdmin)]
    public async Task<ActionResult<IEnumerable<ProvisioningDraftDto>>> ListDrafts(
        [FromQuery] bool includeSubmitted = false, CancellationToken ct = default)
    {
        var drafts = await _drafts.ListAsync(Actor(), includeSubmitted, ct);
        return Ok(drafts.Select(draft => ProvisioningProjection.ToDto(draft, includePayload: false)));
    }

    [HttpGet("drafts/{id:long}")]
    [Authorize(Policy = PlatformPolicies.TenantAdmin)]
    public async Task<ActionResult<ProvisioningDraftDto>> GetDraft(long id, CancellationToken ct)
    {
        var draft = await _drafts.GetAsync(id, Actor(), ct);
        // 404 rather than 403 for somebody else's draft: a 403 would confirm the id exists, and
        // the id space would become a directory of who is being onboarded.
        return draft is null ? NotFound() : Ok(ProvisioningProjection.ToDto(draft, includePayload: true));
    }

    [HttpPost("drafts")]
    [Authorize(Policy = PlatformPolicies.TenantAdmin)]
    public async Task<ActionResult<ProvisioningDraftDto>> CreateDraft(
        [FromBody] SaveProvisioningDraftRequest request, CancellationToken ct)
    {
        if (!ModelState.IsValid)
            return BadRequest(ModelState);

        var result = await _drafts.CreateAsync(request, Actor(), ActorPlatformUserId(), ct);
        if (result.Outcome == ProvisioningDraftOutcome.Rejected)
            return BadRequest(new { error = result.Error });

        var draft = result.Draft!;
        await AuditAsync("provisioning.draft.create", draft.Id.ToString(),
            new { draft.Name }, PlatformAuditResults.Success, ct);

        return CreatedAtAction(nameof(GetDraft), new { id = draft.Id },
            ProvisioningProjection.ToDto(draft, includePayload: true));
    }

    [HttpPut("drafts/{id:long}")]
    [Authorize(Policy = PlatformPolicies.TenantAdmin)]
    public async Task<ActionResult<ProvisioningDraftDto>> UpdateDraft(
        long id, [FromBody] SaveProvisioningDraftRequest request, CancellationToken ct)
    {
        if (!ModelState.IsValid)
            return BadRequest(ModelState);

        var result = await _drafts.UpdateAsync(id, request, Actor(), ct);
        return result.Outcome switch
        {
            ProvisioningDraftOutcome.NotFound => NotFound(),
            ProvisioningDraftOutcome.Rejected => BadRequest(new { error = result.Error }),
            ProvisioningDraftOutcome.VersionConflict => Conflict(new
            {
                error = result.Error,
                currentVersion = result.Draft?.Version
            }),
            _ => Ok(ProvisioningProjection.ToDto(result.Draft!, includePayload: true))
        };
    }

    [HttpDelete("drafts/{id:long}")]
    [Authorize(Policy = PlatformPolicies.TenantAdmin)]
    public async Task<IActionResult> DeleteDraft(long id, CancellationToken ct)
    {
        if (!await _drafts.DeleteAsync(id, Actor(), ct))
            return NotFound();

        await AuditAsync("provisioning.draft.delete", id.ToString(), null,
            PlatformAuditResults.Success, ct);
        return NoContent();
    }

    // ---- helpers ---------------------------------------------------------------------------

    private string Actor() => User.FindFirst("email")?.Value ?? "platform";

    /// <summary>
    /// The acting platform user's id, resolved the same way <c>PlatformAuditService</c> resolves
    /// it. Captured at submit and carried on the execution so the runner — which has no
    /// <c>HttpContext</c> and no principal — can still attribute the tenant's creation to the
    /// human who asked for it rather than to a background process.
    /// </summary>
    private long? ActorPlatformUserId()
    {
        var sub = User.FindFirst(JwtRegisteredClaimNames.Sub)?.Value
                  ?? User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        return long.TryParse(sub, out var parsed) && parsed > 0 ? parsed : null;
    }

    private Task AuditAsync(
        string action, string? targetId, object? metadata, string result, CancellationToken ct)
        => _audit.WriteAsync(User, action, nameof(ProvisioningExecution), targetId, metadata,
            actAsTenantId: null, httpContext: HttpContext, result: result, ct: ct);
}
