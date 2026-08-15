using System.ComponentModel.DataAnnotations;

namespace ERP_RFQ_Automation.Mailbox;

/// <summary>
/// The outbound view of a mailbox. There is deliberately NO password property — not a masked one,
/// not a nulled one. <c>EmailConfiguration.Password</c> is decrypted transparently by the value
/// converter, so any DTO with a password field is one careless mapping away from returning a live
/// customer credential to the browser. The only way to change a password is to send a new one.
/// </summary>
public sealed record MailboxResponseDTO
{
    public long Id { get; init; }
    public string ConfigurationName { get; init; } = string.Empty;
    public string EmailAddress { get; init; } = string.Empty;
    public string Protocol { get; init; } = string.Empty;
    public string Host { get; init; } = string.Empty;
    public int Port { get; init; }
    public string Username { get; init; } = string.Empty;
    public bool UseSsl { get; init; }
    public int PollingInterval { get; init; }
    public bool IsActive { get; init; }
    public DateTime CreatedOn { get; init; }

    // ---- health, from the poller's own telemetry ----------------------------------------
    public DateTime? LastSuccessfulPollOn { get; init; }
    public DateTime? LastPollAttemptOn { get; init; }
    public string? LastPollError { get; init; }
    public int ConsecutivePollFailures { get; init; }

    /// <summary>Derived state for the screen: Healthy, Failing, Never polled, Idle, or Disabled.
    /// Computed server-side so every client agrees on what "failing" means.</summary>
    public string HealthState { get; init; } = string.Empty;

    /// <summary>Plain-language explanation of <see cref="HealthState"/>.</summary>
    public string HealthDetail { get; init; } = string.Empty;

    /// <summary>True when this row's credentials would cross the network unencrypted, given the
    /// protocol's interpretation of <see cref="UseSsl"/>. Surfaced per-row because the answer
    /// differs between IMAP and SMTP for identical settings.</summary>
    public bool CredentialsSentInClear { get; init; }
}

public sealed record MailboxCreateRequestDTO
{
    [Required, StringLength(120, MinimumLength = 2)]
    public string ConfigurationName { get; init; } = string.Empty;

    [Required, EmailAddress, StringLength(320)]
    public string EmailAddress { get; init; } = string.Empty;

    /// <summary>IMAP (read leads in) or SMTP (send quotes out).</summary>
    [Required]
    public string Protocol { get; init; } = string.Empty;

    [Required, StringLength(253, MinimumLength = 3)]
    public string Host { get; init; } = string.Empty;

    [Range(1, 65535)]
    public int Port { get; init; }

    [Required, StringLength(320)]
    public string Username { get; init; } = string.Empty;

    [Required, StringLength(1024, MinimumLength = 1)]
    public string Password { get; init; } = string.Empty;

    public bool UseSsl { get; init; } = true;

    /// <summary>Minutes between polls. Ignored for SMTP.</summary>
    [Range(1, 1440)]
    public int PollingInterval { get; init; } = 5;

    public bool IsActive { get; init; } = true;

    /// <summary>
    /// When true the mailbox is only saved if a live connection test passes first. Default true:
    /// a saved-but-broken mailbox looks configured on the screen while silently ingesting nothing,
    /// and the failure only surfaces as missing leads days later.
    /// </summary>
    public bool VerifyBeforeSave { get; init; } = true;

    /// <inheritdoc cref="MailboxSecretRedaction.Marker"/>
    public override string ToString() =>
        $"MailboxCreateRequestDTO {{ ConfigurationName = {ConfigurationName}, "
        + $"EmailAddress = {EmailAddress}, Protocol = {Protocol}, Host = {Host}, Port = {Port}, "
        + $"Username = {Username}, Password = {MailboxSecretRedaction.Marker(Password)}, "
        + $"UseSsl = {UseSsl}, PollingInterval = {PollingInterval}, IsActive = {IsActive}, "
        + $"VerifyBeforeSave = {VerifyBeforeSave} }}";
}

public sealed record MailboxUpdateRequestDTO
{
    [Required, StringLength(120, MinimumLength = 2)]
    public string ConfigurationName { get; init; } = string.Empty;

    [Required, EmailAddress, StringLength(320)]
    public string EmailAddress { get; init; } = string.Empty;

    [Required, StringLength(253, MinimumLength = 3)]
    public string Host { get; init; } = string.Empty;

    [Range(1, 65535)]
    public int Port { get; init; }

    [Required, StringLength(320)]
    public string Username { get; init; } = string.Empty;

    /// <summary>Null or empty KEEPS the stored password. The UI never receives the current one, so
    /// "leave blank to keep" is the only way to edit a host or port without retyping a credential
    /// the operator may not have.</summary>
    [StringLength(1024)]
    public string? Password { get; init; }

    public bool UseSsl { get; init; } = true;

    [Range(1, 1440)]
    public int PollingInterval { get; init; } = 5;

    public bool IsActive { get; init; } = true;

