using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.Inventory;

/// <summary>
/// Relational mapping for the stock-reservation ledger. Uses only portable constructs so the
/// SQLite integration suite exercises the same model that runs on PostgreSQL.
/// </summary>
public static class InventoryReservationModelBuilderExtensions
{
    public static ModelBuilder ApplyInventoryReservationModel(this ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        // The Inventory POCO existed but was never mapped to the database (no DbSet, no config),
        // so there was no persisted stock at all. Map it minimally here — scalar columns only, with
        // every navigation ignored so mapping does not cascade into other still-unmapped aggregates
        // (InventoryAttachment/InventoryCategory/Image have no key configuration). A later inventory
        // slice can expand this; for now it gives the reservation engine a real on-hand quantity.
        modelBuilder.Entity<Models.Inventory>(entity =>
        {
            entity.ToTable("Inventory");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.QtyOnHand).HasPrecision(18, 4);
            entity.Property(x => x.ReorderPoint).HasPrecision(18, 4);
            entity.Property(x => x.UnitCost).HasPrecision(18, 4);
            entity.Property(x => x.SellingPrice).HasPrecision(18, 4);
            entity.Property(x => x.Weight).HasPrecision(18, 4);
            entity.Ignore(x => x.Bu);
            entity.Ignore(x => x.Category);
            entity.Ignore(x => x.InventoryAttachments);
            entity.Ignore(x => x.ItemStatus);
            entity.Ignore(x => x.PreferredSupplier);
            entity.Ignore(x => x.PrimaryImage);
            entity.Ignore(x => x.Tax);
            entity.Ignore(x => x.Warehouse);
            entity.HasIndex(x => new { x.Buid, x.PartNo }).HasDatabaseName("IX_Inventory_BU_PartNo");
        });

        modelBuilder.Entity<StockReservation>(entity =>
        {
            entity.ToTable("stock_reservations");
            entity.HasKey(x => x.Id);

            entity.Property(x => x.Quantity).HasPrecision(18, 4);
            entity.Property(x => x.Status).HasConversion<string>().HasMaxLength(16);
            entity.Property(x => x.IdempotencyKey).HasMaxLength(200).IsRequired();
            entity.Property(x => x.CreatedBy).HasMaxLength(256).IsRequired();
            entity.Property(x => x.Version).IsConcurrencyToken();

            // Idempotency: a given reservation key can exist at most once per tenant, so a
            // replayed order-confirmation cannot create a second hold for the same line.
            entity.HasIndex(x => new { x.BusinessUnitId, x.IdempotencyKey })
                .IsUnique()
                .HasDatabaseName("UX_stock_reservations_idempotency");

            // Availability is computed by summing ACTIVE rows per inventory item — index the hot path.
            entity.HasIndex(x => new { x.BusinessUnitId, x.InventoryId, x.Status })
                .HasDatabaseName("IX_stock_reservations_availability");

            entity.HasIndex(x => new { x.BusinessUnitId, x.OrderId })
                .HasDatabaseName("IX_stock_reservations_order");

            entity.HasOne<Models.Inventory>()
                .WithMany()
                .HasForeignKey(x => new { x.BusinessUnitId, x.InventoryId })
                .HasPrincipalKey(x => new { BusinessUnitId = x.Buid, InventoryId = x.Id })
                .OnDelete(DeleteBehavior.Restrict);
        });

        return modelBuilder;
    }
}
