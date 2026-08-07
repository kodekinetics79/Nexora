using System.Security.Claims;
using ERP_RFQ_Automation.Notifications;
using ERP_RFQ_Automation.Notifications.Runtime;
using ERP_RFQ_Automation.Platform.Services;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.Platform.Notifications;

/// <summary>Outcome of a save. A conflict is distinguished from a validation refusal because they
/// need different words in front of an operator: one means "somebody else changed this", the other
/// "this configuration cannot work".</summary>
public sealed record PlatformEmailSaveResult(
    bool Succeeded,
    PlatformEmailSettingsDto? Settings,
    string? Error,
    bool Conflict = false);

public interface IPlatformEmailSettingsService
{
    Task<PlatformEmailSettingsDto> GetAsync(CancellationToken ct = default);

    Task<PlatformEmailSaveResult> SaveAsync(
        SavePlatformEmailSettingsRequest request,
        ClaimsPrincipal actor,
        HttpContext? httpContext,
        CancellationToken ct = default);

    Task<OutboundEmailStatusDto> GetStatusAsync(CancellationToken ct = default);

    Task<TestOutboundEmailResponse> TestSendAsync(
        TestOutboundEmailRequest request,
        ClaimsPrincipal actor,
        HttpContext? httpContext,
        CancellationToken ct = default);
}

/// <summary>
/// The operator-facing behaviour behind the platform email endpoints.
///
/// <para>Runs on the platform plane only (<c>nexora_pipeline_app</c>), which is why this class may
/// materialise the whole entity while <see cref="PlatformEmailSettingsStore"/> — reached from every
/// plane — must project. The distinction is the difference between a query that works and a 42501
/// on a tenant request.</para>
/// </summary>
public sealed class PlatformEmailSettingsService : IPlatformEmailSettingsService
{
    private readonly DbContext _db;
    private readonly OutboundEmailTransportResolver _resolver;
    private readonly OutboundEmailProbe _probe;
    private readonly IOutboundEmailHealth _health;
    private readonly IPlatformAuditService _audit;
    private readonly ILogger<PlatformEmailSettingsService> _log;
    private readonly TimeProvider _time;

    public const string ConfigureAction = "platform.email.configure";
    public const string TestAction = "platform.email.test";

    public PlatformEmailSettingsService(
        DbContext db,
        OutboundEmailTransportResolver resolver,
        OutboundEmailProbe probe,
        IOutboundEmailHealth health,
        IPlatformAuditService audit,
        ILogger<PlatformEmailSettingsService> log,
        TimeProvider? timeProvider = null)
    {
        _db = db;
        _resolver = resolver;
        _probe = probe;
        _health = health;
        _audit = audit;
        _log = log;
        _time = timeProvider ?? TimeProvider.System;
    }

    // ==== read ======================================================================================

    public async Task<PlatformEmailSettingsDto> GetAsync(CancellationToken ct = default)
    {
        var row = await LoadAsync(track: false, ct);
        if (row is not null) return ToDto(row);

        // Nothing stored: report what the process is actually using, marked as coming from
        // configuration. An empty form here would tell the operator they have no sending identity
        // when in fact appsettings has one.
        var fallback = _resolver.Current;
        return new PlatformEmailSettingsDto
        {
            Provider = fallback.NormalizedProvider,
            FromAddress = fallback.FromAddress,
            FromName = fallback.FromName,
            ReplyToAddress = fallback.ReplyToAddress,
            AppBaseUrl = fallback.AppBaseUrl,
            SmtpHost = fallback.SmtpHost,
            SmtpPort = fallback.SmtpPort,
            SmtpUsername = fallback.SmtpUsername,
            HasSmtpPassword = !string.IsNullOrWhiteSpace(fallback.SmtpPassword),
            SmtpEnableSsl = fallback.SmtpEnableSsl,
            SmtpTimeoutMs = fallback.SmtpTimeoutMs,
            HasSendGridApiKey = !string.IsNullOrWhiteSpace(fallback.SendGridApiKey),
            SendGridApiBaseUrl = fallback.SendGridApiBaseUrl,
            OutboundGuardMode = fallback.GuardMode.ToString(),
            OutboundGuardRedirectTo = fallback.OutboundGuardRedirectTo,
            OutboundGuardAllowedRecipients = fallback.OutboundGuardAllowedRecipients,
            OutboundGuardAllowedDomains = fallback.OutboundGuardAllowedDomains,
            OutboundGuardSubjectTag = fallback.OutboundGuardSubjectTag,
            Origin = nameof(OutboundEmailSettingsOrigin.Configuration),
            Version = null
        };
    }

