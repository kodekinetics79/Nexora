using System.Security.Claims;
using System.Text;
using System.Text.Json;
using ERP_RFQ_Automation.Agent;
using ERP_RFQ_Automation.Agent.Guardrails;
using ERP_RFQ_Automation.Agent.Models;
using ERP_RFQ_Automation.Authorization;
using ERP_RFQ_Automation.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.Controllers;

/// <summary>
/// HTTP surface for the sourcing-copilot engine. Every endpoint is [Authorize] and
/// tenant-scoped from the <c>businessUnitId</c> JWT claim. Mutations only ever run
/// through the orchestrator/guardrail path.
/// </summary>
[ApiController]
[Route("api/agent")]
[Authorize]
[ERP_RFQ_Automation.Platform.Entitlements.RequiresEntitlement(ERP_RFQ_Automation.Platform.Entitlements.TypedEntitlementCatalog.Ai)]
public sealed class AgentController : ControllerBase
{
    private static readonly JsonSerializerOptions SseJson = new() { WriteIndented = false };

    private readonly IAgentOrchestrator _orchestrator;
    private readonly IAgentToolRegistry _tools;
    private readonly IAgentGuardrail _guardrail;
    private readonly ErpRfqAutomationContext _db;
    private readonly IAuthorizationService _authorization;

    public AgentController(
        IAgentOrchestrator orchestrator,
        IAgentToolRegistry tools,
        IAgentGuardrail guardrail,
        ErpRfqAutomationContext db,
        IAuthorizationService authorization)
    {
        _orchestrator = orchestrator;
        _tools = tools;
        _guardrail = guardrail;
        _db = db;
        _authorization = authorization;
    }

    public sealed class ChatRequest
    {
        public string? SessionId { get; set; }
        public string Message { get; set; } = string.Empty;
    }

    // -------- POST /api/agent/chat (SSE) --------
    [HttpPost("chat")]
    public async Task Chat([FromBody] ChatRequest body, CancellationToken ct)
    {
        var ctx = BuildContext();
        if (ctx is null) { Response.StatusCode = StatusCodes.Status401Unauthorized; return; }
        if (string.IsNullOrWhiteSpace(body?.Message))
        {
            Response.StatusCode = StatusCodes.Status400BadRequest;
            await Response.WriteAsync("{\"error\":\"message is required.\"}", ct);
            return;
        }

        Guid? sessionId = Guid.TryParse(body.SessionId, out var sid) ? sid : null;

        Response.ContentType = "text/event-stream";
        Response.Headers["Cache-Control"] = "no-cache";
        Response.Headers["X-Accel-Buffering"] = "no";
        HttpContext.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();

        try
        {
            await foreach (var ev in _orchestrator.RunAsync(sessionId, body.Message, ctx, ct))
                await WriteSseAsync(MapEvent(ev), ct);
        }
        catch (OperationCanceledException)
        {
            // client disconnected — nothing to do
        }
        catch (Exception)
        {
            await WriteSseAsync(new { type = "error", message = "The assistant encountered an error." }, ct);
        }
    }

    // -------- GET /api/agent/sessions --------
    /// <summary>
    /// The caller's own conversations. A transcript is personal data — the questions a user
    /// asked and every row the agent returned to them — so it is scoped by the token's user id
    /// exactly as ListViewPreferencesController.cs:9-21 scopes a layout, never by a parameter.
    /// <paramref name="all"/> widens to the whole tenant and is manager/admin only.
    /// </summary>
    [HttpGet("sessions")]
    public async Task<IActionResult> GetSessions([FromQuery] bool all = false, CancellationToken ct = default)
    {
        var ctx = BuildContext();
        if (ctx is null) return Unauthorized();

        var query = _db.Set<AgentSession>().AsNoTracking();
        if (all)
        {
            if (!await IsManagerAsync()) return Forbid();
        }
        else
        {
            // A token with no resolvable user owns no transcript, so it sees none.
            if (ctx.UserId is null) return Forbid();
            query = query.Where(s => s.CreatedByUserId == ctx.UserId);
        }

        var sessions = await query
            .OrderByDescending(s => s.UpdatedOn)
            .Select(s => new
            {
                id = s.Id,
                title = s.Title,
                updatedOn = s.UpdatedOn,
                createdByUserId = s.CreatedByUserId,
                createdBy = s.CreatedByName,
                messageCount = _db.Set<AgentMessage>().Count(m => m.SessionId == s.Id)
            })
            .ToListAsync(ct);

        return Ok(sessions);
    }

