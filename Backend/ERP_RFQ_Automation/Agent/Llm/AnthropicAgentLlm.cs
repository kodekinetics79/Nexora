using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System.Diagnostics;
using ERP_RFQ_Automation.AI;

namespace ERP_RFQ_Automation.Agent.Llm;

/// <summary>
/// Real Claude Messages API client (https://api.anthropic.com/v1/messages) using
/// the tool-use API. One call == one turn; the orchestrator loops. Selected only
/// when <c>Agent:Anthropic:ApiKey</c> is non-empty (see AddAgentEngine); otherwise
/// <see cref="MockAgentLlm"/> is wired instead so the engine runs with no key.
/// </summary>
public sealed class AnthropicAgentLlm : IAgentLlm
{
    private const string AnthropicVersion = "2023-06-01";

    private readonly HttpClient _http;
    private readonly ILogger<AnthropicAgentLlm> _log;
    private readonly string _apiKey;
    private readonly string _model;
    private readonly int _maxTokens;
    private readonly IAiGovernanceService _governance;
    private readonly string _endpoint;

    /// <summary>
    /// The destination this client posts to, as the governance model names it.
    ///
    /// <para>The origin used to be a private const here and nowhere else, which is precisely
    /// why the agent's egress sat outside the endpoint model: an origin no descriptor names
    /// can never appear in an allow-list row, so it could never be authorized, expired or
    /// revoked, and the only remaining control was a free-text provider-NAME match. Deriving
    /// both the descriptor and the request URI from
    /// <see cref="AnthropicProviderDefaults"/> means the row an operator authorizes is
    /// provably the origin this client dials.</para>
    /// </summary>
    public AiProviderDescriptor ProviderDescriptor { get; }

    public AnthropicAgentLlm(
        HttpClient http, IConfiguration config, ILogger<AnthropicAgentLlm> log,
        IAiGovernanceService governance)
    {
        _http = http;
        _log = log;
        _apiKey = config["Agent:Anthropic:ApiKey"] ?? string.Empty;
        _model = AnthropicProviderDefaults.Model(config);
        _maxTokens = int.TryParse(config["Agent:Anthropic:MaxTokens"], out var mt) && mt > 0
            ? Math.Min(mt, 8192) : 2048;
        _governance = governance;
        _endpoint = AnthropicProviderDefaults.MessagesUrl(config);
        ProviderDescriptor = AiProviderEndpoint.Describe(
            AnthropicProviderDefaults.Provider, AnthropicProviderDefaults.BaseUrl(config), _model);
    }

