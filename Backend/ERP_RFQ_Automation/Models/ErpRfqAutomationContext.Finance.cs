using ERP_RFQ_Automation.CommercialFinance;
using ERP_RFQ_Automation.GeneralLedger;
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
    public DbSet<FinanceCommunicationContact> FinanceCommunicationContacts => Set<FinanceCommunicationContact>();
    public DbSet<CustomerStatement> CustomerStatements => Set<CustomerStatement>();
    public DbSet<CustomerStatementLine> CustomerStatementLines => Set<CustomerStatementLine>();
    public DbSet<DunningPolicy> DunningPolicies => Set<DunningPolicy>();
    public DbSet<DunningPolicyStep> DunningPolicySteps => Set<DunningPolicyStep>();
    public DbSet<CustomerCollectionProfile> CustomerCollectionProfiles => Set<CustomerCollectionProfile>();
    public DbSet<CollectionControl> CollectionControls => Set<CollectionControl>();
    public DbSet<DunningCase> DunningCases => Set<DunningCase>();
    public DbSet<PromiseToPay> PromisesToPay => Set<PromiseToPay>();
    public DbSet<DunningRun> DunningRuns => Set<DunningRun>();
    public DbSet<DunningRunDecision> DunningRunDecisions => Set<DunningRunDecision>();
    public DbSet<DunningNotice> DunningNotices => Set<DunningNotice>();
    public DbSet<DunningDeliveryAttempt> DunningDeliveryAttempts => Set<DunningDeliveryAttempt>();
    public DbSet<LedgerAccount> LedgerAccounts => Set<LedgerAccount>();
    public DbSet<LedgerBook> LedgerBooks => Set<LedgerBook>();
    public DbSet<AccountingPeriod> AccountingPeriods => Set<AccountingPeriod>();
    public DbSet<JournalEntry> JournalEntries => Set<JournalEntry>();
    public DbSet<JournalEntryLine> JournalEntryLines => Set<JournalEntryLine>();
}