    // -------- GET /api/agent/sessions/{id} --------
    /// <summary>
    /// One transcript, including tool inputs and results. Owner-only; a manager/admin may read
    /// another user's for supervision. A session belonging to someone else answers 404 rather
    /// than 403 so the endpoint cannot be walked to learn which session ids exist. A session
    /// with no recorded owner is readable only by a manager — unattributable is not public.
    /// </summary>
    [HttpGet("sessions/{id:guid}")]
    public async Task<IActionResult> GetSession(Guid id, CancellationToken ct)
    {
        var ctx = BuildContext();
        if (ctx is null) return Unauthorized();

        var session = await _db.Set<AgentSession>().AsNoTracking().FirstOrDefaultAsync(s => s.Id == id, ct);
        if (session is null) return NotFound();

        var isOwner = ctx.UserId is not null && session.CreatedByUserId == ctx.UserId;
        if (!isOwner && !await IsManagerAsync()) return NotFound();

        var messages = await _db.Set<AgentMessage>().AsNoTracking()
            .Where(m => m.SessionId == id)
            .OrderBy(m => m.Sequence)
            .Select(m => new
            {
                role = m.Role.ToString().ToLowerInvariant(),
                content = m.Content,
                toolName = m.ToolName,
                toolResultSummary = m.ToolResult,
                createdOn = m.CreatedOn
            })
            .ToListAsync(ct);

        return Ok(new { id = session.Id, title = session.Title, messages });
    }

    // -------- GET /api/agent/approvals?status=pending --------
    /// <summary>
    /// A manager/admin sees the tenant's whole queue — deciding it is their job, and
    /// approve/reject below are gated to them. Everyone else sees only the approvals their own
    /// chat sessions raised, so the queue is not a listing of what colleagues asked the agent
    /// to do.
    /// </summary>
    [HttpGet("approvals")]
    public async Task<IActionResult> GetApprovals([FromQuery] string status = "pending", CancellationToken ct = default)
    {
        var ctx = BuildContext();
        if (ctx is null) return Unauthorized();

        var query = _db.Set<AgentApproval>().AsNoTracking();
        if (!await IsManagerAsync())
        {
            if (ctx.UserId is null) return Forbid();
            query = query.Where(a => a.RequestedByUserId == ctx.UserId);
        }
        if (!string.IsNullOrWhiteSpace(status) && Enum.TryParse<AgentApprovalStatus>(status, ignoreCase: true, out var st))
            query = query.Where(a => a.Status == st);

        var rows = await query
            .OrderByDescending(a => a.CreatedOn)
            .Select(a => new
            {
                id = a.Id,
                toolName = a.ToolName,
                summary = a.Summary,
                status = a.Status.ToString().ToLowerInvariant(),
                requestedOn = a.CreatedOn
            })
            .ToListAsync(ct);

        return Ok(rows);
    }

