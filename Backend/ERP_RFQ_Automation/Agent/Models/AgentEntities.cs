using System;

namespace ERP_RFQ_Automation.Agent.Models;

/// <summary>Message author within an agent conversation.</summary>
public enum AgentMessageRole
{
    User,
    Assistant,
    Tool,
    System
}

/// <summary>Lifecycle of a queued mutating action awaiting human sign-off.</summary>
public enum AgentApprovalStatus
{
    Pending,
    Approved,
    Rejected,
    Executed,
    Failed
}

/// <summary>
/// A single agent conversation for one tenant. Tenant-scoped via the global query
/// filter configured in <c>ErpRfqAutomationContext.Agent.cs</c>.
/// </summary>
public sealed class AgentSession
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public long BusinessUnitId { get; set; }
    public string Title { get; set; } = "New conversation";
    public long? CreatedByUserId { get; set; }
    public string? CreatedByName { get; set; }
    public DateTime CreatedOn { get; set; }
    public DateTime UpdatedOn { get; set; }
}

/// <summary>
/// One persisted turn/event in a conversation. Assistant tool calls and tool
/// results are stored so a session can be replayed and audited.
/// </summary>
public sealed class AgentMessage
{
    public long Id { get; set; }
    public Guid SessionId { get; set; }
    public long BusinessUnitId { get; set; }
    public AgentMessageRole Role { get; set; }
    public string? Content { get; set; }

    /// <summary>Set when this message represents a tool invocation / result.</summary>
    public string? ToolName { get; set; }

    /// <summary>Raw tool input JSON (jsonb).</summary>
    public string? ToolInput { get; set; }

    /// <summary>Raw tool result JSON (jsonb).</summary>
    public string? ToolResult { get; set; }

    /// <summary>Monotonic ordering within the session.</summary>
    public int Sequence { get; set; }

    public DateTime CreatedOn { get; set; }
}

/// <summary>
/// A mutating action the guardrail engine held for human approval, storing the
/// exact tool + input so it can be re-invoked verbatim on approval.
/// </summary>
public sealed class AgentApproval
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid? SessionId { get; set; }
    public long BusinessUnitId { get; set; }
    public string ToolName { get; set; } = string.Empty;

    /// <summary>Verbatim tool input JSON (jsonb) captured at request time.</summary>
    public string InputJson { get; set; } = "{}";

    public AgentApprovalStatus Status { get; set; } = AgentApprovalStatus.Pending;
    public string? Summary { get; set; }

    public long? RequestedByUserId { get; set; }
    public string? RequestedBy { get; set; }
    public long? DecidedByUserId { get; set; }
    public string? DecidedBy { get; set; }

    /// <summary>Tool result JSON (jsonb) after execution on approval.</summary>
    public string? ResultJson { get; set; }

    public DateTime CreatedOn { get; set; }
    public DateTime UpdatedOn { get; set; }
    public DateTime? DecidedOn { get; set; }
}

/// <summary>
/// Append-only audit trail of every guardrail decision + mutation outcome. Never
/// updated or deleted. Scoped by <see cref="BusinessUnitId"/>.
/// </summary>
public sealed class AgentAuditLog
{
    public long Id { get; set; }
    public long BusinessUnitId { get; set; }

    /// <summary>Who acted — user name/id, or "agent".</summary>
    public string Actor { get; set; } = "agent";

    public string ToolName { get; set; } = string.Empty;

    /// <summary>Guardrail decision / outcome: Allow, RequireApproval, Deny, Executed, Failed.</summary>
    public string Decision { get; set; } = string.Empty;

    public string? InputJson { get; set; }
    public string? ResultSummary { get; set; }

    public DateTime CreatedOn { get; set; }
}
