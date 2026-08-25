using ERP_RFQ_Automation.CommercialCases.Participation;
using ERP_RFQ_Automation.LeadIdentity;
using ERP_RFQ_Automation.Models;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.CommercialCases.Promotion;

public static class RfqPromotionModelBuilderExtensions
{
    public static void ConfigureRfqPromotion(this ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<RfqPromotion>(e =>
        {
            e.ToTable("RfqPromotions");
            e.HasKey(x => x.Id);
            e.HasAlternateKey(x => new { x.BusinessUnitId, x.Id });
            e.HasAlternateKey(x => new { x.BusinessUnitId, x.Id, x.LeadId, x.LeadRevisionId, x.ParticipationDecisionId });
            e.Property(x => x.IdempotencyKey).HasMaxLength(256);
            e.Property(x => x.RequestHash).HasMaxLength(64).IsFixedLength();
            e.Property(x => x.PromotedBy).HasMaxLength(256);
            e.HasIndex(x => new { x.BusinessUnitId, x.IdempotencyKey }).IsUnique();
            e.HasIndex(x => new { x.BusinessUnitId, x.ParticipationDecisionId }).IsUnique();
            e.HasOne<Lead>().WithMany().HasForeignKey(x => new { x.BusinessUnitId, x.LeadId })
                .HasPrincipalKey(x => new { x.BusinessUnitId, x.Id }).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<LeadRevision>().WithMany().HasForeignKey(x => new { x.BusinessUnitId, x.LeadRevisionId, x.LeadId })
                .HasPrincipalKey(x => new { x.BusinessUnitId, x.Id, x.LeadId }).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<LeadParticipationDecision>().WithMany()
                .HasForeignKey(x => new { x.BusinessUnitId, x.ParticipationDecisionId, x.LeadId, x.LeadRevisionId })
                .HasPrincipalKey(x => new { x.BusinessUnitId, x.Id, x.LeadId, x.LeadRevisionId })
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<Rfq>(e =>
        {
            // The focused PostgreSQL migration owns this NOT VALID check. EF cannot model
            // NOT VALID and EnsureCreated would otherwise reject historical Lead-linked RFQs
            // that the production migration intentionally preserves. All new application writes
            // are blocked at the database and the sole creator is RfqPromotionService.
            e.ToTable("RFQ");
            e.HasIndex(x => new { x.BusinessUnitId, x.PromotionId }).IsUnique()
                .HasFilter("\"PromotionId\" IS NOT NULL");
            e.HasOne<RfqPromotion>().WithOne()
                .HasForeignKey<Rfq>("BusinessUnitId", "PromotionId", "LeadId", "SourceLeadRevisionId", "ParticipationDecisionId")
                .HasPrincipalKey<RfqPromotion>("BusinessUnitId", "Id", "LeadId", "LeadRevisionId", "ParticipationDecisionId")
                .OnDelete(DeleteBehavior.Restrict);
            e.HasOne<LeadRevision>().WithMany()
                .HasForeignKey("BusinessUnitId", "SourceLeadRevisionId")
                .HasPrincipalKey("BusinessUnitId", "Id").OnDelete(DeleteBehavior.Restrict);
            e.HasOne<LeadParticipationDecision>().WithMany()
                .HasForeignKey("BusinessUnitId", "ParticipationDecisionId")
                .HasPrincipalKey("BusinessUnitId", "Id").OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<Rfqitem>(e =>
        {
            e.ToTable("RFQItems");
            e.HasIndex(x => new { x.SourceBusinessUnitId, x.SourceLeadItemRevisionId });
            e.HasOne<LeadItemRevision>().WithMany()
                .HasForeignKey(x => new { x.SourceBusinessUnitId, x.SourceLeadItemRevisionId, x.SourceLeadRevisionId, x.SourceLeadId })
                .HasPrincipalKey(x => new { x.BusinessUnitId, x.Id, x.LeadRevisionId, x.LeadId })
                .OnDelete(DeleteBehavior.Restrict);
            // The database migration owns the four-column parent-lineage FK. Modelling its
            // nullable principal tuple as an EF alternate key makes ordinary legacy RFQs
            // (whose lineage columns are all null) unsaveable: EF treats nullable key values
            // as unresolved store-generated values. The existing Rfqitem -> Rfq navigation
            // already maps RFQID; PostgreSQL remains the authoritative consistency backstop
            // for promoted rows without breaking legacy/leadless RFQ construction in memory.
        });
    }
}
