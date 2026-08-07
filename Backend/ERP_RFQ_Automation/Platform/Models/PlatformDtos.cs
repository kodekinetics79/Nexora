using System.ComponentModel.DataAnnotations;

namespace ERP_RFQ_Automation.Platform.Models;

// ---- Auth ----------------------------------------------------------------

public class PlatformLoginRequest
{
    [Required, EmailAddress]
    public string Email { get; set; } = null!;

    [Required]
    public string Password { get; set; } = null!;
}

public class PlatformLoginResponse
{
    public long Id { get; set; }
    public string Email { get; set; } = null!;
    public string PlatformRole { get; set; } = null!;
    public string Token { get; set; } = null!;
    public DateTime ExpiresAtUtc { get; set; }
}

// ---- Tenants -------------------------------------------------------------

public class TenantSummaryDto
{
    public long Id { get; set; }
    public string Name { get; set; } = null!;
    public string Slug { get; set; } = null!;
    public string Status { get; set; } = null!;
    public long? PlanId { get; set; }
    public string? PlanCode { get; set; }
    public long? PrimaryBusinessUnitId { get; set; }
    public DateTime CreatedOn { get; set; }
    public string? StatusReason { get; set; }

    // Legal identity. The console renders these; nothing here is fabricated when absent, so a
    // tenant provisioned before these columns existed simply shows blanks rather than guesses.
    public string? LegalName { get; set; }
    public string? RegistrationNumber { get; set; }
    public string? TaxNumber { get; set; }
    public string? CountryCode { get; set; }
    public string? Industry { get; set; }
    public string? Website { get; set; }
    public string? AddressLine1 { get; set; }
    public string? AddressLine2 { get; set; }
    public string? City { get; set; }
    public string? StateProvince { get; set; }
    public string? PostalCode { get; set; }
    public string? Phone { get; set; }
    public string? ContactEmail { get; set; }
    public string? LogoUrl { get; set; }

    // Operating defaults.
    public string? BaseCurrencyCode { get; set; }
    public string? TimeZoneId { get; set; }
    public string? Locale { get; set; }
    public string? DataRegion { get; set; }

    // Commercial terms. BillingMode travels as its NAME so the console never has to know the
    // enum's numbering, and TrialEndsOn is exposed on the LIST payload deliberately: an expired
    // trial still being served is revenue leaking right now, and it has to be visible without
    // opening the tenant.
    public string BillingMode { get; set; } = nameof(TenantBillingMode.Billable);
    public string? BillingModeReason { get; set; }
    public long? RateCardId { get; set; }
    public DateTime? BillingStartsOn { get; set; }
    public DateTime? TrialEndsOn { get; set; }
    public DateTime? ContractStartOn { get; set; }
    public DateTime? ContractEndOn { get; set; }
    public int? PaymentTermsDays { get; set; }
    public string? PurchaseOrderReference { get; set; }
    public string? BillingContactName { get; set; }
    public string? BillingContactEmail { get; set; }
    public string? BillingAddress { get; set; }
    public string? AccountOwnerEmail { get; set; }
}

/// <summary>
/// Everything it takes to stand up a customer company, in one atomic request.
///
/// <para><b>Why this is not three fields.</b> Tenant creation is the first step of every
/// downstream journey: the legal block becomes the letterhead on the customer's first quote,
/// the operating block becomes the currency and unit lists their commercial screens read, the
/// commercial block decides whether they are ever charged, and the administrator block decides
/// whether anybody can log in at all. Collecting a name and a slug and calling that a company
/// pushed all four of those decisions into "somebody will fix it later", and later never came.</para>
/// </summary>
public class ProvisionTenantRequest
{
    // ---- company identity ---------------------------------------------------------------

    /// <summary>Trading / display name.</summary>
    [Required, StringLength(256)]
    public string Name { get; set; } = null!;

    /// <summary>Optional; derived from Name when omitted.</summary>
    [StringLength(64)]
    public string? Slug { get; set; }

    /// <summary>
    /// Registered legal entity name. Distinct from <see cref="Name"/> because commercial and tax
    /// documents must carry the registered name, not the brand.
    /// </summary>
    [StringLength(256)]
    public string? LegalName { get; set; }

    [StringLength(64)]
    public string? RegistrationNumber { get; set; }

    [StringLength(64)]
    public string? TaxNumber { get; set; }

    /// <summary>ISO-3166-1 alpha-2, e.g. "SA". Validated in the controller, not by attribute,
    /// so the operator gets a message naming the offending value.</summary>
    [StringLength(2, MinimumLength = 2)]
    public string? CountryCode { get; set; }

