using System.ComponentModel.DataAnnotations;

namespace ERP_RFQ_Automation.Platform.Models;

// ---- Auth ----------------------------------------------------------------

public class PlatformLoginRequest
{
    [Required, EmailAddress]
    public string Email { get; set; } = null!;

    [Required]
    public string Password { get; set; } = null!;

    /// <summary>
    /// The opaque "remember this browser" token this browser was handed the last time its owner
    /// completed an MFA challenge here. Presented on login so an operator who has already proved a
    /// second factor on this machine today is not challenged again for it.
    ///
    /// <para>It is a bearer credential and the server holds only its SHA-256 hash
    /// (<c>PlatformBrowserTrust</c>). An absent, unknown, expired or revoked value is simply
    /// ignored — it can never do anything except SKIP a challenge for the user it belongs to, and
    /// never substitute for the password above.</para>
    /// </summary>
    public string? BrowserTrustToken { get; set; }
}

public class PlatformLoginResponse
{
    public long Id { get; set; }
    public string Email { get; set; } = null!;
    public string PlatformRole { get; set; } = null!;
    public string? Token { get; set; }
    public DateTime? ExpiresAtUtc { get; set; }
    public bool MfaRequired { get; set; }
    public Guid? MfaChallengeId { get; set; }
    public DateTime? MfaChallengeExpiresAtUtc { get; set; }

    /// <summary>
    /// Sec-D2: true when this token was issued WITHOUT a second factor because the operator has
    /// never enrolled. The token is real but satisfies only
    /// <c>PlatformPolicies.Enrollment</c> — every other platform endpoint answers 403 until
    /// <c>POST /api/platform/auth/mfa/enrollment/confirm</c> succeeds.
    ///
    /// <para>It is on the login response rather than left for the client to infer from a 403,
    /// because a control the user cannot see is a control they cannot satisfy: without this flag
    /// the first-run operator's only signal is every screen failing at once, with nothing saying
    /// which one to open. The client routes on this to the enrollment screen.</para>
    /// </summary>
    public bool MfaEnrollmentRequired { get; set; }

    /// <summary>
    /// True when this session was issued WITHOUT a second factor because the server-authoritative
    /// platform MFA policy currently permits it (OPTIONAL or DISABLED_TEST_ONLY). It is a REPORT of
    /// a backend decision, never an instruction to the backend: the console renders a banner from
    /// it, and every authorization decision is made again on the server regardless of what the
    /// console does with it.
    /// </summary>
    public bool MfaEnforcementRelaxed { get; set; }

    /// <summary>The raw "remember this browser" token, present exactly once — on the response to the
    /// challenge that created it. It is never stored server-side in this form and never returned
    /// again.</summary>
    public string? BrowserTrustToken { get; set; }

    public DateTime? BrowserTrustExpiresAtUtc { get; set; }

    /// <summary>True when this session's second factor came from a browser that had already been
    /// challenged inside its trust window, rather than from a code entered now.</summary>
    public bool BrowserTrustUsed { get; set; }

    /// <summary>
    /// Whether "remember this browser" is on offer for the challenge this response carries, decided
    /// by the server from the platform policy row.
    ///
    /// <para>It rides on the CHALLENGE response because that is the only place the console can learn
    /// it. At the challenge step the operator holds no platform token, so the effective-policy
    /// endpoint is unreachable to them; a checkbox rendered on a guess would offer a control the
    /// platform has switched off, and the operator would tick it and be challenged again tomorrow
    /// with nothing explaining why.</para>
    /// </summary>
    public bool BrowserTrustOffered { get; set; }

    /// <summary>How long the offer above is good for, in hours — so the checkbox can say "don't ask
    /// again on this browser for 30 days" rather than making a promise it has not read. Zero when
    /// nothing is offered.</summary>
    public int BrowserTrustHours { get; set; }

    [System.Text.Json.Serialization.JsonIgnore]
    public bool RecoveryCodeUsed { get; set; }
}

public sealed class PlatformMfaChallengeRequest
{
    [Required]
    public Guid ChallengeId { get; set; }
    public string? TotpCode { get; set; }
    public string? RecoveryCode { get; set; }

    /// <summary>Issue a browser-trust token alongside the session, so ordinary navigation on this
    /// machine costs one challenge for the configured window instead of one per session.</summary>
    public bool RememberBrowser { get; set; }
}

public sealed record PlatformMfaStatusResponse(bool Enabled, DateTime? EnabledAtUtc, int RecoveryCodesRemaining);

