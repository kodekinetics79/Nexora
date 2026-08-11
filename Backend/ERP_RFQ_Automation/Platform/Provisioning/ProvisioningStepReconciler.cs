using System.Text.Json;
using ERP_RFQ_Automation.CommercialCases.Lifecycle;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Platform.Models;
using ERP_RFQ_Automation.Platform.Onboarding;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.Platform.Provisioning;

/// <summary>
/// A step whose recorded verdict is wrong: it says Failed or Running, and its side effect is on
/// the ground.
/// </summary>
/// <param name="Reason">One sentence, for the operator and the audit record.</param>
/// <param name="Detail">
/// The JSON written into <c>ProvisioningSteps.Detail</c>, always carrying
/// <c>reconciled: true</c> so a green tick that came from a probe is never mistaken for one that
/// came from doing the work.
/// </param>
public sealed record ProvisioningStepReconciliation(string StepCode, string Reason, string Detail);

/// <summary>
/// Answers one question, per step, before a resume re-runs it: <b>did this already happen?</b>
///
/// <para><b>The window this closes.</b> A step's success verdict is written INSIDE the step's own
/// transaction, so a committed step is a committed fact — but the commit can succeed and its
/// acknowledgement never arrive. The connection drops, the pod is evicted, the pooler recycles the
/// backend; the runner sees an exception, and the journal (written on a different connection,
/// which is exactly why it survives) records a failure for work that is sitting in the database.
/// The same shape arrives from the other direction when a process dies mid-step and leaves the
/// step marked Running forever. In both cases the honest state of the world is "done", and the
/// naive repair — retry it — is the one action that can do damage.</para>
///
/// <para><b>Probe, then skip. Never retry blind.</b> Each probe below looks for the SPECIFIC
/// artefact the step creates, identified by an id the step recorded inside its own transaction
/// wherever one exists, and refuses to reconcile on anything weaker. Where the artefact has only a
/// natural key, the probe additionally checks it belongs to THIS execution's business unit: a
/// SUPER_ADMIN role on somebody else's tenant is not evidence that ours was created.</para>
///
/// <para><b>Two steps deliberately have no probe.</b> <c>lifecycle-statuses</c> and
/// <c>baseline-seed</c> are check-then-insert over natural keys from end to end — every row they
/// write is guarded by an existence test inside the step itself — so re-running them is not a
/// repeat of a side effect, it is a no-op that reports how little it had to do. Adding a probe
/// there would duplicate the step's own logic and give the system two places to be wrong about the
/// same question.</para>
/// </summary>
public interface IProvisioningStepReconciler
{
    /// <summary>
    /// Null means "no evidence this step completed — run it". A value means the side effect exists
    /// and the step must be recorded as done WITHOUT being executed.
    /// </summary>
    Task<ProvisioningStepReconciliation?> ProbeAsync(
        ErpRfqAutomationContext db,
        ProvisioningExecution execution,
        string stepCode,
        CancellationToken ct);
}

public sealed class ProvisioningStepReconciler : IProvisioningStepReconciler
{
    public Task<ProvisioningStepReconciliation?> ProbeAsync(
        ErpRfqAutomationContext db,
        ProvisioningExecution execution,
        string stepCode,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(execution);

        return stepCode switch
        {
            ProvisioningStepCodes.Tenant => ProbeTenantAsync(db, execution, ct),
            ProvisioningStepCodes.BusinessUnit => ProbeBusinessUnitAsync(db, execution, ct),
            ProvisioningStepCodes.AiPolicy => ProbeAiPolicyAsync(db, execution, ct),
            ProvisioningStepCodes.FoundingRole => ProbeFoundingRoleAsync(db, execution, ct),
            ProvisioningStepCodes.FoundingAdmin => ProbeFoundingAdminAsync(db, execution, ct),
            ProvisioningStepCodes.Invitation => ProbeInvitationAsync(db, execution, ct),

            // Idempotent by construction; see the class remarks. Re-running IS the reconciliation.
            ProvisioningStepCodes.LifecycleStatuses => None,
            ProvisioningStepCodes.BaselineSeed => None,
            _ => None
        };
    }

    private static readonly Task<ProvisioningStepReconciliation?> None
        = Task.FromResult<ProvisioningStepReconciliation?>(null);

