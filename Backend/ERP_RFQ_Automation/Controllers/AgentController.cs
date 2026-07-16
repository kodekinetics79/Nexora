using System.Security.Claims;
using System.Text;
using System.Text.Json;
using ERP_RFQ_Automation.Agent;
using ERP_RFQ_Automation.Agent.Guardrails;
using ERP_RFQ_Automation.Agent.Models;
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
public sealed class AgentController : ControllerBase
{
    private static readonly JsonSerializerOptions SseJson = new() { WriteIndented = false };

    private readonly IAgentOrchestrator _orchestrator;
    private readonly IAgentToolRegistry _tools;
    private readonly IAgentGuardrail _guardrail;
    private readonly ErpRfqAutomationContext _db;

    public AgentController(
        IAgentOrchestrator orchestrator,
        IAgentToolRegistry tools,
        IAgentGuardrail guardrail,
        ErpRfqAutomationContext db)
    {
        _orchestrator = orchestrator;
        _tools = tools;
        _guardrail = guardrail;
        _db = db;
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
    [HttpGet("sessions")]
    public async Task<IActionResult> GetSessions(CancellationToken ct)
    {
        var ctx = BuildContext();
        if (ctx is null) return Unauthorized();

        var sessions = await _db.Set<AgentSession>().AsNoTracking()
            .OrderByDescending(s => s.UpdatedOn)
            .Select(s => new
            {
                id = s.Id,
                title = s.Title,
                updatedOn = s.UpdatedOn,
                messageCount = _db.Set<AgentMessage>().Count(m => m.SessionId == s.Id)
            })
            .ToListAsync(ct);

        return Ok(sessions);
    }

    // -------- GET /api/agent/sessions/{id} --------
    [HttpGet("sessions/{id:guid}")]
    public async Task<IActionResult> GetSession(Guid id, CancellationToken ct)
    {
        var ctx = BuildContext();
        if (ctx is null) return Unauthorized();

        var session = await _db.Set<AgentSession>().AsNoTracking().FirstOrDefaultAsync(s => s.Id == id, ct);
        if (session is null) return NotFound();

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
    [HttpGet("approvals")]
    public async Task<IActionResult> GetApprovals([FromQuery] string status = "pending", CancellationToken ct = default)
    {
        var ctx = BuildContext();
        if (ctx is null) return Unauthorized();

        var query = _db.Set<AgentApproval>().AsNoTracking();
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
    [HttpPost("approvals/{id:guid}/approve")]
    public async Task<IActionResult> Approve(Guid id, CancellationToken ct)
    {
        var ctx = BuildContext();
        if (ctx is null) return Unauthorized();

        var approval = await _db.Set<AgentApproval>().FirstOrDefaultAsync(a => a.Id == id, ct);
        if (approval is null) return NotFound();
        if (approval.Status != AgentApprovalStatus.Pending)
            return Conflict(new { id = approval.Id, status = approval.Status.ToString().ToLowerInvariant(), message = "Approval is not pending." });

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
    [HttpPost("approvals/{id:guid}/reject")]
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
    [HttpGet("audit")]
    public async Task<IActionResult> GetAudit([FromQuery] int take = 100, CancellationToken ct = default)
    {
        var ctx = BuildContext();
        if (ctx is null) return Unauthorized();

        take = Math.Clamp(take, 1, 500);
        var rows = await _db.Set<AgentAuditLog>().AsNoTracking()
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
        public decimal? MaxAutoAwardValue { get; set; }
        public decimal? MaxAutoOrderValue { get; set; }
        public bool? RequireApprovalForAwards { get; set; }
        public bool? RequireApprovalForOrders { get; set; }
        public bool? RequireApprovalForSupplierEmails { get; set; }
        public string? PerToolOverrides { get; set; }
    }

    // -------- PUT /api/agent/policy --------
    [HttpPut("policy")]
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

        if (dto.MaxAutoAwardValue.HasValue) policy.MaxAutoAwardValue = dto.MaxAutoAwardValue.Value;
        if (dto.MaxAutoOrderValue.HasValue) policy.MaxAutoOrderValue = dto.MaxAutoOrderValue.Value;
        if (dto.RequireApprovalForAwards.HasValue) policy.RequireApprovalForAwards = dto.RequireApprovalForAwards.Value;
        if (dto.RequireApprovalForOrders.HasValue) policy.RequireApprovalForOrders = dto.RequireApprovalForOrders.Value;
        if (dto.RequireApprovalForSupplierEmails.HasValue) policy.RequireApprovalForSupplierEmails = dto.RequireApprovalForSupplierEmails.Value;
        if (dto.PerToolOverrides is not null) policy.PerToolOverrides = string.IsNullOrWhiteSpace(dto.PerToolOverrides) ? null : dto.PerToolOverrides;

        policy.UpdatedOn = DateTime.UtcNow;

        if (isNew) _db.Set<AgentPolicy>().Add(policy);
        else _db.Set<AgentPolicy>().Update(policy);
        await _db.SaveChangesAsync(ct);

        return Ok(ToPolicyDto(policy));
    }

    // ---------------- helpers ----------------

    private static object ToPolicyDto(AgentPolicy p) => new
    {
        businessUnitId = p.BusinessUnitId,
        autonomyLevel = p.AutonomyLevel.ToString(),
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

    private AgentToolContext? BuildContext()
    {
        var buRaw = User.FindFirst("businessUnitId")?.Value;
        if (!long.TryParse(buRaw, out var bu) || bu <= 0) return null;

        long? userId = long.TryParse(
            User.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? User.FindFirst("sub")?.Value, out var uid) ? uid : null;
        var userName = User.FindFirst(ClaimTypes.Email)?.Value ?? User.FindFirst("email")?.Value;

        return new AgentToolContext { BusinessUnitId = bu, UserId = userId, UserName = userName };
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