    public bool VerifyBeforeSave { get; init; } = true;

    /// <inheritdoc cref="MailboxSecretRedaction.Marker"/>
    public override string ToString() =>
        $"MailboxUpdateRequestDTO {{ ConfigurationName = {ConfigurationName}, "
        + $"EmailAddress = {EmailAddress}, Host = {Host}, Port = {Port}, Username = {Username}, "
        + $"Password = {MailboxSecretRedaction.Marker(Password)}, UseSsl = {UseSsl}, "
        + $"PollingInterval = {PollingInterval}, IsActive = {IsActive}, "
        + $"VerifyBeforeSave = {VerifyBeforeSave} }}";
}

/// <summary>
/// Test settings that have not been saved. <see cref="MailboxId"/> lets an operator re-test a
/// stored mailbox without retyping its password: when supplied and <see cref="Password"/> is
/// blank, the stored credential is used. The password still only travels inbound.
/// </summary>
public sealed record MailboxTestRequestDTO
{
    public long? MailboxId { get; init; }

    [Required]
    public string Protocol { get; init; } = string.Empty;

    [Required, StringLength(253, MinimumLength = 3)]
    public string Host { get; init; } = string.Empty;

    [Range(1, 65535)]
    public int Port { get; init; }

    /// <summary>
    /// The mailbox address. REQUIRED for an inbound test, because it is the identity the
    /// poller signs in with — see <c>MailboxLoginIdentity</c>. Without it this endpoint tested
    /// <see cref="Username"/> and therefore tested a credential the IMAP poller never uses.
    /// Optional for SMTP, which genuinely authenticates as <see cref="Username"/>.
    /// </summary>
    [StringLength(320)]
    public string? EmailAddress { get; init; }

    [Required, StringLength(320)]
    public string Username { get; init; } = string.Empty;

    [StringLength(1024)]
    public string? Password { get; init; }

    public bool UseSsl { get; init; } = true;

    /// <inheritdoc cref="MailboxSecretRedaction.Marker"/>
    public override string ToString() =>
        $"MailboxTestRequestDTO {{ MailboxId = {MailboxId}, Protocol = {Protocol}, Host = {Host}, "
        + $"Port = {Port}, EmailAddress = {EmailAddress}, Username = {Username}, "
        + $"Password = {MailboxSecretRedaction.Marker(Password)}, UseSsl = {UseSsl} }}";
}

/// <summary>
/// SEC-G9. Shared marker for the three mailbox request records that carry a live customer
/// mailbox password inbound.
/// </summary>
internal static class MailboxSecretRedaction
{
    /// <summary>
    /// A record's compiler-generated ToString prints EVERY property, so a single
    /// <c>_logger.LogWarning("mailbox test failed for {Request}", request)</c> — the most natural
    /// line anyone would write while diagnosing exactly the failure these DTOs exist to surface —
    /// would put a live customer mailbox credential in the log. The response DTO deliberately
    /// carries no password at all for the same reason (see <see cref="MailboxResponseDTO"/>);
    /// nothing protected the inbound half. Redacted at the source rather than by trusting every
    /// future call site, matching the overrides on <c>IssuedTenantAdminInvitation</c> and
    /// <c>ProvisioningSubmitResult</c>.
    ///
    /// <para>Present-or-absent is kept, because on the update and test records a blank password
    /// MEANS "keep the stored one" — so "was a password supplied" is the first question anyone
    /// diagnosing them asks, and it discloses nothing.</para>
    /// </summary>
    public static string Marker(string? secret) =>
        string.IsNullOrEmpty(secret) ? "none" : "[redacted]";
}

/// <summary>
/// Whether customer-facing mail can currently leave this tenant.
///
/// <para>This exists because the answer is NOT knowable from configuration. Two SMTP transports
/// read host, port and credentials straight from the <c>Email_Configurations</c> row and never
/// consult the notification guard, so an active SMTP row is the real, and only, switch. Anyone
/// about to point the poller at a mailbox of live customer correspondence needs this stated
/// plainly on the screen rather than inferred from a settings file.</para>
/// </summary>
public sealed record OutboundMailStatusDTO
{
    public bool CanSendToCustomers { get; init; }
    public int ActiveSmtpCount { get; init; }
    public IReadOnlyList<string> ActiveSmtpHosts { get; init; } = [];
    public string Summary { get; init; } = string.Empty;

    /// <summary>True when more than one SMTP row is active. The send paths silently take the
    /// lowest Id, so the extra rows are dead weight that reads as configuration.</summary>
    public bool HasAmbiguousOutbound { get; init; }

    /// <summary>Same ambiguity on the inbound side: several active IMAP rows are each polled.</summary>
    public int ActiveImapCount { get; init; }
}

public sealed record MailboxPresetDTO(
    string Key,
    string DisplayName,
    string ImapHost,
    int ImapPort,
    string SmtpHost,
    int SmtpPort,
    bool UseSsl,
    string Guidance);
