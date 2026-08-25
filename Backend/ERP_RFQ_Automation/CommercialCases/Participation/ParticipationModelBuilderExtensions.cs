using ERP_RFQ_Automation.LeadIdentity;
using ERP_RFQ_Automation.Models;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.CommercialCases.Participation;

public static class ParticipationModelBuilderExtensions
{
    public static void ConfigureLeadParticipation(this ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<LeadFitAssessment>(e =>
        {
            e.ToTable("LeadFitAssessments");
            e.HasKey(x => x.Id);
            e.HasAlternateKey(x => new { x.BusinessUnitId, x.Id });
            e.HasAlternateKey(x => new { x.BusinessUnitId, x.Id, x.LeadId, x.LeadRevisionId });
            e.Property(x => x.PolicyVersion).HasMaxLength(64);
            e.Property(x => x.Recommendation).HasMaxLength(32);
            e.Property(x => x.AssessmentJson).HasColumnType("jsonb");
            e.Property(x => x.IdempotencyKey).HasMaxLength(256);
            e.Property(x => x.RequestHash).HasMaxLength(64).IsFixedLength();
            e.Property(x => x.AssessedBy).HasMaxLength(256);
            e.HasIndex(x => new { x.BusinessUnitId, x.IdempotencyKey }).IsUnique();
            e.HasIndex(x => new { x.BusinessUnitId, x.LeadRevisionId, x.Sequence }).IsUnique();
            e.HasOne<Lead>().WithMany().HasForeignKey(x => new { x.BusinessUnitId, x.LeadId })
                .HasPrincipalKey(x => new { x.BusinessUnitId, x.Id }).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<LeadRevision>().WithMany().HasForeignKey(x => new { x.BusinessUnitId, x.LeadRevisionId, x.LeadId })
                .HasPrincipalKey(x => new { x.BusinessUnitId, x.Id, x.LeadId }).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<LeadParticipationDecision>(e =>
        {
            e.ToTable("LeadParticipationDecisions", table => table.HasCheckConstraint(
                "CK_LeadParticipationDecisions_Outcome",
                "\"Outcome\" IN ('Pending','FullBid','PartialBid','NoBid')"));
            e.HasKey(x => x.Id);
            e.HasAlternateKey(x => new { x.BusinessUnitId, x.Id });
            e.HasAlternateKey(x => new { x.BusinessUnitId, x.Id, x.LeadId, x.LeadRevisionId });
            e.Property(x => x.Outcome).HasConversion<string>().HasMaxLength(24);
            e.Property(x => x.ReasonCode).HasMaxLength(64);
            e.Property(x => x.Notes).HasMaxLength(2000);
            e.Property(x => x.IdempotencyKey).HasMaxLength(256);
            e.Property(x => x.RequestHash).HasMaxLength(64).IsFixedLength();
            e.Property(x => x.DecidedBy).HasMaxLength(256);
            e.HasIndex(x => new { x.BusinessUnitId, x.IdempotencyKey }).IsUnique();
            e.HasIndex(x => new { x.BusinessUnitId, x.LeadRevisionId, x.Sequence }).IsUnique();
            e.HasOne<Lead>().WithMany().HasForeignKey(x => new { x.BusinessUnitId, x.LeadId })
                .HasPrincipalKey(x => new { x.BusinessUnitId, x.Id }).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<LeadRevision>().WithMany().HasForeignKey(x => new { x.BusinessUnitId, x.LeadRevisionId, x.LeadId })
                .HasPrincipalKey(x => new { x.BusinessUnitId, x.Id, x.LeadId }).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<LeadFitAssessment>().WithMany()
                .HasForeignKey(x => new { x.BusinessUnitId, x.FitAssessmentId, x.LeadId, x.LeadRevisionId })
                .HasPrincipalKey(x => new { x.BusinessUnitId, x.Id, x.LeadId, x.LeadRevisionId })
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<LeadLineParticipationDecision>(e =>
        {
            e.ToTable("LeadLineParticipationDecisions", table =>
            {
                table.HasCheckConstraint("CK_LeadLineParticipationDecisions_Choice",
                    "\"Choice\" IN ('Pending','Bid','NoBid','Clarify')");
                table.HasCheckConstraint("CK_LeadLineParticipationDecisions_NoBidReason",
                    "\"Choice\" NOT IN ('NoBid','Clarify') OR ((\"ReasonCode\" IS NOT NULL AND trim(\"ReasonCode\") <> '') OR (\"ReasonNotes\" IS NOT NULL AND length(trim(\"ReasonNotes\")) >= 5))");
                table.HasCheckConstraint("CK_LeadLineParticipationDecisions_Quantity",
                    "\"Quantity\" IS NULL OR \"Quantity\" > 0");
                table.HasCheckConstraint("CK_LeadLineParticipationDecisions_BidCommercialIdentity",
                    "\"Choice\" <> 'Bid' OR (\"Quantity\" > 0 AND \"UomId\" IS NOT NULL AND \"CurrencyId\" IS NOT NULL AND \"UnitOfMeasure\" IS NOT NULL AND trim(\"UnitOfMeasure\") <> '' AND \"Currency\" IS NOT NULL AND trim(\"Currency\") <> '')");
            });
            e.HasKey(x => x.Id);
            e.Property(x => x.Choice).HasConversion<string>().HasMaxLength(16);
            e.Property(x => x.ReasonCode).HasMaxLength(64);
            e.Property(x => x.ReasonNotes).HasMaxLength(2000);
            e.Property(x => x.UnitOfMeasure).HasMaxLength(200);
            e.Property(x => x.Currency).HasMaxLength(8);
            e.Property(x => x.CatalogPolicyVersion).HasMaxLength(64);
            e.Property(x => x.WarningSnapshotJson).HasColumnType("jsonb");
            e.HasOne<SetUom>().WithMany().HasForeignKey(x => new { x.BusinessUnitId, x.UomId })
                .HasPrincipalKey(x => new { x.BusinessUnitId, x.UomId }).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<Currency>().WithMany().HasForeignKey(x => new { x.BusinessUnitId, x.CurrencyId })
                .HasPrincipalKey(x => new { x.BusinessUnitId, x.Id }).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(x => new { x.BusinessUnitId, x.ParticipationDecisionId, x.LeadItemRevisionId }).IsUnique();
            e.HasOne(x => x.ParticipationDecision).WithMany(x => x.Lines)
                .HasForeignKey(x => new { x.BusinessUnitId, x.ParticipationDecisionId, x.LeadId, x.LeadRevisionId })
                .HasPrincipalKey(x => new { x.BusinessUnitId, x.Id, x.LeadId, x.LeadRevisionId })
                .OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.LeadItemRevision).WithMany()
                .HasForeignKey(x => new { x.BusinessUnitId, x.LeadItemRevisionId, x.LeadRevisionId, x.LeadId })
                .HasPrincipalKey(x => new { x.BusinessUnitId, x.Id, x.LeadRevisionId, x.LeadId })
                .OnDelete(DeleteBehavior.Restrict);
        });
    }
}
