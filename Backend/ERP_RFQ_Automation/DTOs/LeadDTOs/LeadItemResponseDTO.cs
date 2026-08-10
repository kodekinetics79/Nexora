namespace ERP_RFQ_Automation.DTOs.Lead
{
    public class LeadItemResponseDTO
    {
        public long Id { get; set; }
        public string? CompanyRef { get; set; }
        public string? CustomerAccountPortalId { get; set; }
        public string? CustomerRfqno { get; set; }
        public string? ItemMaterialCode { get; set; }
        public string? CommodityProduct { get; set; }
        public string? BuyerName { get; set; }
        public string? LineItemNo { get; set; }
        public string? ProductShortName { get; set; }
        public string? Alternative { get; set; }
        public string? ProductShortDescription { get; set; }
        public string? Currency { get; set; }
        public string? UnitOfMeasure { get; set; }
        public decimal? UnitPrice { get; set; }
        /// <summary>
        /// Null when the source document stated no readable quantity. Never 0-for-unknown: the
        /// review screen must show a gap the reviewer has to fill, not a number to approve.
        /// </summary>
        public int? Quantity { get; set; }
        public string? StorageLocation { get; set; }
        public string? ManufacturerName { get; set; }
        public string? ManufacturerPartNumber { get; set; }
        public string? AlternateProductName { get; set; }
        public string? AlternatePartNumber { get; set; }
        public string? ItemText { get; set; }
        public string? MaterialPotext { get; set; }
        public int? LeadTime { get; set; }
        public DateTime? ReceivedDate { get; set; }
        public DateTime? BidClosingDateLine { get; set; }
        public decimal? Aiconfidence { get; set; }

        /// <summary>
        /// Unrecognized customer-document columns captured verbatim at extraction time
        /// ({"original column header": "cell value"}); null when none. Serialized as
        /// camelCase "extraFields" with keys preserved as-is.
        /// </summary>
        public Dictionary<string, string>? ExtraFields { get; set; }

        /// <summary>
        /// AA-01 · tenant-defined custom field values for this lead/RFQ line, as the raw jsonb
        /// object keyed by custom-field stable key. Carried on the lead detail payload so the
        /// line grid can render a custom-field column without a round trip per row. Null when
        /// the tenant has defined no custom fields or this line has no values.
        ///
        /// Deliberately distinct from <see cref="ExtraFields"/>: that is an UNGOVERNED verbatim
        /// capture of the buyer's own column headings; this holds values against fields the
        /// tenant defined, typed and named. One is evidence, the other is a schema.
        /// </summary>
        public string? CustomFields { get; set; }
    }
}
