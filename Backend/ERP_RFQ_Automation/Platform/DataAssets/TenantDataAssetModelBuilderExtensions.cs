using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.Platform.DataAssets;

public static class TenantDataAssetModelBuilderExtensions
{
    public static ModelBuilder ApplyTenantDataAssetModel(this ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<TenantDataAsset>(entity =>
        {
            entity.ToTable("TenantDataAssets", "platform", table =>
            {
                table.HasCheckConstraint("CK_TenantDataAssets_BackupPolicyVersion", "\"BackupPolicyVersion\" > 0");
                table.HasCheckConstraint("CK_TenantDataAssets_VerificationVersion", "\"VerificationVersion\" >= 0");
                table.HasCheckConstraint("CK_TenantDataAssets_Version", "\"Version\" > 0");
            });
            entity.HasKey(x => x.Id);
            entity.Property(x => x.LogicalKey).HasMaxLength(80).IsRequired();
            entity.Property(x => x.AssetType).HasMaxLength(64).IsRequired();
            entity.Property(x => x.OpaqueProviderReference).HasMaxLength(256).IsRequired();
            entity.Property(x => x.Region).HasMaxLength(64).IsRequired();
            entity.Property(x => x.Classification).HasMaxLength(64).IsRequired();
            entity.Property(x => x.Disposition).HasMaxLength(96).IsRequired();
            entity.Property(x => x.BackupPolicyReference).HasMaxLength(128).IsRequired();
            entity.Property(x => x.Status).HasMaxLength(32).IsRequired();
            entity.Property(x => x.VerificationEvidenceReference).HasMaxLength(256);
            entity.Property(x => x.VerificationEvidenceSha256).HasMaxLength(64).IsFixedLength();
            entity.Property(x => x.VerifiedBy).HasMaxLength(256);
            entity.Property(x => x.CreatedBy).HasMaxLength(256).IsRequired();
            entity.Property(x => x.ModifiedBy).HasMaxLength(256);
            entity.Property(x => x.Version).IsConcurrencyToken();
            entity.HasIndex(x => new { x.TenantId, x.LogicalKey }).IsUnique();
            entity.HasOne<Models.Tenant>().WithMany().HasForeignKey(x => x.TenantId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        return modelBuilder;
    }
}
