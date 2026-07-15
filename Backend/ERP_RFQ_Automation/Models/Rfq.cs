using System;
using System.Collections.Generic;

namespace ERP_RFQ_Automation.Models;

public partial class Rfq
{
    public long Id { get; set; }

    public string Rfqno { get; set; } = null!;

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

    public long? RfqtypeId { get; set; }

    public string? DurationAgreement { get; set; }

    public long? LeadId { get; set; }

    public string CreatedBy { get; set; } = null!;

    public DateTime CreatedDate { get; set; }

    public string? ModifiedBy { get; set; }

    public DateTime? ModifiedDate { get; set; }

    public long BusinessUnitId { get; set; }

    public long? RfqstatusId { get; set; }

    public long? CustomerId { get; set; }

    public virtual BusinessUnit BusinessUnit { get; set; } = null!;

    public virtual Customer? Customer { get; set; }

    public virtual Lead? Lead { get; set; }

    public virtual ICollection<Order> Orders { get; set; } = new List<Order>();

    public virtual ICollection<Quote> Quotes { get; set; } = new List<Quote>();

    public virtual ICollection<Rfqitem> Rfqitems { get; set; } = new List<Rfqitem>();

    public virtual SetupMaster? Rfqstatus { get; set; }

    public virtual SetupMaster? RfqtypeNavigation { get; set; }
}
