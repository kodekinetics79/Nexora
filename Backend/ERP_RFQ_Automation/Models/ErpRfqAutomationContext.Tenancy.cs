using ERP_RFQ_Automation.MultiTenancy;
using ERP_RFQ_Automation.CommercialRouting;
using ERP_RFQ_Automation.CustomerResolution;
using ERP_RFQ_Automation.Inventory;
using ERP_RFQ_Automation.CustomFields;
using ERP_RFQ_Automation.DocumentIntelligence.Persistence;
using ERP_RFQ_Automation.CommercialCases.Lifecycle;
using ERP_RFQ_Automation.CommercialFinance;
using ERP_RFQ_Automation.OrderToCash;
using ERP_RFQ_Automation.GeneralLedger;
using ERP_RFQ_Automation.BankReconciliation;
using ERP_RFQ_Automation.LeadIdentity;
using ERP_RFQ_Automation.QuoteDelivery;
using ERP_RFQ_Automation.CommercialIntelligence.Sales;
using ERP_RFQ_Automation.Inventory.Commercial;
using ERP_RFQ_Automation.CommercialIntelligence.Exceptions;
using ERP_RFQ_Automation.CommercialIntelligence.Opportunity;
using ERP_RFQ_Automation.CommercialIntelligence.Growth;
using ERP_RFQ_Automation.Platform.Onboarding;
using ERP_RFQ_Automation.Platform.Provisioning;
using ERP_RFQ_Automation.Platform.Lifecycle;
using ERP_RFQ_Automation.Platform.Support;
using ERP_RFQ_Automation.Platform.Notifications;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.Models;

// Tenant isolation via EF Core global query filters (ADR-0005). Implemented in a
// partial so the large scaffolded context file stays untouched. Fail-closed:
// every authenticated request is transparently scoped to its business unit; a
// null tenant (login / anonymous / background worker) applies NO filter so those
// paths keep working. For legitimate cross-tenant reads (platform plane, worker
// sweeps) use .IgnoreQueryFilters().
public partial class ErpRfqAutomationContext
{
    // Set by the tenant-scoping constructor; null on the design-time /
    // parameterless / options-only paths.
    private readonly ITenantContext? _tenant;

    // Null when there is no tenant context -> filters become no-ops.
    private long? CurrentTenantId => _tenant?.BusinessUnitId;
    internal long? ScopedTenantId => CurrentTenantId;

    /// <summary>
    /// Founding-administrator activation links. Platform schema and deliberately UNFILTERED: the
    /// invitation is redeemed by an anonymous caller who has no tenant context yet — a query
    /// filter here would hide the very row that establishes which tenant they belong to. Same
    /// exemption, for the same reason, as the pre-authentication login counter.
    /// </summary>
    public virtual DbSet<TenantAdminInvitation> TenantAdminInvitations { get; set; } = null!;

