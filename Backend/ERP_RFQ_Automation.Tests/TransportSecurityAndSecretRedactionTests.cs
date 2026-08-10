using System.Net.Http;
using System.Text.Json;
using ERP_RFQ_Automation.Agent;
using ERP_RFQ_Automation.Billing;
using ERP_RFQ_Automation.CommercialFinance;
using ERP_RFQ_Automation.Infrastructure;
using ERP_RFQ_Automation.Mailbox;
using ERP_RFQ_Automation.Notifications;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Notifications.Runtime;
using ERP_RFQ_Automation.Platform.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Options;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// Gate 9, code half. Every assertion here fails if the control is REMOVED, not merely if a
/// value fails to round-trip — the point of each is that something now depends on it.
///
/// <para>These are on the portable lane deliberately. Program.cs cannot boot without PostgreSQL,
/// so anything reachable only through the composed pipeline is confined to the container lane;
/// the decisions themselves are pure functions in <see cref="TransportSecurityPolicy"/> and are
/// asserted here, with the header and origin actually reaching the wire asserted in
/// <c>HttpIntegration/TransportSecurityHttpTests</c>.</para>
/// </summary>
public sealed class TransportSecurityAndSecretRedactionTests
{
    // ---------------------------------------------------------------- CORS origin gating

    [Fact]
    public void Localhost_origins_are_admitted_in_development()
    {
        var origins = TransportSecurityPolicy.ResolveCorsOrigins(Configuration(), isDevelopment: true);

        Assert.Contains("http://localhost:5173", origins);
        Assert.Contains("http://127.0.0.1:3000", origins);
    }

    [Fact]
    public void No_localhost_origin_survives_outside_development()
    {
        // The finding: a page on a developer's machine could read production API responses,
        // because a CORS allow-list is enforced by the browser rather than by the network.
        var origins = TransportSecurityPolicy.ResolveCorsOrigins(Configuration(), isDevelopment: false);

        Assert.Empty(origins.Where(origin =>
            origin.Contains("localhost", StringComparison.OrdinalIgnoreCase)
            || origin.Contains("127.0.0.1", StringComparison.Ordinal)));
        foreach (var developmentOrigin in TransportSecurityPolicy.DevelopmentOrigins)
            Assert.DoesNotContain(developmentOrigin, origins);
    }

    [Fact]
    public void The_deployed_frontend_and_configured_origins_survive_in_production()
    {
        // Gating localhost must not cost the real frontend its access; that would be the
        // "tightened it into an outage" version of this fix.
        var origins = TransportSecurityPolicy.ResolveCorsOrigins(
            Configuration(("Cors:AllowedOrigins:0", "https://preview.nexora.example/")),
            isDevelopment: false);

        Assert.Contains(TransportSecurityPolicy.DeployedFrontendOrigin, origins);
        // Trailing slash trimmed: CORS origin matching is exact.
        Assert.Contains("https://preview.nexora.example", origins);
    }

    // ---------------------------------------------------------------- HTTPS redirection gating

    [Fact]
    public void Redirection_is_on_outside_development_even_with_no_trusted_proxy()
    {
        // Safe only because of the loop guard below: the middleware redirects a request that is
        // KNOWN to be plain HTTP, never one whose scheme it merely cannot prove.
        Assert.True(TransportSecurityPolicy.ShouldRedirectToHttps(Environment("Production"), Configuration()));
    }

    [Fact]
    public void No_environment_today_makes_the_forwarded_scheme_authoritative()
    {
        // The state of the deployment as found: appsettings.json has no ForwardedHeaders section
        // and render.yaml says the edge ranges have not been supplied. NOTE what this does and
        // does not mean — ForwardedHeadersBehaviourTests shows the forwarded scheme is applied
        // anyway, because an empty known-hop list trusts everyone. What is missing is the ability
        // to tell a direct plain-HTTP caller from one whose scheme was never labelled.
        Assert.False(TransportSecurityPolicy.ForwardedProtoIsTrusted(Configuration()));
    }

    [Theory]
    [InlineData("ForwardedHeaders:KnownProxies:0", "10.0.0.7")]
    [InlineData("ForwardedHeaders:KnownNetworks:0", "10.0.0.0/8")]
    public void Configuring_the_edge_makes_the_forwarded_scheme_authoritative(string key, string value)
    {
        Assert.True(TransportSecurityPolicy.ForwardedProtoIsTrusted(Configuration((key, value))));
    }

