using System.ComponentModel.DataAnnotations;

namespace ERP_RFQ_Automation.Platform.Notifications;

/// <summary>
/// What a read endpoint may return.
///
/// <para><b>No secret appears on this type, and none can be added by accident.</b> There is no
/// password or API-key property to populate — only <see cref="HasSmtpPassword"/> and
/// <see cref="HasSendGridApiKey"/>, which answer the only question an operator actually has ("is
/// one set?"). A masked value would have been worse than useless: it still travels, still lands in
/// a browser cache and a proxy log, and tells the operator nothing extra.</para>
/// </summary>
public sealed class PlatformEmailSettingsDto
{
    public string Provider { get; set; } = "console";
    public string FromAddress { get; set; } = string.Empty;
    public string FromName { get; set; } = string.Empty;
    public string? ReplyToAddress { get; set; }
    public string AppBaseUrl { get; set; } = string.Empty;

    public string? SmtpHost { get; set; }
    public int SmtpPort { get; set; }
    public string? SmtpUsername { get; set; }
    public bool HasSmtpPassword { get; set; }
    public bool SmtpEnableSsl { get; set; }
    public int SmtpTimeoutMs { get; set; }

    public bool HasSendGridApiKey { get; set; }
    public string? SendGridApiBaseUrl { get; set; }

    public string OutboundGuardMode { get; set; } = "Live";
    public string? OutboundGuardRedirectTo { get; set; }
    public IReadOnlyList<string> OutboundGuardAllowedRecipients { get; set; } = Array.Empty<string>();
    public IReadOnlyList<string> OutboundGuardAllowedDomains { get; set; } = Array.Empty<string>();
    public string? OutboundGuardSubjectTag { get; set; }

    /// <summary>"Configuration" until an operator saves for the first time. Distinguishes "nobody
    /// has set this up, you are looking at appsettings" from "somebody set it up and chose this".</summary>
    public string Origin { get; set; } = nameof(ERP_RFQ_Automation.Notifications.Runtime.OutboundEmailSettingsOrigin.Configuration);

    /// <summary>Concurrency token to echo back on save. Null when nothing is stored yet.</summary>
    public long? Version { get; set; }

    public DateTime? UpdatedAtUtc { get; set; }
    public string? UpdatedBy { get; set; }
    public string? UpdateReason { get; set; }
}

/// <summary>
/// A full replacement of the stored configuration.
///
/// <para><b>Secret semantics, stated once and applied everywhere:</b> <c>null</c> (or omitted)
/// means KEEP the stored secret, empty string means CLEAR it. A console that renders an empty
/// password box — which is the only honest thing it can render, since it is never given the value —
/// would otherwise wipe the credential every time an operator changed the port.</para>
/// </summary>
public sealed class SavePlatformEmailSettingsRequest
{
    [Required]
    [RegularExpression("^(?i)(console|smtp|sendgrid)$",
        ErrorMessage = "Provider must be 'console', 'smtp' or 'sendgrid'.")]
    public string Provider { get; set; } = "console";

    [Required, EmailAddress, StringLength(320)]
    public string FromAddress { get; set; } = string.Empty;

    [Required, StringLength(200)]
    public string FromName { get; set; } = string.Empty;

    [EmailAddress, StringLength(320)]
    public string? ReplyToAddress { get; set; }

    [Required, StringLength(512)]
    public string AppBaseUrl { get; set; } = string.Empty;

    [StringLength(255)]
    public string? SmtpHost { get; set; }

    [Range(1, 65535)]
    public int SmtpPort { get; set; } = 587;

    [StringLength(320)]
    public string? SmtpUsername { get; set; }

    /// <summary>Null keeps the stored password; empty string clears it.</summary>
    [StringLength(512)]
    public string? SmtpPassword { get; set; }

    public bool SmtpEnableSsl { get; set; } = true;

    [Range(1000, 300000)]
    public int SmtpTimeoutMs { get; set; } = 30000;

    /// <summary>Null keeps the stored key; empty string clears it.</summary>
    [StringLength(512)]
    public string? SendGridApiKey { get; set; }

    [StringLength(512)]
    public string? SendGridApiBaseUrl { get; set; }

    [RegularExpression("^(?i)(Live|AllowListOnly|Redirect|DraftOnly)$",
        ErrorMessage = "Outbound guard mode must be Live, AllowListOnly, Redirect or DraftOnly.")]
    public string OutboundGuardMode { get; set; } = "Live";

    [EmailAddress, StringLength(320)]
    public string? OutboundGuardRedirectTo { get; set; }

    public List<string> OutboundGuardAllowedRecipients { get; set; } = new();

    public List<string> OutboundGuardAllowedDomains { get; set; } = new();

    [StringLength(64)]
    public string? OutboundGuardSubjectTag { get; set; }

    /// <summary>The <c>Version</c> the operator was editing. Omit on the first save. A mismatch is a
    /// 409 rather than a silent overwrite of somebody else's credentials.</summary>
    public long? ExpectedVersion { get; set; }

    /// <summary>Why. Required, and audited — changing the platform's sending identity is a
    /// privileged act, and "who changed the From address in March" is a question that gets asked.</summary>
    [Required, StringLength(1000, MinimumLength = 3)]
    public string Reason { get; set; } = string.Empty;
}

