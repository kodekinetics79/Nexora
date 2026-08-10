using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace ERP_RFQ_Automation.Infrastructure;

/// <summary>
/// The three transport-layer decisions Program.cs used to make inline, or not at all:
/// which browser origins may call the API, whether the process may redirect to HTTPS,
/// and what Content-Security-Policy every response carries.
///
/// <para>They live here rather than in the top-level statements for one reason: each is a
/// decision that can be WRONG in a way no controller test would see, and a decision that
/// cannot be reached from a test is a decision nobody checks. Program.cs boots against a
/// real PostgreSQL instance, so anything only reachable through it is confined to the
/// container lane; these predicates are pure and are asserted on the portable lane too.</para>
/// </summary>
public static class TransportSecurityPolicy
{
    /// <summary>
    /// Loopback development origins. Merged into the CORS allow-list ONLY under
    /// <see cref="IHostEnvironment.IsDevelopment"/>.
    ///
    /// <para>These were previously merged in EVERY environment. A CORS allow-list is not a
    /// network control — the browser, not the server, refuses the read — so a page served
    /// from any developer's own machine on one of these ports could call the production API
    /// with the operator's bearer token in it and read the response. Nothing about the
    /// deployment made that reachable-only-locally: <c>http://localhost:5173</c> is an origin
    /// every attacker also owns.</para>
    /// </summary>
    public static readonly IReadOnlyList<string> DevelopmentOrigins =
    [
        "http://localhost:5173",
        "http://localhost:4173",
        "http://localhost:3000",
        "http://127.0.0.1:5173",
        "http://127.0.0.1:4173",
        "http://127.0.0.1:3000"
    ];

    /// <summary>The deployed frontend. Present in every environment; it is where the product runs.</summary>
    public const string DeployedFrontendOrigin = "https://nexora1-ai.vercel.app";

    /// <summary>Configuration key for the explicit HTTPS-redirection override.</summary>
    public const string HttpsRedirectionEnabledKey = "Security:HttpsRedirection:Enabled";

    /// <summary>
    /// The exact origins the CORS policy admits. Configured origins first (previews, custom
    /// domains), then the deployed frontend, then — only in Development — the loopback set.
    /// Trailing slashes are trimmed because CORS origin matching is exact.
    /// </summary>
    public static string[] ResolveCorsOrigins(IConfiguration configuration, bool isDevelopment)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var configured = configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? [];
        return configured
            .Append(DeployedFrontendOrigin)
            .Concat(isDevelopment ? DevelopmentOrigins : [])
            .Where(origin => !string.IsNullOrWhiteSpace(origin))
            .Select(origin => origin.Trim().TrimEnd('/'))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    /// <summary>
    /// True when <c>ForwardedHeaders:KnownProxies</c> or <c>ForwardedHeaders:KnownNetworks</c>
    /// names at least one trusted hop — which is what makes <c>Request.Scheme</c> the truth for
    /// EVERY request, including one that reached the app directly with no forwarding header.
    ///
    /// <para>CORRECTED, and the correction matters because the intuitive reading is backwards.
    /// Program.cs clears both collections and repopulates them from configuration, and no
    /// environment sets either. That does NOT mean the forwarded scheme is discarded:
    /// <c>ForwardedHeadersMiddleware</c> performs its known-address check only when at least one
    /// entry exists, so an empty pair trusts EVERY caller and the scheme IS applied today, from
    /// anyone. <c>ForwardedHeadersBehaviourTests</c> asserts this against the exact options
    /// Program.cs configures, rather than leaving it to be re-reasoned wrongly.</para>
    ///
    /// <para>So this predicate is not a loop guard — the loop guard lives in
    /// <c>HttpsRedirectionMiddleware.ShouldRedirect</c>, which declines to redirect a request
    /// whose scheme is unevidenced. What this predicate decides is narrower: whether an absent
    /// forwarding header means "direct plain-HTTP caller" (trusted edge configured, so redirect
    /// it) or "unknowable" (no trusted edge, so serve it).</para>
    /// </summary>
    public static bool ForwardedProtoIsTrusted(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        return HasAnyValue("ForwardedHeaders:KnownProxies") || HasAnyValue("ForwardedHeaders:KnownNetworks");

        bool HasAnyValue(string key) =>
            (configuration.GetSection(key).Get<string[]>() ?? [])
            .Any(value => !string.IsNullOrWhiteSpace(value));
    }

