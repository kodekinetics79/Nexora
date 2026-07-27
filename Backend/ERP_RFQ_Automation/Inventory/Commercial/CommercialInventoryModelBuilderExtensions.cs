using ERP_RFQ_Automation.Models;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.Inventory.Commercial;

public static class CommercialInventoryModelBuilderExtensions
{
    public static ModelBuilder ApplyCommercialInventoryModel(this ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Product>(entity =>
        {
            entity.HasAlternateKey(x => new { x.Buid, x.Id });
            entity.HasOne(x => x.Bu).WithMany(x => x.Products).HasForeignKey(x => x.Buid)
                .OnDelete(DeleteBehavior.Restrict).HasConstraintName("FK__Products__BUID");
        });
        modelBuilder.Entity<Models.Inventory>(entity =>
        {
            entity.HasAlternateKey(x => new { x.Buid, x.Id });
            entity.Property(x => x.AllocatedQuantity).HasPrecision(18, 4);
            entity.Property(x => x.QuarantineQuantity).HasPrecision(18, 4);
            entity.Property(x => x.DamagedQuantity).HasPrecision(18, 4);
            entity.Property(x => x.ExpiredQuantity).HasPrecision(18, 4);
            entity.Property(x => x.SafetyStockQuantity).HasPrecision(18, 4);
            entity.HasIndex(x => new { x.Buid, x.ProductId, x.WarehouseId }).IsUnique()
                .HasFilter("\"ProductId\" IS NOT NULL AND \"WarehouseId\" IS NOT NULL")
                .HasDatabaseName("UX_Inventory_BU_Product_Warehouse");
            entity.HasOne<Product>().WithMany().HasForeignKey(x => new { BusinessUnitId = x.Buid, ProductId = x.ProductId })
                .HasPrincipalKey(x => new { BusinessUnitId = x.Buid, ProductId = x.Id }).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<ProductAlias>(entity =>
        {
            entity.ToTable("product_aliases"); entity.HasKey(x => x.Id);
            entity.Property(x => x.Kind).HasConversion<string>().HasMaxLength(40);
            entity.Property(x => x.Value).HasMaxLength(200).IsRequired();
            entity.Property(x => x.NormalizedValue).HasMaxLength(200).IsRequired();
            entity.Property(x => x.CreatedBy).HasMaxLength(160).IsRequired();
            entity.HasIndex(x => new { x.BusinessUnitId, x.Kind, x.NormalizedValue, x.AccountId }).IsUnique();
            entity.HasIndex(x => new { x.BusinessUnitId, x.ProductId });
            entity.HasOne<Product>().WithMany().HasForeignKey(x => new { x.BusinessUnitId, x.ProductId })
                .HasPrincipalKey(x => new { BusinessUnitId = x.Buid, ProductId = x.Id }).OnDelete(DeleteBehavior.Restrict);
        });
        modelBuilder.Entity<ProductSupersession>(entity =>
        {
            entity.ToTable("product_supersessions"); entity.HasKey(x => x.Id);
            entity.Property(x => x.Kind).HasConversion<string>().HasMaxLength(40);
            entity.Property(x => x.EvidenceReference).HasMaxLength(500);
            entity.Property(x => x.CreatedBy).HasMaxLength(160).IsRequired();
            entity.HasIndex(x => new { x.BusinessUnitId, x.SupersededProductId, x.ReplacementProductId, x.EffectiveOn }).IsUnique();
            entity.HasOne<Product>().WithMany().HasForeignKey(x => new { x.BusinessUnitId, ProductId = x.SupersededProductId })
                .HasPrincipalKey(x => new { BusinessUnitId = x.Buid, ProductId = x.Id }).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<Product>().WithMany().HasForeignKey(x => new { x.BusinessUnitId, ProductId = x.ReplacementProductId })
                .HasPrincipalKey(x => new { BusinessUnitId = x.Buid, ProductId = x.Id }).OnDelete(DeleteBehavior.Restrict);
        });
        modelBuilder.Entity<InventoryMovement>(entity =>
        {
            entity.ToTable("inventory_movements"); entity.HasKey(x => x.Id);
            entity.Property(x => x.Type).HasConversion<string>().HasMaxLength(40);
            entity.Property(x => x.Quantity).HasPrecision(18, 4);
            entity.Property(x => x.IdempotencyKey).HasMaxLength(160).IsRequired();
            entity.Property(x => x.SourceType).HasMaxLength(40).IsRequired();
            entity.Property(x => x.SourceId).HasMaxLength(160).IsRequired();
            entity.Property(x => x.Reason).HasMaxLength(500);
            entity.Property(x => x.CreatedBy).HasMaxLength(160).IsRequired();
            entity.HasIndex(x => new { x.BusinessUnitId, x.IdempotencyKey }).IsUnique();
            entity.HasIndex(x => new { x.BusinessUnitId, x.ProductId, x.OccurredOn });
            entity.HasOne<Product>().WithMany().HasForeignKey(x => new { x.BusinessUnitId, x.ProductId })
                .HasPrincipalKey(x => new { BusinessUnitId = x.Buid, ProductId = x.Id }).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<Models.Inventory>().WithMany().HasForeignKey(x => new { x.BusinessUnitId, x.InventoryId })
                .HasPrincipalKey(x => new { BusinessUnitId = x.Buid, InventoryId = x.Id }).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<Warehouse>().WithMany().HasForeignKey(x => new { BusinessUnitId = x.BusinessUnitId, Id = x.WarehouseId })
                .HasPrincipalKey(x => new { x.BusinessUnitId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        });
        modelBuilder.Entity<IncomingInventory>(entity =>
        {
            entity.ToTable("incoming_inventory"); entity.HasKey(x => x.Id);
            entity.Property(x => x.OrderedQuantity).HasPrecision(18, 4);
            entity.Property(x => x.ReceivedQuantity).HasPrecision(18, 4);
            entity.Property(x => x.AllocatedQuantity).HasPrecision(18, 4);
            entity.Property(x => x.Status).HasConversion<string>().HasMaxLength(32);
            entity.Property(x => x.SourceType).HasMaxLength(40).IsRequired();
            entity.Property(x => x.SourceId).HasMaxLength(160).IsRequired();
            entity.HasIndex(x => new { x.BusinessUnitId, x.SourceType, x.SourceId, x.ProductId, x.WarehouseId }).IsUnique();
            entity.HasIndex(x => new { x.BusinessUnitId, x.ProductId, x.ExpectedOn });
            entity.HasOne<Product>().WithMany().HasForeignKey(x => new { x.BusinessUnitId, x.ProductId })
                .HasPrincipalKey(x => new { BusinessUnitId = x.Buid, ProductId = x.Id }).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<Warehouse>().WithMany().HasForeignKey(x => new { x.BusinessUnitId, Id = x.WarehouseId })
                .HasPrincipalKey(x => new { x.BusinessUnitId, x.Id }).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<Models.Inventory>().WithMany().HasForeignKey(x => new { x.BusinessUnitId, x.InventoryId })
                .HasPrincipalKey(x => new { BusinessUnitId = x.Buid, InventoryId = x.Id }).OnDelete(DeleteBehavior.Restrict);
        });
        modelBuilder.Entity<LeadLineCommercialResolution>(entity =>
        {
            entity.ToTable("lead_line_commercial_resolutions"); entity.HasKey(x => x.Id);
            entity.Property(x => x.Classification).HasConversion<string>().HasMaxLength(40);
            entity.Property(x => x.RequestedPartNumber).HasMaxLength(200).IsRequired();
            entity.Property(x => x.RequestedQuantity).HasPrecision(18, 4);
            entity.Property(x => x.AvailableToPromise).HasPrecision(18, 4);
            entity.Property(x => x.IncomingAvailable).HasPrecision(18, 4);
            entity.Property(x => x.FulfilmentJson).HasColumnType("jsonb");
            entity.Property(x => x.RelatedResourcesJson).HasColumnType("jsonb");
            entity.Property(x => x.ProductResolutionJson).HasColumnType("jsonb");
            entity.Property(x => x.ResolutionMethod).HasMaxLength(80).IsRequired();
            entity.Property(x => x.EvidenceReference).HasMaxLength(500);
            entity.Ignore(x => x.Fulfilment); entity.Ignore(x => x.RelatedResources);
            entity.HasIndex(x => new { x.BusinessUnitId, x.LeadRevisionId, x.LeadLineId, x.ResolutionBatchId }).IsUnique();
            entity.HasIndex(x => new { x.BusinessUnitId, x.LeadRevisionId, x.LeadLineId, x.ResolvedOn });
            entity.HasIndex(x => new { x.BusinessUnitId, x.RfqId });
            entity.HasOne<ERP_RFQ_Automation.LeadIdentity.LeadRevision>().WithMany()
                .HasForeignKey(x => new { x.BusinessUnitId, x.LeadRevisionId })
                .HasPrincipalKey(x => new { x.BusinessUnitId, x.Id }).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<ERP_RFQ_Automation.LeadIdentity.LeadItemRevision>().WithMany()
                .HasForeignKey(x => new { x.BusinessUnitId, x.LeadLineId })
                .HasPrincipalKey(x => new { x.BusinessUnitId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        });
        return modelBuilder;
    }
}