    [StringLength(128)]
    public string? Industry { get; set; }

    [StringLength(512)]
    public string? Website { get; set; }

    [StringLength(256)]
    public string? AddressLine1 { get; set; }

    [StringLength(256)]
    public string? AddressLine2 { get; set; }

    [StringLength(128)]
    public string? City { get; set; }

    [StringLength(128)]
    public string? StateProvince { get; set; }

    [StringLength(32)]
    public string? PostalCode { get; set; }

    [StringLength(64)]
    public string? Phone { get; set; }

    /// <summary>Company mailbox (info@/sales@) — NOT the administrator's address.</summary>
    [EmailAddress, StringLength(320)]
    public string? ContactEmail { get; set; }

    [StringLength(1024)]
    public string? LogoUrl { get; set; }

    // ---- operating defaults ---------------------------------------------------------------
    // Seeded into the tenant's own reference data, so its first user is not asked to build a
    // currency list before they can raise a quote.

    /// <summary>ISO-4217, e.g. "SAR". Becomes the tenant's base Currency row.</summary>
    [StringLength(3, MinimumLength = 3)]
    public string? BaseCurrencyCode { get; set; }

    /// <summary>IANA time zone id, e.g. "Asia/Riyadh".</summary>
    [StringLength(64)]
    public string? TimeZoneId { get; set; }

    [StringLength(16)]
    public string? Locale { get; set; }

    [StringLength(32)]
    public string? DataRegion { get; set; }

    // ---- commercial terms -----------------------------------------------------------------
    // The revenue block. Defaults are chosen so that the ONLY way to create a tenant nobody
    // pays for is to say so explicitly and give a reason that lands in the audit log.

    /// <summary>
    /// Required when <see cref="BillingMode"/> is Billable. Not optional any more: a Billable
    /// tenant with no plan produces a statement with no base subscription line at all
    /// (BillingStatementService.BuildLines emits it only when a plan exists), so "optional plan"
    /// meant "silently free forever".
    /// </summary>
    public long? PlanId { get; set; }

    /// <summary>One of <see cref="TenantBillingMode"/>. Defaults to Billable when omitted.</summary>
    [StringLength(16)]
    public string? BillingMode { get; set; }

    /// <summary>Required for every mode other than Billable. Audited.</summary>
    [StringLength(1000)]
    public string? BillingModeReason { get; set; }

    /// <summary>
    /// Pins the price list. Omitted means the tenant inherits whichever card is active for the
    /// period — acceptable for standard-price customers, wrong for negotiated ones.
    /// </summary>
    public long? RateCardId { get; set; }

    public DateTime? BillingStartsOn { get; set; }

    /// <summary>Required when <see cref="BillingMode"/> is Trial. An open-ended trial is free service.</summary>
    public DateTime? TrialEndsOn { get; set; }

    public DateTime? ContractStartOn { get; set; }

    public DateTime? ContractEndOn { get; set; }

    [Range(0, 365)]
    public int? PaymentTermsDays { get; set; }

    [StringLength(128)]
    public string? PurchaseOrderReference { get; set; }

    [StringLength(200)]
    public string? BillingContactName { get; set; }

    [EmailAddress, StringLength(320)]
    public string? BillingContactEmail { get; set; }

    [StringLength(1024)]
    public string? BillingAddress { get; set; }

    [EmailAddress, StringLength(320)]
    public string? AccountOwnerEmail { get; set; }

    // ---- founding administrator -------------------------------------------------------
    // Required, deliberately. A tenant without its founding Super Admin is a shell nobody
    // can log into: the customer journey is Platform Admin -> customer account -> that
    // customer's Super Admin -> sub accounts, and provisioning that stops at the shell
    // breaks the journey at its first step. If an operator truly needs an admin-less
    // tenant they are doing something this API should not encourage.

    [Required, EmailAddress, StringLength(320)]
    public string AdminEmail { get; set; } = null!;

    [Required, StringLength(100, MinimumLength = 1)]
    public string AdminFirstName { get; set; } = null!;

    [Required, StringLength(100, MinimumLength = 1)]
    public string AdminLastName { get; set; } = null!;

    [StringLength(128)]
    public string? AdminJobTitle { get; set; }

    [StringLength(64)]
    public string? AdminPhone { get; set; }

    /// <summary>
    /// "invite" (default) or "password".
    ///
    /// <para><b>invite</b> mints no credential at all: the administrator receives a single-use,
    /// expiring activation link and chooses their own password, so the operator never knows it
    /// and there is nothing to read aloud, mistype, or leave unrotated. <b>password</b> exists
    /// for customers whose mail is blocked or who are onboarded live on a call.</para>
    /// </summary>
    [StringLength(16)]
    public string? AdminActivation { get; set; }

