namespace ERP_RFQ_Automation.Intelligence.Pricing;

public static class PricingServiceCollectionExtensions
{
    /// <summary>
    /// Registers the Pricing Intelligence Engine. Splice into Program.cs as
    /// <c>builder.Services.AddPricingIntelligence();</c> (see PRICING-WIRING.md).
    ///
    /// The two copilot tools (<see cref="PriceRfqTool"/>, <see cref="ApplyRfqPricingTool"/>)
    /// are intentionally NOT registered here — the agent tool set is owned by
    /// Agent/AgentServiceCollectionExtensions.cs; the registration lines for the lead to
    /// splice there are in PRICING-WIRING.md.
    /// </summary>
    public static IServiceCollection AddPricingIntelligence(this IServiceCollection services)
    {
        services.AddScoped<IPricingEngine, PricingEngine>();
        return services;
    }
}