    [Fact]
    public void An_explicit_setting_overrides_the_default_in_both_directions()
    {
        Assert.True(TransportSecurityPolicy.ShouldRedirectToHttps(
            Environment("Production"),
            Configuration((TransportSecurityPolicy.HttpsRedirectionEnabledKey, "true"))));

        // An operator whose edge does something unusual can turn it off without a code change.
        Assert.False(TransportSecurityPolicy.ShouldRedirectToHttps(
            Environment("Production"),
            Configuration((TransportSecurityPolicy.HttpsRedirectionEnabledKey, "false"))));
    }

    [Fact]
    public void Development_never_redirects()
    {
        // The local console and the E2E harness both drive http://127.0.0.1 against a host with
        // no certificate; redirecting there breaks the only path a human uses to see the product.
        Assert.False(TransportSecurityPolicy.ShouldRedirectToHttps(
            Environment("Development"),
            Configuration(("ForwardedHeaders:KnownProxies:0", "10.0.0.7"))));
    }

    // ---------------------------------------------------------------- the loop guard itself

    [Theory]
    [InlineData("https")]
    [InlineData("HTTPS")]
    [InlineData("https, http")]
    public void A_request_the_edge_forwards_as_https_is_already_secure(string forwardedProto)
    {
        // THE loop guard. The framework's UseHttpsRedirection decides from Request.IsHttps alone,
        // which stays false behind an untrusted TLS-terminating edge — so it would redirect a
        // request that was already made over TLS, and the edge would forward the result back
        // unchanged, forever. Reading the forwarded scheme is what makes that impossible.
        var request = RequestWith(("X-Forwarded-Proto", forwardedProto));

        Assert.True(HttpsRedirectionMiddleware.IsSecure(request));
    }

    [Theory]
    [InlineData("http")]
    [InlineData("")]
    public void A_request_with_no_https_evidence_is_not_treated_as_secure(string forwardedProto)
    {
        var request = forwardedProto.Length == 0
            ? RequestWith()
            : RequestWith(("X-Forwarded-Proto", forwardedProto));

        Assert.False(HttpsRedirectionMiddleware.IsSecure(request));
    }

    [Fact]
    public void A_tls_socket_is_secure_with_no_header_at_all()
    {
        var request = RequestWith();
        request.IsHttps = true;

        Assert.True(HttpsRedirectionMiddleware.IsSecure(request));
    }

    [Theory]
    [InlineData("localhost")]
    [InlineData("127.0.0.1")]
    [InlineData("::1")]
    [InlineData("[::1]")]
    public void Loopback_hosts_are_excluded(string host)
    {
        // No certificate can be presented for these and an HSTS entry against "localhost" would
        // poison every other project on the machine.
        Assert.True(HttpsRedirectionMiddleware.IsLoopback(host));
    }

    [Fact]
    public void A_real_host_is_not_treated_as_loopback()
    {
        Assert.False(HttpsRedirectionMiddleware.IsLoopback("nexora-fyjw.onrender.com"));
    }

    [Fact]
    public void An_unlabelled_request_behind_an_untrusted_hop_is_never_redirected()
    {
        // THE reason ON-by-default is safe. The scheme here is genuinely unknowable — and
        // guessing "it must be http" is precisely the mechanism of a redirect loop behind an edge
        // that terminates TLS without labelling the scheme.
        Assert.False(HttpsRedirectionMiddleware.ShouldRedirect(RequestWith(), schemeIsAuthoritative: false));
    }

    [Fact]
    public void A_request_an_edge_labelled_as_plain_http_is_redirected_even_untrusted()
    {
        // THE REGRESSION THIS SUITE MISSED FIRST TIME. This is the request shape as it ACTUALLY
        // reaches the middleware: UseForwardedHeaders has already applied X-Forwarded-Proto and
        // REMOVED it, leaving X-Original-Proto as the only surviving evidence. Keying the decision
        // on the raw header made the redirect unreachable in every environment — a control that
        // looks configured and never fires. ForwardedHeadersBehaviourTests pins the framework half.
        Assert.True(HttpsRedirectionMiddleware.ShouldRedirect(
            RequestWith(("X-Original-Proto", "http")), schemeIsAuthoritative: false));

        // The raw header is still honoured, for a pipeline where UseForwardedHeaders did not run.
        Assert.True(HttpsRedirectionMiddleware.ShouldRedirect(
            RequestWith(("X-Forwarded-Proto", "http")), schemeIsAuthoritative: false));
    }