    public async Task<OutboundEmailStatusDto> GetStatusAsync(CancellationToken ct = default)
    {
        var settings = (await _resolver.ResolveAsync(ct)).Settings;
        var channel = _health.Snapshot();
        var row = await LoadAsync(track: false, ct);

        var status = new OutboundEmailStatusDto
        {
            Provider = settings.NormalizedProvider,
            Origin = settings.Origin.ToString(),
            IsSending = settings.TransmitsMail,
            CredentialsSet = settings.HasProviderCredentials,
            FromAddress = settings.FromAddress,
            FromName = settings.FromName,
            ReplyToAddress = settings.ReplyToAddress,
            AppBaseUrl = settings.AppBaseUrl,
            HasSmtpPassword = !string.IsNullOrWhiteSpace(settings.SmtpPassword),
            HasSendGridApiKey = !string.IsNullOrWhiteSpace(settings.SendGridApiKey),
            OutboundGuardMode = settings.GuardMode.ToString(),
            OutboundGuardRedirectTo = settings.OutboundGuardRedirectTo,
            LastSuccessfulSendAtUtc = channel.LastSuccessUtc,
            LastSuccessfulSendProvider = channel.LastSuccessProvider,
            LastFailureAtUtc = channel.LastFailureUtc,
            LastFailureKind = channel.LastFailureKind == OutboundEmailFailureKind.None
                ? null
                : channel.LastFailureKind.ToString(),
            LastFailureReason = channel.LastFailureMessage,
            ConsecutiveFailures = channel.ConsecutiveFailures,
            LastVerifiedAtUtc = row?.LastVerifiedAtUtc,
            LastVerifiedBy = row?.LastVerifiedBy,
            LastVerifiedRecipient = row?.LastVerifiedRecipient,
            LastVerificationFailureAtUtc = row?.LastFailureAtUtc,
            LastVerificationFailureKind = row?.LastFailureKind,
            LastVerificationFailureReason = row?.LastFailureReason,
            ConfiguredAtUtc = row?.UpdatedAtUtc,
            ConfiguredBy = row?.UpdatedBy,
            Warnings = settings.ToNotificationsOptions().Validate()
        };

        status.Summary = Summarize(status);
        return status;
    }

    /// <summary>
    /// The sentence that has to be readable at a glance. Ordered by what would hurt most: a channel
    /// that transmits nothing outranks a credential problem, which outranks a run of failures,
    /// because the first one produces no errors anywhere and therefore looks healthiest.
    /// </summary>
    private static string Summarize(OutboundEmailStatusDto status)
    {
        var guard = status.OutboundGuardMode is "Live"
            ? string.Empty
            : $" The outbound guard is in {status.OutboundGuardMode} mode, so delivery is contained.";

        if (!status.IsSending)
            return "Email is NOT being sent. The active provider is 'console', which writes messages to " +
                   "the log and discards them — activation links, quotes and invoice notices are not " +
                   "reaching anyone. Configure SMTP or SendGrid." + guard;

        if (!status.CredentialsSet)
            return $"Provider '{status.Provider}' is selected but has no credentials, so every send fails. " +
                   "Set the SMTP host (or the SendGrid API key)." + guard;

        if (status.ConsecutiveFailures > 0)
            return $"Email is FAILING via {status.Provider}: {status.LastFailureReason}" + guard;

        if (status.LastSuccessfulSendAtUtc is { } sentAt)
            return $"Email is working. Last message accepted by {status.Provider} at " +
                   $"{sentAt.UtcDateTime:yyyy-MM-dd HH:mm} UTC." + guard;

        if (status.LastVerifiedAtUtc is { } verifiedAt)
            return $"Configured for {status.Provider} and last verified at " +
                   $"{verifiedAt:yyyy-MM-dd HH:mm} UTC. Nothing has been sent since this instance started." + guard;

        return $"Configured for {status.Provider}, but nothing has been sent or verified yet. " +
               "Run a test send before relying on it." + guard;
    }

    // ==== write =====================================================================================

