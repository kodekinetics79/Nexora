using System.Reflection;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace ERP_RFQ_Automation.Platform.Hardening;

/// <summary>Which metric exporter the process actually ended up running.</summary>
public enum ObservabilityExporter
{
    /// <summary>NOTHING is exported. Instruments are collected in-process and discarded.</summary>
    None,
    /// <summary>OTLP to a configured collector endpoint.</summary>
    Otlp,
    /// <summary>Console (Development convenience).</summary>
    Console
}

/// <summary>
/// The resolved observability posture, decided once at composition time so it can be
/// logged, asserted in a test, and read back by the endpoint mapping — instead of being
/// three separate <c>if</c> statements nobody can see the result of.
/// </summary>
/// <param name="Exporter">Push exporter for traces + metrics.</param>
/// <param name="OtlpEndpoint">Resolved OTLP URI, when one is in play.</param>
/// <param name="ConfiguredOtlpValue">Raw configured value, for the log line.</param>
/// <param name="OtlpValueInvalid">True when a value was supplied but was not an absolute URI.</param>
/// <param name="PrometheusEnabled">True when the built-in scrape endpoint is mapped.</param>
/// <param name="PrometheusPath">Scrape path, when enabled.</param>
/// <param name="PrometheusKeyConfigured">True when the scrape endpoint requires a key.</param>
/// <param name="Environment">Environment name the decision was made under.</param>
public sealed record ObservabilitySelection(
    ObservabilityExporter Exporter,
    Uri? OtlpEndpoint,
    string ConfiguredOtlpValue,
    bool OtlpValueInvalid,
    bool PrometheusEnabled,
    string PrometheusPath,
    bool PrometheusKeyConfigured,
    string Environment)
{
    /// <summary>True when NOTHING leaves the process — the failure mode this whole
    /// selection type exists to make impossible to miss.</summary>
    public bool IsBlind => Exporter == ObservabilityExporter.None && !PrometheusEnabled;
}

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

    /// <summary>Configuration key holding the OTLP collector endpoint.</summary>
    public const string OtlpEndpointConfigurationKey = "Observability:OtlpEndpoint";

    /// <summary>The environment-variable form of <see cref="OtlpEndpointConfigurationKey"/>
    /// — this is the exact name to set in render.yaml.</summary>
    public const string OtlpEndpointEnvironmentVariable = "Observability__OtlpEndpoint";

    /// <summary>Environment-variable form of the Prometheus scrape key.</summary>
    public const string PrometheusScrapeKeyEnvironmentVariable = "Observability__Prometheus__ScrapeKey";

    /// <summary>
    /// Resolves the observability posture WITHOUT touching DI, so the decision rules are
    /// directly unit testable (the malware-scanner selection uses the same shape).
    ///
    /// Exporter selection (config key <c>Observability:OtlpEndpoint</c>):
    ///  - endpoint set + valid  → OTLP exporter (traces + metrics).
    ///  - endpoint unset, Development → Console exporter.
    ///  - endpoint unset, Production  → no push exporter (no crash if no collector runs).
    ///  - endpoint set but invalid → treated as unset (never throws).
    ///
    /// The Prometheus scrape endpoint then fills the hole: unless it is explicitly
    /// disabled it turns ON exactly when there is no OTLP endpoint, so "no collector
    /// configured" no longer means "no metrics anywhere". That combination — a documented
    /// meter, a registered MeterProvider and no exporter at all in Production — is what
    /// made every instrument permanently invisible.
    /// </summary>
    public static ObservabilitySelection SelectExporter(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var environment = configuration["Observability:Environment"]
            ?? System.Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT")
            ?? "Production";
        var isDevelopment = string.Equals(environment, "Development", StringComparison.OrdinalIgnoreCase);

        var configured = configuration[OtlpEndpointConfigurationKey];
        var supplied = !string.IsNullOrWhiteSpace(configured);
        // Declared up front: `supplied &&` short-circuits, so the compiler cannot prove
        // TryCreate ran and assigned the out parameter.
        Uri? otlpUri = null;
        var parsed = supplied && Uri.TryCreate(configured, UriKind.Absolute, out otlpUri);
        if (!parsed) otlpUri = null;

        var exporter = parsed
            ? ObservabilityExporter.Otlp
            : isDevelopment ? ObservabilityExporter.Console : ObservabilityExporter.None;

        var prometheus = ReadPrometheusOptions(configuration);
        // Default: the fallback is on precisely when nothing else pushes metrics out.
        var prometheusEnabled = prometheus.Enabled ?? exporter != ObservabilityExporter.Otlp;
        var path = string.IsNullOrWhiteSpace(prometheus.Path) ? "/metrics" : prometheus.Path.Trim();

        return new ObservabilitySelection(
            exporter,
            otlpUri,
            supplied ? configured!.Trim() : "(unset)",
            OtlpValueInvalid: supplied && !parsed,
            prometheusEnabled,
            path,
            PrometheusKeyConfigured: !string.IsNullOrWhiteSpace(prometheus.ScrapeKey),
            environment);
    }

    /// <summary>
    /// States the observability posture explicitly at boot, in the same style as the
    /// malware-scanner selection in Program.cs. Silent no-op observability is exactly what
    /// let every instrument sit at zero unnoticed, so the "nothing is exported" case is an
    /// ERROR, not a debug line.
    /// </summary>
    public static void LogSelection(ILogger logger, ObservabilitySelection selection)
    {
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(selection);

        if (selection.OtlpValueInvalid)
        {
            logger.LogError(
                "OBSERVABILITY CONFIGURATION INVALID: '{ConfiguredValue}' is not an absolute URI, so it "
                + "cannot be used as an OTLP endpoint. Key={ConfigurationKey} (environment variable "
                + "{EnvironmentVariable}). Falling back as if it were unset.",
                selection.ConfiguredOtlpValue, OtlpEndpointConfigurationKey, OtlpEndpointEnvironmentVariable);
        }

        logger.LogInformation(
            "Metrics exporter selected: {Exporter}. ConfiguredValue={ConfiguredValue} Endpoint={Endpoint} "
            + "PrometheusScrape={PrometheusState} Meter={Meter} Environment={Environment}.",
            selection.Exporter,
            selection.ConfiguredOtlpValue,
            selection.OtlpEndpoint?.ToString() ?? "(none)",
            selection.PrometheusEnabled ? selection.PrometheusPath : "(disabled)",
            NexoraMetrics.MeterName,
            selection.Environment);

        if (selection.IsBlind)
        {
            logger.LogError(
                "NO METRICS ARE LEAVING THIS PROCESS. Every {Meter} instrument is being collected and "
                + "discarded in {Environment}: no OTLP endpoint is configured ({EnvironmentVariable} is "
                + "{ConfiguredValue}) and the built-in Prometheus scrape endpoint is disabled. Job "
                + "throughput, failure rate and per-tenant queue age are all unobservable. Point "
                + "that variable at a collector, or re-enable {PrometheusKey}.",
                NexoraMetrics.MeterName, selection.Environment, OtlpEndpointEnvironmentVariable,
                selection.ConfiguredOtlpValue, $"{PrometheusScrapeOptions.SectionName}:Enabled");
        }

        if (selection.PrometheusEnabled && !selection.PrometheusKeyConfigured)
        {
            logger.LogWarning(
                "Prometheus scrape endpoint {Path} is UNAUTHENTICATED. It exposes tenant ids, queue "
                + "depths and per-tenant job age — operational data, not public data. Set "
                + "{EnvironmentVariable} and configure the scraper to send it as the {Header} header, "
                + "or restrict the path at the edge.",
                selection.PrometheusPath, PrometheusScrapeKeyEnvironmentVariable,
                PrometheusScrapeOptions.ScrapeKeyHeader);
        }

        if (selection.Exporter == ObservabilityExporter.Console)
        {
            logger.LogInformation(
                "Console exporter is a DEVELOPMENT convenience: metrics are printed to stdout and "
                + "nothing aggregates them. Do not rely on it for an alert.");
        }
    }

    /// <summary>
    /// Registers <see cref="NexoraMetrics"/>, the queue-gauge poller, OpenTelemetry
    /// tracing + metrics, and (when selected) the built-in Prometheus collector.
    ///
    /// Instruments: ASP.NET Core, HttpClient, EF Core, and Npgsql (source
    /// <c>"Npgsql"</c>), plus the custom <see cref="NexoraMetrics.MeterName"/> meter.
    /// </summary>
    public static IServiceCollection AddPlatformObservability(
        this IServiceCollection services, IConfiguration configuration)
    {
        var selection = SelectExporter(configuration);

        // The decision is stated at boot, before anything can depend on it, exactly as the
        // malware-scanner selection is. A short-lived console logger is used because the
        // host's logging pipeline does not exist yet at composition time.
        using (var startupLoggerFactory = LoggerFactory.Create(logging =>
        {
            logging.AddConsole();
            logging.SetMinimumLevel(LogLevel.Information);
        }))
        {
            LogSelection(
                startupLoggerFactory.CreateLogger("ERP_RFQ_Automation.Platform.Hardening.Observability"),
                selection);
        }

        services.AddSingleton(selection);

        // Custom metrics are always available for other code to inject/emit,
        // regardless of whether an exporter is configured.
        services.AddSingleton<IExtractionQueueSnapshotProvider, ExtractionQueueSnapshotProvider>();
        services.AddSingleton<NexoraMetrics>();

        // Golden-signal gauges are backed by ONE bounded query per interval, never a query
        // per request or per scrape (see ExtractionQueueMetricsPoller).
        var queueMetrics = configuration
            .GetSection(ExtractionQueueMetricsOptions.SectionName)
            .Get<ExtractionQueueMetricsOptions>() ?? new ExtractionQueueMetricsOptions();
        services.AddSingleton(queueMetrics);
        services.AddHostedService<ExtractionQueueMetricsPoller>();

        if (selection.PrometheusEnabled)
        {
            var prometheus = ReadPrometheusOptions(configuration);
            prometheus.Path = selection.PrometheusPath;
            services.AddSingleton(prometheus);
            // Registered as a singleton so the listener attaches once, at composition, and
            // therefore never misses measurements emitted before the first scrape.
            services.AddSingleton<NexoraPrometheusCollector>();
        }

        services.AddOpenTelemetry()
            .ConfigureResource(resource => resource
                .AddService(serviceName: ServiceName, serviceVersion:
                    configuration["Observability:ServiceVersion"]
                    ?? Assembly.GetEntryAssembly()?.GetName().Version?.ToString()
                    ?? "1.0.0")
                .AddAttributes(new KeyValuePair<string, object>[]
                {
                    new("deployment.environment", selection.Environment)
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

                if (selection.Exporter == ObservabilityExporter.Otlp)
                    tracing.AddOtlpExporter(o => o.Endpoint = selection.OtlpEndpoint!);
                else if (selection.Exporter == ObservabilityExporter.Console)
                    tracing.AddConsoleExporter();
            })
            .WithMetrics(metrics =>
            {
                metrics
                    .AddAspNetCoreInstrumentation()
                    .AddHttpClientInstrumentation()
                    .AddMeter(NexoraMetrics.MeterName);

                if (selection.Exporter == ObservabilityExporter.Otlp)
                    metrics.AddOtlpExporter(o => o.Endpoint = selection.OtlpEndpoint!);
                else if (selection.Exporter == ObservabilityExporter.Console)
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

    private static PrometheusScrapeOptions ReadPrometheusOptions(IConfiguration configuration) =>
        configuration.GetSection(PrometheusScrapeOptions.SectionName).Get<PrometheusScrapeOptions>()
        ?? new PrometheusScrapeOptions();
}
