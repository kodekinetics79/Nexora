using ERP_RFQ_Automation.Models;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.CustomerResolution;

public static class CustomerResolutionModelBuilderExtensions
{
    public static ModelBuilder ApplyCustomerResolutionModel(this ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        modelBuilder.Entity<LeadCustomerMatchCandidate>(entity =>
        {
            entity.ToTable("lead_customer_match_candidates");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Confidence).HasPrecision(5, 4);
            entity.Property(x => x.ReasonCode).HasMaxLength(40).IsRequired();
            entity.Property(x => x.Explanation).HasMaxLength(500).IsRequired();
            entity.Property(x => x.CreatedOn).HasDefaultValueSql("now()");
            entity.HasIndex(x => new { x.BusinessUnitId, x.LeadId, x.Rank })
                .IsUnique()
                .HasDatabaseName("UX_lead_customer_match_candidates_lead_rank");
            entity.HasIndex(x => new { x.BusinessUnitId, x.CustomerId })
                .HasDatabaseName("IX_lead_customer_match_candidates_customer");
            entity.HasCheckConstraint("CK_lead_customer_match_candidates_Rank", "\"Rank\" BETWEEN 1 AND 5");
            entity.HasCheckConstraint("CK_lead_customer_match_candidates_Confidence",
                "\"Confidence\" >= 0 AND \"Confidence\" <= 1");
            // Composite tenant-carrying foreign keys: a candidate can never point at
            // another tenant's lead or customer, enforced by the database itself.
            entity.HasOne<Lead>().WithMany()
                .HasForeignKey(x => new { x.BusinessUnitId, x.LeadId })
                .HasPrincipalKey(x => new { x.BusinessUnitId, x.Id })
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne<Customer>().WithMany()
                .HasForeignKey(x => new { x.BusinessUnitId, x.CustomerId })
                .HasPrincipalKey(x => new { x.Buid, x.Id })
                .OnDelete(DeleteBehavior.Restrict);
        });

        return modelBuilder;
    }
}
