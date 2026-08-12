using System.Net;
using System.Security.Claims;
using System.Text.Json;
using ERP_RFQ_Automation.Notifications;
using ERP_RFQ_Automation.Notifications.Runtime;
using ERP_RFQ_Automation.Platform.Models;
using ERP_RFQ_Automation.Platform.Notifications;
using ERP_RFQ_Automation.Platform.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// The platform-plane settings: round-tripping a configuration, keeping the credential unreadable
/// on every surface that leaves the process, and taking effect on the running transport.
/// </summary>
public class PlatformEmailSettingsTests : IDisposable
{
    private readonly PlatformEmailHarness _harness = new();

    public void Dispose() => _harness.Dispose();

    private static SavePlatformEmailSettingsRequest ValidRequest() => new()
    {
        Provider = "smtp",
        FromAddress = "no-reply@customer.test",
        FromName = "Nexora",
        AppBaseUrl = "https://app.customer.test",
        SmtpHost = "smtp.customer.test",
        SmtpPort = 587,
        SmtpUsername = "apikey",
        SmtpPassword = "s3cret-mail-password",
        SmtpEnableSsl = true,
        SmtpTimeoutMs = 30000,
        Reason = "Pilot go-live: activation emails were being discarded by the console provider."
    };

    // ==== round trip =================================================================================