/// <summary>
/// The one response that carries a live TOTP enrolment seed. It is served exactly once, to the
/// operator who is enrolling, and it must reach no other surface.
/// </summary>
public sealed record PlatformMfaEnrollmentStartResponse(string Secret, string OtpAuthUri)
{
    /// <summary>
    /// SEC-G9. A record's compiler-generated ToString prints EVERY property, so a single
    /// <c>_logger.LogInformation("enrolment started {Response}", response)</c> would write the
    /// operator's TOTP seed to the log — and a seed in a log is a second factor that is no longer
    /// a factor, because anyone with log access can generate the codes. <see cref="OtpAuthUri"/>
    /// is redacted with it and is not a lesser secret: the otpauth:// URI EMBEDS the same seed
    /// in its query string, which is exactly how the QR code carries it. Redacted at the source
    /// rather than by trusting every future call site, matching the same override on
    /// <c>IssuedTenantAdminInvitation</c> and <c>ProvisioningSubmitResult</c>.
    /// </summary>
    public override string ToString() =>
        "PlatformMfaEnrollmentStartResponse { Secret = [redacted], OtpAuthUri = [redacted] }";
}

public sealed class PlatformMfaEnrollmentConfirmRequest
{
    [Required, RegularExpression("^[0-9]{6}$")]
    public string TotpCode { get; set; } = null!;
}

public sealed record PlatformMfaEnrollmentConfirmResponse(
    DateTime EnabledAtUtc, IReadOnlyList<string> RecoveryCodes)
{
    /// <summary>
    /// SEC-G9, and the same defect as on the enrolment start response. Recovery codes bypass the
    /// second factor by design, so a logged list is a standing set of single-use passwords for the
    /// platform-owner plane. The COUNT is kept because it is what an operator diagnosing an
    /// enrolment actually needs, and it is already public on <c>PlatformMfaStatusResponse</c>.
    /// </summary>
    public override string ToString() =>
        $"PlatformMfaEnrollmentConfirmResponse {{ EnabledAtUtc = {EnabledAtUtc:O}, "
        + $"RecoveryCodes = [redacted, {RecoveryCodes?.Count ?? 0} issued] }}";
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

    // Deployment profile. On the LIST payload as well as the detail payload, deliberately: a
    // tenant on a relaxed profile is a tenant whose activation decision means something weaker
    // than the others on the same screen, and that has to be legible without opening it.
    /// <summary>One of <see cref="TenantDeploymentProfiles"/>: PRODUCTION, LOCAL_TEST or DEMO.</summary>
    public string DeploymentProfile { get; set; } = TenantDeploymentProfiles.Production;
    public string? DeploymentProfileReason { get; set; }
    public string? DeploymentProfileApprovedBy { get; set; }
    public DateTime? DeploymentProfileApprovedOn { get; set; }
}

/// <summary>
/// Moves a tenant between deployment profiles.
///
/// <para>Its own request and its own Owner-gated route rather than a field on
/// <see cref="UpdateTenantProfileRequest"/>, for the same reason the data region is: the profile
/// form is Owner-or-SupportAdmin and describes the customer, while this decides which production
/// controls that customer's workspace is allowed to defer. Putting it on the same form would have
/// made a control-relaxation decision available to whoever was correcting a postcode.</para>
/// </summary>
public sealed class SetTenantDeploymentProfileRequest
{
    /// <summary>One of <see cref="TenantDeploymentProfiles"/>. Unrecognised values are refused.</summary>
    [Required, StringLength(16)]
    public string Profile { get; set; } = null!;

    /// <summary>
    /// Required for every profile other than PRODUCTION, and audited. Returning to PRODUCTION
    /// needs no justification — tightening a gate never does.
    /// </summary>
    [StringLength(1000)]
    public string? Reason { get; set; }
}

/// <summary>
/// Editable customer identity fields. Commercial terms, slug, residency and base currency are
/// intentionally absent: each has its own governed workflow or becomes immutable commercial
/// lineage after provisioning. Null/blank optional values clear the corresponding profile field.
/// </summary>
public sealed class UpdateTenantProfileRequest
{
    [Required, StringLength(256)]
    public string Name { get; set; } = null!;

    [StringLength(256)] public string? LegalName { get; set; }
    [StringLength(64)] public string? RegistrationNumber { get; set; }
    [StringLength(64)] public string? TaxNumber { get; set; }
    [StringLength(2, MinimumLength = 2)] public string? CountryCode { get; set; }
    [StringLength(128)] public string? Industry { get; set; }
    [Url, StringLength(512)] public string? Website { get; set; }
    [StringLength(256)] public string? AddressLine1 { get; set; }
    [StringLength(256)] public string? AddressLine2 { get; set; }
    [StringLength(128)] public string? City { get; set; }
    [StringLength(128)] public string? StateProvince { get; set; }
    [StringLength(32)] public string? PostalCode { get; set; }
    [StringLength(64)] public string? Phone { get; set; }
    [EmailAddress, StringLength(320)] public string? ContactEmail { get; set; }
    [Url, StringLength(1024)] public string? LogoUrl { get; set; }
    [StringLength(128)] public string? TimeZoneId { get; set; }
    [StringLength(35)] public string? Locale { get; set; }

