using Microsoft.Extensions.DependencyInjection;

namespace ERP_RFQ_Automation.Intelligence.Conversion;

/// <summary>
/// One-call DI wiring for the Lead -&gt; RFQ conversion intelligence. The lead
/// splices a single <c>builder.Services.AddConversionIntelligence();</c> into
/// Program.cs (see CONVERSION-WIRING.md). The controller is auto-mapped; the two
/// copilot tools are registered inside AddAgentEngine via the splice lines in the
/// wiring doc so the agent tool set stays in one place.
/// </summary>
public static class ConversionIntelligenceServiceCollectionExtensions
{
    public static IServiceCollection AddConversionIntelligence(this IServiceCollection services)
    {
        services.AddScoped<ILeadConversionIntelligence, LeadConversionIntelligence>();
        return services;
    }
}
