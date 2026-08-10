using System.Collections.Generic;

namespace ERP_RFQ_Automation.Infrastructure;

/// <summary>
/// The header names every outbound <c>HttpClient</c> registration must redact from its own logs.
///
/// <para>WHY THIS IS A SHARED CONSTANT rather than a list repeated at each call site.
/// <c>IHttpClientFactory</c> installs two logging handlers around every client it builds, and
/// both write the COMPLETE request and response header collection at <c>Trace</c>.
/// <c>HttpClientFactoryOptions.ShouldRedactHeaderValue</c> is what suppresses a value, and its
/// framework default redacts nothing at all — so the protection is entirely opt-in, per client,
/// and a registration that forgets it is indistinguishable from one that has it until somebody
/// turns the log level up. Nexora dials four external providers with a credential in a header
/// (Ollama, Anthropic, SendGrid, the accounting export) plus a signed finance webhook; a single
/// diagnostic session at Trace would have written all five to the log sink. A list nobody can
/// forget to extend is worth more than five correct copies.</para>
///
/// <para>The set is deliberately broader than the five headers actually sent today. Redacting a
/// header a client never sends costs nothing, and the failure mode this exists to prevent is a
/// provider added later whose credential arrives under a name nobody updated the list for.</para>
/// </summary>
public static class OutboundHttpRedaction
{
    /// <summary>
    /// Matched case-insensitively by <c>RedactLoggedHeaders</c>.
    ///
    /// <list type="bullet">
    /// <item><c>Authorization</c> — Ollama (bearer provider key), SendGrid (bearer API key),
    /// the accounting export connector (bearer API key).</item>
    /// <item><c>x-api-key</c> / <c>api-key</c> / <c>X-Goog-Api-Key</c> — Anthropic sends the
    /// first; the others are the same credential under the names neighbouring providers use.</item>
    /// <item><c>Proxy-Authorization</c> — a credential the process never sets itself but a
    /// corporate proxy configuration can inject.</item>
    /// <item><c>Cookie</c> / <c>Set-Cookie</c> — session material on any provider that answers
    /// with one.</item>
    /// <item><c>X-Nexora-Signature</c> — the finance outbox HMAC. Not itself a secret, but it is
    /// a message authentication code over a payload, and logging MACs beside their payloads is
    /// how an offline attack on the signing key starts.</item>
    /// </list>
    /// </summary>
    public static readonly IReadOnlyList<string> SensitiveHeaders =
    [
        "Authorization",
        "Proxy-Authorization",
        "x-api-key",
        "api-key",
        "X-Goog-Api-Key",
        "Cookie",
        "Set-Cookie",
        "X-Nexora-Signature"
    ];
}
