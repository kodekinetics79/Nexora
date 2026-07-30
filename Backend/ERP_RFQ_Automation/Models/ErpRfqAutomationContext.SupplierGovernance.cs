using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.Models;

public partial class ErpRfqAutomationContext
{
    partial void ConfigureSupplierGovernanceModel(ModelBuilder modelBuilder);

    partial void ConfigureSupplierGovernanceModel(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Supplier>(entity =>
        {
            entity.Property(x => x.GovernanceStatus).HasMaxLength(32)
                .HasDefaultValue(SupplierGovernanceStatuses.Unverified).IsRequired();
            entity.Property(x => x.VerificationStatus).HasMaxLength(32)
                .HasDefaultValue(SupplierGovernanceUnknown.Unknown).IsRequired();
            entity.Property(x => x.ComplianceStatus).HasMaxLength(32)
                .HasDefaultValue(SupplierGovernanceUnknown.Unknown).IsRequired();
            entity.Property(x => x.RiskStatus).HasMaxLength(32)
                .HasDefaultValue(SupplierGovernanceUnknown.Unknown).IsRequired();
            entity.Property(x => x.ReadinessStatus).HasMaxLength(32)
                .HasDefaultValue(SupplierReadinessStatuses.ReviewRequired).IsRequired();
            entity.Property(x => x.GovernanceReviewedBy).HasMaxLength(255);
            entity.Property(x => x.ConcurrencyToken).IsConcurrencyToken();
            entity.HasIndex(x => new { x.Buid, x.GovernanceStatus, x.ReadinessStatus })
                .HasDatabaseName("IX_Suppliers_BU_Governance_Readiness");
            entity.HasIndex(x => new { x.Buid, x.DocId }).IsUnique()
                .HasFilter("\"DocId\" IS NOT NULL")
                .HasDatabaseName("UX_Suppliers_BU_DocId");
            entity.HasIndex(x => x.ContactEmail, "UQ__Supplier__FFA796CDFB352BC7").IsUnique(false);
            entity.HasIndex(x => new { x.Buid, x.ContactEmail }).IsUnique()
                .HasFilter("\"ContactEmail\" IS NOT NULL AND \"BUID\" IS NOT NULL")
                .HasDatabaseName("UX_Suppliers_BU_ContactEmail");
            entity.HasCheckConstraint("CK_Suppliers_GovernanceStatus",
                "\"GovernanceStatus\" IN ('DISCOVERED','UNVERIFIED','REVIEW_REQUIRED','PROVISIONAL','APPROVED','PREFERRED','RESTRICTED','BLOCKED','INACTIVE')");
            entity.HasCheckConstraint("CK_Suppliers_ReadinessStatus",
                "\"ReadinessStatus\" IN ('REVIEW_REQUIRED','READY','RESTRICTED','BLOCKED')");
            entity.HasCheckConstraint("CK_Suppliers_VerificationStatus",
                "\"VerificationStatus\" IN ('UNKNOWN','PENDING','VERIFIED','FAILED','EXPIRED')");
            entity.HasCheckConstraint("CK_Suppliers_ComplianceStatus",
                "\"ComplianceStatus\" IN ('UNKNOWN','PENDING','CLEARED','RESTRICTED','BLOCKED','FAILED')");
            entity.HasCheckConstraint("CK_Suppliers_RiskStatus",
                "\"RiskStatus\" IN ('UNKNOWN','LOW','MEDIUM','HIGH','BLOCKED')");
        });

        modelBuilder.Entity<Contact>(entity =>
        {
            entity.HasIndex(x => new { x.BusinessUnitId, x.CustomerId }).IsUnique()
                .HasFilter("\"IsPrimary\" = TRUE AND \"IsActive\" IS DISTINCT FROM FALSE AND \"CustomerID\" IS NOT NULL")
                .HasDatabaseName("UX_Contacts_BU_Customer_Primary");
            entity.HasIndex(x => new { x.BusinessUnitId, x.SupplierId }).IsUnique()
                .HasFilter("\"IsPrimary\" = TRUE AND \"IsActive\" IS DISTINCT FROM FALSE AND \"SupplierID\" IS NOT NULL")
                .HasDatabaseName("UX_Contacts_BU_Supplier_Primary");
        });
    }
}
