using ERP_RFQ_Automation.AI;
using ERP_RFQ_Automation.Agent.Guardrails;
using ERP_RFQ_Automation.Agent.Llm;
using ERP_RFQ_Automation.Agent.Tools;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace ERP_RFQ_Automation.Agent;

/// <summary>
/// One-call DI wiring for the sourcing-copilot engine. The lead splices a single
/// <c>builder.Services.AddAgentEngine(builder.Configuration);</c> into Program.cs
/// (see Agent/AGENT-WIRING.md). Endpoints are exposed via the auto-mapped
/// <c>AgentController</c>, so no extra MapControllers wiring is needed.
/// </summary>
public static class AgentServiceCollectionExtensions
{
    public static IServiceCollection AddAgentEngine(this IServiceCollection services, IConfiguration configuration)
    {
        // ---- LLM: real Claude when a key is present, deterministic mock otherwise ----
        var apiKey = configuration["Agent:Anthropic:ApiKey"];
        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            // The agent posts the whole conversation to this origin, so it is classified by
            // the same rules as every other AI destination and its socket is opened through
            // the same guard: no redirects (a 307 would re-send the conversation to whatever
            // host it named), and the resolved address must match the classification at
            // connect time, not merely at startup.
            var anthropic = AiProviderEndpoint.Describe(
                AnthropicProviderDefaults.Provider,
                AnthropicProviderDefaults.BaseUrl(configuration),
                AnthropicProviderDefaults.Model(configuration));
            services.AddHttpClient<IAgentLlm, AnthropicAgentLlm>(c => c.Timeout = TimeSpan.FromSeconds(120))
                .ConfigurePrimaryHttpMessageHandler(() =>
                    AiEgressGuard.CreateHandler(() => anthropic.ProviderClass));
        }
        else
        {
            services.AddSingleton<IAgentLlm, MockAgentLlm>();
        }

        // ---- Tools (registered as IAgentTool; discovered by the registry) ----
        services.AddScoped<IAgentTool, SearchRfqsTool>();
        services.AddScoped<IAgentTool, GetRfqTool>();
        services.AddScoped<IAgentTool, SearchSuppliersTool>();
        services.AddScoped<IAgentTool, SearchLeadsTool>();
        services.AddScoped<IAgentTool, SearchQuotesTool>();
        services.AddScoped<IAgentTool, SearchOrdersTool>();
        services.AddScoped<IAgentTool, GetDashboardSummaryTool>();
        services.AddScoped<IAgentTool, DispatchRfqToSupplierTool>();
        services.AddScoped<IAgentTool, RecommendAwardTool>();
        services.AddScoped<IAgentTool, CreateOrderFromQuoteTool>();

        // ---- Sourcing-loop tools (dispatch → capture → compare → award) ----
        services.AddScoped<IAgentTool, SendRfqToSuppliersTool>();
        services.AddScoped<IAgentTool, ListSolicitationsTool>();
        services.AddScoped<IAgentTool, CaptureSupplierQuoteTool>();
        services.AddScoped<IAgentTool, CompareSupplierQuotesTool>();
        services.AddScoped<IAgentTool, AwardRfqTool>();

        // ---- Engine services ----
        services.AddScoped<IAgentToolRegistry, AgentToolRegistry>();
        services.AddScoped<IAgentGuardrail, AgentGuardrail>();
        services.AddScoped<IAgentOrchestrator, AgentOrchestrator>();

        return services;
    }
}