    [Required, StringLength(1000, MinimumLength = 3)]
    public string Reason { get; set; } = null!;
}

/// <summary>
/// Corrects the contractual data region. Deliberately its OWN request rather than a field on
/// <see cref="UpdateTenantProfileRequest"/>: the profile form is Owner-or-SupportAdmin and describes
/// the customer, while this asserts where their data physically is, gates tenant activation, and is
/// Owner-only. Putting it on the same form would have made a contractual residency claim editable by
/// whoever was correcting a postcode.
/// </summary>
public sealed class UpdateTenantDataRegionRequest
{
    /// <summary>The region the tenant's registered data assets are actually in. Null clears it,
    /// which is only accepted while the tenant is still Provisioning.</summary>
    [StringLength(32)]
    public string? DataRegion { get; set; }

    [Required, StringLength(1000, MinimumLength = 15)]
    public string Reason { get; set; } = null!;
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

    /// <summary>
    /// One of <see cref="TenantDeploymentProfiles"/>. Omitted means PRODUCTION, which is the only
    /// safe default: a profile is what decides whether catalogued production prerequisites may be
    /// deferred, and defaulting to anything else would hand out a relaxed gate to a typo.
    ///
    /// <para><b>Why it is on the provisioning request at all.</b> Every tenant was born
    /// PRODUCTION, so a demo or an internal test workspace — which has no customer, no contract
    /// and no third-party estate to point at — was created into the strictest profile the product
    /// has and then had to be walked back through a separate Owner endpoint on a screen the
    /// operator had not opened yet. The result was a test tenant sitting in Provisioning behind
    /// controls that were never meant to apply to it. Saying so at creation is the same decision,
    /// taken at the moment the operator actually knows the answer.</para>
    ///
    /// <para><b>It grants nothing the profile endpoint does not.</b> Anything other than
    /// PRODUCTION is Owner-only and requires <see cref="DeploymentProfileReason"/>, enforced in
    /// the controller — provisioning submit is a TenantAdmin endpoint, and without that check this
    /// field would be a SupportAdmin's route to a deferral they cannot otherwise obtain.</para>
    /// </summary>
    [StringLength(16)]
    public string? DeploymentProfile { get; set; }

    /// <summary>
    /// Required for every profile other than PRODUCTION, minimum 15 characters, recorded as the
    /// approval reason on the tenant and in the provisioning audit trail — the same three facts
    /// (who, when, why) <c>DeploymentProfilePolicy.IsApproved</c> demands before a DEMO tenant
    /// defers anything.
    /// </summary>
    [StringLength(1000)]
    public string? DeploymentProfileReason { get; set; }

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
    //
    // Provisioning still creates exactly ONE account, and that remains the point: the
    // customer's own Super Administrator is meant to staff the workspace from there. A
    // product decision has since added a narrow platform-plane path for adding further
    // accounts (TenantUsersController, POST /api/platform/tenants/{id}/users) for the
    // pilot case where a second person must be in the workspace before the founding
    // administrator has ever signed in. It is the secondary door, it invites rather than
    // issues credentials, and it is audited as an operator reaching into a customer's
    // tenant — see that controller for the whole reasoning.

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

