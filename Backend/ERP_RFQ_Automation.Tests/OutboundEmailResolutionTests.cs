using System.Net;
using System.Net.Mail;
using System.Net.Sockets;
using ERP_RFQ_Automation.Notifications;
using ERP_RFQ_Automation.Notifications.Providers;
using ERP_RFQ_Automation.Notifications.Runtime;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// Runtime transport resolution: the stored configuration taking effect without a restart, the
/// fallback to IConfiguration, and — the invariant everything else depends on — that no path
/// through the resolver can produce a transport the outbound guard is not wrapped around.
/// </summary>
public class OutboundEmailResolutionTests
{
    // ==== fallback + runtime change ==================================================================

    [Fact]
    public async Task Falls_back_to_configuration_when_no_stored_row_exists()
    {
        var harness = new ResolverHarness(configured: options =>
        {
            options.Provider = "smtp";
            options.FromAddress = "config@nexora.test";
            options.Smtp.Host = "smtp.config.test";
        });
        harness.Source.Snapshot = null;

        var resolved = await harness.Resolver.ResolveAsync();

        Assert.Equal(OutboundEmailSettingsOrigin.Configuration, resolved.Settings.Origin);
        Assert.Equal("smtp", resolved.Settings.NormalizedProvider);
        Assert.Equal("smtp.config.test", resolved.Settings.SmtpHost);
        Assert.Equal("config@nexora.test", resolved.Settings.FromAddress);
    }

    [Fact]
    public async Task Stored_configuration_overrides_configuration_without_a_restart()
    {
        var harness = new ResolverHarness(configured: options =>
        {
            options.Provider = "console";
            options.AppBaseUrl = "https://config.nexora.test";
        });

        var before = await harness.Resolver.ResolveAsync();
        Assert.Equal("console", before.Settings.NormalizedProvider);
        Assert.False(before.Settings.TransmitsMail);

        // The operator saves. Same process, same singleton, no restart.
        harness.Source.Snapshot = Stored(version: 2, provider: "smtp", host: "smtp.saved.test");
        harness.Resolver.Invalidate();

        var after = await harness.Resolver.ResolveAsync();
        Assert.Equal("smtp", after.Settings.NormalizedProvider);
        Assert.True(after.Settings.TransmitsMail);
        Assert.Equal("smtp.saved.test", after.Settings.SmtpHost);
        Assert.Equal(OutboundEmailSettingsOrigin.Platform, after.Settings.Origin);
    }

    [Fact]
    public async Task Another_instance_picks_up_a_change_after_the_revalidation_interval()
    {
        var harness = new ResolverHarness();
        harness.Source.Snapshot = Stored(version: 1, provider: "console");
        await harness.Resolver.ResolveAsync();

        // No Invalidate: this models the OTHER process, which never saw the save.
        harness.Source.Snapshot = Stored(version: 2, provider: "smtp", host: "smtp.saved.test");

        var stale = await harness.Resolver.ResolveAsync();
        Assert.Equal("console", stale.Settings.NormalizedProvider);

        harness.Time.Advance(harness.Resolver.RevalidationInterval + TimeSpan.FromSeconds(1));

        var refreshed = await harness.Resolver.ResolveAsync();
        Assert.Equal("smtp", refreshed.Settings.NormalizedProvider);
    }

    [Fact]
    public async Task Unchanged_version_never_re_reads_the_credential_columns()
    {
        var harness = new ResolverHarness();
        harness.Source.Snapshot = Stored(version: 7, provider: "smtp", host: "smtp.saved.test");

        await harness.Resolver.ResolveAsync();
        Assert.Equal(1, harness.Source.FullReads);

        harness.Time.Advance(harness.Resolver.RevalidationInterval + TimeSpan.FromSeconds(1));
        await harness.Resolver.ResolveAsync();

        // The revalidation is the cheap two-column read; the encrypted secrets were not selected
        // again, which is what lets the least-privileged roles hold a narrower grant.
        Assert.Equal(1, harness.Source.FullReads);
        Assert.Equal(1, harness.Source.VersionReads);
    }

