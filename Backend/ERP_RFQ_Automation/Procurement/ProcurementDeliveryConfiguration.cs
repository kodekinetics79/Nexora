using ERP_RFQ_Automation.Notifications;
using ERP_RFQ_Automation.Notifications.Runtime;
using Microsoft.Extensions.Options;

namespace ERP_RFQ_Automation.Procurement;

/// <summary>Whether a supplier RFQ for ONE tenant can be dispatched, and through whose identity.</summary>
/// <param name="IsConfigured">A transmitting sender resolves for the tenant.</param>
/// <param name="ProviderName">Protocol of that sender ("smtp", "sendgrid", "console").</param>
/// <param name="Origin">"tenant" when the tenant's own mailbox sends, "platform" / "configuration" otherwise.</param>
public sealed record ProcurementDeliveryReadiness(bool IsConfigured, string ProviderName, string Origin);

public interface IProcurementDeliveryConfiguration
{
    /// <summary>Platform-wide answer: whether the platform transport transmits at all.</summary>
    bool IsConfigured { get; }
    string ProviderName { get; }

    /// <summary>
    /// The answer for the tenant that owns the message. Default: the platform-wide answer, so
    /// fakes and harnesses written against the two properties keep their meaning.
    /// </summary>
    Task<ProcurementDeliveryReadiness> ResolveAsync(long businessUnitId, CancellationToken ct = default)
        => Task.FromResult(new ProcurementDeliveryReadiness(IsConfigured, ProviderName, "platform"));
}

/// <summary>
/// Whether supplier RFQs can be dispatched, per tenant (issue #54).
///
/// <para><see cref="ResolveAsync"/> asks <see cref="IOutboundSenderResolver"/> — the same
/// authority the quote sender and the tenant's mailbox screen consult — so a tenant whose own
/// SMTP mailbox is active dispatches even when the platform provider is <c>console</c>, and a
/// tenant with no mailbox dispatches through the platform transport when that transmits. The
/// worker used to read one platform-wide flag captured at construction, which is why the
/// mailbox screen could say "quotes WILL be delivered through smtpout.secureserver.net" while
/// dispatch dead-lettered <c>DELIVERY_PROVIDER_NOT_CONFIGURED</c>.</para>
///
/// <para><see cref="IsConfigured"/> and <see cref="ProviderName"/> keep the platform-wide
/// meaning, read LAZILY: the options object is the effective (stored-row) view, and capturing
/// it in the constructor froze the pre-warm-up appsettings value for the process lifetime.</para>
/// </summary>
public sealed class ProcurementDeliveryConfiguration(
    IOptions<NotificationsOptions> options,
    IOutboundSenderResolver? senders = null)
    : IProcurementDeliveryConfiguration
{
    private string Provider => (options.Value.Provider ?? "console").Trim().ToLowerInvariant();

    public bool IsConfigured => Provider is "smtp" or "sendgrid";
    public string ProviderName => Provider;

    public async Task<ProcurementDeliveryReadiness> ResolveAsync(long businessUnitId, CancellationToken ct = default)
    {
        if (senders is null)
            return new ProcurementDeliveryReadiness(IsConfigured, ProviderName, "platform");

        var sender = await senders.ResolveAsync(businessUnitId, ct).ConfigureAwait(false);
        return new ProcurementDeliveryReadiness(
            sender.TransmitsMail,
            sender.Provider,
            sender.Origin.ToString().ToLowerInvariant());
    }
}
