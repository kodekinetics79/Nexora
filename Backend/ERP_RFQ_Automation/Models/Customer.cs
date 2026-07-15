using System;
using System.Collections.Generic;

namespace ERP_RFQ_Automation.Models;

public partial class Customer
{
    public long Id { get; set; }

    public string? DocId { get; set; }

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

    public bool? IsActive { get; set; }

    public string CreatedBy { get; set; } = null!;

    public DateTime CreatedOn { get; set; }

    public string? ModifiedBy { get; set; }

    public DateTime? ModifiedOn { get; set; }

    public virtual BusinessUnit? Bu { get; set; }

    public virtual ICollection<Contact> Contacts { get; set; } = new List<Contact>();

    public virtual ICollection<Order> Orders { get; set; } = new List<Order>();

    public virtual ICollection<Quote> Quotes { get; set; } = new List<Quote>();

    public virtual ICollection<Rfq> Rfqs { get; set; } = new List<Rfq>();
}