    public async Task<AgentLlmTurnResult> RunTurnAsync(
        string systemPrompt,
        IReadOnlyList<AgentLlmMessage> history,
        IReadOnlyList<AgentToolDefinition> tools,
        AiCallContext callContext,
        CancellationToken ct)
    {
        var body = BuildRequestBody(systemPrompt, history, tools);
        // The reservation carries the destination's real class, so an agent turn is governed
        // as the egress it is. It was previously left at the AiCallContext default, which was
        // correct only by coincidence.
        var reservation = await _governance.ReserveAsync(
            callContext with { ProviderClass = ProviderDescriptor.ProviderClass },
            AnthropicProviderDefaults.Provider, _model, body,
            Encoding.UTF8.GetByteCount(body), _maxTokens, 1, ct);
        var started = DateTime.UtcNow;
        var stopwatch = Stopwatch.StartNew();
        var providerResponseReceived = false;
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, _endpoint)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };
            req.Headers.TryAddWithoutValidation("x-api-key", _apiKey);
            req.Headers.TryAddWithoutValidation("anthropic-version", AnthropicVersion);
            req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            var json = await resp.Content.ReadAsStringAsync(ct);
            providerResponseReceived = true;
            stopwatch.Stop();
            var (inputTokens, outputTokens, tokenSource) = ReadUsage(json, Encoding.UTF8.GetByteCount(body));
            var requestId = resp.Headers.TryGetValues("request-id", out var requestIds)
                ? requestIds.FirstOrDefault() : null;
            var success = resp.IsSuccessStatusCode;
            await _governance.RecordAttemptAsync(reservation, new AiAttemptCompletion(
                1, success ? AiCallStatuses.Succeeded : AiCallStatuses.Failed,
                (int)resp.StatusCode, requestId, inputTokens, outputTokens, tokenSource,
                stopwatch.ElapsedMilliseconds, null,
                string.IsNullOrEmpty(json) ? null : AiGovernanceService.Hash(json),
                success ? null : "provider_http_error", started, DateTime.UtcNow), CancellationToken.None);

            if (!success)
            {
                await _governance.CompleteAsync(reservation, AiCallStatuses.Failed,
                    inputTokens, outputTokens, tokenSource, null, "provider_http_error", CancellationToken.None);
                _log.LogWarning("Anthropic API returned {Status}.", (int)resp.StatusCode);
                return new AgentLlmTurnResult
                {
                    StopReason = AgentTurnStopReason.Error,
                    Error = $"Anthropic API error {(int)resp.StatusCode}."
                };
            }

            AgentLlmTurnResult result;
            try
            {
                result = ParseResponse(json);
            }
            catch (JsonException)
            {
                await _governance.CompleteAsync(reservation, AiCallStatuses.Failed,
                    inputTokens, outputTokens, tokenSource, null, "invalid_output", CancellationToken.None);
                return new AgentLlmTurnResult
                {
                    StopReason = AgentTurnStopReason.Error,
                    Error = "The provider returned an invalid response."
                };
            }
            await _governance.CompleteAsync(reservation, AiCallStatuses.Succeeded,
                inputTokens, outputTokens, tokenSource, json, null, CancellationToken.None);
            return result;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            if (providerResponseReceived)
                throw;
            stopwatch.Stop();
            var estimatedInput = AiGovernanceService.ConservativeTokenUpperBound(Encoding.UTF8.GetByteCount(body));
            await _governance.RecordAttemptAsync(reservation, new AiAttemptCompletion(
                1, AiCallStatuses.Unknown, null, null, estimatedInput, _maxTokens, AiTokenSources.Estimated,
                stopwatch.ElapsedMilliseconds, null, null, "provider_cancelled", started, DateTime.UtcNow),
                CancellationToken.None);
            await _governance.CompleteAsync(reservation, AiCallStatuses.Unknown,
                estimatedInput, _maxTokens, AiTokenSources.Estimated, null, "provider_cancelled", CancellationToken.None);
            throw;
        }
        catch (Exception ex)
        {
            if (providerResponseReceived)
                throw;
            stopwatch.Stop();
            var estimatedInput = AiGovernanceService.ConservativeTokenUpperBound(Encoding.UTF8.GetByteCount(body));
            await _governance.RecordAttemptAsync(reservation, new AiAttemptCompletion(
                1, AiCallStatuses.Unknown, null, null, estimatedInput, _maxTokens, AiTokenSources.Estimated,
                stopwatch.ElapsedMilliseconds, null, null, "provider_exception", started, DateTime.UtcNow),
                CancellationToken.None);
            await _governance.CompleteAsync(reservation, AiCallStatuses.Unknown,
                estimatedInput, _maxTokens, AiTokenSources.Estimated, null, "provider_exception", CancellationToken.None);
            _log.LogError(ex, "Anthropic API call failed.");
            return new AgentLlmTurnResult { StopReason = AgentTurnStopReason.Error, Error = "LLM call failed." };
        }
    }

    private static (long InputTokens, long OutputTokens, string TokenSource) ReadUsage(string json, int requestBytes)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("usage", out var usage)
                && usage.TryGetProperty("input_tokens", out var input)
                && usage.TryGetProperty("output_tokens", out var output)
                && input.TryGetInt64(out var inputTokens)
                && output.TryGetInt64(out var outputTokens))
                return (Math.Max(0, inputTokens), Math.Max(0, outputTokens), AiTokenSources.ProviderExact);
        }
        catch (JsonException) { }
        return (AiGovernanceService.ConservativeTokenUpperBound(requestBytes),
            string.IsNullOrEmpty(json)
                ? 0
                : AiGovernanceService.ConservativeTokenUpperBound(Encoding.UTF8.GetByteCount(json)),
            AiTokenSources.Estimated);
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