    /// <summary>
    /// Honoured only when <see cref="AdminActivation"/> is "password". When that path is chosen
    /// and this is omitted, a strong password is GENERATED and returned exactly once in the
    /// provisioning response — stored only as a BCrypt hash, retrievable never.
    /// </summary>
    [StringLength(128, MinimumLength = 8)]
    public string? AdminPassword { get; set; }
}

/// <summary>
/// Recognised values for <see cref="ProvisionTenantRequest.AdminActivation"/>.
/// </summary>
public static class AdminActivationMethods
{
    public const string Invite = "invite";
    public const string Password = "password";
}

/// <summary>
/// What provisioning returns. Distinct from <see cref="TenantSummaryDto"/> because it can carry a
/// ONE-TIME credential or activation link that must never appear on any list/get endpoint.
/// </summary>
public class ProvisionTenantResponse
{
    public TenantSummaryDto Tenant { get; set; } = null!;

    public FoundingAdminDto FoundingAdmin { get; set; } = null!;

    /// <summary>
    /// Proof the workspace is genuinely usable rather than an empty shell. The operator reads
    /// this back to the customer as a readiness checklist, and it is the thing that would have
    /// made the old "tenant created successfully" toast honest.
    /// </summary>
    public TenantBaselineDto Baseline { get; set; } = new();

    /// <summary>
    /// The commercial posture this tenant was created under, including anything that puts
    /// revenue at risk. Surfaced at creation time because a leak noticed at creation costs
    /// nothing and a leak noticed at the quarter's end costs a quarter.
    /// </summary>
    public TenantBillingPostureDto Billing { get; set; } = new();
}

public class FoundingAdminDto
{
    public long UserId { get; set; }
    public string Email { get; set; } = null!;
    public string RoleName { get; set; } = null!;

    /// <summary>
    /// Present ONLY when the password was generated server-side, and only in this response.
    /// The operator hands it to the customer through a secure channel. Null on the invite path,
    /// where no password exists yet at all.
    /// </summary>
    public string? GeneratedPassword { get; set; }

    /// <summary>Present ONLY on the invite path. Null when a password was set instead.</summary>
    public AdminInvitationDto? Invitation { get; set; }
}

/// <summary>
/// The issued activation invitation. The token is never persisted in cleartext, so nothing here
/// can be re-read later — a lost link is re-issued, never recovered.
/// </summary>
public class AdminInvitationDto
{
    public DateTime ExpiresAtUtc { get; set; }

    /// <summary>
    /// Populated ONLY when <see cref="EmailSent"/> is false.
    ///
    /// <para>An activation link is a working credential for an Owner-rank account in the
    /// customer's tenant. Handing it to the operator by default would reinstate exactly what the
    /// invite path exists to remove — an operator holding the means to enter their customer's
    /// workspace. When the mail actually went out, the recipient's mailbox is the only place the
    /// link belongs. It is surfaced only as the fallback for a send that failed, where the
    /// alternative is a customer who can never activate at all.</para>
    /// </summary>
    public string? ActivationUrl { get; set; }

    /// <summary>
    /// False when the notification provider rejected or skipped the send (the default provider
    /// logs instead of sending). The operator must then hand the link over themselves rather
    /// than assume the customer received it.
    /// </summary>
    public bool EmailSent { get; set; }
}

/// <summary>What provisioning actually created inside the tenant's workspace.</summary>
public class TenantBaselineDto
{
    public bool QuoteConfiguration { get; set; }
    public string? BaseCurrency { get; set; }
    public int UnitsOfMeasure { get; set; }
    public int Roles { get; set; }
    public int PermissionGrants { get; set; }
    public string? LeadReferencePrefix { get; set; }
}

/// <summary>The tenant's commercial posture at creation, with any revenue risk named out loud.</summary>
public class TenantBillingPostureDto
{
    public string Mode { get; set; } = nameof(TenantBillingMode.Billable);
    public string? PlanCode { get; set; }
    public string? RateCardCode { get; set; }
    public DateTime? BillingStartsOn { get; set; }
    public DateTime? TrialEndsOn { get; set; }

    /// <summary>
    /// Human-readable revenue risks — an unpriced plan, an unpinned rate card, a trial with no
    /// conversion date. Empty means this tenant will be charged exactly as intended.
    /// </summary>
    public List<string> Warnings { get; set; } = new();
}

