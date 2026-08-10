using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Procurement;
using Microsoft.EntityFrameworkCore;
using System.Linq.Expressions;

namespace ERP_RFQ_Automation.Traceability;

/// <summary>
/// Gate 5 Module 5 — material traceability schema.
///
/// <para>Three tenant-scoped tables: <c>material_lots</c>, <c>material_lot_certificates</c> and
/// <c>material_lot_consumptions</c>. Every one of them needs BOTH a row-level-security policy and
/// an explicit GRANT in the migration that creates it — the schema revoked default privileges early
/// on, so a table with a policy and no grant raises <c>42501</c> before any row predicate is even
/// evaluated. Three tables shipped exactly that defect in the last gate.</para>
///
/// <para>The model is deliberately portable: no <c>jsonb</c>, no PostgreSQL-only expressions, so
/// the SQLite lane exercises the same relationships. The <c>CHECK</c> constraints are NOT exercised
/// there (that lane runs with <c>ignore_check_constraints</c> on), which is why every invariant they
/// express is also enforced in the service.</para>
/// </summary>
public static class TraceabilityModelBuilderExtensions
{
    public static void ApplyMaterialTraceabilityModel(
        this ModelBuilder modelBuilder,
        Expression<Func<MaterialLot, bool>> lotTenantFilter,
        Expression<Func<MaterialLotCertificate, bool>> certificateTenantFilter,
        Expression<Func<MaterialLotConsumption, bool>> consumptionTenantFilter)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        // The despatch note is a first-class target of the forward link (FR-MTR-01), so the
        // consumption row needs a tenant-qualified principal key to point at. Shipment carries
        // BusinessUnitId already; this only makes the pair addressable as a foreign key.
        modelBuilder.Entity<Shipment>().HasAlternateKey(x => new { x.BusinessUnitId, x.Id });

