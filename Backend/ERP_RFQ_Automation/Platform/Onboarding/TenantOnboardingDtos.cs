using System.ComponentModel.DataAnnotations;

namespace ERP_RFQ_Automation.Platform.Onboarding;

/// <summary>
/// The activation page's view of a token.
///
/// <para>Field names and the <see cref="Status"/> vocabulary are the contract
/// <c>Frontend/src/pages/Activation/activationApi.ts</c> reads: it switches on
/// <c>status</c> when present and falls back to the HTTP status code, so the two must agree.
/// A rejected token carries the status and NOTHING else — no address, no company — because a
/// link the holder cannot use must not describe the account it belonged to.</para>
/// </summary>
public sealed class TenantActivationChallengeResponse
{
    /// <summary>"valid" | "expired" | "used" | "revoked" | "invalid".</summary>
    public string Status { get; set; } = null!;

    /// <summary>Masked by default (see <c>TenantOnboarding:MaskActivationEmail</c>). Null unless valid.</summary>
    public string? Email { get; set; }

    public string? TenantName { get; set; }

    public DateTime? ExpiresAtUtc { get; set; }

    /// <summary>Null unless valid. Lets the page greet the invitee by name.</summary>
    public string? FirstName { get; set; }

    /// <summary>
    /// So the page can state the rule up front instead of after a failed submit — and so the
    /// client-side checklist cannot silently drift from what the server actually enforces.
    /// </summary>
    public int? MinimumPasswordLength { get; set; }
}

/// <summary>
/// The activation POST body. The token is not in it: it travels in the route, so the page can
/// submit exactly what it was opened with and no request can carry a token and a password that
/// disagree about which account is being activated.
/// </summary>
public sealed class SetActivationPasswordRequest
{
    /// <summary>
    /// The password the invitee chooses. Length is bounded here only to reject obvious abuse
    /// cheaply; the real rule lives in <see cref="ActivationPasswordPolicy"/>, which can explain
    /// itself to the person typing.
    /// </summary>
    [Required]
    [StringLength(256, MinimumLength = 1)]
    public string Password { get; set; } = null!;
}

/// <summary>Reissue an invitation, invalidating whatever link was outstanding.</summary>
public sealed class ResendTenantAdminInvitationRequest
{
    /// <summary>
    /// Target account. Omit to let the platform resolve the tenant's founding administrator,
    /// which is what an operator clicking "resend" from the tenant screen means.
    /// </summary>
    public long? UserId { get; set; }

    [StringLength(1000)]
    public string? Reason { get; set; }
}

public sealed class RevokeTenantAdminInvitationRequest
{
    /// <summary>
    /// Required, like every other platform mutation that changes a customer's access. Revoking
    /// an invitation is normally a response to something (a wrong address, a forwarded email) and
    /// that something is exactly what an incident review needs to read six months later.
    /// </summary>
    [Required]
    [StringLength(1000, MinimumLength = 3)]
    public string Reason { get; set; } = null!;
}

/// <summary>
/// The operator's answer to "did the invite go out?".
///
/// <para>It carries no token and no link, deliberately. Handing the activation URL back to the
/// person who pressed the button would put the customer's credential-in-waiting into the
/// operator's hands — precisely the property this whole flow exists to remove. When mail is
/// genuinely blocked, the documented fallback is the generated-password handover that
/// provisioning already supports, which is at least visible as such.</para>
/// </summary>
public sealed class ResendTenantAdminInvitationResponse
{
    public TenantAdminInvitationSummary Invitation { get; set; } = null!;

    /// <summary>
    /// False when the configured provider refused or is the console provider in a deployment
    /// that never wired real mail. The invitation is valid either way — this says whether it
    /// left the building.
    /// </summary>
    public bool EmailDispatched { get; set; }
}
