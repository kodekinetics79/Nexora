using ERP_RFQ_Automation.Notifications;
using Microsoft.Extensions.Options;

namespace ERP_RFQ_Automation.Procurement;

public interface IProcurementDeliveryConfiguration
{
    bool IsConfigured { get; }
    string ProviderName { get; }
}

public sealed class ProcurementDeliveryConfiguration(IOptions<NotificationsOptions> options)
    : IProcurementDeliveryConfiguration
{
    private readonly string _provider = (options.Value.Provider ?? "console").Trim().ToLowerInvariant();

    public bool IsConfigured => _provider is "smtp" or "sendgrid";
    public string ProviderName => _provider;
}