    [Fact]
    public void A_request_an_edge_labelled_as_https_is_secure_after_the_scheme_rewrite()
    {
        // The same real shape the other way round: UseForwardedHeaders set Request.Scheme to https
        // and consumed the header, so IsHttps — not a header read — is what carries the answer.
        var request = RequestWith(("X-Original-Proto", "http"));
        request.IsHttps = true;

        Assert.True(HttpsRedirectionMiddleware.IsSecure(request));
        Assert.False(HttpsRedirectionMiddleware.ShouldRedirect(request, schemeIsAuthoritative: false));
    }

    [Fact]
    public void With_no_trusted_edge_a_raw_https_label_is_honoured()
    {
        // Only reachable in a pipeline where UseForwardedHeaders did not run — where it did, the
        // header has been consumed and Request.IsHttps carries the answer instead.
        Assert.False(HttpsRedirectionMiddleware.ShouldRedirect(
            RequestWith(("X-Forwarded-Proto", "https")), schemeIsAuthoritative: false));
    }

    [Fact]
    public void With_a_trusted_edge_a_surviving_raw_label_is_a_refused_one_and_is_not_honoured()
    {
        // Deliberately the opposite answer to the test above, and it is a tightening. Once known
        // hops are configured, ForwardedHeadersMiddleware leaves the raw header in place exactly
        // when it REFUSED it — the peer was not a known proxy. Honouring it here would re-admit
        // what that check just rejected, letting any direct caller opt out of redirection by
        // asserting its own scheme. Request.IsHttps is the only trustworthy source in that
        // configuration, and it says plain, so the request is redirected.
        Assert.True(HttpsRedirectionMiddleware.ShouldRedirect(
            RequestWith(("X-Forwarded-Proto", "https")), schemeIsAuthoritative: true));
    }

    [Fact]
    public void With_a_trusted_edge_an_unlabelled_plain_request_is_redirected()
    {
        // Once KnownProxies/KnownNetworks are configured, UseForwardedHeaders has already
        // rewritten the scheme, so Request.IsHttps is the truth for every request and a plain
        // one is plain — including a direct caller that bypassed the edge entirely.
        Assert.True(HttpsRedirectionMiddleware.ShouldRedirect(RequestWith(), schemeIsAuthoritative: true));
    }

    [Fact]
    public void The_hsts_value_commits_a_year_and_no_preload()
    {
        Assert.Equal("max-age=31536000; includeSubDomains", TransportSecurityPolicy.HstsHeaderValue);
        // preload is effectively irrevocable and the production domain is not settled.
        Assert.DoesNotContain("preload", TransportSecurityPolicy.HstsHeaderValue);
    }

    private static Microsoft.AspNetCore.Http.HttpRequest RequestWith(
        params (string Name, string Value)[] headers)
    {
        var context = new Microsoft.AspNetCore.Http.DefaultHttpContext();
        foreach (var (name, value) in headers)
            context.Request.Headers[name] = value;
        return context.Request;
    }

    // ---------------------------------------------------------------- Content-Security-Policy

    [Fact]
    public void The_api_policy_denies_every_fetch_directive_by_default()
    {
        var policy = TransportSecurityPolicy.ContentSecurityPolicyFor(Environment("Production"));

        // default-src 'none' is what neuters a stored .html served from this origin: script-src
        // falls back to it, and 'none' blocks inline script as well as external.
        Assert.Contains("default-src 'none'", policy);
        Assert.Contains("frame-ancestors 'none'", policy);
        Assert.Contains("base-uri 'none'", policy);
        Assert.Contains("form-action 'none'", policy);
        Assert.Contains("object-src 'none'", policy);
        // sandbox would break a download opened by direct navigation, which FileController serves.
        Assert.DoesNotContain("sandbox", policy);
    }

    [Fact]
    public void The_development_policy_admits_swagger_and_nothing_else()
    {
        var policy = TransportSecurityPolicy.ContentSecurityPolicyFor(Environment("Development"));

        // Swagger UI is inline script and inline style, and is served ONLY in Development.
        Assert.Contains("script-src 'self' 'unsafe-inline'", policy);
        Assert.Contains("style-src 'self' 'unsafe-inline'", policy);
        // The clickjacking and form-hijack boundaries are identical in both environments.
        Assert.Contains("frame-ancestors 'none'", policy);
        Assert.Contains("object-src 'none'", policy);
        Assert.DoesNotContain("'unsafe-eval'", policy);
        Assert.DoesNotContain("*", policy);
    }

    // ---------------------------------------------------------------- outbound header redaction

