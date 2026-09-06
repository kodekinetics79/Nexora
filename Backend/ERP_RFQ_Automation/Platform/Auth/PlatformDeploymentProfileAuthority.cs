using System.Security.Claims;
using ERP_RFQ_Automation.Platform.Models;

namespace ERP_RFQ_Automation.Platform.Auth;

/// <summary>
/// Who may create a tenant into a non-PRODUCTION deployment profile, and on what evidence.
///
/// <para><b>The hole this closes before it opens.</b> Both provisioning doors take the same
/// <c>ProvisionTenantRequest</c> under <see cref="PlatformPolicies.TenantAdmin"/>, which a
/// SupportAdmin satisfies — while <c>TenantsController.SetDeploymentProfile</c>, the only other
/// way a profile is ever set, is Owner-only and says why: the profile decides which catalogued
/// production prerequisites a tenant's activation may record as DEFERRED, and that is the one
/// lever in the activation system capable of switching a tenant on without its gates. Accepting
/// the field on the provisioning request without this rule would hand a SupportAdmin, at creation
/// time, exactly the relaxation the profile endpoint refuses them forever after.</para>
///
/// <para><b>Why a shared rule rather than two attributes.</b> The same reasoning as
/// <see cref="PlatformCommercialAuthority"/>: a rule written on one door is a rule the other
/// silently does not have. Gating the whole endpoint on Owner instead would be the wrong shape —
/// creating a customer, their workspace and their administrator is legitimately support work.
/// Deciding that a tenant is exempt from production prerequisites is not.</para>
/// </summary>
public static class PlatformDeploymentProfileAuthority
{
    /// <summary>
    /// The same floor as a data-region restatement and a billing exemption, for the same reason: a
    /// required field satisfied by "test" leaves a record that answers "what" and refuses to
    /// answer "why", which is the only question anybody asks it six months later.
    /// </summary>
    public const int MinimumReasonLength = 15;

    /// <summary>
    /// Reads the same claim <see cref="PlatformPolicies.Owner"/> reads, so the two cannot drift
    /// into disagreeing about who holds the authority.
    /// </summary>
    public static bool HoldsProfileAuthority(ClaimsPrincipal? actor) =>
        actor?.FindFirst(PlatformAuthConstants.PlatformRoleClaim)?.Value == nameof(PlatformRole.Owner);

    /// <param name="refusal">
    /// Null when the request may proceed. Otherwise the message to return — and
    /// <paramref name="forbidden"/> says whether it is a 403 (authority) or a 400 (evidence),
    /// because telling an operator "you may not" when they merely wrote a short reason sends them
    /// looking for a permission they already have.
    /// </param>
    /// <returns>The profile to record. PRODUCTION whenever the field is absent.</returns>
    public static TenantDeploymentProfile Validate(
        string? requestedProfile, string? reason, ClaimsPrincipal? actor,
        out string? refusal, out bool forbidden)
    {
        refusal = null;
        forbidden = false;

        if (string.IsNullOrWhiteSpace(requestedProfile))
            return TenantDeploymentProfile.Production;

        if (!TenantDeploymentProfiles.TryParse(requestedProfile, out var profile))
        {
            refusal = $"'{requestedProfile}' is not a deployment profile. Use one of: "
                      + $"{string.Join(", ", TenantDeploymentProfiles.All)}.";
            return TenantDeploymentProfile.Production;
        }

        // Asking for PRODUCTION explicitly is asking for the default. It asserts nothing, relaxes
        // nothing, and must not require an Owner or a reason to say out loud.
        if (profile == TenantDeploymentProfile.Production) return profile;

        if (!HoldsProfileAuthority(actor))
        {
            forbidden = true;
            refusal =
                $"Creating a tenant on the {TenantDeploymentProfiles.ToWire(profile)} profile decides that "
                + "its catalogued production prerequisites may be deferred, which is an Owner decision — "
                + "the same rule that governs changing a tenant's deployment profile afterwards. Provision "
                + "it as PRODUCTION and have an Owner move it, or ask an Owner to submit this.";
            return profile;
        }

        var trimmed = reason?.Trim();
        if (string.IsNullOrWhiteSpace(trimmed) || trimmed.Length < MinimumReasonLength)
        {
            refusal =
                $"A deploymentProfileReason of at least {MinimumReasonLength} characters is required for the "
                + $"{TenantDeploymentProfiles.ToWire(profile)} profile. It is recorded as the approval on the "
                + "tenant itself, and an unapproved non-production profile defers nothing and fails closed "
                + "exactly as PRODUCTION.";
        }

        return profile;
    }
}
