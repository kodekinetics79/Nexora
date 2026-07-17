namespace ERP_RFQ_Automation.Intelligence.Decision;

public static class DecisionServiceCollectionExtensions
{
    /// <summary>
    /// Registers the Lead Decision Brief intelligence. Splice into Program.cs as
    /// <c>builder.Services.AddLeadDecisionIntelligence();</c> (see DECISION-WIRING.md).
    ///
    /// The copilot tool (<see cref="LeadDecisionBriefTool"/>) is intentionally NOT
    /// registered here — the agent tool set is owned by
    /// Agent/AgentServiceCollectionExtensions.cs; the registration line for the
    /// lead to splice there is in DECISION-WIRING.md.
    /// </summary>
    public static IServiceCollection AddLeadDecisionIntelligence(this IServiceCollection services)
    {
        services.AddScoped<ILeadDecisionService, LeadDecisionService>();
        return services;
    }
}