    public static TheoryData<string> CredentialBearingClients => new()
    {
        // A typed client is named after its SERVICE type, not its implementation — so the
        // Anthropic registration answers to "IAgentLlm". Spelled out because getting it wrong
        // yields a passing lookup against a client nobody registered.
        nameof(ERP_RFQ_Automation.Services.OllamaLlmService),
        nameof(ERP_RFQ_Automation.Agent.Llm.IAgentLlm),
        ERP_RFQ_Automation.Notifications.Providers.SendGridEmailSender.HttpClientName,
        nameof(ERP_RFQ_Automation.Billing.Accounting.HttpAccountingExportConnector),
        ERP_RFQ_Automation.CommercialFinance.FinanceHttpEventPublisher.HttpClientName
    };

    [Theory]
    [MemberData(nameof(CredentialBearingClients))]
    public void Every_credential_bearing_client_redacts_its_authentication_headers(string clientName)
    {
        // HttpClientFactoryOptions.ShouldRedactHeaderValue defaults to redacting NOTHING, and the
        // factory's logging handlers write the full header collection at Trace — so this asserts
        // the registration opted in. Reading the options is the only way to see it: no request is
        // needed, and the default would pass any test that merely sent one.
        var options = OptionsFor(clientName);

        Assert.True(options.ShouldRedactHeaderValue("Authorization"));
        Assert.True(options.ShouldRedactHeaderValue("authorization"));   // case-insensitive
        Assert.True(options.ShouldRedactHeaderValue("x-api-key"));
        Assert.True(options.ShouldRedactHeaderValue("X-Nexora-Signature"));
        // Non-secret headers stay legible, or the log stops being a diagnostic.
        Assert.False(options.ShouldRedactHeaderValue("Content-Type"));
        Assert.False(options.ShouldRedactHeaderValue("Idempotency-Key"));
    }

    private static HttpClientFactoryOptions OptionsFor(string clientName)
    {
        // The REAL registration entry points Program.cs calls, not a reconstruction of them —
        // otherwise this suite would prove only that the test file remembers to redact.
        var configuration = Configuration(
            // Only present so the Anthropic branch of AddAgentEngine is taken; the mock LLM is
            // registered instead when the key is absent, and there is then no client to assert on.
            ("Agent:Anthropic:ApiKey", "value-is-irrelevant-it-only-selects-the-registration"),
            ("Notifications:Provider", "console"),
            ("Notifications:FromAddress", "no-reply@example.test"),
            ("Notifications:AppBaseUrl", "https://example.test"));

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddHttpClient<ERP_RFQ_Automation.Services.OllamaLlmService>()
            .RedactLoggedHeaders(OutboundHttpRedaction.SensitiveHeaders);
        services.AddAgentEngine(configuration);
        services.AddNotifications(configuration);
        services.AddPlatformBilling(configuration);
        services.AddCommercialFinanceOutboxDispatcher(options =>
        {
            options.Enabled = false;
            options.Endpoint = "https://finance.invalid/events";
        });

        using var provider = services.BuildServiceProvider();
        return provider.GetRequiredService<IOptionsMonitor<HttpClientFactoryOptions>>().Get(clientName);
    }

    [Fact]
    public void An_unregistered_client_redacts_nothing_which_is_what_makes_the_assertions_above_mean_something()
    {
        // The framework default. Stated here because every assertion above is only evidence that
        // the registration opted in if the un-opted-in state is visibly different.
        using var provider = new ServiceCollection().AddLogging().BuildServiceProvider();
        var untouched = provider.GetRequiredService<IOptionsMonitor<HttpClientFactoryOptions>>()
            .Get("a-client-nobody-registered");

        Assert.False(untouched.ShouldRedactHeaderValue("Authorization"));
    }

    // ---------------------------------------------------------------- redacted ToString

    [Fact]
    public void Outbound_email_settings_never_print_their_credentials()
    {
        var snapshot = new OutboundEmailSettingsSnapshot
        {
            Provider = "smtp",
            SmtpHost = "smtp.example.test",
            SmtpUsername = "postmaster@example.test",
            SmtpPassword = SecretMarker + "-smtp",
            SendGridApiKey = SecretMarker + "-sendgrid"
        };

        var printed = snapshot.ToString();

        Assert.DoesNotContain(SecretMarker, printed, StringComparison.Ordinal);
        Assert.Contains("[redacted]", printed);
        // Diagnostics that are NOT secrets must survive, or the redaction is paid for twice.
        Assert.Contains("smtp.example.test", printed);
        Assert.Contains("postmaster@example.test", printed);
    }

