using ERP_RFQ_Automation.InboundLogistics;
using ERP_RFQ_Automation.Procurement;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.Models;

/// <summary>
/// Gate 5 / Module 6 — inbound supplier shipments, the Saudi entry-point master and the per-business
/// -unit lead times the Material Available Date is derived from (FR-MAS-01..05, decision R3).
///
/// <para>Four new tenant-owned tables. Each carries an EF global query filter here AND requires a
/// PostgreSQL row-level-security policy plus an explicit GRANT in the migration — the migration is
/// the integration owner's to write, and the exact statements are stated in the gate report. A
/// policy without a grant is not a narrower boundary, it is a table nobody can read: the schema is
/// deny-by-default since the default privileges were revoked, so PostgreSQL raises 42501 before any
/// row predicate is evaluated.</para>
/// </summary>
public partial class ErpRfqAutomationContext
{
    public DbSet<PortOfEntry> PortsOfEntry => Set<PortOfEntry>();
    public DbSet<SupplierShipment> SupplierShipments => Set<SupplierShipment>();
    public DbSet<SupplierShipmentLine> SupplierShipmentLines => Set<SupplierShipmentLine>();
    public DbSet<InboundLogisticsPolicy> InboundLogisticsPolicies => Set<InboundLogisticsPolicy>();

