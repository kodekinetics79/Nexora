namespace ERP_RFQ_Automation.Repositories
{
    /// <summary>
    /// The column-header filters on the RFQs list (owner 2026-09-30: "move on to RFQ", after the
    /// Leads list got a filter on every field). Text columns match "contains"; date columns take an
    /// inclusive day range (either side optional) or, for the bid due date, a preset window; Lines
    /// a min/max; Status, RFQ type, Inquiry type and Bidding decision one value; Quote the state of
    /// the RFQ's quote. Applied on the server before the count, so rows, count and Excel agree.
    /// </summary>
    public sealed record RfqListColumnFilters
    {
        public string? Customer { get; init; }          // customer id, or "none"
        public string? Rfq { get; init; }
        public string? Serial { get; init; }
        public string? CustomerRef { get; init; }
        public string? Email { get; init; }
        public string? Buyer { get; init; }
        public string? AccountOwner { get; init; }
        public string? Location { get; init; }
        public string? Agreement { get; init; }
        public string? Opportunity { get; init; }
        public string? PromotedBy { get; init; }
        public string? RfqType { get; init; }
        public string? InquiryType { get; init; }
        public string? Bidding { get; init; }
        public long? StatusId { get; init; }
        public string? Quote { get; init; }             // none | draft | sent
        public string? Due { get; init; }               // overdue | 7d | 14d
        public DateTime? Today { get; init; }
        public DateTime? DueFrom { get; init; }
        public DateTime? DueTo { get; init; }
        public DateTime? ReceivedFrom { get; init; }
        public DateTime? ReceivedTo { get; init; }
        public DateTime? RequiredFrom { get; init; }
        public DateTime? RequiredTo { get; init; }
        public DateTime? SubmittedFrom { get; init; }
        public DateTime? SubmittedTo { get; init; }
        public DateTime? CreatedFrom { get; init; }
        public DateTime? CreatedTo { get; init; }
        public DateTime? ModifiedFrom { get; init; }
        public DateTime? ModifiedTo { get; init; }
        public int? LinesMin { get; init; }
        public int? LinesMax { get; init; }
    }
}
