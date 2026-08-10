using System.Security.Claims;
using System.Text.Json;
using ERP_RFQ_Automation.Platform.Auth;
using ERP_RFQ_Automation.Platform.Lifecycle;
using ERP_RFQ_Automation.Platform.DataAssets;
using Microsoft.AspNetCore.Authorization;

namespace ERP_RFQ_Automation.Platform.Support;

/// <summary>
/// Decides who may read the PAYLOAD of an audit entry, as opposed to the fact of it.
///
/// <para><b>The defect this closes (R6), and why it is a class rather than a field.</b> Every
/// privileged endpoint in this control plane now records a rich <c>{before, after}</c> snapshot into
/// <c>platform.PlatformAuditLogs</c>, and the audit explorer is a single read surface over all of
/// them, gated on <see cref="PlatformPolicies.PlatformScope"/> — which every operator satisfies,
/// including ReadOnlyOps. So the explorer was a MORE OPEN DOOR onto those payloads than the
/// endpoints that wrote them. A red-team audit drove the real Owner|BillingAdmin
/// <c>PUT /api/platform/billing/tenants/{id}/commercial-terms</c> and then read
/// <c>Tenant.BillingModeReason</c> — the one commercial column that BOTH grant-hardening migrations
/// deliberately withhold from the tenant plane, and that <c>TenantAccessService</c> reduces to a
/// boolean inside the database rather than read — straight back out as a ReadOnlyOps actor.</para>
///
/// <para>Blanking that column by name would have fixed one leak and left the shape intact: the next
/// Billing-gated or Owner-gated write to record a reason, a price, a credential hint or a customer's
/// commercial terms would leak identically, and nothing would have failed. The rule implemented here
/// is therefore about the SHAPE:</para>
///
/// <para><b>An audit entry's payload is disclosed only to a caller who could have performed the
/// write.</b> Each action verb maps to the authorization policy that gates the endpoint writing it,
/// and the explorer evaluates the CALLER against that policy — the real registered platform
/// policies, not a re-statement of the role logic — before returning metadata, before/after, or
/// changed VALUES.</para>
///
/// <para><b>Why the writer's own policy and not a severity ladder.</b> The platform policies are
/// deliberately not totally ordered: <see cref="PlatformPolicies.Billing"/> is Owner|BillingAdmin and
/// <see cref="PlatformPolicies.TenantAdmin"/> is Owner|SupportAdmin, and neither contains the other.
/// That incomparability is the separation of duties recorded at <c>TenantsController.ChangePlan</c>
/// (Sec9), and it is exactly the property a ladder would destroy — a BillingAdmin would inherit
/// support-impersonation reasons, or a SupportAdmin would inherit commercial commentary. Keying on
/// the writer's policy reproduces the separation instead of approximating it.</para>
///
/// <para><b>What survives for every tier, deliberately.</b> Withholding is scoped to VALUES. The
/// entry's identity — actor, action, target, tenant, result, IP, timestamp — and the NAMES of the
/// fields that changed are served to anyone who can reach the explorer. So "what did we change for
/// this customer, when, and who did it" still answers for ReadOnlyOps; only "and what exactly did it
/// say" needs the writer's authority. Field names are entity property names, i.e. schema, not
/// customer content. And the withholding is EXPLICIT: the response carries
/// <c>metadataDisclosed = false</c> and names the policy that would unlock it, so a console renders
/// "restricted — requires Platform.Billing" rather than a silently empty field that reads as "this
/// action recorded nothing".</para>
///
/// <para><b>Unknown verbs fail CLOSED to <see cref="PlatformPolicies.Owner"/>.</b> Owner is the only
/// policy no other policy satisfies, so it is the most restrictive default available. This is the
/// property that makes the fix generalise rather than merely enumerate: a verb introduced next month
/// by a module nobody told this file about is restricted until it is registered, and drift can only
/// ever over-restrict. The failure mode of forgetting is an operator asking why they cannot see a
/// payload — not a silent republication of the thing that was restricted elsewhere.</para>
/// </summary>
public static class PlatformAuditDisclosure
{
    /// <summary>
    /// Applied to any verb this table does not name. See the type docs: Owner is the most
    /// restrictive policy in the platform set, so an unregistered verb over-restricts rather than
    /// leaks.
    /// </summary>
    public const string FailClosedPolicy = PlatformPolicies.Owner;

