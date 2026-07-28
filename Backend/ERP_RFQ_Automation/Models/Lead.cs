using System;
using System.Collections.Generic;

namespace ERP_RFQ_Automation.Models;

public partial class Lead
{
    public long Id { get; set; }

    public string? Rfqno { get; set; }

    public string? BuyersName { get; set; }

    public DateTime RecDate { get; set; }

    public DateTime? BidClosingDate { get; set; }

    public string? BiddingDecision { get; set; }

    public DateTime? AcknowledgmentDate { get; set; }

    public DateTime? SubDate { get; set; }

    public string? HeaderRemarks { get; set; }

    public string? OpportunityNo { get; set; }

    public int? NoOfLineItems { get; set; }

    public string? Rfqtype { get; set; }

    public string? DurationAgreement { get; set; }

    public string LeadSource { get; set; } = null!;

    public decimal? Aiconfidence { get; set; }

    public string CreatedBy { get; set; } = null!;

    public DateTime CreatedDate { get; set; }

    public long BusinessUnitId { get; set; }

    public long? EmailIngestsId { get; set; }

    public DateTime? ModifiedDate { get; set; }

    public string? EmailSource { get; set; }

    public string? Clientemail { get; set; }

    public long? LeadStatusId { get; set; }

    public long? AssignTo { get; set; }

    public DateTime? AssignOn { get; set; }

    public string? AssignComment { get; set; }

    public long? LeadRejectedReasonId { get; set; }

    public virtual User? AssignToNavigation { get; set; }

    public virtual BusinessUnit BusinessUnit { get; set; } = null!;

    public virtual EmailIngest? EmailIngests { get; set; }

    public virtual ICollection<LeadItem> LeadItems { get; set; } = new List<LeadItem>();

    public virtual SetupMaster? LeadRejectedReason { get; set; }

    public virtual SetupMaster? LeadStatus { get; set; }

    public virtual ICollection<Order> Orders { get; set; } = new List<Order>();

    public virtual ICollection<Rfq> Rfqs { get; set; } = new List<Rfq>();
}