    public async Task<PlatformEmailSaveResult> SaveAsync(
        SavePlatformEmailSettingsRequest request,
        ClaimsPrincipal actor,
        HttpContext? httpContext,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!IsMapped)
            return new PlatformEmailSaveResult(false, null,
                "The platform email settings table is not present in this deployment yet. Apply the " +
                "PlatformEmailSettings migration, then save again. Outbound mail is currently taking its " +
                "configuration from the Notifications section of appsettings.");

        var provider = (request.Provider ?? "console").Trim().ToLowerInvariant();
        var guardMode = NormalizeGuardMode(request.OutboundGuardMode);

        var row = await LoadAsync(track: true, ct);

        // Checked before the write so the operator gets "somebody else changed this, reload" rather
        // than a DbUpdateConcurrencyException surfacing as a 500. The concurrency token on Version
        // is still the real guarantee — this is the readable half of it.
        if (row is not null && request.ExpectedVersion is { } expected && expected != row.Version)
            return new PlatformEmailSaveResult(false, null,
                "These settings were changed by someone else while you were editing. Reload and reapply your change.",
                Conflict: true);

        var isNew = row is null;
        if (row is null)
        {
            row = new PlatformEmailSettings { Id = PlatformEmailSettings.SingletonId, CreatedAtUtc = UtcNow() };
            _db.Set<PlatformEmailSettings>().Add(row);
        }

        // Null keeps, empty clears. Resolved BEFORE validation so a provider validated as
        // "credentialed" is judged against the credential it will actually have.
        var smtpPassword = ResolveSecret(request.SmtpPassword, row.SmtpPassword);
        var sendGridKey = ResolveSecret(request.SendGridApiKey, row.SendGridApiKey);

        var refusal = Validate(provider, guardMode, request, smtpPassword, sendGridKey);
        if (refusal is not null)
        {
            // Detach an entity that was added for a save that is not happening, or the next
            // SaveChanges on this scoped context — the audit write, for instance — would persist it.
            if (isNew) _db.Entry(row).State = EntityState.Detached;
            return new PlatformEmailSaveResult(false, null, refusal);
        }

        var secretsChanged =
            !string.Equals(smtpPassword, row.SmtpPassword, StringComparison.Ordinal) ||
            !string.Equals(sendGridKey, row.SendGridApiKey, StringComparison.Ordinal);