    /// <summary>
    /// Action verb → the policy gating the endpoint that writes it. The mapping is DATA; the rule
    /// that consults it is the mechanism. Entries are grouped by their writer so a reader can check
    /// them against the controller in one hop, and
    /// <c>PlatformAuditExplorerDisclosureTests</c> pins the high-risk groups against the writing
    /// controller's actual <c>[Authorize]</c> attribute by reflection rather than against a copy of
    /// it here.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string> ByAction =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            // Billing/Controllers/PlatformBillingController — class gate Platform.Billing, with
            // finalization elevated to Owner. These payloads carry prices, rate-card lines,
            // statement totals and the commercial-terms commentary that started this finding.
            ["billing.ratecard.create"] = PlatformPolicies.Billing,
            ["billing.ratecard.update"] = PlatformPolicies.Billing,
            ["billing.statement.compute"] = PlatformPolicies.Billing,
            ["billing.statement.finalize"] = PlatformPolicies.Owner,
            ["billing.invoice.create"] = PlatformPolicies.Billing,
            ["billing.invoice.finalize"] = PlatformPolicies.Owner,
            ["billing.invoice.credit"] = PlatformPolicies.Owner,
            ["billing.invoice.payment"] = PlatformPolicies.Billing,
            ["billing.tenant.rate-card"] = PlatformPolicies.Billing,
            ["billing.tenant.commercial-terms"] = PlatformPolicies.Billing,
            // Where a customer's invoice is SENT and when it falls due — same writer, same class
            // gate, so the same reader. Registered explicitly rather than left to the fail-closed
            // default, which would hide a BillingAdmin's own change from the BillingAdmin who made
            // it: the one reader who certainly needs to check it.
            ["billing.tenant.account-contact"] = PlatformPolicies.Billing,

            // Platform/Controllers/TenantsController.ChangePlan — Sec9 puts plan assignment on the
            // BILLING policy precisely so support cannot change what a customer is charged. Reading
            // the before/after of that change back is the same fact.
            ["tenant.plan.change"] = PlatformPolicies.Billing,

            // Platform/Controllers/PlatformOperationsController — Owner. Plan payloads are the price
            // list itself.
            ["plan.create"] = PlatformPolicies.Owner,
            ["plan.update"] = PlatformPolicies.Owner,

            // Platform/Controllers/PlatformUsersController — class gate Owner. These payloads
            // describe other operators' roles and account state; the tier that cannot grant a role
            // has no call to read the grant's before/after.
            ["platform-user.create"] = PlatformPolicies.Owner,
            ["platform-user.role.change"] = PlatformPolicies.Owner,
            ["platform-user.deactivate"] = PlatformPolicies.Owner,
            ["platform-user.reactivate"] = PlatformPolicies.Owner,
            ["platform-user.password.reset"] = PlatformPolicies.Owner,

            // Platform/Services/PlatformOwnerSeeder — no HTTP surface at all, so nothing weaker than
            // the fail-closed default can be justified.
            ["platform.owner.bootstrap"] = PlatformPolicies.Owner,

            // Platform/Lifecycle/TenantOffboardingController — Owner on every irreversible verb.
            // Purge and erasure payloads enumerate what was destroyed for a named customer.
            ["tenant.offboarding.schedule-deletion"] = PlatformPolicies.Owner,
            ["tenant.offboarding.cancel-deletion"] = PlatformPolicies.Owner,
            ["tenant.offboarding.purge.started"] = PlatformPolicies.Owner,
            ["tenant.offboarding.purge.completed"] = PlatformPolicies.Owner,
            ["tenant.offboarding.purge.failed"] = PlatformPolicies.Owner,
            ["tenant.offboarding.erase-personal-data"] = PlatformPolicies.Owner,
            [TenantLegalHoldService.PlaceAction] = PlatformPolicies.Owner,
            [TenantLegalHoldService.ReleaseAction] = PlatformPolicies.Owner,
            ["tenant.offboarding.export"] = PlatformPolicies.Owner,
            [TenantDataAssetRegistryService.RegisterAction] = PlatformPolicies.Owner,
            [TenantDataAssetRegistryService.VerifyAction] = PlatformPolicies.Owner,