    // -------- POST /api/agent/approvals/{id}/approve --------
    // Deciding a guarded mutation is a supervisory action: manager/admin only.
    //
    // Manager rank alone is not separation of duties. A manager whose OWN chat session asked
    // the agent to award an RFQ could previously approve that same request, which makes the
    // approval a formality the requester performs on themselves — the human in "human in the
    // loop" has to be a second human. Procurement/ProcurementApplicationService.cs:1487-1497
    // is the precedent: it refuses to approve a purchase order whose award the approver
    // themselves made, and refuses outright when the requester is unrecorded, because a
    // control that cannot be verified has not been satisfied.
    [HttpPost("approvals/{id:guid}/approve")]
    [RequireManagerRole]
    public async Task<IActionResult> Approve(Guid id, CancellationToken ct)
    {
        var ctx = BuildContext();
        if (ctx is null) return Unauthorized();

        var approval = await _db.Set<AgentApproval>().FirstOrDefaultAsync(a => a.Id == id, ct);
        if (approval is null) return NotFound();
        if (approval.Status != AgentApprovalStatus.Pending)
            return Conflict(new { id = approval.Id, status = approval.Status.ToString().ToLowerInvariant(), message = "Approval is not pending." });

        if (ctx.UserId is null || approval.RequestedByUserId is null)
            return UnprocessableEntity(new
            {
                id = approval.Id,
                status = approval.Status.ToString().ToLowerInvariant(),
                message = "Segregation of duties cannot be verified: this request does not record the user " +
                          "whose session raised it, or the caller has no resolvable user id. " +
                          "Reject it and ask for it to be raised again."
            });

        if (approval.RequestedByUserId == ctx.UserId)
            return Conflict(new
            {
                id = approval.Id,
                status = approval.Status.ToString().ToLowerInvariant(),
                message = "Segregation of duties: the user whose session requested this action cannot approve it. " +
                          "Another manager must decide it."
            });

        var tool = _tools.Find(approval.ToolName);
        if (tool is null)
        {
            approval.Status = AgentApprovalStatus.Failed;
            approval.ResultJson = JsonSerializer.Serialize(new { error = $"Tool '{approval.ToolName}' is no longer registered." });
            Decide(approval, ctx);
            await _db.SaveChangesAsync(ct);
            return UnprocessableEntity(new { id = approval.Id, status = "failed", resultSummary = "Tool no longer available." });
        }

        JsonElement input;
        using (var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(approval.InputJson) ? "{}" : approval.InputJson))
            input = doc.RootElement.Clone();

        var outcome = await _orchestrator.ExecuteApprovedAsync(tool, input, ctx, ct);

        approval.Status = outcome.Ok ? AgentApprovalStatus.Executed : AgentApprovalStatus.Failed;
        approval.ResultJson = outcome.ResultJson;
        Decide(approval, ctx);
        await _db.SaveChangesAsync(ct);