    partial void OnModelCreatingPartial(ModelBuilder modelBuilder)
    {
        if (Database.IsNpgsql())
            modelBuilder.HasSequence<long>("CommercialCaseReferenceSequence");

        ConfigureAiGovernance(modelBuilder);
        ConfigureExtractionOperations(modelBuilder);
        ConfigureEvidenceRetentionModel(modelBuilder);
        // Platform-plane SaaS billing (rate cards, statements) — platform schema, not tenant-scoped.
        ConfigureBillingModel(modelBuilder);
        // SEC-H6: pre-authentication login counter — deliberately unfiltered, see the partial.
        ConfigureLoginThrottlingModel(modelBuilder);
        modelBuilder.ConfigureCommercialFinance();
        modelBuilder.ConfigureGeneralLedger();
        modelBuilder.ConfigureBankReconciliation();
        modelBuilder.ConfigureOrderToCash();
        modelBuilder.ConfigureLeadIdentity();

        // Commercial documents (non-nullable BusinessUnitId).
        modelBuilder.Entity<Lead>().HasQueryFilter(e => CurrentTenantId == null || e.BusinessUnitId == CurrentTenantId);
        modelBuilder.Entity<Rfq>().HasQueryFilter(e => CurrentTenantId == null || e.BusinessUnitId == CurrentTenantId);
        modelBuilder.Entity<Quote>().HasQueryFilter(e => CurrentTenantId == null || e.BusinessUnitId == CurrentTenantId);
        modelBuilder.Entity<Order>().HasQueryFilter(e => CurrentTenantId == null || e.BusinessUnitId == CurrentTenantId);
        modelBuilder.Entity<Shipment>().HasQueryFilter(e => CurrentTenantId == null || e.BusinessUnitId == CurrentTenantId);
        modelBuilder.Entity<CommercialCase>().HasQueryFilter(e => CurrentTenantId == null || e.BusinessUnitId == CurrentTenantId);
        modelBuilder.Entity<LeadReferenceConfiguration>().HasQueryFilter(e => CurrentTenantId == null || e.BusinessUnitId == CurrentTenantId);
        modelBuilder.Entity<LeadStatusHistory>().HasQueryFilter(e => CurrentTenantId == null || e.BusinessUnitId == CurrentTenantId);
        modelBuilder.Entity<SetupMaster>().HasQueryFilter(e => CurrentTenantId == null || e.BusinessUnitId == CurrentTenantId);
        modelBuilder.Entity<RolePermission>().HasQueryFilter(e => CurrentTenantId == null || e.BusinessUnitId == CurrentTenantId);
        ConfigureIamAuditTrail(modelBuilder);
        modelBuilder.Entity<User>().HasQueryFilter(e => CurrentTenantId == null || e.Buid == null || e.Buid == CurrentTenantId);
        modelBuilder.Entity<CommercialLifecycleEvent>().HasQueryFilter(e => CurrentTenantId == null || e.BusinessUnitId == CurrentTenantId);
        modelBuilder.Entity<LifecycleOutboxMessage>().HasQueryFilter(e => CurrentTenantId == null || e.BusinessUnitId == CurrentTenantId);
        modelBuilder.Entity<ERP_RFQ_Automation.AI.AiProcessingPolicy>().HasQueryFilter(e => CurrentTenantId == null || e.BusinessUnitId == CurrentTenantId);
        modelBuilder.Entity<ERP_RFQ_Automation.AI.AiRequest>().HasQueryFilter(e => CurrentTenantId == null || e.BusinessUnitId == CurrentTenantId);
        modelBuilder.Entity<ERP_RFQ_Automation.AI.AiCallAttempt>().HasQueryFilter(e => CurrentTenantId == null || e.BusinessUnitId == CurrentTenantId);
        modelBuilder.Entity<ERP_RFQ_Automation.AI.AiBudgetPeriod>().HasQueryFilter(e => CurrentTenantId == null || e.BusinessUnitId == CurrentTenantId);
        modelBuilder.Entity<ReceivableDocument>().HasQueryFilter(e => CurrentTenantId == null || e.BusinessUnitId == CurrentTenantId);
        modelBuilder.Entity<ReceivableDocumentLine>().HasQueryFilter(e => CurrentTenantId == null || e.BusinessUnitId == CurrentTenantId);
        modelBuilder.Entity<CustomerPayment>().HasQueryFilter(e => CurrentTenantId == null || e.BusinessUnitId == CurrentTenantId);
        modelBuilder.Entity<PaymentAllocation>().HasQueryFilter(e => CurrentTenantId == null || e.BusinessUnitId == CurrentTenantId);
        modelBuilder.Entity<LegalDocumentCounter>().HasQueryFilter(e => CurrentTenantId == null || e.BusinessUnitId == CurrentTenantId);
        modelBuilder.Entity<CommercialFinanceAudit>().HasQueryFilter(e => CurrentTenantId == null || e.BusinessUnitId == CurrentTenantId);
        modelBuilder.Entity<FinanceOutboxMessage>().HasQueryFilter(e => CurrentTenantId == null || e.BusinessUnitId == CurrentTenantId);
        modelBuilder.Entity<BankAccount>().HasQueryFilter(e => CurrentTenantId == null || e.BusinessUnitId == CurrentTenantId);
        modelBuilder.Entity<BankMatchingRule>().HasQueryFilter(e => CurrentTenantId == null || e.BusinessUnitId == CurrentTenantId);
        modelBuilder.Entity<ReconciliationRunRule>().HasQueryFilter(e => CurrentTenantId == null || e.BusinessUnitId == CurrentTenantId);
        modelBuilder.Entity<BankAdjustment>().HasQueryFilter(e => CurrentTenantId == null || e.BusinessUnitId == CurrentTenantId);
        modelBuilder.Entity<BankAdjustmentDistribution>().HasQueryFilter(e => CurrentTenantId == null || e.BusinessUnitId == CurrentTenantId);
        modelBuilder.Entity<BankStatementImport>().HasQueryFilter(e => CurrentTenantId == null || e.BusinessUnitId == CurrentTenantId);
        modelBuilder.Entity<BankStatement>().HasQueryFilter(e => CurrentTenantId == null || e.BusinessUnitId == CurrentTenantId);
        modelBuilder.Entity<BankStatementLine>().HasQueryFilter(e => CurrentTenantId == null || e.BusinessUnitId == CurrentTenantId);
        modelBuilder.Entity<ReconciliationRun>().HasQueryFilter(e => CurrentTenantId == null || e.BusinessUnitId == CurrentTenantId);
        modelBuilder.Entity<ReconciliationMatch>().HasQueryFilter(e => CurrentTenantId == null || e.BusinessUnitId == CurrentTenantId);
        modelBuilder.Entity<ReconciliationAllocation>().HasQueryFilter(e => CurrentTenantId == null || e.BusinessUnitId == CurrentTenantId);
        modelBuilder.Entity<ReceivableWriteOff>().HasQueryFilter(e => CurrentTenantId == null || e.BusinessUnitId == CurrentTenantId);
        modelBuilder.Entity<WriteOffAllocation>().HasQueryFilter(e => CurrentTenantId == null || e.BusinessUnitId == CurrentTenantId);
        modelBuilder.Entity<CustomerRefund>().HasQueryFilter(e => CurrentTenantId == null || e.BusinessUnitId == CurrentTenantId);
        modelBuilder.Entity<FinanceCommunicationContact>().HasQueryFilter(e => CurrentTenantId == null || e.BusinessUnitId == CurrentTenantId);
        modelBuilder.Entity<CustomerStatement>().HasQueryFilter(e => CurrentTenantId == null || e.BusinessUnitId == CurrentTenantId);
        modelBuilder.Entity<CustomerStatementLine>().HasQueryFilter(e => CurrentTenantId == null || e.BusinessUnitId == CurrentTenantId);
        modelBuilder.Entity<DunningPolicy>().HasQueryFilter(e => CurrentTenantId == null || e.BusinessUnitId == CurrentTenantId);
        modelBuilder.Entity<DunningPolicyStep>().HasQueryFilter(e => CurrentTenantId == null || e.BusinessUnitId == CurrentTenantId);
        modelBuilder.Entity<CustomerCollectionProfile>().HasQueryFilter(e => CurrentTenantId == null || e.BusinessUnitId == CurrentTenantId);
        modelBuilder.Entity<CollectionControl>().HasQueryFilter(e => CurrentTenantId == null || e.BusinessUnitId == CurrentTenantId);
        modelBuilder.Entity<DunningCase>().HasQueryFilter(e => CurrentTenantId == null || e.BusinessUnitId == CurrentTenantId);
        modelBuilder.Entity<PromiseToPay>().HasQueryFilter(e => CurrentTenantId == null || e.BusinessUnitId == CurrentTenantId);
        modelBuilder.Entity<DunningRun>().HasQueryFilter(e => CurrentTenantId == null || e.BusinessUnitId == CurrentTenantId);
        modelBuilder.Entity<DunningRunDecision>().HasQueryFilter(e => CurrentTenantId == null || e.BusinessUnitId == CurrentTenantId);
        modelBuilder.Entity<DunningNotice>().HasQueryFilter(e => CurrentTenantId == null || e.BusinessUnitId == CurrentTenantId);
        modelBuilder.Entity<DunningDeliveryAttempt>().HasQueryFilter(e => CurrentTenantId == null || e.BusinessUnitId == CurrentTenantId);
        modelBuilder.Entity<LedgerAccount>().HasQueryFilter(e => CurrentTenantId == null || e.BusinessUnitId == CurrentTenantId);
        modelBuilder.Entity<LedgerBook>().HasQueryFilter(e => CurrentTenantId == null || e.BusinessUnitId == CurrentTenantId);
        modelBuilder.Entity<AccountingPeriod>().HasQueryFilter(e => CurrentTenantId == null || e.BusinessUnitId == CurrentTenantId);
        modelBuilder.Entity<JournalEntry>().HasQueryFilter(e => CurrentTenantId == null || e.BusinessUnitId == CurrentTenantId);
        modelBuilder.Entity<JournalEntryLine>().HasQueryFilter(e => CurrentTenantId == null || e.BusinessUnitId == CurrentTenantId);
        modelBuilder.Entity<CustomerPurchaseOrder>().HasQueryFilter(e => CurrentTenantId == null || e.BusinessUnitId == CurrentTenantId);
        modelBuilder.Entity<CustomerPurchaseOrderLine>().HasQueryFilter(e => CurrentTenantId == null || e.BusinessUnitId == CurrentTenantId);
        modelBuilder.Entity<CustomerAward>().HasQueryFilter(e => CurrentTenantId == null || e.BusinessUnitId == CurrentTenantId);
        modelBuilder.Entity<CustomerAwardLineAllocation>().HasQueryFilter(e => CurrentTenantId == null || e.BusinessUnitId == CurrentTenantId);
        modelBuilder.Entity<OrderToCashAuditEvent>().HasQueryFilter(e => CurrentTenantId == null || e.BusinessUnitId == CurrentTenantId);
        modelBuilder.Entity<OrderToCashDocumentCounter>().HasQueryFilter(e => CurrentTenantId == null || e.BusinessUnitId == CurrentTenantId);
        modelBuilder.Entity<LeadIngestionBatch>().HasQueryFilter(e => CurrentTenantId == null || e.BusinessUnitId == CurrentTenantId);
        modelBuilder.Entity<LeadIngestionOccurrence>().HasQueryFilter(e => CurrentTenantId == null || e.BusinessUnitId == CurrentTenantId);
        modelBuilder.Entity<LeadOccurrenceDocument>().HasQueryFilter(e => CurrentTenantId == null || e.BusinessUnitId == CurrentTenantId);
        modelBuilder.Entity<LeadRevision>().HasQueryFilter(e => CurrentTenantId == null || e.BusinessUnitId == CurrentTenantId);
        modelBuilder.Entity<LeadItemRevision>().HasQueryFilter(e => CurrentTenantId == null || e.BusinessUnitId == CurrentTenantId);
        modelBuilder.Entity<LeadRevisionDifference>().HasQueryFilter(e => CurrentTenantId == null || e.BusinessUnitId == CurrentTenantId);
        modelBuilder.Entity<LeadMatchCandidate>().HasQueryFilter(e => CurrentTenantId == null || e.BusinessUnitId == CurrentTenantId);
        modelBuilder.Entity<LeadIdentityAuditEvent>().HasQueryFilter(e => CurrentTenantId == null || e.BusinessUnitId == CurrentTenantId);
        modelBuilder.Entity<LeadRevisionImpact>().HasQueryFilter(e => CurrentTenantId == null || e.BusinessUnitId == CurrentTenantId);

        // Foreign-exchange authority (Fx/). These two tables decide what rate a customer's quote
        // is converted at, so a tenant-crossing read is a commercial data leak, not a schema nit.
        // The database layer is closed by 20260804203000_TenantIsolationForFxAuthority (RLS +
        // policies); these are the matching EF filters, and they are the layer that still protects
        // reads made by nexora_pipeline_app, which is created BYPASSRLS. Entity shape itself is
        // configured by data annotations in Fx/FxEntities.cs — see Models/ErpRfqAutomationContext.Fx.cs.
        modelBuilder.Entity<ERP_RFQ_Automation.Fx.FxRate>().HasQueryFilter(e => CurrentTenantId == null || e.BusinessUnitId == CurrentTenantId);
        modelBuilder.Entity<ERP_RFQ_Automation.Fx.FxRateSnapshot>().HasQueryFilter(e => CurrentTenantId == null || e.BusinessUnitId == CurrentTenantId);

        // Dependent commercial rows inherit the tenant boundary from their required
        // aggregate root. Keep these filters aligned with the PostgreSQL parent-derived
        // RLS policies so non-PostgreSQL test/provider paths retain the same isolation.
        modelBuilder.Entity<LeadItem>().HasQueryFilter(e => CurrentTenantId == null || e.Lead.BusinessUnitId == CurrentTenantId);
        modelBuilder.Entity<Rfqitem>().HasQueryFilter(e => CurrentTenantId == null || e.Rfq != null && e.Rfq.BusinessUnitId == CurrentTenantId);
        modelBuilder.Entity<QuoteItem>().HasQueryFilter(e => CurrentTenantId == null || e.Quote.BusinessUnitId == CurrentTenantId);
        modelBuilder.Entity<OrderItem>().HasQueryFilter(e => CurrentTenantId == null || e.Order.BusinessUnitId == CurrentTenantId);
        modelBuilder.Entity<ShipmentItem>().HasQueryFilter(e => CurrentTenantId == null || e.Shipment.BusinessUnitId == CurrentTenantId);
        modelBuilder.Entity<ShipmentStatusHistory>().HasQueryFilter(e => CurrentTenantId == null || e.Shipment.BusinessUnitId == CurrentTenantId);

        // Customer identity is tenant-owned. Other catalog master data may remain shared.
        modelBuilder.Entity<Customer>().HasQueryFilter(e => CurrentTenantId == null || e.Buid == CurrentTenantId);
        modelBuilder.Entity<Supplier>().HasQueryFilter(e => CurrentTenantId == null || e.Buid == CurrentTenantId);
        modelBuilder.Entity<Product>().HasQueryFilter(e => CurrentTenantId == null || e.Buid == null || e.Buid == CurrentTenantId);
        modelBuilder.Entity<Inventory>().HasQueryFilter(e => CurrentTenantId == null || e.Buid == null || e.Buid == CurrentTenantId);
        modelBuilder.Entity<ProductAttachment>().HasQueryFilter(e =>
            CurrentTenantId == null || e.Inventory.Buid == null || e.Inventory.Buid == CurrentTenantId);
        modelBuilder.Entity<SupplierPurchaseHistory>().HasQueryFilter(e => CurrentTenantId == null ||
            (e.Product.Buid == null || e.Product.Buid == CurrentTenantId) &&
            (e.Supplier.Buid == null || e.Supplier.Buid == CurrentTenantId));
        modelBuilder.Entity<SupplierQuotedItem>().HasQueryFilter(e =>
            CurrentTenantId == null || e.BusinessUnitId == CurrentTenantId);
        modelBuilder.Entity<EmailConfiguration>().HasQueryFilter(e =>
            CurrentTenantId == null || e.BusinessUnitId == CurrentTenantId);
        modelBuilder.Entity<EmailIngest>().HasQueryFilter(e =>
            CurrentTenantId == null || e.EmailConfiguration.BusinessUnitId == CurrentTenantId);
        modelBuilder.Entity<Contact>().HasQueryFilter(e => CurrentTenantId == null || e.BusinessUnitId == CurrentTenantId);

        // ==== Tenant-owned configuration + reference data ====
        // These twelve tables carry a tenant column and were already covered by the
        // information_schema sweep in 20260723120000_CompleteTenantRlsCoverage, so PostgreSQL
        // has hidden other tenants' rows from nexora_tenant_app since that migration. They had
        // no matching EF filter, which left three real gaps the database could not close:
        //   * nexora_pipeline_app (every background worker) is created BYPASSRLS, so RLS never
        //     constrained worker-side reads of these tables;
        //   * the SQLite integration suite and any non-PostgreSQL provider have no RLS at all;
        //   * a warehouse/currency/UOM/tax row resolved by primary key alone would silently
        //     attach ANOTHER tenant's reference row to a quote or order on those paths.
        // Every tenant column here is NOT NULL except ProductSubCategory.BusinessUnitId; the
        // predicates below are written to mirror each table's RLS policy exactly, so the two
        // layers can never disagree about which rows exist. In particular there is no
        // "null means shared master data" clause: the sweep granted that only to Customers,
        // Suppliers, Products and Inventory, so a NULL BusinessUnitId row here is already
        // invisible to a tenant in PostgreSQL and must stay invisible in EF too.
        // Guarded by TenantIsolationTests.Every_entity_with_a_tenant_column_has_a_global_query_filter.
        modelBuilder.Entity<Warehouse>().HasQueryFilter(e => CurrentTenantId == null || e.BusinessUnitId == CurrentTenantId);
        modelBuilder.Entity<Currency>().HasQueryFilter(e => CurrentTenantId == null || e.BusinessUnitId == CurrentTenantId);
        modelBuilder.Entity<Team>().HasQueryFilter(e => CurrentTenantId == null || e.BusinessUnitId == CurrentTenantId);
        modelBuilder.Entity<UserGroup>().HasQueryFilter(e => CurrentTenantId == null || e.BusinessUnitId == CurrentTenantId);
        modelBuilder.Entity<ProductCategory>().HasQueryFilter(e => CurrentTenantId == null || e.BusinessUnitId == CurrentTenantId);
        modelBuilder.Entity<ProductSubCategory>().HasQueryFilter(e => CurrentTenantId == null || e.BusinessUnitId == CurrentTenantId);
        modelBuilder.Entity<QuoteConfiguration>().HasQueryFilter(e => CurrentTenantId == null || e.BusinessUnitId == CurrentTenantId);
        modelBuilder.Entity<Taxis>().HasQueryFilter(e => CurrentTenantId == null || e.BusinessUnitId == CurrentTenantId);
        modelBuilder.Entity<SetCountry>().HasQueryFilter(e => CurrentTenantId == null || e.Buid == CurrentTenantId);
        modelBuilder.Entity<SetState>().HasQueryFilter(e => CurrentTenantId == null || e.Buid == CurrentTenantId);
        modelBuilder.Entity<SetCity>().HasQueryFilter(e => CurrentTenantId == null || e.Buid == CurrentTenantId);
        modelBuilder.Entity<SetUom>().HasQueryFilter(e => CurrentTenantId == null || e.BusinessUnitId == CurrentTenantId);

        // LeadItem.ExtraFields (partial property in LeadItem.Extra.cs): verbatim
        // unrecognized customer-document columns, stored as jsonb.
        modelBuilder.Entity<LeadItem>().Property(e => e.ExtraFields).HasColumnType("jsonb");

        // WP-A3 duplicate flag (partial properties in Lead.Duplicate.cs). Columns are
        // added by a lead-generated migration; see Deduplication/DEDUP-WIRING.md.
        modelBuilder.Entity<Lead>().Property(e => e.DuplicateStatus).HasMaxLength(20);
        modelBuilder.Entity<Lead>().Property(e => e.DuplicateResolvedBy).HasMaxLength(256);
        modelBuilder.Entity<Lead>().HasIndex(e => new { e.BusinessUnitId, e.DuplicateStatus })
            .HasDatabaseName("IX_Lead_BU_DuplicateStatus");

        // WP-BOQ foundation: inquiry classification (partial property in
        // Lead.Inquiry.cs). Column ("InquiryType" varchar(16) NULL) is added by a
        // lead-generated migration, same pattern as the duplicate-flag columns above.
        modelBuilder.Entity<Lead>().Property(e => e.InquiryType).HasMaxLength(16);

        // Routing uses portable relational constructs and is also enabled for the
        // SQLite integration suite. The evidence/custom-field modules retain their
        // PostgreSQL-only model because their constraints use jsonb and PostgreSQL
        // expressions.
        modelBuilder.ApplyCommercialRoutingModel();
        // Client-organisation identity: the ranked machine proposals behind every
        // "Nexora thinks this is ..." on a lead.
        modelBuilder.ApplyCustomerResolutionModel();
        modelBuilder.ApplyCommercialSalesModel();
        modelBuilder.ApplyCommercialExceptionModel();
        modelBuilder.ApplyOpportunityPriorityModel(Database.IsNpgsql());
        modelBuilder.ApplyGrowthIntelligenceModel(
            e => CurrentTenantId == null || e.BusinessUnitId == CurrentTenantId);
        modelBuilder.ApplyQuoteDeliveryModel();

        // Stock reservation ledger (portable relational model; enabled for the SQLite suite).
        modelBuilder.ApplyInventoryReservationModel();
        modelBuilder.ApplyCommercialInventoryModel();
        ConfigureProcurementModel(modelBuilder);
        ConfigureCommercialDocumentsModel(modelBuilder);
        ConfigureSupplierGovernanceModel(modelBuilder);
        ConfigureSupplierQuotesModel(modelBuilder);
        ConfigureCommercialLearningModel(modelBuilder);
        ConfigurePlatformGovernanceModel(modelBuilder);
        modelBuilder.Entity<Quote>().HasAlternateKey(x => new { x.BusinessUnitId, x.Id });
        modelBuilder.Entity<QuoteItem>().HasAlternateKey(x => new { x.Id, x.QuoteId });
        modelBuilder.Entity<ERP_RFQ_Automation.Inventory.StockReservation>()
            .HasQueryFilter(e => CurrentTenantId == null || e.BusinessUnitId == CurrentTenantId);
        modelBuilder.Entity<CustomerIdentifier>().HasQueryFilter(e => CurrentTenantId == null || e.BusinessUnitId == CurrentTenantId);
        modelBuilder.Entity<ERP_RFQ_Automation.CustomerResolution.LeadCustomerMatchCandidate>()
            .HasQueryFilter(e => CurrentTenantId == null || e.BusinessUnitId == CurrentTenantId);
        modelBuilder.Entity<CustomerOwnership>().HasQueryFilter(e => CurrentTenantId == null || e.BusinessUnitId == CurrentTenantId);
        modelBuilder.Entity<LeadRoutingDecision>().HasQueryFilter(e => CurrentTenantId == null || e.BusinessUnitId == CurrentTenantId);
        modelBuilder.Entity<LeadAssignment>().HasQueryFilter(e => CurrentTenantId == null || e.BusinessUnitId == CurrentTenantId);
        modelBuilder.Entity<UnassignedWorkItem>().HasQueryFilter(e => CurrentTenantId == null || e.BusinessUnitId == CurrentTenantId);
        modelBuilder.Entity<SalesRepProfile>().HasQueryFilter(e => CurrentTenantId == null || e.BusinessUnitId == CurrentTenantId);
        modelBuilder.Entity<SalesTeamMembership>().HasQueryFilter(e => CurrentTenantId == null || e.BusinessUnitId == CurrentTenantId);
        modelBuilder.Entity<CommercialExceptionCase>().HasQueryFilter(e => CurrentTenantId == null || e.BusinessUnitId == CurrentTenantId);
        modelBuilder.Entity<CommercialExceptionEvent>().HasQueryFilter(e => CurrentTenantId == null || e.BusinessUnitId == CurrentTenantId);
        modelBuilder.Entity<CommercialExceptionOutboxMessage>().HasQueryFilter(e => CurrentTenantId == null || e.BusinessUnitId == CurrentTenantId);
        modelBuilder.Entity<CommercialExceptionOperation>().HasQueryFilter(e => CurrentTenantId == null || e.BusinessUnitId == CurrentTenantId);
        modelBuilder.Entity<OpportunityRecommendation>().HasQueryFilter(e => CurrentTenantId == null || e.BusinessUnitId == CurrentTenantId);
        modelBuilder.Entity<OpportunityOutcome>().HasQueryFilter(e => CurrentTenantId == null || e.BusinessUnitId == CurrentTenantId);
        modelBuilder.Entity<OpportunityFeedback>().HasQueryFilter(e => CurrentTenantId == null || e.BusinessUnitId == CurrentTenantId);
        modelBuilder.Entity<OpportunityEvent>().HasQueryFilter(e => CurrentTenantId == null || e.BusinessUnitId == CurrentTenantId);
        modelBuilder.Entity<OpportunityOutbox>().HasQueryFilter(e => CurrentTenantId == null || e.BusinessUnitId == CurrentTenantId);
        modelBuilder.Entity<OpportunityOperation>().HasQueryFilter(e => CurrentTenantId == null || e.BusinessUnitId == CurrentTenantId);
        modelBuilder.Entity<CommercialActivity>().HasQueryFilter(e => CurrentTenantId == null || e.BusinessUnitId == CurrentTenantId);
        modelBuilder.Entity<FollowUpTask>().HasQueryFilter(e => CurrentTenantId == null || e.BusinessUnitId == CurrentTenantId);
        modelBuilder.Entity<FollowUpTransitionEvent>().HasQueryFilter(e => CurrentTenantId == null || e.BusinessUnitId == CurrentTenantId);
        modelBuilder.Entity<SalesContribution>().HasQueryFilter(e => CurrentTenantId == null || e.BusinessUnitId == CurrentTenantId);
        modelBuilder.Entity<QuoteDeliveryRequest>().HasQueryFilter(e => CurrentTenantId == null || e.BusinessUnitId == CurrentTenantId);
        modelBuilder.Entity<ERP_RFQ_Automation.Extraction.ExtractionJob>()
            .HasQueryFilter(e => CurrentTenantId == null || e.BusinessUnitId == CurrentTenantId);
        modelBuilder.Entity<ProductAlias>().HasQueryFilter(e => CurrentTenantId == null || e.BusinessUnitId == CurrentTenantId);
        modelBuilder.Entity<ProductSupersession>().HasQueryFilter(e => CurrentTenantId == null || e.BusinessUnitId == CurrentTenantId);
        modelBuilder.Entity<InventoryMovement>().HasQueryFilter(e => CurrentTenantId == null || e.BusinessUnitId == CurrentTenantId);
        modelBuilder.Entity<IncomingInventory>().HasQueryFilter(e => CurrentTenantId == null || e.BusinessUnitId == CurrentTenantId);
        modelBuilder.Entity<LeadLineCommercialResolution>().HasQueryFilter(e => CurrentTenantId == null || e.BusinessUnitId == CurrentTenantId);

        modelBuilder.ConfigureGovernedCustomFields();
        modelBuilder.ConfigureCommercialLifecycle();
        modelBuilder.Entity<CustomFieldDefinition>().HasQueryFilter(e => CurrentTenantId == null || e.BusinessUnitId == CurrentTenantId);
        modelBuilder.Entity<CustomFieldVersion>().HasQueryFilter(e => CurrentTenantId == null || e.Definition.BusinessUnitId == CurrentTenantId);
        modelBuilder.Entity<CustomFieldOption>().HasQueryFilter(e => CurrentTenantId == null || e.Version.Definition.BusinessUnitId == CurrentTenantId);
        modelBuilder.Entity<CustomFieldRule>().HasQueryFilter(e => CurrentTenantId == null || e.Version.Definition.BusinessUnitId == CurrentTenantId);
        modelBuilder.Entity<CustomFieldDependency>().HasQueryFilter(e => CurrentTenantId == null || e.Version.Definition.BusinessUnitId == CurrentTenantId);
        modelBuilder.Entity<CustomFieldRecord>().HasQueryFilter(e => CurrentTenantId == null || e.BusinessUnitId == CurrentTenantId);
        modelBuilder.Entity<CustomFieldValue>().HasQueryFilter(e => CurrentTenantId == null || e.BusinessUnitId == CurrentTenantId);
        modelBuilder.Entity<CustomFieldValueHistory>().HasQueryFilter(e => CurrentTenantId == null || e.BusinessUnitId == CurrentTenantId);

        // PostgreSQL-backed enterprise foundations.
        if (Database.IsNpgsql())
        {
            modelBuilder.AddEvidenceLedger();

            modelBuilder.Entity<ERP_RFQ_Automation.Extraction.ExtractionJob>()
                .HasAlternateKey(e => new { e.BusinessUnitId, e.Id })
                .HasName("AK_ExtractionJobs_BusinessUnitId_Id");
            modelBuilder.Entity<ERP_RFQ_Automation.Extraction.ExtractionJob>()
                .HasOne<SourceDocumentOccurrence>()
                .WithMany()
                .HasForeignKey(e => new { e.BusinessUnitId, e.SourceDocumentOccurrenceId })
                .HasPrincipalKey(e => new { e.BusinessUnitId, e.Id })
                .OnDelete(DeleteBehavior.Restrict);
            modelBuilder.Entity<LeadIngestionOccurrence>()
                .HasOne<SourceDocument>()
                .WithMany()
                .HasForeignKey(e => new { e.BusinessUnitId, e.SourceDocumentId })
                .HasPrincipalKey(e => new { e.BusinessUnitId, e.Id })
                .OnDelete(DeleteBehavior.Restrict);
            modelBuilder.Entity<LeadIngestionOccurrence>()
                .HasOne<SourceDocumentOccurrence>()
                .WithMany()
                .HasForeignKey(e => new { e.BusinessUnitId, e.SourceDocumentOccurrenceId })
                .HasPrincipalKey(e => new { e.BusinessUnitId, e.Id })
                .OnDelete(DeleteBehavior.Restrict);
            modelBuilder.Entity<LeadIngestionOccurrence>()
                .HasOne<ERP_RFQ_Automation.Extraction.ExtractionJob>()
                .WithMany()
                .HasForeignKey(e => new { e.BusinessUnitId, e.ExtractionJobId })
                .HasPrincipalKey(e => new { e.BusinessUnitId, e.Id })
                .OnDelete(DeleteBehavior.Restrict);
            modelBuilder.Entity<LeadOccurrenceDocument>()
                .HasOne<SourceDocument>()
                .WithMany()
                .HasForeignKey(e => new { e.BusinessUnitId, e.SourceDocumentId })
                .HasPrincipalKey(e => new { e.BusinessUnitId, e.Id })
                .OnDelete(DeleteBehavior.Restrict);
            modelBuilder.Entity<ERP_RFQ_Automation.AI.AiRequest>()
                .HasOne<ERP_RFQ_Automation.Extraction.ExtractionJob>()
                .WithMany()
                .HasForeignKey(e => new { e.BusinessUnitId, e.ExtractionJobId })
                .HasPrincipalKey(e => new { e.BusinessUnitId, e.Id })
                .OnDelete(DeleteBehavior.Restrict);
            modelBuilder.Entity<ERP_RFQ_Automation.AI.AiRequest>()
                .HasOne<SourceDocumentOccurrence>()
                .WithMany()
                .HasForeignKey(e => new { e.BusinessUnitId, e.SourceDocumentOccurrenceId })
                .HasPrincipalKey(e => new { e.BusinessUnitId, e.Id })
                .OnDelete(DeleteBehavior.Restrict);
            modelBuilder.Entity<CanonicalInquiry>()
                .HasOne<Lead>()
                .WithMany()
                .HasForeignKey(e => new { e.BusinessUnitId, e.LeadId })
                .HasPrincipalKey(e => new { e.BusinessUnitId, e.Id })
                .OnDelete(DeleteBehavior.Restrict);

            modelBuilder.Entity<DocumentCorpus>().HasQueryFilter(e => CurrentTenantId == null || e.BusinessUnitId == CurrentTenantId);
            modelBuilder.Entity<SourceDocument>().HasQueryFilter(e => CurrentTenantId == null || e.BusinessUnitId == CurrentTenantId);
            modelBuilder.Entity<SourceDocumentOccurrence>().HasQueryFilter(e => CurrentTenantId == null || e.BusinessUnitId == CurrentTenantId);
            modelBuilder.Entity<ExtractionRun>().HasQueryFilter(e => CurrentTenantId == null || e.BusinessUnitId == CurrentTenantId);
            modelBuilder.Entity<DocumentPage>().HasQueryFilter(e => CurrentTenantId == null || e.BusinessUnitId == CurrentTenantId);
            modelBuilder.Entity<DocumentRegion>().HasQueryFilter(e => CurrentTenantId == null || e.BusinessUnitId == CurrentTenantId);
            modelBuilder.Entity<CanonicalInquiry>().HasQueryFilter(e => CurrentTenantId == null || e.BusinessUnitId == CurrentTenantId);
            modelBuilder.Entity<CanonicalLineItem>().HasQueryFilter(e => CurrentTenantId == null || e.BusinessUnitId == CurrentTenantId);
            modelBuilder.Entity<ValidationFinding>().HasQueryFilter(e => CurrentTenantId == null || e.BusinessUnitId == CurrentTenantId);
            modelBuilder.Entity<FieldEvidence>().HasQueryFilter(e => CurrentTenantId == null || e.BusinessUnitId == CurrentTenantId);

        }

        // Permanent tenant-scoped commercial-case identity. PostgreSQL assigns the
        // value through the migration trigger; ValueGeneratedOnAdd makes EF read the
        // generated value back with the INSERT result.
        modelBuilder.Entity<Lead>().Property(e => e.CommercialCaseReference)
            .HasMaxLength(100)
            .IsRequired()
            .ValueGeneratedOnAdd();
        var commercialCaseId = modelBuilder.Entity<Lead>().Property(e => e.CommercialCaseId);
        if (Database.IsNpgsql())
            commercialCaseId.ValueGeneratedOnAdd();
        modelBuilder.Entity<Lead>().HasIndex(e => new { e.BusinessUnitId, e.CommercialCaseReference })
            .IsUnique()
            .HasDatabaseName("UX_Leads_BU_CommercialCaseReference");
        modelBuilder.Entity<Lead>().HasIndex(e => e.CommercialCaseId).IsUnique()
            .HasDatabaseName("UX_Leads_CommercialCaseID");
        modelBuilder.Entity<Order>().HasIndex(e => new { e.BusinessUnitId, e.QuoteId })
            .IsUnique()
            .HasFilter("\"QuoteID\" IS NOT NULL AND \"CustomerAwardID\" IS NULL")
            .HasDatabaseName("UX_Orders_BU_LegacyQuoteID");
        modelBuilder.Entity<Order>().HasIndex(e => e.BusinessUnitId)
            .HasDatabaseName("IX_Orders_BusinessUnitID");
        modelBuilder.Entity<Quote>().Property(e => e.FinancialCalculationVersion)
            .HasDefaultValue(2);

        modelBuilder.Entity<CommercialCase>(entity =>
        {
            entity.ToTable("CommercialCases");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.BusinessUnitId).HasColumnName("BusinessUnitID");
            var allocationNumber = entity.Property(e => e.AllocationNumber).ValueGeneratedOnAdd();
            if (Database.IsNpgsql())
                allocationNumber.HasDefaultValueSql("nextval('\"CommercialCaseReferenceSequence\"')");
            entity.Property(e => e.MasterReference).HasMaxLength(100).IsRequired().ValueGeneratedOnAdd();
            entity.Property(e => e.CreatedOn).HasDefaultValueSql("now()");
            entity.Property(e => e.CreatedBy).HasMaxLength(255).IsRequired();
            entity.HasIndex(e => e.AllocationNumber).IsUnique()
                .HasDatabaseName("UX_CommercialCases_AllocationNumber");
            entity.HasIndex(e => new { e.BusinessUnitId, e.MasterReference }).IsUnique()
                .HasDatabaseName("UX_CommercialCases_BU_MasterReference");
            entity.HasOne(e => e.BusinessUnit).WithMany(e => e.CommercialCases)
                .HasForeignKey(e => e.BusinessUnitId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(e => e.Lead).WithOne(e => e.CommercialCase)
                .HasForeignKey<Lead>(e => e.CommercialCaseId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<LeadReferenceConfiguration>(entity =>
        {
            entity.ToTable("LeadReferenceConfigurations");
            entity.HasKey(e => e.BusinessUnitId);
            entity.Property(e => e.BusinessUnitId).ValueGeneratedNever().HasColumnName("BusinessUnitID");
            entity.Property(e => e.Prefix).HasMaxLength(20).IsRequired();
            entity.Property(e => e.Format).HasMaxLength(100).IsRequired();
            entity.Property(e => e.SequencePadding).HasDefaultValue(6);
            entity.Property(e => e.FinancialYearStartMonth).HasDefaultValue(1);
            entity.Property(e => e.CreatedOn).HasDefaultValueSql("now()");
            entity.HasOne(e => e.BusinessUnit).WithOne(e => e.LeadReferenceConfiguration)
                .HasForeignKey<LeadReferenceConfiguration>(e => e.BusinessUnitId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<LeadStatusHistory>(entity =>
        {
            entity.ToTable("LeadStatusHistories");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.BusinessUnitId).HasColumnName("BusinessUnitID");
            entity.Property(e => e.LeadId).HasColumnName("LeadID");
            entity.Property(e => e.CommercialCaseId).HasColumnName("CommercialCaseID");
            entity.Property(e => e.PreviousStatusId).HasColumnName("PreviousStatusID");
            entity.Property(e => e.NewStatusId).HasColumnName("NewStatusID");
            entity.Property(e => e.EventType).HasMaxLength(30).IsRequired();
            entity.Property(e => e.ChangedBy).HasMaxLength(255);
            entity.Property(e => e.ActorSource).HasMaxLength(50).IsRequired();
            entity.Property(e => e.ChangedOn).HasDefaultValueSql("now()");
            entity.Property(e => e.Reason).HasMaxLength(1000);
            entity.Property(e => e.CommercialCaseReference).HasMaxLength(100).IsRequired();
            entity.HasIndex(e => new { e.BusinessUnitId, e.LeadId, e.ChangedOn })
                .HasDatabaseName("IX_LeadStatusHistory_BU_Lead_ChangedOn");
            entity.HasOne(e => e.Lead).WithMany(e => e.StatusHistory)
                .HasForeignKey(e => e.LeadId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(e => e.CommercialCase).WithMany(e => e.LeadStatusHistory)
                .HasForeignKey(e => e.CommercialCaseId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(e => e.BusinessUnit).WithMany(e => e.LeadStatusHistories)
                .HasForeignKey(e => e.BusinessUnitId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        // ==== Async extraction pipeline (ADR-0003) ====
        modelBuilder.Entity<ERP_RFQ_Automation.Extraction.ExtractionJob>(entity =>
        {
            entity.ToTable("ExtractionJobs");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.SourceType).HasConversion<string>().HasMaxLength(30).IsRequired();
            entity.Property(e => e.Status).HasConversion<string>().HasMaxLength(20).IsRequired();
            entity.Property(e => e.ContentHash).HasMaxLength(64).IsRequired();
            entity.Property(e => e.StoragePath).HasMaxLength(1024).IsRequired();
            entity.Property(e => e.FileName).HasMaxLength(512);
            entity.Property(e => e.FileType).HasMaxLength(50);
            entity.Property(e => e.LeasedBy).HasMaxLength(200);
            entity.Property(e => e.LastError).HasMaxLength(4000);
            entity.Property(e => e.CreatedOn).HasDefaultValueSql("now()");
            entity.Property(e => e.UpdatedOn).HasDefaultValueSql("now()");
            entity.HasIndex(e => new { e.BusinessUnitId, e.ContentHash }).HasDatabaseName("IX_ExtractionJobs_BU_ContentHash");
            entity.HasIndex(e => new { e.BusinessUnitId, e.SourceDocumentOccurrenceId }).IsUnique()
                .HasFilter("\"SourceDocumentOccurrenceId\" IS NOT NULL")
                .HasDatabaseName("UX_ExtractionJobs_BU_SourceOccurrence");
            entity.HasIndex(e => new { e.Status, e.NextAttemptAt, e.Priority, e.SchedulerTag }).HasDatabaseName("IX_ExtractionJobs_Claim");
            entity.HasIndex(e => new { e.BusinessUnitId, e.Status }).HasDatabaseName("IX_ExtractionJobs_BU_Status");
            entity.HasIndex(e => e.BatchId).HasDatabaseName("IX_ExtractionJobs_BatchId");
        });
        modelBuilder.Entity<ERP_RFQ_Automation.Extraction.TenantQueueState>(entity =>
        {
            entity.ToTable("TenantQueueStates");
            entity.HasKey(e => e.BusinessUnitId);
            entity.Property(e => e.BusinessUnitId).ValueGeneratedNever();
            entity.Property(e => e.Weight).HasDefaultValue(1.0);
            // Per-tenant extraction queue fairness state. The scheduler sweeps every tenant
            // and therefore runs with a null tenant context, where this filter is a no-op;
            // it exists so a request-scoped context can never read or rewrite another
            // tenant's queue weight/backoff. Mirrors the table's RLS policy.
            entity.HasQueryFilter(e => CurrentTenantId == null || e.BusinessUnitId == CurrentTenantId);
        });

        modelBuilder.Entity<Lead>(entity =>
        {
            entity.Property(e => e.ReviewVersion).HasDefaultValue(1L).IsConcurrencyToken();
            entity.Property(e => e.RequiresCommercialReview).HasDefaultValue(false);
            entity.Property(e => e.CommercialFactsVerified).HasDefaultValue(false);
            entity.Property(e => e.ReviewApprovedBy).HasMaxLength(255);
        });
        modelBuilder.Entity<LeadReviewAudit>(entity =>
        {
            entity.ToTable("LeadReviewAudits");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Action).HasMaxLength(20).IsRequired();
            entity.Property(e => e.ReviewedBy).HasMaxLength(255).IsRequired();
            entity.Property(e => e.Reason).HasMaxLength(1000);
            entity.Property(e => e.BeforeJson).HasColumnType("jsonb").IsRequired();
            entity.Property(e => e.AfterJson).HasColumnType("jsonb").IsRequired();
            entity.Property(e => e.ReviewedOn).HasDefaultValueSql("now()");
            entity.HasIndex(e => new { e.BusinessUnitId, e.LeadId, e.ToVersion }).IsUnique()
                .HasDatabaseName("UX_LeadReviewAudits_BU_Lead_Version");
            entity.HasOne(e => e.Lead).WithMany(e => e.ReviewAudits)
                .HasForeignKey(e => new { e.BusinessUnitId, e.LeadId })
                .HasPrincipalKey(e => new { e.BusinessUnitId, e.Id })
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasQueryFilter(e => CurrentTenantId == null || e.BusinessUnitId == CurrentTenantId);
        });

        // ==== Extraction accuracy corpus (labels harvested from approved reviews) ====
        // The only ground truth Nexora has. Written inside the review transaction by
        // LeadRepository.CaptureExtractionCorpusAsync; read by AccuracyMeasurementService.
        modelBuilder.Entity<ExtractionCorpusEntry>(entity =>
        {
            entity.ToTable("ExtractionCorpusEntries");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.ExtractionPath).HasMaxLength(32).IsRequired();
            entity.Property(e => e.Scope).HasMaxLength(16).IsRequired();
            entity.Property(e => e.FieldName).HasMaxLength(64).IsRequired();
            entity.Property(e => e.ApprovedBy).HasMaxLength(255).IsRequired();
            entity.Property(e => e.CapturedOn).HasDefaultValueSql("now()");
            // One cell per (review, scope, field): a re-approval writes a new audit and a
            // new set of cells, so a re-reviewed document can never double-count itself
            // into its own sample.
            entity.HasIndex(e => new { e.BusinessUnitId, e.LeadReviewAuditId, e.Scope, e.FieldName })
                .IsUnique().HasDatabaseName("UX_ExtractionCorpusEntries_BU_Audit_Field");
            // The measurement read path: group by path + field within a tenant.
            entity.HasIndex(e => new { e.BusinessUnitId, e.ExtractionPath, e.Scope, e.FieldName })
                .HasDatabaseName("IX_ExtractionCorpusEntries_BU_Path_Field");
            entity.HasOne(e => e.ReviewAudit).WithMany()
                .HasForeignKey(e => e.LeadReviewAuditId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasQueryFilter(e => CurrentTenantId == null || e.BusinessUnitId == CurrentTenantId);
        });

        // ==== Platform-Owner control plane (ADR-0005) — non-tenant, "platform" schema ====
        modelBuilder.Entity<ERP_RFQ_Automation.Platform.Models.PlatformUser>(e =>
        {
            e.ToTable("PlatformUsers", "platform");
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.Email).IsUnique();
            e.Property(x => x.Email).IsRequired().HasMaxLength(256);
            e.Property(x => x.PasswordHash).IsRequired();
            e.Property(x => x.PlatformRole).HasConversion<string>().HasMaxLength(32);
            e.Property(x => x.DisplayName).HasMaxLength(200);
        });
        modelBuilder.Entity<ERP_RFQ_Automation.Platform.Models.Plan>(e =>
        {
            e.ToTable("Plans", "platform");
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.Code).IsUnique();
            e.Property(x => x.Code).IsRequired().HasMaxLength(64);
            e.Property(x => x.Name).IsRequired().HasMaxLength(128);
            e.Property(x => x.Features).HasColumnType("jsonb").HasDefaultValue("{}");
            e.Property(x => x.MonthlyPriceUsd).HasPrecision(10, 2);
        });
        modelBuilder.Entity<ERP_RFQ_Automation.Platform.Models.Tenant>(e =>
        {
            e.ToTable("Tenants", "platform");
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.Slug).IsUnique();
            e.Property(x => x.Name).IsRequired().HasMaxLength(256);
            e.Property(x => x.Slug).IsRequired().HasMaxLength(64);
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(32);
            e.Property(x => x.StatusReason).HasMaxLength(1000);
            e.HasOne(x => x.Plan).WithMany().HasForeignKey(x => x.PlanId).OnDelete(DeleteBehavior.SetNull);

            // Legal identity. Widths follow what actually gets printed on a commercial document;
            // LegalName matches Name because a registered entity name can be longer than its brand.
            e.Property(x => x.LegalName).HasMaxLength(256);
            e.Property(x => x.RegistrationNumber).HasMaxLength(64);
            e.Property(x => x.TaxNumber).HasMaxLength(64);
            e.Property(x => x.CountryCode).HasMaxLength(2).IsFixedLength();
            e.Property(x => x.Industry).HasMaxLength(128);
            e.Property(x => x.Website).HasMaxLength(512);
            e.Property(x => x.AddressLine1).HasMaxLength(256);
            e.Property(x => x.AddressLine2).HasMaxLength(256);
            e.Property(x => x.City).HasMaxLength(128);
            e.Property(x => x.StateProvince).HasMaxLength(128);
            e.Property(x => x.PostalCode).HasMaxLength(32);
            e.Property(x => x.Phone).HasMaxLength(64);
            e.Property(x => x.ContactEmail).HasMaxLength(320);
            e.Property(x => x.LogoUrl).HasMaxLength(1024);

            // Operating defaults seeded into the tenant's own reference data.
            e.Property(x => x.BaseCurrencyCode).HasMaxLength(3).IsFixedLength();
            e.Property(x => x.TimeZoneId).HasMaxLength(64);
            e.Property(x => x.Locale).HasMaxLength(16);
            e.Property(x => x.DataRegion).HasMaxLength(32);

            // Commercial terms. BillingMode is stored as its NAME, not its ordinal: a statement
            // produced today has to remain explainable years later, and reordering the enum must
            // not silently reclassify historical tenants from Trial to Internal.
            e.Property(x => x.BillingMode).HasConversion<string>().HasMaxLength(16)
                .HasDefaultValue(ERP_RFQ_Automation.Platform.Models.TenantBillingMode.Billable);
            e.Property(x => x.BillingModeReason).HasMaxLength(1000);
            e.Property(x => x.PurchaseOrderReference).HasMaxLength(128);
            e.Property(x => x.BillingContactName).HasMaxLength(200);
            e.Property(x => x.BillingContactEmail).HasMaxLength(320);
            e.Property(x => x.BillingAddress).HasMaxLength(1024);
            e.Property(x => x.AccountOwnerEmail).HasMaxLength(320);

            // No navigation to RateCard: the billing aggregate sits above the platform model in
            // the dependency order, so the pin is carried as a plain id and resolved by
            // BillingStatementService. Indexed because the billing run sweeps tenants by card.
            e.HasIndex(x => x.RateCardId);

            // The billing run's working set: every tenant that should produce a statement.
            e.HasIndex(x => new { x.Status, x.BillingMode });
        });
        modelBuilder.Entity<ERP_RFQ_Automation.Platform.Models.PlatformAuditLog>(e =>
        {
            e.ToTable("PlatformAuditLogs", "platform");
            e.HasKey(x => x.Id);
            e.Property(x => x.Action).IsRequired().HasMaxLength(128);
            e.Property(x => x.TargetType).HasMaxLength(128);
            e.Property(x => x.TargetId).HasMaxLength(128);
            e.Property(x => x.Metadata).HasColumnType("jsonb");
            e.Property(x => x.Ip).HasMaxLength(64);
            e.Property(x => x.Result).IsRequired().HasMaxLength(16)
                .HasDefaultValue(ERP_RFQ_Automation.Platform.Models.PlatformAuditResults.Success);
            e.HasIndex(x => new { x.ActorPlatformUserId, x.CreatedOn });
            e.HasIndex(x => new { x.ActAsTenantId, x.CreatedOn });
        });
        modelBuilder.Entity<ERP_RFQ_Automation.Platform.Models.ImpersonationSession>(e =>
        {
            e.ToTable("ImpersonationSessions", "platform");
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.Jti).IsUnique();
            e.Property(x => x.Jti).IsRequired().HasMaxLength(64);
            e.Property(x => x.Reason).IsRequired().HasMaxLength(1000);
            e.Property(x => x.RevokedBy).HasMaxLength(256);
            e.HasIndex(x => new { x.TenantId, x.IssuedAtUtc });
            e.HasIndex(x => x.ExpiresAtUtc);
        });

        // ==== Sourcing-copilot ("Agent") engine (Agent/) ====
        // Entity configuration + tenant query filters live in the NEW partial file
        // ErpRfqAutomationContext.Agent.cs; this single delegating call is the only
        // splice point (partial methods allow exactly one call site + implementation).
        ConfigureAgentModel(modelBuilder);

        // ==== SLA / deadline engine + quote outcome capture (Sla/) ====
        // Same partial-splice pattern; implementation in ErpRfqAutomationContext.Sla.cs.
        ConfigureSlaModel(modelBuilder);

        // ==== Passive AI metrics + quote revisions (WP-B4, Metrics/) ====
        // Same partial-splice pattern; implementation in ErpRfqAutomationContext.Metrics.cs.
        ConfigureMetricsModel(modelBuilder);

        // ==== Service RFQ → BOQ engine (Boq/) ====
        // Same partial-splice pattern; implementation in ErpRfqAutomationContext.Boq.cs.
        ConfigureBoqModel(modelBuilder);

        // ==== Founding-administrator activation (Platform/Onboarding/) ====
        modelBuilder.ApplyTenantOnboardingModel();

        // ==== Durable tenant provisioning (Platform/Provisioning/) ====
        modelBuilder.ApplyTenantProvisioningModel();

        // ==== Tenant offboarding: retention, export, purge, erasure (Platform/Lifecycle/) ====
        modelBuilder.ApplyTenantLifecycleModel();

        // ==== Operator support desk (Platform/Support/) ====
        modelBuilder.ApplyPlatformSupportModel();

        // ==== Platform outbound email identity (Platform/Notifications/) ====
        modelBuilder.ApplyPlatformEmailModel();
    }

    /// <summary>
    /// Append-only IAM audit trail (RC-7).
    ///
    /// The query filter below is not just a read guard: it auto-enrols this table in
    /// PostgreSqlProductionDialectTests.AllMigrationsApplyToAnEmptyPostgreSqlDatabase, which
    /// enumerates every entity carrying a filter and FAILS unless a matching
    /// <c>nexora_tenant_isolation</c> RLS policy exists with both polqual and polwithcheck
    /// referencing <c>nexora.business_unit_id</c>. That is the intended safety net — see
    /// 20260805120000_IamCanViewAndAccessAuditTrail for the database half.
    /// </summary>
    private void ConfigureIamAuditTrail(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<IamAuditEvent>(entity =>
        {
            entity.ToTable("IamAuditEvents");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Action).HasMaxLength(64).IsRequired();
            entity.Property(e => e.TargetType).HasMaxLength(32).IsRequired();
            entity.Property(e => e.TargetLabel).HasMaxLength(256);
            entity.Property(e => e.Reason).HasMaxLength(512);
            entity.Property(e => e.CorrelationId).HasMaxLength(64);
            entity.Property(e => e.BeforeJson).HasColumnType("jsonb");
            entity.Property(e => e.AfterJson).HasColumnType("jsonb");
            entity.Property(e => e.OccurredOn).HasDefaultValueSql("now()");
            entity.HasIndex(e => new { e.BusinessUnitId, e.OccurredOn })
                .HasDatabaseName("IX_IamAuditEvents_BU_OccurredOn");
            entity.HasIndex(e => new { e.BusinessUnitId, e.TargetType, e.TargetId })
                .HasDatabaseName("IX_IamAuditEvents_BU_Target");

            // ActorUserId and TargetId deliberately carry NO foreign key: the record of who
            // deleted a user must survive that user's deletion. BusinessUnitId does, because a
            // tenant-less audit row is unattributable and falls outside the RLS policy.
            entity.HasOne<BusinessUnit>()
                .WithMany()
                .HasForeignKey(e => e.BusinessUnitId)
                .OnDelete(DeleteBehavior.Restrict);

            entity.HasQueryFilter(e => CurrentTenantId == null || e.BusinessUnitId == CurrentTenantId);
        });
    }
}
