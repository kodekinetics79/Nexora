using System.Text.Json.Serialization;
using ERP_RFQ_Automation.Security;

namespace ERP_RFQ_Automation.Email;

/// <summary>Which way mail is moving. Inbound is the customer's mailbox being READ so RFQs can be
/// ingested; outbound is mail this system SENDS. A provider may support one, the other, or both —
/// SendGrid and Postmark send only, and offering an "IMAP host" for them would be a form field with
/// no possible answer.
///
/// <para>Serialised BY NAME, as <c>MailboxProbeStage</c> already is. This API registers no global
/// string-enum converter, so without the attribute these cross the wire as 0/1 and every client has
/// to hardcode the ordinal.</para></summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum MailDirection
{
    Inbound,
    Outbound
}

/// <summary>How a provider is actually spoken to.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum MailTransport
{
    Imap,
    Pop3,
    Smtp,

    /// <summary>An HTTPS submission API (SendGrid v3, the SES and Postmark REST endpoints). Not a
    /// mail protocol: there is no AUTH stage to fail and no mailbox to open, which is why the
    /// connection test reports these stages differently rather than pretending.</summary>
    HttpApi
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum MailAuthMode
{
    /// <summary>The account's own password.</summary>
    Password,

    /// <summary>A password minted specifically for this application because the account's real
    /// password will be refused. Distinct from <see cref="Password"/> because the remedy is
    /// different: nobody fixes an app-password rejection by re-typing their login.</summary>
    AppPassword,

    /// <summary>A provider-issued key presented instead of a username/password pair.</summary>
    ApiKey,

    /// <summary>Delegated authorisation. Declared where a provider supports it so the catalogue
    /// tells the truth about the provider — Nexora implements none of these flows yet, and
    /// <see cref="EmailProviderDefinition.SupportedAuthModes"/> is what a screen must filter on.</summary>
    OAuth2
}

/// <summary>
/// How the socket is secured, stated as the mode the provider documents rather than as a boolean.
///
/// <para><b>Why not the <c>UseSsl</c> flag the mailbox row stores.</b> That flag means three
/// different things in this codebase (see <c>MailboxConnectionProbe.SecurityFor</c>), and one of
/// them is a trap: for SMTP, <c>UseSsl = false</c> means STARTTLS, but for IMAP the identical value
/// means NO ENCRYPTION AT ALL. A single boolean shared between the two directions cannot express
/// "993 implicit TLS inbound, 587 STARTTLS outbound", which is exactly Microsoft 365's published
/// configuration — and the preset table shipped it as one flag, so choosing Microsoft 365 and then
/// switching to SMTP produced port 587 with implicit TLS, a combination that cannot connect.</para>
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum MailTlsMode
{
    /// <summary>Cleartext. Never used by a catalogue preset; expressible only because an operator
    /// with an internal relay may genuinely mean it, and the connection test must be able to say
    /// out loud that the credential crossed the network in the clear.</summary>
    None,

    /// <summary>Connect in the clear, then upgrade with STARTTLS before authenticating. Ports 587,
    /// 25, 2525 and 143.</summary>
    StartTls,

    /// <summary>TLS from the first byte, before any protocol is spoken. Ports 993, 465 and 443.</summary>
    Implicit
}

/// <summary>
/// One provider-documented endpoint: what to connect to, on which port, secured how.
/// </summary>
public sealed record EmailConnectionPreset(
    MailDirection Direction,
    MailTransport Transport,
    string Host,
    int Port,
    MailTlsMode Tls)
{
    /// <summary>
    /// The value the stored <c>UseSsl</c> flag must hold for the runtime to negotiate
    /// <see cref="Tls"/> on this transport.
    ///
    /// <para>This is the translation the preset table exists to get right, and it is asymmetric.
    /// For SMTP the runtime reads <c>false</c> as STARTTLS, so a 587 preset needs the flag OFF.
    /// For IMAP the runtime reads <c>false</c> as NO ENCRYPTION, so a StartTls inbound preset would
    /// silently send the mailbox password in the clear — which is why every inbound preset in this
    /// catalogue is implicit TLS on 993 and why <c>EmailProviderCatalogTests</c> refuses any that
    /// is not.</para>
    /// </summary>
    public bool UseSsl => Tls == MailTlsMode.Implicit;

    /// <summary>True when this endpoint is a mail protocol a socket probe can stage through
    /// (DNS → TCP → TLS → AUTH → mailbox), as opposed to an HTTPS submission API.</summary>
    public bool IsMailProtocol => Transport is MailTransport.Imap or MailTransport.Pop3 or MailTransport.Smtp;
}

/// <summary>
/// Everything a setup screen needs to know about one provider.
/// </summary>
/// <param name="Key">Stable identifier. Persisted against a mailbox row and echoed by the API, so
/// it must never be renamed — the display name is the part that may change.</param>
/// <param name="RequiresAppPassword">The account password will be refused outright when MFA is on.
/// Surfaced BEFORE the operator types, because the failure it prevents ("the password is definitely
/// correct and it still says wrong password") is the single most common mailbox support ticket.</param>
/// <param name="SmtpAuthDisabledByDefault">The provider ships with SMTP submission turned off per
/// mailbox, so a correct host, port, username and password still fail until a tenant administrator
/// changes something in the provider's own console.</param>
/// <param name="SendingLimit">A volume ceiling worth stating up front. A quoting system that sends
/// hundreds of supplier emails a day against a mailbox capped at 250 will simply stop delivering
/// part-way through an afternoon, and the provider's refusal reads as an unrelated outage.</param>
/// <param name="InboundEnablementNote">What must be switched on at the provider before the mailbox
/// can be read at all.</param>
public sealed record EmailProviderDefinition(
    string Key,
    string DisplayName,
    IReadOnlyList<EmailConnectionPreset> Presets,
    IReadOnlyList<MailAuthMode> SupportedAuthModes,
    string Guidance,
    string? DocumentationUrl = null,
    bool RequiresAppPassword = false,
    bool SmtpAuthDisabledByDefault = false,
    string? SendingLimit = null,
    string? InboundEnablementNote = null)
{
    public EmailConnectionPreset? Inbound =>
        Presets.FirstOrDefault(x => x.Direction == MailDirection.Inbound);

    /// <summary>The SMTP endpoint. Preferred over the HTTP API for the tenant screen, because a
    /// tenant sends from its own domain through its own mailbox.</summary>
    public EmailConnectionPreset? OutboundSmtp =>
        Presets.FirstOrDefault(x => x is { Direction: MailDirection.Outbound, Transport: MailTransport.Smtp });

    public EmailConnectionPreset? OutboundApi =>
        Presets.FirstOrDefault(x => x is { Direction: MailDirection.Outbound, Transport: MailTransport.HttpApi });

    public bool SupportsInbound => Inbound is not null;

    public bool SupportsOutbound => Presets.Any(x => x.Direction == MailDirection.Outbound);

    /// <summary>True when the same provider can both feed ingestion and carry quotes out, which is
    /// the only shape the tenant mailbox screen can store — it writes host/port rows, not API keys.</summary>
    public bool SupportsTenantMailbox => SupportsInbound && OutboundSmtp is not null;
}

/// <summary>
/// The mail providers Nexora knows how to connect to, with the host, port and TLS mode each one
/// publishes.
///
/// <para><b>The failure this ends.</b> Every mailbox — the platform's own sending identity and
/// every customer's RFQ inbox — was configured by an operator reading a provider's help pages and
/// typing four values into a form. Two of those values (port, and the encryption toggle) have to
/// agree with each other and with the direction, and getting them wrong does not produce an error
/// that names the cause: it produces a hang, or "could not connect", or a mailbox that saves
/// cleanly and then ingests nothing. Picking "GoDaddy Workspace" must fill in
/// <c>imap.secureserver.net:993</c> implicit TLS and <c>smtpout.secureserver.net:465</c> implicit
/// TLS, because that is the difference between a form and a product.</para>
///
/// <para><b>Why a code catalogue rather than a seeded table</b>, following
/// <see cref="ERP_RFQ_Automation.Authorization.ModuleCatalog"/>: a seed fixes the rows that exist on
/// the day it runs, and a provider's published settings change (Microsoft retired basic auth;
/// GoDaddy moved off <c>smtpout.secureserver.net:25</c>). This list is asserted by
/// <c>EmailProviderCatalogTests</c> against the rules that make a preset usable at all — every host
/// must be one <see cref="MailEndpointPolicy"/> would actually dial, every inbound preset must be
/// encrypted, and every port/TLS pair must be one the runtime's own <c>UseSsl</c> interpretation
/// can express. A preset that cannot work fails the build instead of shipping as a recommended
/// default that silently does not connect.</para>
///
/// <para><b>What adding a provider costs.</b> A provider that speaks IMAP and/or SMTP costs exactly
/// one entry here and nothing else: <c>SmtpEmailSender</c> and the ingestion poller are already
/// host-agnostic. A provider that only has an HTTPS submission API additionally costs one class
/// implementing <c>IEmailSender</c> and one arm in
/// <c>OutboundEmailTransportResolver.BuildTransport</c>. That is stated here rather than implied,
/// so nobody plans on the strength of a promise this module cannot keep.</para>
/// </summary>
public static class EmailProviderCatalog
{
    /// <summary>The escape hatch. Always present, always last, and deliberately carries no preset:
    /// its whole purpose is that the operator supplies the endpoint.</summary>
    public const string CustomKey = "custom";

    public static readonly IReadOnlyList<EmailProviderDefinition> All =
    [
        // The provider the CEO was hunting settings for. GoDaddy publishes 465/implicit for
        // sending; the older 25 and 80 relays it also documents are cleartext submission, which
        // this catalogue will not offer.
        new EmailProviderDefinition(
            Key: "godaddy",
            DisplayName: "GoDaddy Workspace / Professional Email",
            Presets:
            [
                new(MailDirection.Inbound, MailTransport.Imap, "imap.secureserver.net", 993, MailTlsMode.Implicit),
                new(MailDirection.Outbound, MailTransport.Smtp, "smtpout.secureserver.net", 465, MailTlsMode.Implicit)
            ],
            SupportedAuthModes: [MailAuthMode.Password],
            Guidance:
            "Sign in with the full email address as the username and the mailbox password. GoDaddy " +
            "also documents port 25 and port 80 relays — do not use them, they submit your password " +
            "in the clear.",
            DocumentationUrl: "https://www.godaddy.com/help/server-and-port-settings-for-workspace-email-6949",
            SendingLimit:
            "GoDaddy caps outbound volume per day (250 recipients on Workspace Email, 500 on " +
            "Professional Email). Sending past the cap is refused for the rest of the day, which " +
            "arrives as quotes and supplier emails failing from mid-afternoon onwards."),

        new EmailProviderDefinition(
            Key: "microsoft365",
            DisplayName: "Microsoft 365 / Outlook",
            Presets:
            [
                new(MailDirection.Inbound, MailTransport.Imap, "outlook.office365.com", 993, MailTlsMode.Implicit),
                // 587/STARTTLS is the ONLY submission endpoint Microsoft offers; there is no
                // implicit-TLS port. This is the pair the single UseSsl flag could not express.
                new(MailDirection.Outbound, MailTransport.Smtp, "smtp.office365.com", 587, MailTlsMode.StartTls)
            ],
            SupportedAuthModes: [MailAuthMode.AppPassword, MailAuthMode.OAuth2],
            Guidance:
            "Sending uses port 587 with STARTTLS, so 'use a secure connection' must be OFF for the " +
            "SMTP row — the connection still upgrades to TLS before the password is sent. Reading " +
            "uses port 993 with it ON.",
            DocumentationUrl: "https://learn.microsoft.com/exchange/clients-and-mobile-in-exchange-online/authenticated-client-smtp-submission",
            RequiresAppPassword: true,
            SmtpAuthDisabledByDefault: true,
            InboundEnablementNote:
            "IMAP is disabled per mailbox by default. A tenant administrator must enable it in the " +
            "Exchange admin centre before this mailbox can be read."),

        new EmailProviderDefinition(
            Key: "google",
            DisplayName: "Google Workspace / Gmail",
            Presets:
            [
                new(MailDirection.Inbound, MailTransport.Imap, "imap.gmail.com", 993, MailTlsMode.Implicit),
                new(MailDirection.Outbound, MailTransport.Smtp, "smtp.gmail.com", 465, MailTlsMode.Implicit)
            ],
            SupportedAuthModes: [MailAuthMode.AppPassword, MailAuthMode.OAuth2],
            Guidance:
            "Google refuses the account password outright once 2-Step Verification is on. Generate " +
            "an App Password and use it here; the username stays the full email address.",
            DocumentationUrl: "https://support.google.com/mail/answer/7126229",
            RequiresAppPassword: true,
            SendingLimit:
            "Google Workspace caps relayed mail at 2,000 messages a day per account; a consumer " +
            "Gmail account is capped at 500.",
            InboundEnablementNote:
            "IMAP must also be switched on in the mailbox's own Gmail settings."),

        new EmailProviderDefinition(
            Key: "zoho",
            DisplayName: "Zoho Mail",
            Presets:
            [
                new(MailDirection.Inbound, MailTransport.Imap, "imap.zoho.com", 993, MailTlsMode.Implicit),
                new(MailDirection.Outbound, MailTransport.Smtp, "smtp.zoho.com", 465, MailTlsMode.Implicit)
            ],
            SupportedAuthModes: [MailAuthMode.AppPassword],
            Guidance:
            "Use an application-specific password generated under Zoho account security. Accounts " +
            "on the EU or India data centres use imap.zoho.eu / smtp.zoho.eu (or .in) instead.",
            DocumentationUrl: "https://www.zoho.com/mail/help/imap-access.html"),

        new EmailProviderDefinition(
            Key: "cpanel",
            DisplayName: "cPanel / company mail server",
            Presets:
            [
                new(MailDirection.Inbound, MailTransport.Imap, "mail.yourcompany.com", 993, MailTlsMode.Implicit),
                new(MailDirection.Outbound, MailTransport.Smtp, "mail.yourcompany.com", 465, MailTlsMode.Implicit)
            ],
            SupportedAuthModes: [MailAuthMode.Password],
            Guidance:
            "Replace the hostname with your own mail server's name; the ports are the cPanel " +
            "defaults. If your host does not offer TLS on 993/465, use 143/587 — but note that an " +
            "IMAP row on 143 connects WITHOUT encryption, so the mailbox password crosses the " +
            "network in the clear."),

        // ---- send-only providers. No inbound preset, because they have no mailbox to read. -----

        new EmailProviderDefinition(
            Key: "sendgrid",
            DisplayName: "SendGrid",
            Presets:
            [
                new(MailDirection.Outbound, MailTransport.HttpApi, "api.sendgrid.com", 443, MailTlsMode.Implicit),
                new(MailDirection.Outbound, MailTransport.Smtp, "smtp.sendgrid.net", 587, MailTlsMode.StartTls)
            ],
            SupportedAuthModes: [MailAuthMode.ApiKey],
            Guidance:
            "Over SMTP the username is the literal word 'apikey' and the password is the API key " +
            "itself. Sending from a domain requires that domain to be authenticated in SendGrid " +
            "first, or the mail is accepted and then filed as spam by the recipient.",
            DocumentationUrl: "https://www.twilio.com/docs/sendgrid/for-developers/sending-email/integrating-with-the-smtp-api",
            SendingLimit: "The free tier is capped at 100 messages a day."),

        new EmailProviderDefinition(
            Key: "amazonses",
            DisplayName: "Amazon SES",
            Presets:
            [
                // Region-specific by construction; us-east-1 stands in so the shape is visible and
                // the guidance says plainly that the region must be corrected.
                new(MailDirection.Outbound, MailTransport.Smtp, "email-smtp.us-east-1.amazonaws.com", 587, MailTlsMode.StartTls)
            ],
            SupportedAuthModes: [MailAuthMode.Password],
            Guidance:
            "The host is region-specific — replace us-east-1 with the region the SES identity lives " +
            "in, or every send is refused. SES SMTP credentials are NOT an IAM access key: they are " +
            "generated separately in the SES console, and pasting an IAM key here fails " +
            "authentication with no hint as to why.",
            DocumentationUrl: "https://docs.aws.amazon.com/ses/latest/dg/smtp-connect.html",
            SendingLimit:
            "A new SES account is in the sandbox: it may only send to addresses you have verified, " +
            "and production access has to be requested. Until then every customer-facing send is " +
            "rejected."),

        new EmailProviderDefinition(
            Key: "postmark",
            DisplayName: "Postmark",
            Presets:
            [
                new(MailDirection.Outbound, MailTransport.HttpApi, "api.postmarkapp.com", 443, MailTlsMode.Implicit),
                new(MailDirection.Outbound, MailTransport.Smtp, "smtp.postmarkapp.com", 587, MailTlsMode.StartTls)
            ],
            SupportedAuthModes: [MailAuthMode.ApiKey],
            Guidance:
            "The server API token is used as BOTH the username and the password over SMTP. Postmark " +
            "will not deliver from an unverified sender signature or domain.",
            DocumentationUrl: "https://postmarkapp.com/developer/user-guide/send-email-with-smtp"),

        new EmailProviderDefinition(
            Key: "mailgun",
            DisplayName: "Mailgun",
            Presets:
            [
                new(MailDirection.Outbound, MailTransport.HttpApi, "api.mailgun.net", 443, MailTlsMode.Implicit),
                new(MailDirection.Outbound, MailTransport.Smtp, "smtp.mailgun.org", 587, MailTlsMode.StartTls)
            ],
            SupportedAuthModes: [MailAuthMode.ApiKey, MailAuthMode.Password],
            Guidance:
            "Use the SMTP credentials shown on the sending domain's page, not your Mailgun login. " +
            "Domains created in the EU region use smtp.eu.mailgun.org and api.eu.mailgun.net.",
            DocumentationUrl: "https://documentation.mailgun.com/docs/mailgun/user-manual/sending-messages/"),

        new EmailProviderDefinition(
            Key: CustomKey,
            DisplayName: "Something else",
            Presets: [],
            SupportedAuthModes: [MailAuthMode.Password, MailAuthMode.ApiKey],
            Guidance:
            "Enter the hostname and port your provider documents. Reading mail is normally port 993 " +
            "with a secure connection; sending is 465 with one, or 587 without one (587 still " +
            "encrypts, via STARTTLS, before the password is sent).")
    ];

    public static readonly IReadOnlyDictionary<string, EmailProviderDefinition> ByKey =
        All.ToDictionary(x => x.Key, StringComparer.OrdinalIgnoreCase);

    public static EmailProviderDefinition? Find(string? key) =>
        !string.IsNullOrWhiteSpace(key) && ByKey.TryGetValue(key.Trim(), out var provider) ? provider : null;

    /// <summary>Providers a tenant can store as a mailbox row: they read AND send over IMAP/SMTP.
    /// The send-only API providers are excluded because <c>Email_Configurations</c> holds a host,
    /// port and password — there is nowhere to put an API key, and offering them would produce a
    /// mailbox that saves and can never connect.</summary>
    public static IReadOnlyList<EmailProviderDefinition> ForTenantMailbox =>
        All.Where(x => x.SupportsTenantMailbox || x.Key == CustomKey).ToList();

    /// <summary>Providers the platform can send its own mail through.</summary>
    public static IReadOnlyList<EmailProviderDefinition> ForPlatformOutbound =>
        All.Where(x => x.SupportsOutbound || x.Key == CustomKey).ToList();

    /// <summary>
    /// Which provider a hostname belongs to, for rows configured before the catalogue existed and
    /// for hosts typed by hand.
    ///
    /// <para>Every mailbox in the product today was created with no provider recorded, so a
    /// connection test on an existing row would otherwise have no provider-specific advice to give
    /// — and "Microsoft 365 disables SMTP AUTH by default" is precisely the sentence that resolves
    /// the ticket. Matching on the domain rather than the exact host is deliberate: tenants use
    /// <c>outlook.office365.com</c>, <c>smtp.office365.com</c> and their own vanity CNAMEs onto the
    /// same service.</para>
    /// </summary>
    public static string? InferKeyFromHost(string? host)
    {
        if (string.IsNullOrWhiteSpace(host)) return null;
        var normalized = MailEndpointPolicy.Normalize(host).ToLowerInvariant();

        // An exact preset host is the strongest signal and is checked first, so a provider whose
        // domain is a substring of another's cannot capture it.
        foreach (var provider in All)
            if (provider.Presets.Any(p => string.Equals(p.Host, normalized, StringComparison.OrdinalIgnoreCase)))
                return provider.Key;

        return normalized switch
        {
            _ when Ends(normalized, "secureserver.net") => "godaddy",
            _ when Ends(normalized, "office365.com") || Ends(normalized, "outlook.com")
                || Ends(normalized, "office.com") => "microsoft365",
            _ when Ends(normalized, "gmail.com") || Ends(normalized, "googlemail.com")
                || Ends(normalized, "google.com") => "google",
            _ when normalized.Contains("zoho.") => "zoho",
            _ when Ends(normalized, "sendgrid.net") || Ends(normalized, "sendgrid.com") => "sendgrid",
            _ when Ends(normalized, "amazonaws.com") => "amazonses",
            _ when Ends(normalized, "postmarkapp.com") => "postmark",
            _ when Ends(normalized, "mailgun.org") || Ends(normalized, "mailgun.net") => "mailgun",
            _ => null
        };

        // Suffix, not Contains: "gmail.com.attacker.example" is not Google, and matching it as
        // Google would attach Google's remedies to a failure that has nothing to do with Google.
        static bool Ends(string value, string domain) =>
            value.Equals(domain, StringComparison.Ordinal) ||
            value.EndsWith("." + domain, StringComparison.Ordinal);
    }

    /// <summary>The preset for one provider and direction, or null when the provider does not serve
    /// that direction. SMTP is preferred over the HTTP API for outbound, because it is the one a
    /// host/port form can express.</summary>
    public static EmailConnectionPreset? PresetFor(string? providerKey, MailDirection direction)
    {
        var provider = Find(providerKey);
        if (provider is null) return null;
        return direction == MailDirection.Inbound ? provider.Inbound : provider.OutboundSmtp ?? provider.OutboundApi;
    }
}
