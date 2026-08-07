namespace ERP_RFQ_Automation.Email;

/// <summary>One endpoint, in the shape a form can bind to directly.</summary>
/// <param name="UseSsl">What to store in the mailbox row's <c>UseSSL</c> column for this endpoint.
/// Carried alongside <paramref name="Tls"/> rather than left to the client to derive, because the
/// derivation is asymmetric between IMAP and SMTP and getting it wrong is the exact defect this
/// module exists to remove.</param>
public sealed record EmailEndpointDto(
    MailDirection Direction,
    MailTransport Transport,
    string Host,
    int Port,
    MailTlsMode Tls,
    bool UseSsl);

/// <summary>
/// Everything a setup screen needs for one provider: what to fill in, what to warn about, and what
/// the operator has to go and switch on somewhere else before any of it will work.
///
/// <para>The guidance fields are the difference between a form and a product. An operator who is
/// told up front that Microsoft 365 disables SMTP submission per mailbox does not spend an
/// afternoon re-typing a password that was correct the first time.</para>
/// </summary>
public sealed record EmailProviderCapabilityDto
{
    public string Key { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;

    public bool SupportsInbound { get; init; }
    public bool SupportsOutbound { get; init; }

    /// <summary>True when this provider can be stored as a tenant mailbox row — it both reads and
    /// sends over IMAP/SMTP. False for send-only API providers, which the tenant screen cannot
    /// represent.</summary>
    public bool SupportsTenantMailbox { get; init; }

    public EmailEndpointDto? Inbound { get; init; }
    public EmailEndpointDto? OutboundSmtp { get; init; }
    public EmailEndpointDto? OutboundApi { get; init; }

    public IReadOnlyList<MailAuthMode> AuthModes { get; init; } = [];

    /// <summary>The fields the operator must supply for this provider, by name, so a screen can
    /// show an API-key box for SendGrid and a username/password pair for GoDaddy without
    /// hardcoding either.</summary>
    public IReadOnlyList<string> RequiredFields { get; init; } = [];

    /// <summary>True when the account's own password will be refused and a provider-issued app
    /// password is the only credential that works.</summary>
    public bool RequiresAppPassword { get; init; }

    /// <summary>True when SMTP submission is off by default and a provider-side change is needed
    /// before correct credentials can succeed.</summary>
    public bool SmtpAuthDisabledByDefault { get; init; }

    /// <summary>True when the provider will refuse a message whose From is not the mailbox that
    /// authenticated — a mailbox host rather than a relay. Sent rather than re-derived on the
    /// client, so the rule has exactly one definition.</summary>
    public bool RequiresSenderMatchesMailbox { get; init; }

    /// <summary>A daily or monthly ceiling worth stating before the tenant discovers it as a
    /// partial outage. Null when the provider publishes none that matters at this scale.</summary>
    public string? SendingLimit { get; init; }

    /// <summary>What has to be enabled at the provider before the mailbox can be read.</summary>
    public string? InboundEnablementNote { get; init; }

    public string Guidance { get; init; } = string.Empty;
    public string? DocumentationUrl { get; init; }
}

public static class EmailProviderCapabilityMapper
{
    public static EmailProviderCapabilityDto ToDto(this EmailProviderDefinition provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        return new EmailProviderCapabilityDto
        {
            Key = provider.Key,
            DisplayName = provider.DisplayName,
            SupportsInbound = provider.SupportsInbound,
            SupportsOutbound = provider.SupportsOutbound,
            SupportsTenantMailbox = provider.SupportsTenantMailbox,
            Inbound = Endpoint(provider.Inbound),
            OutboundSmtp = Endpoint(provider.OutboundSmtp),
            OutboundApi = Endpoint(provider.OutboundApi),
            AuthModes = provider.SupportedAuthModes,
            RequiredFields = RequiredFields(provider),
            RequiresAppPassword = provider.RequiresAppPassword,
            SmtpAuthDisabledByDefault = provider.SmtpAuthDisabledByDefault,
            RequiresSenderMatchesMailbox = provider.RequiresSenderMatchesMailbox,
            SendingLimit = provider.SendingLimit,
            InboundEnablementNote = provider.InboundEnablementNote,
            Guidance = provider.Guidance,
            DocumentationUrl = provider.DocumentationUrl
        };
    }

    private static EmailEndpointDto? Endpoint(EmailConnectionPreset? preset) =>
        preset is null
            ? null
            : new EmailEndpointDto(
                preset.Direction, preset.Transport, preset.Host, preset.Port, preset.Tls, preset.UseSsl);

    /// <summary>
    /// Derived from the auth modes rather than listed per provider, so a provider entry cannot
    /// declare API-key authentication and then ask a screen for a password.
    /// </summary>
    private static IReadOnlyList<string> RequiredFields(EmailProviderDefinition provider)
    {
        var fields = new List<string> { "host", "port" };

        // An API key still travels in the password field over SMTP, and SendGrid's username is the
        // literal "apikey" — so an API-key provider needs a username box too the moment it is used
        // as a mailbox. Listing both is honest; listing only "apiKey" would hide a required field.
        if (provider.SupportedAuthModes.Any(x => x is MailAuthMode.Password or MailAuthMode.AppPassword))
        {
            fields.Add("username");
            fields.Add("password");
        }

        if (provider.SupportedAuthModes.Contains(MailAuthMode.ApiKey))
            fields.Add("apiKey");

        return fields;
    }
}
