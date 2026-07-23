using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace ERP_RFQ_Automation.CommercialFinance;

public interface IFinanceEventPublisher
{
    Task PublishAsync(FinanceOutboxEnvelope envelope, CancellationToken cancellationToken);
}

public sealed class FinanceEventPublishException : HttpRequestException
{
    public FinanceEventPublishException(HttpStatusCode statusCode, string message)
        : base(message, null, statusCode)
    {
    }
}

public sealed class FinanceHttpEventPublisher : IFinanceEventPublisher
{
    internal const string HttpClientName = "CommercialFinanceOutboxDispatcher";
    private const int MaximumErrorBodyLength = 1000;
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IOptionsMonitor<FinanceOutboxDispatcherOptions> _options;
    private readonly IHostEnvironment _environment;

    public FinanceHttpEventPublisher(
        IHttpClientFactory httpClientFactory,
        IOptionsMonitor<FinanceOutboxDispatcherOptions> options,
        IHostEnvironment environment)
    {
        _httpClientFactory = httpClientFactory;
        _options = options;
        _environment = environment;
    }

    public async Task PublishAsync(FinanceOutboxEnvelope envelope, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        var options = _options.CurrentValue;
        var endpoint = GetEndpoint(options);
        var body = SerializeEnvelope(envelope);
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);

        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new ByteArrayContent(body)
        };
        request.Content.Headers.ContentType = new("application/json") { CharSet = "utf-8" };
        request.Headers.TryAddWithoutValidation("Idempotency-Key", envelope.EventId.ToString("D"));
        request.Headers.TryAddWithoutValidation("X-Nexora-Event-Id", envelope.EventId.ToString("D"));
        request.Headers.TryAddWithoutValidation("X-Nexora-Event-Type", envelope.EventType);
        request.Headers.TryAddWithoutValidation("X-Nexora-Schema-Version",
            envelope.SchemaVersion.ToString(CultureInfo.InvariantCulture));
        request.Headers.TryAddWithoutValidation("X-Nexora-Signature-Timestamp", timestamp);

        if (!string.IsNullOrEmpty(options.HmacSecret))
        {
            var signature = CreateSignature(options.HmacSecret, timestamp, body);
            request.Headers.TryAddWithoutValidation("X-Nexora-Signature", $"sha256={signature}");
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(options.RequestTimeout);

        HttpResponseMessage response;
        try
        {
            response = await _httpClientFactory.CreateClient(HttpClientName)
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"Finance event {envelope.EventId:D} exceeded the {options.RequestTimeout} HTTP timeout.");
        }

        using (response)
        {
            if (response.IsSuccessStatusCode)
                return;

            var detail = await ReadBoundedErrorAsync(response, cancellationToken);
            throw new FinanceEventPublishException(
                response.StatusCode,
                $"Finance event {envelope.EventId:D} was rejected with HTTP {(int)response.StatusCode}"
                + (detail.Length == 0 ? "." : $": {detail}"));
        }
    }

    private Uri GetEndpoint(FinanceOutboxDispatcherOptions options)
    {
        if (!Uri.TryCreate(options.Endpoint, UriKind.Absolute, out var endpoint)
            || (endpoint.Scheme != Uri.UriSchemeHttps && endpoint.Scheme != Uri.UriSchemeHttp))
        {
            throw new InvalidOperationException("A valid finance outbox HTTP endpoint is required.");
        }

        if (_environment.IsProduction() && endpoint.Scheme != Uri.UriSchemeHttps)
            throw new InvalidOperationException("The finance outbox endpoint must use HTTPS in production.");

        return endpoint;
    }

    private static byte[] SerializeEnvelope(FinanceOutboxEnvelope envelope)
    {
        using var payload = JsonDocument.Parse(envelope.Payload);
        return JsonSerializer.SerializeToUtf8Bytes(new
        {
            envelope.EventId,
            envelope.EventType,
            envelope.SchemaVersion,
            envelope.OccurredOn,
            envelope.BusinessUnitId,
            Aggregate = new
            {
                Type = envelope.AggregateType,
                Id = envelope.AggregateId,
                Version = envelope.AggregateVersion
            },
            Payload = payload.RootElement
        }, SerializerOptions);
    }

    private static string CreateSignature(string secret, string timestamp, byte[] body)
    {
        var timestampBytes = Encoding.UTF8.GetBytes(timestamp + ".");
        var signedContent = new byte[timestampBytes.Length + body.Length];
        Buffer.BlockCopy(timestampBytes, 0, signedContent, 0, timestampBytes.Length);
        Buffer.BlockCopy(body, 0, signedContent, timestampBytes.Length, body.Length);

        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        return Convert.ToHexString(hmac.ComputeHash(signedContent)).ToLowerInvariant();
    }

    private static async Task<string> ReadBoundedErrorAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var buffer = new byte[4096];
        var bytesRead = await stream.ReadAsync(buffer, cancellationToken);
        var content = Encoding.UTF8.GetString(buffer, 0, bytesRead);
        content = content.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return content.Length <= MaximumErrorBodyLength
            ? content
            : content[..MaximumErrorBodyLength];
    }
}
