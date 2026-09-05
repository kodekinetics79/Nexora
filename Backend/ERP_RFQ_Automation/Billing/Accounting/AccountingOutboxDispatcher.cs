using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace ERP_RFQ_Automation.Billing.Accounting;

public sealed class AccountingExportOptions
{
    public const string SectionName = "PlatformBilling:AccountingExport";
    public bool Enabled { get; set; }
    public string Endpoint { get; set; } = string.Empty;
    public string ApiKey { get; set; } = string.Empty;
    public string WorkerId { get; set; } = $"accounting-{Environment.MachineName}";
    public int PollSeconds { get; set; } = 10;
}

public sealed record AccountingExportReceipt(string ExternalReference, string ReceiptSha256);
public interface IAccountingExportConnector
{
    Task<AccountingExportReceipt> ExportAsync(AccountingOutboxMessage message, CancellationToken ct);
}

public sealed class HttpAccountingExportConnector(
    IHttpClientFactory clients, IOptions<AccountingExportOptions> options) : IAccountingExportConnector
{
    public async Task<AccountingExportReceipt> ExportAsync(AccountingOutboxMessage message, CancellationToken ct)
    {
        var settings = options.Value;
        if (!settings.Enabled || !Uri.TryCreate(settings.Endpoint, UriKind.Absolute, out var endpoint)
            || endpoint.Scheme != Uri.UriSchemeHttps || string.IsNullOrWhiteSpace(settings.ApiKey))
            throw new AccountingOutboxException("Accounting connector is not configured with HTTPS endpoint and credentials.");
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new StringContent(message.PayloadJson, Encoding.UTF8, "application/json")
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", settings.ApiKey);
        request.Headers.TryAddWithoutValidation("Idempotency-Key", message.IdempotencyKey);
        request.Headers.TryAddWithoutValidation("X-Nexora-Payload-SHA256", message.PayloadSha256);
        using var response = await clients.CreateClient(nameof(HttpAccountingExportConnector))
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        var bounded = await response.Content.ReadAsByteArrayAsync(ct);
        if (bounded.Length > 64 * 1024) throw new AccountingOutboxException("Accounting receipt exceeds 64 KiB.");
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"Accounting provider returned HTTP {(int)response.StatusCode}.");
        var external = response.Headers.TryGetValues("X-Accounting-Reference", out var references)
            ? references.FirstOrDefault() : null;
        if (string.IsNullOrWhiteSpace(external))
        {
            using var document = JsonDocument.Parse(bounded);
            external = document.RootElement.TryGetProperty("reference", out var value) ? value.GetString() : null;
        }
        if (string.IsNullOrWhiteSpace(external) || external.Length > 256)
            throw new AccountingOutboxException("Accounting provider did not return a bounded external reference.");
        return new(external, Convert.ToHexString(SHA256.HashData(bounded)).ToLowerInvariant());
    }
}

public sealed class AccountingOutboxDispatcher : BackgroundService
{
    private readonly IServiceScopeFactory scopes;
    private readonly IAccountingExportConnector connector;
    private readonly IOptions<AccountingExportOptions> options;
    private readonly ILogger<AccountingOutboxDispatcher> log;
    private readonly ERP_RFQ_Automation.HealthChecks.IBackgroundWorkerHeartbeats? _heartbeats;

    public AccountingOutboxDispatcher(
        IServiceScopeFactory scopes, IAccountingExportConnector connector,
        IOptions<AccountingExportOptions> options, ILogger<AccountingOutboxDispatcher> log,
        ERP_RFQ_Automation.HealthChecks.IBackgroundWorkerHeartbeats? heartbeats = null)
    {
        this.scopes = scopes;
        this.connector = connector;
        this.options = options;
        this.log = log;
        _heartbeats = heartbeats;
        // The loop turns whether or not export is enabled (a disabled cycle is just the delay),
        // so liveness is meaningful either way.
        _heartbeats?.Register(
            ERP_RFQ_Automation.HealthChecks.BackgroundWorkerNames.AccountingOutbox, Period(options.Value));
    }

    private static TimeSpan Period(AccountingExportOptions settings)
        => TimeSpan.FromSeconds(Math.Clamp(settings.PollSeconds, 2, 300));

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _heartbeats?.Beat(ERP_RFQ_Automation.HealthChecks.BackgroundWorkerNames.AccountingOutbox, Period(options.Value));
        while (!stoppingToken.IsCancellationRequested)
        {
            var settings = options.Value;
            if (!settings.Enabled)
            {
                _heartbeats?.Beat(ERP_RFQ_Automation.HealthChecks.BackgroundWorkerNames.AccountingOutbox, Period(settings));
                await Task.Delay(Period(settings), stoppingToken);
                continue;
            }
            try
            {
                using var scope = scopes.CreateScope();
                var outbox = scope.ServiceProvider.GetRequiredService<AccountingOutboxService>();
                var claimed = await outbox.ClaimAsync(settings.WorkerId, 25, ct: stoppingToken);
                foreach (var message in claimed)
                {
                    try
                    {
                        var receipt = await connector.ExportAsync(message, stoppingToken);
                        await outbox.AcknowledgeAsync(message.Id, message.LeaseToken!.Value,
                            receipt.ExternalReference, receipt.ReceiptSha256, settings.WorkerId, stoppingToken);
                    }
                    catch (Exception exception) when (exception is not OperationCanceledException)
                    {
                        var code = exception is HttpRequestException ? "PROVIDER_HTTP_FAILURE" : "CONNECTOR_FAILURE";
                        await outbox.FailAsync(message.Id, message.LeaseToken!.Value, code,
                            TimeSpan.FromMinutes(Math.Min(60, Math.Pow(2, message.AttemptCount))), stoppingToken);
                        log.LogWarning("Accounting export failed for message {MessageId} with {FailureCode}.", message.Id, code);
                    }
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                log.LogError(exception, "Accounting outbox dispatcher cycle failed.");
            }
            _heartbeats?.Beat(ERP_RFQ_Automation.HealthChecks.BackgroundWorkerNames.AccountingOutbox, Period(settings));
            await Task.Delay(Period(settings), stoppingToken);
        }
    }
}
