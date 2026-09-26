using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Notifications.Runtime;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.Mailbox;

/// <summary>
/// The tenant plane's answer to "which mailbox does this tenant send from?" (issue #54).
///
/// <para>The rule is the one <c>SmtpController</c> has applied since the table existed: the
/// tenant's ACTIVE SMTP row with the lowest Id. Two active rows are a configuration the screen
/// already flags as ambiguous; this does not try to be cleverer than the flag.</para>
///
/// <para><b>Refuses a scope mismatch.</b> Under a pushed tenant scope the global query filter
/// would silently return nothing for another unit's id, and "nothing" would become a platform
/// fallback — a cross-tenant bug hidden behind a correctly addressed email. Comparing the
/// context's scoped tenant to the requested one turns that into an exception.</para>
///
/// <para>The From display name is the business unit's name — the company — rather than the
/// mailbox label (<c>ConfigurationName</c>, e.g. "Mail Box 2"), which is what an operator calls
/// the row, not what a customer should see in their inbox.</para>
/// </summary>
public sealed class TenantOutboundSenderSource(ErpRfqAutomationContext context) : ITenantOutboundSenderSource
{
    public Task<TenantOutboundSender?> ResolveAsync(long businessUnitId, CancellationToken ct = default)
        => ResolveAsync(businessUnitId, null, ct);

    public async Task<TenantOutboundSender?> ResolveAsync(long businessUnitId, long? mailboxId, CancellationToken ct = default)
    {
        if (context.ScopedTenantId is { } scoped && scoped != businessUnitId)
            throw new InvalidOperationException(
                $"Refusing to resolve the outbound sender of BU {businessUnitId} through a DbContext scoped to BU {scoped}.");

        var outgoing = context.EmailConfigurations.AsNoTracking()
            .Where(x => x.BusinessUnitId == businessUnitId && x.IsActive && x.Protocol.ToUpper() == "SMTP");
        // The mailbox the user chose, when it is still one of this company's active outgoing
        // mailboxes; otherwise the company's first, as before.
        var row = mailboxId is { } chosen
            ? await outgoing.Where(x => x.Id == chosen).FirstOrDefaultAsync(ct)
            : null;
        row ??= await outgoing.OrderBy(x => x.Id).FirstOrDefaultAsync(ct);
        if (row is null) return null;

        var companyName = await context.BusinessUnits.AsNoTracking()
            .Where(x => x.Id == businessUnitId)
            .Select(x => x.BusinessUnitName)
            .FirstOrDefaultAsync(ct);

        return new TenantOutboundSender(
            businessUnitId,
            row.Id,
            row.ConfigurationName,
            row.EmailAddress,
            string.IsNullOrWhiteSpace(companyName) ? row.ConfigurationName : companyName!,
            row);
    }
}
