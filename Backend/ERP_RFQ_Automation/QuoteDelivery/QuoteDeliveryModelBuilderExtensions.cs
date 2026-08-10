using ERP_RFQ_Automation.Models;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.QuoteDelivery;

public static class QuoteDeliveryModelBuilderExtensions
{
    public static ModelBuilder ApplyQuoteDeliveryModel(this ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<QuoteDeliveryRequest>(entity =>
        {
            entity.ToTable("quote_delivery_requests");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.IdempotencyKey).HasMaxLength(160).IsRequired();
            entity.Property(x => x.RecipientEmail).HasMaxLength(320).IsRequired();
            entity.Property(x => x.Subject).HasMaxLength(998).IsRequired();
            entity.Property(x => x.Body).HasMaxLength(100_000).IsRequired();
            entity.Property(x => x.FromEmail).HasMaxLength(320);
            entity.Property(x => x.AttachmentFileName).HasMaxLength(255).IsRequired();
            entity.Property(x => x.LeaseOwner).HasMaxLength(200);
            entity.Property(x => x.LastErrorCode).HasMaxLength(160);
            // Lowercase hex SHA-256 produced by PriceAttestationService.ComputeFingerprint.
            entity.Property(x => x.AttestedPriceFingerprint).HasMaxLength(64);
            entity.Property(x => x.Version).IsConcurrencyToken();
            entity.HasIndex(x => new { x.BusinessUnitId, x.IdempotencyKey }).IsUnique();
            entity.HasIndex(x => new { x.CompletedOn, x.DeadLetteredOn, x.AvailableOn, x.LeaseUntil });
            entity.HasCheckConstraint("CK_quote_delivery_requests_state", "\"AttemptCount\" >= 0 AND \"Version\" > 0 AND trim(\"IdempotencyKey\") <> '' AND trim(\"RecipientEmail\") <> '' AND trim(\"Subject\") <> '' AND trim(\"AttachmentFileName\") <> '' AND ((\"LeaseOwner\" IS NULL AND \"LeaseToken\" IS NULL AND \"LeaseUntil\" IS NULL) OR (\"LeaseOwner\" IS NOT NULL AND \"LeaseToken\" IS NOT NULL AND \"LeaseUntil\" IS NOT NULL)) AND NOT (\"CompletedOn\" IS NOT NULL AND \"DeadLetteredOn\" IS NOT NULL) AND ((\"CompletedOn\" IS NULL AND \"DeadLetteredOn\" IS NULL) OR (\"LeaseOwner\" IS NULL AND \"LeaseToken\" IS NULL AND \"LeaseUntil\" IS NULL))");
            entity.HasOne<Quote>().WithMany()
                .HasForeignKey(x => new { x.BusinessUnitId, x.QuoteId })
                .HasPrincipalKey(x => new { x.BusinessUnitId, x.Id })
                .OnDelete(DeleteBehavior.Restrict);
        });
        return modelBuilder;
    }
}
