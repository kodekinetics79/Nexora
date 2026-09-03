using System.Text.Json;
using ERP_RFQ_Automation.AI;
using ERP_RFQ_Automation.Platform.Entitlements;
using ERP_RFQ_Automation.Platform.Hardening;
using ERP_RFQ_Automation.Platform.Lifecycle;
using ERP_RFQ_Automation.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Console;
using Microsoft.Extensions.Options;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// Three observability defects with one shape: the log could not be tied to a request, and the
/// lines that WERE there were the ones nobody needed. Scopes (correlation id, tenant) never
/// rendered because the simple console formatter drops them; the global 500 body gave a user
/// nothing to quote back; and three per-minute lines drowned the rest.
/// </summary>
public sealed class ObservabilityHardeningTests
{
    // ---- the 500 body names the correlation id ------------------------------------------------

    [Fact]
    public async Task The_global_500_body_carries_the_inbound_correlation_id_and_echoes_it_on_the_header()
    {
        var context = new DefaultHttpContext();
        context.Request.Headers[TenantLoggingMiddleware.CorrelationHeader] = "corr-7f3a";
        context.Response.Body = new MemoryStream();

        await GlobalExceptionResponse.WriteAsync(context);

        Assert.Equal(StatusCodes.Status500InternalServerError, context.Response.StatusCode);
        Assert.Equal("application/json", context.Response.ContentType);
        Assert.Equal("corr-7f3a", context.Response.Headers[TenantLoggingMiddleware.CorrelationHeader].ToString());

        context.Response.Body.Position = 0;
        using var body = await JsonDocument.ParseAsync(context.Response.Body);
        Assert.Equal(GlobalExceptionResponse.Message, body.RootElement.GetProperty("error").GetString());
        Assert.Equal("corr-7f3a", body.RootElement.GetProperty("correlationId").GetString());
        // Still nothing but those two fields: no exception detail leaves the process.
        Assert.Equal(2, body.RootElement.EnumerateObject().Count());
    }

    [Fact]
    public async Task Without_an_inbound_header_the_500_body_names_the_trace_identifier()
    {
        var context = new DefaultHttpContext { TraceIdentifier = "0HN5-TRACE" };
        context.Response.Body = new MemoryStream();

        await GlobalExceptionResponse.WriteAsync(context);

        context.Response.Body.Position = 0;
        using var body = await JsonDocument.ParseAsync(context.Response.Body);
        Assert.Equal("0HN5-TRACE", body.RootElement.GetProperty("correlationId").GetString());
        Assert.Equal("0HN5-TRACE", context.Response.Headers[TenantLoggingMiddleware.CorrelationHeader].ToString());
    }

    // ---- the console formatter renders scopes -------------------------------------------------

    [Fact]
    public void Outside_development_the_console_logger_is_json_with_scopes()
    {
        using var provider = new ServiceCollection()
            .AddLogging(logging => logging.AddNexoraConsole(isDevelopment: false))
            .BuildServiceProvider();

        Assert.Equal(ConsoleFormatterNames.Json,
            provider.GetRequiredService<IOptions<ConsoleLoggerOptions>>().Value.FormatterName);
        var json = provider.GetRequiredService<IOptions<JsonConsoleFormatterOptions>>().Value;
        Assert.True(json.IncludeScopes, "scopes (CorrelationId, TenantId, RequestPath) must reach the log line");
        Assert.True(json.UseUtcTimestamp);
    }

    [Fact]
    public void In_development_the_console_logger_stays_readable_but_still_renders_scopes()
    {
        using var provider = new ServiceCollection()
            .AddLogging(logging => logging.AddNexoraConsole(isDevelopment: true))
            .BuildServiceProvider();

        Assert.Equal(ConsoleFormatterNames.Simple,
            provider.GetRequiredService<IOptions<ConsoleLoggerOptions>>().Value.FormatterName);
        Assert.True(provider.GetRequiredService<IOptions<SimpleConsoleFormatterOptions>>().Value.IncludeScopes);
    }

    // ---- the per-minute lines are said once ---------------------------------------------------

    [Fact]
    public async Task An_orphan_business_unit_is_warned_about_once_per_process_and_then_at_debug()
    {
        // A fresh id per run: the announcement set is process-wide by design.
        var orphan = 700_000 + Random.Shared.Next(1, 100_000);
        var logger = new CapturingLogger<MailboxTenantWorkGate>();
        var gate = new MailboxTenantWorkGate(new OrphanAccess(), logger);

        for (var poll = 0; poll < 3; poll++)
            Assert.Empty(await gate.FilterPollableAsync([orphan]));

        var lines = logger.Entries.Where(entry => entry.Message.Contains($"orphan business unit {orphan}")).ToList();
        Assert.Equal(3, lines.Count);
        Assert.Equal(LogLevel.Warning, lines[0].Level);
        Assert.All(lines.Skip(1), entry => Assert.Equal(LogLevel.Debug, entry.Level));
    }

    [Fact]
    public void The_external_llm_provider_is_announced_once_per_process_and_then_at_debug()
    {
        OllamaLlmService.ResetExternalProviderAnnouncementForTests();
        var first = new CapturingLogger<OllamaLlmService>();
        var second = new CapturingLogger<OllamaLlmService>();

        // Two constructions, as two scoped resolutions in one process would produce.
        _ = ExternalOllama(first);
        _ = ExternalOllama(second);

        var announcedFirst = Assert.Single(first.Entries, entry => entry.Message.Contains("EXTERNAL provider"));
        Assert.Equal(LogLevel.Warning, announcedFirst.Level);
        var announcedAgain = Assert.Single(second.Entries, entry => entry.Message.Contains("EXTERNAL provider"));
        Assert.Equal(LogLevel.Debug, announcedAgain.Level);
    }

    private static OllamaLlmService ExternalOllama(ILogger<OllamaLlmService> logger)
        => new(new HttpClient(new HttpClientHandler()), logger,
            new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Ollama:BaseUrl"] = "https://ollama.example.com/",
                ["Ollama:Model"] = "test-model",
                ["Ollama:ApiKey"] = "test-key"
            }).Build(),
            new NoGovernance());

    // ---- support -------------------------------------------------------------------------------

    private sealed record LogEntry(LogLevel Level, string Message);

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<LogEntry> Entries { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
            => Entries.Add(new LogEntry(logLevel, formatter(state, exception)));
    }

    /// <summary>The platform plane answered, and no tenant owns the unit — an orphan.</summary>
    private sealed class OrphanAccess : ITenantAccessService
    {
        public Task<TenantAccessSnapshot> GetAccessAsync(long businessUnitId, CancellationToken ct = default)
            => Task.FromResult(new TenantAccessSnapshot(businessUnitId, null, null, null));
    }

    private sealed class NoGovernance : IAiGovernanceService
    {
        public Task<AiReservation> ReserveAsync(AiCallContext context, string provider, string model, string input,
            int maximumInputBytes, int maximumOutputTokens, int maximumAttempts, CancellationToken ct)
            => throw new NotSupportedException("The constructor is all this test exercises.");
        public Task RecordAttemptAsync(AiReservation reservation, AiAttemptCompletion attempt, CancellationToken ct)
            => Task.CompletedTask;
        public Task CompleteAsync(AiReservation reservation, string status, long inputTokens, long outputTokens,
            string tokenSource, string? output, string? errorCode, CancellationToken ct)
            => Task.CompletedTask;
    }
}
