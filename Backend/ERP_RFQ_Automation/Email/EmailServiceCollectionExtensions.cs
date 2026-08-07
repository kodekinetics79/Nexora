namespace ERP_RFQ_Automation.Email;

/// <summary>
/// Single entry point for the email provider-and-connection module, matching the shape
/// <c>AddNotifications</c> and <c>AddPlatformEmailSettings</c> already use.
///
/// <para>Registered independently of both planes on purpose: the catalogue and the connection
/// tester are consumed by the tenant mailbox surface AND the platform console, and binding them to
/// either one would put the other's dependency in the middle of a module that has none.</para>
/// </summary>
public static class EmailServiceCollectionExtensions
{
    /// <summary>
    /// Must be registered after <c>IMailboxConnectionProbe</c>, which the tester delegates the
    /// staged mail-protocol diagnosis to rather than reimplementing it — a second implementation
    /// would mean a second interpretation of the <c>UseSsl</c> flag, and the probe's whole value is
    /// that there is only one.
    /// </summary>
    public static IServiceCollection AddEmailProviders(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddSingleton<IMailConnectionTester, MailConnectionTester>();
        return services;
    }
}
