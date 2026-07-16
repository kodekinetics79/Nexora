using System.Reflection;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace ERP_RFQ_Automation.Platform.Hardening;

/// <summary>
/// Config-driven OpenTelemetry (traces + metrics) and structured per-tenant
/// logging enrichment for Nexora. Everything here has safe defaults and NEVER
/// throws at startup when configuration is absent (PlatformHardening: no-op when
/// unconfigured). See HARDENING-WIRING.md for the exact Program.cs lines.
/// </summary>
public static class ObservabilityExtensions
{
    /// <summary>Resource <c>service.name</c> reported to the collector.</summary>
    public const string ServiceName = "nexora-api";

    /// <summary>
    /// Registers <see cref="NexoraMetrics"/> and OpenTelemetry tracing + metrics.
    ///
    /// Exporter selection (config key <c>Observability:OtlpEndpoint</c>):
    ///  - endpoint set + valid  → OTLP exporter (traces + metrics).
    ///  - endpoint unset, Development → Console exporter.
    ///  - endpoint unset, Production  → no exporter (collect-only, no crash if no
    ///    collector is running).
    ///  - endpoint set but invalid → treated as unset (never throws).
    ///
    /// Instruments: ASP.NET Core, HttpClient, EF Core, and Npgsql (source
    /// <c>"Npgsql"</c>), plus the custom <see cref="NexoraMetrics.MeterName"/> meter.
    /// </summary>
    public static IServiceCollection AddPlatformObservability(
        this IServiceCollection services, IConfiguration configuration)
    {
        // Custom metrics are always available for other code to inject/emit,
        // regardless of whether an exporter is configured.
        services.AddSingleton<NexoraMetrics>();

        var environment = configuration["Observability:Environment"]
            ?? Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT")
            ?? "Production";
        var isDevelopment = string.Equals(environment, "Development", StringComparison.OrdinalIgnoreCase);

        var serviceVersion = configuration["Observability:ServiceVersion"]
            ?? Assembly.GetEntryAssembly()?.GetName().Version?.ToString()
            ?? "1.0.0";

        // Resolve + validate the OTLP endpoint defensively. An invalid value must
        // degrade to "no exporter", never crash the host.
        var otlpEndpoint = configuration["Observability:OtlpEndpoint"];
        Uri? otlpUri = null;
        var hasOtlp = !string.IsNullOrWhiteSpace(otlpEndpoint)
            && Uri.TryCreate(otlpEndpoint, UriKind.Absolute, out otlpUri);

        services.AddOpenTelemetry()
            .ConfigureResource(resource => resource
                .AddService(serviceName: ServiceName, serviceVersion: serviceVersion)
                .AddAttributes(new KeyValuePair<string, object>[]
                {
                    new("deployment.environment", environment)
                }))
            .WithTracing(tracing =>
            {
                tracing
                    .AddAspNetCoreInstrumentation()
                    .AddHttpClientInstrumentation()
                    .AddEntityFrameworkCoreInstrumentation()
                    // Npgsql publishes activities under the "Npgsql" ActivitySource;
                    // adding it as a source captures DB spans below the EF layer.
                    .AddSource("Npgsql");

                if (hasOtlp)
                    tracing.AddOtlpExporter(o => o.Endpoint = otlpUri!);
                else if (isDevelopment)
                    tracing.AddConsoleExporter();
            })
            .WithMetrics(metrics =>
            {
                metrics
                    .AddAspNetCoreInstrumentation()
                    .AddHttpClientInstrumentation()
                    .AddMeter(NexoraMetrics.MeterName);

                if (hasOtlp)
                    metrics.AddOtlpExporter(o => o.Endpoint = otlpUri!);
                else if (isDevelopment)
                    metrics.AddConsoleExporter();
            });

        return services;
    }

    /// <summary>
    /// Adds the structured per-tenant logging scope middleware. Place it AFTER
    /// <c>UseAuthentication()</c> so the <c>businessUnitId</c> claim is populated;
    /// the correlation id is captured regardless. Every log line emitted during
    /// the request then carries tenant + correlation context. Provider-agnostic
    /// (works with the default console logger — no Serilog required).
    /// </summary>
    public static IApplicationBuilder UsePlatformObservability(this IApplicationBuilder app) =>
        app.UseMiddleware<TenantLoggingMiddleware>();
}
