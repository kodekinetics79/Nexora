using System.Text.Json;
using ERP_RFQ_Automation.Agent.Guardrails;

namespace ERP_RFQ_Automation.Agent.Llm;

/// <summary>
/// Deterministic, network-free stand-in for Claude. Selected automatically when
/// <c>Agent:Anthropic:ApiKey</c> is empty so the whole engine is demoable with no
/// key. It parses the latest human message for simple intents, emits ONE plausible
/// tool_use, and on the following turn (once it sees a tool_result) returns a final
/// summary — exercising the full read / mutation / approval paths end to end.
/// </summary>
public sealed class MockAgentLlm : IAgentLlm
{
    public Task<AgentLlmTurnResult> RunTurnAsync(
        string systemPrompt,
        IReadOnlyList<AgentLlmMessage> history,
        IReadOnlyList<AgentToolDefinition> tools,
        CancellationToken ct)
    {
        var last = history.Count > 0 ? history[^1] : null;

        // If we just received tool results, wrap up with a summary (end_turn).
        if (last is not null && last.Role == "user" && last.Content.Any(b => b.Type == "tool_result"))
            return Task.FromResult(Summarize(history));

        // Otherwise, decide a single tool call from the latest human utterance.
        var userText = ExtractLatestUserText(history);
        var (toolName, inputJson) = SelectTool(userText, tools);

        if (toolName is null)
        {
            return Task.FromResult(new AgentLlmTurnResult
            {
                AssistantText = "I can search RFQs, suppliers, leads, quotes and orders, summarize your " +
                                "dashboard, and (with approval) dispatch RFQs, recommend awards, and create " +
                                "orders. Tell me what you'd like to do.",
                StopReason = AgentTurnStopReason.EndTurn
            });
        }

        var input = JsonDocument.Parse(inputJson).RootElement.Clone();
        return Task.FromResult(new AgentLlmTurnResult
        {
            AssistantText = $"Let me use {toolName} to help with that.",
            ToolUses = new[] { new AgentToolUse { Id = $"mock_{Guid.NewGuid():N}", Name = toolName, Input = input } },
            StopReason = AgentTurnStopReason.ToolUse
        });
    }

    private static AgentLlmTurnResult Summarize(IReadOnlyList<AgentLlmMessage> history)
    {
        // Name the tool(s) that ran so the summary is grounded.
        var lastToolUse = history
            .SelectMany(m => m.Content)
            .LastOrDefault(b => b.Type == "tool_use");
        var toolName = lastToolUse?.Name ?? "the requested tool";

        var lastResult = history[^1].Content.FirstOrDefault(b => b.Type == "tool_result");
        var queued = lastResult?.ResultText?.Contains("approval", StringComparison.OrdinalIgnoreCase) == true;

        var text = queued
            ? $"I've prepared that action ({toolName}) and queued it for human approval under your current " +
              "guardrail policy. You'll be able to approve or reject it from the approvals queue."
            : $"Done — I ran {toolName} and included the results above. Let me know if you'd like me to take " +
              "the next step.";

        return new AgentLlmTurnResult { AssistantText = text, StopReason = AgentTurnStopReason.EndTurn };
    }

    private static (string? tool, string inputJson) SelectTool(string text, IReadOnlyList<AgentToolDefinition> tools)
    {
        var t = (text ?? string.Empty).ToLowerInvariant();
        var available = new HashSet<string>(tools.Select(x => x.Name), StringComparer.OrdinalIgnoreCase);

        bool Has(string name) => available.Contains(name);

        // Mutations / actions first (more specific intents).
        if ((t.Contains("award") || t.Contains("recommend") || t.Contains("compare")) && Has(AgentToolNames.RecommendAward))
            return (AgentToolNames.RecommendAward, $"{{\"rfqId\":{FirstNumberOr(t, 1)}}}");

        if ((t.Contains("order") && (t.Contains("create") || t.Contains("place") || t.Contains("from quote"))) && Has(AgentToolNames.CreateOrderFromQuote))
            return (AgentToolNames.CreateOrderFromQuote, $"{{\"quoteId\":{FirstNumberOr(t, 1)},\"notes\":\"Created by sourcing copilot\"}}");

        if ((t.Contains("send") || t.Contains("dispatch") || t.Contains("invite")) && Has(AgentToolNames.DispatchRfqToSupplier))
            return (AgentToolNames.DispatchRfqToSupplier, $"{{\"rfqId\":{FirstNumberOr(t, 1)},\"supplierId\":1,\"message\":\"Please submit your best pricing and lead times.\"}}");

        // Reads.
        if (t.Contains("supplier") && Has(AgentToolNames.SearchSuppliers))
            return (AgentToolNames.SearchSuppliers, BuildSearchInput(text));
        if (t.Contains("lead") && Has(AgentToolNames.SearchLeads))
            return (AgentToolNames.SearchLeads, BuildSearchInput(text));
        if (t.Contains("quote") && Has(AgentToolNames.SearchQuotes))
            return (AgentToolNames.SearchQuotes, BuildSearchInput(text));
        if (t.Contains("order") && Has(AgentToolNames.SearchOrders))
            return (AgentToolNames.SearchOrders, BuildSearchInput(text));
        if ((t.Contains("dashboard") || t.Contains("summary") || t.Contains("overview") || t.Contains("kpi")) && Has(AgentToolNames.GetDashboardSummary))
            return (AgentToolNames.GetDashboardSummary, "{}");
        if (t.Contains("rfq") && Has(AgentToolNames.SearchRfqs))
            return (AgentToolNames.SearchRfqs, BuildSearchInput(text));

        // Fallback to a dashboard summary if available, else an RFQ search.
        if (Has(AgentToolNames.GetDashboardSummary)) return (AgentToolNames.GetDashboardSummary, "{}");
        if (Has(AgentToolNames.SearchRfqs)) return (AgentToolNames.SearchRfqs, BuildSearchInput(text));
        return (null, "{}");
    }

    private static string BuildSearchInput(string? rawText)
    {
        var query = ExtractQuery(rawText);
        var escaped = JsonEncodedText.Encode(query).ToString();
        return $"{{\"query\":\"{escaped}\",\"page\":1,\"pageSize\":5}}";
    }

    private static string ExtractQuery(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;
        // Naive: drop common command words, keep the rest as a search query.
        var stop = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "search","find","show","list","get","me","the","for","all","please","rfq","rfqs",
            "supplier","suppliers","lead","leads","quote","quotes","order","orders","a","an"
        };
        var words = text.Split(new[] { ' ', ',', '.', '?', '!', ':', ';' }, StringSplitOptions.RemoveEmptyEntries)
            .Where(w => !stop.Contains(w))
            .Take(6);
        return string.Join(' ', words);
    }

    private static long FirstNumberOr(string text, long fallback)
    {
        var digits = new string(text.SkipWhile(c => !char.IsDigit(c)).TakeWhile(char.IsDigit).ToArray());
        return long.TryParse(digits, out var n) && n > 0 ? n : fallback;
    }

    private static string ExtractLatestUserText(IReadOnlyList<AgentLlmMessage> history)
    {
        for (var i = history.Count - 1; i >= 0; i--)
        {
            var m = history[i];
            if (m.Role != "user") continue;
            var textBlock = m.Content.FirstOrDefault(b => b.Type == "text");
            if (textBlock?.Text is { Length: > 0 }) return textBlock.Text;
        }
        return string.Empty;
    }
}
