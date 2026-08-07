using System;
using System.Collections.Generic;
using System.Linq;

namespace ERP_RFQ_Automation.Notifications.Runtime
{
    /// <summary>Where the effective outbound settings came from. Shown in the status readout so an
    /// operator can tell "nobody has configured this yet, you are seeing appsettings" apart from
    /// "somebody configured it and chose console".</summary>
    public enum OutboundEmailSettingsOrigin
    {
        /// <summary>No stored row: the <c>Notifications</c> configuration section is in force,
        /// exactly as it was before the platform settings existed.</summary>
        Configuration = 0,

        /// <summary>A platform operator saved a configuration and it is what is being used.</summary>
        Platform = 1,

        /// <summary>Candidate settings supplied to a test send and never persisted.</summary>
        Candidate = 2
    }

    /// <summary>
    /// The complete outbound-email configuration in force, decoupled from where it was read.
    ///
    /// <para>Deliberately a plain immutable value rather than the EF entity: it crosses from the
    /// platform plane (which owns persistence) into the Notifications module (which must keep
    /// working with no database at all, as it did before), and it is the unit the transport cache
    /// is keyed on. <see cref="Version"/> is the cache key — the resolver revalidates by comparing
    /// it, never by re-reading credentials.</para>
    ///
    /// <para>Secrets are present here in CLEARTEXT, because this is the value a transport is built
    /// from. It never leaves the process: no read model, DTO, log line or audit record is built
    /// from this type, and the read model is a separate shape that carries booleans instead.</para>
    /// </summary>
    public sealed record OutboundEmailSettingsSnapshot
    {
        public OutboundEmailSettingsOrigin Origin { get; init; } = OutboundEmailSettingsOrigin.Configuration;

        /// <summary>Monotonic version of the stored row. 0 for configuration-derived settings, which
        /// therefore never satisfy a revalidation check and are re-read the moment a row appears.</summary>
        public long Version { get; init; }

        public DateTimeOffset? UpdatedAtUtc { get; init; }

        public string Provider { get; init; } = "console";
        public string FromAddress { get; init; } = string.Empty;
        public string FromName { get; init; } = string.Empty;
        public string? ReplyToAddress { get; init; }
        public string AppBaseUrl { get; init; } = string.Empty;

        public string? SmtpHost { get; init; }
        public int SmtpPort { get; init; } = 587;
        public string? SmtpUsername { get; init; }
        public string? SmtpPassword { get; init; }
        public bool SmtpEnableSsl { get; init; } = true;
        public int SmtpTimeoutMs { get; init; } = 30000;

        public string? SendGridApiKey { get; init; }
        public string? SendGridApiBaseUrl { get; init; }

        public string OutboundGuardMode { get; init; } = nameof(OutboundEmailMode.Live);
        public string? OutboundGuardRedirectTo { get; init; }
        public IReadOnlyList<string> OutboundGuardAllowedRecipients { get; init; } = Array.Empty<string>();
        public IReadOnlyList<string> OutboundGuardAllowedDomains { get; init; } = Array.Empty<string>();
        public string? OutboundGuardSubjectTag { get; init; }

        /// <summary>Normalised provider key: "smtp", "sendgrid", or "console" for anything else.
        /// Unknown values fall to console for the reason the original switch did — an unrecognised
        /// provider must log, never guess at a transport.</summary>
        public string NormalizedProvider
        {
            get
            {
                var value = (Provider ?? string.Empty).Trim().ToLowerInvariant();
                return value is "smtp" or "sendgrid" ? value : "console";
            }
        }

        /// <summary>True when the active transport puts bytes on the wire. The single most
        /// important fact in the status readout: a console provider is not a degraded send, it is
        /// no send at all, and everything upstream still reports success.</summary>
        public bool TransmitsMail => NormalizedProvider is "smtp" or "sendgrid";

        /// <summary>True when the selected provider has the credentials it needs. Distinguishes
        /// "configured and broken" from "never configured", which are different operator tasks.</summary>
        public bool HasProviderCredentials => NormalizedProvider switch
        {
            "smtp" => !string.IsNullOrWhiteSpace(SmtpHost),
            "sendgrid" => !string.IsNullOrWhiteSpace(SendGridApiKey),
            _ => true
        };

