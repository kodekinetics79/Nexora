using ERP_RFQ_Automation.Agent.Models;
using ERP_RFQ_Automation.Inventory.Commercial;
using ERP_RFQ_Automation.Procurement;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.Models;

public partial class ErpRfqAutomationContext
{
    public DbSet<CommercialDemandLine> CommercialDemandLines => Set<CommercialDemandLine>();
    public DbSet<SourcingCase> SourcingCases => Set<SourcingCase>();
    public DbSet<SourcingCaseCandidate> SourcingCaseCandidates => Set<SourcingCaseCandidate>();
    public DbSet<SupplierPurchaseOrder> SupplierPurchaseOrders => Set<SupplierPurchaseOrder>();
    public DbSet<SupplierPurchaseOrderLine> SupplierPurchaseOrderLines => Set<SupplierPurchaseOrderLine>();
    public DbSet<GoodsReceipt> GoodsReceipts => Set<GoodsReceipt>();
    public DbSet<GoodsReceiptLine> GoodsReceiptLines => Set<GoodsReceiptLine>();
    public DbSet<ProcurementEvent> ProcurementEvents => Set<ProcurementEvent>();
    public DbSet<ProcurementOutboxMessage> ProcurementOutboxMessages => Set<ProcurementOutboxMessage>();
    public DbSet<ProcurementHandoff> ProcurementHandoffs => Set<ProcurementHandoff>();
    public DbSet<ProcurementCallbackReceipt> ProcurementCallbackReceipts => Set<ProcurementCallbackReceipt>();

    partial void ConfigureProcurementModel(ModelBuilder modelBuilder);

