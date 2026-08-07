namespace ERP_RFQ_Automation.Platform.Notifications;

/// <summary>
/// The platform's OUTBOUND email identity and transport, stored so it can be changed by an
/// operator without a redeploy.
///
/// <para><b>Not to be confused with <c>EmailConfiguration</c>.</b> That entity is per-tenant
/// INBOUND: the customer's own IMAP mailbox that leads are ingested from, one row per business
/// unit, owned by the tenant. This is exactly one row for the whole platform, describing how
/// Nexora sends: the address activation links come from, the SMTP host or SendGrid key they go
/// through, and the containment mode applied on the way out. They share nothing but the word
/// "email", and conflating them would give every tenant the ability to rewrite the platform's
/// sending identity.</para>
///
/// <para><b>Why it exists at all.</b> The transport was previously selected once at startup from
/// <c>Notifications:Provider</c> and registered as a singleton. The default was <c>console</c>,
/// which logs instead of sending, so a pilot deployment swallowed every activation link, invoice
/// notice and quote delivery — and the only signal was <c>emailSent: false</c> in a provisioning
/// response, with no reason and no way to fix it short of editing appsettings and redeploying.</para>
///
/// <para><b>Single row by construction.</b> The primary key is pinned to
/// <see cref="SingletonId"/> by a check constraint rather than by convention. A second row would
/// not be a duplicate — it would be an ambiguity about which identity the platform sends under,
/// resolved differently by whichever query happened to run.</para>
/// </summary>
public class PlatformEmailSettings
{
    /// <summary>The only permitted primary key. Enforced by a database check constraint.</summary>
    public const long SingletonId = 1;

    public long Id { get; set; } = SingletonId;

    /// <summary>"console" | "smtp" | "sendgrid". Anything unrecognised resolves to console, which
    /// logs rather than sends — the same fail-safe the configuration path has always had.</summary>
    public string Provider { get; set; } = "console";

    public string FromAddress { get; set; } = string.Empty;

    public string FromName { get; set; } = string.Empty;

    public string? ReplyToAddress { get; set; }

    /// <summary>Base URL used to build the links inside outbound mail — including the founding
    /// administrator's activation link. A correct transport with the wrong base URL delivers an
    /// email that leads nowhere, which is the same dead end as not sending it.</summary>
    public string AppBaseUrl { get; set; } = string.Empty;

    public string? SmtpHost { get; set; }

    public int SmtpPort { get; set; } = 587;

    public string? SmtpUsername { get; set; }

    /// <summary>
    /// Encrypted at rest by <c>ProtectedSecretConverter</c> (AES-256-GCM), exactly as the per-tenant
    /// mailbox password is. The key lives in application configuration
    /// (<c>Security:SecretProtectionKey</c>) and deliberately never in the database, so a database
    /// backup or a role that bypasses RLS yields ciphertext and nothing else.
    ///
    /// <para>This property holds PLAINTEXT in memory — the converter applies at the persistence
    /// boundary. It must therefore never be projected into a DTO, a log line, or audit metadata.
    /// The read model reports whether a secret is set, never what it is.</para>
    /// </summary>
    public string? SmtpPassword { get; set; }

    public bool SmtpEnableSsl { get; set; } = true;

    public int SmtpTimeoutMs { get; set; } = 30000;

    /// <summary>Encrypted at rest, on the same terms as <see cref="SmtpPassword"/>.</summary>
    public string? SendGridApiKey { get; set; }

    /// <summary>Overridable for EU data residency.</summary>
    public string? SendGridApiBaseUrl { get; set; }

    /// <summary>Live | AllowListOnly | Redirect | DraftOnly. Persisted as the enum NAME so a
    /// reordering of <c>OutboundEmailMode</c> can never silently reclassify a deployment from
    /// contained to live.</summary>
    public string OutboundGuardMode { get; set; } = "Live";

    public string? OutboundGuardRedirectTo { get; set; }

    /// <summary>Newline-separated addresses. Stored as delimited text rather than jsonb because the
    /// SQLite integration suite builds this same model, and a jsonb column would make the table
    /// PostgreSQL-only for no benefit on a list that is read once per settings load.</summary>
    public string? OutboundGuardAllowedRecipients { get; set; }

    /// <summary>Newline-separated domains, with or without a leading '@'.</summary>
    public string? OutboundGuardAllowedDomains { get; set; }

    public string? OutboundGuardSubjectTag { get; set; }

    /// <summary>
    /// Bumped on every save. Two jobs: it is the optimistic-concurrency token, so two operators
    /// editing from two tabs cannot silently overwrite each other's credentials; and it is what the
    /// transport cache revalidates against, so a running instance can notice a change with a
    /// two-column read instead of re-selecting the encrypted secrets on every send.
    /// </summary>
    public long Version { get; set; } = 1;

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;

    /// <summary>Email of the platform operator who last saved. The full audit trail is in
    /// <c>platform."PlatformAuditLogs"</c>; this is the copy the status readout renders without a
    /// second query.</summary>
    public string? UpdatedBy { get; set; }

    public string? UpdateReason { get; set; }

    /// <summary>
    /// When a verification send last SUCCEEDED, and to where.
    ///
    /// <para>Durable on purpose, and durable only for verifications. Ordinary sends record their
    /// outcome in the process-local <c>IOutboundEmailHealth</c> ledger instead: writing to this row
    /// on every outbound message would put an UPDATE of one global row inside every tenant
    /// transaction that happens to send an email — a contention point, and one that a rolled-back
    /// business transaction would silently undo. A verification is operator-initiated, rare, and
    /// the one fact that must survive a restart, because "somebody proved this worked at 14:05" is
    /// what an operator needs when the process-local ledger is empty.</para>
    /// </summary>
    public DateTime? LastVerifiedAtUtc { get; set; }

    public string? LastVerifiedBy { get; set; }

    public string? LastVerifiedRecipient { get; set; }

    public DateTime? LastFailureAtUtc { get; set; }

    /// <summary>The <c>OutboundEmailFailureKind</c> name of the last verification failure.</summary>
    public string? LastFailureKind { get; set; }

    /// <summary>Operator-readable guidance for the last verification failure. Never the provider's
    /// raw text and never anything derived from a credential.</summary>
    public string? LastFailureReason { get; set; }
}