public class TenantStatusChangeRequest
{
    [Required]
    public string Reason { get; set; } = null!;
}

public class TenantAiPolicyDto
{
    public long BusinessUnitId { get; set; }
    public bool IsEnabled { get; set; }
    public bool ExternalProcessingAllowed { get; set; }
    public string[] AllowedPurposes { get; set; } = [];
    public string? AllowedProvider { get; set; }
    public string? AllowedModel { get; set; }
    public long? MonthlySoftTokenLimit { get; set; }
    public long? MonthlyHardTokenLimit { get; set; }
    public long Version { get; set; }
    public DateTime UpdatedOn { get; set; }
    public string UpdatedBy { get; set; } = null!;
}

public class UpdateTenantAiPolicyRequest
{
    public bool IsEnabled { get; set; } = true;
    public bool ExternalProcessingAllowed { get; set; }
    public string[] AllowedPurposes { get; set; } = [];
    public string? AllowedProvider { get; set; }
    public string? AllowedModel { get; set; }
    public long? MonthlySoftTokenLimit { get; set; }
    public long? MonthlyHardTokenLimit { get; set; }
    public long Version { get; set; }

    [Required]
    public string Reason { get; set; } = null!;
}

// ---- Impersonation -------------------------------------------------------

public class ImpersonationRequest
{
    [Required]
    public string Reason { get; set; } = null!;
}

public class ImpersonationResponse
{
    public long TenantId { get; set; }
    public long BusinessUnitId { get; set; }
    public string Token { get; set; } = null!;
    public bool ReadOnly { get; set; } = true;
    public DateTime ExpiresAtUtc { get; set; }

    /// <summary>
    /// The revocable session id — pass to POST /api/platform/impersonation/{jti}/revoke.
    /// Surfaced directly so the console never has to decode the JWT to end a session.
    /// </summary>
    public string Jti { get; set; } = null!;
}

public class ImpersonationSessionDto
{
    public string Jti { get; set; } = null!;
    public long TenantId { get; set; }
    public string? TenantName { get; set; }
    public long ActorPlatformUserId { get; set; }
    public string? ActorEmail { get; set; }
    public string Reason { get; set; } = null!;
    public DateTime IssuedAtUtc { get; set; }
    public DateTime ExpiresAtUtc { get; set; }
    public DateTime? RevokedAtUtc { get; set; }
    public string? RevokedBy { get; set; }

    /// <summary>"active" | "expired" | "revoked".</summary>
    public string Status { get; set; } = null!;
}

// ---- Tenant plan assignment ----------------------------------------------

public class ChangeTenantPlanRequest
{
    [Required]
    public long PlanId { get; set; }

    public string? Reason { get; set; }
}

// ---- Platform users ------------------------------------------------------

public class PlatformUserDto
{
    public long Id { get; set; }
    public string Email { get; set; } = null!;
    public string PlatformRole { get; set; } = null!;
    public bool IsActive { get; set; }
    public string? DisplayName { get; set; }
    public DateTime? LastLogin { get; set; }
    public DateTime CreatedOn { get; set; }
}

public class CreatePlatformUserRequest
{
    [Required, EmailAddress]
    public string Email { get; set; } = null!;

    [Required, MinLength(12)]
    public string Password { get; set; } = null!;

    /// <summary>One of the <see cref="Models.PlatformRole"/> names.</summary>
    [Required]
    public string Role { get; set; } = null!;

    public string? DisplayName { get; set; }
}

public class ChangePlatformUserRoleRequest
{
    /// <summary>One of the <see cref="Models.PlatformRole"/> names.</summary>
    [Required]
    public string Role { get; set; } = null!;
}

public class ResetPlatformUserPasswordRequest
{
    [Required, MinLength(12)]
    public string NewPassword { get; set; } = null!;
}

// ---- Plans ---------------------------------------------------------------

public class UpsertPlanRequest
{
    [Required]
    public string Code { get; set; } = null!;

    [Required]
    public string Name { get; set; } = null!;

    [Range(1, 1000)]
    public int Weight { get; set; } = 1;

    [Range(1, 1000)]
    public int MaxConcurrentExtractionJobs { get; set; } = 2;

    [Range(0, int.MaxValue)]
    public int MaxDocsPerMonth { get; set; } = 1000;

    [Range(0, int.MaxValue)]
    public int MaxSeats { get; set; } = 5;

    [Range(0, 99999999.99)]
    public decimal? MonthlyPriceUsd { get; set; }

    /// <summary>JSON object of feature entitlements; defaults to "{}".</summary>
    public string? Features { get; set; }

    public bool IsActive { get; set; } = true;
}