        row.Provider = provider;
        row.FromAddress = request.FromAddress.Trim();
        row.FromName = request.FromName.Trim();
        row.ReplyToAddress = Blank(request.ReplyToAddress);
        row.AppBaseUrl = request.AppBaseUrl.Trim().TrimEnd('/');
        row.SmtpHost = Blank(request.SmtpHost);
        row.SmtpPort = request.SmtpPort;
        row.SmtpUsername = Blank(request.SmtpUsername);
        row.SmtpPassword = smtpPassword;
        row.SmtpEnableSsl = request.SmtpEnableSsl;
        row.SmtpTimeoutMs = request.SmtpTimeoutMs;
        row.SendGridApiKey = sendGridKey;
        row.SendGridApiBaseUrl = Blank(request.SendGridApiBaseUrl);
        row.OutboundGuardMode = guardMode;
        row.OutboundGuardRedirectTo = Blank(request.OutboundGuardRedirectTo);
        row.OutboundGuardAllowedRecipients = PlatformEmailSettingsStore.JoinList(request.OutboundGuardAllowedRecipients);
        row.OutboundGuardAllowedDomains = PlatformEmailSettingsStore.JoinList(request.OutboundGuardAllowedDomains);
        row.OutboundGuardSubjectTag = Blank(request.OutboundGuardSubjectTag);
        row.UpdatedAtUtc = UtcNow();
        row.UpdatedBy = ActorLabel(actor);
        row.UpdateReason = request.Reason.Trim();
        row.Version = isNew ? 1 : row.Version + 1;

        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            return new PlatformEmailSaveResult(false, null,
                "These settings were changed by someone else while you were editing. Reload and reapply your change.",
                Conflict: true);
        }

        // Take effect now, in this process. Other instances converge on the resolver's revalidation
        // interval — bounded, stated, and the reason Version is cheap to read.
        _resolver.Invalidate();

        // Booleans, never values. A secret in audit metadata would defeat the encryption at rest by
        // copying the credential into a table that is deliberately readable for a very long time.
        await _audit.WriteAsync(actor, ConfigureAction, "PlatformEmailSettings", row.Id.ToString(),
            new
            {
                reason = row.UpdateReason,
                provider = row.Provider,
                fromAddress = row.FromAddress,
                replyToAddress = row.ReplyToAddress,
                appBaseUrl = row.AppBaseUrl,
                smtpHost = row.SmtpHost,
                smtpPort = row.SmtpPort,
                smtpUsername = row.SmtpUsername,
                smtpEnableSsl = row.SmtpEnableSsl,
                outboundGuardMode = row.OutboundGuardMode,
                outboundGuardRedirectTo = row.OutboundGuardRedirectTo,
                smtpPasswordSet = !string.IsNullOrEmpty(row.SmtpPassword),
                sendGridApiKeySet = !string.IsNullOrEmpty(row.SendGridApiKey),
                credentialsChanged = secretsChanged,
                version = row.Version
            },
            actAsTenantId: null, httpContext, ct);

        _log.LogWarning(
            "[Notifications] Platform outbound email configuration changed by {Actor} to provider {Provider} " +
            "(guard {Guard}, version {Version}). Reason: {Reason}",
            row.UpdatedBy, row.Provider, row.OutboundGuardMode, row.Version, row.UpdateReason);

        return new PlatformEmailSaveResult(true, ToDto(row), null);
    }

    // ==== verify ====================================================================================

    public async Task<TestOutboundEmailResponse> TestSendAsync(
        TestOutboundEmailRequest request,
        ClaimsPrincipal actor,
        HttpContext? httpContext,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var stored = (await _resolver.ResolveAsync(ct)).Settings;
        var candidate = Overlay(stored, request.Settings);
        var actorLabel = ActorLabel(actor);

        var result = await _probe.SendAsync(candidate, request.Recipient.Trim(), actorLabel, ct);

        // The durable half of the readout. Persisted only for verifications — an ordinary send
        // writing here would put an UPDATE of one global row inside every business transaction that
        // happens to mail somebody.
        await StampVerificationAsync(result, actorLabel, ct);

        // Audited whichever way it went: a verification send is a real message from the platform's
        // identity to an address the operator chose, and a failed one is the more interesting record.
        await _audit.WriteAsync(actor, TestAction, "PlatformEmailSettings",
            PlatformEmailSettings.SingletonId.ToString(),
            new
            {
                recipient = result.IntendedRecipient,
                effectiveRecipient = result.EffectiveRecipient,
                provider = result.Provider,
                outboundGuardMode = result.OutboundGuardMode,
                usedCandidateSettings = request.Settings is not null,
                succeeded = result.Succeeded,
                transmitted = result.Transmitted,
                kind = result.Kind.ToString(),
                providerStatus = result.ProviderStatus,
                elapsedMs = result.ElapsedMs
            },
            actAsTenantId: null, httpContext,
            result.Succeeded
                ? ERP_RFQ_Automation.Platform.Models.PlatformAuditResults.Success
                : ERP_RFQ_Automation.Platform.Models.PlatformAuditResults.Failure,
            ct);

        return new TestOutboundEmailResponse
        {
            Succeeded = result.Succeeded,
            Transmitted = result.Transmitted,
            Kind = result.Kind.ToString(),
            Message = result.Message,
            ProviderStatus = result.ProviderStatus,
            Provider = result.Provider,
            OutboundGuardMode = result.OutboundGuardMode,
            IntendedRecipient = result.IntendedRecipient,
            EffectiveRecipient = result.EffectiveRecipient,
            AcceptanceReference = result.AcceptanceReference,
            AttemptedAtUtc = result.AttemptedAtUtc,
            ElapsedMs = result.ElapsedMs
        };
    }

    private async Task StampVerificationAsync(
        OutboundEmailProbeResult result, string? actorLabel, CancellationToken ct)
    {
        var row = await LoadAsync(track: true, ct);
        if (row is null) return; // Nothing stored yet; there is no row to stamp and none to invent.

        var now = UtcNow();
        if (result.Succeeded && result.Transmitted)
        {
            row.LastVerifiedAtUtc = now;
            row.LastVerifiedBy = actorLabel;
            row.LastVerifiedRecipient = result.EffectiveRecipient;
            row.LastFailureAtUtc = null;
            row.LastFailureKind = null;
            row.LastFailureReason = null;
        }
        else if (!result.Succeeded)
        {
            row.LastFailureAtUtc = now;
            row.LastFailureKind = result.Kind.ToString();
            row.LastFailureReason = Truncate(result.Message, 1000);
        }
        else
        {
            // Succeeded without transmitting (console, or DraftOnly). Neither a verification nor a
            // failure: recording it either way would be a claim the attempt did not support.
            return;
        }

        // Version is untouched: the CONFIGURATION did not change, and bumping it would invalidate
        // every instance's cached transport for a verification stamp.
        await _db.SaveChangesAsync(ct);
    }

    // ==== internals =================================================================================

    /// <summary>True once the context has been spliced with <c>ApplyPlatformEmailModel</c>. The
    /// migration and the splice are owned elsewhere; until they land these endpoints report the
    /// configuration fallback and refuse to save, rather than throwing an unmapped-entity error at
    /// an operator who cannot act on it.</summary>
    private bool IsMapped => _db.Model.FindEntityType(typeof(PlatformEmailSettings)) is not null;

    private Task<PlatformEmailSettings?> LoadAsync(bool track, CancellationToken ct)
    {
        if (!IsMapped) return Task.FromResult<PlatformEmailSettings?>(null);

        var query = _db.Set<PlatformEmailSettings>().AsQueryable();
        if (!track) query = query.AsNoTracking();
        return query.FirstOrDefaultAsync(x => x.Id == PlatformEmailSettings.SingletonId, ct);
    }

    /// <summary>Candidate over stored, field by field. A null field means "keep what is saved",
    /// which is what makes it possible to test a new host with a password the console was never
    /// given.</summary>
    private static OutboundEmailSettingsSnapshot Overlay(
        OutboundEmailSettingsSnapshot stored, CandidatePlatformEmailSettings? candidate)
    {
        if (candidate is null) return stored;

        return stored with
        {
            Origin = OutboundEmailSettingsOrigin.Candidate,
            Provider = string.IsNullOrWhiteSpace(candidate.Provider) ? stored.Provider : candidate.Provider!.Trim(),
            FromAddress = string.IsNullOrWhiteSpace(candidate.FromAddress) ? stored.FromAddress : candidate.FromAddress!.Trim(),
            FromName = string.IsNullOrWhiteSpace(candidate.FromName) ? stored.FromName : candidate.FromName!.Trim(),
            ReplyToAddress = string.IsNullOrWhiteSpace(candidate.ReplyToAddress) ? stored.ReplyToAddress : candidate.ReplyToAddress!.Trim(),
            SmtpHost = string.IsNullOrWhiteSpace(candidate.SmtpHost) ? stored.SmtpHost : candidate.SmtpHost!.Trim(),
            SmtpPort = candidate.SmtpPort ?? stored.SmtpPort,
            SmtpUsername = string.IsNullOrWhiteSpace(candidate.SmtpUsername) ? stored.SmtpUsername : candidate.SmtpUsername!.Trim(),
            SmtpPassword = string.IsNullOrEmpty(candidate.SmtpPassword) ? stored.SmtpPassword : candidate.SmtpPassword,
            SmtpEnableSsl = candidate.SmtpEnableSsl ?? stored.SmtpEnableSsl,
            SmtpTimeoutMs = candidate.SmtpTimeoutMs ?? stored.SmtpTimeoutMs,
            SendGridApiKey = string.IsNullOrEmpty(candidate.SendGridApiKey) ? stored.SendGridApiKey : candidate.SendGridApiKey,
            SendGridApiBaseUrl = string.IsNullOrWhiteSpace(candidate.SendGridApiBaseUrl) ? stored.SendGridApiBaseUrl : candidate.SendGridApiBaseUrl!.Trim()
        };
    }

    /// <summary>Refusals that would otherwise become a silently dead channel. Each one is a state
    /// the old configuration path accepted and then failed at, at the worst possible moment.</summary>
    private static string? Validate(
        string provider,
        string guardMode,
        SavePlatformEmailSettingsRequest request,
        string? smtpPassword,
        string? sendGridApiKey)
    {
        if (provider == "smtp" && string.IsNullOrWhiteSpace(request.SmtpHost))
            return "Provider 'smtp' requires an SMTP host.";

        if (provider == "sendgrid" && string.IsNullOrWhiteSpace(sendGridApiKey))
            return "Provider 'sendgrid' requires an API key.";

        // A username with no password authenticates as nothing and fails at the server, which
        // reports it as bad credentials — a failure that costs an operator an afternoon.
        if (provider == "smtp" &&
            !string.IsNullOrWhiteSpace(request.SmtpUsername) &&
            string.IsNullOrEmpty(smtpPassword))
            return "An SMTP username was given with no password. Provide the password, or clear the username for an unauthenticated relay.";

        if (string.Equals(guardMode, nameof(OutboundEmailMode.Redirect), StringComparison.OrdinalIgnoreCase) &&
            string.IsNullOrWhiteSpace(request.OutboundGuardRedirectTo))
            return "Outbound guard mode 'Redirect' requires a sink address, or every send will be refused.";

        if (string.Equals(guardMode, nameof(OutboundEmailMode.AllowListOnly), StringComparison.OrdinalIgnoreCase) &&
            request.OutboundGuardAllowedRecipients.Count == 0 &&
            request.OutboundGuardAllowedDomains.Count == 0)
            return "Outbound guard mode 'AllowListOnly' requires at least one allowed recipient or domain, or every send will be refused.";

        if (!Uri.TryCreate(request.AppBaseUrl?.Trim(), UriKind.Absolute, out var baseUri) ||
            (baseUri.Scheme != Uri.UriSchemeHttp && baseUri.Scheme != Uri.UriSchemeHttps))
            // This URL is the activation link a founding administrator clicks. A relative or
            // malformed value produces mail that is delivered perfectly and leads nowhere.
            return "The application base URL must be an absolute http(s) URL, e.g. https://app.example.com.";

        return null;
    }

    private static string NormalizeGuardMode(string? value) =>
        Enum.TryParse<OutboundEmailMode>((value ?? string.Empty).Trim(), true, out var parsed)
            ? parsed.ToString()
            : nameof(OutboundEmailMode.Live);

    /// <summary>Null keeps the stored secret, empty clears it, anything else replaces it.</summary>
    private static string? ResolveSecret(string? submitted, string? stored) => submitted switch
    {
        null => stored,
        "" => null,
        _ => submitted
    };

    private static string? Blank(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string? Truncate(string? value, int maximum) =>
        value is null || value.Length <= maximum ? value : value[..maximum];

    private static string? ActorLabel(ClaimsPrincipal? actor) =>
        actor?.FindFirst(ClaimTypes.Email)?.Value
        ?? actor?.FindFirst("email")?.Value
        ?? actor?.Identity?.Name;

    private DateTime UtcNow() => _time.GetUtcNow().UtcDateTime;

    private static PlatformEmailSettingsDto ToDto(PlatformEmailSettings row) => new()
    {
        Provider = row.Provider,
        FromAddress = row.FromAddress,
        FromName = row.FromName,
        ReplyToAddress = row.ReplyToAddress,
        AppBaseUrl = row.AppBaseUrl,
        SmtpHost = row.SmtpHost,
        SmtpPort = row.SmtpPort,
        SmtpUsername = row.SmtpUsername,
        // The whole read-side rule in one line: presence, never the value.
        HasSmtpPassword = !string.IsNullOrEmpty(row.SmtpPassword),
        SmtpEnableSsl = row.SmtpEnableSsl,
        SmtpTimeoutMs = row.SmtpTimeoutMs,
        HasSendGridApiKey = !string.IsNullOrEmpty(row.SendGridApiKey),
        SendGridApiBaseUrl = row.SendGridApiBaseUrl,
        OutboundGuardMode = row.OutboundGuardMode,
        OutboundGuardRedirectTo = row.OutboundGuardRedirectTo,
        OutboundGuardAllowedRecipients = PlatformEmailSettingsStore.SplitList(row.OutboundGuardAllowedRecipients),
        OutboundGuardAllowedDomains = PlatformEmailSettingsStore.SplitList(row.OutboundGuardAllowedDomains),
        OutboundGuardSubjectTag = row.OutboundGuardSubjectTag,
        Origin = nameof(OutboundEmailSettingsOrigin.Platform),
        Version = row.Version,
        UpdatedAtUtc = row.UpdatedAtUtc,
        UpdatedBy = row.UpdatedBy,
        UpdateReason = row.UpdateReason
    };
}
