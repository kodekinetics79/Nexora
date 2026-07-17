using System.Text.Json;

namespace ERP_RFQ_Automation.Agent.Llm;

/// <summary>
/// One tool call the mock has already issued during the CURRENT user turn, paired
/// with the orchestrator's tool_result for it. Reconstructed purely from the
/// message history on every invocation, so the mock stays completely stateless.
/// </summary>
internal sealed class MockStep
{
    public string ToolName { get; init; } = string.Empty;
    public JsonElement? Input { get; init; }
    public string? ResultText { get; init; }
    public bool IsError { get; init; }

    /// <summary>Result parsed as JSON, or null when the result is plain text (e.g. approval feedback).</summary>
    public JsonElement? ResultJson { get; init; }

    /// <summary>The orchestrator held this mutation for human approval.</summary>
    public bool HeldForApproval =>
        ResultText?.Contains("queued for human approval", StringComparison.OrdinalIgnoreCase) == true;

    /// <summary>The guardrail denied this mutation outright.</summary>
    public bool DeniedByPolicy =>
        ResultText?.Contains("denied by policy", StringComparison.OrdinalIgnoreCase) == true;
}

/// <summary>Everything the mock knows about the turn in progress.</summary>
internal sealed class MockTurnState
{
    /// <summary>The latest human utterance (the message that started this turn).</summary>
    public string UserText { get; init; } = string.Empty;

    /// <summary>Tool calls already made this turn, in order, with their results.</summary>
    public IReadOnlyList<MockStep> Steps { get; init; } = Array.Empty<MockStep>();
}

/// <summary>
/// Derives the current chain position from the conversation history alone.
/// The orchestrator appends, per iteration: assistant(text + tool_use blocks)
/// then user(tool_result blocks). Prior turns are reloaded as plain text only,
/// so every tool_use/tool_result after the last text-bearing user message
/// belongs to THIS turn.
/// </summary>
internal static class MockTurn
{
    public static MockTurnState Read(IReadOnlyList<AgentLlmMessage> history)
    {
        var userTextIdx = -1;
        for (var i = history.Count - 1; i >= 0; i--)
        {
            var m = history[i];
            if (m.Role == "user" && m.Content.Any(b => b.Type == "text" && !string.IsNullOrWhiteSpace(b.Text)))
            {
                userTextIdx = i;
                break;
            }
        }

        var userText = userTextIdx >= 0
            ? string.Join(" ", history[userTextIdx].Content
                .Where(b => b.Type == "text" && !string.IsNullOrWhiteSpace(b.Text))
                .Select(b => b.Text))
            : string.Empty;

        // Collect this turn's tool_use blocks in order and pair each with its tool_result.
        var uses = new List<AgentContentBlock>();
        var resultsById = new Dictionary<string, AgentContentBlock>(StringComparer.Ordinal);
        for (var i = userTextIdx + 1; i < history.Count; i++)
        {
            foreach (var b in history[i].Content)
            {
                if (b.Type == "tool_use" && !string.IsNullOrEmpty(b.Name))
                    uses.Add(b);
                else if (b.Type == "tool_result" && !string.IsNullOrEmpty(b.ToolUseId))
                    resultsById[b.ToolUseId!] = b;
            }
        }

        var steps = new List<MockStep>(uses.Count);
        foreach (var use in uses)
        {
            resultsById.TryGetValue(use.Id ?? string.Empty, out var res);
            steps.Add(new MockStep
            {
                ToolName = use.Name!,
                Input = use.Input,
                ResultText = res?.ResultText,
                IsError = res?.IsError ?? false,
                ResultJson = MockJson.TryParse(res?.ResultText)
            });
        }

        return new MockTurnState { UserText = userText, Steps = steps };
    }
}

/// <summary>
/// Defensive, case-insensitive JSON readers. Tool results serialize anonymous
/// types with mixed property casing ("Id" vs "rfqNo"), and some tools referenced
/// here are still being built by other engineers — so every lookup tolerates
/// missing properties, alternate names and non-JSON payloads.
/// </summary>
internal static class MockJson
{
    public static JsonElement? TryParse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            return null; // plain-text tool feedback (approval/deny messages)
        }
    }

    /// <summary>First matching property (case-insensitive), trying each name in order.</summary>
    public static JsonElement? Prop(JsonElement? el, params string[] names)
    {
        if (el is null || el.Value.ValueKind != JsonValueKind.Object) return null;
        foreach (var name in names)
        {
            foreach (var p in el.Value.EnumerateObject())
            {
                if (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase))
                    return p.Value;
            }
        }
        return null;
    }

    public static string? Str(JsonElement? el, params string[] names)
    {
        var v = Prop(el, names);
        return v is { ValueKind: JsonValueKind.String } s ? s.GetString() : null;
    }

    public static long? Int(JsonElement? el, params string[] names)
    {
        var v = Prop(el, names);
        if (v is null) return null;
        if (v.Value.ValueKind == JsonValueKind.Number && v.Value.TryGetInt64(out var n)) return n;
        if (v.Value.ValueKind == JsonValueKind.String && long.TryParse(v.Value.GetString(), out var s)) return s;
        return null;
    }

    public static decimal? Num(JsonElement? el, params string[] names)
    {
        var v = Prop(el, names);
        if (v is null) return null;
        if (v.Value.ValueKind == JsonValueKind.Number && v.Value.TryGetDecimal(out var n)) return n;
        if (v.Value.ValueKind == JsonValueKind.String && decimal.TryParse(v.Value.GetString(), out var s)) return s;
        return null;
    }

    public static bool Flag(JsonElement? el, params string[] names) =>
        Prop(el, names) is { ValueKind: JsonValueKind.True };

    public static IReadOnlyList<JsonElement> Arr(JsonElement? el, params string[] names)
    {
        var v = Prop(el, names);
        return v is { ValueKind: JsonValueKind.Array } a
            ? a.EnumerateArray().ToList()
            : Array.Empty<JsonElement>();
    }
}
