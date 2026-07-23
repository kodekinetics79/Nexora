using ERP_RFQ_Automation.Models;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.CommercialFinance;

public static class FinanceModelBuilderExtensions
{
    public static void ConfigureCommercialFinance(this ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Order>().HasAlternateKey(x => new { x.BusinessUnitId, x.Id });
        modelBuilder.Entity<CommercialCase>().HasAlternateKey(x => new { x.BusinessUnitId, x.Id });

        modelBuilder.Entity<ReceivableDocument>(entity =>
        {
            entity.ToTable("ReceivableDocuments");
            entity.HasKey(x => x.Id);
            entity.HasAlternateKey(x => new { x.BusinessUnitId, x.Id });
            entity.Property(x => x.DocumentType).HasMaxLength(20).IsRequired();
            entity.Property(x => x.Status).HasMaxLength(20).IsRequired();
            entity.Property(x => x.DocumentNumber).HasMaxLength(50);
            entity.Property(x => x.IdempotencyKey).HasMaxLength(128).IsRequired();
            entity.Property(x => x.RequestHash).HasMaxLength(64).IsRequired();
            entity.Property(x => x.VoidReason).HasMaxLength(500);
            entity.Property(x => x.CreatedBy).HasMaxLength(255).IsRequired();
            entity.Property(x => x.IssuedBy).HasMaxLength(255);
            entity.Property(x => x.Version).IsConcurrencyToken();
            entity.Property(x => x.SubTotal).HasPrecision(18, 2);
            entity.Property(x => x.DiscountAmount).HasPrecision(18, 2);
            entity.Property(x => x.TaxAmount).HasPrecision(18, 2);
            entity.Property(x => x.TotalAmount).HasPrecision(18, 2);
            entity.HasIndex(x => new { x.BusinessUnitId, x.IdempotencyKey }).IsUnique()
                .HasDatabaseName("UX_ReceivableDocuments_BU_Idempotency");
            entity.HasIndex(x => new { x.BusinessUnitId, x.DocumentNumber }).IsUnique()
                .HasFilter("\"DocumentNumber\" IS NOT NULL")
                .HasDatabaseName("UX_ReceivableDocuments_BU_Number");
            entity.HasIndex(x => new { x.BusinessUnitId, x.Status, x.DueDate })
                .HasDatabaseName("IX_ReceivableDocuments_BU_Status_Due");
            entity.HasCheckConstraint("CK_ReceivableDocuments_Total", "\"TotalAmount\" >= 0 AND \"SubTotal\" >= 0 AND \"DiscountAmount\" >= 0 AND \"TaxAmount\" >= 0");
            entity.HasCheckConstraint("CK_ReceivableDocuments_Reconciles", "\"TotalAmount\" = round(\"SubTotal\" - \"DiscountAmount\" + \"TaxAmount\", 2)");
            entity.HasCheckConstraint("CK_ReceivableDocuments_Issue", "(\"Status\" = 'Draft' AND \"DocumentNumber\" IS NULL AND \"IssuedOn\" IS NULL) OR (\"Status\" IN ('Issued', 'Void') AND \"DocumentNumber\" IS NOT NULL AND \"IssuedOn\" IS NOT NULL)");
            entity.HasOne<Order>().WithMany()
                .HasForeignKey(x => new { x.BusinessUnitId, x.OrderId })
                .HasPrincipalKey(x => new { x.BusinessUnitId, x.Id }).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<CommercialCase>().WithMany()
                .HasForeignKey(x => new { x.BusinessUnitId, x.CommercialCaseId })
                .HasPrincipalKey(x => new { x.BusinessUnitId, x.Id }).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<Customer>().WithMany().HasForeignKey(x => x.CustomerId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<Currency>().WithMany().HasForeignKey(x => x.CurrencyId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<ReceivableDocument>().WithMany()
                .HasForeignKey(x => new { x.BusinessUnitId, x.ParentDocumentId })
                .HasPrincipalKey(x => new { x.BusinessUnitId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<ReceivableDocumentLine>(entity =>
        {
            entity.ToTable("ReceivableDocumentLines");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Description).HasMaxLength(1000).IsRequired();
            entity.HasCheckConstraint("CK_ReceivableDocumentLines_Money", "\"Quantity\" > 0 AND \"UnitPrice\" >= 0 AND \"DiscountAmount\" >= 0 AND \"TaxAmount\" >= 0 AND \"LineTotal\" >= 0");
            entity.Property(x => x.Quantity).HasPrecision(18, 6);
            entity.Property(x => x.UnitPrice).HasPrecision(18, 2);
            entity.Property(x => x.DiscountAmount).HasPrecision(18, 2);
            entity.Property(x => x.TaxAmount).HasPrecision(18, 2);
            entity.Property(x => x.LineTotal).HasPrecision(18, 2);
            entity.HasOne(x => x.Document).WithMany(x => x.Lines)
                .HasForeignKey(x => new { x.BusinessUnitId, x.ReceivableDocumentId })
                .HasPrincipalKey(x => new { x.BusinessUnitId, x.Id }).OnDelete(DeleteBehavior.Cascade);
            entity.HasOne<OrderItem>().WithMany().HasForeignKey(x => x.OrderItemId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<CustomerPayment>(entity =>
        {
            entity.ToTable("CustomerPayments");
            entity.HasKey(x => x.Id);
            entity.HasAlternateKey(x => new { x.BusinessUnitId, x.Id });
            entity.Property(x => x.ReceiptNumber).HasMaxLength(50).IsRequired();
            entity.Property(x => x.Status).HasMaxLength(20).IsRequired();
            entity.Property(x => x.Method).HasMaxLength(100);
            entity.Property(x => x.BankReference).HasMaxLength(200);
            entity.Property(x => x.IdempotencyKey).HasMaxLength(128).IsRequired();
            entity.Property(x => x.RequestHash).HasMaxLength(64).IsRequired();
            entity.Property(x => x.ReversalReason).HasMaxLength(500);
            entity.Property(x => x.Version).IsConcurrencyToken();
            entity.Property(x => x.Amount).HasPrecision(18, 2);
            entity.Property(x => x.CreatedBy).HasMaxLength(255).IsRequired();
            entity.HasIndex(x => new { x.BusinessUnitId, x.IdempotencyKey }).IsUnique()
                .HasDatabaseName("UX_CustomerPayments_BU_Idempotency");
            entity.HasIndex(x => new { x.BusinessUnitId, x.ReceiptNumber }).IsUnique()
                .HasDatabaseName("UX_CustomerPayments_BU_Number");
            entity.HasCheckConstraint("CK_CustomerPayments_Amount", "\"Amount\" > 0");
            entity.HasOne<Customer>().WithMany().HasForeignKey(x => x.CustomerId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<CommercialCase>().WithMany()
                .HasForeignKey(x => new { x.BusinessUnitId, x.CommercialCaseId })
                .HasPrincipalKey(x => new { x.BusinessUnitId, x.Id }).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<Currency>().WithMany().HasForeignKey(x => x.CurrencyId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<PaymentAllocation>(entity =>
        {
            entity.ToTable("PaymentAllocations");
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => new { x.BusinessUnitId, x.CustomerPaymentId, x.ReceivableDocumentId }).IsUnique()
                .HasDatabaseName("UX_PaymentAllocations_BU_Payment_Document");
            entity.HasCheckConstraint("CK_PaymentAllocations_Amount", "\"Amount\" > 0");
            entity.Property(x => x.Amount).HasPrecision(18, 2);
            entity.HasOne(x => x.Payment).WithMany(x => x.Allocations)
                .HasForeignKey(x => new { x.BusinessUnitId, x.CustomerPaymentId })
                .HasPrincipalKey(x => new { x.BusinessUnitId, x.Id }).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(x => x.Document).WithMany(x => x.Allocations)
                .HasForeignKey(x => new { x.BusinessUnitId, x.ReceivableDocumentId })
                .HasPrincipalKey(x => new { x.BusinessUnitId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<LegalDocumentCounter>(entity =>
        {
            entity.ToTable("LegalDocumentCounters");
            entity.HasKey(x => new { x.BusinessUnitId, x.DocumentType, x.FiscalYear });
            entity.Property(x => x.DocumentType).HasMaxLength(20);
            entity.HasCheckConstraint("CK_LegalDocumentCounters_Next", "\"NextNumber\" > 0");
        });

        modelBuilder.Entity<CommercialFinanceAudit>(entity =>
        {
            entity.ToTable("CommercialFinanceAudits");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.AggregateType).HasMaxLength(40).IsRequired();
            entity.Property(x => x.Action).HasMaxLength(40).IsRequired();
            entity.Property(x => x.Actor).HasMaxLength(255).IsRequired();
            entity.Property(x => x.DetailJson).HasColumnType("jsonb").IsRequired();
            entity.HasIndex(x => new { x.BusinessUnitId, x.AggregateType, x.AggregateId, x.OccurredOn });
        });
    }
}
