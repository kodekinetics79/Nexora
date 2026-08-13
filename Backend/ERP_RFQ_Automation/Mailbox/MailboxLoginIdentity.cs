using System;
using ERP_RFQ_Automation.Models;

namespace ERP_RFQ_Automation.Mailbox;

/// <summary>
/// THE login identity a mailbox authenticates with — resolved in exactly one place, so the
/// "Test Connection" button and the thing being tested cannot disagree.
///
/// <para><b>The defect this closes.</b> The mailbox connection probe authenticated as
/// <see cref="EmailConfiguration.Username"/> while <c>EmailService</c> — the poller that
/// actually reads the mailbox — authenticated as <see cref="EmailConfiguration.EmailAddress"/>.
/// They are independent columns. Wherever a tenant's UPN differs from the address on the
/// mailbox, which is the normal case for shared and enterprise mailboxes, a GREEN connection
/// test proved nothing whatsoever about the poller. That is the precise shape of the failure
/// <c>EmailBackgroundService</c> documents having already happened in production: the door was
/// shut for seven days and every surface a human could look at said the mailbox was fine.</para>
///
/// <para><b>Why the answer is direction-aware rather than one column.</b> The asymmetry is
/// real, not an accident: the inbound poller has always signed in as the address, while
/// <c>OutboundSmtpTransport</c> and <c>SmtpEmailSender</c> sign in as the username. Collapsing
/// both onto one column would change the credential a working mailbox uses and break live
/// mail to fix a test. So the rule below records what each side ACTUALLY does, and the probe
/// is corrected to follow it. No working path changes; the test stops lying.</para>
/// </summary>
public static class MailboxLoginIdentity
{
    /// <summary>
    /// The identity the INBOUND poller signs in with. Must stay byte-identical to the argument
    /// <c>EmailService</c> passes to <c>AuthenticateAsync</c> — that is the whole contract, and
    /// <c>MailboxLoginIdentityTests</c> pins it.
    /// </summary>
    public static string ForInbound(EmailConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        return configuration.EmailAddress;
    }

    /// <summary>The identity the OUTBOUND transport signs in with.</summary>
    public static string ForOutbound(EmailConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        return configuration.Username;
    }

    /// <summary>
    /// Direction-aware resolution for callers holding a protocol string rather than an
    /// intention. SMTP is outbound; IMAP and POP3 are inbound.
    /// </summary>
    public static string For(EmailConfiguration configuration, string? protocol)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        return string.Equals(protocol?.Trim(), MailboxConnectionProbe.Smtp, StringComparison.OrdinalIgnoreCase)
            ? ForOutbound(configuration)
            : ForInbound(configuration);
    }
}