        ConfigureLots(modelBuilder, lotTenantFilter);
        ConfigureCertificates(modelBuilder, certificateTenantFilter);
        ConfigureConsumptions(modelBuilder, consumptionTenantFilter);
    }

    private static void ConfigureLots(
        ModelBuilder modelBuilder, Expression<Func<MaterialLot, bool>> tenantFilter)
    {
        modelBuilder.Entity<MaterialLot>(entity =>
        {
            entity.ToTable("material_lots");
            entity.HasKey(x => x.Id);
            entity.HasAlternateKey(x => new { x.BusinessUnitId, x.Id });
            entity.Ignore(x => x.QuantityRemaining);
            entity.Ignore(x => x.OriginDiffersFromOrder);

            entity.Property(x => x.LotNumber).HasMaxLength(80).IsRequired();
            entity.Property(x => x.TrackingMode).HasMaxLength(16).IsRequired();
            entity.Property(x => x.Status).HasMaxLength(16).IsRequired()
                .HasDefaultValue(MaterialLotStatuses.Available);
            entity.Property(x => x.NexoraSerial).HasMaxLength(100);
            entity.Property(x => x.CountryOfOrigin).HasMaxLength(100);
            entity.Property(x => x.OrderedCountryOfOrigin).HasMaxLength(100);
            entity.Property(x => x.ManufacturerName).HasMaxLength(255);
            entity.Property(x => x.ManufacturerPartNumber).HasMaxLength(120);
            entity.Property(x => x.SupplierBatchReference).HasMaxLength(120);
            entity.Property(x => x.QuarantineReasonCode).HasMaxLength(32);
            entity.Property(x => x.QuarantineReason).HasMaxLength(1000);
            entity.Property(x => x.QuarantinedBy).HasMaxLength(255);
            entity.Property(x => x.ReleasedBy).HasMaxLength(255);
            entity.Property(x => x.ReleaseReason).HasMaxLength(1000);
            entity.Property(x => x.CreatedBy).HasMaxLength(255).IsRequired();
            entity.Property(x => x.ModifiedBy).HasMaxLength(255);
            entity.Property(x => x.QuantityReceived).HasPrecision(18, 4);
            entity.Property(x => x.QuantityConsumed).HasPrecision(18, 4).HasDefaultValue(0m);
            entity.Property(x => x.Version).HasDefaultValue(1).IsConcurrencyToken();

            entity.HasCheckConstraint("CK_material_lots_TrackingMode",
                "\"TrackingMode\" IN ('LOT','SERIAL','UNTRACKED')");
            entity.HasCheckConstraint("CK_material_lots_Status",
                "\"Status\" IN ('AVAILABLE','QUARANTINED')");
            entity.HasCheckConstraint("CK_material_lots_Quantities",
                "\"QuantityReceived\" > 0 AND \"QuantityConsumed\" >= 0 "
                + "AND \"QuantityConsumed\" <= \"QuantityReceived\"");
            // A serial identifies one physical unit. A "serial" covering 12 units is a lot number
            // that has been mislabelled, and it would break the one-recall-one-unit property the
            // whole serial model rests on.
            entity.HasCheckConstraint("CK_material_lots_SerialQuantity",
                "\"TrackingMode\" <> 'SERIAL' OR \"QuantityReceived\" = 1");
            // A hold with no reason and no name is not a control. This makes the three columns one
            // fact at the database level rather than three the service happens to write together.
            entity.HasCheckConstraint("CK_material_lots_Quarantine",
                "\"Status\" <> 'QUARANTINED' OR (\"QuarantinedOn\" IS NOT NULL "
                + "AND \"QuarantinedBy\" IS NOT NULL AND \"QuarantineReasonCode\" IS NOT NULL "
                + "AND \"QuarantineReason\" IS NOT NULL)");
            entity.HasCheckConstraint("CK_material_lots_Release",
                "\"ReleasedOn\" IS NULL OR (\"ReleasedBy\" IS NOT NULL AND \"ReleaseReason\" IS NOT NULL)");
            entity.HasCheckConstraint("CK_material_lots_ManufactureBeforeExpiry",
                "\"ManufactureDate\" IS NULL OR \"ExpiryDate\" IS NULL "
                + "OR \"ExpiryDate\" >= \"ManufactureDate\"");

            // A serial number must identify exactly one unit per product per tenant. Batch numbers
            // are deliberately NOT globally unique: the same supplier batch legitimately arrives on
            // two shipments, and forcing the second receipt to reuse the first row would make one
            // lot span two purchase orders and destroy the where-from answer.
            entity.HasIndex(x => new { x.BusinessUnitId, x.ProductId, x.LotNumber })
                .IsUnique()
                .HasFilter("\"TrackingMode\" = 'SERIAL'")
                .HasDatabaseName("UX_material_lots_BU_Product_Serial");
            // One receipt cannot declare the same lot twice on the same purchase-order line.
            entity.HasIndex(x => new
                {
                    x.BusinessUnitId, x.GoodsReceiptId, x.SupplierPurchaseOrderLineId, x.LotNumber
                })
                .IsUnique()
                .HasDatabaseName("UX_material_lots_BU_Receipt_Line_LotNumber");
            entity.HasIndex(x => new { x.BusinessUnitId, x.ProductId, x.LotNumber })
                .HasDatabaseName("IX_material_lots_BU_Product_LotNumber");
            // The recall sweep: "everything from supplier X that is still available".
            entity.HasIndex(x => new { x.BusinessUnitId, x.SupplierId, x.Status })
                .HasDatabaseName("IX_material_lots_BU_Supplier_Status");
            entity.HasIndex(x => new { x.BusinessUnitId, x.SupplierPurchaseOrderId })
                .HasDatabaseName("IX_material_lots_BU_SupplierPurchaseOrder");
            entity.HasIndex(x => new { x.BusinessUnitId, x.InventoryId, x.Status })
                .HasDatabaseName("IX_material_lots_BU_Inventory_Status");
            entity.HasIndex(x => new { x.BusinessUnitId, x.CommercialCaseId })
                .HasDatabaseName("IX_material_lots_BU_CommercialCase");

            entity.HasOne<SupplierPurchaseOrder>().WithMany()
                .HasForeignKey(x => new { x.BusinessUnitId, x.SupplierPurchaseOrderId })
                .HasPrincipalKey(x => new { x.BusinessUnitId, x.Id }).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<SupplierPurchaseOrderLine>().WithMany()
                .HasForeignKey(x => new { x.BusinessUnitId, x.SupplierPurchaseOrderLineId })
                .HasPrincipalKey(x => new { x.BusinessUnitId, x.Id }).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<GoodsReceipt>().WithMany()
                .HasForeignKey(x => new { x.BusinessUnitId, x.GoodsReceiptId })
                .HasPrincipalKey(x => new { x.BusinessUnitId, x.Id }).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<Supplier>().WithMany()
                .HasForeignKey(x => new { x.SupplierId, x.BusinessUnitId })
                .HasPrincipalKey(x => new { x.Id, x.Buid }).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<Product>().WithMany()
                .HasForeignKey(x => new { x.BusinessUnitId, x.ProductId })
                .HasPrincipalKey(x => new { BusinessUnitId = x.Buid, ProductId = x.Id })
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<Models.Inventory>().WithMany()
                .HasForeignKey(x => new { x.BusinessUnitId, x.InventoryId })
                .HasPrincipalKey(x => new { BusinessUnitId = x.Buid, InventoryId = x.Id })
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<Warehouse>().WithMany()
                .HasForeignKey(x => new { x.BusinessUnitId, x.WarehouseId })
                .HasPrincipalKey(x => new { x.BusinessUnitId, x.Id }).OnDelete(DeleteBehavior.Restrict);
            // Nullable because a STOCK replenishment lot has no customer case. The foreign key is
            // what makes a WRONG case id impossible rather than merely visible.
            entity.HasOne<CommercialCase>().WithMany()
                .HasForeignKey(x => new { x.BusinessUnitId, x.CommercialCaseId })
                .HasPrincipalKey(x => new { x.BusinessUnitId, x.Id }).OnDelete(DeleteBehavior.Restrict);

            entity.HasQueryFilter(tenantFilter);
        });
    }

    private static void ConfigureCertificates(
        ModelBuilder modelBuilder, Expression<Func<MaterialLotCertificate, bool>> tenantFilter)
    {
        modelBuilder.Entity<MaterialLotCertificate>(entity =>
        {
            entity.ToTable("material_lot_certificates");
            entity.HasKey(x => x.Id);
            entity.HasAlternateKey(x => new { x.BusinessUnitId, x.Id });

            entity.Property(x => x.CertificateType).HasMaxLength(40).IsRequired();
            entity.Property(x => x.CertificateNumber).HasMaxLength(120);
            entity.Property(x => x.IssuedBy).HasMaxLength(255);
            entity.Property(x => x.ContentSha256).HasMaxLength(64).IsRequired();
            entity.Property(x => x.FileName).HasMaxLength(255).IsRequired();
            entity.Property(x => x.Notes).HasMaxLength(1000);
            entity.Property(x => x.UploadedBy).HasMaxLength(255).IsRequired();

            entity.HasCheckConstraint("CK_material_lot_certificates_Type",
                "\"CertificateType\" IN ('MANUFACTURER','CERTIFICATE_OF_ORIGIN',"
                + "'CERTIFICATE_OF_CONFORMITY','SASO','SABER','MILL_TEST_REPORT','OTHER')");
            entity.HasCheckConstraint("CK_material_lot_certificates_Dates",
                "\"IssuedOn\" IS NULL OR \"ExpiresOn\" IS NULL OR \"ExpiresOn\" >= \"IssuedOn\"");

            // One certificate row per stored document: re-filing the same bytes under a second row
            // would double-count it in the expiry verdict and make "which document is this" ambiguous.
            entity.HasIndex(x => new { x.BusinessUnitId, x.AttachmentId }).IsUnique()
                .HasDatabaseName("UX_material_lot_certificates_BU_Attachment");
            // The same numbered certificate cannot be filed twice against one lot. Unnumbered
            // documents are exempt — a mill test report often carries no number, and refusing the
            // second one would block a legitimate upload.
            entity.HasIndex(x => new { x.BusinessUnitId, x.MaterialLotId, x.CertificateType, x.CertificateNumber })
                .IsUnique()
                .HasFilter("\"CertificateNumber\" IS NOT NULL")
                .HasDatabaseName("UX_material_lot_certificates_BU_Lot_Type_Number");
            entity.HasIndex(x => new { x.BusinessUnitId, x.MaterialLotId })
                .HasDatabaseName("IX_material_lot_certificates_BU_Lot");
            // Drives the expiry watchlist without scanning every certificate ever filed.
            entity.HasIndex(x => new { x.BusinessUnitId, x.ExpiresOn })
                .HasDatabaseName("IX_material_lot_certificates_BU_ExpiresOn");

            entity.HasOne<MaterialLot>().WithMany(x => x.Certificates)
                .HasForeignKey(x => new { x.BusinessUnitId, x.MaterialLotId })
                .HasPrincipalKey(x => new { x.BusinessUnitId, x.Id }).OnDelete(DeleteBehavior.Cascade);
            entity.HasOne<Attachment>().WithMany()
                .HasForeignKey(x => x.AttachmentId).OnDelete(DeleteBehavior.Restrict);

            entity.HasQueryFilter(tenantFilter);
        });
    }

    private static void ConfigureConsumptions(
        ModelBuilder modelBuilder, Expression<Func<MaterialLotConsumption, bool>> tenantFilter)
    {
        modelBuilder.Entity<MaterialLotConsumption>(entity =>
        {
            entity.ToTable("material_lot_consumptions");
            entity.HasKey(x => x.Id);
            entity.HasAlternateKey(x => new { x.BusinessUnitId, x.Id });

            entity.Property(x => x.Quantity).HasPrecision(18, 4);
            entity.Property(x => x.ComplianceStateAtDeclaration).HasMaxLength(24).IsRequired();
            entity.Property(x => x.ComplianceOverrideReason).HasMaxLength(1000);
            entity.Property(x => x.ComplianceOverrideBy).HasMaxLength(255);
            entity.Property(x => x.NexoraSerial).HasMaxLength(100);
            entity.Property(x => x.IdempotencyKey).HasMaxLength(160).IsRequired();
            entity.Property(x => x.DeclaredBy).HasMaxLength(255).IsRequired();
            entity.Property(x => x.Version).HasDefaultValue(1).IsConcurrencyToken();

            entity.HasCheckConstraint("CK_material_lot_consumptions_Quantity", "\"Quantity\" > 0");
            entity.HasCheckConstraint("CK_material_lot_consumptions_Compliance",
                "\"ComplianceStateAtDeclaration\" IN ('COMPLIANT','CERTIFICATE_EXPIRED','NO_CERTIFICATE')");
            // Shipping against an expired certificate is permitted, but only as a named decision.
            // Making the override columns mandatory at the database level is what stops the
            // exception being written by anything that forgets to ask.
            entity.HasCheckConstraint("CK_material_lot_consumptions_Override",
                "\"ComplianceStateAtDeclaration\" <> 'CERTIFICATE_EXPIRED' "
                + "OR (\"ComplianceOverrideReason\" IS NOT NULL AND \"ComplianceOverrideBy\" IS NOT NULL)");

            entity.HasIndex(x => new { x.BusinessUnitId, x.IdempotencyKey }).IsUnique()
                .HasDatabaseName("UX_material_lot_consumptions_BU_IdempotencyKey");
            // The where-used read key. Both directions of FR-MTR-03 are one indexed predicate each.
            entity.HasIndex(x => new { x.BusinessUnitId, x.OrderId })
                .HasDatabaseName("IX_material_lot_consumptions_BU_Order");
            entity.HasIndex(x => new { x.BusinessUnitId, x.OrderItemId })
                .HasDatabaseName("IX_material_lot_consumptions_BU_OrderItem");
            entity.HasIndex(x => new { x.BusinessUnitId, x.MaterialLotId })
                .HasDatabaseName("IX_material_lot_consumptions_BU_Lot");
            entity.HasIndex(x => new { x.BusinessUnitId, x.ShipmentId })
                .HasDatabaseName("IX_material_lot_consumptions_BU_Shipment");
            entity.HasIndex(x => new { x.BusinessUnitId, x.CommercialCaseId })
                .HasDatabaseName("IX_material_lot_consumptions_BU_CommercialCase");

            entity.HasOne<MaterialLot>().WithMany()
                .HasForeignKey(x => new { x.BusinessUnitId, x.MaterialLotId })
                .HasPrincipalKey(x => new { x.BusinessUnitId, x.Id }).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<Order>().WithMany()
                .HasForeignKey(x => new { x.BusinessUnitId, x.OrderId })
                .HasPrincipalKey(x => new { x.BusinessUnitId, x.Id }).OnDelete(DeleteBehavior.Restrict);
            // OrderItem carries no tenant column — isolation is parent-derived — so the pair
            // (line, order) is the only key that cannot name another order's line.
            entity.HasOne<OrderItem>().WithMany()
                .HasForeignKey(x => new { x.OrderItemId, x.OrderId })
                .HasPrincipalKey(x => new { x.Id, x.OrderId }).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<Shipment>().WithMany()
                .HasForeignKey(x => new { x.BusinessUnitId, x.ShipmentId })
                .HasPrincipalKey(x => new { x.BusinessUnitId, x.Id }).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<CommercialCase>().WithMany()
                .HasForeignKey(x => new { x.BusinessUnitId, x.CommercialCaseId })
                .HasPrincipalKey(x => new { x.BusinessUnitId, x.Id }).OnDelete(DeleteBehavior.Restrict);

            entity.HasQueryFilter(tenantFilter);
        });
    }
}