    partial void ConfigureProcurementModel(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Rfq>().HasAlternateKey(x => new { x.BusinessUnitId, x.Id });
        modelBuilder.Entity<Rfqitem>().HasAlternateKey(x => new { x.Id, x.Rfqid });
        modelBuilder.Entity<Warehouse>().HasAlternateKey(x => new { x.BusinessUnitId, x.Id });
        modelBuilder.Entity<InventoryMovement>().HasAlternateKey(x => new
            { x.BusinessUnitId, x.Id, x.ProductId, x.InventoryId, x.WarehouseId });
        modelBuilder.Entity<IncomingInventory>().HasAlternateKey(x => new { x.BusinessUnitId, x.Id });

        modelBuilder.Entity<SupplierQuotedItem>(entity =>
        {
            entity.HasAlternateKey(x => new { x.BusinessUnitId, x.Id });
            entity.HasCheckConstraint("CK_SupplierQuotedItems_ProcurementValues",
                "(\"UnitPrice\" IS NULL OR \"UnitPrice\" >= 0) AND \"Quantity\" > 0 AND (\"LeadTimeDays\" IS NULL OR \"LeadTimeDays\" >= 0) AND (\"AvailableQuantity\" IS NULL OR \"AvailableQuantity\" >= 0) AND \"FreightCost\" >= 0 AND \"DutyCost\" >= 0 AND \"OtherCost\" >= 0 AND (\"ReliabilitySnapshot\" IS NULL OR (\"ReliabilitySnapshot\" >= 0 AND \"ReliabilitySnapshot\" <= 100))");
            entity.Property(x => x.FreightCost).HasPrecision(18, 4);
            entity.Property(x => x.DutyCost).HasPrecision(18, 4);
            entity.Property(x => x.OtherCost).HasPrecision(18, 4);
            entity.Property(x => x.AvailableQuantity).HasPrecision(18, 4);
            entity.Property(x => x.MinimumOrderQuantity).HasPrecision(18, 4);
            entity.Property(x => x.ReliabilitySnapshot).HasPrecision(5, 2);
            entity.Property(x => x.LandedUnitCost).HasPrecision(18, 4);
            entity.Property(x => x.QuoteRevision).HasDefaultValue(1);
            entity.Property(x => x.ResponseIdempotencyKey).HasMaxLength(160);
            entity.Property(x => x.RequestHash).HasMaxLength(64);
            entity.Property(x => x.Version).HasDefaultValue(1).IsConcurrencyToken();
            entity.HasIndex(x => new { x.BusinessUnitId, x.ResponseIdempotencyKey }).IsUnique()
                .HasFilter("\"ResponseIdempotencyKey\" IS NOT NULL")
                .HasDatabaseName("UX_SupplierQuotedItems_BU_ResponseKey");
            entity.HasIndex(x => new { x.BusinessUnitId, x.RfqId, x.RfqItemId });
            entity.HasIndex(x => new { x.BusinessUnitId, x.SourceSupplierQuoteLineId }).IsUnique()
                .HasFilter("\"SourceSupplierQuoteLineId\" IS NOT NULL")
                .HasDatabaseName("UX_SupplierQuotedItems_BU_SourceQuoteLine");
            entity.HasOne<ERP_RFQ_Automation.SupplierQuotes.SupplierQuote>().WithMany()
                .HasForeignKey(x => new { x.BusinessUnitId, x.SourceSupplierQuoteId })
                .HasPrincipalKey(x => new { x.BusinessUnitId, x.Id }).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<ERP_RFQ_Automation.SupplierQuotes.SupplierQuoteRevision>().WithMany()
                .HasForeignKey(x => new { x.BusinessUnitId, x.SourceSupplierQuoteRevisionId })
                .HasPrincipalKey(x => new { x.BusinessUnitId, x.Id }).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<ERP_RFQ_Automation.SupplierQuotes.SupplierQuoteLine>().WithMany()
                .HasForeignKey(x => new { x.BusinessUnitId, x.SourceSupplierQuoteLineId })
                .HasPrincipalKey(x => new { x.BusinessUnitId, x.Id }).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<CommercialDemandLine>().WithMany()
                .HasForeignKey(x => new { x.BusinessUnitId, x.CommercialDemandLineId })
                .HasPrincipalKey(x => new { x.BusinessUnitId, x.Id }).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<SourcingCase>().WithMany()
                .HasForeignKey(x => new { x.BusinessUnitId, x.SourcingCaseId })
                .HasPrincipalKey(x => new { x.BusinessUnitId, x.Id }).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<SupplierSolicitation>().WithMany()
                .HasForeignKey(x => new { x.BusinessUnitId, x.SupplierSolicitationId })
                .HasPrincipalKey(x => new { x.BusinessUnitId, x.Id }).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<Rfq>().WithMany()
                .HasForeignKey(x => new { x.BusinessUnitId, x.RfqId })
                .HasPrincipalKey(x => new { x.BusinessUnitId, x.Id }).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<Rfqitem>().WithMany()
                .HasForeignKey(x => new { x.RfqItemId, x.RfqId })
                .HasPrincipalKey(x => new { x.Id, x.Rfqid }).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(x => x.Supplier).WithMany(x => x.SupplierQuotedItems)
                .HasForeignKey(x => new { x.SupplierId, x.BusinessUnitId })
                .HasPrincipalKey(x => new { x.Id, x.Buid }).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(x => x.Currency).WithMany(x => x.SupplierQuotedItems)
                .HasForeignKey(x => new { x.BusinessUnitId, x.CurrencyId })
                .HasPrincipalKey(x => new { x.BusinessUnitId, x.Id }).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<Product>().WithMany().HasForeignKey(x => new { x.BusinessUnitId, x.ProductId })
                .HasPrincipalKey(x => new { BusinessUnitId = x.Buid, ProductId = x.Id })
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<SourcingAward>().HasOne<Currency>().WithMany()
            .HasForeignKey(x => new { x.BusinessUnitId, x.CurrencyId })
            .HasPrincipalKey(x => new { x.BusinessUnitId, x.Id }).OnDelete(DeleteBehavior.Restrict);

        ConfigureSourcingCases(modelBuilder);
        ConfigurePurchaseOrders(modelBuilder);
        ConfigureReceipts(modelBuilder);
        ConfigureEventsAndOutbox(modelBuilder);
        ConfigureProcurementHandoffs(modelBuilder);
        // Gate 5 / Module 6. Inbound supplier shipments hang off the supplier purchase order
        // (decision R3), so they are configured as part of the procurement model rather than in a
        // module of their own. See ErpRfqAutomationContext.InboundLogistics.cs.
        ConfigureInboundLogisticsModel(modelBuilder);
    }

    private void ConfigureProcurementHandoffs(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<OrderItem>().HasAlternateKey(x => new { x.Id, x.OrderId });
        modelBuilder.Entity<ProcurementHandoff>(entity =>
        {
            entity.ToTable("procurement_handoffs");
            entity.HasKey(x => x.Id);
            entity.HasAlternateKey(x => new { x.BusinessUnitId, x.Id });
            entity.HasCheckConstraint("CK_procurement_handoffs_Values",
                "\"RequiredQuantity\" > 0 AND \"SelectedUnitCost\" >= 0 AND (\"ExternalOrderedQuantity\" IS NULL OR \"ExternalOrderedQuantity\" > 0) AND (\"ExternalApprovedUnitCost\" IS NULL OR \"ExternalApprovedUnitCost\" >= 0)");
            entity.HasCheckConstraint("CK_procurement_handoffs_Destination",
                "\"DestinationType\" IN ('WAREHOUSE','DROP_SHIP') AND (\"DestinationType\" <> 'WAREHOUSE' OR \"WarehouseId\" IS NOT NULL)");
            entity.HasCheckConstraint("CK_procurement_handoffs_Status",
                "\"Status\" IN ('CREATED','EXTERNAL_PO_CREATED','SUPPLIER_CONFIRMED','DISPATCHED','DELIVERED','PARTIALLY_RECEIVED','RECEIVED','CANCELLED')");
            entity.Property(x => x.NexoraSerial).HasMaxLength(100).IsRequired();
            entity.Property(x => x.DestinationType).HasMaxLength(24).IsRequired();
            entity.Property(x => x.DeliveryLocation).HasMaxLength(500);
            entity.Property(x => x.ExternalSystemTarget).HasMaxLength(100).IsRequired();
            entity.Property(x => x.Status).HasMaxLength(32).IsRequired();
            entity.Property(x => x.IdempotencyKey).HasMaxLength(160).IsRequired();
            entity.Property(x => x.RequestHash).HasMaxLength(64).IsRequired();
            entity.Property(x => x.ExternalSupplierPoNumber).HasMaxLength(160);
            entity.Property(x => x.ExternalSupplierPoLineNumber).HasMaxLength(80);
            entity.Property(x => x.ExternalSalesOrderNumber).HasMaxLength(160);
            entity.Property(x => x.ExternalStatus).HasMaxLength(80);
            entity.Property(x => x.LastExternalEventId).HasMaxLength(160);
            entity.Property(x => x.LastCorrelationId).HasMaxLength(160);
            entity.Property(x => x.SourceOfTruth).HasMaxLength(100);
            entity.Property(x => x.CreatedBy).HasMaxLength(255).IsRequired();
            entity.Property(x => x.ModifiedBy).HasMaxLength(255);
            entity.Property(x => x.RequiredQuantity).HasPrecision(18, 4);
            entity.Property(x => x.SelectedUnitCost).HasPrecision(18, 4);
            entity.Property(x => x.ExternalOrderedQuantity).HasPrecision(18, 4);
            entity.Property(x => x.ExternalApprovedUnitCost).HasPrecision(18, 4);
            entity.Property(x => x.Version).HasDefaultValue(1).IsConcurrencyToken();
            entity.HasIndex(x => new { x.BusinessUnitId, x.CustomerOrderLineId }).IsUnique();
            entity.HasIndex(x => new { x.BusinessUnitId, x.IdempotencyKey }).IsUnique();
            entity.HasIndex(x => new { x.BusinessUnitId, x.ExternalSupplierPoNumber, x.ExternalSupplierPoLineNumber })
                .IsUnique().HasFilter("\"ExternalSupplierPoNumber\" IS NOT NULL");
            entity.HasOne<Order>().WithMany().HasForeignKey(x => new { x.BusinessUnitId, x.CustomerOrderId })
                .HasPrincipalKey(x => new { x.BusinessUnitId, x.Id }).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<OrderItem>().WithMany()
                .HasForeignKey(x => new { x.CustomerOrderLineId, x.CustomerOrderId })
                .HasPrincipalKey(x => new { x.Id, x.OrderId })
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<CommercialDemandLine>().WithMany()
                .HasForeignKey(x => new { x.BusinessUnitId, x.CommercialDemandLineId })
                .HasPrincipalKey(x => new { x.BusinessUnitId, x.Id }).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<SourcingAward>().WithMany().HasForeignKey(x => new { x.BusinessUnitId, x.SourcingAwardId })
                .HasPrincipalKey(x => new { x.BusinessUnitId, x.Id }).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<SupplierQuotedItem>().WithMany()
                .HasForeignKey(x => new { x.BusinessUnitId, x.SupplierQuotedItemId })
                .HasPrincipalKey(x => new { x.BusinessUnitId, x.Id }).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<Supplier>().WithMany().HasForeignKey(x => new { x.SupplierId, x.BusinessUnitId })
                .HasPrincipalKey(x => new { x.Id, x.Buid }).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<Currency>().WithMany().HasForeignKey(x => new { x.BusinessUnitId, x.CurrencyId })
                .HasPrincipalKey(x => new { x.BusinessUnitId, x.Id }).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<Rfq>().WithMany().HasForeignKey(x => new { x.BusinessUnitId, x.RfqId })
                .HasPrincipalKey(x => new { x.BusinessUnitId, x.Id }).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<Rfqitem>().WithMany().HasForeignKey(x => new { x.RfqItemId, x.RfqId })
                .HasPrincipalKey(x => new { x.Id, x.Rfqid }).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<Warehouse>().WithMany().HasForeignKey(x => new { x.BusinessUnitId, x.WarehouseId })
                .HasPrincipalKey(x => new { x.BusinessUnitId, x.Id }).OnDelete(DeleteBehavior.Restrict);
            entity.HasQueryFilter(x => CurrentTenantId == null || x.BusinessUnitId == CurrentTenantId);
        });

        modelBuilder.Entity<ProcurementCallbackReceipt>(entity =>
        {
            entity.ToTable("procurement_callback_receipts");
            entity.HasKey(x => x.Id);
            entity.HasAlternateKey(x => new { x.BusinessUnitId, x.Id });
            entity.Property(x => x.SourceSystem).HasMaxLength(100).IsRequired();
            entity.Property(x => x.ExternalEventId).HasMaxLength(160).IsRequired();
            entity.Property(x => x.PayloadHash).HasMaxLength(64).IsRequired();
            entity.Property(x => x.CorrelationId).HasMaxLength(160).IsRequired();
            entity.Property(x => x.Status).HasMaxLength(24).IsRequired();
            entity.Property(x => x.RejectionCode).HasMaxLength(120);
            entity.Property(x => x.ObservedQuantity).HasPrecision(18, 4);
            entity.Property(x => x.ObservedUnitCost).HasPrecision(18, 4);
            entity.Property(x => x.ObservedStatus).HasMaxLength(80).IsRequired();
            entity.HasCheckConstraint("CK_procurement_callback_receipts_Status", "\"Status\" IN ('APPLIED','REJECTED')");
            entity.HasIndex(x => new { x.BusinessUnitId, x.SourceSystem, x.ExternalEventId }).IsUnique();
            entity.HasIndex(x => new { x.BusinessUnitId, x.ProcurementHandoffId, x.ReceivedOn });
            entity.HasOne<ProcurementHandoff>().WithMany()
                .HasForeignKey(x => new { x.BusinessUnitId, x.ProcurementHandoffId })
                .HasPrincipalKey(x => new { x.BusinessUnitId, x.Id }).OnDelete(DeleteBehavior.Restrict);
            entity.HasQueryFilter(x => CurrentTenantId == null || x.BusinessUnitId == CurrentTenantId);
        });
    }

    private void ConfigureSourcingCases(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<CommercialDemandLine>(entity =>
        {
            entity.ToTable("commercial_demand_lines");
            entity.HasKey(x => x.Id);
            entity.HasAlternateKey(x => new { x.BusinessUnitId, x.Id });
            entity.Property(x => x.NexoraSerial).HasMaxLength(100).IsRequired();
            entity.Property(x => x.IdentityKey).HasMaxLength(160).IsRequired();
            entity.Property(x => x.CreatedBy).HasMaxLength(255).IsRequired();
            entity.HasIndex(x => new { x.BusinessUnitId, x.RfqItemId }).IsUnique();
            entity.HasIndex(x => new { x.BusinessUnitId, x.IdentityKey }).IsUnique();
            entity.HasOne<Rfq>().WithMany().HasForeignKey(x => new { x.BusinessUnitId, x.RfqId })
                .HasPrincipalKey(x => new { x.BusinessUnitId, x.Id }).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<Rfqitem>().WithMany().HasForeignKey(x => new { x.RfqItemId, x.RfqId })
                .HasPrincipalKey(x => new { x.Id, x.Rfqid }).OnDelete(DeleteBehavior.Restrict);
            entity.HasQueryFilter(x => CurrentTenantId == null || x.BusinessUnitId == CurrentTenantId);
        });

        modelBuilder.Entity<SourcingCase>(entity =>
        {
            entity.ToTable("sourcing_cases");
            entity.HasKey(x => x.Id);
            entity.HasAlternateKey(x => new { x.BusinessUnitId, x.Id });
            entity.HasCheckConstraint("CK_sourcing_cases_Quantities",
                "\"RequestedQuantity\" > 0 AND \"StockQuantity\" >= 0 AND \"UnfulfilledQuantity\" > 0 AND \"UnfulfilledQuantity\" <= \"RequestedQuantity\"");
            entity.HasCheckConstraint("CK_sourcing_cases_SearchLimit", "\"SearchLimit\" IN (10,20,50)");
            entity.HasCheckConstraint("CK_sourcing_cases_Status",
                "\"Status\" IN ('DRAFT','INTERNAL_SEARCH','DISCOVERY_REQUIRED','SUPPLIERS_SELECTED','OUTREACH_READY','OUTREACH_SENT','RESPONSES_PARTIAL','RESPONSES_COMPLETE','COMPARISON_READY','NEGOTIATION','AWARD_REVIEW','SUPPLIER_SELECTED','CUSTOMER_QUOTE_READY','CLOSED','CANCELLED')");
            entity.Property(x => x.NexoraSerial).HasMaxLength(100).IsRequired();
            entity.Property(x => x.RequestedPartNumber).HasMaxLength(255);
            entity.Property(x => x.Manufacturer).HasMaxLength(255);
            entity.Property(x => x.Description).HasMaxLength(1000).IsRequired();
            entity.Property(x => x.UnitOfMeasure).HasMaxLength(80);
            entity.Property(x => x.DeliveryLocation).HasMaxLength(500);
            entity.Property(x => x.Priority).HasMaxLength(24).IsRequired();
            entity.Property(x => x.Status).HasMaxLength(32).IsRequired();
            entity.Property(x => x.NextAction).HasMaxLength(255).IsRequired();
            entity.Property(x => x.ShortageDecisionKey).HasMaxLength(64).IsRequired();
            entity.Property(x => x.IdempotencyKey).HasMaxLength(160).IsRequired();
            entity.Property(x => x.RequestHash).HasMaxLength(64).IsRequired();
            entity.Property(x => x.CreatedBy).HasMaxLength(255).IsRequired();
            entity.Property(x => x.UpdatedBy).HasMaxLength(255).IsRequired();
            entity.Property(x => x.RequestedQuantity).HasPrecision(18, 4);
            entity.Property(x => x.StockQuantity).HasPrecision(18, 4);
            entity.Property(x => x.UnfulfilledQuantity).HasPrecision(18, 4);
            entity.Property(x => x.Version).HasDefaultValue(1).IsConcurrencyToken();
            entity.HasIndex(x => new { x.BusinessUnitId, x.IdempotencyKey }).IsUnique();
            entity.HasIndex(x => new { x.BusinessUnitId, x.CommercialDemandLineId, x.ShortageDecisionKey }).IsUnique();
            entity.HasOne<CommercialDemandLine>().WithMany()
                .HasForeignKey(x => new { x.BusinessUnitId, x.CommercialDemandLineId })
                .HasPrincipalKey(x => new { x.BusinessUnitId, x.Id }).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<Rfq>().WithMany().HasForeignKey(x => new { x.BusinessUnitId, x.RfqId })
                .HasPrincipalKey(x => new { x.BusinessUnitId, x.Id }).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<Rfqitem>().WithMany().HasForeignKey(x => new { x.RfqItemId, x.RfqId })
                .HasPrincipalKey(x => new { x.Id, x.Rfqid }).OnDelete(DeleteBehavior.Restrict);
            entity.HasQueryFilter(x => CurrentTenantId == null || x.BusinessUnitId == CurrentTenantId);
        });

        modelBuilder.Entity<SourcingCaseCandidate>(entity =>
        {
            entity.ToTable("sourcing_case_candidates");
            entity.HasKey(x => x.Id);
            entity.HasCheckConstraint("CK_sourcing_case_candidates_RankScore",
                "\"Rank\" > 0 AND \"EvidenceScore\" >= 0 AND \"EvidenceScore\" <= 100");
            entity.Property(x => x.EvidenceType).HasMaxLength(40).IsRequired();
            entity.Property(x => x.RecommendationReason).HasMaxLength(500).IsRequired();
            entity.Property(x => x.EvidenceJson).HasColumnType("jsonb");
            entity.Property(x => x.EvidenceScore).HasPrecision(5, 2);
            entity.HasIndex(x => new { x.BusinessUnitId, x.SourcingCaseId, x.SupplierId }).IsUnique();
            entity.HasIndex(x => new { x.BusinessUnitId, x.SourcingCaseId, x.Rank });
            entity.HasOne<SourcingCase>().WithMany(x => x.Candidates)
                .HasForeignKey(x => new { x.BusinessUnitId, x.SourcingCaseId })
                .HasPrincipalKey(x => new { x.BusinessUnitId, x.Id }).OnDelete(DeleteBehavior.Cascade);
            entity.HasOne<Supplier>().WithMany().HasForeignKey(x => new { x.SupplierId, x.BusinessUnitId })
                .HasPrincipalKey(x => new { x.Id, x.Buid }).OnDelete(DeleteBehavior.Restrict);
            entity.HasQueryFilter(x => CurrentTenantId == null || x.BusinessUnitId == CurrentTenantId);
        });
    }

    private void ConfigurePurchaseOrders(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<SupplierPurchaseOrder>(entity =>
        {
            entity.ToTable("supplier_purchase_orders");
            entity.HasCheckConstraint("CK_supplier_purchase_orders_TotalValue", "\"TotalValue\" >= 0");
            // The eight states FR-SPO-04 names, plus the legacy ISSUED that existing rows carry
            // and the two additions a real order needs. This constraint is the reason the status
            // set could not simply be widened in code: Postgres would have rejected every new
            // transition. It is invisible in the SQLite lane, which runs with check constraints
            // disabled, so a green suite would have shipped an order that could not move.
            entity.HasCheckConstraint("CK_supplier_purchase_orders_Status",
                "\"Status\" IN ('DRAFT','APPROVED','SENT','ACKNOWLEDGED','IN_PRODUCTION','SHIPPED'," +
                "'ISSUED','PARTIALLY_RECEIVED','RECEIVED','CLOSED','CANCELLED')");
            // An acknowledgement is one of three answers or absent; a counter-offered lead time
            // that is zero or negative is not a lead time.
            entity.HasCheckConstraint("CK_supplier_purchase_orders_Acknowledgement",
                "\"AcknowledgementStatus\" IS NULL OR \"AcknowledgementStatus\" IN ('ACCEPTED','REJECTED','COUNTERED')");
            entity.HasCheckConstraint("CK_supplier_purchase_orders_RevisedLeadTime",
                "\"RevisedLeadTimeDays\" IS NULL OR \"RevisedLeadTimeDays\" > 0");
            // The three approval columns are one fact and must be written together. Application
            // code is already the sole writer and sets all three in one transaction; this makes a
            // half-approved row impossible rather than merely unlikely.
            entity.HasCheckConstraint("CK_supplier_purchase_orders_Approval",
                "(\"ApprovedByUserId\" IS NULL AND \"ApprovedBy\" IS NULL AND \"ApprovedOn\" IS NULL) " +
                "OR (\"ApprovedByUserId\" IS NOT NULL AND \"ApprovedBy\" IS NOT NULL AND \"ApprovedOn\" IS NOT NULL)");
            entity.Property(x => x.ApprovedBy).HasMaxLength(255);
            entity.Property(x => x.AcknowledgedBy).HasMaxLength(255);
            entity.Property(x => x.AcknowledgementStatus).HasMaxLength(16);
            entity.Property(x => x.AcknowledgementNote).HasMaxLength(1000);
            entity.Property(x => x.Incoterm).HasMaxLength(40);
            entity.Property(x => x.PortOfLoading).HasMaxLength(120);
            entity.Property(x => x.PortOfDischarge).HasMaxLength(120);
            entity.HasKey(x => x.Id);
            entity.HasAlternateKey(x => new { x.BusinessUnitId, x.Id });
            entity.Property(x => x.PurchaseOrderNumber).HasMaxLength(80).IsRequired();
            entity.Property(x => x.Status).HasMaxLength(32).IsRequired();
            entity.Property(x => x.TotalValue).HasPrecision(18, 4);
            entity.Property(x => x.IdempotencyKey).HasMaxLength(160).IsRequired();
            entity.Property(x => x.RequestHash).HasMaxLength(64).IsRequired();
            entity.Property(x => x.Version).HasDefaultValue(1).IsConcurrencyToken();
            entity.Property(x => x.CreatedBy).HasMaxLength(255).IsRequired();
            entity.Property(x => x.ModifiedBy).HasMaxLength(255);
            // FR-SPO-05. The commercial case is a read key, not decoration: CommercialCaseQueryService
            // selects supplier orders that DECLARE the case rather than deriving them through the RFQ,
            // so this index carries the case-detail page. The foreign key is what makes a wrong case id
            // impossible rather than merely visible; it stays nullable because a STOCK replenishment
            // order legitimately has no customer case behind it.
            entity.HasIndex(x => new { x.BusinessUnitId, x.CommercialCaseId });
            entity.HasOne<CommercialCase>().WithMany()
                .HasForeignKey(x => new { x.BusinessUnitId, x.CommercialCaseId })
                .HasPrincipalKey(x => new { x.BusinessUnitId, x.Id })
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasIndex(x => new { x.BusinessUnitId, x.PurchaseOrderNumber }).IsUnique();
            entity.HasIndex(x => new { x.BusinessUnitId, x.IdempotencyKey }).IsUnique();
            entity.HasOne<Rfq>().WithMany().HasForeignKey(x => new { x.BusinessUnitId, x.RfqId })
                .HasPrincipalKey(x => new { x.BusinessUnitId, x.Id }).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<Supplier>().WithMany().HasForeignKey(x => new { x.SupplierId, x.BusinessUnitId })
                .HasPrincipalKey(x => new { x.Id, x.Buid }).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<Currency>().WithMany().HasForeignKey(x => new { x.BusinessUnitId, x.CurrencyId })
                .HasPrincipalKey(x => new { x.BusinessUnitId, x.Id }).OnDelete(DeleteBehavior.Restrict);
            entity.HasQueryFilter(x => CurrentTenantId == null || x.BusinessUnitId == CurrentTenantId);
        });

        modelBuilder.Entity<SupplierPurchaseOrderLine>(entity =>
        {
            entity.ToTable("supplier_purchase_order_lines");
            entity.HasCheckConstraint("CK_supplier_purchase_order_lines_Quantities",
                "\"OrderedQuantity\" > 0 AND \"ReceivedQuantity\" >= 0 AND \"ReceivedQuantity\" <= \"OrderedQuantity\"");
            // Landed cost subtracts the supplier's discount, so a discounted line legitimately
            // lands BELOW its own list price. The old form asserted "LandedUnitCost >= UnitCost",
            // an invariant commerce does not obey, and it became easier to violate once
            // recoverable input VAT stopped inflating landed cost (R15/R16). What is genuinely
            // invariant is positivity, which is also what the service enforces before it writes.
            entity.HasCheckConstraint("CK_supplier_purchase_order_lines_Costs",
                "\"UnitCost\" >= 0 AND \"LandedUnitCost\" > 0");
            entity.HasKey(x => x.Id);
            entity.HasAlternateKey(x => new { x.BusinessUnitId, x.Id });
            entity.HasAlternateKey(x => new { x.BusinessUnitId, x.Id, x.ProductId, x.WarehouseId });
            // Bounded like every other string on this table. These shipped as unbounded `text`
            // while the application already enforced 20 and 100; the column now agrees with the
            // rule instead of trusting the only caller to keep applying it.
            entity.Property(x => x.HsCode).HasMaxLength(20);
            entity.Property(x => x.CountryOfOrigin).HasMaxLength(100);
            entity.Property(x => x.OrderedQuantity).HasPrecision(18, 4);
            entity.Property(x => x.ReceivedQuantity).HasPrecision(18, 4);
            entity.Property(x => x.UnitCost).HasPrecision(18, 4);
            entity.Property(x => x.LandedUnitCost).HasPrecision(18, 4);
            entity.Property(x => x.Version).HasDefaultValue(1).IsConcurrencyToken();
            entity.HasIndex(x => new { x.BusinessUnitId, x.SourcingAwardId }).IsUnique();
            entity.HasOne<SupplierPurchaseOrder>().WithMany(x => x.Lines)
                .HasForeignKey(x => new { x.BusinessUnitId, x.SupplierPurchaseOrderId })
                .HasPrincipalKey(x => new { x.BusinessUnitId, x.Id }).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<SourcingAward>().WithMany()
                .HasForeignKey(x => new { x.BusinessUnitId, x.SourcingAwardId })
                .HasPrincipalKey(x => new { x.BusinessUnitId, x.Id }).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<SupplierQuotedItem>().WithMany()
                .HasForeignKey(x => new { x.BusinessUnitId, x.SupplierQuotedItemId })
                .HasPrincipalKey(x => new { x.BusinessUnitId, x.Id }).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<Rfq>().WithMany().HasForeignKey(x => new { x.BusinessUnitId, x.RfqId })
                .HasPrincipalKey(x => new { x.BusinessUnitId, x.Id }).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<Rfqitem>().WithMany().HasForeignKey(x => new { x.RfqItemId, x.RfqId })
                .HasPrincipalKey(x => new { x.Id, x.Rfqid }).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<Product>().WithMany().HasForeignKey(x => new { x.BusinessUnitId, x.ProductId })
                .HasPrincipalKey(x => new { BusinessUnitId = x.Buid, ProductId = x.Id })
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<Warehouse>().WithMany().HasForeignKey(x => new { x.BusinessUnitId, x.WarehouseId })
                .HasPrincipalKey(x => new { x.BusinessUnitId, x.Id }).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<Models.Inventory>().WithMany().HasForeignKey(x => new { x.BusinessUnitId, x.InventoryId })
                .HasPrincipalKey(x => new { BusinessUnitId = x.Buid, InventoryId = x.Id })
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<IncomingInventory>().WithMany().HasForeignKey(x => new { x.BusinessUnitId, x.IncomingInventoryId })
                .HasPrincipalKey(x => new { x.BusinessUnitId, x.Id }).OnDelete(DeleteBehavior.Restrict);
            entity.HasQueryFilter(x => CurrentTenantId == null || x.BusinessUnitId == CurrentTenantId);
        });
    }

    private void ConfigureReceipts(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<GoodsReceipt>(entity =>
        {
            entity.ToTable("goods_receipts"); entity.HasKey(x => x.Id);
            entity.HasAlternateKey(x => new { x.BusinessUnitId, x.Id });
            entity.Property(x => x.ReceiptNumber).HasMaxLength(80).IsRequired();
            entity.Property(x => x.Status).HasMaxLength(24).IsRequired();
            entity.Property(x => x.IdempotencyKey).HasMaxLength(160).IsRequired();
            entity.Property(x => x.RequestHash).HasMaxLength(64).IsRequired();
            entity.Property(x => x.Version).HasDefaultValue(1).IsConcurrencyToken();
            entity.Property(x => x.CreatedBy).HasMaxLength(255).IsRequired();
            entity.HasIndex(x => new { x.BusinessUnitId, x.ReceiptNumber }).IsUnique();
            entity.HasIndex(x => new { x.BusinessUnitId, x.IdempotencyKey }).IsUnique();
            entity.HasOne<SupplierPurchaseOrder>().WithMany()
                .HasForeignKey(x => new { x.BusinessUnitId, x.SupplierPurchaseOrderId })
                .HasPrincipalKey(x => new { x.BusinessUnitId, x.Id }).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<Warehouse>().WithMany().HasForeignKey(x => new { x.BusinessUnitId, x.WarehouseId })
                .HasPrincipalKey(x => new { x.BusinessUnitId, x.Id }).OnDelete(DeleteBehavior.Restrict);
            entity.HasQueryFilter(x => CurrentTenantId == null || x.BusinessUnitId == CurrentTenantId);
        });
        modelBuilder.Entity<GoodsReceiptLine>(entity =>
        {
            entity.ToTable("goods_receipt_lines"); entity.HasKey(x => x.Id);
            entity.HasCheckConstraint("CK_goods_receipt_lines_Quantity", "\"ReceivedQuantity\" > 0");
            entity.HasIndex(x => x.InventoryMovementId).IsUnique()
                .HasDatabaseName("UX_goods_receipt_lines_InventoryMovementId");
            entity.Property(x => x.ReceivedQuantity).HasPrecision(18, 4);
            entity.HasOne<GoodsReceipt>().WithMany(x => x.Lines)
                .HasForeignKey(x => new { x.BusinessUnitId, x.GoodsReceiptId })
                .HasPrincipalKey(x => new { x.BusinessUnitId, x.Id }).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<SupplierPurchaseOrderLine>().WithMany()
                .HasForeignKey(x => new { x.BusinessUnitId, x.SupplierPurchaseOrderLineId, x.ProductId, x.WarehouseId })
                .HasPrincipalKey(x => new { x.BusinessUnitId, x.Id, x.ProductId, x.WarehouseId }).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<InventoryMovement>().WithMany()
                .HasForeignKey(x => new { x.BusinessUnitId, x.InventoryMovementId, x.ProductId, x.InventoryId, x.WarehouseId })
                .HasPrincipalKey(x => new { x.BusinessUnitId, x.Id, x.ProductId, x.InventoryId, x.WarehouseId })
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasQueryFilter(x => CurrentTenantId == null || x.BusinessUnitId == CurrentTenantId);
        });
    }

    private void ConfigureEventsAndOutbox(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ProcurementEvent>(entity =>
        {
            entity.ToTable("procurement_events"); entity.HasKey(x => x.Id);
            entity.Property(x => x.AggregateType).HasMaxLength(80).IsRequired();
            entity.Property(x => x.EventType).HasMaxLength(80).IsRequired();
            entity.Property(x => x.Actor).HasMaxLength(255).IsRequired();
            entity.Property(x => x.CorrelationId).HasMaxLength(160).IsRequired();
            entity.Property(x => x.IdempotencyKey).HasMaxLength(160).IsRequired();
            entity.Property(x => x.PayloadJson).HasColumnType("jsonb");
            entity.HasIndex(x => new { x.BusinessUnitId, x.EventType, x.IdempotencyKey }).IsUnique();
            entity.HasIndex(x => new { x.BusinessUnitId, x.AggregateType, x.AggregateId, x.OccurredOn });
            entity.HasOne<BusinessUnit>().WithMany().HasForeignKey(x => x.BusinessUnitId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasQueryFilter(x => CurrentTenantId == null || x.BusinessUnitId == CurrentTenantId);
        });
        modelBuilder.Entity<ProcurementOutboxMessage>(entity =>
        {
            entity.ToTable("procurement_outbox"); entity.HasKey(x => x.Id);
            entity.Property(x => x.MessageType).HasMaxLength(80).IsRequired();
            entity.Property(x => x.Status).HasMaxLength(24).IsRequired();
            entity.Property(x => x.PayloadJson).HasColumnType("jsonb");
            entity.Property(x => x.ProviderReference).HasMaxLength(255);
            entity.Property(x => x.ProviderName).HasMaxLength(100);
            entity.Property(x => x.LeaseOwner).HasMaxLength(200);
            entity.Property(x => x.OriginCorrelationId).HasMaxLength(160);
            entity.Property(x => x.LastErrorCode).HasMaxLength(120);
            entity.HasIndex(x => new { x.BusinessUnitId, x.SupplierSolicitationId }).IsUnique();
            entity.HasIndex(x => new { x.Status, x.NextAttemptOn });
            entity.HasIndex(x => new { x.BusinessUnitId, x.DeadLetteredOn, x.NextAttemptOn });
            entity.HasOne<SupplierSolicitation>().WithMany()
                .HasForeignKey(x => new { x.BusinessUnitId, x.SupplierSolicitationId })
                .HasPrincipalKey(x => new { x.BusinessUnitId, x.Id }).OnDelete(DeleteBehavior.Restrict);
            entity.HasQueryFilter(x => CurrentTenantId == null || x.BusinessUnitId == CurrentTenantId);
        });
    }
}