        public OutboundEmailMode GuardMode =>
            Enum.TryParse<OutboundEmailMode>((OutboundGuardMode ?? string.Empty).Trim(), true, out var parsed)
                ? parsed
                : OutboundEmailMode.Live;

        /// <summary>
        /// Projects onto the options shape the three providers and the guard already consume, so
        /// no provider needed to learn about persisted settings. This is the whole adaptation
        /// layer: everything below <see cref="IEmailSender"/> is unchanged code.
        /// </summary>
        public NotificationsOptions ToNotificationsOptions() => new()
        {
            Provider = NormalizedProvider,
            FromAddress = FromAddress,
            FromName = FromName,
            ReplyToAddress = ReplyToAddress,
            AppBaseUrl = AppBaseUrl,
            Smtp = new SmtpOptions
            {
                Host = SmtpHost ?? string.Empty,
                Port = SmtpPort > 0 ? SmtpPort : 587,
                Username = SmtpUsername,
                Password = SmtpPassword,
                EnableSsl = SmtpEnableSsl,
                TimeoutMs = SmtpTimeoutMs > 0 ? SmtpTimeoutMs : 30000
            },
            SendGrid = new SendGridOptions
            {
                ApiKey = SendGridApiKey ?? string.Empty,
                ApiBaseUrl = string.IsNullOrWhiteSpace(SendGridApiBaseUrl)
                    ? "https://api.sendgrid.com"
                    : SendGridApiBaseUrl!
            },
            OutboundGuard = new OutboundEmailGuardOptions
            {
                Mode = OutboundGuardMode ?? nameof(OutboundEmailMode.Live),
                RedirectTo = OutboundGuardRedirectTo,
                SubjectTag = string.IsNullOrWhiteSpace(OutboundGuardSubjectTag)
                    ? "[NEXORA TEST]"
                    : OutboundGuardSubjectTag!,
                AllowedRecipients = OutboundGuardAllowedRecipients.ToList(),
                AllowedDomains = OutboundGuardAllowedDomains.ToList()
            }
        };

        /// <summary>
        /// The fallback: settings as they were before any of this existed. Keeping the
        /// configuration path intact is not politeness to old deployments, it is the reason a
        /// database outage cannot take outbound mail with it — the resolver falls back here.
        /// </summary>
        public static OutboundEmailSettingsSnapshot FromOptions(NotificationsOptions options)
        {
            ArgumentNullException.ThrowIfNull(options);
            return new OutboundEmailSettingsSnapshot
            {
                Origin = OutboundEmailSettingsOrigin.Configuration,
                Version = 0,
                Provider = options.Provider ?? "console",
                FromAddress = options.FromAddress ?? string.Empty,
                FromName = options.FromName ?? string.Empty,
                ReplyToAddress = options.ReplyToAddress,
                AppBaseUrl = options.AppBaseUrl ?? string.Empty,
                SmtpHost = options.Smtp?.Host,
                SmtpPort = options.Smtp?.Port ?? 587,
                SmtpUsername = options.Smtp?.Username,
                SmtpPassword = options.Smtp?.Password,
                SmtpEnableSsl = options.Smtp?.EnableSsl ?? true,
                SmtpTimeoutMs = options.Smtp?.TimeoutMs ?? 30000,
                SendGridApiKey = options.SendGrid?.ApiKey,
                SendGridApiBaseUrl = options.SendGrid?.ApiBaseUrl,
                OutboundGuardMode = options.OutboundGuard?.Mode ?? nameof(OutboundEmailMode.Live),
                OutboundGuardRedirectTo = options.OutboundGuard?.RedirectTo,
                OutboundGuardSubjectTag = options.OutboundGuard?.SubjectTag,
                OutboundGuardAllowedRecipients =
                    options.OutboundGuard?.AllowedRecipients?.ToList() ?? new List<string>(),
                OutboundGuardAllowedDomains =
                    options.OutboundGuard?.AllowedDomains?.ToList() ?? new List<string>()
            };
        }
    }
}