    // ---- 1. the tenant record ---------------------------------------------------------------

    private static async Task<ProvisioningStepReconciliation?> ProbeTenantAsync(
        ErpRfqAutomationContext db, ProvisioningExecution execution, CancellationToken ct)
    {
        // The recorded id, and ONLY the recorded id. A slug lookup cannot tell our tenant from one
        // an operator created by hand between attempts, and adopting a stranger's tenant is a far
        // worse outcome than a step that runs again and fails honestly on the unique index.
        if (execution.TenantId is not long tenantId)
            return null;

        var tenant = await db.Set<Tenant>().IgnoreQueryFilters().AsNoTracking()
            .Where(t => t.Id == tenantId)
            .Select(t => new { t.Id, t.Slug })
            .FirstOrDefaultAsync(ct);
        if (tenant is null || !string.Equals(tenant.Slug, execution.Slug, StringComparison.Ordinal))
            return null;

        return Reconciled(ProvisioningStepCodes.Tenant,
            $"Tenant {tenantId} exists and carries this execution's workspace address, so the "
            + "tenant step committed even though its verdict says otherwise.",
            new { tenantId, slug = tenant.Slug });
    }

    // ---- 2. the primary business unit --------------------------------------------------------

    private static async Task<ProvisioningStepReconciliation?> ProbeBusinessUnitAsync(
        ErpRfqAutomationContext db, ProvisioningExecution execution, CancellationToken ct)
    {
        if (execution.ProvisionedBusinessUnitId is not long businessUnitId)
            return null;

        var unit = await db.Set<BusinessUnit>().AsNoTracking()
            .Where(b => b.Id == businessUnitId)
            .Select(b => new { b.Id, b.BusinessUnitCode })
            .FirstOrDefaultAsync(ct);
        if (unit is null)
            return null;

        return Reconciled(ProvisioningStepCodes.BusinessUnit,
            $"Business unit {businessUnitId} ({unit.BusinessUnitCode}) exists, so the business-unit "
            + "step committed. Re-running it would create a SECOND data boundary for one tenant.",
            new { businessUnitId, unit.BusinessUnitCode });
    }

    // ---- 4. the AI processing policy ---------------------------------------------------------

    private static async Task<ProvisioningStepReconciliation?> ProbeAiPolicyAsync(
        ErpRfqAutomationContext db, ProvisioningExecution execution, CancellationToken ct)
    {
        if (execution.ProvisionedBusinessUnitId is not long businessUnitId)
            return null;

        var exists = await db.AiProcessingPolicies.IgnoreQueryFilters().AsNoTracking()
            .AnyAsync(p => p.BusinessUnitId == businessUnitId, ct);

        return exists
            ? Reconciled(ProvisioningStepCodes.AiPolicy,
                $"Business unit {businessUnitId} already has an AI processing policy — on PostgreSQL "
                + "the business_units_create_ai_policy trigger writes it — so there is nothing to add.",
                new { businessUnitId })
            : null;
    }

    // ---- 5. the founding role -----------------------------------------------------------------

    private static async Task<ProvisioningStepReconciliation?> ProbeFoundingRoleAsync(
        ErpRfqAutomationContext db, ProvisioningExecution execution, CancellationToken ct)
    {
        if (execution.ProvisionedBusinessUnitId is not long businessUnitId)
            return null;

        // Scoped to THIS execution's business unit. Every tenant has a SUPER_ADMIN, so an
        // unscoped existence check would reconcile every execution in the system on the strength
        // of somebody else's role.
        var role = await db.SetupMasters.IgnoreQueryFilters().AsNoTracking()
            .Where(s => s.BusinessUnitId == businessUnitId
                        && s.SetupType == "Role" && s.SetupCode == "SUPER_ADMIN")
            .Select(s => new { s.SetupId, s.SetupValue })
            .FirstOrDefaultAsync(ct);
        if (role is null)
            return null;

        // Re-recorded, because the failed attempt's transaction rolled the id back off the
        // execution even where the role itself survived on another attempt's commit.
        execution.FoundingRoleId = role.SetupId;

        return Reconciled(ProvisioningStepCodes.FoundingRole,
            $"The SUPER_ADMIN role for business unit {businessUnitId} already exists.",
            new { roleId = role.SetupId, role.SetupValue });
    }

