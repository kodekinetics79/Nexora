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

    /// <summary>
    /// 255 IS THE COLUMN LIMIT, NOT THE EMAIL LIMIT. <c>Email_Configurations."EmailAddress"</c> is
    /// <c>character varying(255)</c>, so 255 is the longest address this system can STORE.
    ///
    /// <para><b>Do not "correct" this back to 320.</b> 320 is the right number for email — RFC 5321
    /// caps an address at 64 characters of local part, an @, and 255 of domain — and it is exactly
    /// what this attribute said before. That is what made it a defect rather than a typo: the
    /// number was correct about the protocol and wrong about the table, so a 262-character address
    /// passed ModelState, was mapped onto the entity, and died inside the INSERT as Postgres
    /// <c>22001</c>. The mailbox screen answered "An unexpected error occurred." — on the one
    /// screen a customer must finish before this product ingests anything at all.</para>
    ///
    /// <para>Refusing a genuinely valid 260-character address with a clean 400 is the accepted cost
    /// of that. Addresses over 255 characters essentially do not occur; an unexplained 500 during
    /// onboarding is not survivable. Widening the COLUMN would need a migration, and
    /// <c>Program.cs</c> runs <c>MigrateAsync()</c> unguarded at startup — a failed migration fails
    /// the DEPLOY rather than degrading. If the limit ever genuinely binds, the fix is a migration
    /// widening BOTH columns to 320 with this cap raised in the same change, never this cap alone.</para>
    /// </summary>
    [Required, EmailAddress, StringLength(255)]
    public string EmailAddress { get; init; } = string.Empty;

    /// <summary>IMAP (read leads in) or SMTP (send quotes out).</summary>
    [Required]
    public string Protocol { get; init; } = string.Empty;

    [Required, StringLength(253, MinimumLength = 3)]
    public string Host { get; init; } = string.Empty;

    [Range(1, 65535)]
    public int Port { get; init; }

    /// <summary>
    /// 255 IS THE COLUMN LIMIT, NOT THE EMAIL LIMIT — <c>Email_Configurations."Username"</c> is
    /// <c>character varying(255)</c>. See <see cref="EmailAddress"/> for the full account; the
    /// sign-in name is frequently an address, so it carried the identical 320 and the identical
    /// defect. Do not widen this without widening the column in the same change.
    /// </summary>
    [Required, StringLength(255)]
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

    /// <inheritdoc cref="MailboxCreateRequestDTO.EmailAddress"/>
    [Required, EmailAddress, StringLength(255)]
    public string EmailAddress { get; init; } = string.Empty;

    [Required, StringLength(253, MinimumLength = 3)]
    public string Host { get; init; } = string.Empty;

    [Range(1, 65535)]
    public int Port { get; init; }

    /// <inheritdoc cref="MailboxCreateRequestDTO.Username"/>
    [Required, StringLength(255)]
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

    // ---- the sender the code will actually use (issue #54) --------------------------------
    //
    // Read from IOutboundSenderResolver, the SAME authority the quote sender and the supplier
    // RFQ worker consult, so this screen can no longer say "quotes WILL be delivered through X"
    // while the send path does something else.

    /// <summary>"tenant" (this tenant's own mailbox), "platform" (the operator's stored
    /// configuration) or "configuration" (deployment settings, nothing stored).</summary>
    public string SenderOrigin { get; init; } = string.Empty;

    /// <summary>The From address customers and suppliers will see.</summary>
    public string SenderAddress { get; init; } = string.Empty;

    public string SenderName { get; init; } = string.Empty;

    /// <summary>The SMTP host that will be dialled, or null for an HTTP provider / console.</summary>
    public string? SenderHost { get; init; }

    /// <summary>The mailbox row that sends, when <see cref="SenderOrigin"/> is "tenant".</summary>
    public long? SenderMailboxId { get; init; }

    public string? SenderMailboxName { get; init; }

    /// <summary>"Live", or the platform containment mode (Redirect / AllowListOnly / DraftOnly)
    /// that will intercept every send, tenant mailbox or not.</summary>
    public string ContainmentMode { get; init; } = "Live";
}

/// <summary>Body of <c>POST /api/Mailbox/{id}/send-test</c>.</summary>
public sealed class MailboxSendTestRequestDTO
{
    /// <summary>Where to send. Must be the signed-in user's own address or the mailbox's own
    /// address: this endpoint proves a channel works, it is not a relay.</summary>
    [System.ComponentModel.DataAnnotations.Required]
    [System.ComponentModel.DataAnnotations.EmailAddress]
    [System.ComponentModel.DataAnnotations.StringLength(320)]
    public string Recipient { get; set; } = null!;
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
