using System.Net;
using System.Text;
using ERP_RFQ_Automation.AI;
using ERP_RFQ_Automation.Agent.Llm;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace ERP_RFQ_Automation.Tests;

public sealed class AnthropicGovernanceTests
{
    [Fact]
    public async Task AgentTurn_ReservesAndRecordsExactProviderUsage()
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""
                {"content":[{"type":"text","text":"done"}],"stop_reason":"end_turn",
                 "usage":{"input_tokens":42,"output_tokens":7}}
                """, Encoding.UTF8, "application/json")
        };
        response.Headers.Add("request-id", "anthropic-request-1");
        var governance = new CapturingGovernance();
        var service = new AnthropicAgentLlm(
            new HttpClient(new Handler(response)),
            new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Agent:Anthropic:ApiKey"] = "test-key",
                ["Agent:Anthropic:Model"] = "test-model",
                ["Agent:Anthropic:MaxTokens"] = "512"
            }).Build(),
            NullLogger<AnthropicAgentLlm>.Instance,
            governance);

        var result = await service.RunTurnAsync("system", [AgentLlmMessage.User("hello")], [],
            new AiCallContext(9, AiPurposes.Agent, "agent-call-1", "agent-v1"), default);

        Assert.Equal("done", result.AssistantText);
        var attempt = Assert.Single(governance.Attempts);
        Assert.Equal(42, attempt.InputTokens);
        Assert.Equal(7, attempt.OutputTokens);
        Assert.Equal("anthropic-request-1", attempt.ProviderRequestId);
        Assert.Equal(AiTokenSources.ProviderExact, attempt.TokenSource);
        Assert.Equal(AiCallStatuses.Succeeded, governance.CompletedStatus);
    }

    [Fact]
    public async Task AgentTurnWithoutUsage_UsesCompleteRequestAndResponseUpperBounds()
    {
        const string providerJson = """
            {"content":[{"type":"text","text":"done"}],"stop_reason":"end_turn"}
            """;
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(providerJson, Encoding.UTF8, "application/json")
        };
        var governance = new CapturingGovernance();
        var handler = new RecordingHandler(response);
        var service = new AnthropicAgentLlm(
            new HttpClient(handler),
            new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Agent:Anthropic:ApiKey"] = "test-key",
                ["Agent:Anthropic:Model"] = "test-model",
                ["Agent:Anthropic:MaxTokens"] = "512"
            }).Build(),
            NullLogger<AnthropicAgentLlm>.Instance,
            governance);

        await service.RunTurnAsync("system", [AgentLlmMessage.User("hello")], [],
            new AiCallContext(9, AiPurposes.Agent, "agent-call-fallback", "agent-v1"), default);

        var attempt = Assert.Single(governance.Attempts);
        Assert.Equal(Encoding.UTF8.GetByteCount(handler.RequestBody!), attempt.InputTokens);
        Assert.Equal(Encoding.UTF8.GetByteCount(providerJson), attempt.OutputTokens);
        Assert.Equal(AiTokenSources.Estimated, attempt.TokenSource);
        Assert.Equal(attempt.InputTokens, governance.CompletedInputTokens);
        Assert.Equal(attempt.OutputTokens, governance.CompletedOutputTokens);
    }

    private sealed class Handler(HttpResponseMessage response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => Task.FromResult(response);
    }

    private sealed class RecordingHandler(HttpResponseMessage response) : HttpMessageHandler
    {
        public string? RequestBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            RequestBody = await request.Content!.ReadAsStringAsync(ct);
            return response;
        }
    }

    private sealed class CapturingGovernance : IAiGovernanceService
    {
        public List<AiAttemptCompletion> Attempts { get; } = [];
        public string? CompletedStatus { get; private set; }
        public long? CompletedInputTokens { get; private set; }
        public long? CompletedOutputTokens { get; private set; }

        public Task<AiReservation> ReserveAsync(AiCallContext context, string provider, string model,
            string input, int maximumInputBytes, int maximumOutputTokens, int maximumAttempts, CancellationToken ct)
            => Task.FromResult(new AiReservation(Guid.NewGuid(), context.BusinessUnitId,
                maximumOutputTokens, AiGovernanceService.EstimateTokens(input.Length), 1));

        public Task RecordAttemptAsync(AiReservation reservation, AiAttemptCompletion attempt, CancellationToken ct)
        {
            Attempts.Add(attempt);
            return Task.CompletedTask;
        }

        public Task CompleteAsync(AiReservation reservation, string status, long inputTokens,
            long outputTokens, string tokenSource, string? output, string? errorCode, CancellationToken ct)
        {
            CompletedStatus = status;
            CompletedInputTokens = inputTokens;
            CompletedOutputTokens = outputTokens;
            return Task.CompletedTask;
        }
    }
}
