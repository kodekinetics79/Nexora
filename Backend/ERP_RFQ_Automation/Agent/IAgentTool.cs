using System.Text.Json;

namespace ERP_RFQ_Automation.Agent;

/// <summary>
/// Per-invocation context handed to every tool. Carries the tenant + acting user
/// resolved from the JWT so tools never trust caller-supplied identifiers. The EF
/// global query filter already scopes reads to <see cref="BusinessUnitId"/>; tools
/// stamp it onto any rows they create.
/// </summary>
public sealed class AgentToolContext
{
    public long BusinessUnitId { get; init; }
    public long? UserId { get; init; }
    public string? UserName { get; init; }
}

/// <summary>Uniform result envelope for tool execution.</summary>
public sealed class AgentToolResult
{
    public bool Success { get; init; }
    public object? Data { get; init; }
    public string? Error { get; init; }

    public static AgentToolResult Ok(object? d) => new() { Success = true, Data = d };
    public static AgentToolResult Fail(string e) => new() { Success = false, Error = e };
}

/// <summary>
/// A single capability Claude can invoke. Tools are pure executors — they do NOT
/// know about guardrails. The orchestrator inspects <see cref="IsMutation"/> and
/// routes mutating tools through the guardrail engine before ever calling
/// <see cref="ExecuteAsync"/>.
/// </summary>
public interface IAgentTool
{
    /// <summary>Stable snake_case identifier, e.g. "search_rfqs".</summary>
    string Name { get; }

    /// <summary>Human/model-facing description shown to Claude.</summary>
    string Description { get; }

    /// <summary>JSON Schema (as a string) describing the tool input object.</summary>
    string InputJsonSchema { get; }

    /// <summary>true =&gt; routed through the guardrail engine before execution.</summary>
    bool IsMutation { get; }

    Task<AgentToolResult> ExecuteAsync(JsonElement input, AgentToolContext ctx, CancellationToken ct);
}

/// <summary>Discovers the registered <see cref="IAgentTool"/> set.</summary>
public interface IAgentToolRegistry
{
    IReadOnlyList<IAgentTool> All { get; }
    IAgentTool? Find(string name);
}

public sealed class AgentToolRegistry : IAgentToolRegistry
{
    private readonly Dictionary<string, IAgentTool> _byName;

    public AgentToolRegistry(IEnumerable<IAgentTool> tools)
    {
        _byName = new Dictionary<string, IAgentTool>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in tools)
            _byName[t.Name] = t; // last registration wins; names are expected unique
        All = _byName.Values.ToList();
    }

    public IReadOnlyList<IAgentTool> All { get; }

    public IAgentTool? Find(string name) =>
        name is not null && _byName.TryGetValue(name, out var t) ? t : null;
}

/// <summary>Small helpers for reading tool input JSON defensively.</summary>
internal static class JsonInputExtensions
{
    public static string? GetStringOrNull(this JsonElement e, string prop) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;

    public static long? GetInt64OrNull(this JsonElement e, string prop)
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(prop, out var v)) return null;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n)) return n;
        if (v.ValueKind == JsonValueKind.String && long.TryParse(v.GetString(), out var s)) return s;
        return null;
    }

    public static int GetInt32(this JsonElement e, string prop, int fallback)
    {
        var v = e.GetInt64OrNull(prop);
        return v.HasValue ? (int)v.Value : fallback;
    }

    public static decimal? GetDecimalOrNull(this JsonElement e, string prop)
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(prop, out var v)) return null;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetDecimal(out var n)) return n;
        if (v.ValueKind == JsonValueKind.String && decimal.TryParse(v.GetString(), out var s)) return s;
        return null;
    }

    public static bool GetBool(this JsonElement e, string prop, bool fallback = false)
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(prop, out var v)) return fallback;
        if (v.ValueKind == JsonValueKind.True) return true;
        if (v.ValueKind == JsonValueKind.False) return false;
        if (v.ValueKind == JsonValueKind.String && bool.TryParse(v.GetString(), out var s)) return s;
        return fallback;
    }
}
