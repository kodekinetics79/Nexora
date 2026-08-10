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

        // WP-B3 below-floor control: detection + hold creation reused by the
        // pricing controller and QuoteService's send path. Registered here (this
        // extension is already spliced into Program.cs) so no new splice is needed.
        // The ApproveBelowFloorQuoteTool executor is NOT registered here — same
        // convention as the other pricing tools; line lives in Sla/WAVEB-WIRING.md.
        services.AddScoped<IBelowFloorGuard, BelowFloorGuard>();

        // R5 price-provenance attestation (Decision Register R5). QuoteService constructs
        // its own instance for the send gate so the gate cannot be omitted by construction;
        // this registration serves QuoteController's read/confirm endpoints.
        services.AddScoped<IPriceAttestationService, PriceAttestationService>();

        return services;
    }
}
