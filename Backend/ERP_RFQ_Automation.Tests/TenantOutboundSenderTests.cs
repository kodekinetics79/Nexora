using System.Security.Claims;
using ERP_RFQ_Automation.Authorization;
using ERP_RFQ_Automation.Controllers;
using ERP_RFQ_Automation.Email;
using ERP_RFQ_Automation.Mailbox;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.MultiTenancy;
using ERP_RFQ_Automation.Notifications;
using ERP_RFQ_Automation.Notifications.Providers;
using ERP_RFQ_Automation.Notifications.Runtime;
using ERP_RFQ_Automation.Procurement;
using ERP_RFQ_Automation.Security;
using ERP_RFQ_Automation.Tests.Support;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MimeKit;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// Issue #54: a message leaves from the VERIFIED sender of the tenant that owns it — the tenant's
/// own active SMTP mailbox when it has one, the platform address otherwise, never another
/// tenant's — and the mailbox screen reports the same answer the sender uses.
///
/// <para>Fixture rows carry the shape production carries (2026-09-02, BU 7 row 10): protocol
/// <c>SMTP</c>, <c>smtpout.secureserver.net:465</c>, <c>UseSSL=t</c>, an encrypted password (the
/// module initialiser installs the test key), <c>IsActive=t</c>, plus the INACTIVE SMTP and IMAP
/// siblings a real tenant accumulates, so "lowest active SMTP id" is exercised and not assumed.</para>
/// </summary>
public sealed class TenantOutboundSenderTests
{
    private const long TenantA = 71_007;
    private const long TenantB = 71_008;
    private const long TenantWithoutMailbox = 71_009;

    // ==== the send path ===========================================================================

    [Fact]
    public async Task A_message_owned_by_a_tenant_leaves_from_that_tenants_active_smtp_mailbox()
    {
        using var harness = new Harness(platformProvider: "console");
        harness.SeedTenant(TenantA, "Noor & Sons LLC", "sales@noor-sons.test");

        var message = Message(TenantA, "Quotation Q-1001");
        var receipt = await harness.Sender.SendAsync(message);

        Assert.NotNull(receipt);
        Assert.Equal("smtp", receipt!.Provider);
        Assert.Equal(1, harness.Transport.SendCount);
        var configuration = Assert.IsType<EmailConfiguration>(harness.Transport.LastConfiguration);
        Assert.Equal(TenantA, configuration.BusinessUnitId);
        Assert.Equal(harness.ActiveSmtpRowId(TenantA), configuration.Id);
        Assert.Equal("SMTP", configuration.Protocol);
        Assert.Equal(465, configuration.Port);
        Assert.True(configuration.UseSsl);
        Assert.Equal("app-password-A", configuration.Password);

        var from = Assert.Single(harness.Transport.LastMessage!.From.Mailboxes);
        Assert.Equal("sales@noor-sons.test", from.Address);
        Assert.Equal("Noor & Sons LLC", from.Name);
    }

    [Fact]
    public async Task A_tenant_with_no_smtp_mailbox_falls_back_to_the_platform_sender()
    {
        using var harness = new Harness(platformProvider: "console");
        harness.SeedTenant(TenantWithoutMailbox, "Mailbox-less Trading", smtpAddress: null);

        var resolved = await harness.Resolver.ResolveAsync(TenantWithoutMailbox);
        var receipt = await harness.Sender.SendAsync(Message(TenantWithoutMailbox, "Quotation Q-2"));

        Assert.Equal(OutboundSenderOrigin.Configuration, resolved.Origin);
        Assert.Equal("platform@nexora.test", resolved.FromAddress);
        Assert.Null(resolved.MailboxId);
        Assert.Null(receipt);                       // console provider: logged, not sent
        Assert.Equal(0, harness.Transport.SendCount);
    }

