using System.Net;
using System.Net.Sockets;
using ERP_RFQ_Automation.Security;

namespace ERP_RFQ_Automation.AI;

/// <summary>
/// The connect-time half of AI endpoint classification.
///
/// <para><b>Why classification at startup is not enough.</b>
/// <see cref="AiProviderEndpoint.Describe(string,string?,string?,IAiEndpointHostResolver?)"/>
/// decides Local/External once, from configuration. Everything downstream — the "no
/// third-party egress" line in the deployment log, the allow-list, the ledger's
/// <c>ProviderClass</c> column — trusts that verdict for the life of the process. Nothing
/// re-checked it when a request was actually sent, which left two ways for a call to leave
/// the box that the verdict said would never leave it:</para>
/// <list type="number">
/// <item>a name classified Local at startup answering with an off-box address later — a
/// short TTL, a hosts change, or a deliberate rebinding response; and</item>
/// <item>a 307/308 from the loopback service, which <see cref="HttpClient"/> follows by
/// default and which re-sends the method AND the body to whatever host the redirect names.
/// The document text is already in that body.</item>
/// </list>
///
/// <para>This handler closes both. Redirects are not followed at all — an inference API has
/// no legitimate need to redirect, and the finance outbox dispatcher already made exactly
/// this call for exactly this reason. And every connection is opened through the same
/// resolve-then-connect discipline the mail policy uses: the name is resolved at connect
/// time, EVERY address it returns must satisfy the rule for the class this endpoint was
/// classified as, and the socket is connected to one of the addresses that was actually
/// validated — so there is no window between the check and the dial.</para>
///
/// <para>The rule is the mirror image on each side, and both directions are enforced:
/// a LOCAL endpoint must reach loopback and nothing else (that is the guarantee), and an
/// EXTERNAL endpoint must reach a publicly routable address and nothing else (so an
/// authorized third-party origin cannot be rebound onto instance metadata or an RFC 1918
/// host and turned into an SSRF primitive against this server).</para>
/// </summary>
public static class AiEgressGuard
{
    /// <summary>Refusal surfaced when a resolved address does not match the classification.</summary>
    public const string LocalEndpointLeftTheHostMessage =
        "The AI endpoint is classified LOCAL but resolves to an address that is not on this machine.";

    public const string ExternalEndpointIsNotPublicMessage =
        "The AI endpoint is classified EXTERNAL but resolves to an address that is not publicly routable.";

    /// <summary>
    /// Resolves and connects under the rule for <paramref name="classification"/>. The
    /// caller owns the returned socket.
    /// </summary>
    public static Task<Socket> ConnectAsync(
        AiProviderClass classification, string host, int port, CancellationToken cancellationToken) =>
        classification == AiProviderClass.Local
            ? MailEndpointPolicy.ConnectAsync(
                host, port, MailEndpointPolicy.IsLoopbackAddress,
                LocalEndpointLeftTheHostMessage, cancellationToken)
            : MailEndpointPolicy.ConnectAsync(
                host, port, MailEndpointPolicy.IsPublicAddress,
                ExternalEndpointIsNotPublicMessage, cancellationToken);

    /// <summary>
    /// The primary handler every AI HTTP client is built on.
    /// </summary>
    /// <param name="classification">
    /// Read per connection rather than captured, so a resolver that is constructed after the
    /// handler still governs it, and so the handler can never be holding a stale verdict.
    /// </param>
    public static SocketsHttpHandler CreateHandler(Func<AiProviderClass> classification)
    {
        ArgumentNullException.ThrowIfNull(classification);
        return new SocketsHttpHandler
        {
            // A redirect re-sends the method and the body. For an inference client the body
            // IS the customer's document text, so following one is an egress the endpoint
            // classification never saw and no allow-list row ever authorized.
            AllowAutoRedirect = false,
            ConnectCallback = async (context, cancellationToken) =>
            {
                var socket = await ConnectAsync(
                    classification(), context.DnsEndPoint.Host, context.DnsEndPoint.Port, cancellationToken);
                try
                {
                    socket.NoDelay = true;
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch
                {
                    socket.Dispose();
                    throw;
                }
            }
        };
    }
}
