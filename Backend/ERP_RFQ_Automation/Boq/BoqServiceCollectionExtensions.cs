using Microsoft.Extensions.DependencyInjection.Extensions;

namespace ERP_RFQ_Automation.Boq;

public static class BoqServiceCollectionExtensions
{
    /// <summary>
    /// Registers the Service RFQ → BOQ engine. Splice into Program.cs as
    /// <c>builder.Services.AddBoqEngine();</c> (see BOQ-WIRING.md).
    ///
    /// The vision seam uses TryAddScoped: registering a real
    /// <see cref="IVisionDocumentReader"/> (e.g. AnthropicVisionReader) BEFORE this
    /// call swaps out the honest placeholder with zero other changes.
    ///
    /// The two copilot tools (<see cref="DraftBoqTool"/>, <see cref="GetBoqTool"/>)
    /// are intentionally NOT registered here — the agent tool set is owned by
    /// Agent/AgentServiceCollectionExtensions.cs; the registration lines for the
    /// lead to splice there are in BOQ-WIRING.md (same convention as pricing).
    /// </summary>
    public static IServiceCollection AddBoqEngine(this IServiceCollection services)
    {
        services.AddScoped<IBoqBuilderService, BoqBuilderService>();
        services.TryAddScoped<IVisionDocumentReader, NotConfiguredVisionReader>();
        return services;
    }
}