        return Ok(new { id = approval.Id, status = approval.Status.ToString().ToLowerInvariant(), resultSummary = outcome.Summary });
    }

    // -------- POST /api/agent/approvals/{id}/reject --------
    // Same supervisory boundary as approve, deliberately WITHOUT the requester/approver
    // separation applied above: rejecting executes nothing and only ever removes authority, so
    // a manager who realises their own session asked for something wrong must be able to
    // withdraw it immediately rather than having to find a colleague first.
    [HttpPost("approvals/{id:guid}/reject")]
    [RequireManagerRole]
    public async Task<IActionResult> Reject(Guid id, CancellationToken ct)
    {
        var ctx = BuildContext();
        if (ctx is null) return Unauthorized();

        var approval = await _db.Set<AgentApproval>().FirstOrDefaultAsync(a => a.Id == id, ct);
        if (approval is null) return NotFound();
        if (approval.Status != AgentApprovalStatus.Pending)
            return Conflict(new { id = approval.Id, status = approval.Status.ToString().ToLowerInvariant(), message = "Approval is not pending." });

        approval.Status = AgentApprovalStatus.Rejected;
        Decide(approval, ctx);

        _db.Set<AgentAuditLog>().Add(new AgentAuditLog
        {
            BusinessUnitId = ctx.BusinessUnitId,
            Actor = ctx.UserName ?? (ctx.UserId?.ToString() ?? "user"),
            ToolName = approval.ToolName,
            Decision = "Rejected",
            InputJson = approval.InputJson,
            ResultSummary = "Rejected by human reviewer.",
            CreatedOn = DateTime.UtcNow
        });

        await _db.SaveChangesAsync(ct);
        return Ok(new { id = approval.Id, status = "rejected", resultSummary = "Rejected." });
    }

    // -------- GET /api/agent/audit?take=100 --------
    /// <summary>
    /// The guardrail decision trail. Reading the whole tenant's — every tool call every
    /// colleague made, with the actor named — is supervisory, so it is manager/admin only.
    /// Everyone else gets their own decisions, which is what the Activity screen claims to
    /// show ("what I've been doing for you") and all it ever needed.
    ///
    /// <para>The predicate matches <c>AgentAuditLog.Actor</c>, which
    /// <c>AgentOrchestrator.AuditAsync</c> writes as the email claim, falling back to the user
    /// id. That is a free-text identity, not a foreign key: a caller whose identity strings
    /// match no row sees an empty list rather than someone else's. SCHEMA DELTA OWED —
    /// <c>AgentAuditLog</c> should carry <c>ActorUserId bigint NULL</c> so this predicate is a
    /// key rather than a string; reported, not migrated here.</para>
    /// </summary>
    [HttpGet("audit")]
    public async Task<IActionResult> GetAudit([FromQuery] int take = 100, CancellationToken ct = default)
    {
        var ctx = BuildContext();
        if (ctx is null) return Unauthorized();

        var query = _db.Set<AgentAuditLog>().AsNoTracking();
        if (!await IsManagerAsync())
        {
            var identities = new[] { ctx.UserName, ctx.UserId?.ToString() }
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Select(x => x!)
                .ToList();
            if (identities.Count == 0) return Forbid();
            query = query.Where(a => identities.Contains(a.Actor));
        }

        take = Math.Clamp(take, 1, 500);
        var rows = await query
            .OrderByDescending(a => a.CreatedOn)
            .Take(take)
            .Select(a => new
            {
                id = a.Id,
                actor = a.Actor,
                toolName = a.ToolName,
                decision = a.Decision,
                summary = a.ResultSummary,
                createdOn = a.CreatedOn
            })
            .ToListAsync(ct);

        return Ok(rows);
    }

    // -------- GET /api/agent/policy --------
    [HttpGet("policy")]
    public async Task<IActionResult> GetPolicy(CancellationToken ct)
    {
        var ctx = BuildContext();
        if (ctx is null) return Unauthorized();

        var policy = await _guardrail.GetPolicyAsync(ctx.BusinessUnitId, ct);
        return Ok(ToPolicyDto(policy));
    }

    public sealed class PolicyUpdateDto
    {
        public string? AutonomyLevel { get; set; }

        /// <summary>
        /// The currency the two caps below are expressed in. Until this is set, the caps denote
        /// no definite amount and the guardrail routes every amount-bearing action to a human —
        /// so this is the field a tenant sets to restore agent auto-approval.
        /// </summary>
        public long? CurrencyId { get; set; }

        public decimal? MaxAutoAwardValue { get; set; }
        public decimal? MaxAutoOrderValue { get; set; }
        public bool? RequireApprovalForAwards { get; set; }
        public bool? RequireApprovalForOrders { get; set; }
        public bool? RequireApprovalForSupplierEmails { get; set; }
        public string? PerToolOverrides { get; set; }
    }

    // -------- PUT /api/agent/policy --------
    // Changing autonomy levels / approval thresholds is manager/admin only (GET stays open).
    [HttpPut("policy")]
    [RequireManagerRole]
    public async Task<IActionResult> PutPolicy([FromBody] PolicyUpdateDto dto, CancellationToken ct)
    {
        var ctx = BuildContext();
        if (ctx is null) return Unauthorized();

        var policy = await _db.Set<AgentPolicy>().FirstOrDefaultAsync(p => p.BusinessUnitId == ctx.BusinessUnitId, ct);
        var isNew = policy is null;
        if (policy is null)
        {
            policy = AgentPolicy.Default(ctx.BusinessUnitId);
            policy.CreatedOn = DateTime.UtcNow;
        }

        if (!string.IsNullOrWhiteSpace(dto.AutonomyLevel) &&
            Enum.TryParse<AgentAutonomyLevel>(dto.AutonomyLevel, ignoreCase: true, out var lvl))
            policy.AutonomyLevel = lvl;

        if (dto.CurrencyId.HasValue)
        {
            // Validated against this tenant's own currencies before it is stored. The composite
            // FK would reject a foreign id at the database anyway; catching it here turns a 500
            // into a sentence the operator setting a spend cap can act on.
            var currencyExists = await _db.Set<Currency>()
                .AnyAsync(c => c.Id == dto.CurrencyId.Value && c.BusinessUnitId == ctx.BusinessUnitId && c.IsActive == true, ct);
            if (!currencyExists)
                return BadRequest(new { error = $"Currency {dto.CurrencyId.Value} is not an active currency of this business unit; the agent spend cap currency was not changed." });
            policy.CurrencyId = dto.CurrencyId.Value;
        }

        if (dto.MaxAutoAwardValue.HasValue) policy.MaxAutoAwardValue = dto.MaxAutoAwardValue.Value;
        if (dto.MaxAutoOrderValue.HasValue) policy.MaxAutoOrderValue = dto.MaxAutoOrderValue.Value;
        if (dto.RequireApprovalForAwards.HasValue) policy.RequireApprovalForAwards = dto.RequireApprovalForAwards.Value;
        if (dto.RequireApprovalForOrders.HasValue) policy.RequireApprovalForOrders = dto.RequireApprovalForOrders.Value;
        if (dto.RequireApprovalForSupplierEmails.HasValue) policy.RequireApprovalForSupplierEmails = dto.RequireApprovalForSupplierEmails.Value;

        if (dto.PerToolOverrides is not null)
        {
            if (string.IsNullOrWhiteSpace(dto.PerToolOverrides))
            {
                policy.PerToolOverrides = null;
            }
            else
            {
                // The guardrail treats anything it cannot parse as "no override", so an
                // unvalidated write stores a control that silently does nothing. Rejected here
                // instead, with the reason, so a manager who mistypes a tool name is told.
                var error = ValidateOverrides(dto.PerToolOverrides);
                if (error is not null) return BadRequest(new { error });
                policy.PerToolOverrides = dto.PerToolOverrides;
            }
        }

        policy.UpdatedOn = DateTime.UtcNow;

        if (isNew) _db.Set<AgentPolicy>().Add(policy);
        else _db.Set<AgentPolicy>().Update(policy);
        await _db.SaveChangesAsync(ct);

        return Ok(ToPolicyDto(policy));
    }

    // ---------------- helpers ----------------

    /// <summary>
    /// Null when the overrides document is usable; otherwise the sentence to return. Only the
    /// three verbs the guardrail understands are accepted, and only for tools that exist —
    /// "allow" on a misspelled tool name would otherwise read as a granted exception while
    /// granting nothing.
    /// </summary>
    private string? ValidateOverrides(string json)
    {
        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); }
        catch (JsonException) { return "perToolOverrides must be a JSON object mapping tool name to \"allow\", \"require_approval\" or \"deny\"."; }

        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return "perToolOverrides must be a JSON object mapping tool name to \"allow\", \"require_approval\" or \"deny\".";

            foreach (var property in doc.RootElement.EnumerateObject())
            {
                if (_tools.Find(property.Name) is null)
                    return $"perToolOverrides names '{property.Name}', which is not a registered agent tool.";

                var verb = property.Value.ValueKind == JsonValueKind.String
                    ? AgentGuardrail.ReadOverrideVerb(json, property.Name)
                    : null;
                if (verb is null)
                    return $"perToolOverrides value for '{property.Name}' must be \"allow\", \"require_approval\" or \"deny\".";
            }
        }

        return null;
    }

    /// <summary>
    /// The caller holds the same manager/admin authority <c>[RequireManagerRole]</c> enforces.
    /// Asks the real policy rather than re-reading role rows — same approach as
    /// CustomFieldsController.cs:185-187.
    /// </summary>
    private async Task<bool> IsManagerAsync() =>
        (await _authorization.AuthorizeAsync(User, null, RequireManagerRoleAttribute.PolicyName)).Succeeded;

    private static object ToPolicyDto(AgentPolicy p) => new
    {
        businessUnitId = p.BusinessUnitId,
        autonomyLevel = p.AutonomyLevel.ToString(),
        currencyId = p.CurrencyId,
        // Null tells the UI the caps are undenominated and auto-approval is therefore suspended.
        capsAreDenominated = p.CurrencyId is not null,
        maxAutoAwardValue = p.MaxAutoAwardValue,
        maxAutoOrderValue = p.MaxAutoOrderValue,
        requireApprovalForAwards = p.RequireApprovalForAwards,
        requireApprovalForOrders = p.RequireApprovalForOrders,
        requireApprovalForSupplierEmails = p.RequireApprovalForSupplierEmails,
        perToolOverrides = p.PerToolOverrides
    };

    private void Decide(AgentApproval approval, AgentToolContext ctx)
    {
        approval.DecidedByUserId = ctx.UserId;
        approval.DecidedBy = ctx.UserName;
        approval.DecidedOn = DateTime.UtcNow;
        approval.UpdatedOn = DateTime.UtcNow;
    }

    /// <summary>
    /// The acting identity, entirely from the token. Carries the role and the principal as
    /// well as the tenant, because the orchestrator authorizes every tool against the same
    /// module policies an HTTP route would — without these two the copilot was a way round
    /// module RBAC entirely.
    /// </summary>
    private AgentToolContext? BuildContext()
    {
        var buRaw = User.FindFirst("businessUnitId")?.Value;
        if (!long.TryParse(buRaw, out var bu) || bu <= 0) return null;

        long? userId = long.TryParse(
            User.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? User.FindFirst("sub")?.Value, out var uid) ? uid : null;
        var userName = User.FindFirst(ClaimTypes.Email)?.Value ?? User.FindFirst("email")?.Value;
        long? roleId = long.TryParse(User.FindFirst("roleId")?.Value, out var rid) ? rid : null;

        return new AgentToolContext
        {
            BusinessUnitId = bu,
            UserId = userId,
            UserName = userName,
            RoleId = roleId,
            Principal = User
        };
    }

    private static object MapEvent(AgentStreamEvent ev) => ev.Type switch
    {
        AgentStreamEventType.Session => new { type = "session", sessionId = ev.SessionId },
        AgentStreamEventType.Token => new { type = "token", text = ev.Text },
        AgentStreamEventType.ToolCall => new { type = "tool_call", name = ev.ToolName, input = ev.Input },
        AgentStreamEventType.ToolResult => new { type = "tool_result", name = ev.ToolName, ok = ev.Ok ?? false, summary = ev.Summary },
        AgentStreamEventType.ApprovalRequired => new { type = "approval_required", approvalId = ev.ApprovalId, toolName = ev.ToolName, summary = ev.Summary },
        AgentStreamEventType.Done => new { type = "done", messageId = ev.MessageId },
        AgentStreamEventType.Error => new { type = "error", message = ev.Message },
        _ => new { type = "error", message = "unknown event" }
    };

    private async Task WriteSseAsync(object payload, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(payload, SseJson);
        await Response.WriteAsync($"data: {json}\n\n", Encoding.UTF8, ct);
        await Response.Body.FlushAsync(ct);
    }
}
