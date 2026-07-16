using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace ERP_RFQ_Automation.Agent.Llm;

/// <summary>
/// Real Claude Messages API client (https://api.anthropic.com/v1/messages) using
/// the tool-use API. One call == one turn; the orchestrator loops. Selected only
/// when <c>Agent:Anthropic:ApiKey</c> is non-empty (see AddAgentEngine); otherwise
/// <see cref="MockAgentLlm"/> is wired instead so the engine runs with no key.
/// </summary>
public sealed class AnthropicAgentLlm : IAgentLlm
{
    private const string Endpoint = "https://api.anthropic.com/v1/messages";
    private const string AnthropicVersion = "2023-06-01";

    private readonly HttpClient _http;
    private readonly ILogger<AnthropicAgentLlm> _log;
    private readonly string _apiKey;
    private readonly string _model;
    private readonly int _maxTokens;

    public AnthropicAgentLlm(HttpClient http, IConfiguration config, ILogger<AnthropicAgentLlm> log)
    {
        _http = http;
        _log = log;
        _apiKey = config["Agent:Anthropic:ApiKey"] ?? string.Empty;
        _model = string.IsNullOrWhiteSpace(config["Agent:Anthropic:Model"])
            ? "claude-sonnet-5"
            : config["Agent:Anthropic:Model"]!;
        _maxTokens = int.TryParse(config["Agent:Anthropic:MaxTokens"], out var mt) && mt > 0 ? mt : 2048;
    }

    public async Task<AgentLlmTurnResult> RunTurnAsync(
        string systemPrompt,
        IReadOnlyList<AgentLlmMessage> history,
        IReadOnlyList<AgentToolDefinition> tools,
        CancellationToken ct)
    {
        try
        {
            var body = BuildRequestBody(systemPrompt, history, tools);

            using var req = new HttpRequestMessage(HttpMethod.Post, Endpoint)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };
            req.Headers.TryAddWithoutValidation("x-api-key", _apiKey);
            req.Headers.TryAddWithoutValidation("anthropic-version", AnthropicVersion);
            req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            var json = await resp.Content.ReadAsStringAsync(ct);

            if (!resp.IsSuccessStatusCode)
            {
                _log.LogWarning("Anthropic API returned {Status}: {Body}", (int)resp.StatusCode, json);
                return new AgentLlmTurnResult
                {
                    StopReason = AgentTurnStopReason.Error,
                    Error = $"Anthropic API error {(int)resp.StatusCode}."
                };
            }

            return ParseResponse(json);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Anthropic API call failed.");
            return new AgentLlmTurnResult { StopReason = AgentTurnStopReason.Error, Error = "LLM call failed." };
        }
    }

    private string BuildRequestBody(
        string systemPrompt,
        IReadOnlyList<AgentLlmMessage> history,
        IReadOnlyList<AgentToolDefinition> tools)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms))
        {
            w.WriteStartObject();
            w.WriteString("model", _model);
            w.WriteNumber("max_tokens", _maxTokens);
            w.WriteString("system", systemPrompt);

            // tools
            w.WritePropertyName("tools");
            w.WriteStartArray();
            foreach (var t in tools)
            {
                w.WriteStartObject();
                w.WriteString("name", t.Name);
                w.WriteString("description", t.Description);
                w.WritePropertyName("input_schema");
                WriteRawOrEmptyObject(w, t.InputJsonSchema);
                w.WriteEndObject();
            }
            w.WriteEndArray();

            // messages
            w.WritePropertyName("messages");
            w.WriteStartArray();
            foreach (var m in history)
            {
                w.WriteStartObject();
                w.WriteString("role", m.Role);
                w.WritePropertyName("content");
                w.WriteStartArray();
                foreach (var b in m.Content)
                    WriteContentBlock(w, b);
                w.WriteEndArray();
                w.WriteEndObject();
            }
            w.WriteEndArray();

            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    private static void WriteContentBlock(Utf8JsonWriter w, AgentContentBlock b)
    {
        w.WriteStartObject();
        switch (b.Type)
        {
            case "tool_use":
                w.WriteString("type", "tool_use");
                w.WriteString("id", b.Id);
                w.WriteString("name", b.Name);
                w.WritePropertyName("input");
                if (b.Input.HasValue) b.Input.Value.WriteTo(w);
                else { w.WriteStartObject(); w.WriteEndObject(); }
                break;

            case "tool_result":
                w.WriteString("type", "tool_result");
                w.WriteString("tool_use_id", b.ToolUseId);
                w.WriteString("content", b.ResultText ?? string.Empty);
                if (b.IsError) w.WriteBoolean("is_error", true);
                break;

            default:
                w.WriteString("type", "text");
                w.WriteString("text", b.Text ?? string.Empty);
                break;
        }
        w.WriteEndObject();
    }

    private static void WriteRawOrEmptyObject(Utf8JsonWriter w, string rawJson)
    {
        if (string.IsNullOrWhiteSpace(rawJson))
        {
            w.WriteStartObject();
            w.WriteEndObject();
            return;
        }
        try
        {
            using var doc = JsonDocument.Parse(rawJson);
            doc.RootElement.WriteTo(w);
        }
        catch (JsonException)
        {
            w.WriteStartObject();
            w.WriteEndObject();
        }
    }

    private static AgentLlmTurnResult ParseResponse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var text = new StringBuilder();
        var toolUses = new List<AgentToolUse>();

        if (root.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
        {
            foreach (var block in content.EnumerateArray())
            {
                var type = block.TryGetProperty("type", out var t) ? t.GetString() : null;
                if (type == "text" && block.TryGetProperty("text", out var txt))
                {
                    text.Append(txt.GetString());
                }
                else if (type == "tool_use")
                {
                    var id = block.TryGetProperty("id", out var idEl) ? idEl.GetString() ?? "" : "";
                    var name = block.TryGetProperty("name", out var nEl) ? nEl.GetString() ?? "" : "";
                    JsonElement input = block.TryGetProperty("input", out var inEl)
                        ? inEl.Clone()
                        : JsonDocument.Parse("{}").RootElement.Clone();
                    toolUses.Add(new AgentToolUse { Id = id, Name = name, Input = input });
                }
            }
        }

        var stop = root.TryGetProperty("stop_reason", out var sr) ? sr.GetString() : null;
        var stopReason = stop switch
        {
            "tool_use" => AgentTurnStopReason.ToolUse,
            "max_tokens" => AgentTurnStopReason.MaxTokens,
            _ => AgentTurnStopReason.EndTurn
        };
        // Defensive: if the model emitted tool_use blocks, always drive the loop.
        if (toolUses.Count > 0) stopReason = AgentTurnStopReason.ToolUse;

        return new AgentLlmTurnResult
        {
            AssistantText = text.Length > 0 ? text.ToString() : null,
            ToolUses = toolUses,
            StopReason = stopReason
        };
    }
}
