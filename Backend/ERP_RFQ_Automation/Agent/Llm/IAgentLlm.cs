using System.Text.Json;
using ERP_RFQ_Automation.AI;

namespace ERP_RFQ_Automation.Agent.Llm;

/// <summary>Anthropic-style content block within a message.</summary>
public sealed class AgentContentBlock
{
    /// <summary>"text" | "tool_use" | "tool_result".</summary>
    public string Type { get; set; } = "text";

    // text
    public string? Text { get; set; }

    // tool_use
    public string? Id { get; set; }
    public string? Name { get; set; }
    public JsonElement? Input { get; set; }

    // tool_result
    public string? ToolUseId { get; set; }
    public string? ResultText { get; set; }
    public bool IsError { get; set; }

    public static AgentContentBlock TextBlock(string text) => new() { Type = "text", Text = text };

    public static AgentContentBlock ToolUseBlock(string id, string name, JsonElement input) =>
        new() { Type = "tool_use", Id = id, Name = name, Input = input };

    public static AgentContentBlock ToolResultBlock(string toolUseId, string resultText, bool isError) =>
        new() { Type = "tool_result", ToolUseId = toolUseId, ResultText = resultText, IsError = isError };
}

/// <summary>One message in the conversation history sent to the model.</summary>
public sealed class AgentLlmMessage
{
    public string Role { get; set; } = "user"; // "user" | "assistant"
    public List<AgentContentBlock> Content { get; set; } = new();

    public static AgentLlmMessage User(string text) =>
        new() { Role = "user", Content = { AgentContentBlock.TextBlock(text) } };

    public static AgentLlmMessage Assistant(string text) =>
        new() { Role = "assistant", Content = { AgentContentBlock.TextBlock(text) } };
}

/// <summary>Tool advertised to the model for this turn.</summary>
public sealed class AgentToolDefinition
{
    public string Name { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public string InputJsonSchema { get; init; } = "{}";
}

public enum AgentTurnStopReason
{
    EndTurn,
    ToolUse,
    MaxTokens,
    Error
}

public sealed class AgentToolUse
{
    public string Id { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public JsonElement Input { get; init; }
}

public sealed class AgentLlmTurnResult
{
    public string? AssistantText { get; init; }
    public IReadOnlyList<AgentToolUse> ToolUses { get; init; } = Array.Empty<AgentToolUse>();
    public AgentTurnStopReason StopReason { get; init; }
    public string? Error { get; init; }
}

/// <summary>
/// Runs a single Claude Messages API turn. The orchestrator owns the tool-use loop;
/// this abstraction just maps one request/response.
/// </summary>
public interface IAgentLlm
{
    Task<AgentLlmTurnResult> RunTurnAsync(
        string systemPrompt,
        IReadOnlyList<AgentLlmMessage> history,
        IReadOnlyList<AgentToolDefinition> tools,
        AiCallContext callContext,
        CancellationToken ct);
}