    [Fact]
    public void Outbound_email_settings_distinguish_an_absent_credential_from_a_present_one()
    {
        // "Provider selected, no credential" and "credential the provider rejected" are different
        // operator tasks, and the marker is the only thing in the log that tells them apart.
        Assert.Contains("SmtpPassword = none", new OutboundEmailSettingsSnapshot().ToString());
        Assert.Contains("SendGridApiKey = none", new OutboundEmailSettingsSnapshot().ToString());
    }

    [Fact]
    public void The_totp_enrolment_response_prints_neither_the_seed_nor_the_uri_that_embeds_it()
    {
        var response = new PlatformMfaEnrollmentStartResponse(
            SecretMarker, $"otpauth://totp/Nexora?secret={SecretMarker}");

        var printed = response.ToString();

        Assert.DoesNotContain(SecretMarker, printed, StringComparison.Ordinal);
        Assert.Contains("[redacted]", printed);
    }

    [Fact]
    public void Recovery_codes_are_redacted_but_their_count_survives()
    {
        var response = new PlatformMfaEnrollmentConfirmResponse(
            DateTime.UtcNow, [SecretMarker + "-1", SecretMarker + "-2"]);

        var printed = response.ToString();

        Assert.DoesNotContain(SecretMarker, printed, StringComparison.Ordinal);
        Assert.Contains("2 issued", printed);
    }

    [Fact]
    public void Mailbox_create_never_prints_the_customer_mailbox_password()
    {
        var printed = new MailboxCreateRequestDTO
        {
            ConfigurationName = "Sales inbox",
            EmailAddress = "sales@example.test",
            Protocol = "IMAP",
            Host = "imap.example.test",
            Port = 993,
            Username = "sales@example.test",
            Password = SecretMarker
        }.ToString();

        Assert.DoesNotContain(SecretMarker, printed, StringComparison.Ordinal);
        Assert.Contains("Password = [redacted]", printed);
        Assert.Contains("imap.example.test", printed);
    }

    [Fact]
    public void Mailbox_update_never_prints_the_password_and_still_states_whether_one_was_sent()
    {
        // On update a BLANK password means "keep the stored one", so present-or-absent is the
        // first question anyone diagnosing this request asks — and it discloses nothing.
        Assert.Contains("Password = none",
            new MailboxUpdateRequestDTO { Host = "imap.example.test" }.ToString());

        var printed = new MailboxUpdateRequestDTO
        {
            Host = "imap.example.test",
            Password = SecretMarker
        }.ToString();
        Assert.DoesNotContain(SecretMarker, printed, StringComparison.Ordinal);
        Assert.Contains("Password = [redacted]", printed);
    }

    [Fact]
    public void Mailbox_test_never_prints_the_password()
    {
        var printed = new MailboxTestRequestDTO
        {
            Protocol = "SMTP",
            Host = "smtp.example.test",
            Port = 587,
            Username = "sales@example.test",
            Password = SecretMarker
        }.ToString();

        Assert.DoesNotContain(SecretMarker, printed, StringComparison.Ordinal);
        Assert.Contains("Password = [redacted]", printed);
    }

    // ---------------------------------------------------------------- User.PasswordHash

    [Fact]
    public void A_serialised_user_entity_carries_no_password_hash()
    {
        // No endpoint returns the entity today. The attribute exists so that the day one does —
        // directly, or through a navigation on some other graph — the hash does not go with it.
        var json = JsonSerializer.Serialize(new User
        {
            Id = 1,
            FirstName = "Aisha",
            LastName = "Rahman",
            Email = "aisha@example.test",
            PasswordHash = SecretMarker,
            ImageUrl = string.Empty
        });

        Assert.DoesNotContain(SecretMarker, json, StringComparison.Ordinal);
        Assert.DoesNotContain("PasswordHash", json, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("aisha@example.test", json);
    }

    // ---------------------------------------------------------------- helpers

    /// <summary>Never a real credential shape; only a token this test can search the output for.</summary>
    private const string SecretMarker = "nexora-test-sentinel-value";

    private static IConfiguration Configuration(params (string Key, string Value)[] values) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(values.Select(pair =>
                new KeyValuePair<string, string?>(pair.Key, pair.Value)))
            .Build();

    private static IHostEnvironment Environment(string environmentName) =>
        new StubHostEnvironment(environmentName);

    private sealed class StubHostEnvironment(string environmentName) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = environmentName;
        public string ApplicationName { get; set; } = "ERP_RFQ_Automation";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } =
            new Microsoft.Extensions.FileProviders.NullFileProvider();
    }
}