    /// <summary>
    /// Lead, RFQ, quote, order and payment lifecycle rows the baseline seeder had to write. Zero on
    /// the normal path, where <c>TenantsController.Provision</c> and
    /// <c>ProvisioningStepExecutor</c>'s lifecycle-statuses step already wrote them. A NON-zero
    /// figure is the signal worth reading: it means the workspace reached the seeder without the
    /// states its own quote and order screens resolve, and the seeder repaired it.
    /// </summary>
    public int LifecycleStatuses { get; set; }

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
    public long? MaxTokensPerDocument { get; set; }
    public decimal? ExternalInputCostPerMillionTokens { get; set; }
    public decimal? ExternalOutputCostPerMillionTokens { get; set; }
    public string? ExternalCostCurrency { get; set; }
    public string? ExternalPricingVersion { get; set; }
    public decimal ExternalDependencyCeilingPercent { get; set; }
    public bool RedactionRequired { get; set; }
    public string AllowedDataClassifications { get; set; } = null!;
    public string EgressPolicy { get; set; } = null!;
    public string DataResidency { get; set; } = null!;
    public int RetentionDays { get; set; }
    public bool InputOutputAuditAllowed { get; set; }
    public bool PrivacyReviewRequired { get; set; }
    public decimal? LocalComputeCostPerHour { get; set; }
    public decimal? OcrCostPerPage { get; set; }
    public string? LocalCostCurrency { get; set; }
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
    public long? MaxTokensPerDocument { get; set; }
    public decimal? ExternalInputCostPerMillionTokens { get; set; }
    public decimal? ExternalOutputCostPerMillionTokens { get; set; }
    public string? ExternalCostCurrency { get; set; }
    public string? ExternalPricingVersion { get; set; }
    public decimal ExternalDependencyCeilingPercent { get; set; }
    public bool RedactionRequired { get; set; } = true;
    public string AllowedDataClassifications { get; set; } = "Public,Internal";
    public string EgressPolicy { get; set; } = "RedactedFieldsOnly";
    public string DataResidency { get; set; } = "TenantApprovedRegion";
    public int RetentionDays { get; set; } = 30;
    public bool InputOutputAuditAllowed { get; set; }
    public bool PrivacyReviewRequired { get; set; } = true;
    public decimal? LocalComputeCostPerHour { get; set; }
    public decimal? OcrCostPerPage { get; set; }
    public string? LocalCostCurrency { get; set; }
    public long Version { get; set; }

    [Required]
    public string Reason { get; set; } = null!;
}

// ---- Impersonation -------------------------------------------------------

public class ImpersonationRequest
{
    [Required, StringLength(1000, MinimumLength = 1)]
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

// ---- Tenant module & capability grants -----------------------------------

/// <summary>
/// One row of the tenant Modules screen: a catalogue key, whether THIS customer has it, and the
/// two facts that decide whether the switch is even meaningful.
/// </summary>
/// <param name="Key">Closed <c>TypedEntitlementCatalog</c> key, e.g. <c>module.orders</c>.</param>
/// <param name="Enabled">What this tenant is granted right now.</param>
/// <param name="Available">
/// Whether a production execution boundary exists for the key. Five catalogue keys — API access,
/// Automation, SSO, SCIM and dedicated resources — are declared
/// <c>RuntimeUnavailableBoundary</c>: the server denies them even when the grant says true,
/// because the product surface does not exist yet. Sending this lets the console show them as
/// "not built" instead of offering a switch that grants nothing, which is the "toggle exists,
/// capability does not" pattern this codebase has shipped before.
/// </param>
/// <param name="FromPlanTemplate">
/// What the tenant's plan declares for this key. Advisory only — the plan is no longer the
/// authority (20260818013530) — but it is what the console shows as "differs from plan", which is
/// how an operator spots a deliberate exception a year after making it.
/// </param>
public sealed record TenantModuleGrantDto(
    string Key,
    bool Enabled,
    bool Available,
    bool? FromPlanTemplate);

/// <summary>The whole Modules screen in one read.</summary>
public sealed record TenantModulesDto(
    long TenantId,
    string TenantName,
    long? PlanId,
    string? PlanCode,
    IReadOnlyList<TenantModuleGrantDto> Modules);

/// <summary>
/// Replace this tenant's module grants wholesale.
///
/// <para>Wholesale rather than a patch of changed keys, deliberately. A partial write cannot say
/// "off" and "undecided" apart, and the activation control
/// <c>entitlements.typed-hard-limits</c> requires every key to be DECIDED — so a patch API would
/// let an operator leave a customer permanently unactivatable through a screen that looked like
/// it had saved. The server completes the set from the closed catalogue on the way in.</para>
/// </summary>
public class UpdateTenantModulesRequest
{
    /// <summary>Catalogue key → granted. Unknown keys are refused; absent keys are stored false.</summary>
    [Required]
    public Dictionary<string, bool> Modules { get; set; } = new();

    /// <summary>
    /// Why. Required, and required at a length that cannot be satisfied by "x": revoking a module
    /// from a live customer removes access to work they may be in the middle of, and an audit row
    /// reading "update" explains nothing to whoever finds it during a dispute.
    /// </summary>
    [Required]
    [MinLength(TenantModuleGrantRules.MinimumReasonLength)]
    [MaxLength(1000)]
    public string Reason { get; set; } = null!;
}

/// <summary>Shared between the request validation and the console, so both agree on the bound.</summary>
public static class TenantModuleGrantRules
{
    public const int MinimumReasonLength = 15;
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
