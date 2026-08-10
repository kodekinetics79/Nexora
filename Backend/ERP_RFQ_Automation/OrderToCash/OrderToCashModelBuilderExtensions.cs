using ERP_RFQ_Automation.Models;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.OrderToCash;

public static class OrderToCashModelBuilderExtensions
{
    /// <summary>
    /// The sentinel for every settable number on <see cref="CommercialMatchingPolicy"/>. Negative:
    /// none of those fields — two percentages, a tolerance percentage and a tolerance amount — can
    /// legitimately hold it, and the tax percentages have a check constraint that forbids it, so it
    /// can never collide with a value a customer chose.
    ///
    /// <para><b>Why this is necessary.</b> EF omits a property from an INSERT when its value equals
    /// the property's SENTINEL, letting the database default supply it — and the sentinel defaults
    /// to the CLR default. For <c>decimal</c> that is 0, and for <c>decimal?</c> it is null, so a
    /// tenant saving "0% of supplier tax is recoverable" or "we have no output tax rate" wrote a row
    /// that came back reading 100 and 15. The value the customer chose was silently replaced by the
    /// default it was chosen to override — on the exact fields decision R18 and R17 exist to make
    /// settable. Pinning the sentinel to an impossible number makes EF write every value the caller
    /// actually set, while the database defaults still backfill existing rows on migration.</para>
    /// </summary>
    private const decimal NotAPercentage = -1m;

    public static void ConfigureOrderToCash(this ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<BusinessUnit>().HasAlternateKey(x => new { x.Id });
        modelBuilder.Entity<CommercialCase>().HasAlternateKey(x => new { x.BusinessUnitId, x.Id });
        modelBuilder.Entity<Quote>().HasAlternateKey(x => new { x.BusinessUnitId, x.Id });
        modelBuilder.Entity<Currency>().HasAlternateKey(x => new { x.BusinessUnitId, x.Id });
        modelBuilder.Entity<SetUom>().HasAlternateKey(x => new { x.BusinessUnitId, x.UomId });

        ConfigurePurchaseOrders(modelBuilder);
        ConfigureAwards(modelBuilder);
        ConfigureOrderSources(modelBuilder);
        ConfigureGovernance(modelBuilder);
    }

