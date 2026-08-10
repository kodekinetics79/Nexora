using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace ERP_RFQ_Automation.CommercialFinance;

public static class FinanceOutboxDispatcherServiceCollectionExtensions
{
    public static IServiceCollection AddCommercialFinanceOutboxDispatcher(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<FinanceOutboxDispatcherOptions>()
            .Bind(configuration.GetSection(FinanceOutboxDispatcherOptions.SectionName))
            .ValidateOnStart();
        return AddDispatcherServices(services);
    }

    public static IServiceCollection AddCommercialFinanceOutboxDispatcher(
        this IServiceCollection services,
        Action<FinanceOutboxDispatcherOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        services.AddOptions<FinanceOutboxDispatcherOptions>()
            .Configure(configure)
            .ValidateOnStart();
        return AddDispatcherServices(services);
    }

    private static IServiceCollection AddDispatcherServices(IServiceCollection services)
    {
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IValidateOptions<FinanceOutboxDispatcherOptions>,
                FinanceOutboxDispatcherOptionsValidator>());
        services.TryAddScoped<IFinanceOutboxStore, FinanceOutboxStore>();
        services.TryAddScoped<IFinanceEventPublisher, FinanceHttpEventPublisher>();
        services.AddHttpClient(FinanceHttpEventPublisher.HttpClientName, client =>
            {
                client.Timeout = Timeout.InfiniteTimeSpan;
                client.DefaultRequestHeaders.UserAgent.ParseAdd("Nexora-FinanceOutbox/1.0");
            })
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
            {
                AllowAutoRedirect = false
            })
            // SEC-G9: FinanceEventPublisher signs each envelope and sends the MAC as
            // X-Nexora-Signature. The factory's logging handlers write every header at Trace and
            // redact nothing by default, so a diagnostic session would have logged the signature
            // beside the exact payload it covers — the starting position for an offline attack on
            // the shared secret — and any Authorization header a future endpoint requires.
            .RedactLoggedHeaders(ERP_RFQ_Automation.Infrastructure.OutboundHttpRedaction.SensitiveHeaders);
        services.AddHostedService<FinanceOutboxDispatcherService>();
        return services;
    }
}
