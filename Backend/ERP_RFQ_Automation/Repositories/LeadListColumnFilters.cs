namespace ERP_RFQ_Automation.Repositories
{
    /// <summary>
    /// The column-header filters on the Leads list (owner 2026-09-29: "give filter on all fields").
    /// Text columns match "contains"; date columns take an inclusive day range (from/to, either
    /// side optional); Items takes a min/max; Status takes a status id or "none" (not opened yet).
    /// Everything is applied on the server, before the count, so the rows, count and Excel agree.
    /// </summary>
    public sealed record LeadListColumnFilters
    {
        public string? Serial { get; init; }
        public string? Rfq { get; init; }
        public string? Buyer { get; init; }
        public string? Agreement { get; init; }
        /// <summary>Ingested window as instants (the page sends its local midnights), matching the cell.</summary>
        public DateTimeOffset? IngestedFrom { get; init; }
        public DateTimeOffset? IngestedBefore { get; init; }
        public DateTime? ReceivedFrom { get; init; }
        public DateTime? ReceivedTo { get; init; }
        public DateTime? DueFrom { get; init; }
        public DateTime? DueTo { get; init; }
        public DateTime? RequiredFrom { get; init; }
        public DateTime? RequiredTo { get; init; }
        public int? ItemsMin { get; init; }
        public int? ItemsMax { get; init; }
        public string? Status { get; init; }

        /// <summary>A wall-clock day, like the columns it is compared with.</summary>
        public static DateTime? Day(DateTime? value) =>
            value.HasValue ? DateTime.SpecifyKind(value.Value.Date, DateTimeKind.Unspecified) : null;
    }
}