/// <summary>Candidate settings for a verification send. Every field is optional: what is supplied
/// overrides the stored configuration for this one attempt, and what is not is inherited — so an
/// operator can test a new SMTP host without re-typing a password the console never received.</summary>
public sealed class CandidatePlatformEmailSettings
{
    [RegularExpression("^(?i)(console|smtp|sendgrid)$")]
    public string? Provider { get; set; }

    [EmailAddress, StringLength(320)]
    public string? FromAddress { get; set; }

    [StringLength(200)]
    public string? FromName { get; set; }

    [EmailAddress, StringLength(320)]
    public string? ReplyToAddress { get; set; }

    [StringLength(255)]
    public string? SmtpHost { get; set; }

    [Range(1, 65535)]
    public int? SmtpPort { get; set; }

    [StringLength(320)]
    public string? SmtpUsername { get; set; }

    [StringLength(512)]
    public string? SmtpPassword { get; set; }

    public bool? SmtpEnableSsl { get; set; }

    [Range(1000, 300000)]
    public int? SmtpTimeoutMs { get; set; }

    [StringLength(512)]
    public string? SendGridApiKey { get; set; }

    [StringLength(512)]
    public string? SendGridApiBaseUrl { get; set; }
}

public sealed class TestOutboundEmailRequest
{
    [Required, EmailAddress, StringLength(320)]
    public string Recipient { get; set; } = string.Empty;

    /// <summary>Unsaved settings to test. Omit to test exactly what is stored.</summary>
    public CandidatePlatformEmailSettings? Settings { get; set; }
}

public sealed class TestOutboundEmailResponse
{
    public bool Succeeded { get; set; }

    /// <summary>False when nothing left the process. A console provider "succeeds" and transmits
    /// nothing, and an operator must not read that as proof that mail works.</summary>
    public bool Transmitted { get; set; }

    /// <summary>Classified cause: None, NotConfigured, AuthenticationFailed, HostUnreachable,
    /// TlsFailure, RelayDenied, RecipientRejected, Timeout, RateLimited, ProviderRejected,
    /// BlockedByOutboundGuard, Unknown.</summary>
    public string Kind { get; set; } = nameof(ERP_RFQ_Automation.Notifications.OutboundEmailFailureKind.None);

    /// <summary>Operator-facing sentence naming the next action. Never the provider's raw text.</summary>
    public string Message { get; set; } = string.Empty;

    /// <summary>Protocol status, e.g. "SMTP 550" or "401". Safe to display; never a credential.</summary>
    public string? ProviderStatus { get; set; }

    public string Provider { get; set; } = "console";
    public string OutboundGuardMode { get; set; } = "Live";
    public string IntendedRecipient { get; set; } = string.Empty;

    /// <summary>Where it actually went — different from the intended address when the outbound
    /// guard is redirecting.</summary>
    public string EffectiveRecipient { get; set; } = string.Empty;

    public string? AcceptanceReference { get; set; }
    public DateTimeOffset AttemptedAtUtc { get; set; }
    public long ElapsedMs { get; set; }
}

/// <summary>
/// One-glance answer to "is mail working?".
/// </summary>
public sealed class OutboundEmailStatusDto
{
    /// <summary>The single sentence an operator should be able to read and stop.</summary>
    public string Summary { get; set; } = string.Empty;

    public string Provider { get; set; } = "console";
    public string Origin { get; set; } = string.Empty;

    /// <summary>The fact everything else hangs off: false means messages are logged and discarded.</summary>
    public bool IsSending { get; set; }

    public bool CredentialsSet { get; set; }
    public string FromAddress { get; set; } = string.Empty;
    public string FromName { get; set; } = string.Empty;
    public string? ReplyToAddress { get; set; }
    public string AppBaseUrl { get; set; } = string.Empty;
    public bool HasSmtpPassword { get; set; }
    public bool HasSendGridApiKey { get; set; }

    public string OutboundGuardMode { get; set; } = "Live";
    public string? OutboundGuardRedirectTo { get; set; }

    /// <summary>Last real message accepted by a transmitting provider, as observed by THIS process.
    /// Null after a restart even if mail is working — which is why the verification stamp below is
    /// persisted.</summary>
    public DateTimeOffset? LastSuccessfulSendAtUtc { get; set; }
    public string? LastSuccessfulSendProvider { get; set; }

    public DateTimeOffset? LastFailureAtUtc { get; set; }
    public string? LastFailureKind { get; set; }
    public string? LastFailureReason { get; set; }
    public int ConsecutiveFailures { get; set; }

    /// <summary>Durable: the last verification send that succeeded, and who ran it.</summary>
    public DateTime? LastVerifiedAtUtc { get; set; }
    public string? LastVerifiedBy { get; set; }
    public string? LastVerifiedRecipient { get; set; }

    public DateTime? LastVerificationFailureAtUtc { get; set; }
    public string? LastVerificationFailureKind { get; set; }
    public string? LastVerificationFailureReason { get; set; }

    public DateTime? ConfiguredAtUtc { get; set; }
    public string? ConfiguredBy { get; set; }

    /// <summary>Non-fatal configuration problems in the settings that are actually in force.</summary>
    public IReadOnlyList<string> Warnings { get; set; } = Array.Empty<string>();
}