    private void ConfigureInboundLogisticsModel(ModelBuilder modelBuilder)
    {
        var milestones = string.Join(",", InboundShipmentMilestones.All.Select(x => $"'{x}'"));
        var trackingSources = string.Join(",", InboundShipmentTrackingSources.All.Select(x => $"'{x}'"));
        var portKinds = string.Join(",", PortOfEntryKinds.All.Select(x => $"'{x}'"));

        modelBuilder.Entity<PortOfEntry>(entity =>
        {
            entity.ToTable("ports_of_entry");
            entity.HasKey(x => x.Id);
            entity.HasAlternateKey(x => new { x.BusinessUnitId, x.Id });
            entity.Property(x => x.Code).HasMaxLength(40).IsRequired();
            entity.Property(x => x.Name).HasMaxLength(200).IsRequired();
            entity.Property(x => x.Kind).HasMaxLength(24).IsRequired();
            entity.Property(x => x.CountryCode).HasMaxLength(2).IsRequired();
            entity.Property(x => x.City).HasMaxLength(120);
            entity.Property(x => x.CreatedBy).HasMaxLength(255).IsRequired();
            entity.Property(x => x.ModifiedBy).HasMaxLength(255);
            entity.Property(x => x.Version).HasDefaultValue(1).IsConcurrencyToken();
            // FR-MAS-01 / E21. The whole reason this is a table and not a string column: a report can
            // only group by a controlled value. The constraint is what keeps it controlled.
            entity.HasCheckConstraint("CK_ports_of_entry_Kind", $"\"Kind\" IN ({portKinds})");
            entity.HasIndex(x => new { x.BusinessUnitId, x.Code }).IsUnique();
            entity.HasIndex(x => new { x.BusinessUnitId, x.IsActive, x.Kind });
            entity.HasOne<BusinessUnit>().WithMany().HasForeignKey(x => x.BusinessUnitId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasQueryFilter(x => CurrentTenantId == null || x.BusinessUnitId == CurrentTenantId);
        });

        modelBuilder.Entity<SupplierShipment>(entity =>
        {
            entity.ToTable("supplier_shipments");
            entity.HasKey(x => x.Id);
            entity.HasAlternateKey(x => new { x.BusinessUnitId, x.Id });
            entity.Property(x => x.ShipmentNumber).HasMaxLength(100).IsRequired();
            entity.Property(x => x.Milestone).HasMaxLength(32).IsRequired();
            entity.Property(x => x.TrackingSource).HasMaxLength(16).IsRequired();
            entity.Property(x => x.CarrierName).HasMaxLength(255);
            entity.Property(x => x.TrackingReference).HasMaxLength(160);
            entity.Property(x => x.CancellationReason).HasMaxLength(1000);
            entity.Property(x => x.EtaUpdatedBy).HasMaxLength(255);
            entity.Property(x => x.MaterialAvailableBasisKind).HasMaxLength(32);
            entity.Property(x => x.MaterialAvailableUnavailableReason).HasMaxLength(500);
            entity.Property(x => x.IdempotencyKey).HasMaxLength(160).IsRequired();
            entity.Property(x => x.RequestHash).HasMaxLength(64).IsRequired();
            entity.Property(x => x.CreatedBy).HasMaxLength(255).IsRequired();
            entity.Property(x => x.ModifiedBy).HasMaxLength(255);
            entity.Property(x => x.Version).HasDefaultValue(1).IsConcurrencyToken();

            // The ordered lifecycle, enforced by the database and not only by the service. E20
            // recorded what the alternative looks like: the outbound Shipment's status is an
            // unseeded free-text picklist, every tenant invented its own words, and no cross-tenant
            // milestone KPI or delay rule was possible.
            entity.HasCheckConstraint("CK_supplier_shipments_Milestone", $"\"Milestone\" IN ({milestones})");
            entity.HasCheckConstraint("CK_supplier_shipments_TrackingSource",
                $"\"TrackingSource\" IN ({trackingSources})");
            // FR-MAS-01. An arrival with no named location is the free-text failure by another route.
            entity.HasCheckConstraint("CK_supplier_shipments_ArrivalLocation",
                "\"ArrivedSaudiPortOn\" IS NULL OR \"PortOfEntryId\" IS NOT NULL");
            // A cancellation is three facts written together, or it is not a cancellation.
            entity.HasCheckConstraint("CK_supplier_shipments_Cancellation",
                "\"Milestone\" <> 'CANCELLED' OR (\"CancelledOn\" IS NOT NULL AND \"CancellationReason\" IS NOT NULL)");
            // Lead times applied to a derived date can be zero (same-day clearance is a real
            // assertion) but never negative.
            entity.HasCheckConstraint("CK_supplier_shipments_AppliedLeadDays",
                "(\"AppliedCustomsClearanceDays\" IS NULL OR \"AppliedCustomsClearanceDays\" >= 0) "
                + "AND (\"AppliedPutawayDays\" IS NULL OR \"AppliedPutawayDays\" >= 0)");

            entity.HasIndex(x => new { x.BusinessUnitId, x.ShipmentNumber }).IsUnique();
            entity.HasIndex(x => new { x.BusinessUnitId, x.IdempotencyKey }).IsUnique();
            entity.HasIndex(x => new { x.BusinessUnitId, x.SupplierPurchaseOrderId });
            // The two SLA sweeps and the availability read, in index form.
            entity.HasIndex(x => new { x.BusinessUnitId, x.Milestone, x.EtaDate });
            entity.HasIndex(x => new { x.BusinessUnitId, x.MaterialAvailableDate });

            entity.HasOne<SupplierPurchaseOrder>().WithMany()
                .HasForeignKey(x => new { x.BusinessUnitId, x.SupplierPurchaseOrderId })
                .HasPrincipalKey(x => new { x.BusinessUnitId, x.Id })
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<PortOfEntry>().WithMany()
                .HasForeignKey(x => new { x.BusinessUnitId, x.PortOfEntryId })
                .HasPrincipalKey(x => new { x.BusinessUnitId, x.Id })
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasQueryFilter(x => CurrentTenantId == null || x.BusinessUnitId == CurrentTenantId);
        });

        modelBuilder.Entity<SupplierShipmentLine>(entity =>
        {
            entity.ToTable("supplier_shipment_lines");
            entity.HasKey(x => x.Id);
            entity.HasAlternateKey(x => new { x.BusinessUnitId, x.Id });
            entity.Property(x => x.ShippedQuantity).HasPrecision(18, 4);
            // The Gate 4/Gate 5 seam. Zero is the correct value for a line nothing has been
            // receipted against, and it is also the value every row carries the moment the table is
            // created — so the code default and the "backfill" agree, which is failure #10's test.
            entity.Property(x => x.ReceivedQuantity).HasPrecision(18, 4).HasDefaultValue(0m);
            entity.Property(x => x.Version).HasDefaultValue(1).IsConcurrencyToken();
            entity.Ignore(x => x.OutstandingReceiptQuantity);
            entity.HasCheckConstraint("CK_supplier_shipment_lines_Quantity", "\"ShippedQuantity\" > 0");
            // A goods receipt can never book in more than the shipment carried. Enforced in the
            // settlement, and again here, because a validator alone is a rule that holds until
            // somebody writes a second code path.
            entity.HasCheckConstraint("CK_supplier_shipment_lines_ReceivedQuantity",
                "\"ReceivedQuantity\" >= 0 AND \"ReceivedQuantity\" <= \"ShippedQuantity\"");
            // One purchase order line appears at most once on a shipment, so the manifest cannot
            // double-count and the reconciliation total cannot be inflated by a duplicate row.
            entity.HasIndex(x => new { x.BusinessUnitId, x.SupplierShipmentId, x.SupplierPurchaseOrderLineId })
                .IsUnique();
            entity.HasIndex(x => new { x.BusinessUnitId, x.SupplierPurchaseOrderLineId });
            entity.HasOne<SupplierShipment>().WithMany(x => x.Lines)
                .HasForeignKey(x => new { x.BusinessUnitId, x.SupplierShipmentId })
                .HasPrincipalKey(x => new { x.BusinessUnitId, x.Id })
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne<SupplierPurchaseOrderLine>().WithMany()
                .HasForeignKey(x => new { x.BusinessUnitId, x.SupplierPurchaseOrderLineId })
                .HasPrincipalKey(x => new { x.BusinessUnitId, x.Id })
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<Product>().WithMany()
                .HasForeignKey(x => new { x.BusinessUnitId, x.ProductId })
                .HasPrincipalKey(x => new { BusinessUnitId = x.Buid, ProductId = x.Id })
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasQueryFilter(x => CurrentTenantId == null || x.BusinessUnitId == CurrentTenantId);
        });

        modelBuilder.Entity<InboundLogisticsPolicy>(entity =>
        {
            entity.ToTable("inbound_logistics_policies");
            entity.HasKey(x => x.Id);
            entity.HasAlternateKey(x => new { x.BusinessUnitId, x.Id });
            entity.Property(x => x.ModifiedBy).HasMaxLength(255);
            entity.Property(x => x.Version).HasDefaultValue(1).IsConcurrencyToken();
            // NULL is the "not configured" state and must stay reachable — see the entity. The bound
            // catches a date typed into a day box, which is the realistic mis-entry.
            entity.HasCheckConstraint("CK_inbound_logistics_policies_LeadDays",
                "(\"CustomsClearanceLeadDays\" IS NULL OR (\"CustomsClearanceLeadDays\" >= 0 AND \"CustomsClearanceLeadDays\" <= 365)) "
                + "AND (\"PutawayLeadDays\" IS NULL OR (\"PutawayLeadDays\" >= 0 AND \"PutawayLeadDays\" <= 365))");
            entity.HasIndex(x => x.BusinessUnitId).IsUnique();
            entity.HasOne<BusinessUnit>().WithMany().HasForeignKey(x => x.BusinessUnitId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasQueryFilter(x => CurrentTenantId == null || x.BusinessUnitId == CurrentTenantId);
        });

        // FR-MAS-05. The reconciliation invariant, on the table it constrains. Configured from here
        // rather than from ConfigurePurchaseOrders so this gate's schema surface stays in one file.
        modelBuilder.Entity<SupplierPurchaseOrderLine>(entity =>
        {
            entity.Property(x => x.ShippedQuantity).HasPrecision(18, 4).HasDefaultValue(0m);
            entity.HasCheckConstraint("CK_supplier_purchase_order_lines_ShippedQuantity",
                "\"ShippedQuantity\" >= 0 AND \"ShippedQuantity\" <= \"OrderedQuantity\"");
        });
    }
}