    [Fact]
    public async Task A_settings_read_failure_keeps_the_last_known_good_transport()
    {
        var harness = new ResolverHarness();
        harness.Source.Snapshot = Stored(version: 3, provider: "smtp", host: "smtp.saved.test");
        await harness.Resolver.ResolveAsync();

        harness.Source.Throw = true;
        harness.Time.Advance(harness.Resolver.RevalidationInterval + TimeSpan.FromSeconds(1));

        var resolved = await harness.Resolver.ResolveAsync();

        // A settings-table outage must not become a total outbound outage.
        Assert.Equal("smtp", resolved.Settings.NormalizedProvider);
        Assert.Equal("smtp.saved.test", resolved.Settings.SmtpHost);
    }

    // ==== containment ================================================================================

    [Theory]
    [InlineData("console", typeof(ConsoleEmailSender))]
    [InlineData("smtp", typeof(SmtpEmailSender))]
    [InlineData("sendgrid", typeof(SendGridEmailSender))]
    [InlineData("something-else-entirely", typeof(ConsoleEmailSender))]
    public void Every_resolved_transport_is_wrapped_by_the_outbound_guard(string provider, Type expectedInner)
    {
        var harness = new ResolverHarness();

        var sender = harness.Resolver.BuildTransport(Stored(1, provider, host: "smtp.test", apiKey: "SG.key"));

        // The static type is already GuardedEmailSender — that is the point of the resolver's
        // signature. This asserts the other half: the decorator really is wrapped AROUND the
        // provider the settings selected, not beside it.
        var inner = typeof(GuardedEmailSender)
            .GetField("_inner", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(sender);
        Assert.IsType(expectedInner, inner);
    }

    [Fact]
    public async Task Containment_applies_to_a_transport_selected_at_runtime()
    {
        var harness = new ResolverHarness();
        harness.Source.Snapshot = Stored(1, "smtp", host: "smtp.invalid.test") with
        {
            OutboundGuardMode = nameof(OutboundEmailMode.DraftOnly)
        };

        var resolved = await harness.Resolver.ResolveAsync();
        var message = new EmailMessage { Subject = "contained" };
        message.AddTo("supplier@real-company.test");

        // DraftOnly means nothing is transmitted. If the guard had been dropped, this would have
        // attempted a real SMTP connection to smtp.invalid.test and thrown.
        var receipt = await resolved.Sender.SendAsync(message);
        Assert.Null(receipt);
    }

    [Fact]
    public void The_container_exposes_only_the_guarded_sender()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddNotifications(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Notifications:Provider"] = "smtp",
                ["Notifications:Smtp:Host"] = "smtp.test"
            }).Build());

        using var provider = services.BuildServiceProvider();

        Assert.IsType<RuntimeConfiguredEmailSender>(provider.GetRequiredService<IEmailSender>());

        // The concrete transports are deliberately absent from the container. Before, each sat in
        // DI as itself so the decorator could be composed around it — which also meant a raw,
        // uncontained transport was resolvable by anyone who asked for it by type.
        Assert.Null(provider.GetService<SmtpEmailSender>());
        Assert.Null(provider.GetService<SendGridEmailSender>());
        Assert.Null(provider.GetService<ConsoleEmailSender>());
    }

    [Fact]
    public async Task Effective_options_report_the_stored_values_to_existing_consumers()
    {
        var harness = new ResolverHarness(configured: options => options.AppBaseUrl = "https://config.nexora.test");
        IOptions<NotificationsOptions> options = new EffectiveNotificationsOptions(harness.Resolver);

        Assert.Equal("https://config.nexora.test", options.Value.AppBaseUrl);

        harness.Source.Snapshot = Stored(2, "smtp", host: "smtp.saved.test") with
        {
            AppBaseUrl = "https://app.customer.test"
        };
        harness.Resolver.Invalidate();
        await harness.Resolver.ResolveAsync();

        // This is the value TenantAdminInvitationService puts in an activation link. A delivered
        // email pointing at the wrong host is the same dead end as an email nobody sent.
        Assert.Equal("https://app.customer.test", options.Value.AppBaseUrl);
    }

    // ==== send-side health ===========================================================================

    [Fact]
    public async Task A_console_send_is_never_recorded_as_a_successful_delivery()
    {
        var harness = new ResolverHarness();
        harness.Source.Snapshot = Stored(1, "console");
        var health = new OutboundEmailHealth();
        var sender = new RuntimeConfiguredEmailSender(
            harness.Resolver, health, NullLogger<RuntimeConfiguredEmailSender>.Instance);

        var message = new EmailMessage { Subject = "logged, not sent" };
        message.AddTo("founder@customer.test");
        await sender.SendAsync(message);

        Assert.Null(health.Snapshot().LastSuccessUtc);
    }

    [Fact]
    public async Task A_transport_failure_is_recorded_and_classified()
    {
        var harness = new ResolverHarness(handler: new StubHttpMessageHandler(HttpStatusCode.Unauthorized));
        harness.Source.Snapshot = Stored(1, "sendgrid", apiKey: "SG.rotated-away");
        var health = new OutboundEmailHealth();
        var sender = new RuntimeConfiguredEmailSender(
            harness.Resolver, health, NullLogger<RuntimeConfiguredEmailSender>.Instance);

        var message = new EmailMessage { Subject = "will fail" };
        message.AddTo("founder@customer.test");

        await Assert.ThrowsAsync<OutboundEmailTransportException>(() => sender.SendAsync(message));

        var snapshot = health.Snapshot();
        Assert.Equal(OutboundEmailFailureKind.AuthenticationFailed, snapshot.LastFailureKind);
        Assert.Equal(1, snapshot.ConsecutiveFailures);
    }

    // ==== verification send ==========================================================================

    [Fact]
    public async Task Test_send_reports_that_a_console_provider_transmitted_nothing()
    {
        var harness = new ResolverHarness();
        var probe = new OutboundEmailProbe(harness.Resolver, NullLogger<OutboundEmailProbe>.Instance);

        var result = await probe.SendAsync(Stored(1, "console"), "ops@nexora.test", "owner@nexora.test");

        Assert.True(result.Succeeded);
        Assert.False(result.Transmitted);
        Assert.Contains("NOTHING was transmitted", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Test_send_classifies_a_missing_credential_before_touching_the_network()
    {
        var harness = new ResolverHarness();
        var probe = new OutboundEmailProbe(harness.Resolver, NullLogger<OutboundEmailProbe>.Instance);

        var result = await probe.SendAsync(Stored(1, "smtp", host: null), "ops@nexora.test", null);

        Assert.False(result.Succeeded);
        Assert.Equal(OutboundEmailFailureKind.NotConfigured, result.Kind);
    }

    [Fact]
    public async Task Test_send_classifies_a_rejected_api_key()
    {
        var harness = new ResolverHarness(handler: new StubHttpMessageHandler(HttpStatusCode.Unauthorized));
        var probe = new OutboundEmailProbe(harness.Resolver, NullLogger<OutboundEmailProbe>.Instance);

        var result = await probe.SendAsync(
            Stored(1, "sendgrid", apiKey: "SG.rotated-away"), "ops@nexora.test", "owner@nexora.test");

        Assert.False(result.Succeeded);
        Assert.Equal(OutboundEmailFailureKind.AuthenticationFailed, result.Kind);
        Assert.Equal("401", result.ProviderStatus);

        // The provider's own sentence never reaches the operator; ours names the fix.
        Assert.Contains("credentials", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("SG.rotated-away", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Test_send_classifies_an_unverified_sender_as_a_provider_rejection()
    {
        var harness = new ResolverHarness(handler: new StubHttpMessageHandler(HttpStatusCode.BadRequest));
        var probe = new OutboundEmailProbe(harness.Resolver, NullLogger<OutboundEmailProbe>.Instance);

        var result = await probe.SendAsync(Stored(1, "sendgrid", apiKey: "SG.key"), "ops@nexora.test", null);

        Assert.Equal(OutboundEmailFailureKind.ProviderRejected, result.Kind);
    }

    [Fact]
    public async Task Test_send_reports_a_guard_refusal_as_containment_rather_than_a_provider_fault()
    {
        var harness = new ResolverHarness();
        var probe = new OutboundEmailProbe(harness.Resolver, NullLogger<OutboundEmailProbe>.Instance);

        var candidate = Stored(1, "smtp", host: "smtp.example.test") with
        {
            OutboundGuardMode = nameof(OutboundEmailMode.AllowListOnly)
        };

        var result = await probe.SendAsync(candidate, "outsider@elsewhere.test", null);

        Assert.False(result.Succeeded);
        Assert.Equal(OutboundEmailFailureKind.BlockedByOutboundGuard, result.Kind);
    }

    [Fact]
    public async Task Test_send_says_where_the_guard_actually_sent_it()
    {
        var harness = new ResolverHarness();
        var probe = new OutboundEmailProbe(harness.Resolver, NullLogger<OutboundEmailProbe>.Instance);

        var candidate = Stored(1, "console") with
        {
            OutboundGuardMode = nameof(OutboundEmailMode.Redirect),
            OutboundGuardRedirectTo = "sink@nexora.test"
        };

        var result = await probe.SendAsync(candidate, "ops@nexora.test", null);

        Assert.Equal("ops@nexora.test", result.IntendedRecipient);
        Assert.Equal("sink@nexora.test", result.EffectiveRecipient);
    }

    // ==== classification =============================================================================

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, OutboundEmailFailureKind.AuthenticationFailed)]
    [InlineData(HttpStatusCode.Forbidden, OutboundEmailFailureKind.AuthenticationFailed)]
    [InlineData(HttpStatusCode.TooManyRequests, OutboundEmailFailureKind.RateLimited)]
    [InlineData(HttpStatusCode.BadRequest, OutboundEmailFailureKind.ProviderRejected)]
    [InlineData(HttpStatusCode.ServiceUnavailable, OutboundEmailFailureKind.HostUnreachable)]
    public void Http_statuses_classify_to_an_actionable_cause(HttpStatusCode status, OutboundEmailFailureKind expected)
        => Assert.Equal(expected, OutboundEmailFailureClassifier.ClassifyHttpStatus(status).Kind);

    [Fact]
    public void Smtp_authentication_failure_is_recognised_from_its_enhanced_status_code()
    {
        // 535 is not in SmtpStatusCode, so the framework collapses it to GeneralFailure and the
        // response text is the only remaining signal. This is the single most common real failure.
        var failure = OutboundEmailFailureClassifier.Classify(
            new SmtpException(SmtpStatusCode.GeneralFailure, "The server response was: 5.7.0 Authentication Required"));

        Assert.Equal(OutboundEmailFailureKind.AuthenticationFailed, failure.Kind);
    }

    [Fact]
    public void Relay_denial_is_distinguished_from_a_bad_password()
    {
        var failure = OutboundEmailFailureClassifier.Classify(
            new SmtpException(SmtpStatusCode.GeneralFailure, "The server response was: 5.7.1 Relay access denied"));

        Assert.Equal(OutboundEmailFailureKind.RelayDenied, failure.Kind);
        Assert.Contains("verify the sending domain", failure.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_starttls_demand_is_reported_as_a_tls_mismatch()
        => Assert.Equal(
            OutboundEmailFailureKind.TlsFailure,
            OutboundEmailFailureClassifier.Classify(
                new SmtpException(SmtpStatusCode.MustIssueStartTlsFirst)).Kind);

    [Fact]
    public void A_socket_failure_nested_inside_an_smtp_exception_is_still_a_host_problem()
    {
        var failure = OutboundEmailFailureClassifier.Classify(
            new SmtpException("Failure sending mail.", new SocketException((int)SocketError.HostNotFound)));

        Assert.Equal(OutboundEmailFailureKind.HostUnreachable, failure.Kind);
    }

    [Fact]
    public void A_guard_refusal_is_never_reported_as_a_provider_failure()
        => Assert.Equal(
            OutboundEmailFailureKind.BlockedByOutboundGuard,
            OutboundEmailFailureClassifier.Classify(
                new OutboundEmailBlockedException("refused")).Kind);

    // ==== harness ====================================================================================

    private static OutboundEmailSettingsSnapshot Stored(
        long version, string provider, string? host = null, string? apiKey = null) => new()
    {
        Origin = OutboundEmailSettingsOrigin.Platform,
        Version = version,
        Provider = provider,
        FromAddress = "no-reply@nexora.test",
        FromName = "Nexora",
        AppBaseUrl = "https://app.nexora.test",
        SmtpHost = host,
        SendGridApiKey = apiKey
    };

    private sealed class ResolverHarness
    {
        public ResolverHarness(
            Action<NotificationsOptions>? configured = null,
            HttpMessageHandler? handler = null)
        {
            var services = new ServiceCollection();
            services.AddLogging();
            services.Configure<NotificationsOptions>(options =>
            {
                options.Provider = "console";
                options.FromAddress = "config@nexora.test";
                options.AppBaseUrl = "https://config.nexora.test";
                configured?.Invoke(options);
            });
            services.AddSingleton<IOutboundEmailSettingsSource>(Source);
            Provider = services.BuildServiceProvider();

            Resolver = new OutboundEmailTransportResolver(
                Provider.GetRequiredService<IServiceScopeFactory>(),
                new StubHttpClientFactory(handler ?? new StubHttpMessageHandler(HttpStatusCode.Accepted)),
                Provider.GetRequiredService<IOptionsFactory<NotificationsOptions>>(),
                Provider.GetRequiredService<ILoggerFactory>(),
                Time);
        }

        public FakeSettingsSource Source { get; } = new();
        public AdvanceableTimeProvider Time { get; } = new();
        public ServiceProvider Provider { get; }
        public OutboundEmailTransportResolver Resolver { get; }
    }

    private sealed class FakeSettingsSource : IOutboundEmailSettingsSource
    {
        public OutboundEmailSettingsSnapshot? Snapshot { get; set; }
        public bool Throw { get; set; }
        public int FullReads { get; private set; }
        public int VersionReads { get; private set; }

        public Task<long?> ReadVersionAsync(CancellationToken ct = default)
        {
            VersionReads++;
            if (Throw) throw new InvalidOperationException("42501: permission denied");
            return Task.FromResult(Snapshot?.Version);
        }

        public Task<OutboundEmailSettingsSnapshot?> ReadAsync(CancellationToken ct = default)
        {
            FullReads++;
            if (Throw) throw new InvalidOperationException("42501: permission denied");
            return Task.FromResult(Snapshot);
        }
    }

    private sealed class AdvanceableTimeProvider : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 8, 7, 9, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now = _now.Add(by);
    }

    private sealed class StubHttpClientFactory : IHttpClientFactory
    {
        private readonly HttpMessageHandler _handler;
        public StubHttpClientFactory(HttpMessageHandler handler) => _handler = handler;
        public HttpClient CreateClient(string name) => new(_handler, disposeHandler: false);
    }

    private sealed class StubHttpMessageHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _status;
        public StubHttpMessageHandler(HttpStatusCode status) => _status = status;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(_status) { RequestMessage = request });
    }
}
