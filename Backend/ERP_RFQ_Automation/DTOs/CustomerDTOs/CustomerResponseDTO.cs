namespace ERP_RFQ_Automation.DTOs.CustomerDTOs
{
    public class CustomerResponseDTO
    {
        public long Id { get; set; }
        public string Name { get; set; } = null!;
        public string? ContactEmail { get; set; }
        public string ImageUrl { get; set; } = null!;
        public string? BillingAddressLine1 { get; set; }
        public string? BillingAddressLine2 { get; set; }
        public string? BillingCity { get; set; }
        public string? BillingState { get; set; }
        public string? BillingCountry { get; set; }
        public string? BillingPostalCode { get; set; }
        public string? ShippingAddressLine1 { get; set; }
        public string? ShippingAddressLine2 { get; set; }
        public string? ShippingCity { get; set; }
        public string? ShippingState { get; set; }
        public string? ShippingCountry { get; set; }
        public string? ShippingPostalCode { get; set; }
        public long? Buid { get; set; }
        public string? BusinessUnitName { get; set; }
        public bool? IsActive { get; set; }
        public string CreatedBy { get; set; } = null!;
        public DateTime CreatedOn { get; set; }
        public string? ModifiedBy { get; set; }
        public DateTime? ModifiedOn { get; set; }
        public Guid ConcurrencyToken { get; set; }

        public string? DocId { get; set; }

        // ── FR-CST-01 customer master ────────────────────────────────────────
        // Every one of these is NULLABLE and null means "not captured". The client renders a
        // stated gap ("Not captured", "No account team") rather than an empty cell, because a
        // blank in a register reads as a loading state and a register that looks complete when it
        // is not is the whole reason this module was reported as unmet.

        public string? CommercialRegistrationNumber { get; set; }
        public string? TaxRegistrationNumber { get; set; }

        /// <summary>Stored code (GOVERNMENT | SEMI_GOVERNMENT | PRIVATE), not the label. The client
        /// maps it for display; storing the label would let a rename orphan the rows.</summary>
        public string? Sector { get; set; }

        public int? RegionStateId { get; set; }

        /// <summary>Resolved from the tenant's region master so the caller does not need a second
        /// round trip to render the row. It is a projection of <see cref="RegionStateId"/>, never a
        /// substitute for it.</summary>
        public string? RegionName { get; set; }

        /// <summary>FR-CST-02. Null means no account team is assigned, which leaves the record
        /// readable tenant-wide.</summary>
        public long? AccountTeamId { get; set; }

        public string? AccountTeamName { get; set; }

        /// <summary>
        /// AA-01 · tenant-defined custom field values for this customer, as the raw jsonb
        /// object keyed by custom-field stable key. Carried on the list payload so the grid
        /// can render a custom-field column without a second round trip per row. Null when
        /// the tenant has defined no custom fields or this record has no values.
        /// </summary>
        public string? CustomFields { get; set; }
    }
}
