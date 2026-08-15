using System.Text.RegularExpressions;
using ERP_RFQ_Automation.Mailbox;
using ERP_RFQ_Automation.Models;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// The connection test must sign in as the thing it claims to be testing.
///
/// <para>It did not. The probe authenticated as <c>Username</c>; the IMAP poller authenticated
/// as <c>EmailAddress</c>. They are independent columns, so wherever a tenant's UPN differs
/// from the mailbox address — the normal case for shared and enterprise mailboxes — a green
/// "Test Connection" said nothing at all about whether mail could be read. That is the exact
/// shape of the outage <c>EmailBackgroundService</c> records: the door shut for seven days
/// while every human-facing surface reported healthy.</para>
/// </summary>
public class MailboxLoginIdentityTests
{
    private static EmailConfiguration Mailbox(string address, string username) => new()
    {
        EmailAddress = address,
        Username = username,
        Protocol = "IMAP",
        Host = "imap.example.com",
        Port = 993,
        Password = "unused-by-these-assertions"
    };

    [Fact]
    public void Inbound_signs_in_as_the_mailbox_address()
    {
        // The distinguishing case: address and username differ, which is what made the old
        // mismatch invisible whenever they happened to be equal.
        var config = Mailbox("rfq@customer.example", "CUSTOMER\\svc-rfq");

        Assert.Equal("rfq@customer.example", MailboxLoginIdentity.ForInbound(config));
    }

    [Fact]
    public void Outbound_signs_in_as_the_username()
    {
        var config = Mailbox("rfq@customer.example", "CUSTOMER\\svc-rfq");

        Assert.Equal("CUSTOMER\\svc-rfq", MailboxLoginIdentity.ForOutbound(config));
    }

    [Theory]
    [InlineData("IMAP", "rfq@customer.example")]
    [InlineData("imap", "rfq@customer.example")]
    [InlineData("POP3", "rfq@customer.example")]
    [InlineData("SMTP", "CUSTOMER\\svc-rfq")]
    [InlineData("smtp", "CUSTOMER\\svc-rfq")]
    public void Protocol_selects_the_direction(string protocol, string expected)
        => Assert.Equal(expected,
            MailboxLoginIdentity.For(Mailbox("rfq@customer.example", "CUSTOMER\\svc-rfq"), protocol));

    [Fact]
    public void An_unknown_protocol_is_treated_as_inbound()
        // Inbound is the safe default: the poller is the path whose silent failure costs
        // deals, so an unrecognised protocol tests the credential that matters.
        => Assert.Equal("rfq@customer.example",
            MailboxLoginIdentity.For(Mailbox("rfq@customer.example", "CUSTOMER\\svc-rfq"), "GRAPH"));

    /// <summary>
    /// The contract, pinned against the source rather than a comment: every IMAP
    /// <c>AuthenticateAsync</c> in <c>EmailService</c> must pass the resolver's answer.
    ///
    /// <para>A source assertion is used because there is no seam to observe here — nothing in
    /// the suite polls a real mailbox, which is precisely why the original mismatch survived
    /// review and production. If this test is failing, someone has reintroduced a second
    /// opinion about which column is the login.</para>
    /// </summary>
    [Fact]
    public void Every_inbound_authenticate_in_EmailService_uses_the_resolver()
    {
        var source = File.ReadAllText(Path.Combine(
            RepositoryRoot(), "Backend", "ERP_RFQ_Automation", "Services", "EmailService.cs"));

        // Each ImapClient authentication in the poll path.
        var authentications = Regex.Matches(source, @"client\.AuthenticateAsync\(\s*([^,]+),")
            .Select(m => m.Groups[1].Value.Trim())
            .ToList();

        Assert.NotEmpty(authentications);
        foreach (var identity in authentications)
        {
            // The one deliberate exception is the SMTP quote-delivery send, which has always
            // authenticated as the address and is annotated as such at the call site. It is
            // matched by the literal, so it does not silently satisfy the rule.
            Assert.True(
                identity.Contains("MailboxLoginIdentity.ForInbound", StringComparison.Ordinal)
                || identity.Equals("config.EmailAddress", StringComparison.Ordinal),
                $"EmailService authenticates with '{identity}', which is neither the resolver "
                + "nor the annotated outbound exception. The connection test and the poller "
                + "have drifted apart again.");
        }
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "Backend")))
            directory = directory.Parent;
        Assert.NotNull(directory);
        return directory!.FullName;
    }
}
