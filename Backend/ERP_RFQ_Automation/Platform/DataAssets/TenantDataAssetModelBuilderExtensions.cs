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

        modelBuilder.Entity<TenantDataRecoveryEvidence>(entity =>
        {
            entity.ToTable("TenantDataRecoveryEvidence", "platform", table =>
            {
                table.HasCheckConstraint("CK_TenantDataRecoveryEvidence_Rpo", "\"ConfiguredRpoSeconds\" IS NULL OR \"ConfiguredRpoSeconds\" > 0");
                table.HasCheckConstraint("CK_TenantDataRecoveryEvidence_Rto", "\"ConfiguredRtoSeconds\" IS NULL OR \"ConfiguredRtoSeconds\" > 0");
                table.HasCheckConstraint("CK_TenantDataRecoveryEvidence_Rows", "\"CustomerRowsObserved\" IS NULL OR \"CustomerRowsObserved\" >= 0");
            });
            entity.HasKey(x => x.Id);
            entity.Property(x => x.ScopeKey).HasMaxLength(80).IsRequired();
            entity.Property(x => x.EvidenceType).HasMaxLength(64).IsRequired();
            entity.Property(x => x.OpaqueProviderReference).HasMaxLength(256).IsRequired();
            entity.Property(x => x.OpaqueBackupSetReference).HasMaxLength(256);
            entity.Property(x => x.EvidenceReference).HasMaxLength(256).IsRequired();
            entity.Property(x => x.EvidenceSha256).HasMaxLength(64).IsFixedLength().IsRequired();
            entity.Property(x => x.CorrelationId).HasMaxLength(128).IsRequired();
            entity.Property(x => x.IdempotencyKey).HasMaxLength(128).IsRequired();
            entity.Property(x => x.ActorEmail).HasMaxLength(320).IsRequired();
            entity.Property(x => x.Reason).HasMaxLength(1000).IsRequired();
            entity.HasIndex(x => new { x.TenantId, x.IdempotencyKey }).IsUnique();
            entity.HasIndex(x => new { x.TenantId, x.ScopeKey, x.EvidenceType, x.CompletedUtc });
        });

        modelBuilder.Entity<TenantDeletionCertificate>(entity =>
        {
            entity.ToTable("TenantDeletionCertificates", "platform");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.TenantSlug).HasMaxLength(64).IsRequired();
            entity.Property(x => x.ActorEmail).HasMaxLength(320).IsRequired();
            entity.Property(x => x.EvidenceManifestSha256).HasMaxLength(64).IsFixedLength().IsRequired();
            entity.Property(x => x.EvidenceIdsJson).HasColumnType("jsonb").IsRequired();
            entity.Property(x => x.Reason).HasMaxLength(1000).IsRequired();
            entity.HasIndex(x => x.TenantId).IsUnique();
        });

        return modelBuilder;
    }
}
