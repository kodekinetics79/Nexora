using System;
using System.Collections.Generic;

namespace ERP_RFQ_Automation.Models;

public partial class SetupMaster
{
    public long SetupId { get; set; }

    public string SetupType { get; set; } = null!;

    public string? SetupCode { get; set; }

    public string SetupValue { get; set; } = null!;

    public string? Description { get; set; }

    public long? ParentSetupId { get; set; }

    public long BusinessUnitId { get; set; }

    /// <summary>
    /// Authority tier for rows whose <see cref="SetupType"/> is a role — see
    /// <c>ERP_RFQ_Automation.Authorization.RoleRanks</c>. Non-role rows keep 0 and ignore it.
    /// This column, and NOT <see cref="SetupCode"/>/<see cref="SetupValue"/>, is what
    /// <c>IRoleGate</c> reads to decide super-admin/manager status.
    /// </summary>
    public short RoleRank { get; set; }

    public bool? IsActive { get; set; }

    public string CreatedBy { get; set; } = null!;

    public DateTime CreatedOn { get; set; }

    public string? ModifiedBy { get; set; }

    public DateTime? ModifiedOn { get; set; }

    public virtual BusinessUnit BusinessUnit { get; set; } = null!;

    public virtual ICollection<SetupMaster> InverseParentSetup { get; set; } = new List<SetupMaster>();

    public virtual ICollection<Lead> LeadLeadRejectedReasons { get; set; } = new List<Lead>();

    public virtual ICollection<Lead> LeadLeadStatuses { get; set; } = new List<Lead>();

    public virtual ICollection<Order> OrderPaymentMethods { get; set; } = new List<Order>();

    public virtual ICollection<Order> OrderPaymentStatuses { get; set; } = new List<Order>();

    public virtual ICollection<Order> OrderStatuses { get; set; } = new List<Order>();

    public virtual SetupMaster? ParentSetup { get; set; }

    public virtual ICollection<Quote> QuoteDiscountTypes { get; set; } = new List<Quote>();

    public virtual ICollection<QuoteItem> QuoteItems { get; set; } = new List<QuoteItem>();

    public virtual ICollection<Quote> QuoteStatuses { get; set; } = new List<Quote>();

    public virtual ICollection<Rfq> RfqRfqstatuses { get; set; } = new List<Rfq>();

    public virtual ICollection<Rfq> RfqRfqtypeNavigations { get; set; } = new List<Rfq>();

    public virtual ICollection<RolePermission> RolePermissions { get; set; } = new List<RolePermission>();

    public virtual ICollection<ShipmentStatusHistory> ShipmentStatusHistoryNewStatuses { get; set; } = new List<ShipmentStatusHistory>();

    public virtual ICollection<ShipmentStatusHistory> ShipmentStatusHistoryPreviousStatuses { get; set; } = new List<ShipmentStatusHistory>();

    public virtual ICollection<Shipment> Shipments { get; set; } = new List<Shipment>();

    public virtual ICollection<User> Users { get; set; } = new List<User>();
}
