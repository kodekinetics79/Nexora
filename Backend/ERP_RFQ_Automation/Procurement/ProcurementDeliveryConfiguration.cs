using ERP_RFQ_Automation.Notifications;
using Microsoft.Extensions.Options;

namespace ERP_RFQ_Automation.Procurement;

public interface IProcurementDeliveryConfiguration
{
    bool IsConfigured { get; }
    string ProviderName { get; }
}

/// <summary>
/// Whether supplier RFQs can be dispatched at all.
///
/// <para><b>THIS IS PLATFORM-WIDE, NOT PER TENANT — see issue #54 before relying on it.</b>
/// It reads <see cref="NotificationsOptions"/>, one setting for the whole deployment, so every
/// tenant's supplier RFQs and customer quotes leave from the SAME address. Correct while there
/// is one customer. On the second, client B's quotes go out from client A's address: replies
/// land in the wrong inbox, SPF/DKIM/DMARC align to the wrong domain so deliverability drops
/// and it reads as spoofing, and one tenant's sending reputation damages every other
/// tenant's.</para>
///
/// <para>Note what this does NOT read: <c>EmailConfigurations</c>, the tenant-scoped mailbox
/// table behind <c>/setup/mailboxes</c> where each customer already enters their own SMTP
/// server and credentials. That table is used for IMAP polling and for the outbound-status
/// banner and nothing else — which is why that screen can say "quotes WILL be delivered
/// through smtpout.secureserver.net" while dispatch fails with
/// <c>DELIVERY_PROVIDER_NOT_CONFIGURED</c>. The screen a user can see is the one that does not
/// count.</para>
///
/// <para>The fix is to resolve the sender from the tenant that owns the solicitation and fall
/// back to this only when that tenant has no SMTP row, so system mail — password resets,
/// invitations — keeps working. Most of it exists: the table is tenant-scoped, the UI collects
/// it, and <c>TenantSmtpConcurrencyGate</c> shows per-tenant sending was anticipated. Only the
/// lookup is missing.</para>
/// </summary>
public sealed class ProcurementDeliveryConfiguration(IOptions<NotificationsOptions> options)
    : IProcurementDeliveryConfiguration
{
    private readonly string _provider = (options.Value.Provider ?? "console").Trim().ToLowerInvariant();

    public bool IsConfigured => _provider is "smtp" or "sendgrid";
    public string ProviderName => _provider;
}
