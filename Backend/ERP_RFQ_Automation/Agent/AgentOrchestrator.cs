using System.Runtime.CompilerServices;
using System.Text.Json;
using ERP_RFQ_Automation.Agent.Guardrails;
using ERP_RFQ_Automation.Agent.Llm;
using ERP_RFQ_Automation.Agent.Models;
using ERP_RFQ_Automation.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using ERP_RFQ_Automation.AI;

namespace ERP_RFQ_Automation.Agent;

/// <summary>Outcome of executing a single tool through the shared executor.</summary>
public sealed record ToolExecutionOutcome(bool Ok, string Summary, string ResultJson);

public interface IAgentOrchestrator
{
    /// <summary>
    /// Runs one user turn end to end, streaming events as the tool-use loop advances.
    /// </summary>
    IAsyncEnumerable<AgentStreamEvent> RunAsync(Guid? sessionId, string message, AgentToolContext ctx, CancellationToken ct);

    /// <summary>
    /// Re-invokes a previously held mutation with its saved input (used by the
    /// approve endpoint). Applies NO further guardrail — approval IS the gate.
    /// </summary>
    Task<ToolExecutionOutcome> ExecuteApprovedAsync(IAgentTool tool, JsonElement input, AgentToolContext ctx, CancellationToken ct);
}

