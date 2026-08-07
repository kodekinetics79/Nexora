using Microsoft.Extensions.Options;

namespace ERP_RFQ_Automation.Notifications.Runtime
{
    /// <summary>
    /// Makes <c>IOptions&lt;NotificationsOptions&gt;</c> report the settings that are actually in
    /// force, rather than the ones bound from appsettings at startup.
    ///
    /// <para><b>Why this is necessary and not merely tidy.</b> The transport is only half of what
    /// a stored configuration decides. <c>AppBaseUrl</c> is the other half: it is what
    /// <c>TenantAdminInvitationService.BuildActivationUrl</c> puts in the activation link, and an
    /// email that is delivered perfectly to a link pointing at the wrong host is the same dead end
    /// as an email that was never sent. The same applies to the default From address and Reply-To
    /// used when composing messages. Those consumers read <c>IOptions</c>, so <c>IOptions</c> is
    /// where the stored values have to arrive — otherwise "configurable without a redeploy" would
    /// have been true of the provider and quietly false of the link.</para>
    ///
    /// <para><b>Never blocks.</b> <see cref="Value"/> is a synchronous property read from
    /// constructors on the request path, so it reads the resolver's cached snapshot and does no
    /// I/O. Before the first load it reports the configuration values — which is exactly what the
    /// process is using at that moment, so the answer is true rather than merely available. The
    /// warm-up hosted service performs that first load during startup, ahead of traffic.</para>
    /// </summary>
    public sealed class EffectiveNotificationsOptions : IOptions<NotificationsOptions>
    {
        private readonly OutboundEmailTransportResolver _resolver;

        public EffectiveNotificationsOptions(OutboundEmailTransportResolver resolver) => _resolver = resolver;

        public NotificationsOptions Value => _resolver.Current.ToNotificationsOptions();
    }
}
