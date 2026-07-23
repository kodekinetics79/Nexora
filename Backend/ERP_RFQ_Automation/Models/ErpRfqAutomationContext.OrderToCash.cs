using ERP_RFQ_Automation.OrderToCash;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.Models;

public partial class ErpRfqAutomationContext
{
    public virtual DbSet<CustomerPurchaseOrder> CustomerPurchaseOrders { get; set; }
    public virtual DbSet<CustomerPurchaseOrderLine> CustomerPurchaseOrderLines { get; set; }
    public virtual DbSet<CustomerAward> CustomerAwards { get; set; }
    public virtual DbSet<CustomerAwardLineAllocation> CustomerAwardLineAllocations { get; set; }
    public virtual DbSet<OrderToCashAuditEvent> OrderToCashAuditEvents { get; set; }
    public virtual DbSet<OrderToCashDocumentCounter> OrderToCashDocumentCounters { get; set; }
}
