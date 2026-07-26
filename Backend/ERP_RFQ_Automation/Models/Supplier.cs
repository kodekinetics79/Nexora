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
