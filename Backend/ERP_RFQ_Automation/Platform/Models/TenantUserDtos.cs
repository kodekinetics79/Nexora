using System.ComponentModel.DataAnnotations;
using ERP_RFQ_Automation.Platform.Onboarding;

namespace ERP_RFQ_Automation.Platform.Models;

/// <summary>
/// One account inside a customer's workspace, as the platform console sees it.
///
/// <para>Distinct from <see cref="PlatformUserDto"/>, which describes a control-plane OPERATOR.
/// These are the customer's own people: rows in <c>public."Users"</c> scoped to the tenant's
/// primary business unit, holding a role from that unit's <c>Setup_Master</c>.</para>
/// </summary>
public sealed class TenantUserDto
{
    public required long Id { get; init; }
    public required string FirstName { get; init; }
    public string? MiddleName { get; init; }
    public required string LastName { get; init; }
    public required string Email { get; init; }

    public long? RoleId { get; init; }
    public string? RoleCode { get; init; }
    public string? RoleName { get; init; }

    /// <summary>
    /// The stored <c>Setup_Master.RoleRank</c>. Carried to the console because the rank — not the
    /// label — is what decides authority, and an operator looking at "Site Supervisor - Admin"
    /// has no other way to tell whether that name holds the entire tenant.
    /// </summary>
    public short? RoleRank { get; init; }

    public required bool IsActive { get; init; }
    public DateTime? DeactivatedAtUtc { get; init; }
    public DateTime? LastLogin { get; init; }
    public required DateTime CreatedOn { get; init; }

    /// <summary>
    /// The most recent activation invitation issued to this account, or null when none exists.
    ///
    /// <para>The latest one rather than only a live one: an operator answering "why can this
    /// person not sign in?" needs to see that the link was <c>Revoked</c> or <c>Expired</c> just
    /// as much as they need to see that one is <c>Pending</c>. The summary carries no token.</para>
    /// </summary>
    public TenantAdminInvitationSummary? Invitation { get; init; }

    /// <summary>
    /// True when the account has never redeemed an invitation and therefore holds no credential
    /// anyone knows. Such an account cannot sign in however active it is, so reactivating it is
    /// not the repair — reissuing its invitation is.
    /// </summary>
    public required bool AwaitingActivation { get; init; }
}

/// <summary>
/// An assignable role from the tenant's own <c>Setup_Master</c>. There was no way to read this
/// from the platform plane before, which is why the console could not offer a role picker at all.
/// </summary>
public sealed class TenantRoleDto
{
    public required long Id { get; init; }
    public string? Code { get; init; }
    public required string Name { get; init; }
    public string? Description { get; init; }
    public required short Rank { get; init; }

    /// <summary>Human label for <see cref="Rank"/> — "Owner", "Admin", "Manager", "Member".</summary>
    public required string RankLabel { get; init; }

    public required int ActiveUserCount { get; init; }

    /// <summary>
    /// Whether the CALLING operator may grant this role. False is not a server-side secret: the
    /// console renders the option disabled with <see cref="NotGrantableReason"/> beside it, because
    /// a picker that offers a value guaranteed to 403 teaches operators that refusals are noise.
    /// </summary>
    public required bool Grantable { get; init; }

    public string? NotGrantableReason { get; init; }
}

/// <summary>
/// Recognised values for <see cref="CreateTenantUserRequest.Activation"/>. Deliberately the same
/// two words <see cref="AdminActivationMethods"/> uses for the founding administrator, so an
/// operator learns one vocabulary for "how does this person get a password?".
/// </summary>
public static class TenantUserActivationMethods
{
    public const string Invite = "invite";
    public const string Password = "password";
}

public sealed class CreateTenantUserRequest
{
    [Required, EmailAddress, StringLength(320)]
    public string Email { get; set; } = null!;

    [Required, StringLength(100, MinimumLength = 1)]
    public string FirstName { get; set; } = null!;

    [StringLength(100)]
    public string? MiddleName { get; set; }

    [Required, StringLength(100, MinimumLength = 1)]
    public string LastName { get; set; } = null!;

    /// <summary>A role id from <c>GET /api/platform/tenants/{tenantId}/roles</c>.</summary>
    [Required]
    public long RoleId { get; set; }

    [StringLength(64)]
    public string? Timezone { get; set; }

    /// <summary>
    /// "invite" (default) or "password".
    ///
    /// <para><b>invite</b> mints no usable credential: the person receives a single-use, expiring
    /// activation link and chooses their own password. <b>password</b> means a platform operator
    /// types a credential for somebody else's employee, so it is Owner-only, never generated, and
    /// recorded in the audit trail as an operator-set credential. See the class comment on
    /// <c>TenantUsersController</c> for why that path exists at all.</para>
    /// </summary>
    [StringLength(16)]
    public string? Activation { get; set; }

    /// <summary>Read only when <see cref="Activation"/> is "password", and never echoed back.</summary>
    [StringLength(128, MinimumLength = 12)]
    public string? Password { get; set; }

    /// <summary>
    /// Why an operator is creating an account inside a customer's workspace rather than letting
    /// the customer's own Super Administrator do it. Required, and written to the platform audit
    /// trail — this sentence is the whole justification for an endpoint that reaches across the
    /// plane boundary.
    /// </summary>
    [Required, StringLength(1000, MinimumLength = 5)]
    public string Reason { get; set; } = null!;
}

public sealed class ChangeTenantUserRoleRequest
{
    [Required]
    public long RoleId { get; set; }

    [Required, StringLength(1000, MinimumLength = 5)]
    public string Reason { get; set; } = null!;
}

public sealed class TenantUserStatusChangeRequest
{
    [Required, StringLength(1000, MinimumLength = 5)]
    public string Reason { get; set; } = null!;
}

/// <summary>
/// What creating a tenant user returns. Separate from <see cref="TenantUserDto"/> because it can
/// carry a ONE-TIME activation link that must never appear on any list or get endpoint.
/// </summary>
public sealed class CreateTenantUserResponse
{
    public required TenantUserDto User { get; init; }

    /// <summary>Null on the operator-set-password path, where no invitation is issued.</summary>
    public TenantAdminInvitationSummary? Invitation { get; init; }

    /// <summary>True when the configured email provider accepted the invitation message.</summary>
    public required bool EmailDispatched { get; init; }

    /// <summary>
    /// Returned exactly once, to a platform Owner only, and only when the provider did NOT
    /// transmit the message — the identical rule the resend endpoint applies, so a mail outage
    /// cannot strand a customer without also making activation links routinely visible to
    /// operators.
    /// </summary>
    public string? ActivationUrl { get; init; }
}