public sealed class AgentOrchestrator : IAgentOrchestrator
{
    private const int MaxIterations = 8;

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = false };

    private readonly ErpRfqAutomationContext _db;
    private readonly IAgentLlm _llm;
    private readonly IAgentToolRegistry _tools;
    private readonly IAgentGuardrail _guardrail;
    private readonly IAuthorizationService _authorization;
    private readonly ILogger<AgentOrchestrator> _log;

    public AgentOrchestrator(
        ErpRfqAutomationContext db,
        IAgentLlm llm,
        IAgentToolRegistry tools,
        IAgentGuardrail guardrail,
        IAuthorizationService authorization,
        ILogger<AgentOrchestrator> log)
    {
        _db = db;
        _llm = llm;
        _tools = tools;
        _guardrail = guardrail;
        _authorization = authorization;
        _log = log;
    }

    public async IAsyncEnumerable<AgentStreamEvent> RunAsync(
        Guid? sessionId, string message, AgentToolContext ctx, [EnumeratorCancellation] CancellationToken ct)
    {
        // ---- Session bootstrap ----
        // Resuming somebody else's transcript is a read of their questions and of every row
        // the agent returned to them, so an id that is not this user's is refused outright
        // rather than silently starting a new conversation.
        var session = await GetOrCreateSessionAsync(sessionId, message, ctx, ct);
        if (session is null)
        {
            yield return AgentStreamEvent.ErrorEvent("That conversation does not belong to you.");
            yield break;
        }
        yield return AgentStreamEvent.SessionEvent(session.Id);

        // Reconstruct prior conversational context (plain text only, to keep
        // tool_use/tool_result block pairing valid for the current turn only).
        var history = await LoadHistoryAsync(session.Id, ct);

        // Persist + append the current user message.
        var seq = await NextSequenceAsync(session.Id, ct);
        var userMessageId = await PersistMessageAsync(
            session, AgentMessageRole.User, message, null, null, null, seq++, ct);
        history.Add(AgentLlmMessage.User(message));

        var toolDefs = _tools.All
            .Select(t => new AgentToolDefinition { Name = t.Name, Description = t.Description, InputJsonSchema = t.InputJsonSchema })
            .ToList();

        var systemPrompt = BuildSystemPrompt(ctx);
        long? lastAssistantMessageId = null;

        for (var iteration = 0; iteration < MaxIterations; iteration++)
        {
            var turn = await _llm.RunTurnAsync(systemPrompt, history, toolDefs,
                new AiCallContext(ctx.BusinessUnitId, AiPurposes.Agent,
                    $"agent:{session.Id}:message:{userMessageId}:iteration:{iteration + 1}", "agent-tools-v1"), ct);

            if (turn.StopReason == AgentTurnStopReason.Error)
            {
                yield return AgentStreamEvent.ErrorEvent(turn.Error ?? "The assistant failed to respond.");
                yield break;
            }

            // Emit + persist any assistant text for this turn.
            if (!string.IsNullOrWhiteSpace(turn.AssistantText))
            {
                yield return AgentStreamEvent.TokenEvent(turn.AssistantText!);
                lastAssistantMessageId = await PersistMessageAsync(
                    session, AgentMessageRole.Assistant, turn.AssistantText, null, null, null, seq++, ct);
            }

            // Record the assistant turn (text + tool_use blocks) in the model history.
            history.Add(BuildAssistantHistoryMessage(turn));

            if (turn.StopReason != AgentTurnStopReason.ToolUse || turn.ToolUses.Count == 0)
            {
                yield return AgentStreamEvent.DoneEvent(lastAssistantMessageId);
                yield break;
            }

            // ---- Process each tool_use, collecting tool_result blocks for the next turn ----
            var resultBlocks = new List<AgentContentBlock>();

            foreach (var use in turn.ToolUses)
            {
                var tool = _tools.Find(use.Name);
                yield return AgentStreamEvent.ToolCallEvent(use.Name, CloneInput(use.Input));

                if (tool is null)
                {
                    var msg = $"Unknown tool '{use.Name}'.";
                    resultBlocks.Add(AgentContentBlock.ToolResultBlock(use.Id, msg, isError: true));
                    await PersistMessageAsync(session, AgentMessageRole.Tool, msg, use.Name, RawText(use.Input), null, seq++, ct);
                    yield return AgentStreamEvent.ToolResultEvent(use.Name, false, msg);
                    continue;
                }

                if (!tool.IsMutation)
                {
                    var outcome = await ExecuteToolAsync(tool, use.Input, ctx, ct);
                    // Tool OUTPUT is untrusted evidence — supplier/customer text reaches the model
                    // through here. Fenced; see AgentUntrustedContent.
                    resultBlocks.Add(AgentContentBlock.ToolResultBlock(
                        use.Id, AgentUntrustedContent.Fence(outcome.ResultJson), isError: !outcome.Ok));
                    await PersistMessageAsync(session, AgentMessageRole.Tool, outcome.Summary, use.Name, RawText(use.Input), outcome.ResultJson, seq++, ct);
                    yield return AgentStreamEvent.ToolResultEvent(use.Name, outcome.Ok, outcome.Summary);
                    continue;
                }

                // ---- Mutation: consult the guardrail engine ----
                var decision = await _guardrail.EvaluateAsync(tool, use.Input, ctx, ct);

                if (decision.Outcome == GuardrailOutcome.Allow)
                {
                    var outcome = await ExecuteToolAsync(tool, use.Input, ctx, ct);
                    await AuditAsync(ctx, use.Name, outcome.Ok ? "Executed" : "Failed", RawText(use.Input), outcome.Summary, ct);
                    resultBlocks.Add(AgentContentBlock.ToolResultBlock(
                        use.Id, AgentUntrustedContent.Fence(outcome.ResultJson), isError: !outcome.Ok));
                    await PersistMessageAsync(session, AgentMessageRole.Tool, outcome.Summary, use.Name, RawText(use.Input), outcome.ResultJson, seq++, ct);
                    yield return AgentStreamEvent.ToolResultEvent(use.Name, outcome.Ok, outcome.Summary);
                }
                else if (decision.Outcome == GuardrailOutcome.RequireApproval)
                {
                    var approval = await CreateApprovalAsync(session, ctx, use.Name, use.Input, decision.Reason, ct);
                    await AuditAsync(ctx, use.Name, "Held", RawText(use.Input), decision.Reason, ct);

                    var feedback =
                        $"Action '{use.Name}' has been queued for human approval (approvalId={approval.Id}). " +
                        $"Reason: {decision.Reason} Inform the user it will run once a human approves it.";
                    resultBlocks.Add(AgentContentBlock.ToolResultBlock(use.Id, feedback, isError: false));
                    await PersistMessageAsync(session, AgentMessageRole.Tool, feedback, use.Name, RawText(use.Input), null, seq++, ct);

                    yield return AgentStreamEvent.ApprovalEvent(approval.Id, use.Name, decision.Reason);
                }
                else // Deny
                {
                    await AuditAsync(ctx, use.Name, "Denied", RawText(use.Input), decision.Reason, ct);
                    var feedback = $"Action '{use.Name}' was denied by policy: {decision.Reason}";
                    resultBlocks.Add(AgentContentBlock.ToolResultBlock(use.Id, feedback, isError: true));
                    await PersistMessageAsync(session, AgentMessageRole.Tool, feedback, use.Name, RawText(use.Input), null, seq++, ct);
                    yield return AgentStreamEvent.ToolResultEvent(use.Name, false, feedback);
                }
            }

            // Feed all tool results back to the model as one user message and continue.
            history.Add(new AgentLlmMessage { Role = "user", Content = resultBlocks });
            await TouchSessionAsync(session, ct);
        }

        // Iteration cap reached — close the stream gracefully.
        yield return AgentStreamEvent.DoneEvent(lastAssistantMessageId);
    }

    public async Task<ToolExecutionOutcome> ExecuteApprovedAsync(IAgentTool tool, JsonElement input, AgentToolContext ctx, CancellationToken ct)
    {
        var outcome = await ExecuteToolAsync(tool, input, ctx, ct);
        await AuditAsync(ctx, tool.Name, outcome.Ok ? "Executed" : "Failed", input.GetRawText(), outcome.Summary, ct);
        return outcome;
    }

    // ---------------- helpers ----------------

    /// <summary>
    /// The single dispatch point for every tool — the read path, the guardrail-allowed
    /// mutation path and the approved-mutation path all funnel through here, which is why the
    /// module-permission gate lives here and not in each tool.
    /// </summary>
    private async Task<ToolExecutionOutcome> ExecuteToolAsync(IAgentTool tool, JsonElement input, AgentToolContext ctx, CancellationToken ct)
    {
        var refusal = await AuthorizeToolAsync(tool, ctx, input, ct);
        if (refusal is not null) return refusal;

        try
        {
            var result = await tool.ExecuteAsync(input, ctx, ct);
            if (result.Success)
            {
                var json = JsonSerializer.Serialize(result.Data, JsonOpts);
                return new ToolExecutionOutcome(true, Truncate(json, 400), json);
            }
            var errJson = JsonSerializer.Serialize(new { error = result.Error }, JsonOpts);
            return new ToolExecutionOutcome(false, result.Error ?? "Tool failed.", errJson);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Tool {Tool} threw during execution.", tool.Name);
            var errJson = JsonSerializer.Serialize(new { error = "Tool execution failed." }, JsonOpts);
            return new ToolExecutionOutcome(false, "Tool execution failed.", errJson);
        }
    }

    /// <summary>
    /// Refuses a tool the caller may not run, and returns the refusal as the tool's own
    /// result so the model is told "you are not allowed to do that" rather than being handed
    /// the data. Returns null when the caller holds every declared grant.
    ///
    /// <para>Deny-by-default in three ways: an unmapped tool, a context with no principal,
    /// and a context with no role are each a refusal. The policy consulted is the real
    /// "ModulePermission:{module}:{action}" policy — the same object
    /// <c>[RequireModulePermission]</c> resolves — so agent and HTTP surfaces cannot drift
    /// apart. CustomFieldsController.cs:185-187 is the precedent for calling it by name.</para>
    /// </summary>
    private async Task<ToolExecutionOutcome?> AuthorizeToolAsync(
        IAgentTool tool, AgentToolContext ctx, JsonElement input, CancellationToken ct)
    {
        if (!AgentToolPermissions.TryGetRequirements(tool.Name, out var requirements))
            return await RefuseAsync(tool, ctx, input,
                $"'{tool.Name}' declares no module permission, so no one is authorized to run it.", ct);

        if (ctx.Principal is null)
            return await RefuseAsync(tool, ctx, input,
                $"'{tool.Name}' cannot run: this session carries no authenticated principal to authorize.", ct);

        if (ctx.RoleId is null)
            return await RefuseAsync(tool, ctx, input,
                $"'{tool.Name}' cannot run: this session carries no role, so no permission can be resolved.", ct);

        foreach (var requirement in requirements)
        {
            var result = await _authorization.AuthorizeAsync(ctx.Principal, null, requirement.Policy);
            if (!result.Succeeded)
                return await RefuseAsync(tool, ctx, input,
                    $"You do not have {requirement.Module} ({requirement.Action}) permission, " +
                    $"which '{tool.Name}' requires. Tell the user this and do not attempt it another way.", ct);
        }

        return null;
    }

    private async Task<ToolExecutionOutcome> RefuseAsync(
        IAgentTool tool, AgentToolContext ctx, JsonElement input, string reason, CancellationToken ct)
    {
        _log.LogWarning("Agent tool {Tool} refused for role {RoleId} in BU {Bu}: {Reason}",
            tool.Name, ctx.RoleId, ctx.BusinessUnitId, reason);
        await AuditAsync(ctx, tool.Name, "Denied", RawText(input), reason, ct);
        var json = JsonSerializer.Serialize(new { error = reason }, JsonOpts);
        return new ToolExecutionOutcome(false, reason, json);
    }

    private static string BuildSystemPrompt(AgentToolContext ctx)
    {
        var who = string.IsNullOrWhiteSpace(ctx.UserName) ? "a procurement user" : ctx.UserName;
        return
            "You are Nexora's autonomous sourcing copilot: an expert procurement assistant for a single " +
            $"business unit (tenant #{ctx.BusinessUnitId}), currently assisting {who}. " +
            "You can search RFQs, suppliers, leads, quotes and orders, summarize the dashboard, recommend " +
            "supplier awards, dispatch RFQs to suppliers, and create orders from quotes — using the provided " +
            "tools. Always ground your answers in tool results rather than guessing. " +
            "Some actions (sending supplier emails, recommending awards, creating orders) are governed by " +
            "tenant guardrails and may be queued for human approval; when a tool tells you an action is queued " +
            "for approval, clearly tell the user it is pending approval and do not claim it is done. " +
            "Explain your reasoning briefly, cite the concrete records (ids, numbers, amounts) you used, and " +
            "never take a mutating action the user did not ask for. All data is already scoped to this tenant, " +
            "and every tool additionally enforces the user's own module permissions: if a tool answers that the " +
            "user lacks a permission, say so plainly and do not try to obtain the same data through another tool.\n\n" +
            AgentUntrustedContent.Policy;
    }

    private static AgentLlmMessage BuildAssistantHistoryMessage(AgentLlmTurnResult turn)
    {
        var msg = new AgentLlmMessage { Role = "assistant" };
        if (!string.IsNullOrWhiteSpace(turn.AssistantText))
            msg.Content.Add(AgentContentBlock.TextBlock(turn.AssistantText!));
        foreach (var use in turn.ToolUses)
            msg.Content.Add(AgentContentBlock.ToolUseBlock(use.Id, use.Name, use.Input));
        // An assistant message must have at least one block.
        if (msg.Content.Count == 0)
            msg.Content.Add(AgentContentBlock.TextBlock("(working...)"));
        return msg;
    }

    /// <summary>
    /// Resumes the caller's own session or starts a new one. Returns null when the supplied
    /// id names a session this user does not own — the tenant filter alone is not enough,
    /// because resuming replays the previous user's questions and answers into this turn.
    /// A session with no recorded owner is not resumable by anyone.
    /// </summary>
    private async Task<AgentSession?> GetOrCreateSessionAsync(Guid? sessionId, string message, AgentToolContext ctx, CancellationToken ct)
    {
        if (sessionId is not null)
        {
            var existing = await _db.Set<AgentSession>().FirstOrDefaultAsync(s => s.Id == sessionId.Value, ct);
            if (existing is not null)
                return ctx.UserId is not null && existing.CreatedByUserId == ctx.UserId ? existing : null;
        }

        var now = DateTime.UtcNow;
        var session = new AgentSession
        {
            Id = Guid.NewGuid(),
            BusinessUnitId = ctx.BusinessUnitId,
            Title = Truncate(message, 80),
            CreatedByUserId = ctx.UserId,
            CreatedByName = ctx.UserName,
            CreatedOn = now,
            UpdatedOn = now
        };
        _db.Set<AgentSession>().Add(session);
        await _db.SaveChangesAsync(ct);
        return session;
    }

    private async Task<List<AgentLlmMessage>> LoadHistoryAsync(Guid sessionId, CancellationToken ct)
    {
        var rows = await _db.Set<AgentMessage>().AsNoTracking()
            .Where(m => m.SessionId == sessionId)
            .OrderBy(m => m.Sequence)
            .Select(m => new { m.Role, m.Content })
            .ToListAsync(ct);

        var history = new List<AgentLlmMessage>();
        foreach (var r in rows)
        {
            if (string.IsNullOrWhiteSpace(r.Content)) continue;
            if (r.Role == AgentMessageRole.User) history.Add(AgentLlmMessage.User(r.Content!));
            else if (r.Role == AgentMessageRole.Assistant) history.Add(AgentLlmMessage.Assistant(r.Content!));
            // Tool/System rows are omitted to keep block pairing valid.
        }
        return history;
    }

    private async Task<int> NextSequenceAsync(Guid sessionId, CancellationToken ct)
    {
        var max = await _db.Set<AgentMessage>().Where(m => m.SessionId == sessionId)
            .Select(m => (int?)m.Sequence).MaxAsync(ct);
        return (max ?? -1) + 1;
    }

    private async Task<long> PersistMessageAsync(
        AgentSession session, AgentMessageRole role, string? content,
        string? toolName, string? toolInput, string? toolResult, int sequence, CancellationToken ct)
    {
        var msg = new AgentMessage
        {
            SessionId = session.Id,
            BusinessUnitId = session.BusinessUnitId,
            Role = role,
            Content = content,
            ToolName = toolName,
            ToolInput = toolInput,
            ToolResult = toolResult,
            Sequence = sequence,
            CreatedOn = DateTime.UtcNow
        };
        _db.Set<AgentMessage>().Add(msg);
        await _db.SaveChangesAsync(ct);
        return msg.Id;
    }

    private async Task<AgentApproval> CreateApprovalAsync(
        AgentSession session, AgentToolContext ctx, string toolName, JsonElement input, string reason, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var approval = new AgentApproval
        {
            Id = Guid.NewGuid(),
            SessionId = session.Id,
            BusinessUnitId = ctx.BusinessUnitId,
            ToolName = toolName,
            InputJson = input.GetRawText(),
            Status = AgentApprovalStatus.Pending,
            Summary = $"{toolName}: {reason}",
            RequestedByUserId = ctx.UserId,
            RequestedBy = ctx.UserName,
            CreatedOn = now,
            UpdatedOn = now
        };
        _db.Set<AgentApproval>().Add(approval);
        await _db.SaveChangesAsync(ct);
        return approval;
    }

    private async Task AuditAsync(AgentToolContext ctx, string toolName, string decision, string? inputJson, string? summary, CancellationToken ct)
    {
        _db.Set<AgentAuditLog>().Add(new AgentAuditLog
        {
            BusinessUnitId = ctx.BusinessUnitId,
            Actor = ctx.UserName ?? (ctx.UserId?.ToString() ?? "agent"),
            ToolName = toolName,
            Decision = decision,
            InputJson = inputJson,
            ResultSummary = Truncate(summary ?? string.Empty, 1000),
            CreatedOn = DateTime.UtcNow
        });
        await _db.SaveChangesAsync(ct);
    }

    private async Task TouchSessionAsync(AgentSession session, CancellationToken ct)
    {
        session.UpdatedOn = DateTime.UtcNow;
        _db.Set<AgentSession>().Update(session);
        await _db.SaveChangesAsync(ct);
    }

    private static object? CloneInput(JsonElement input)
    {
        try { return JsonSerializer.Deserialize<object>(input.GetRawText()); }
        catch { return null; }
    }

    private static string RawText(JsonElement input)
    {
        try { return input.GetRawText(); } catch { return "{}"; }
    }

    private static string Truncate(string s, int max) =>
        string.IsNullOrEmpty(s) || s.Length <= max ? s : s[..max] + "…";
}
