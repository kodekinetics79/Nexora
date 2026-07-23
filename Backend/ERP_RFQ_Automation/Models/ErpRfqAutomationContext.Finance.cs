using ERP_RFQ_Automation.CommercialFinance;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.Models;

public partial class ErpRfqAutomationContext
{
    public DbSet<ReceivableDocument> ReceivableDocuments => Set<ReceivableDocument>();
    public DbSet<ReceivableDocumentLine> ReceivableDocumentLines => Set<ReceivableDocumentLine>();
    public DbSet<CustomerPayment> CustomerPayments => Set<CustomerPayment>();
    public DbSet<PaymentAllocation> PaymentAllocations => Set<PaymentAllocation>();
    public DbSet<LegalDocumentCounter> LegalDocumentCounters => Set<LegalDocumentCounter>();
    public DbSet<CommercialFinanceAudit> CommercialFinanceAudits => Set<CommercialFinanceAudit>();
    public DbSet<FinanceOutboxMessage> FinanceOutboxMessages => Set<FinanceOutboxMessage>();
    public DbSet<ReceivableWriteOff> ReceivableWriteOffs => Set<ReceivableWriteOff>();
    public DbSet<WriteOffAllocation> WriteOffAllocations => Set<WriteOffAllocation>();
    public DbSet<CustomerRefund> CustomerRefunds => Set<CustomerRefund>();
}