    // ---- 6. the founding administrator --------------------------------------------------------

    private static async Task<ProvisioningStepReconciliation?> ProbeFoundingAdminAsync(
        ErpRfqAutomationContext db, ProvisioningExecution execution, CancellationToken ct)
    {
        if (execution.ProvisionedBusinessUnitId is not long businessUnitId)
            return null;

        var email = execution.AdminEmail;
        var existing = await db.Users.IgnoreQueryFilters().AsNoTracking()
            .Where(u => u.Email == email)
            .Select(u => new { u.Id, u.Buid, u.IsActive })
            .FirstOrDefaultAsync(ct);

        // No row: nothing committed. A row on ANOTHER tenant is the email-taken case, which is a
        // terminal failure the step itself must raise — reconciling it would silently adopt a
        // stranger's account as this tenant's founding administrator.
        if (existing is null || existing.Buid != businessUnitId)
            return null;

        execution.FoundingUserId = existing.Id;

        return Reconciled(ProvisioningStepCodes.FoundingAdmin,
            $"User {existing.Id} already holds this tenant's administrator address, so the "
            + "founding-admin step committed. Re-running it cannot create a second account — "
            + "Users.Email is globally unique — but it would fail the resume for no reason.",
            new { userId = existing.Id, existing.IsActive });
    }

    // ---- 8. the activation invitation ---------------------------------------------------------

    private static async Task<ProvisioningStepReconciliation?> ProbeInvitationAsync(
        ErpRfqAutomationContext db, ProvisioningExecution execution, CancellationToken ct)
    {
        if (execution.FoundingUserId is not long userId)
            return null;

        // THE probe that matters most, and the only one whose absence would be actively dangerous
        // rather than merely wasteful.
        //
        // The invitation step is a supersede-then-issue, which is exactly right when nobody has
        // used the old link. It is exactly wrong once somebody has: a REDEEMED invitation means
        // the founding administrator has already chosen their password and is using the account.
        // Re-running the step would mint a fresh, live, single-use link that sets that password
        // again, and mail it — a second key to a door the customer has already walked through, and
        // the supersede clause cannot revoke it because redeemed invitations are outside its
        // filter by design.
        var redeemed = await db.Set<TenantAdminInvitation>().AsNoTracking()
            .Where(i => i.UserId == userId && i.RedeemedAtUtc != null)
            .Select(i => new { i.Id, i.RedeemedAtUtc })
            .FirstOrDefaultAsync(ct);
        if (redeemed is not null)
        {
            execution.InvitationId ??= redeemed.Id;
            return Reconciled(ProvisioningStepCodes.Invitation,
                $"Invitation {redeemed.Id} was redeemed at {redeemed.RedeemedAtUtc:O}: the founding "
                + "administrator has already activated this account. Issuing another link would hand "
                + "out a second credential for an account that is already in use.",
                new { invitationId = redeemed.Id, redeemedAtUtc = redeemed.RedeemedAtUtc });
        }

        // Not redeemed: a link this execution issued may still be live and already in the
        // customer's inbox. Reissuing revokes it, so a customer following the mail they were sent
        // would get "this link is no longer valid" for a link nothing was wrong with.
        if (execution.InvitationId is not long invitationId)
            return null;

        var live = await db.Set<TenantAdminInvitation>().AsNoTracking()
            .Where(i => i.Id == invitationId && i.UserId == userId
                        && i.RevokedAtUtc == null && i.ExpiresAtUtc > DateTime.UtcNow)
            .Select(i => new { i.Id, i.ExpiresAtUtc })
            .FirstOrDefaultAsync(ct);

        return live is null
            ? null
            : Reconciled(ProvisioningStepCodes.Invitation,
                $"Invitation {invitationId} is live until {live.ExpiresAtUtc:O} and may already be in "
                + "the administrator's inbox. Reissuing would revoke a link nothing is wrong with.",
                new { invitationId, expiresAtUtc = live.ExpiresAtUtc });
    }

    private static ProvisioningStepReconciliation Reconciled(
        string stepCode, string reason, object detail)
        => new(stepCode, reason, JsonSerializer.Serialize(new
        {
            reconciled = true,
            reason,
            evidence = detail
        }));
}
