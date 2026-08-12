using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;

namespace ERP_RFQ_Automation.Models;

public partial class Supplier
{
    public long Id { get; set; }

    public string? DocId { get; set; }

    public string Name { get; set; } = null!;

    public string? ContactEmail { get; set; }

    public string ImageUrl { get; set; } = null!;

    public string? PaymentTerms { get; set; }

    public string? AddressLine1 { get; set; }

    public string? AddressLine2 { get; set; }

    public string? PostalCode { get; set; }

    public decimal? SuccessRate { get; set; }

    public int? AvgResponseTime { get; set; }

    public string? Tags { get; set; }

    public string? Comments { get; set; }

    public long? CurrencyId { get; set; }

    public long? Buid { get; set; }

    public bool? IsActive { get; set; }

    public string GovernanceStatus { get; set; } = SupplierGovernanceStatuses.Unverified;

    public string VerificationStatus { get; set; } = SupplierGovernanceUnknown.Unknown;

    public string ComplianceStatus { get; set; } = SupplierGovernanceUnknown.Unknown;

    public string RiskStatus { get; set; } = SupplierGovernanceUnknown.Unknown;

    public string ReadinessStatus { get; set; } = SupplierReadinessStatuses.ReviewRequired;

    /// <summary>
    /// The customer's commercial classification of this supplier — one of
    /// <see cref="SupplierTiers"/>. Null means NOT YET CLASSIFIED, which is the state every existing
    /// supplier starts in and a legitimate resting state; the CHECK constraint permits it.
    ///
    /// <para>Tier annotates, orders and pre-selects. It NEVER gates: dispatch eligibility stays with
    /// the governance check in <c>SupplierNegotiationService</c>, and tier enters no blocker list, no
    /// eligibility predicate and no award guard. The two are different axes — a compliant brand-new
    /// supplier is legitimately Tier 3, and a Tier 1 partner with a lapsed CR is legitimately
    /// blocked — and collapsing them would make a commercial preference read as a compliance
    /// verdict.</para>
    ///
    /// <para>Customer-set. Nothing derives it: no spend bands, no auto-promotion, no sync with
    /// <see cref="GovernanceStatus"/>. It also never enters the weighted comparison score.</para>
    /// </summary>
    public string? Tier { get; set; }

    /// <summary>
    /// The numeric companion to the free-text <see cref="PaymentTerms"/>: how many days of credit
    /// this supplier extends. The customer types <c>30</c>; the free text keeps the human wording
    /// ("30 days net from invoice date").
    ///
    /// <para>Null means NOT CONFIGURED, not zero. Zero is the positive assertion "this supplier is
    /// cash on delivery" and is spelled 0. The weighted supplier comparison treats a missing value
    /// as unscorable rather than imputing one — a number a person typed beats a number a model
    /// guessed.</para>
    /// </summary>
    public int? CreditDays { get; set; }

    public DateTime? EffectiveFrom { get; set; }

    public string? GovernanceReviewedBy { get; set; }

    public DateTime? GovernanceReviewedOn { get; set; }

    [ConcurrencyCheck]
    public Guid? ConcurrencyToken { get; set; }

    public string CreatedBy { get; set; } = null!;

    public DateTime CreatedOn { get; set; }

    public string? ModifiedBy { get; set; }

    public DateTime? ModifiedOn { get; set; }

    public int? CityId { get; set; }

    public int? CountryId { get; set; }

    public virtual BusinessUnit? Bu { get; set; }

    public virtual SetCity? City { get; set; }

    public virtual ICollection<Contact> Contacts { get; set; } = new List<Contact>();

    public virtual SetCountry? Country { get; set; }

    public virtual Currency? Currency { get; set; }

    public virtual ICollection<Product> Products { get; set; } = new List<Product>();

    public virtual ICollection<Rfqitem> Rfqitems { get; set; } = new List<Rfqitem>();

    public virtual ICollection<SupplierPurchaseHistory> SupplierPurchaseHistories { get; set; } = new List<SupplierPurchaseHistory>();

    public virtual ICollection<SupplierQuotedItem> SupplierQuotedItems { get; set; } = new List<SupplierQuotedItem>();
}

public static class SupplierGovernanceStatuses
{
    public const string Discovered = "DISCOVERED";
    public const string Unverified = "UNVERIFIED";
    public const string ReviewRequired = "REVIEW_REQUIRED";
    public const string Provisional = "PROVISIONAL";
    public const string Approved = "APPROVED";
    public const string Preferred = "PREFERRED";
    public const string Restricted = "RESTRICTED";
    public const string Blocked = "BLOCKED";
    public const string Inactive = "INACTIVE";
}

/// <summary>
/// The customer's commercial classification of a supplier (FR-MDM-03, first clause). Three values,
/// set by a master-data administrator and derived by nothing.
///
/// <para>Deliberately NOT a governance axis. <see cref="SupplierGovernanceStatuses"/> answers "may
/// we trade with them"; this answers "how close is the relationship". Keeping them apart is what
/// lets a Tier 1 partner be blocked for a lapsed commercial registration without the tier being
/// silently downgraded, and a brand-new compliant supplier be Tier 3 without being blocked.</para>
/// </summary>
public static class SupplierTiers
{
    /// <summary>A partner we hold a standing commercial relationship with.</summary>
    public const string Tier1Partner = "TIER_1_PARTNER";

    /// <summary>Approved for trade, outside the partner set.</summary>
    public const string Tier2Extended = "TIER_2_EXTENDED";

    /// <summary>Traded with on exception, outside the intended supplier network.</summary>
    public const string Tier3OutOfNetwork = "TIER_3_OUT_OF_NETWORK";

    /// <summary>
    /// Every permitted value, in tier order. The CHECK constraint, the API and the tier select on
    /// the supplier form all read the list from here so none of them can drift from the others.
    /// </summary>
    public static readonly IReadOnlyList<string> All = [Tier1Partner, Tier2Extended, Tier3OutOfNetwork];

    /// <summary>Null (not yet classified) is permitted; anything else must be a listed tier.</summary>
    public static bool IsValid(string? tier) => tier is null || All.Contains(tier);
}

public static class SupplierReadinessStatuses
{
    public const string ReviewRequired = "REVIEW_REQUIRED";
    public const string Ready = "READY";
    public const string Restricted = "RESTRICTED";
    public const string Blocked = "BLOCKED";
}

public static class SupplierGovernanceUnknown
{
    public const string Unknown = "UNKNOWN";
}

public static class SupplierVerificationStatuses
{
    public const string Pending = "PENDING";
    public const string Verified = "VERIFIED";
    public const string Failed = "FAILED";
    public const string Expired = "EXPIRED";
}

public static class SupplierComplianceStatuses
{
    public const string Pending = "PENDING";
    public const string Cleared = "CLEARED";
    public const string Restricted = "RESTRICTED";
    public const string Blocked = "BLOCKED";
    public const string Failed = "FAILED";
}

public static class SupplierRiskStatuses
{
    public const string Low = "LOW";
    public const string Medium = "MEDIUM";
    public const string High = "HIGH";
    public const string Blocked = "BLOCKED";
}
