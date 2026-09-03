using System;
using System.Threading;
using System.Threading.Tasks;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Notifications.Providers;
using ERP_RFQ_Automation.Security;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ERP_RFQ_Automation.Notifications.Runtime
{
    /// <summary>Whose identity a message leaves under.</summary>
    public enum OutboundSenderOrigin
    {
        /// <summary>No stored platform row either: the <c>Notifications</c> configuration section.</summary>
        Configuration = 0,

        /// <summary>The platform operator's stored configuration (<c>platform.PlatformEmailSettings</c>).</summary>
        Platform = 1,

        /// <summary>The tenant's own active SMTP mailbox (<c>Email_Configurations</c>).</summary>
        Tenant = 2
    }

    /// <summary>
    /// A tenant's active outbound mailbox, as the sender needs it. The <see cref="Configuration"/>
    /// row is carried whole because <see cref="IOutboundSmtpTransport"/> — the ONE transport the
    /// tenant's "Test connection" button and <c>SmtpController</c> already use — takes the entity.
    /// Its <c>Password</c> is plaintext in memory (decrypted by the value converter) and must never
    /// be logged; the record's <see cref="ToString"/> redacts it.
    /// </summary>
    public sealed record TenantOutboundSender(
        long BusinessUnitId,
        long MailboxId,
        string MailboxLabel,
        string FromAddress,
        string FromName,
        EmailConfiguration Configuration)
    {
        public string Host => Configuration.Host;
        public int Port => Configuration.Port;

        public override string ToString() =>
            $"TenantOutboundSender {{ BusinessUnitId = {BusinessUnitId}, MailboxId = {MailboxId}, " +
            $"FromAddress = {FromAddress}, Host = {Host}:{Port} }}";
    }

    /// <summary>
    /// Where the resolver reads a tenant's outbound mailbox from. Same inversion as
    /// <see cref="IOutboundEmailSettingsSource"/>: the Notifications module declares what it
    /// needs and the tenant plane (<c>Mailbox/</c>) supplies it, so a host that registers only
    /// <c>AddNotifications</c> keeps platform-only behaviour with no null checks on the send path.
    ///
    /// <para><b>Must refuse, never guess.</b> An implementation is handed the business unit the
    /// MESSAGE says it belongs to. It must select that unit's row with an explicit predicate, and
    /// if the DbContext it reads through is scoped to a DIFFERENT tenant it must throw rather
    /// than return nothing — silently falling back to the platform sender would hide a
    /// cross-tenant bug behind a correctly addressed email.</para>
    /// </summary>
    public interface ITenantOutboundSenderSource
    {
        Task<TenantOutboundSender?> ResolveAsync(long businessUnitId, CancellationToken ct = default);
    }

    /// <summary>
    /// The answer to "who sends this?", typed so the containment decorator cannot be bypassed:
    /// <see cref="Sender"/> is the concrete <see cref="GuardedEmailSender"/>, for the reason
    /// documented on <see cref="ResolvedOutboundTransport"/>.
    /// </summary>
    public sealed record ResolvedOutboundSender(
        OutboundSenderOrigin Origin,
        GuardedEmailSender Sender,
        string Provider,
        bool TransmitsMail,
        OutboundEmailMode GuardMode,
        string FromAddress,
        string FromName,
        string? Host,
        long? MailboxId,
        string? MailboxLabel,
        OutboundEmailSettingsSnapshot PlatformSettings);

    /// <summary>
    /// ONE authority for the sender of any message, consulted by the send path AND by the
    /// tenant's mailbox screen, so the two cannot disagree.
    /// </summary>
    public interface IOutboundSenderResolver
    {
        /// <summary>
        /// The tenant's active SMTP mailbox when <paramref name="businessUnitId"/> names a tenant
        /// that has one; the platform transport otherwise (system mail, or a tenant with no
        /// outbound row).
        /// </summary>
        Task<ResolvedOutboundSender> ResolveAsync(long? businessUnitId, CancellationToken ct = default);

        /// <summary>
        /// A sender for one specific tenant mailbox, for the tenant's "send test message"
        /// action. Built the same way a live send would be built, guard included.
        /// </summary>
        ResolvedOutboundSender ForMailbox(TenantOutboundSender mailbox, OutboundEmailSettingsSnapshot platformSettings);
    }

    /// <summary>
    /// Resolves the tenant that owns a message BEFORE the platform row (issue #54).
    ///
    /// <para><b>Why there is no per-tenant cache.</b> "Pause outbound email" flips the row's
    /// <c>IsActive</c>; a cached sender would keep sending from a mailbox the tenant just switched
    /// off. One projected read per send is the honest cost, and quote and RFQ sends are rare.
    /// The platform transport keeps its existing cache.</para>
    ///
    /// <para><b>Why the tenant transport is <see cref="IOutboundSmtpTransport"/> and not
    /// <see cref="SmtpEmailSender"/>.</b> The tenant tested their settings through the mailbox
    /// screen, which interprets <c>UseSsl</c> exactly as that transport does (implicit TLS when
    /// set, STARTTLS otherwise). <see cref="SmtpEmailSender"/> derives the TLS mode from the
    /// port instead; a row that tested green as implicit TLS on a non-465 port would fail through
    /// it. One interpretation, the one the tenant already verified.</para>
    /// </summary>
    public sealed class OutboundSenderResolver : IOutboundSenderResolver
    {
        private readonly OutboundEmailTransportResolver _platform;
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly IOutboundSmtpTransport _smtpTransport;
        private readonly ILoggerFactory _loggerFactory;

        public OutboundSenderResolver(
            OutboundEmailTransportResolver platform,
            IServiceScopeFactory scopeFactory,
            IOutboundSmtpTransport smtpTransport,
            ILoggerFactory loggerFactory)
        {
            _platform = platform;
            _scopeFactory = scopeFactory;
            _smtpTransport = smtpTransport;
            _loggerFactory = loggerFactory;
        }

        public async Task<ResolvedOutboundSender> ResolveAsync(long? businessUnitId, CancellationToken ct = default)
        {
            var platform = await _platform.ResolveAsync(ct).ConfigureAwait(false);
            if (businessUnitId is not { } tenant)
                return Platform(platform);

            TenantOutboundSender? mailbox;
            // A scope per read, as the platform source does: the tenant source is scoped over the
            // request-lifetime DbContext, and the read must not enlist in the caller's
            // business transaction.
            using (var scope = _scopeFactory.CreateScope())
            {
                var source = scope.ServiceProvider.GetService<ITenantOutboundSenderSource>();
                mailbox = source is null
                    ? null
                    : await source.ResolveAsync(tenant, ct).ConfigureAwait(false);
            }

            return mailbox is null ? Platform(platform) : ForMailbox(mailbox, platform.Settings);
        }

        public ResolvedOutboundSender ForMailbox(TenantOutboundSender mailbox, OutboundEmailSettingsSnapshot platformSettings)
        {
            ArgumentNullException.ThrowIfNull(mailbox);
            ArgumentNullException.ThrowIfNull(platformSettings);

            // The platform's containment policy (Redirect / AllowListOnly / DraftOnly) applies to
            // a tenant send exactly as to a platform send: a rehearsal that must not reach a real
            // supplier must not reach one from the tenant's mailbox either.
            var options = Options.Create(platformSettings.ToNotificationsOptions());
            var inner = new TenantSmtpEmailSender(mailbox, _smtpTransport, _loggerFactory.CreateLogger<TenantSmtpEmailSender>());
            var guarded = new GuardedEmailSender(inner, options, _loggerFactory.CreateLogger<GuardedEmailSender>());

            return new ResolvedOutboundSender(
                OutboundSenderOrigin.Tenant,
                guarded,
                Provider: "smtp",
                TransmitsMail: true,
                GuardMode: platformSettings.GuardMode,
                FromAddress: mailbox.FromAddress,
                FromName: mailbox.FromName,
                Host: mailbox.Host,
                MailboxId: mailbox.MailboxId,
                MailboxLabel: mailbox.MailboxLabel,
                PlatformSettings: platformSettings);
        }

        private static ResolvedOutboundSender Platform(ResolvedOutboundTransport transport) =>
            new(
                transport.Settings.Origin == OutboundEmailSettingsOrigin.Platform
                    ? OutboundSenderOrigin.Platform
                    : OutboundSenderOrigin.Configuration,
                transport.Sender,
                transport.Settings.NormalizedProvider,
                transport.Settings.TransmitsMail,
                transport.Settings.GuardMode,
                transport.Settings.FromAddress,
                transport.Settings.FromName,
                transport.Settings.NormalizedProvider == "smtp" ? transport.Settings.SmtpHost : null,
                MailboxId: null,
                MailboxLabel: null,
                transport.Settings);
    }
}
