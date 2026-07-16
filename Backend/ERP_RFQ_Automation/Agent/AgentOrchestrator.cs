using System.Runtime.CompilerServices;
using System.Text.Json;
using ERP_RFQ_Automation.Agent.Guardrails;
using ERP_RFQ_Automation.Agent.Llm;
using ERP_RFQ_Automation.Agent.Models;
using ERP_RFQ_Automation.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

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
    private readonly ILogger<AgentOrchestrator> _log;

    public AgentOrchestrator(
        ErpRfqAutomationContext db,
        IAgentLlm llm,
        IAgentToolRegistry tools,
        IAgentGuardrail guardrail,
        ILogger<AgentOrchestrator> log)
    {
        _db = db;
        _llm = llm;
        _tools = tools;
        _guardrail = guardrail;
        _log = log;
    }

    public async IAsyncEnumerable<AgentStreamEvent> RunAsync(
        Guid? sessionId, string message, AgentToolContext ctx, [EnumeratorCancellation] CancellationToken ct)
    {
        // ---- Session bootstrap ----
        var session = await GetOrCreateSessionAsync(sessionId, message, ctx, ct);
        yield return AgentStreamEvent.SessionEvent(session.Id);

        // Reconstruct prior conversational context (plain text only, to keep
        // tool_use/tool_result block pairing valid for the current turn only).
        var history = await LoadHistoryAsync(session.Id, ct);

        // Persist + append the current user message.
        var seq = await NextSequenceAsync(session.Id, ct);
        await PersistMessageAsync(session, AgentMessageRole.User, message, null, null, null, seq++, ct);
        history.Add(AgentLlmMessage.User(message));

        var toolDefs = _tools.All
            .Select(t => new AgentToolDefinition { Name = t.Name, Description = t.Description, InputJsonSchema = t.InputJsonSchema })
            .ToList();

        var systemPrompt = BuildSystemPrompt(ctx);
        long? lastAssistantMessageId = null;

        for (var iteration = 0; iteration < MaxIterations; iteration++)
        {
            var turn = await _llm.RunTurnAsync(systemPrompt, history, toolDefs, ct);

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
                    resultBlocks.Add(AgentContentBlock.ToolResultBlock(use.Id, outcome.ResultJson, isError: !outcome.Ok));
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
                    resultBlocks.Add(AgentContentBlock.ToolResultBlock(use.Id, outcome.ResultJson, isError: !outcome.Ok));
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

    private async Task<ToolExecutionOutcome> ExecuteToolAsync(IAgentTool tool, JsonElement input, AgentToolContext ctx, CancellationToken ct)
    {
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
            "never take a mutating action the user did not ask for. All data is already scoped to this tenant.";
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

    private async Task<AgentSession> GetOrCreateSessionAsync(Guid? sessionId, string message, AgentToolContext ctx, CancellationToken ct)
    {
        if (sessionId is not null)
        {
            var existing = await _db.Set<AgentSession>().FirstOrDefaultAsync(s => s.Id == sessionId.Value, ct);
            if (existing is not null) return existing;
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
