namespace ERP_RFQ_Automation.DTOs.Lead
{
    // Row shape for the extraction "needs review" workbench list.
    public class LeadNeedsReviewItemDTO
    {
        public long Id { get; set; }
        public string? Rfqno { get; set; }
        public string? BuyersName { get; set; }
        public DateTime RecDate { get; set; }
        public DateTime? BidClosingDate { get; set; }
        public string LeadSource { get; set; } = null!;
        public decimal? Aiconfidence { get; set; }
        public int ItemCount { get; set; }
        public string? ReviewReason { get; set; }
        public DateTime? ReceivedOn { get; set; }
        public long ReviewVersion { get; set; }

        /// <summary>
        /// Lines on this document that a human still has to look at, from the evidence
        /// ledger's own per-line validation status — the SAME verdict the review screen
        /// renders, so the queue and the workbench can never disagree.
        ///
        /// <para>
        /// The client has typed and rendered this since the needs-check count replaced the
        /// confidence percentage, and nothing served it: a reader with no writer, so every
        /// row fell back to the bare line count. Null stays meaningful — a document whose
        /// path wrote no ledger has no per-line verdict, and the queue shows the line count
        /// alone rather than inventing a figure.
        /// </para>
        /// </summary>
        public int? LinesNeedingCheck { get; set; }
    }
}
