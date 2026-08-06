using ERP_RFQ_Automation.Notifications;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// Containment for outbound mail. The scenario being defended against is concrete: a rehearsal
/// of supplier RFQ dispatch runs against real supplier records, and a real buyer at a real
/// company receives a commercial approach from Nexora that nobody authorised. That is not a bug
/// you can roll back.
/// </summary>
public sealed class OutboundEmailGuardTests
{
    private sealed class RecordingSender : IEmailSender
    {
        public List<EmailMessage> Sent { get; } = new();
        public Task<EmailDeliveryReceipt?> SendAsync(EmailMessage message, CancellationToken ct = default)
        {
            Sent.Add(message);
            return Task.FromResult<EmailDeliveryReceipt?>(null);
        }
    }

    private static (GuardedEmailSender guard, RecordingSender inner) Build(OutboundEmailGuardOptions guard)
    {
        var inner = new RecordingSender();
        var options = Options.Create(new NotificationsOptions { OutboundGuard = guard });
        return (new GuardedEmailSender(inner, options, NullLogger<GuardedEmailSender>.Instance), inner);
    }

    private static EmailMessage ToSupplier(string address = "buyer@real-supplier.com")
    {
        var m = new EmailMessage { Subject = "RFQ SR-2026-0042 — Ball valve 2IN class 300" };
        m.AddTo(address, "Real Supplier");
        return m;
    }

    // ---------------------------------------------------------------- default is unchanged

    [Fact]
    public async Task DefaultIsLive_SoBindingTheSectionChangesNothingForAnExistingDeployment()
    {
        var (guard, inner) = Build(new OutboundEmailGuardOptions());

        await guard.SendAsync(ToSupplier());

        var sent = Assert.Single(inner.Sent);
        Assert.Equal("buyer@real-supplier.com", Assert.Single(sent.To).Address);
        Assert.DoesNotContain("[NEXORA TEST]", sent.Subject); // Live must not rewrite anything
    }

    // ---------------------------------------------------------------- Redirect

    [Fact]
    public async Task Redirect_ReroutesEveryRecipientToTheSink()
    {
        var (guard, inner) = Build(new OutboundEmailGuardOptions
        {
            Mode = nameof(OutboundEmailMode.Redirect),
            RedirectTo = "sink@localhost"
        });
        var message = ToSupplier();
        message.Cc.Add(new EmailAddress("cc@real-supplier.com", null));
        message.Bcc.Add(new EmailAddress("bcc@another-supplier.com", null));

        await guard.SendAsync(message);

        var sent = Assert.Single(inner.Sent);
        Assert.Equal("sink@localhost", Assert.Single(sent.To).Address);
        // Cc/Bcc CLEARED, not redirected — a surviving Bcc is exactly how a real address slips
        // through a rehearsal unnoticed.
        Assert.Empty(sent.Cc);
        Assert.Empty(sent.Bcc);
    }

    [Fact]
    public async Task Redirect_TagsTheSubjectSoAHumanCannotMistakeItForReal()
    {
        var (guard, inner) = Build(new OutboundEmailGuardOptions
        {
            Mode = nameof(OutboundEmailMode.Redirect),
            RedirectTo = "sink@localhost"
        });

        await guard.SendAsync(ToSupplier());

        Assert.StartsWith("[NEXORA TEST]", Assert.Single(inner.Sent).Subject);
    }

    [Fact]
    public async Task Redirect_WithNoSinkConfigured_RefusesRatherThanFallingBackToLive()
    {
        var (guard, inner) = Build(new OutboundEmailGuardOptions { Mode = nameof(OutboundEmailMode.Redirect) });

        await Assert.ThrowsAsync<OutboundEmailBlockedException>(() => guard.SendAsync(ToSupplier()));
        Assert.Empty(inner.Sent);
    }

    // ---------------------------------------------------------------- AllowListOnly

    [Fact]
    public async Task AllowListOnly_RefusesAnAddressThatIsNotListed()
    {
        var (guard, inner) = Build(new OutboundEmailGuardOptions
        {
            Mode = nameof(OutboundEmailMode.AllowListOnly),
            AllowedRecipients = { "qa@nexora.sa" }
        });

        var ex = await Assert.ThrowsAsync<OutboundEmailBlockedException>(() => guard.SendAsync(ToSupplier()));
        Assert.Contains("buyer@real-supplier.com", ex.Message);
        Assert.Empty(inner.Sent);
    }

