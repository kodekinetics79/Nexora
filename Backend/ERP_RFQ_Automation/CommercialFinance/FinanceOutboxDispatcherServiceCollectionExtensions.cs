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
            });
        services.AddHostedService<FinanceOutboxDispatcherService>();
        return services;
    }
}
