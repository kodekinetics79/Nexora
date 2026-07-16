namespace ERP_RFQ_Automation.Agent;

public enum AgentStreamEventType
{
    Session,
    Token,
    ToolCall,
    ToolResult,
    ApprovalRequired,
    MessageComplete,
    Done,
    Error
}

/// <summary>
/// One event streamed out of the orchestrator. The endpoint maps these to the SSE
/// JSON line shapes documented in the API contract.
/// </summary>
public sealed class AgentStreamEvent
{
    public AgentStreamEventType Type { get; init; }

    public Guid? SessionId { get; init; }
    public string? Text { get; init; }
    public string? ToolName { get; init; }
    public object? Input { get; init; }
    public bool? Ok { get; init; }
    public string? Summary { get; init; }
    public Guid? ApprovalId { get; init; }
    public long? MessageId { get; init; }
    public string? Message { get; init; }

    public static AgentStreamEvent SessionEvent(Guid id) => new() { Type = AgentStreamEventType.Session, SessionId = id };
    public static AgentStreamEvent TokenEvent(string text) => new() { Type = AgentStreamEventType.Token, Text = text };
    public static AgentStreamEvent ToolCallEvent(string name, object? input) => new() { Type = AgentStreamEventType.ToolCall, ToolName = name, Input = input };
    public static AgentStreamEvent ToolResultEvent(string name, bool ok, string summary) => new() { Type = AgentStreamEventType.ToolResult, ToolName = name, Ok = ok, Summary = summary };
    public static AgentStreamEvent ApprovalEvent(Guid approvalId, string toolName, string summary) => new() { Type = AgentStreamEventType.ApprovalRequired, ApprovalId = approvalId, ToolName = toolName, Summary = summary };
    public static AgentStreamEvent DoneEvent(long? messageId) => new() { Type = AgentStreamEventType.Done, MessageId = messageId };
    public static AgentStreamEvent ErrorEvent(string message) => new() { Type = AgentStreamEventType.Error, Message = message };
}
