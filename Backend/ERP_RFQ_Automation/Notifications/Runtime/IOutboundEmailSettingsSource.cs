using System.Threading;
using System.Threading.Tasks;

namespace ERP_RFQ_Automation.Notifications.Runtime
{
    /// <summary>
    /// Where the resolver reads persisted outbound settings from.
    ///
    /// <para><b>Why the seam exists.</b> The Notifications module has never had a database
    /// dependency, and the platform-plane storage lives in <c>Platform/Notifications</c> alongside
    /// the entity and the operator endpoints. Inverting it here keeps that direction: Notifications
    /// declares what it needs, the platform plane supplies it, and a host that registers only
    /// <c>AddNotifications</c> (every test that composes the module today) keeps the pure
    /// configuration behaviour with no null checks scattered through the send path.</para>
    ///
    /// <para><b>Two methods, not one, on purpose.</b> The hot path revalidates far more often than
    /// it rebuilds, and <see cref="ReadVersionAsync"/> is a two-column read that the least-privileged
    /// database roles can be granted without ever being granted the credential columns. Collapsing
    /// them into a single full read would have forced a SELECT of the encrypted secrets on every
    /// tenant request that happens to send an email.</para>
    /// </summary>
    public interface IOutboundEmailSettingsSource
    {
        /// <summary>Current version of the stored configuration, or null when no row exists.
        /// MUST project only the identity and version columns.</summary>
        Task<long?> ReadVersionAsync(CancellationToken ct = default);

        /// <summary>The full stored configuration with secrets decrypted, or null when no row
        /// exists. MUST project exactly the transport columns — never the whole entity, whose
        /// operational columns are not granted to the tenant and identity roles.</summary>
        Task<OutboundEmailSettingsSnapshot?> ReadAsync(CancellationToken ct = default);
    }
}