            // Platform/Controllers/TenantsController — tenant lifecycle is TenantAdmin; AI policy
            // mutation is Owner. Payload authority follows the writing method, not the class gate.
            ["tenant.provision"] = PlatformPolicies.TenantAdmin,
            ["tenant.suspend"] = PlatformPolicies.TenantAdmin,
            ["tenant.resume"] = PlatformPolicies.TenantAdmin,
            ["tenant.archive"] = PlatformPolicies.TenantAdmin,
            ["tenant.restore"] = PlatformPolicies.TenantAdmin,
            // Owner, matching its writer. The payload is a contractual residency assertion and the
            // evidence it was checked against, which is an auditor's document rather than a
            // support one.
            ["tenant.data-region.update"] = PlatformPolicies.Owner,
            ["tenant.ai-policy.update"] = PlatformPolicies.Owner,
            ["tenant.ai-provider.authorize"] = PlatformPolicies.Owner,
            ["tenant.ai-provider.revoke"] = PlatformPolicies.Owner,

            // Platform/Onboarding/TenantAdminInvitationsController — TenantAdmin.
            ["tenant.admin-invitation.resend"] = PlatformPolicies.TenantAdmin,
            ["tenant.admin-invitation.revoke"] = PlatformPolicies.TenantAdmin,

            // Platform/Controllers/ImpersonationController — Platform.Impersonate. The payload is the
            // mandatory business reason for entering a customer's account; it is support context and
            // belongs to the support tier, not to whoever happens to hold a platform token.
            ["impersonate.issue"] = PlatformPolicies.Impersonate,
            ["impersonate.revoke"] = PlatformPolicies.Impersonate,

            // Platform/Support — this module. Mutations are TenantAdmin, so their payloads are too.
            [PlatformSupportTicketsController.Actions.Create] = PlatformPolicies.TenantAdmin,
            [PlatformSupportTicketsController.Actions.Note] = PlatformPolicies.TenantAdmin,
            [PlatformSupportTicketsController.Actions.Transition] = PlatformPolicies.TenantAdmin,
            [PlatformSupportTicketsController.Actions.Assign] = PlatformPolicies.TenantAdmin,
            [PlatformSupportTicketsController.Actions.Severity] = PlatformPolicies.TenantAdmin,
            [PlatformSupportTicketsController.Actions.Link] = PlatformPolicies.TenantAdmin,
            [PlatformSupportTicketsController.Actions.Unlink] = PlatformPolicies.TenantAdmin,
            [SupportTicketRedactionService.AuditAction] = PlatformPolicies.Owner,

            // Platform/Controllers/PlatformAuthController — the login endpoint is [AllowAnonymous],
            // so there is no writer's authority to inherit. These two are pinned to the policy the
            // reader already had to satisfy to reach the explorer at all: operator sign-in telemetry
            // (an email and an outcome) is observability, which is the whole remit of the read-only
            // tier. Nothing else in this table is allowed to be PlatformScope for exactly the reason
            // R6 exists — this is the deliberate, named exception, not the default.
            ["platform.login"] = PlatformPolicies.PlatformScope,
            ["platform.login.failed"] = PlatformPolicies.PlatformScope
        };

    /// <summary>The policy a caller must satisfy to read <paramref name="action"/>'s payload.</summary>
    public static string RequiredPolicyFor(string action)
        => action is not null && ByAction.TryGetValue(action, out var policy) ? policy : FailClosedPolicy;

    /// <summary>Every verb with an explicit entry. Exposed so tests can check the table against its writers.</summary>
    public static IReadOnlyCollection<string> KnownActions { get; } = ByAction.Keys.ToArray();

    /// <summary>
    /// Evaluates <paramref name="actor"/> against the policy of each distinct action once, and
    /// returns the resulting gate. Batched deliberately: a 200-row page would otherwise run 200
    /// authorization evaluations to answer at most a handful of distinct questions.
    /// </summary>
    public static async Task<AuditDisclosureGate> ResolveAsync(
        IAuthorizationService authorization, ClaimsPrincipal actor,
        IEnumerable<string> actions, CancellationToken ct = default)
    {
        var decisions = new Dictionary<string, bool>(StringComparer.Ordinal);
        var byPolicy = new Dictionary<string, bool>(StringComparer.Ordinal);

        foreach (var action in actions.Where(a => !string.IsNullOrEmpty(a)).Distinct(StringComparer.Ordinal))
        {
            var policy = RequiredPolicyFor(action);
            if (!byPolicy.TryGetValue(policy, out var granted))
            {
                ct.ThrowIfCancellationRequested();
                granted = (await authorization.AuthorizeAsync(actor, policy)).Succeeded;
                byPolicy[policy] = granted;
            }
            decisions[action] = granted;
        }

        return new AuditDisclosureGate(decisions);
    }
}