    [Fact]
    public async Task Settings_round_trip_and_the_secret_is_never_readable()
    {
        var saved = await _harness.Service.SaveAsync(ValidRequest(), _harness.Owner, null);
        Assert.True(saved.Succeeded);

        var read = await _harness.Service.GetAsync();

        Assert.Equal("smtp", read.Provider);
        Assert.Equal("smtp.customer.test", read.SmtpHost);
        Assert.Equal("apikey", read.SmtpUsername);
        Assert.True(read.HasSmtpPassword);
        Assert.Equal(nameof(OutboundEmailSettingsOrigin.Platform), read.Origin);

        // The read model reports that a secret is SET and has no property that could carry its
        // value. Serialising the whole DTO is the assertion that stays true when somebody adds a
        // field later.
        var json = JsonSerializer.Serialize(read);
        Assert.DoesNotContain("s3cret-mail-password", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_secret_is_encrypted_at_rest()
    {
        await _harness.Service.SaveAsync(ValidRequest(), _harness.Owner, null);

        var stored = await _harness.ReadRawColumnAsync("SmtpPassword");

        Assert.NotNull(stored);
        Assert.StartsWith("v1:", stored);
        Assert.DoesNotContain("s3cret-mail-password", stored, StringComparison.Ordinal);

        // And it decrypts back through the same converter, so the transport still gets a usable
        // credential. Encryption that broke sending would be its own outage.
        await using var context = _harness.NewContext();
        var row = await context.Set<PlatformEmailSettings>().AsNoTracking().FirstAsync();
        Assert.Equal("s3cret-mail-password", row.SmtpPassword);
    }

    [Fact]
    public async Task A_null_secret_keeps_the_stored_one_and_an_empty_secret_clears_it()
    {
        var first = await _harness.Service.SaveAsync(ValidRequest(), _harness.Owner, null);

        // The console never receives the password, so it can only ever post an empty box back.
        // Null must therefore mean "keep", or changing the port would wipe the credential.
        var edit = ValidRequest();
        edit.SmtpPort = 465;
        edit.SmtpPassword = null;
        edit.ExpectedVersion = first.Settings!.Version;

        var second = await _harness.Service.SaveAsync(edit, _harness.Owner, null);
        Assert.True(second.Succeeded);
        Assert.True(second.Settings!.HasSmtpPassword);

        var clear = ValidRequest();
        clear.SmtpUsername = null;
        clear.SmtpPassword = string.Empty;
        clear.ExpectedVersion = second.Settings.Version;

        var third = await _harness.Service.SaveAsync(clear, _harness.Owner, null);
        Assert.True(third.Succeeded);
        Assert.False(third.Settings!.HasSmtpPassword);
    }

    [Fact]
    public async Task Falls_back_to_configuration_when_no_row_exists()
    {
        var read = await _harness.Service.GetAsync();

        Assert.Equal(nameof(OutboundEmailSettingsOrigin.Configuration), read.Origin);
        Assert.Null(read.Version);
        Assert.Equal("console", read.Provider);
    }

    // ==== effect on the running transport ============================================================

    [Fact]
    public async Task A_saved_configuration_takes_effect_without_a_restart()
    {
        var before = await _harness.Resolver.ResolveAsync();
        Assert.Equal("console", before.Settings.NormalizedProvider);
        Assert.False(before.Settings.TransmitsMail);

        await _harness.Service.SaveAsync(ValidRequest(), _harness.Owner, null);

        var after = await _harness.Resolver.ResolveAsync();
        Assert.Equal("smtp", after.Settings.NormalizedProvider);
        Assert.True(after.Settings.TransmitsMail);
        Assert.Equal("smtp.customer.test", after.Settings.SmtpHost);

        // Decrypted on the way through, so the transport can actually authenticate.
        Assert.Equal("s3cret-mail-password", after.Settings.SmtpPassword);
    }

    [Fact]
    public async Task Every_resolved_transport_is_still_wrapped_by_the_guard()
    {
        var request = ValidRequest();
        request.OutboundGuardMode = nameof(OutboundEmailMode.DraftOnly);
        await _harness.Service.SaveAsync(request, _harness.Owner, null);

        var resolved = await _harness.Resolver.ResolveAsync();
        var message = new EmailMessage { Subject = "contained" };
        message.AddTo("supplier@real-company.test");

        // DraftOnly transmits nothing. Without the decorator this would have opened a real SMTP
        // connection to smtp.customer.test.
        Assert.Null(await resolved.Sender.SendAsync(message));
    }

    // ==== audit ======================================================================================

    [Fact]
    public async Task Audit_records_the_change_and_carries_no_secret()
    {
        await _harness.Service.SaveAsync(ValidRequest(), _harness.Owner, null);

        var entry = Assert.Single(_harness.Audit.Entries);
        Assert.Equal(PlatformEmailSettingsService.ConfigureAction, entry.Action);
        Assert.Equal("PlatformEmailSettings", entry.TargetType);

        var metadata = JsonSerializer.Serialize(entry.Metadata);
        Assert.DoesNotContain("s3cret-mail-password", metadata, StringComparison.Ordinal);

        // The reason and the operator-visible fields ARE recorded — "who changed the From address
        // in March, and why" is a question that gets asked.
        Assert.Contains("Pilot go-live", metadata, StringComparison.Ordinal);
        Assert.Contains("smtpPasswordSet", metadata, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_test_send_is_audited_with_its_outcome_and_no_secret()
    {
        await _harness.Service.SaveAsync(ValidRequest(), _harness.Owner, null);
        _harness.Audit.Entries.Clear();

        await _harness.Service.TestSendAsync(
            new TestOutboundEmailRequest
            {
                Recipient = "ops@nexora.test",
                Settings = new CandidatePlatformEmailSettings { Provider = "console" }
            },
            _harness.Owner, null);

        var entry = Assert.Single(_harness.Audit.Entries);
        Assert.Equal(PlatformEmailSettingsService.TestAction, entry.Action);
        var metadata = JsonSerializer.Serialize(entry.Metadata);
        Assert.DoesNotContain("s3cret-mail-password", metadata, StringComparison.Ordinal);
        Assert.Contains("ops@nexora.test", metadata, StringComparison.Ordinal);
    }

    // ==== concurrency + refusals =====================================================================

    [Fact]
    public async Task A_stale_edit_is_a_conflict_rather_than_a_silent_overwrite()
    {
        var first = await _harness.Service.SaveAsync(ValidRequest(), _harness.Owner, null);

        var stale = ValidRequest();
        stale.ExpectedVersion = first.Settings!.Version - 1;
        stale.SmtpHost = "smtp.someone-elses-idea.test";

        var result = await _harness.Service.SaveAsync(stale, _harness.Owner, null);

        Assert.False(result.Succeeded);
        Assert.True(result.Conflict);

        var read = await _harness.Service.GetAsync();
        Assert.Equal("smtp.customer.test", read.SmtpHost);
    }

    [Theory]
    [InlineData("smtp", null, "Live", null, "https://app.customer.test")]
    [InlineData("sendgrid", "smtp.customer.test", "Live", null, "https://app.customer.test")]
    [InlineData("console", null, "Redirect", null, "https://app.customer.test")]
    [InlineData("console", null, "AllowListOnly", null, "https://app.customer.test")]
    [InlineData("console", null, "Live", null, "app.customer.test")]
    public async Task Configurations_that_could_not_have_worked_are_refused(
        string provider, string? host, string guardMode, string? redirectTo, string appBaseUrl)
    {
        var request = ValidRequest();
        request.Provider = provider;
        request.SmtpHost = host;
        request.SmtpUsername = null;
        request.SmtpPassword = null;
        request.SendGridApiKey = null;
        request.OutboundGuardMode = guardMode;
        request.OutboundGuardRedirectTo = redirectTo;
        request.AppBaseUrl = appBaseUrl;

        var result = await _harness.Service.SaveAsync(request, _harness.Owner, null);

        Assert.False(result.Succeeded);
        Assert.False(result.Conflict);
        Assert.False(string.IsNullOrWhiteSpace(result.Error));

        // A refused save must leave nothing behind — including the entity it had already added to
        // the change tracker.
        await using var context = _harness.NewContext();
        Assert.False(await context.Set<PlatformEmailSettings>().AnyAsync());
    }

    [Fact]
    public async Task A_refused_save_is_audited_as_a_failure_so_it_is_not_invisible_afterwards()
    {
        // Nothing in the settings table and nothing in the audit log is what "the email screen will
        // not save" used to look like from the server — indistinguishable from an operator who
        // never pressed the button. The refusal is the only thing that can tell those apart.
        var request = ValidRequest();
        request.SmtpHost = null; // provider 'smtp' with no host

        var result = await _harness.Service.SaveAsync(request, _harness.Owner, null);
        Assert.False(result.Succeeded);

        var recorded = Assert.Single(_harness.Audit.Entries);
        Assert.Equal(PlatformEmailSettingsService.ConfigureAction, recorded.Action);
        Assert.Equal(PlatformAuditResults.Failure, recorded.Result);

        // The refusal text is what an operator needs read back to them. The credential is not in it,
        // and is not anywhere else on this path either.
        var metadata = JsonSerializer.Serialize(recorded.Metadata);
        Assert.Contains("requires an SMTP host", metadata, StringComparison.Ordinal);
        Assert.DoesNotContain("s3cret-mail-password", metadata, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_conflict_is_audited_as_a_failure_too()
    {
        var first = await _harness.Service.SaveAsync(ValidRequest(), _harness.Owner, null);
        _harness.Audit.Entries.Clear();

        var stale = ValidRequest();
        stale.ExpectedVersion = first.Settings!.Version - 1;

        var result = await _harness.Service.SaveAsync(stale, _harness.Owner, null);
        Assert.True(result.Conflict);

        var recorded = Assert.Single(_harness.Audit.Entries);
        Assert.Equal(PlatformAuditResults.Failure, recorded.Result);
        Assert.Contains("conflict", JsonSerializer.Serialize(recorded.Metadata), StringComparison.OrdinalIgnoreCase);
    }

    // ==== status readout =============================================================================

    [Fact]
    public async Task Status_says_plainly_that_a_console_deployment_is_sending_nothing()
    {
        var status = await _harness.Service.GetStatusAsync();

        Assert.False(status.IsSending);
        Assert.Equal("console", status.Provider);
        Assert.Contains("NOT being sent", status.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Status_reports_the_durable_verification_stamp()
    {
        var request = ValidRequest();
        request.Provider = "console";
        request.SmtpUsername = null;
        request.SmtpPassword = null;
        await _harness.Service.SaveAsync(request, _harness.Owner, null);

        await _harness.Service.TestSendAsync(
            new TestOutboundEmailRequest { Recipient = "ops@nexora.test" }, _harness.Owner, null);

        // A console "success" transmitted nothing, so it must NOT be recorded as a verification.
        var afterConsole = await _harness.Service.GetStatusAsync();
        Assert.Null(afterConsole.LastVerifiedAtUtc);

        var failing = await _harness.Service.TestSendAsync(
            new TestOutboundEmailRequest
            {
                Recipient = "ops@nexora.test",
                Settings = new CandidatePlatformEmailSettings { Provider = "sendgrid", SendGridApiKey = "SG.rotated" }
            },
            _harness.Owner, null);

        Assert.False(failing.Succeeded);

        var afterFailure = await _harness.Service.GetStatusAsync();
        Assert.Equal(nameof(OutboundEmailFailureKind.AuthenticationFailed), afterFailure.LastVerificationFailureKind);
        Assert.NotNull(afterFailure.LastVerificationFailureAtUtc);
    }

    // ==== harness ====================================================================================

    /// <summary>
    /// A SQLite database built from <c>ApplyPlatformEmailModel</c> alone, plus the real resolver
    /// reading through the real store. The context splice that puts this entity into
    /// <c>ErpRfqAutomationContext</c> is owned elsewhere, so the module's own tests build the model
    /// from the extension they hand over — which also proves that extension is self-sufficient.
    /// </summary>
    internal sealed class PlatformEmailHarness : IDisposable
    {
        private readonly SqliteConnection _connection;
        private readonly DbContextOptions<PlatformEmailTestContext> _options;
        private readonly ServiceProvider _provider;

        public PlatformEmailHarness()
        {
            _connection = new SqliteConnection("Data Source=:memory:");
            _connection.Open();
            _options = new DbContextOptionsBuilder<PlatformEmailTestContext>()
                .UseSqlite(_connection)
                .Options;

            using (var context = NewContext())
                context.Database.EnsureCreated();

            var services = new ServiceCollection();
            services.AddLogging();
            services.Configure<NotificationsOptions>(options =>
            {
                options.Provider = "console";
                options.FromAddress = "config@nexora.test";
                options.AppBaseUrl = "https://config.nexora.test";
            });
            services.AddScoped<IOutboundEmailSettingsSource>(_ => new PlatformEmailSettingsStore(NewContext()));
            _provider = services.BuildServiceProvider();

            Resolver = new OutboundEmailTransportResolver(
                _provider.GetRequiredService<IServiceScopeFactory>(),
                new UnauthorizedHttpClientFactory(),
                _provider.GetRequiredService<IOptionsFactory<NotificationsOptions>>(),
                _provider.GetRequiredService<ILoggerFactory>());

            Service = new PlatformEmailSettingsService(
                NewContext(),
                Resolver,
                new OutboundEmailProbe(Resolver, NullLogger<OutboundEmailProbe>.Instance),
                new OutboundEmailHealth(),
                Audit,
                NullLogger<PlatformEmailSettingsService>.Instance);
        }

        public OutboundEmailTransportResolver Resolver { get; }
        public PlatformEmailSettingsService Service { get; }
        public RecordingAuditService Audit { get; } = new();

        public ClaimsPrincipal Owner { get; } = new(new ClaimsIdentity(new[]
        {
            new Claim(System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.Sub, "1"),
            new Claim(ClaimTypes.Email, "owner@nexora.test")
        }, "Platform"));

        public PlatformEmailTestContext NewContext() => new(_options);

        public async Task<string?> ReadRawColumnAsync(string column)
        {
            // Raw ADO, deliberately: reading through EF would apply the value converter and show the
            // plaintext, which is precisely what must not be visible here. The table name is looked
            // up rather than assumed, because SQLite has no schemas and EF folds "platform" into the
            // table name itself.
            await using var lookup = _connection.CreateCommand();
            lookup.CommandText =
                "SELECT name FROM sqlite_master WHERE type = 'table' AND name LIKE '%PlatformEmailSettings'";
            var table = (string?)await lookup.ExecuteScalarAsync();
            Assert.False(string.IsNullOrEmpty(table));

            await using var command = _connection.CreateCommand();
            command.CommandText = $"SELECT \"{column}\" FROM \"{table}\" LIMIT 1";
            return (await command.ExecuteScalarAsync()) as string;
        }

        public void Dispose()
        {
            _provider.Dispose();
            _connection.Dispose();
        }
    }

    /// <summary>Every SendGrid call is refused, so a verification against a candidate key exercises
    /// the failure classification without reaching the network.</summary>
    private sealed class UnauthorizedHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new Handler(), disposeHandler: true);

        private sealed class Handler : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request, CancellationToken cancellationToken)
                => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized));
        }
    }

    internal sealed record RecordedAudit(
        string Action, string? TargetType, string? TargetId, object? Metadata, string Result);

    internal sealed class RecordingAuditService : IPlatformAuditService
    {
        public List<RecordedAudit> Entries { get; } = new();

        public Task WriteAsync(
            ClaimsPrincipal actor, string action, string? targetType = null, string? targetId = null,
            object? metadata = null, long? actAsTenantId = null, HttpContext? httpContext = null,
            CancellationToken ct = default)
            => WriteAsync(actor, action, targetType, targetId, metadata, actAsTenantId, httpContext,
                PlatformAuditResults.Success, ct);

        public Task WriteAsync(
            ClaimsPrincipal actor, string action, string? targetType, string? targetId, object? metadata,
            long? actAsTenantId, HttpContext? httpContext, string result, CancellationToken ct = default)
        {
            Entries.Add(new RecordedAudit(action, targetType, targetId, metadata, result));
            return Task.CompletedTask;
        }
    }
}

/// <summary>
/// A context carrying only the platform email model. Exists because the splice into
/// <c>ErpRfqAutomationContext</c> is owned by the migration author; building the model from the
/// shipped <c>ApplyPlatformEmailModel</c> extension tests exactly what will be spliced.
/// </summary>
public sealed class PlatformEmailTestContext : DbContext
{
    public PlatformEmailTestContext(DbContextOptions<PlatformEmailTestContext> options) : base(options) { }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
        => modelBuilder.ApplyPlatformEmailModel();
}