    [Fact]
    public async Task AllowListOnly_PermitsAnExactAddress_CaseInsensitively()
    {
        var (guard, inner) = Build(new OutboundEmailGuardOptions
        {
            Mode = nameof(OutboundEmailMode.AllowListOnly),
            AllowedRecipients = { "QA@Nexora.sa" }
        });

        await guard.SendAsync(ToSupplier("qa@nexora.sa"));

        Assert.Single(inner.Sent);
    }

    [Theory]
    [InlineData("nexora.sa")]
    [InlineData("@nexora.sa")]
    public async Task AllowListOnly_PermitsAWholeDomain_WithOrWithoutTheAtSign(string domain)
    {
        var (guard, inner) = Build(new OutboundEmailGuardOptions
        {
            Mode = nameof(OutboundEmailMode.AllowListOnly),
            AllowedDomains = { domain }
        });

        await guard.SendAsync(ToSupplier("anyone@nexora.sa"));

        Assert.Single(inner.Sent);
    }

    [Fact]
    public async Task AllowListOnly_RefusesTheWholeMessageWhenOnlyOneRecipientIsNotListed()
    {
        // Fails closed on the message, not per recipient: delivering "the allowed part" of a
        // supplier RFQ is a silent partial send nobody asked for.
        var (guard, inner) = Build(new OutboundEmailGuardOptions
        {
            Mode = nameof(OutboundEmailMode.AllowListOnly),
            AllowedDomains = { "nexora.sa" }
        });
        var message = ToSupplier("qa@nexora.sa");
        message.Bcc.Add(new EmailAddress("buyer@real-supplier.com", null));

        await Assert.ThrowsAsync<OutboundEmailBlockedException>(() => guard.SendAsync(message));
        Assert.Empty(inner.Sent);
    }

    [Fact]
    public async Task AllowListOnly_WithAnEmptyList_RefusesEverything()
    {
        var (guard, inner) = Build(new OutboundEmailGuardOptions { Mode = nameof(OutboundEmailMode.AllowListOnly) });

        await Assert.ThrowsAsync<OutboundEmailBlockedException>(() => guard.SendAsync(ToSupplier()));
        Assert.Empty(inner.Sent);
    }

    [Fact]
    public void AllowListOnly_DoesNotTreatASuffixMatchAsADomainMatch()
    {
        // "notnexora.sa" must not satisfy an allow-list of "nexora.sa".
        var guard = new OutboundEmailGuardOptions { AllowedDomains = { "nexora.sa" } };

        Assert.True(guard.IsAllowed("a@nexora.sa"));
        Assert.False(guard.IsAllowed("a@notnexora.sa"));
        Assert.False(guard.IsAllowed("a@nexora.sa.evil.com"));
        Assert.False(guard.IsAllowed("nexora.sa"));       // not an address at all
        Assert.False(guard.IsAllowed("a@"));
        Assert.False(guard.IsAllowed(null));
    }

    // ---------------------------------------------------------------- DraftOnly

    [Fact]
    public async Task DraftOnly_TransmitsNothingAtAll()
    {
        var (guard, inner) = Build(new OutboundEmailGuardOptions { Mode = nameof(OutboundEmailMode.DraftOnly) });

        var receipt = await guard.SendAsync(ToSupplier());

        Assert.Null(receipt);
        Assert.Empty(inner.Sent);
    }

    // ---------------------------------------------------------------- configuration safety

    [Fact]
    public void AnUnrecognisedModeFallsBackToLive_AndSaysSo()
    {
        // Silently containing production mail would be its own outage, so the fallback is Live —
        // but it must be a stated warning, never a quiet assumption.
        var options = new OutboundEmailGuardOptions { Mode = "Sandbox" };

        Assert.Equal(OutboundEmailMode.Live, options.ResolvedMode);
        Assert.Contains(options.Validate(), w => w.Contains("not recognised"));
    }

    [Fact]
    public void ARealTransportWithNoContainmentIsWarnedAboutByName()
    {
        var options = new NotificationsOptions { Provider = "smtp", Smtp = { Host = "smtp.example.com" } };

        Assert.Contains(options.Validate(), w => w.Contains("REAL recipients"));
    }

    [Fact]
    public void ContainmentModesDoNotWarnAboutRealRecipients()
    {
        var options = new NotificationsOptions
        {
            Provider = "smtp",
            Smtp = { Host = "smtp.example.com" },
            OutboundGuard = { Mode = nameof(OutboundEmailMode.Redirect), RedirectTo = "sink@localhost" }
        };

        Assert.DoesNotContain(options.Validate(), w => w.Contains("REAL recipients"));
    }
}
