using ERP_RFQ_Automation.Notifications.Runtime;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.Platform.Notifications;

/// <summary>
/// Reads the stored outbound configuration for the transport resolver.
///
/// <para><b>Every query here is PROJECTED, and that is a runtime correctness requirement rather
/// than a performance preference.</b> Outbound mail is sent from all three planes: the platform
/// plane provisions a tenant and mails an activation link (<c>nexora_pipeline_app</c>), a
/// tenant-scoped request delivers a quote or routes a lead (<c>nexora_tenant_app</c>), and the
/// background workers sweep (<c>nexora_pipeline_app</c> again). The runtime roles hold
/// COLUMN-level grants on this table — the tenant and identity roles can read the transport
/// columns and cannot read the operational ones. Materialising the whole entity would emit a
/// <c>SELECT *</c> that fails with 42501 the first time a tenant request sends an email, while
/// every SQLite test stayed green, because SQLite has neither roles nor privileges.</para>
///
/// <para>Typed as <see cref="DbContext"/> rather than the concrete context: this reads one
/// self-contained platform table and needs nothing else from the model, and the looser type is
/// what lets the tests exercise the real projections against a model built from
/// <see cref="PlatformEmailModelBuilderExtensions.ApplyPlatformEmailModel"/> alone.</para>
/// </summary>
public sealed class PlatformEmailSettingsStore : IOutboundEmailSettingsSource
{
    private readonly DbContext _db;

    public PlatformEmailSettingsStore(DbContext db) => _db = db;

    /// <summary>
    /// True once the context has been spliced with <c>ApplyPlatformEmailModel</c>.
    ///
    /// <para>The migration and the one-line splice into <c>ErpRfqAutomationContext</c> are owned
    /// elsewhere, so there is a window in which this module is registered and its table is not in
    /// the model. Asking EF for an unmapped entity throws, and the resolver would log that as a
    /// settings-read failure every time its cache expired. Reporting "no stored configuration"
    /// instead is both quieter and true: the process really is running on the configuration
    /// fallback.</para>
    /// </summary>
    public bool IsMapped => _db.Model.FindEntityType(typeof(PlatformEmailSettings)) is not null;

    public async Task<long?> ReadVersionAsync(CancellationToken ct = default)
    {
        if (!IsMapped) return null;

        return await _db.Set<PlatformEmailSettings>().AsNoTracking()
            .Where(x => x.Id == PlatformEmailSettings.SingletonId)
            .Select(x => (long?)x.Version)
            .FirstOrDefaultAsync(ct);
    }

    public async Task<OutboundEmailSettingsSnapshot?> ReadAsync(CancellationToken ct = default)
    {
        if (!IsMapped) return null;

        // The exact column list the tenant and identity roles are granted. Adding a column to this
        // projection without adding it to those grants is the 42501 defect described above; adding
        // an operational column here would also hand the tenant plane data it has no business with.
        var row = await _db.Set<PlatformEmailSettings>().AsNoTracking()
            .Where(x => x.Id == PlatformEmailSettings.SingletonId)
            .Select(x => new TransportProjection(
                x.Version,
                x.UpdatedAtUtc,
                x.Provider,
                x.FromAddress,
                x.FromName,
                x.ReplyToAddress,
                x.AppBaseUrl,
                x.SmtpHost,
                x.SmtpPort,
                x.SmtpUsername,
                x.SmtpPassword,
                x.SmtpEnableSsl,
                x.SmtpTimeoutMs,
                x.SendGridApiKey,
                x.SendGridApiBaseUrl,
                x.OutboundGuardMode,
                x.OutboundGuardRedirectTo,
                x.OutboundGuardAllowedRecipients,
                x.OutboundGuardAllowedDomains,
                x.OutboundGuardSubjectTag))
            .FirstOrDefaultAsync(ct);

        if (row is null) return null;

        return new OutboundEmailSettingsSnapshot
        {
            Origin = OutboundEmailSettingsOrigin.Platform,
            Version = row.Version,
            UpdatedAtUtc = new DateTimeOffset(DateTime.SpecifyKind(row.UpdatedAtUtc, DateTimeKind.Utc)),
            Provider = row.Provider,
            FromAddress = row.FromAddress,
            FromName = row.FromName,
            ReplyToAddress = row.ReplyToAddress,
            AppBaseUrl = row.AppBaseUrl,
            SmtpHost = row.SmtpHost,
            SmtpPort = row.SmtpPort,
            SmtpUsername = row.SmtpUsername,
            SmtpPassword = row.SmtpPassword,
            SmtpEnableSsl = row.SmtpEnableSsl,
            SmtpTimeoutMs = row.SmtpTimeoutMs,
            SendGridApiKey = row.SendGridApiKey,
            SendGridApiBaseUrl = row.SendGridApiBaseUrl,
            OutboundGuardMode = row.OutboundGuardMode,
            OutboundGuardRedirectTo = row.OutboundGuardRedirectTo,
            OutboundGuardAllowedRecipients = SplitList(row.OutboundGuardAllowedRecipients),
            OutboundGuardAllowedDomains = SplitList(row.OutboundGuardAllowedDomains),
            OutboundGuardSubjectTag = row.OutboundGuardSubjectTag
        };
    }

    /// <summary>Accepts newline, comma or semicolon as the separator. An operator pasting a list out
    /// of a ticket should not have their allow-list silently become one long invalid address.</summary>
    public static IReadOnlyList<string> SplitList(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? Array.Empty<string>()
            : value.Split(new[] { '\n', '\r', ',', ';' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(entry => entry.Trim())
                .Where(entry => entry.Length > 0)
                .ToList();

    public static string? JoinList(IEnumerable<string>? values)
    {
        var cleaned = (values ?? Enumerable.Empty<string>())
            .Where(entry => !string.IsNullOrWhiteSpace(entry))
            .Select(entry => entry.Trim())
            .ToList();
        return cleaned.Count == 0 ? null : string.Join('\n', cleaned);
    }

    /// <summary>Named record rather than an anonymous type so the projected column list is a
    /// reviewable declaration that sits next to the grant it has to match.</summary>
    private sealed record TransportProjection(
        long Version,
        DateTime UpdatedAtUtc,
        string Provider,
        string FromAddress,
        string FromName,
        string? ReplyToAddress,
        string AppBaseUrl,
        string? SmtpHost,
        int SmtpPort,
        string? SmtpUsername,
        string? SmtpPassword,
        bool SmtpEnableSsl,
        int SmtpTimeoutMs,
        string? SendGridApiKey,
        string? SendGridApiBaseUrl,
        string OutboundGuardMode,
        string? OutboundGuardRedirectTo,
        string? OutboundGuardAllowedRecipients,
        string? OutboundGuardAllowedDomains,
        string? OutboundGuardSubjectTag);
}