    /// <summary>
    /// Whether the pipeline registers HTTPS redirection and HSTS at all.
    ///
    /// <list type="bullet">
    /// <item>Development: never. The local console and the E2E harness both drive
    /// <c>http://127.0.0.1</c> against a host with no certificate, so redirecting there breaks
    /// the one path a human uses to look at the product.</item>
    /// <item><c>Security:HttpsRedirection:Enabled</c>, when set, is authoritative in both
    /// directions — an operator who knows their edge does something unusual can say so.</item>
    /// <item>Otherwise: ON, in every non-Development environment.</item>
    /// </list>
    ///
    /// <para>ON BY DEFAULT is safe because of what the middleware does, not because of anything
    /// this method checks. <c>UseHttpsRedirection</c> redirects any request whose scheme is not
    /// <c>https</c>, so behind an edge that terminates TLS and does NOT label the scheme it would
    /// answer every request with a 307 the edge forwards straight back — an infinite redirect.
    /// <c>UseLoopSafeHttpsRedirection</c> redirects only a request that is KNOWN to be plain:
    /// either because a configured trusted edge makes the scheme authoritative, or because
    /// <c>X-Original-Proto</c> shows an edge labelled it. Where neither holds the scheme is
    /// genuinely unknowable and the request is served. See
    /// <c>HttpsRedirectionMiddleware.ShouldRedirect</c>.</para>
    /// </summary>
    public static bool ShouldRedirectToHttps(IHostEnvironment environment, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(configuration);

        if (environment.IsDevelopment())
            return false;

        return configuration.GetValue<bool?>(HttpsRedirectionEnabledKey) ?? true;
    }

    /// <summary>
    /// The Content-Security-Policy for every response outside Development.
    ///
    /// <para>This origin is an API. The frontend is a separate Vercel deployment with its own
    /// origin and its own headers, so nothing this process returns is a document a browser is
    /// meant to render — which is why the policy is <c>'none'</c> across the board rather than
    /// an allow-list of the fonts and scripts the SPA loads. Those belong to the frontend's
    /// origin, and admitting them here would only weaken this one.</para>
    ///
    /// <para>It is not decorative. Three writers store user-supplied files under
    /// <c>WebRootPath</c> (<c>ProductRepository.PersistAttachmentAsync</c>,
    /// <c>CustomerController</c>, <c>UserController</c>) preserving the uploaded extension, and
    /// <c>.html</c> is on <c>DocumentIntakeAllowList</c>. No static-file middleware is
    /// registered, so those URLs are dead today — but the single line the default ASP.NET
    /// template ships with would turn every stored page into same-origin script, and the
    /// frontend keeps its JWT in <c>localStorage</c>. The header middleware runs BEFORE the
    /// point that line would be added, so this policy would cover such a response and
    /// <c>default-src 'none'</c> blocks inline script, which is the whole payload.</para>
    ///
    /// <para>Deliberately NOT included: <c>sandbox</c>. It applies to documents, so it costs
    /// nothing on a JSON response, but without <c>allow-downloads</c> it would break a file
    /// download opened by direct navigation — and <c>FileController</c> serves exactly those.</para>
    /// </summary>
    public const string ApiContentSecurityPolicy =
        "default-src 'none'; " +
        "object-src 'none'; " +
        "frame-ancestors 'none'; " +
        "base-uri 'none'; " +
        "form-action 'none'";

    /// <summary>
    /// Development only, and looser by exactly one requirement: Swagger UI is served from this
    /// origin under <see cref="IHostEnvironment.IsDevelopment"/> and is built from inline script
    /// and inline style. <c>frame-ancestors</c>, <c>object-src</c> and <c>form-action</c> stay
    /// closed, so the clickjacking and form-hijack boundaries are identical in both environments.
    /// </summary>
    public const string SwaggerContentSecurityPolicy =
        "default-src 'self'; " +
        "script-src 'self' 'unsafe-inline'; " +
        "style-src 'self' 'unsafe-inline'; " +
        "img-src 'self' data:; " +
        "font-src 'self' data:; " +
        "connect-src 'self'; " +
        "object-src 'none'; " +
        "frame-ancestors 'none'; " +
        "base-uri 'self'; " +
        "form-action 'self'";

    /// <summary>
    /// The <c>Strict-Transport-Security</c> value.
    ///
    /// <para>One year rather than the framework's 30 days: a browser that has not visited for a
    /// month would otherwise be downgradeable again, and a year is what the preload list requires
    /// and what a KSA pilot should be able to state. <c>includeSubDomains</c> is scoped to the
    /// host that sent the header, so it commits only this service's own subdomains.</para>
    ///
    /// <para><c>preload</c> is deliberately ABSENT. It is an effectively irrevocable submission
    /// of the domain to a list compiled into browsers, removal takes months, and this deployment
    /// does not yet own its production domain. Sending it before the domain is final is a
    /// decision that cannot be taken back at the speed the rest of the stack can be changed.</para>
    /// </summary>
    public const string HstsHeaderValue = "max-age=31536000; includeSubDomains";

    /// <summary>The policy in force for <paramref name="environment"/>.</summary>
    public static string ContentSecurityPolicyFor(IHostEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(environment);
        return environment.IsDevelopment() ? SwaggerContentSecurityPolicy : ApiContentSecurityPolicy;
    }
}
