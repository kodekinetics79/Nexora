using System.Text.Json;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Platform.Models;
using ERP_RFQ_Automation.Platform.Onboarding;
using ERP_RFQ_Automation.Platform.Provisioning;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.Platform.Lifecycle;

/// <summary>What one erasure target reported: which record class, and how many identities went.</summary>
public sealed record TenantErasureTarget(string Target, long IdentitiesErased, string Description);

/// <summary>
/// Replaces the identities of the natural persons this platform holds FOR a tenant, and leaves
/// every commercial record standing.
///
/// <para><b>This is not the deletion operation, and the difference is legal, not stylistic.</b>
/// A customer can be entitled to erasure under GDPR Article 17 while the same customer's invoices,
/// purchase orders and delivery documents must be retained for years under tax and commercial law
/// — Article 17(3)(b) is the carve-out that makes both true at once. Modelling erasure as a step
/// on the way to deletion would force one obligation to be broken to honour the other. So the two
/// operations are independent: a tenant can be erased and continue to be invoiced, and a tenant
/// can be purged having never been erased. <see cref="TenantPurgeExecutor"/> is the other one.</para>
///
/// <para><b>Pseudonymisation, not deletion of rows.</b> A user row is the target of foreign keys
/// from leads, quotes, approvals and audit entries; deleting it would either fail or cascade a
/// hole through the commercial history the erasure is explicitly required to preserve. Every
/// identifying value is therefore overwritten in place: the row survives as a referent, and there
/// is no longer a person behind it. The replacement address is per-user and inside the
/// RFC 2606 reserved <c>.invalid</c> domain, so it satisfies the global unique index on
/// <c>Users.Email</c> and can never resolve, be delivered to, or be signed in as.</para>
///
/// <para><b>What this deliberately does NOT touch, and why.</b>
/// <list type="bullet">
/// <item>The tenant's COUNTERPARTIES — customers, suppliers and their named contacts. Those are
/// the tenant's commercial records and the tenant is their controller, not this platform. Erasing
/// them would destroy the very records the retention obligation applies to, on the authority of
/// the wrong data subject.</item>
/// <item><c>public."IamAuditEvents"</c> and the platform audit trail. Both are append-only at
/// database level and both are the evidence that access was or was not abused; an erasure that
/// can rewrite the security log is a mechanism for hiding a breach.</item>
/// <item><c>Tenant.AccountOwnerEmail</c>. That is the OPERATOR's account manager, not the
/// customer's person — erasing it removes the operator's own escalation path and erases nobody's
/// personal data but their own staff's.</item>
/// </list></para>
/// </summary>
public sealed class TenantPersonalDataEraser(
    ErpRfqAutomationContext context, ILogger<TenantPersonalDataEraser> logger)
{
    /// <summary>
    /// RFC 2606 reserves <c>.invalid</c> as a top-level domain guaranteed never to resolve, which
    /// is the property that matters: a pseudonymised address must not become deliverable if the
    /// domain is ever registered.
    /// </summary>
    internal const string ErasedEmailDomain = "erased.invalid";

    internal const string ErasedName = "Erased";

    public async Task<IReadOnlyList<TenantErasureTarget>> EraseAsync(
        Tenant tenant, long? businessUnitId, CancellationToken cancellationToken)
    {
        var targets = new List<TenantErasureTarget>();

        if (businessUnitId is long unit)
            targets.Add(await EraseUsersAsync(unit, cancellationToken));

        targets.Add(await EraseInvitationsAsync(tenant.Id, cancellationToken));
        targets.AddRange(await EraseProvisioningRecordsAsync(tenant.Id, cancellationToken));
        targets.Add(EraseTenantContacts(tenant));

        await context.SaveChangesAsync(cancellationToken);

        logger.LogWarning(
            "TENANT PERSONAL DATA ERASURE complete for tenant {TenantId}: {Identities} identity/identities "
            + "replaced across {Targets} target(s). Commercial records were not affected.",
            tenant.Id, targets.Sum(t => t.IdentitiesErased), targets.Count);

        return targets;
    }

    /// <summary>
    /// The tenant's own people. Each user is overwritten individually because the replacement
    /// address has to be unique — <c>Users.Email</c> is globally unique, one address to one
    /// account, so a single set-them-all-to-the-same-value update would violate the index on the
    /// second row and abort the erasure.
    ///
    /// <para>Ids are projected rather than entities materialised. Materialising a whole
    /// <see cref="User"/> selects every column, which is a 42501 the moment a role holds only
    /// column-level SELECT on that table — a failure mode SQLite cannot reproduce and PostgreSQL
    /// only shows at runtime.</para>
    /// </summary>
    private async Task<TenantErasureTarget> EraseUsersAsync(
        long businessUnitId, CancellationToken cancellationToken)
    {
        var userIds = await context.Users.IgnoreQueryFilters().AsNoTracking()
            .Where(u => u.Buid == businessUnitId)
            .Select(u => u.Id)
            .ToListAsync(cancellationToken);

        var erasedOn = DateTime.UtcNow;
        foreach (var id in userIds)
        {
            // A real BCrypt hash of a discarded random value: unusable by construction, and it
            // cannot be brute-forced into something that signs in because no plaintext for it
            // exists anywhere. A malformed placeholder would be worse than useless — BCrypt.Verify
            // throws on one, turning every subsequent login attempt into a 500.
            var unusable = BCrypt.Net.BCrypt.HashPassword(Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N"));

            await context.Users.IgnoreQueryFilters()
                .Where(u => u.Id == id)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(u => u.FirstName, ErasedName)
                    .SetProperty(u => u.MiddleName, (string?)null)
                    .SetProperty(u => u.LastName, ErasedName)
                    .SetProperty(u => u.Email, $"erased-{id}@{ErasedEmailDomain}")
                    .SetProperty(u => u.PasswordHash, unusable)
                    .SetProperty(u => u.ImageUrl, string.Empty)
                    .SetProperty(u => u.Timezone, (string?)null)
                    .SetProperty(u => u.Region, (string?)null)
                    // Deactivated as part of the erasure, not as a side effect: an account whose
                    // credential no longer exists must not keep occupying a billable seat, and the
                    // seats meter reads exactly this flag.
                    .SetProperty(u => u.IsActive, false)
                    .SetProperty(u => u.DeactivatedAtUtc, (DateTime?)erasedOn)
                    .SetProperty(u => u.ModifiedBy, ErasedName)
                    .SetProperty(u => u.ModifiedOn, (DateTime?)erasedOn),
                    cancellationToken);
        }

        return new TenantErasureTarget(
            nameof(User), userIds.Count,
            "Names, email addresses, avatars, time zones and regions replaced; credentials made "
            + "unusable and the accounts deactivated. The rows survive because leads, quotes and "
            + "approvals point at them.");
    }

    /// <summary>
    /// Activation invitations carry the founding administrator's address and, once redeemed, the
    /// IP they redeemed from — which is personal data in its own right under GDPR Recital 30.
    /// The token hash is left alone: it identifies nobody, and it is what makes a spent invitation
    /// unusable.
    /// </summary>
    private async Task<TenantErasureTarget> EraseInvitationsAsync(
        long tenantId, CancellationToken cancellationToken)
    {
        var invitationIds = await context.TenantAdminInvitations.AsNoTracking()
            .Where(i => i.TenantId == tenantId)
            .Select(i => i.Id)
            .ToListAsync(cancellationToken);

        foreach (var id in invitationIds)
            await context.TenantAdminInvitations
                .Where(i => i.Id == id)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(i => i.Email, $"erased-{id}@{ErasedEmailDomain}")
                    .SetProperty(i => i.RedeemedFromIp, (string?)null),
                    cancellationToken);

        return new TenantErasureTarget(
            nameof(TenantAdminInvitation), invitationIds.Count,
            "Recipient addresses and redemption IPs replaced. Issue, redemption and revocation "
            + "timestamps are retained: they evidence how tenant access was granted.");
    }

    /// <summary>
    /// The provisioning trail: the request that created the tenant, and any draft that produced it.
    ///
    /// <para>Both hold the founding administrator's email address, and both survived an erasure
    /// until a red-team audit found them. <c>ProvisioningExecutions</c> carries it in a column AND
    /// inside <c>RequestPayload</c>; <c>ProvisioningDrafts</c> carries it only inside
    /// <c>Payload</c>, and reaches the tenant solely through <c>SubmittedExecutionId</c> — no
    /// tenant column, so nothing that sweeps for one was ever going to find it.</para>
    ///
    /// <para><b>The payload is replaced wholesale rather than edited key by key.</b> It is
    /// free-form jsonb holding whatever the submitting client sent, so a list of personal keys to
    /// strip is a list that silently stops covering the next field somebody adds — the same defect
    /// as a hand-maintained delete list, with a worse failure mode, because an erasure that misses
    /// a key reports success. Nothing of value is lost: the company's legal identity is retained
    /// where it belongs, on <c>platform."Tenants"</c>, and the operator's account of the attempt
    /// survives in <c>PlatformAuditLogs</c>. What goes is a snapshot of a form.</para>
    ///
    /// <para>A draft that was never submitted is untouched. It has no tenant, and it is the
    /// drafting operator's own work in progress rather than any customer's record.</para>
    /// </summary>
    private async Task<IReadOnlyList<TenantErasureTarget>> EraseProvisioningRecordsAsync(
        long tenantId, CancellationToken cancellationToken)
    {
        var tombstone = JsonSerializer.Serialize(new
        {
            redacted = "Personal data erased under a tenant erasure request.",
            erasedOnUtc = DateTime.UtcNow
        });

        var executionIds = await context.Set<ProvisioningExecution>().AsNoTracking()
            .Where(e => e.TenantId == tenantId)
            .Select(e => e.Id)
            .ToListAsync(cancellationToken);

        foreach (var id in executionIds)
        {
            // Same construction as the user credential above: a real BCrypt hash of a discarded
            // random value, so nothing can be recovered from it and nothing downstream chokes on
            // a malformed one.
            var unusable = BCrypt.Net.BCrypt.HashPassword(
                Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N"));

            await context.Set<ProvisioningExecution>()
                .Where(e => e.Id == id)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(e => e.AdminEmail, $"erased-{id}@{ErasedEmailDomain}")
                    .SetProperty(e => e.AdminPasswordHash, unusable)
                    .SetProperty(e => e.RequestPayload, tombstone)
                    .SetProperty(e => e.ResultPayload, (string?)null),
                    cancellationToken);
        }

        var draftIds = executionIds.Count == 0
            ? []
            : await context.Set<ProvisioningDraft>().AsNoTracking()
                .Where(d => d.SubmittedExecutionId != null
                            && executionIds.Contains(d.SubmittedExecutionId.Value))
                .Select(d => d.Id)
                .ToListAsync(cancellationToken);

        foreach (var id in draftIds)
            await context.Set<ProvisioningDraft>()
                .Where(d => d.Id == id)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(d => d.Payload, tombstone),
                    cancellationToken);

        return
        [
            new TenantErasureTarget(nameof(ProvisioningExecution), executionIds.Count,
                "The founding administrator's address and the submitted request snapshot replaced; "
                + "the provisioning attempt's own timeline and outcome are retained in the platform "
                + "audit trail."),
            new TenantErasureTarget(nameof(ProvisioningDraft), draftIds.Count,
                "Submitted drafts' payloads replaced. A draft that was never submitted belongs to "
                + "the operator drafting it, not to this tenant, and is untouched.")
        ];
    }

    /// <summary>
    /// The named humans on the tenant record itself. <c>ContactEmail</c> is documented as a
    /// general company address, but in practice it is a person's mailbox often enough that
    /// leaving it would make the erasure a half-truth.
    /// </summary>
    private TenantErasureTarget EraseTenantContacts(Tenant tenant)
    {
        var erased = 0;
        if (tenant.ContactEmail is not null) { tenant.ContactEmail = null; erased++; }
        if (tenant.Phone is not null) { tenant.Phone = null; erased++; }
        if (tenant.BillingContactName is not null) { tenant.BillingContactName = null; erased++; }
        if (tenant.BillingContactEmail is not null) { tenant.BillingContactEmail = null; erased++; }

        return new TenantErasureTarget(
            nameof(Tenant), erased,
            "Company contact email, phone and billing contact cleared. Legal name, registration "
            + "and tax numbers are NOT personal data and are retained — they are what makes the "
            + "statutory records that survive attributable to a legal entity.");
    }
}