    [Fact]
    public async Task System_mail_with_no_owner_uses_the_platform_sender_even_when_tenants_have_mailboxes()
    {
        using var harness = new Harness(platformProvider: "console");
        harness.SeedTenant(TenantA, "Noor & Sons LLC", "sales@noor-sons.test");

        var resolved = await harness.Resolver.ResolveAsync(null);
        await harness.Sender.SendAsync(Message(owner: null, "Password reset"));

        Assert.Equal(OutboundSenderOrigin.Configuration, resolved.Origin);
        Assert.Equal(0, harness.Transport.SendCount);
    }

    [Fact]
    public async Task A_tenant_never_sends_through_another_tenants_mailbox()
    {
        using var harness = new Harness(platformProvider: "console");
        harness.SeedTenant(TenantA, "Noor & Sons LLC", "sales@noor-sons.test");
        harness.SeedTenant(TenantB, "Gulf Fasteners", "quotes@gulf-fasteners.test");

        await harness.Sender.SendAsync(Message(TenantA, "A's quote"));
        var aRow = harness.Transport.LastConfiguration!;
        await harness.Sender.SendAsync(Message(TenantB, "B's quote"));
        var bRow = harness.Transport.LastConfiguration!;

        Assert.Equal(TenantA, aRow.BusinessUnitId);
        Assert.Equal("sales@noor-sons.test", aRow.EmailAddress);
        Assert.Equal(TenantB, bRow.BusinessUnitId);
        Assert.Equal("quotes@gulf-fasteners.test", bRow.EmailAddress);

        // Under a scope pushed for A, asking for B's sender is a bug — refused, not "no mailbox".
        using (harness.TenantScope.Push(TenantA))
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => harness.Resolver.ResolveAsync(TenantB));
        }

        // Under a scope pushed for A, A's own sender resolves — the dispatcher's shape.
        using (harness.TenantScope.Push(TenantA))
        {
            var resolved = await harness.Resolver.ResolveAsync(TenantA);
            Assert.Equal(OutboundSenderOrigin.Tenant, resolved.Origin);
            Assert.Equal("sales@noor-sons.test", resolved.FromAddress);
        }
    }

    [Fact]
    public async Task The_tenant_sender_refuses_a_message_owned_by_a_different_unit()
    {
        var transport = new RecordingSmtpTransport();
        var mailbox = new TenantOutboundSender(TenantA, 1, "Sales - SMTP", "sales@noor-sons.test", "Noor & Sons LLC",
            new EmailConfiguration { Id = 1, BusinessUnitId = TenantA, Host = "smtpout.secureserver.net", Port = 465, UseSsl = true, Username = "u", Password = "p", Protocol = "SMTP", EmailAddress = "sales@noor-sons.test", ConfigurationName = "Sales - SMTP" });
        var sender = new TenantSmtpEmailSender(mailbox, transport, NullLogger<TenantSmtpEmailSender>.Instance);

        await Assert.ThrowsAsync<InvalidOperationException>(() => sender.SendAsync(Message(TenantB, "wrong owner")));
        Assert.Equal(0, transport.SendCount);
    }

    [Fact]
    public async Task The_tenant_sender_keeps_the_verified_from_and_demotes_a_caller_supplied_from_to_reply_to()
    {
        var transport = new RecordingSmtpTransport();
        var mailbox = new TenantOutboundSender(TenantA, 1, "Sales - SMTP", "sales@noor-sons.test", "Noor & Sons LLC",
            new EmailConfiguration { Id = 1, BusinessUnitId = TenantA, Host = "smtpout.secureserver.net", Port = 465, UseSsl = true, Username = "u", Password = "p", Protocol = "SMTP", EmailAddress = "sales@noor-sons.test", ConfigurationName = "Sales - SMTP" });
        var sender = new TenantSmtpEmailSender(mailbox, transport, NullLogger<TenantSmtpEmailSender>.Instance);

        var message = Message(TenantA, "spoof attempt");
        message.From = new EmailAddress("ceo@other-company.test", "Someone Else");
        await sender.SendAsync(message);

        var sent = transport.LastMessage!;
        Assert.Equal("sales@noor-sons.test", Assert.Single(sent.From.Mailboxes).Address);
        Assert.Equal("ceo@other-company.test", Assert.Single(sent.ReplyTo.Mailboxes).Address);
    }

    // ==== supplier RFQ dispatch ===================================================================

    [Fact]
    public async Task Supplier_rfq_dispatch_treats_a_tenant_mailbox_as_configured_when_the_platform_provider_is_console()
    {
        // The exact production complaint behind #54: platform provider console (or unset), the
        // tenant's SMTP row active, the screen saying quotes WILL be sent — and dispatch
        // dead-lettering DELIVERY_PROVIDER_NOT_CONFIGURED because it read only the platform flag.
        var configuration = new ProcurementDeliveryConfiguration(
            Options.Create(new NotificationsOptions { Provider = "console" }),
            new StubSenderResolver(OutboundSenderOrigin.Tenant, transmits: true));

        Assert.False(configuration.IsConfigured);               // platform-wide answer, unchanged
        var readiness = await configuration.ResolveAsync(TenantA);
        Assert.True(readiness.IsConfigured);                     // this tenant's answer
        Assert.Equal("tenant", readiness.Origin);
        Assert.Equal("smtp", readiness.ProviderName);

        var absent = new ProcurementDeliveryConfiguration(
            Options.Create(new NotificationsOptions { Provider = "console" }),
            new StubSenderResolver(OutboundSenderOrigin.Configuration, transmits: false));
        Assert.False((await absent.ResolveAsync(TenantWithoutMailbox)).IsConfigured);
    }

    // ==== the screen says what the sender does =====================================================

    [Fact]
    public async Task Outbound_status_reports_the_tenant_mailbox_the_sender_will_use()
    {
        using var harness = new Harness(platformProvider: "console");
        harness.SeedTenant(TenantA, "Noor & Sons LLC", "sales@noor-sons.test");

        var controller = harness.MailboxController(TenantA, "admin@noor-sons.test");
        var status = Assert.IsType<OutboundMailStatusDTO>(
            Assert.IsType<OkObjectResult>((await controller.GetOutboundStatus()).Result).Value);

        var resolved = await harness.Resolver.ResolveAsync(TenantA);
        Assert.Equal("tenant", status.SenderOrigin);
        Assert.Equal(resolved.FromAddress, status.SenderAddress);
        Assert.Equal(resolved.FromName, status.SenderName);
        Assert.Equal(resolved.MailboxId, status.SenderMailboxId);
        Assert.Equal("smtpout.secureserver.net", status.SenderHost);
        Assert.True(status.CanSendToCustomers);
        Assert.Equal(1, status.ActiveSmtpCount);       // the inactive SMTP sibling is not counted
        Assert.False(status.HasAmbiguousOutbound);
        Assert.Contains("sales@noor-sons.test", status.Summary);
        Assert.Contains("smtpout.secureserver.net", status.Summary);
        Assert.DoesNotContain("WILL be delivered", status.Summary);
    }

    [Fact]
    public async Task Outbound_status_says_the_platform_address_sends_when_the_tenant_has_no_mailbox()
    {
        using var harness = new Harness(platformProvider: "smtp");
        harness.SeedTenant(TenantWithoutMailbox, "Mailbox-less Trading", smtpAddress: null);

        var controller = harness.MailboxController(TenantWithoutMailbox, "admin@mailbox-less.test");
        var status = Assert.IsType<OutboundMailStatusDTO>(
            Assert.IsType<OkObjectResult>((await controller.GetOutboundStatus()).Result).Value);

        Assert.Equal("configuration", status.SenderOrigin);
        Assert.Equal("platform@nexora.test", status.SenderAddress);
        Assert.Null(status.SenderMailboxId);
        Assert.True(status.CanSendToCustomers);        // the platform transport transmits
        Assert.Equal(0, status.ActiveSmtpCount);
        Assert.Contains("platform address platform@nexora.test", status.Summary);
    }

    [Fact]
    public async Task Outbound_status_is_contained_when_neither_tenant_nor_platform_can_send()
    {
        using var harness = new Harness(platformProvider: "console");
        harness.SeedTenant(TenantWithoutMailbox, "Mailbox-less Trading", smtpAddress: null);

        var controller = harness.MailboxController(TenantWithoutMailbox, "admin@mailbox-less.test");
        var status = Assert.IsType<OutboundMailStatusDTO>(
            Assert.IsType<OkObjectResult>((await controller.GetOutboundStatus()).Result).Value);

        Assert.False(status.CanSendToCustomers);
        Assert.Contains("contained", status.Summary, StringComparison.OrdinalIgnoreCase);
    }

    // ==== send test ================================================================================

    [Fact]
    public async Task Send_test_goes_through_the_tenant_mailbox_to_the_signed_in_user()
    {
        using var harness = new Harness(platformProvider: "console");
        harness.SeedTenant(TenantA, "Noor & Sons LLC", "sales@noor-sons.test");
        var controller = harness.MailboxController(TenantA, "admin@noor-sons.test");

        var result = Assert.IsType<OutboundEmailProbeResult>(Assert.IsType<OkObjectResult>(
            (await controller.SendTest(harness.ActiveSmtpRowId(TenantA),
                new MailboxSendTestRequestDTO { Recipient = "admin@noor-sons.test" })).Result).Value);

        Assert.True(result.Succeeded);
        Assert.True(result.Transmitted);
        Assert.Equal("smtp", result.Provider);
        Assert.Equal(1, harness.Transport.SendCount);
        Assert.Equal("admin@noor-sons.test", Assert.Single(harness.Transport.LastMessage!.To.Mailboxes).Address);
        Assert.Equal("sales@noor-sons.test", Assert.Single(harness.Transport.LastMessage!.From.Mailboxes).Address);
        Assert.Contains(harness.Audit.Entries, e => e.Action == IamAuditActions.MailboxTested);
    }

    [Fact]
    public async Task Send_test_refuses_any_recipient_that_is_not_the_caller_or_the_mailbox()
    {
        using var harness = new Harness(platformProvider: "console");
        harness.SeedTenant(TenantA, "Noor & Sons LLC", "sales@noor-sons.test");
        var controller = harness.MailboxController(TenantA, "admin@noor-sons.test");

        var refused = await controller.SendTest(harness.ActiveSmtpRowId(TenantA),
            new MailboxSendTestRequestDTO { Recipient = "victim@somewhere-else.test" });

        Assert.IsType<BadRequestObjectResult>(refused.Result);
        Assert.Equal(0, harness.Transport.SendCount);
    }

    [Fact]
    public async Task Send_test_cannot_reach_another_tenants_mailbox()
    {
        using var harness = new Harness(platformProvider: "console");
        harness.SeedTenant(TenantA, "Noor & Sons LLC", "sales@noor-sons.test");
        harness.SeedTenant(TenantB, "Gulf Fasteners", "quotes@gulf-fasteners.test");
        var controller = harness.MailboxController(TenantB, "admin@gulf-fasteners.test");

        var result = await controller.SendTest(harness.ActiveSmtpRowId(TenantA),
            new MailboxSendTestRequestDTO { Recipient = "admin@gulf-fasteners.test" });

        Assert.IsType<NotFoundObjectResult>(result.Result);
        Assert.Equal(0, harness.Transport.SendCount);
    }

    // ==== harness =================================================================================

    private static EmailMessage Message(long? owner, string subject)
    {
        var message = new EmailMessage
        {
            Subject = subject,
            HtmlBody = "<p>body</p>",
            OwningBusinessUnitId = owner,
            BusinessUnitId = owner?.ToString(),
            TenantId = owner?.ToString()
        };
        message.AddTo("buyer@customer.test", "Buyer");
        return message;
    }

    private sealed class Harness : IDisposable
    {
        private readonly TestDb _database = new();
        private readonly ServiceProvider _provider;

        public Harness(string platformProvider)
        {
            var services = new ServiceCollection();
            services.AddLogging();
            services.Configure<NotificationsOptions>(options =>
            {
                options.Provider = platformProvider;
                options.FromAddress = "platform@nexora.test";
                options.FromName = "Nexora";
                options.AppBaseUrl = "https://app.nexora.test";
                options.Smtp.Host = "platform-smtp.nexora.test";
                options.Smtp.Port = 587;
                options.Smtp.Username = "platform";
                options.Smtp.Password = "platform-secret";
            });
            services.AddSingleton<ITenantScopeAccessor>(TenantScope);
            services.AddScoped(_ => _database.ContextFor(TenantScope.BusinessUnitId));
            services.AddScoped<ITenantOutboundSenderSource, TenantOutboundSenderSource>();
            services.AddSingleton<IOutboundSmtpTransport>(Transport);
            _provider = services.BuildServiceProvider();

            var transports = new OutboundEmailTransportResolver(
                _provider.GetRequiredService<IServiceScopeFactory>(),
                new NoHttpClientFactory(),
                _provider.GetRequiredService<IOptionsFactory<NotificationsOptions>>(),
                _provider.GetRequiredService<ILoggerFactory>());
            Resolver = new OutboundSenderResolver(
                transports,
                _provider.GetRequiredService<IServiceScopeFactory>(),
                Transport,
                _provider.GetRequiredService<ILoggerFactory>());
            Sender = new RuntimeConfiguredEmailSender(
                transports, new OutboundEmailHealth(), NullLogger<RuntimeConfiguredEmailSender>.Instance,
                senders: Resolver);
            Probe = new OutboundEmailProbe(transports, NullLogger<OutboundEmailProbe>.Instance);
        }

        public TenantScopeAccessor TenantScope { get; } = new();
        public RecordingSmtpTransport Transport { get; } = new();
        public OutboundSenderResolver Resolver { get; }
        public RuntimeConfiguredEmailSender Sender { get; }
        public OutboundEmailProbe Probe { get; }
        public RecordingAuditWriter Audit { get; } = new();

        /// <summary>The rows a real tenant carries: an IMAP inbox, a retired SMTP row, and the
        /// live SMTP row — in that id order, so "lowest active SMTP" must skip two rows.</summary>
        public void SeedTenant(long businessUnitId, string companyName, string? smtpAddress)
        {
            using var db = _database.ContextFor(null);
            var unit = Seed.EnsureBusinessUnit(db, businessUnitId);
            unit.BusinessUnitName = companyName;
            var baseId = businessUnitId * 10;
            db.EmailConfigurations.Add(Row(baseId + 1, businessUnitId, "Sales - IMAP", "IMAP",
                "imap.secureserver.net", 993, $"inbox@{businessUnitId}.test", true, "imap-password", active: true));
            db.EmailConfigurations.Add(Row(baseId + 2, businessUnitId, "Old SMTP", "SMTP",
                "mail.spacemail.com", 465, $"old@{businessUnitId}.test", true, "old-password", active: false));
            if (smtpAddress is not null)
                db.EmailConfigurations.Add(Row(baseId + 3, businessUnitId, "Sales - SMTP", "SMTP",
                    "smtpout.secureserver.net", 465, smtpAddress, true,
                    businessUnitId == TenantA ? "app-password-A" : "app-password-B", active: true));
            db.SaveChanges();
        }

        public long ActiveSmtpRowId(long businessUnitId) => businessUnitId * 10 + 3;

        private static EmailConfiguration Row(long id, long bu, string name, string protocol, string host, int port,
            string address, bool ssl, string password, bool active) => new()
        {
            Id = id, BusinessUnitId = bu, ConfigurationName = name, EmailAddress = address, Protocol = protocol,
            Host = host, Port = port, Username = address, Password = password, UseSsl = ssl,
            PollingInterval = 5, IsActive = active, CreatedOn = DateTime.UtcNow, ConsecutivePollFailures = 0
        };

        public MailboxController MailboxController(long businessUnitId, string callerEmail)
        {
            var context = _database.ContextFor(businessUnitId);
            return new MailboxController(context, new UnreachableTester(), Audit,
                NullLogger<MailboxController>.Instance, Resolver, Probe)
            {
                ControllerContext = new ControllerContext
                {
                    HttpContext = new DefaultHttpContext
                    {
                        User = new ClaimsPrincipal(new ClaimsIdentity(
                        [
                            new Claim("businessUnitId", businessUnitId.ToString()),
                            new Claim(ClaimTypes.NameIdentifier, "7"),
                            new Claim(ClaimTypes.Email, callerEmail)
                        ], "test"))
                    }
                }
            };
        }

        public void Dispose()
        {
            _provider.Dispose();
            _database.Dispose();
        }
    }

    private sealed class RecordingSmtpTransport : IOutboundSmtpTransport
    {
        public int SendCount { get; private set; }
        public EmailConfiguration? LastConfiguration { get; private set; }
        public MimeMessage? LastMessage { get; private set; }

        public Task SendAsync(EmailConfiguration configuration, MimeMessage message, CancellationToken cancellationToken)
        {
            SendCount++;
            LastConfiguration = configuration;
            LastMessage = message;
            return Task.CompletedTask;
        }
    }

    private sealed class StubSenderResolver(OutboundSenderOrigin origin, bool transmits) : IOutboundSenderResolver
    {
        private static readonly OutboundEmailSettingsSnapshot Platform =
            OutboundEmailSettingsSnapshot.FromOptions(new NotificationsOptions { Provider = "console" });

        public Task<ResolvedOutboundSender> ResolveAsync(long? businessUnitId, CancellationToken ct = default)
            => Task.FromResult(new ResolvedOutboundSender(origin,
                new GuardedEmailSender(new ConsoleEmailSender(NullLogger<ConsoleEmailSender>.Instance),
                    Options.Create(new NotificationsOptions()), NullLogger<GuardedEmailSender>.Instance),
                transmits ? "smtp" : "console", transmits, OutboundEmailMode.Live,
                "x@y.test", "X", null, null, null, Platform));

        public ResolvedOutboundSender ForMailbox(TenantOutboundSender mailbox, OutboundEmailSettingsSnapshot platformSettings)
            => throw new NotSupportedException();
    }

    private sealed class NoHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }

    private sealed class UnreachableTester : IMailConnectionTester
    {
        public Task<MailConnectionTestResult> TestAsync(MailConnectionTestRequest request, CancellationToken cancellationToken)
            => throw new NotSupportedException("No unit test may open a mail connection.");
    }

    private sealed class RecordingAuditWriter : IIamAuditWriter
    {
        public List<IamAuditEntry> Entries { get; } = [];

        public IamAuditEvent Enlist(ClaimsPrincipal? principal, IamAuditEntry entry)
        {
            Entries.Add(entry);
            return new IamAuditEvent { Action = entry.Action, TargetType = entry.TargetType };
        }

        public Task<IamAuditEvent> WriteAsync(ClaimsPrincipal? principal, IamAuditEntry entry, CancellationToken cancellationToken = default)
            => Task.FromResult(Enlist(principal, entry));

        public Task<Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction?> BeginAtomicAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction?>(null);

        public Task ExecuteAtomicAsync(Func<Task> work, CancellationToken cancellationToken = default) => work();
    }
}
