using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.Billing.Accounting;

public static class AccountingOutboxModelConfiguration
{
    public static void Apply(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<AccountingOutboxMessage>(entity =>
        {
            entity.ToTable("AccountingOutbox", "platform");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.MessageType).HasMaxLength(64).IsRequired();
            entity.Property(x => x.IdempotencyKey).HasMaxLength(160).IsRequired();
            entity.Property(x => x.PayloadJson).HasColumnType("jsonb").IsRequired();
            entity.Property(x => x.PayloadSha256).HasMaxLength(64).IsFixedLength().IsRequired();
            entity.Property(x => x.Status).HasConversion<string>().HasMaxLength(24);
            entity.Property(x => x.ReconciliationStatus).HasConversion<string>().HasMaxLength(32);
            entity.Property(x => x.CreatedAtUtc).HasColumnType("timestamp with time zone");
            entity.Property(x => x.AvailableAtUtc).HasColumnType("timestamp with time zone");
            entity.Property(x => x.LastAttemptAtUtc).HasColumnType("timestamp with time zone");
            entity.Property(x => x.LeaseExpiresAtUtc).HasColumnType("timestamp with time zone");
            entity.Property(x => x.AcknowledgedAtUtc).HasColumnType("timestamp with time zone");
            entity.Property(x => x.RedrivenAtUtc).HasColumnType("timestamp with time zone");
            entity.Property(x => x.WorkerId).HasMaxLength(128);
            entity.Property(x => x.LastFailureCode).HasMaxLength(64);
            entity.Property(x => x.ExternalReference).HasMaxLength(256);
            entity.Property(x => x.ExternalReceiptSha256).HasMaxLength(64).IsFixedLength();
            entity.Property(x => x.AcknowledgedBy).HasMaxLength(256);
            entity.Property(x => x.RedrivenBy).HasMaxLength(256);
            entity.Property(x => x.RedriveReason).HasMaxLength(1000);
            entity.HasIndex(x => x.IdempotencyKey).IsUnique()
                .HasDatabaseName("UX_AccountingOutbox_IdempotencyKey");
            entity.HasIndex(x => new { x.Status, x.AvailableAtUtc });
            entity.HasIndex(x => new { x.SubscriptionInvoiceId, x.MessageType }).IsUnique()
                .HasDatabaseName("UX_AccountingOutbox_Invoice_MessageType");
            entity.HasOne<SubscriptionInvoice>().WithMany()
                .HasForeignKey(x => new { x.TenantId, x.SubscriptionInvoiceId })
                .HasPrincipalKey(x => new { x.TenantId, x.Id })
                .OnDelete(DeleteBehavior.Restrict);
        });
    }
}