    private static void ConfigureOrderSources(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Order>(entity =>
        {
            entity.Property(x => x.SourceType)
                .HasColumnName("SourceType")
                .HasMaxLength(24)
                .HasDefaultValue(OrderSourceTypes.Manual)
                .IsRequired();
            entity.Property(x => x.CustomerAwardId).HasColumnName("CustomerAwardID");
            entity.HasCheckConstraint("CK_Orders_SourceType",
                "\"SourceType\" IN ('MANUAL','LEGACY_QUOTE','CUSTOMER_AWARD')");
            entity.HasCheckConstraint("CK_Orders_SourceIdentity",
                "(\"SourceType\" = 'MANUAL' AND \"QuoteID\" IS NULL AND \"CustomerAwardID\" IS NULL) OR (\"SourceType\" = 'LEGACY_QUOTE' AND \"QuoteID\" IS NOT NULL AND \"CustomerAwardID\" IS NULL) OR (\"SourceType\" = 'CUSTOMER_AWARD' AND \"QuoteID\" IS NOT NULL AND \"CustomerAwardID\" IS NOT NULL)");
            entity.HasIndex(x => new { x.BusinessUnitId, x.CustomerAwardId })
                .IsUnique()
                .HasFilter("\"CustomerAwardID\" IS NOT NULL")
                .HasDatabaseName("UX_Orders_BU_CustomerAwardID");
            entity.HasOne(x => x.CustomerAward).WithMany(x => x.Orders)
                .HasForeignKey(x => new { x.BusinessUnitId, x.CustomerAwardId })
                .HasPrincipalKey(x => new { x.BusinessUnitId, x.Id })
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<OrderItem>(entity =>
        {
            entity.Property(x => x.CustomerAwardLineAllocationId)
                .HasColumnName("CustomerAwardLineAllocationID");
            entity.HasIndex(x => x.CustomerAwardLineAllocationId)
                .IsUnique()
                .HasFilter("\"CustomerAwardLineAllocationID\" IS NOT NULL")
                .HasDatabaseName("UX_OrderItems_CustomerAwardLineAllocationID");
            entity.HasOne(x => x.CustomerAwardLineAllocation).WithMany()
                .HasForeignKey(x => x.CustomerAwardLineAllocationId)
                .OnDelete(DeleteBehavior.Restrict);
        });
    }

    private static void ConfigurePurchaseOrders(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<CommercialMatchingPolicy>(entity =>
        {
            entity.ToTable("CommercialMatchingPolicies");
            entity.HasKey(x => x.Id);
            entity.HasAlternateKey(x => new { x.BusinessUnitId, x.Id });
            // One policy per tenant. The unique index is what makes "create on demand" safe under
            // concurrency: two requests racing to create the first row leaves one winner.
            entity.HasIndex(x => x.BusinessUnitId).IsUnique();
            // KSA default: a VAT-registered tenant making taxable supplies reclaims ALL supplier
            // input tax, so none of it is a cost and none of it enters landed cost. A partly exempt
            // trader sets its recovery ratio and the irrecoverable share becomes cost (R18).
            // See CommercialMatchingPolicy.SupplierInputTaxRecoverablePercent.
            entity.Property(x => x.SupplierInputTaxRecoverablePercent).HasPrecision(9, 4)
                .HasDefaultValue(CommercialMatchingPolicy.FullyRecoverablePercent)
                .HasSentinel(NotAPercentage);
            // Nullable on purpose: null is "no rate stated", which derives NO tax and blocks the
            // send, whereas 0 is the positive assertion "our rate is zero". Defaulting to the KSA
            // standard rate keeps every existing tenant correct without a data migration.
            entity.Property(x => x.OutputTaxRatePercent).HasPrecision(9, 4)
                .HasDefaultValue(CommercialMatchingPolicy.KsaStandardOutputTaxRatePercent)
                .HasSentinel(NotAPercentage);
            // Both rates are percentages and neither is meaningful outside 0-100. Enforced in the
            // database as well as the service, because the value re-bases every landed cost and
            // every derived tax computed after it and there is no safe out-of-range reading.
            // The CAST is not decoration. SQLite — which the portable test lane builds this schema
            // on — has no decimal type and stores these columns as TEXT, and SQLite orders TEXT
            // above every number, so a bare `"OutputTaxRatePercent" <= 100` is FALSE for every
            // value and the constraint rejects 15. On PostgreSQL the cast is a no-op against a
            // numeric column. Written this way the SAME invariant is enforced in both lanes rather
            // than being certified only under Docker.
            entity.ToTable(table =>
            {
                // Named short on purpose: the obvious name is 64 characters and PostgreSQL truncates
                // identifiers at 63, which produced "…RecoverablePerce~" in the emitted DDL.
                table.HasCheckConstraint("CK_CommercialMatchingPolicies_InputTaxRecoverablePercent",
                    "CAST(\"SupplierInputTaxRecoverablePercent\" AS numeric) >= 0 AND " +
                    "CAST(\"SupplierInputTaxRecoverablePercent\" AS numeric) <= 100");
                table.HasCheckConstraint("CK_CommercialMatchingPolicies_OutputTaxRatePercent",
                    "\"OutputTaxRatePercent\" IS NULL OR (CAST(\"OutputTaxRatePercent\" AS numeric) >= 0 AND " +
                    "CAST(\"OutputTaxRatePercent\" AS numeric) <= 100)");
            });
            entity.Property(x => x.PriceTolerancePercent).HasPrecision(9, 4).HasDefaultValue(2.0m)
                .HasSentinel(NotAPercentage);
            entity.Property(x => x.PriceToleranceMinimumAmount).HasPrecision(18, 6).HasDefaultValue(0m)
                .HasSentinel(NotAPercentage);
            entity.Property(x => x.QuantityTolerancePercent).HasPrecision(9, 4).HasDefaultValue(0m)
                .HasSentinel(NotAPercentage);
            entity.Property(x => x.Version).HasDefaultValue(1).IsConcurrencyToken();
            entity.Property(x => x.CreatedOn).HasDefaultValueSql("now()");
            entity.Property(x => x.ModifiedBy).HasMaxLength(255);
            // Tenant query filter lives in ErpRfqAutomationContext.Tenancy.cs with every other
            // one, so the set of filtered entities is readable in a single place.
        });

        modelBuilder.Entity<CustomerPurchaseOrder>(entity =>
        {
            entity.ToTable("CustomerPurchaseOrders");
            entity.HasKey(x => x.Id);
            entity.HasAlternateKey(x => new { x.BusinessUnitId, x.Id });
            entity.Property(x => x.InternalNumber).HasMaxLength(50).IsRequired();
            entity.Property(x => x.ExternalPoNumber).HasMaxLength(200).IsRequired();
            entity.Property(x => x.NormalizedExternalPoNumber).HasMaxLength(200).IsRequired();
            entity.Property(x => x.Status).HasMaxLength(24).IsRequired();
            entity.Property(x => x.Version).HasDefaultValue(1).IsConcurrencyToken();
            entity.Property(x => x.CreatedOn).HasDefaultValueSql("now()");
            entity.Property(x => x.CreatedBy).HasMaxLength(255).IsRequired();
            entity.Property(x => x.ModifiedBy).HasMaxLength(255);
            entity.Property(x => x.CancellationReason).HasMaxLength(500);
            entity.HasIndex(x => new { x.BusinessUnitId, x.InternalNumber }).IsUnique()
                .HasDatabaseName("UX_CustomerPurchaseOrders_BU_InternalNumber");
            entity.HasIndex(x => new { x.BusinessUnitId, x.CustomerId, x.NormalizedExternalPoNumber }).IsUnique()
                .HasDatabaseName("UX_CustomerPurchaseOrders_BU_Customer_ExternalNumber");
            entity.HasIndex(x => new { x.BusinessUnitId, x.CommercialCaseId, x.Status })
                .HasDatabaseName("IX_CustomerPurchaseOrders_BU_Case_Status");
            entity.HasCheckConstraint("CK_CustomerPurchaseOrders_ExternalNumber",
                "length(trim(\"ExternalPoNumber\")) > 0 AND length(trim(\"NormalizedExternalPoNumber\")) > 0");
            entity.HasCheckConstraint("CK_CustomerPurchaseOrders_Version", "\"Version\" >= 1");
            entity.HasCheckConstraint("CK_CustomerPurchaseOrders_Status",
                "\"Status\" IN ('DRAFT','CONFIRMED','PARTIALLY_AWARDED','FULLY_AWARDED','CLOSED','CANCELLED')");
            entity.HasCheckConstraint("CK_CustomerPurchaseOrders_Cancellation",
                "(\"Status\" = 'CANCELLED' AND length(trim(COALESCE(\"CancellationReason\", ''))) > 0) OR (\"Status\" <> 'CANCELLED' AND \"CancellationReason\" IS NULL)");
            entity.HasOne(x => x.BusinessUnit).WithMany()
                .HasForeignKey(x => x.BusinessUnitId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(x => x.CommercialCase).WithMany()
                .HasForeignKey(x => new { x.BusinessUnitId, x.CommercialCaseId })
                .HasPrincipalKey(x => new { x.BusinessUnitId, x.Id }).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(x => x.Customer).WithMany()
                .HasForeignKey(x => x.CustomerId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(x => x.Currency).WithMany()
                .HasForeignKey(x => new { x.BusinessUnitId, x.CurrencyId })
                .HasPrincipalKey(x => new { x.BusinessUnitId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<CustomerPurchaseOrderLine>(entity =>
        {
            entity.ToTable("CustomerPurchaseOrderLines");
            entity.HasKey(x => x.Id);
            entity.HasAlternateKey(x => new { x.BusinessUnitId, x.Id });
            entity.Property(x => x.ExternalLineReference).HasMaxLength(100).IsRequired();
            entity.Property(x => x.Description).HasMaxLength(1000).IsRequired();
            entity.Property(x => x.OrderedQuantity).HasPrecision(18, 6);
            entity.Property(x => x.UnitPrice).HasPrecision(18, 4);
            entity.Property(x => x.LineAmount).HasPrecision(18, 2);
            entity.Property(x => x.Version).HasDefaultValue(1).IsConcurrencyToken();
            entity.HasIndex(x => new { x.BusinessUnitId, x.CustomerPurchaseOrderId, x.ExternalLineReference }).IsUnique()
                .HasDatabaseName("UX_CustomerPurchaseOrderLines_PO_ExternalReference");
            entity.HasCheckConstraint("CK_CustomerPurchaseOrderLines_ExternalReference",
                "length(trim(\"ExternalLineReference\")) > 0");
            entity.HasCheckConstraint("CK_CustomerPurchaseOrderLines_Description",
                "length(trim(\"Description\")) > 0");
            entity.HasCheckConstraint("CK_CustomerPurchaseOrderLines_QuantityMoney",
                "\"OrderedQuantity\" > 0 AND (\"UnitPrice\" IS NULL OR \"UnitPrice\" >= 0) AND (\"LineAmount\" IS NULL OR \"LineAmount\" >= 0)");
            entity.HasCheckConstraint("CK_CustomerPurchaseOrderLines_Version", "\"Version\" >= 1");
            entity.HasOne(x => x.PurchaseOrder).WithMany(x => x.Lines)
                .HasForeignKey(x => new { x.BusinessUnitId, x.CustomerPurchaseOrderId })
                .HasPrincipalKey(x => new { x.BusinessUnitId, x.Id }).OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(x => x.Product).WithMany()
                .HasForeignKey(x => x.ProductId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(x => x.Uom).WithMany()
                .HasForeignKey(x => new { x.BusinessUnitId, x.UomId })
                .HasPrincipalKey(x => new { x.BusinessUnitId, x.UomId }).OnDelete(DeleteBehavior.Restrict);
        });
    }

    private static void ConfigureAwards(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<CustomerAward>(entity =>
        {
            entity.ToTable("CustomerAwards");
            entity.HasKey(x => x.Id);
            entity.HasAlternateKey(x => new { x.BusinessUnitId, x.Id });
            entity.Property(x => x.AwardNumber).HasMaxLength(50).IsRequired();
            entity.Property(x => x.Status).HasMaxLength(20).IsRequired();
            entity.Property(x => x.Version).HasDefaultValue(1).IsConcurrencyToken();
            entity.Property(x => x.ConfirmedBy).HasMaxLength(255);
            entity.Property(x => x.CancelledBy).HasMaxLength(255);
            entity.Property(x => x.CancellationReason).HasMaxLength(500);
            entity.Property(x => x.CreatedOn).HasDefaultValueSql("now()");
            entity.Property(x => x.CreatedBy).HasMaxLength(255).IsRequired();
            entity.Property(x => x.ModifiedBy).HasMaxLength(255);
            entity.HasIndex(x => new { x.BusinessUnitId, x.AwardNumber }).IsUnique()
                .HasDatabaseName("UX_CustomerAwards_BU_AwardNumber");
            entity.HasIndex(x => new { x.BusinessUnitId, x.CustomerPurchaseOrderId, x.Status })
                .HasDatabaseName("IX_CustomerAwards_BU_PO_Status");
            entity.HasIndex(x => new { x.BusinessUnitId, x.QuoteId, x.Status })
                .HasDatabaseName("IX_CustomerAwards_BU_Quote_Status");
            entity.HasCheckConstraint("CK_CustomerAwards_Version", "\"Version\" >= 1");
            entity.HasCheckConstraint("CK_CustomerAwards_Status",
                "\"Status\" IN ('DRAFT','CONFIRMED','ORDERED','CANCELLED')");
            entity.HasCheckConstraint("CK_CustomerAwards_StateMetadata",
                "(\"Status\" = 'DRAFT' AND \"ConfirmedOn\" IS NULL AND \"ConfirmedBy\" IS NULL AND \"CancelledOn\" IS NULL AND \"CancelledBy\" IS NULL AND \"CancellationReason\" IS NULL) OR " +
                "(\"Status\" IN ('CONFIRMED','ORDERED') AND \"ConfirmedOn\" IS NOT NULL AND length(trim(COALESCE(\"ConfirmedBy\", ''))) > 0 AND \"CancelledOn\" IS NULL AND \"CancelledBy\" IS NULL AND \"CancellationReason\" IS NULL) OR " +
                "(\"Status\" = 'CANCELLED' AND \"CancelledOn\" IS NOT NULL AND length(trim(COALESCE(\"CancelledBy\", ''))) > 0 AND length(trim(COALESCE(\"CancellationReason\", ''))) > 0)");
            entity.HasOne(x => x.BusinessUnit).WithMany()
                .HasForeignKey(x => x.BusinessUnitId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(x => x.PurchaseOrder).WithMany(x => x.Awards)
                .HasForeignKey(x => new { x.BusinessUnitId, x.CustomerPurchaseOrderId })
                .HasPrincipalKey(x => new { x.BusinessUnitId, x.Id }).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(x => x.Quote).WithMany()
                .HasForeignKey(x => new { x.BusinessUnitId, x.QuoteId })
                .HasPrincipalKey(x => new { x.BusinessUnitId, x.Id }).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(x => x.CommercialCase).WithMany()
                .HasForeignKey(x => new { x.BusinessUnitId, x.CommercialCaseId })
                .HasPrincipalKey(x => new { x.BusinessUnitId, x.Id }).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(x => x.Customer).WithMany()
                .HasForeignKey(x => x.CustomerId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(x => x.Currency).WithMany()
                .HasForeignKey(x => new { x.BusinessUnitId, x.CurrencyId })
                .HasPrincipalKey(x => new { x.BusinessUnitId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<CustomerAwardLineAllocation>(entity =>
        {
            entity.ToTable("CustomerAwardLineAllocations");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.AwardedQuantity).HasPrecision(18, 6);
            entity.Property(x => x.UnitPriceSnapshot).HasPrecision(18, 4);
            entity.Property(x => x.DiscountSnapshot).HasPrecision(18, 2);
            entity.Property(x => x.TaxSnapshot).HasPrecision(18, 2);
            entity.Property(x => x.TotalSnapshot).HasPrecision(18, 2);
            entity.Property(x => x.Version).HasDefaultValue(1).IsConcurrencyToken();
            entity.HasIndex(x => new { x.CustomerAwardId, x.CustomerPurchaseOrderLineId, x.QuoteItemId }).IsUnique()
                .HasDatabaseName("UX_CustomerAwardAllocations_Award_POLine_QuoteItem");
            entity.HasIndex(x => new { x.BusinessUnitId, x.QuoteItemId })
                .HasDatabaseName("IX_CustomerAwardAllocations_BU_QuoteItem");
            entity.HasCheckConstraint("CK_CustomerAwardAllocations_QuantityMoney",
                "\"AwardedQuantity\" > 0 AND \"UnitPriceSnapshot\" >= 0 AND \"DiscountSnapshot\" >= 0 AND \"TaxSnapshot\" >= 0 AND \"TotalSnapshot\" >= 0");
            entity.HasCheckConstraint("CK_CustomerAwardAllocations_Version", "\"Version\" >= 1");
            entity.HasOne(x => x.Award).WithMany(x => x.LineAllocations)
                .HasForeignKey(x => new { x.BusinessUnitId, x.CustomerAwardId })
                .HasPrincipalKey(x => new { x.BusinessUnitId, x.Id }).OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(x => x.PurchaseOrderLine).WithMany(x => x.AwardAllocations)
                .HasForeignKey(x => new { x.BusinessUnitId, x.CustomerPurchaseOrderLineId })
                .HasPrincipalKey(x => new { x.BusinessUnitId, x.Id }).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(x => x.QuoteItem).WithMany()
                .HasForeignKey(x => x.QuoteItemId).OnDelete(DeleteBehavior.Restrict);
        });
    }

    private static void ConfigureGovernance(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<OrderToCashAuditEvent>(entity =>
        {
            entity.ToTable("OrderToCashAuditEvents");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.AggregateType).HasMaxLength(50).IsRequired();
            entity.Property(x => x.CommandType).HasMaxLength(80).IsRequired();
            entity.Property(x => x.PreviousState).HasMaxLength(30);
            entity.Property(x => x.NewState).HasMaxLength(30).IsRequired();
            entity.Property(x => x.Actor).HasMaxLength(255).IsRequired();
            entity.Property(x => x.Reason).HasMaxLength(500);
            entity.Property(x => x.RequestHash).HasMaxLength(64).IsRequired();
            entity.Property(x => x.IdempotencyKey).HasMaxLength(128).IsRequired();
            entity.Property(x => x.ResultJson).HasColumnType("jsonb").IsRequired();
            entity.Property(x => x.CorrelationId).HasMaxLength(128).IsRequired();
            entity.Property(x => x.OccurredOn).HasDefaultValueSql("now()").ValueGeneratedOnAdd();
            entity.HasIndex(x => new { x.BusinessUnitId, x.CommandType, x.IdempotencyKey }).IsUnique()
                .HasDatabaseName("UX_OrderToCashAudit_BU_Command_Idempotency");
            entity.HasIndex(x => new { x.BusinessUnitId, x.AggregateType, x.AggregateId, x.AggregateVersion }).IsUnique()
                .HasDatabaseName("UX_OrderToCashAudit_BU_Aggregate_Version");
            entity.HasIndex(x => new { x.BusinessUnitId, x.CorrelationId })
                .HasDatabaseName("IX_OrderToCashAudit_BU_Correlation");
            entity.HasCheckConstraint("CK_OrderToCashAudit_Identity",
                "\"AggregateId\" > 0 AND \"AggregateVersion\" >= 1 AND length(trim(\"AggregateType\")) > 0 AND length(trim(\"CommandType\")) > 0 AND length(trim(\"Actor\")) > 0 AND length(trim(\"IdempotencyKey\")) > 0 AND length(trim(\"CorrelationId\")) > 0");
            entity.HasOne(x => x.BusinessUnit).WithMany()
                .HasForeignKey(x => x.BusinessUnitId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<OrderToCashDocumentCounter>(entity =>
        {
            entity.ToTable("OrderToCashDocumentCounters");
            entity.HasKey(x => new { x.BusinessUnitId, x.DocumentType, x.CalendarYear });
            entity.Property(x => x.DocumentType).HasMaxLength(10).IsRequired();
            entity.Property(x => x.NextNumber).HasDefaultValue(1);
            entity.HasCheckConstraint("CK_OrderToCashDocumentCounters_Type",
                "\"DocumentType\" IN ('CPO','AWD','SO')");
            entity.HasCheckConstraint("CK_OrderToCashDocumentCounters_Values",
                "\"CalendarYear\" >= 2000 AND \"NextNumber\" > 0");
            entity.HasOne(x => x.BusinessUnit).WithMany()
                .HasForeignKey(x => x.BusinessUnitId).OnDelete(DeleteBehavior.Restrict);
        });
    }
}
