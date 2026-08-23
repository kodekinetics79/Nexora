using System.Net;
using ERP_RFQ_Automation.Security;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// The loopback allowance, and the reason it is safe to have one at all.
///
/// <para><see cref="MailEndpointPolicy"/> used to refuse loopback in every environment, and its
/// stated reason was sound: an environment-conditional bypass in an SSRF control is exactly the
/// kind of flag that reaches production set the wrong way. The allowance answers that
/// STRUCTURALLY — the environment is a parameter to the enabling call, so no configuration can
/// grant it on a non-Development host. These tests are what make that claim checkable.</para>
///
/// <para>The allowance is PROCESS-GLOBAL static state, and <c>AcceptanceJourneyTests</c> turns
/// it on while it drives the mailbox journey through a loopback IMAP sink. This class asserts
/// the default-off state, so the two must never run in parallel: both live in the serialized
/// PostgreSQL integration collection for exactly that reason (this class does not use the
/// database fixture — only the collection's no-parallelism guarantee).</para>
/// </summary>
[Collection(Support.PostgreSqlIntegrationCollection.Name)]
public sealed class MailEndpointPolicyLoopbackAllowanceTests : IDisposable
{
    public void Dispose() => MailEndpointPolicy.ResetLoopbackAllowance();

    [Fact]
    public void By_default_loopback_is_refused()
    {
        MailEndpointPolicy.ResetLoopbackAllowance();

        Assert.False(MailEndpointPolicy.IsLoopbackAllowed);
        Assert.False(MailEndpointPolicy.IsAllowedEndpoint("127.0.0.1", 3143));
        Assert.False(MailEndpointPolicy.IsAllowedEndpoint("localhost", 3143));
    }

    [Fact]
    public void A_production_host_cannot_grant_it_even_when_the_key_is_true()
    {
        // THE test. A deployment carrying the flag set true must be a no-op, not a hole — which
        // is the entire argument for the allowance being acceptable.
        var granted = MailEndpointPolicy.EnableLoopbackForLocalDevelopment(
            isDevelopmentEnvironment: false, requested: true);

        Assert.False(granted);
        Assert.False(MailEndpointPolicy.IsLoopbackAllowed);
        Assert.False(MailEndpointPolicy.IsAllowedEndpoint("127.0.0.1", 3143));
        Assert.Throws<InvalidOperationException>(
            () => MailEndpointPolicy.ValidateResolvedAddresses(new[] { IPAddress.Loopback }));
    }

    [Fact]
    public void A_development_host_that_does_not_ask_does_not_get_it()
    {
        Assert.False(MailEndpointPolicy.EnableLoopbackForLocalDevelopment(
            isDevelopmentEnvironment: true, requested: false));
        Assert.False(MailEndpointPolicy.IsAllowedEndpoint("127.0.0.1", 3143));
    }

    [Fact]
    public void A_development_host_that_asks_may_dial_loopback_and_nothing_else()
    {
        Assert.True(MailEndpointPolicy.EnableLoopbackForLocalDevelopment(
            isDevelopmentEnvironment: true, requested: true));

        Assert.True(MailEndpointPolicy.IsAllowedEndpoint("127.0.0.1", 3143));
        Assert.True(MailEndpointPolicy.IsAllowedEndpoint("localhost", 3143));
        MailEndpointPolicy.ValidateResolvedAddresses(new[] { IPAddress.Loopback });

        // SCOPE. The risk this control exists for is a mail server dialling internal
        // infrastructure. Loopback reaches only the machine already running the code; these do
        // not, and stay refused even under the allowance.
        foreach (var address in new[] { "10.0.0.5", "172.16.4.9", "192.168.1.20", "169.254.1.1", "100.64.0.1" })
        {
            Assert.False(MailEndpointPolicy.IsAllowedEndpoint(address, 3143),
                $"{address} must stay refused even with the loopback allowance active.");
            Assert.Throws<InvalidOperationException>(
                () => MailEndpointPolicy.ValidateResolvedAddresses(new[] { IPAddress.Parse(address) }));
        }
    }

    [Fact]
    public void A_mixed_resolution_is_still_refused_wholesale()
    {
        MailEndpointPolicy.EnableLoopbackForLocalDevelopment(true, true);

        // All-must-pass is unchanged by the allowance: a name resolving to loopback AND a private
        // address must not be dialled on whichever the OS happened to return first.
        Assert.Throws<InvalidOperationException>(() => MailEndpointPolicy.ValidateResolvedAddresses(
            new[] { IPAddress.Loopback, IPAddress.Parse("10.0.0.5") }));
    }
}