/// <summary>
/// The per-request answer to "may this caller see this action's payload". Immutable and cheap to
/// consult; built once per response by <see cref="PlatformAuditDisclosure.ResolveAsync"/>.
/// </summary>
public sealed class AuditDisclosureGate
{
    private readonly IReadOnlyDictionary<string, bool> _decisions;

    internal AuditDisclosureGate(IReadOnlyDictionary<string, bool> decisions) => _decisions = decisions;

    /// <summary>
    /// Fail-closed on a verb that was not part of the batch this gate was built from — a projection
    /// that forgot to declare an action must withhold, not disclose.
    /// </summary>
    public bool MayDisclose(string action)
        => action is not null && _decisions.TryGetValue(action, out var granted) && granted;

    /// <summary>The policy that would unlock the payload, for the caller-facing explanation.</summary>
    public static string RequiredPolicyFor(string action) => PlatformAuditDisclosure.RequiredPolicyFor(action);

    /// <summary>
    /// One row's metadata as this caller may see it: the parsed payload, or null. Every surface that
    /// republishes an audit payload — the explorer's list and entry views, the tenant timeline, the
    /// per-ticket timeline and the tenant operations summary — reads it through here or through
    /// <see cref="Shape"/>, because a redaction applied at five call sites is a redaction that will
    /// be applied at four of them after the next endpoint is added.
    /// </summary>
    public JsonElement? Metadata(string action, string? rawMetadata)
        => MayDisclose(action) ? PlatformAuditMetadata.TryParse(rawMetadata) : null;

    /// <summary>
    /// The full decode, for the entry-detail view: metadata, before, after, and field-level changes.
    ///
    /// <para>When disclosure is refused the VALUES go and the field NAMES stay: an operator can still
    /// see that the commercial terms changed, and which terms, without reading what they now say.
    /// The list surfaces use <see cref="Metadata"/> instead — they discard the change set, and
    /// decoding a diff per row to throw it away would be 200 parses a page.</para>
    /// </summary>
    public AuditPayloadView Shape(string action, string? rawMetadata)
    {
        var policy = RequiredPolicyFor(action);
        var parsed = PlatformAuditMetadata.TryParse(rawMetadata);
        var changes = PlatformAuditMetadata.Changes(parsed);

        if (MayDisclose(action))
            return new AuditPayloadView(
                parsed, PlatformAuditMetadata.Before(parsed), PlatformAuditMetadata.After(parsed),
                changes, true, policy);

        var fieldsOnly = changes
            .Select(c => new PlatformAuditFieldChangeDto { Field = c.Field })
            .ToList();
        return new AuditPayloadView(null, null, null, fieldsOnly, false, policy);
    }
}

/// <summary>One audit row's payload as a specific caller is permitted to see it.</summary>
public sealed record AuditPayloadView(
    JsonElement? Metadata,
    JsonElement? Before,
    JsonElement? After,
    IReadOnlyList<PlatformAuditFieldChangeDto> Changes,
    bool Disclosed,
    string RequiredPolicy);
