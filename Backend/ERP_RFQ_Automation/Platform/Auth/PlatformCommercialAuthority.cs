using System.Security.Claims;
using ERP_RFQ_Automation.Platform.Models;

namespace ERP_RFQ_Automation.Platform.Auth;

/// <summary>
/// Who may decide what a customer is charged.
///
/// <para><b>The hole this closes.</b> <c>TenantsController.ChangePlan</c> is gated on
/// <see cref="PlatformPolicies.Billing"/> and says why in as many words: plan assignment is a
/// BILLING operation, and a support engineer must not be able to change what a customer is
/// charged. But BOTH provisioning doors — the synchronous endpoint and the durable one — accept
/// <c>PlanId</c>, <c>BillingMode</c>, <c>RateCardId</c>, <c>BillingStartsOn</c> and
/// <c>TrialEndsOn</c> under <see cref="PlatformPolicies.TenantAdmin"/>, which a SupportAdmin
/// satisfies. The separation of duties held for changing an existing customer's price and was
/// absent at the one moment the price is first set — so a SupportAdmin could not reprice one
/// tenant, and could create any number of new ones as Internal or Partner, with a reason string,
/// that are never invoiced at all.</para>
///
/// <para><b>Why a shared rule rather than two attributes.</b> The two doors take the same
/// <c>ProvisionTenantRequest</c>, so a rule written on one of them is a rule the other silently
/// does not have. Gating the whole endpoint on Billing instead would be the wrong shape: creating
/// a customer record, its workspace and its administrator is legitimately support work. What is
/// not support work is deciding the commercial terms attached to it.</para>
/// </summary>
public static class PlatformCommercialAuthority
{
    /// <summary>
    /// True when the caller satisfies <see cref="PlatformPolicies.Billing"/> — Owner or
    /// BillingAdmin. Evaluated from the same claim the policy itself reads, so the two cannot
    /// drift into disagreeing about who holds commercial authority.
    /// </summary>
    public static bool HoldsCommercialAuthority(ClaimsPrincipal? actor)
    {
        var role = actor?.FindFirst(PlatformAuthConstants.PlatformRoleClaim)?.Value;
        return role == nameof(PlatformRole.Owner) || role == nameof(PlatformRole.BillingAdmin);
    }

    /// <summary>
    /// Names the commercial levers present on a provisioning request, or an empty list when it
    /// carries none. Reported as a list rather than a bare boolean so the refusal can tell the
    /// operator exactly which fields to drop — a 403 that does not say what was objectionable
    /// gets worked around by trial and error.
    /// </summary>
    public static IReadOnlyList<string> CommercialLeversIn(
        long? planId, string? billingMode, string? billingModeReason, long? rateCardId,
        DateTime? billingStartsOn, DateTime? trialEndsOn)
    {
        var levers = new List<string>();
        if (planId is not null) levers.Add("planId");
        // Billable is the default, so choosing it asserts nothing; every other mode is a decision
        // to charge this customer differently, or not at all.
        if (!string.IsNullOrWhiteSpace(billingMode)
            && !string.Equals(billingMode.Trim(), nameof(Models.TenantBillingMode.Billable),
                StringComparison.OrdinalIgnoreCase))
            levers.Add("billingMode");
        if (!string.IsNullOrWhiteSpace(billingModeReason)) levers.Add("billingModeReason");
        if (rateCardId is not null) levers.Add("rateCardId");
        if (billingStartsOn is not null) levers.Add("billingStartsOn");
        if (trialEndsOn is not null) levers.Add("trialEndsOn");
        return levers;
    }

    /// <summary>
    /// The refusal message. Deliberately explains the rule rather than only stating it: the
    /// operator reading this is usually a support engineer doing legitimate onboarding, and the
    /// useful next step is "ask an Owner or BillingAdmin to set the terms", not "you are denied".
    /// </summary>
    public static string DescribeRefusal(IReadOnlyList<string> levers) =>
        $"Setting {string.Join(", ", levers)} decides what this customer is charged, which is a "
        + "billing operation reserved to an Owner or BillingAdmin — the same rule that governs "
        + "changing an existing tenant's plan. Provision the tenant without commercial terms and "
        + "have them set separately, or ask someone holding billing authority to submit this.";
}
