using System.Text.Json;
using ERP_RFQ_Automation.Agent.Models;

namespace ERP_RFQ_Automation.Agent.Guardrails;

public enum GuardrailOutcome
{
    Allow,
    RequireApproval,
    Deny
}

public sealed class GuardrailDecision
{
    public GuardrailOutcome Outcome { get; init; }
    public string Reason { get; init; } = string.Empty;

    public static GuardrailDecision Allow(string reason) => new() { Outcome = GuardrailOutcome.Allow, Reason = reason };
    public static GuardrailDecision RequireApproval(string reason) => new() { Outcome = GuardrailOutcome.RequireApproval, Reason = reason };
    public static GuardrailDecision Deny(string reason) => new() { Outcome = GuardrailOutcome.Deny, Reason = reason };
}

/// <summary>
/// Policy engine consulted by the orchestrator before any mutating tool runs.
/// </summary>
public interface IAgentGuardrail
{
    Task<GuardrailDecision> EvaluateAsync(IAgentTool tool, JsonElement input, AgentToolContext ctx, CancellationToken ct);

    /// <summary>Loads the tenant policy or the conservative default.</summary>
    Task<AgentPolicy> GetPolicyAsync(long businessUnitId, CancellationToken ct);
}
