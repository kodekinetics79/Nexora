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
            entity.Property(x => x.AdjustmentReasonCode).HasMaxLength(50);
            entity.Property(x => x.AdjustmentReason).HasMaxLength(500);
            entity.Property(x => x.IdempotencyKey).HasMaxLength(128).IsRequired();
            entity.Property(x => x.RequestHash).HasMaxLength(64).IsRequired();
            entity.Property(x => x.VoidReason).HasMaxLength(500);
            entity.Property(x => x.VoidedBy).HasMaxLength(255);
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
            entity.HasCheckConstraint("CK_ReceivableDocuments_Type", "(\"DocumentType\" = 'Invoice' AND \"ParentDocumentId\" IS NULL AND \"AdjustmentReasonCode\" IS NULL AND \"AdjustmentReason\" IS NULL) OR (\"DocumentType\" IN ('CreditNote','DebitNote') AND \"ParentDocumentId\" IS NOT NULL AND \"AdjustmentReasonCode\" IS NOT NULL AND length(trim(\"AdjustmentReasonCode\")) > 0 AND \"AdjustmentReason\" IS NOT NULL AND length(trim(\"AdjustmentReason\")) > 0)");
            entity.HasCheckConstraint("CK_ReceivableDocuments_Reconciles", "\"TotalAmount\" = round(\"SubTotal\" - \"DiscountAmount\" + \"TaxAmount\", 2)");
            entity.HasCheckConstraint("CK_ReceivableDocuments_Issue", "(\"Status\" = 'Draft' AND \"DocumentNumber\" IS NULL AND \"IssuedOn\" IS NULL AND \"VoidedOn\" IS NULL AND \"VoidReason\" IS NULL AND \"VoidedBy\" IS NULL) OR (\"Status\" = 'Cancelled' AND \"DocumentNumber\" IS NULL AND \"IssuedOn\" IS NULL AND \"VoidedOn\" IS NOT NULL AND \"VoidReason\" IS NOT NULL AND length(trim(\"VoidReason\")) > 0 AND \"VoidedBy\" IS NOT NULL AND length(trim(\"VoidedBy\")) > 0) OR (\"Status\" IN ('Issued', 'Void') AND \"DocumentNumber\" IS NOT NULL AND \"IssuedOn\" IS NOT NULL)");
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
            entity.HasAlternateKey(x => new { x.BusinessUnitId, x.Id });
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
            entity.HasOne<ReceivableDocumentLine>().WithMany()
                .HasForeignKey(x => new { x.BusinessUnitId, x.ParentDocumentLineId })
                .HasPrincipalKey(x => new { x.BusinessUnitId, x.Id }).OnDelete(DeleteBehavior.Restrict);
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

        modelBuilder.Entity<ReceivableWriteOff>(entity =>
        {
            entity.ToTable("ReceivableWriteOffs");
            entity.HasKey(x => x.Id);
            entity.HasAlternateKey(x => new { x.BusinessUnitId, x.Id });
            entity.Property(x => x.WriteOffNumber).HasMaxLength(50);
            entity.Property(x => x.Status).HasMaxLength(20).IsRequired();
            entity.Property(x => x.TotalAmount).HasPrecision(18, 2);
            entity.Property(x => x.ReasonCode).HasMaxLength(50).IsRequired();
            entity.Property(x => x.Reason).HasMaxLength(500).IsRequired();
            entity.Property(x => x.EvidenceReference).HasMaxLength(500);
            entity.Property(x => x.PostingStatus).HasMaxLength(30).IsRequired();
            entity.Property(x => x.JournalReference).HasMaxLength(100);
            entity.Property(x => x.IdempotencyKey).HasMaxLength(128).IsRequired();
            entity.Property(x => x.RequestHash).HasMaxLength(64).IsRequired();
            entity.Property(x => x.CreatedBy).HasMaxLength(255).IsRequired();
            entity.Property(x => x.ApprovedBy).HasMaxLength(255);
            entity.Property(x => x.CancelledBy).HasMaxLength(255);
            entity.Property(x => x.CancellationReason).HasMaxLength(500);
            entity.Property(x => x.ReversedBy).HasMaxLength(255);
            entity.Property(x => x.ReversalReason).HasMaxLength(500);
            entity.Property(x => x.ReversalEvidenceReference).HasMaxLength(500);
            entity.Property(x => x.Version).IsConcurrencyToken();
            entity.HasIndex(x => new { x.BusinessUnitId, x.IdempotencyKey }).IsUnique()
                .HasDatabaseName("UX_ReceivableWriteOffs_BU_Idempotency");
            entity.HasIndex(x => new { x.BusinessUnitId, x.WriteOffNumber }).IsUnique()
                .HasFilter("\"WriteOffNumber\" IS NOT NULL")
                .HasDatabaseName("UX_ReceivableWriteOffs_BU_Number");
            entity.HasCheckConstraint("CK_ReceivableWriteOffs_Amount", "\"TotalAmount\" > 0");
            entity.HasOne<Customer>().WithMany().HasForeignKey(x => x.CustomerId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<Currency>().WithMany().HasForeignKey(x => x.CurrencyId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<CommercialCase>().WithMany()
                .HasForeignKey(x => new { x.BusinessUnitId, x.CommercialCaseId })
                .HasPrincipalKey(x => new { x.BusinessUnitId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<WriteOffAllocation>(entity =>
        {
            entity.ToTable("WriteOffAllocations");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Amount).HasPrecision(18, 2);
            entity.Property(x => x.BalanceBefore).HasPrecision(18, 2);
            entity.Property(x => x.BalanceAfter).HasPrecision(18, 2);
            entity.HasCheckConstraint("CK_WriteOffAllocations_Amount", "\"Amount\" > 0 AND \"BalanceBefore\" >= \"Amount\" AND \"BalanceAfter\" = round(\"BalanceBefore\" - \"Amount\", 2)");
            entity.HasIndex(x => new { x.BusinessUnitId, x.ReceivableWriteOffId, x.ReceivableDocumentId }).IsUnique()
                .HasDatabaseName("UX_WriteOffAllocations_BU_WriteOff_Document");
            entity.HasOne(x => x.WriteOff).WithMany(x => x.Allocations)
                .HasForeignKey(x => new { x.BusinessUnitId, x.ReceivableWriteOffId })
                .HasPrincipalKey(x => new { x.BusinessUnitId, x.Id }).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(x => x.Document).WithMany()
                .HasForeignKey(x => new { x.BusinessUnitId, x.ReceivableDocumentId })
                .HasPrincipalKey(x => new { x.BusinessUnitId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<CustomerRefund>(entity =>
        {
            entity.ToTable("CustomerRefunds");
            entity.HasKey(x => x.Id);
            entity.HasAlternateKey(x => new { x.BusinessUnitId, x.Id });
            entity.Property(x => x.RefundNumber).HasMaxLength(50);
            entity.Property(x => x.Status).HasMaxLength(20).IsRequired();
            entity.Property(x => x.Amount).HasPrecision(18, 2);
            entity.Property(x => x.Method).HasMaxLength(50).IsRequired();
            entity.Property(x => x.DestinationReference).HasMaxLength(200).IsRequired();
            entity.Property(x => x.ReasonCode).HasMaxLength(50).IsRequired();
            entity.Property(x => x.Reason).HasMaxLength(500).IsRequired();
            entity.Property(x => x.EvidenceReference).HasMaxLength(500);
            entity.Property(x => x.PostingStatus).HasMaxLength(30).IsRequired();
            entity.Property(x => x.JournalReference).HasMaxLength(100);
            entity.Property(x => x.IdempotencyKey).HasMaxLength(128).IsRequired();
            entity.Property(x => x.RequestHash).HasMaxLength(64).IsRequired();
            entity.Property(x => x.CreatedBy).HasMaxLength(255).IsRequired();
            entity.Property(x => x.ApprovedBy).HasMaxLength(255);
            entity.Property(x => x.ReleasedBy).HasMaxLength(255);
            entity.Property(x => x.DisbursementUpdatedBy).HasMaxLength(255);
            entity.Property(x => x.DisbursementFailureReason).HasMaxLength(500);
            entity.Property(x => x.CancelledBy).HasMaxLength(255);
            entity.Property(x => x.CancellationReason).HasMaxLength(500);
            entity.Property(x => x.ReversedBy).HasMaxLength(255);
            entity.Property(x => x.ReversalReason).HasMaxLength(500);
            entity.Property(x => x.ReversalEvidenceReference).HasMaxLength(500);
            entity.Property(x => x.Version).IsConcurrencyToken();
            entity.HasIndex(x => new { x.BusinessUnitId, x.IdempotencyKey }).IsUnique()
                .HasDatabaseName("UX_CustomerRefunds_BU_Idempotency");
            entity.HasIndex(x => new { x.BusinessUnitId, x.RefundNumber }).IsUnique()
                .HasFilter("\"RefundNumber\" IS NOT NULL")
                .HasDatabaseName("UX_CustomerRefunds_BU_Number");
            entity.HasCheckConstraint("CK_CustomerRefunds_Amount", "\"Amount\" > 0");
            entity.HasOne(x => x.SourcePayment).WithMany()
                .HasForeignKey(x => new { x.BusinessUnitId, x.SourcePaymentId })
                .HasPrincipalKey(x => new { x.BusinessUnitId, x.Id }).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<Customer>().WithMany().HasForeignKey(x => x.CustomerId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<Currency>().WithMany().HasForeignKey(x => x.CurrencyId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<CommercialCase>().WithMany()
                .HasForeignKey(x => new { x.BusinessUnitId, x.CommercialCaseId })
                .HasPrincipalKey(x => new { x.BusinessUnitId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<FinanceCommunicationContact>(entity =>
        {
            entity.ToTable("FinanceCommunicationContacts");
            entity.HasKey(x => x.Id);
            entity.HasAlternateKey(x => new { x.BusinessUnitId, x.Id });
            entity.Property(x => x.Purpose).HasMaxLength(30).IsRequired();
            entity.Property(x => x.Channel).HasMaxLength(20).IsRequired();
            entity.Property(x => x.DestinationToken).HasMaxLength(200).IsRequired();
            entity.Property(x => x.MaskedDestination).HasMaxLength(120).IsRequired();
            entity.Property(x => x.VerificationEvidenceReference).HasMaxLength(500).IsRequired();
            entity.Property(x => x.ProviderSignature).HasMaxLength(64);
            entity.Property(x => x.IdempotencyKey).HasMaxLength(128).IsRequired();
            entity.Property(x => x.RequestHash).HasMaxLength(64).IsRequired();
            entity.Property(x => x.CreatedBy).HasMaxLength(255).IsRequired();
            entity.Property(x => x.DeactivatedBy).HasMaxLength(255);
            entity.Property(x => x.DeactivationReason).HasMaxLength(500);
            entity.Property(x => x.Version).IsConcurrencyToken();
            entity.HasIndex(x => new { x.BusinessUnitId, x.CustomerId, x.Purpose, x.IsActive })
                .HasDatabaseName("IX_FinanceCommunicationContacts_BU_Customer_Purpose");
            entity.HasIndex(x => new { x.BusinessUnitId, x.DestinationToken }).IsUnique()
                .HasDatabaseName("UX_FinanceCommunicationContacts_BU_Token");
            entity.HasIndex(x => new { x.BusinessUnitId, x.IdempotencyKey }).IsUnique()
                .HasDatabaseName("UX_FinanceCommunicationContacts_BU_Idempotency");
            entity.HasIndex(x => new { x.BusinessUnitId, x.VerificationProviderEventId }).IsUnique()
                .HasDatabaseName("UX_FinanceCommunicationContacts_BU_VerificationEvent");
            entity.HasCheckConstraint("CK_FinanceCommunicationContacts_Effective",
                "\"EffectiveTo\" IS NULL OR \"EffectiveTo\" > \"EffectiveFrom\"");
            entity.HasOne<Customer>().WithMany().HasForeignKey(x => x.CustomerId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<CustomerStatement>(entity =>
        {
            entity.ToTable("CustomerStatements");
            entity.HasKey(x => x.Id);
            entity.HasAlternateKey(x => new { x.BusinessUnitId, x.Id });
            entity.Property(x => x.StatementNumber).HasMaxLength(50);
            entity.Property(x => x.Status).HasMaxLength(20).IsRequired();
            entity.Property(x => x.SourceFingerprint).HasMaxLength(64).IsRequired();
            entity.Property(x => x.SnapshotHash).HasMaxLength(64).IsRequired();
            entity.Property(x => x.ArtifactHash).HasMaxLength(64).IsRequired();
            entity.Property(x => x.ArtifactReference).HasMaxLength(500);
            entity.Property(x => x.ArtifactMediaType).HasMaxLength(100).IsRequired();
            entity.Property(x => x.ArtifactContent).HasColumnType("text").IsRequired();
            entity.Property(x => x.GeneratorVersion).HasMaxLength(40).IsRequired();
            entity.Property(x => x.TemplateVersion).HasMaxLength(40).IsRequired();
            entity.Property(x => x.IssuerNameSnapshot).HasMaxLength(255).IsRequired();
            entity.Property(x => x.CustomerNameSnapshot).HasMaxLength(255).IsRequired();
            entity.Property(x => x.BillingAddressSnapshot).HasMaxLength(1000).IsRequired();
            entity.Property(x => x.IdempotencyKey).HasMaxLength(128).IsRequired();
            entity.Property(x => x.RequestHash).HasMaxLength(64).IsRequired();
            entity.Property(x => x.CreatedBy).HasMaxLength(255).IsRequired();
            entity.Property(x => x.FinalizedBy).HasMaxLength(255);
            entity.Property(x => x.CancelledBy).HasMaxLength(255);
            entity.Property(x => x.CancellationReason).HasMaxLength(500);
            entity.Property(x => x.CorrectionReason).HasMaxLength(500);
            entity.Property(x => x.Version).IsConcurrencyToken();
            foreach (var property in new[] { nameof(CustomerStatement.OpeningBalance), nameof(CustomerStatement.DebitTotal),
                nameof(CustomerStatement.CreditTotal), nameof(CustomerStatement.UnappliedCash), nameof(CustomerStatement.ClosingBalance),
                nameof(CustomerStatement.NetCustomerPosition), nameof(CustomerStatement.AgingCurrent), nameof(CustomerStatement.Aging1To30),
                nameof(CustomerStatement.Aging31To60), nameof(CustomerStatement.Aging61To90), nameof(CustomerStatement.AgingOver90) })
                entity.Property<decimal>(property).HasPrecision(18, 2);
            entity.HasIndex(x => new { x.BusinessUnitId, x.IdempotencyKey }).IsUnique()
                .HasDatabaseName("UX_CustomerStatements_BU_Idempotency");
            entity.HasIndex(x => new { x.BusinessUnitId, x.StatementNumber }).IsUnique()
                .HasFilter("\"StatementNumber\" IS NOT NULL").HasDatabaseName("UX_CustomerStatements_BU_Number");
            entity.HasIndex(x => new { x.BusinessUnitId, x.CustomerId, x.CurrencyId, x.CutoffAt, x.Revision })
                .IsUnique().AreNullsDistinct(false).HasFilter("\"Status\" <> 'Cancelled'")
                .HasDatabaseName("UX_CustomerStatements_BU_Customer_Currency_Cutoff_Revision");
            entity.HasIndex(x => new { x.BusinessUnitId, x.SupersedesStatementId }).IsUnique()
                .HasFilter("\"SupersedesStatementId\" IS NOT NULL AND \"Status\" <> 'Cancelled'")
                .HasDatabaseName("UX_CustomerStatements_BU_Successor");
            entity.HasCheckConstraint("CK_CustomerStatements_Period", "\"PeriodStart\" <= \"CutoffAt\" AND \"CapturedOn\" >= \"CutoffAt\"");
            entity.HasCheckConstraint("CK_CustomerStatements_Reconciles", "\"ClosingBalance\" = round(\"OpeningBalance\" + \"DebitTotal\" - \"CreditTotal\", 2) AND \"NetCustomerPosition\" = \"ClosingBalance\"");
            entity.HasCheckConstraint("CK_CustomerStatements_Aging", "\"AgingCurrent\" >= 0 AND \"Aging1To30\" >= 0 AND \"Aging31To60\" >= 0 AND \"Aging61To90\" >= 0 AND \"AgingOver90\" >= 0");
            entity.HasOne<Customer>().WithMany().HasForeignKey(x => x.CustomerId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<Currency>().WithMany().HasForeignKey(x => x.CurrencyId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<CustomerStatement>().WithMany()
                .HasForeignKey(x => new { x.BusinessUnitId, x.SupersedesStatementId })
                .HasPrincipalKey(x => new { x.BusinessUnitId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<CustomerStatementLine>(entity =>
        {
            entity.ToTable("CustomerStatementLines");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.SourceType).HasMaxLength(40).IsRequired();
            entity.Property(x => x.SourceNumber).HasMaxLength(80).IsRequired();
            entity.Property(x => x.Description).HasMaxLength(500).IsRequired();
            entity.Property(x => x.AgingBucket).HasMaxLength(20).IsRequired();
            entity.Property(x => x.DebitAmount).HasPrecision(18, 2);
            entity.Property(x => x.CreditAmount).HasPrecision(18, 2);
            entity.Property(x => x.AppliedAmount).HasPrecision(18, 2);
            entity.Property(x => x.OutstandingAmount).HasPrecision(18, 2);
            entity.Property(x => x.RunningBalance).HasPrecision(18, 2);
            entity.HasIndex(x => new { x.BusinessUnitId, x.CustomerStatementId, x.Sequence }).IsUnique()
                .HasDatabaseName("UX_CustomerStatementLines_BU_Statement_Sequence");
            entity.HasCheckConstraint("CK_CustomerStatementLines_Money",
                "\"Sequence\" > 0 AND \"DebitAmount\" >= 0 AND \"CreditAmount\" >= 0 AND NOT (\"DebitAmount\" > 0 AND \"CreditAmount\" > 0) AND \"AppliedAmount\" >= 0 AND \"OutstandingAmount\" >= 0");
            entity.HasOne(x => x.Statement).WithMany(x => x.Lines)
                .HasForeignKey(x => new { x.BusinessUnitId, x.CustomerStatementId })
                .HasPrincipalKey(x => new { x.BusinessUnitId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<DunningPolicy>(entity =>
        {
            entity.ToTable("DunningPolicies");
            entity.HasKey(x => x.Id);
            entity.HasAlternateKey(x => new { x.BusinessUnitId, x.Id });
            entity.Property(x => x.Name).HasMaxLength(120).IsRequired();
            entity.Property(x => x.Status).HasMaxLength(20).IsRequired();
            entity.Property(x => x.JurisdictionCode).HasMaxLength(20).IsRequired();
            entity.Property(x => x.TimeZoneId).HasMaxLength(100).IsRequired();
            entity.Property(x => x.TemplateVersion).HasMaxLength(40).IsRequired();
            entity.Property(x => x.IdempotencyKey).HasMaxLength(128).IsRequired();
            entity.Property(x => x.RequestHash).HasMaxLength(64).IsRequired();
            entity.Property(x => x.MinimumOverdueAmount).HasPrecision(18, 2);
            entity.Property(x => x.CreatedBy).HasMaxLength(255).IsRequired();
            entity.Property(x => x.ApprovedBy).HasMaxLength(255);
            entity.Property(x => x.ActivatedBy).HasMaxLength(255);
            entity.Property(x => x.RetiredBy).HasMaxLength(255);
            entity.Property(x => x.Version).IsConcurrencyToken();
            entity.HasIndex(x => new { x.BusinessUnitId, x.PolicyVersion }).IsUnique()
                .HasDatabaseName("UX_DunningPolicies_BU_Version");
            entity.HasIndex(x => new { x.BusinessUnitId, x.IdempotencyKey }).IsUnique()
                .HasDatabaseName("UX_DunningPolicies_BU_Idempotency");
            entity.HasIndex(x => new { x.BusinessUnitId, x.Status }).IsUnique()
                .HasFilter("\"Status\" = 'Active'").HasDatabaseName("UX_DunningPolicies_BU_Active");
            entity.HasCheckConstraint("CK_DunningPolicies_Rules", "\"PolicyVersion\" > 0 AND \"GraceDays\" >= 0 AND \"CadenceDays\" > 0 AND \"MaximumStage\" BETWEEN 1 AND 9 AND \"MinimumOverdueAmount\" >= 0 AND \"QuietHoursStart\" BETWEEN 0 AND 23 AND \"QuietHoursEnd\" BETWEEN 0 AND 23");
        });

        modelBuilder.Entity<DunningPolicyStep>(entity =>
        {
            entity.ToTable("DunningPolicySteps");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.MinimumAmount).HasPrecision(18, 2);
            entity.Property(x => x.Channel).HasMaxLength(20).IsRequired();
            entity.Property(x => x.TemplateVersion).HasMaxLength(40).IsRequired();
            entity.Property(x => x.EscalationRole).HasMaxLength(100).IsRequired();
            entity.HasIndex(x => new { x.BusinessUnitId, x.DunningPolicyId, x.Stage }).IsUnique()
                .HasDatabaseName("UX_DunningPolicySteps_BU_Policy_Stage");
            entity.HasCheckConstraint("CK_DunningPolicySteps_Rules", "\"Stage\" > 0 AND \"MinimumDaysPastDue\" >= 0 AND \"MinimumAmount\" >= 0 AND \"WaitDaysAfterPriorStage\" >= 0 AND \"MaximumAttempts\" BETWEEN 1 AND 20");
            entity.HasOne(x => x.Policy).WithMany(x => x.Steps)
                .HasForeignKey(x => new { x.BusinessUnitId, x.DunningPolicyId })
                .HasPrincipalKey(x => new { x.BusinessUnitId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<CustomerCollectionProfile>(entity =>
        {
            entity.ToTable("CustomerCollectionProfiles");
            entity.HasKey(x => x.Id);
            entity.HasAlternateKey(x => new { x.BusinessUnitId, x.Id });
            entity.Property(x => x.Locale).HasMaxLength(20).IsRequired();
            entity.Property(x => x.TimeZoneId).HasMaxLength(100).IsRequired();
            entity.Property(x => x.Collector).HasMaxLength(255);
            entity.Property(x => x.HoldReason).HasMaxLength(500);
            entity.Property(x => x.HoldEvidenceReference).HasMaxLength(500);
            entity.Property(x => x.CreatedBy).HasMaxLength(255).IsRequired();
            entity.Property(x => x.ModifiedBy).HasMaxLength(255);
            entity.Property(x => x.Version).IsConcurrencyToken();
            entity.HasIndex(x => new { x.BusinessUnitId, x.CustomerId, x.CurrencyId }).IsUnique()
                .AreNullsDistinct(false)
                .HasDatabaseName("UX_CustomerCollectionProfiles_BU_Customer_Currency");
            entity.HasOne<Customer>().WithMany().HasForeignKey(x => x.CustomerId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<Currency>().WithMany().HasForeignKey(x => x.CurrencyId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(x => x.Policy).WithMany()
                .HasForeignKey(x => new { x.BusinessUnitId, x.DunningPolicyId })
                .HasPrincipalKey(x => new { x.BusinessUnitId, x.Id }).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(x => x.Contact).WithMany()
                .HasForeignKey(x => new { x.BusinessUnitId, x.FinanceCommunicationContactId })
                .HasPrincipalKey(x => new { x.BusinessUnitId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<CollectionControl>(entity =>
        {
            entity.ToTable("CollectionControls");
            entity.HasKey(x => x.Id);
            entity.HasAlternateKey(x => new { x.BusinessUnitId, x.Id });
            entity.Property(x => x.ControlType).HasMaxLength(40).IsRequired();
            entity.Property(x => x.Status).HasMaxLength(20).IsRequired();
            entity.Property(x => x.DisputedAmount).HasPrecision(18, 2);
            entity.Property(x => x.ReasonCode).HasMaxLength(50).IsRequired();
            entity.Property(x => x.Reason).HasMaxLength(500).IsRequired();
            entity.Property(x => x.EvidenceReference).HasMaxLength(500).IsRequired();
            entity.Property(x => x.IdempotencyKey).HasMaxLength(128).IsRequired();
            entity.Property(x => x.RequestHash).HasMaxLength(64).IsRequired();
            entity.Property(x => x.CreatedBy).HasMaxLength(255).IsRequired();
            entity.Property(x => x.ResolvedBy).HasMaxLength(255);
            entity.Property(x => x.ResolutionReason).HasMaxLength(500);
            entity.Property(x => x.ResolutionEvidenceReference).HasMaxLength(500);
            entity.Property(x => x.Version).IsConcurrencyToken();
            entity.HasIndex(x => new { x.BusinessUnitId, x.CustomerId, x.Status, x.ControlType })
                .HasDatabaseName("IX_CollectionControls_BU_Customer_Status_Type");
            entity.HasIndex(x => new { x.BusinessUnitId, x.IdempotencyKey }).IsUnique()
                .HasDatabaseName("UX_CollectionControls_BU_Idempotency");
            entity.HasCheckConstraint("CK_CollectionControls_Dates", "\"ReviewOn\" IS NULL OR \"ReviewOn\" >= \"EffectiveFrom\"");
            entity.HasCheckConstraint("CK_CollectionControls_Dispute", "(\"ControlType\" = 'Dispute' AND \"ReceivableDocumentId\" IS NOT NULL AND \"DisputedAmount\" > 0) OR (\"ControlType\" IN ('CommunicationRestriction','LegalHold') AND \"DisputedAmount\" IS NULL)");
            entity.HasOne<Customer>().WithMany().HasForeignKey(x => x.CustomerId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<Currency>().WithMany().HasForeignKey(x => x.CurrencyId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<ReceivableDocument>().WithMany()
                .HasForeignKey(x => new { x.BusinessUnitId, x.ReceivableDocumentId })
                .HasPrincipalKey(x => new { x.BusinessUnitId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<DunningCase>(entity =>
        {
            entity.ToTable("DunningCases");
            entity.HasKey(x => x.Id);
            entity.HasAlternateKey(x => new { x.BusinessUnitId, x.Id });
            entity.Property(x => x.Status).HasMaxLength(20).IsRequired();
            entity.Property(x => x.ExposureAtOpen).HasPrecision(18, 2);
            entity.Property(x => x.CurrentExposure).HasPrecision(18, 2);
            entity.Property(x => x.AssignedTo).HasMaxLength(255);
            entity.Property(x => x.PromiseAmount).HasPrecision(18, 2);
            entity.Property(x => x.IdempotencyKey).HasMaxLength(128).IsRequired();
            entity.Property(x => x.RequestHash).HasMaxLength(64).IsRequired();
            entity.Property(x => x.CreatedBy).HasMaxLength(255).IsRequired();
            entity.Property(x => x.UpdatedBy).HasMaxLength(255);
            entity.Property(x => x.StatusReason).HasMaxLength(500);
            entity.Property(x => x.EvidenceReference).HasMaxLength(500);
            entity.Property(x => x.Version).IsConcurrencyToken();
            entity.HasIndex(x => new { x.BusinessUnitId, x.IdempotencyKey }).IsUnique()
                .HasDatabaseName("UX_DunningCases_BU_Idempotency");
            entity.HasIndex(x => new { x.BusinessUnitId, x.CustomerId, x.CurrencyId }).IsUnique()
                .AreNullsDistinct(false).HasFilter("\"Status\" IN ('Open','Held','Disputed')")
                .HasDatabaseName("UX_DunningCases_BU_ActiveCustomerCurrency");
            entity.HasCheckConstraint("CK_DunningCases_Exposure", "\"CurrentStage\" >= 0 AND \"ExposureAtOpen\" > 0 AND \"CurrentExposure\" >= 0");
            entity.HasOne<Customer>().WithMany().HasForeignKey(x => x.CustomerId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<Currency>().WithMany().HasForeignKey(x => x.CurrencyId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(x => x.Policy).WithMany()
                .HasForeignKey(x => new { x.BusinessUnitId, x.DunningPolicyId })
                .HasPrincipalKey(x => new { x.BusinessUnitId, x.Id }).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(x => x.Statement).WithMany()
                .HasForeignKey(x => new { x.BusinessUnitId, x.CustomerStatementId })
                .HasPrincipalKey(x => new { x.BusinessUnitId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<PromiseToPay>(entity =>
        {
            entity.ToTable("PromisesToPay");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Amount).HasPrecision(18, 2);
            entity.Property(x => x.Status).HasMaxLength(20).IsRequired();
            entity.Property(x => x.EvidenceReference).HasMaxLength(500).IsRequired();
            entity.Property(x => x.CreatedBy).HasMaxLength(255).IsRequired();
            entity.Property(x => x.ClosedBy).HasMaxLength(255);
            entity.Property(x => x.ClosureEvidenceReference).HasMaxLength(500);
            entity.Property(x => x.MatchedAmount).HasPrecision(18, 2);
            entity.Property(x => x.Version).IsConcurrencyToken();
            entity.HasCheckConstraint("CK_PromisesToPay_Rules", "\"Amount\" > 0 AND \"DueOn\" >= \"PromisedOn\" AND ((\"Status\" = 'Kept' AND \"MatchedPaymentId\" IS NOT NULL AND \"MatchedAmount\" >= \"Amount\") OR (\"Status\" <> 'Kept' AND \"MatchedPaymentId\" IS NULL AND \"MatchedAmount\" IS NULL))");
            entity.HasIndex(x => new { x.BusinessUnitId, x.IdempotencyKey }).IsUnique()
                .HasDatabaseName("UX_PromisesToPay_BU_Idempotency");
            entity.HasIndex(x => new { x.BusinessUnitId, x.MatchedPaymentId }).IsUnique()
                .HasFilter("\"MatchedPaymentId\" IS NOT NULL")
                .HasDatabaseName("UX_PromisesToPay_BU_MatchedPayment");
            entity.HasOne(x => x.Case).WithMany(x => x.Promises)
                .HasForeignKey(x => new { x.BusinessUnitId, x.DunningCaseId })
                .HasPrincipalKey(x => new { x.BusinessUnitId, x.Id }).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(x => x.MatchedPayment).WithMany()
                .HasForeignKey(x => new { x.BusinessUnitId, x.MatchedPaymentId })
                .HasPrincipalKey(x => new { x.BusinessUnitId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<DunningRun>(entity =>
        {
            entity.ToTable("DunningRuns");
            entity.HasKey(x => x.Id);
            entity.HasAlternateKey(x => new { x.BusinessUnitId, x.Id });
            entity.Property(x => x.Status).HasMaxLength(20).IsRequired();
            entity.Property(x => x.IdempotencyKey).HasMaxLength(128).IsRequired();
            entity.Property(x => x.RequestHash).HasMaxLength(64).IsRequired();
            entity.Property(x => x.LeaseOwner).HasMaxLength(200);
            entity.Property(x => x.CreatedBy).HasMaxLength(255).IsRequired();
            entity.Property(x => x.CompletionEvidenceReference).HasMaxLength(500);
            entity.Property(x => x.FailureReason).HasMaxLength(500);
            entity.Property(x => x.FailureEvidenceReference).HasMaxLength(500);
            entity.Property(x => x.Version).IsConcurrencyToken();
            entity.HasIndex(x => new { x.BusinessUnitId, x.IdempotencyKey }).IsUnique()
                .HasDatabaseName("UX_DunningRuns_BU_Idempotency");
            entity.HasCheckConstraint("CK_DunningRuns_Counts", "\"CandidateCount\" >= 0 AND \"NoticeCount\" >= 0 AND \"SuppressedCount\" >= 0 AND \"FailedCount\" >= 0 AND ((\"LeaseOwner\" IS NULL) = (\"LeaseToken\" IS NULL)) AND ((\"LeaseOwner\" IS NULL) = (\"LeaseUntil\" IS NULL))");
            entity.HasOne(x => x.Policy).WithMany()
                .HasForeignKey(x => new { x.BusinessUnitId, x.DunningPolicyId })
                .HasPrincipalKey(x => new { x.BusinessUnitId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<DunningRunDecision>(entity =>
        {
            entity.ToTable("DunningRunDecisions");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Outcome).HasMaxLength(30).IsRequired();
            entity.Property(x => x.ReasonCode).HasMaxLength(80).IsRequired();
            entity.Property(x => x.EvidenceHash).HasMaxLength(64).IsRequired();
            entity.HasCheckConstraint("CK_DunningRunDecisions_Evidence",
                "\"Outcome\" IN ('NoticeCreated','Suppressed','Skipped','Failed') AND length(\"EvidenceHash\") = 64");
            entity.HasIndex(x => new { x.BusinessUnitId, x.DunningRunId, x.CustomerCollectionProfileId })
                .IsUnique().HasFilter("\"CustomerCollectionProfileId\" IS NOT NULL")
                .HasDatabaseName("UX_DunningRunDecisions_BU_Run_Profile");
            entity.HasOne(x => x.Run).WithMany(x => x.Decisions)
                .HasForeignKey(x => new { x.BusinessUnitId, x.DunningRunId })
                .HasPrincipalKey(x => new { x.BusinessUnitId, x.Id }).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(x => x.Profile).WithMany()
                .HasForeignKey(x => new { x.BusinessUnitId, x.CustomerCollectionProfileId })
                .HasPrincipalKey(x => new { x.BusinessUnitId, x.Id }).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<Customer>().WithMany().HasForeignKey(x => x.CustomerId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<Currency>().WithMany().HasForeignKey(x => x.CurrencyId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(x => x.Statement).WithMany()
                .HasForeignKey(x => new { x.BusinessUnitId, x.CustomerStatementId })
                .HasPrincipalKey(x => new { x.BusinessUnitId, x.Id }).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(x => x.Case).WithMany()
                .HasForeignKey(x => new { x.BusinessUnitId, x.DunningCaseId })
                .HasPrincipalKey(x => new { x.BusinessUnitId, x.Id }).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(x => x.Notice).WithMany()
                .HasForeignKey(x => new { x.BusinessUnitId, x.DunningNoticeId })
                .HasPrincipalKey(x => new { x.BusinessUnitId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<DunningNotice>(entity =>
        {
            entity.ToTable("DunningNotices");
            entity.HasKey(x => x.Id);
            entity.HasAlternateKey(x => new { x.BusinessUnitId, x.Id });
            entity.Property(x => x.Status).HasMaxLength(20).IsRequired();
            entity.Property(x => x.SnapshotExposure).HasPrecision(18, 2);
            entity.Property(x => x.SnapshotHash).HasMaxLength(64).IsRequired();
            entity.Property(x => x.TemplateVersion).HasMaxLength(40).IsRequired();
            entity.Property(x => x.Locale).HasMaxLength(20).IsRequired();
            entity.Property(x => x.Subject).HasMaxLength(255).IsRequired();
            entity.Property(x => x.ArtifactMediaType).HasMaxLength(100).IsRequired();
            entity.Property(x => x.ArtifactContent).HasColumnType("text").IsRequired();
            entity.Property(x => x.ArtifactHash).HasMaxLength(64).IsRequired();
            entity.Property(x => x.IdempotencyKey).HasMaxLength(128).IsRequired();
            entity.Property(x => x.RequestHash).HasMaxLength(64).IsRequired();
            entity.Property(x => x.CreatedBy).HasMaxLength(255).IsRequired();
            entity.Property(x => x.ApprovedBy).HasMaxLength(255);
            entity.Property(x => x.ReleasedBy).HasMaxLength(255);
            entity.Property(x => x.DeliveryUpdatedBy).HasMaxLength(255);
            entity.Property(x => x.ProviderReference).HasMaxLength(100);
            entity.Property(x => x.FailureCode).HasMaxLength(100);
            entity.Property(x => x.SuppressionReason).HasMaxLength(500);
            entity.Property(x => x.CancelledBy).HasMaxLength(255);
            entity.Property(x => x.CancellationReason).HasMaxLength(500);
            entity.Property(x => x.CancellationEvidenceReference).HasMaxLength(500);
            entity.Property(x => x.Version).IsConcurrencyToken();
            entity.HasIndex(x => new { x.BusinessUnitId, x.IdempotencyKey }).IsUnique()
                .HasDatabaseName("UX_DunningNotices_BU_Idempotency");
            entity.HasIndex(x => new { x.BusinessUnitId, x.DunningCaseId, x.Stage, x.SnapshotHash }).IsUnique()
                .HasDatabaseName("UX_DunningNotices_BU_Case_Stage_Hash");
            entity.HasCheckConstraint("CK_DunningNotices_Rules", "\"Stage\" > 0 AND \"SnapshotExposure\" > 0");
            entity.HasOne(x => x.Case).WithMany(x => x.Notices)
                .HasForeignKey(x => new { x.BusinessUnitId, x.DunningCaseId })
                .HasPrincipalKey(x => new { x.BusinessUnitId, x.Id }).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(x => x.Statement).WithMany()
                .HasForeignKey(x => new { x.BusinessUnitId, x.CustomerStatementId })
                .HasPrincipalKey(x => new { x.BusinessUnitId, x.Id }).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(x => x.Contact).WithMany()
                .HasForeignKey(x => new { x.BusinessUnitId, x.FinanceCommunicationContactId })
                .HasPrincipalKey(x => new { x.BusinessUnitId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<DunningDeliveryAttempt>(entity =>
        {
            entity.ToTable("DunningDeliveryAttempts");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Status).HasMaxLength(30).IsRequired();
            entity.Property(x => x.MaskedDestination).HasMaxLength(120).IsRequired();
            entity.Property(x => x.ArtifactHash).HasMaxLength(64).IsRequired();
            entity.Property(x => x.TemplateVersion).HasMaxLength(40).IsRequired();
            entity.Property(x => x.ProviderReference).HasMaxLength(100);
            entity.Property(x => x.FailureCode).HasMaxLength(100);
            entity.Property(x => x.SignedEvidenceReference).HasMaxLength(500).IsRequired();
            entity.Property(x => x.ProviderSignature).HasMaxLength(64);
            entity.Property(x => x.RecordedBy).HasMaxLength(255).IsRequired();
            entity.HasIndex(x => new { x.BusinessUnitId, x.ProviderEventId }).IsUnique()
                .HasDatabaseName("UX_DunningDeliveryAttempts_BU_ProviderEvent");
            entity.HasIndex(x => new { x.BusinessUnitId, x.DunningNoticeId, x.AttemptNumber }).IsUnique()
                .HasDatabaseName("UX_DunningDeliveryAttempts_BU_Notice_Attempt");
            entity.HasCheckConstraint("CK_DunningDeliveryAttempts_Number", "\"AttemptNumber\" > 0");
            entity.HasOne(x => x.Notice).WithMany(x => x.DeliveryAttempts)
                .HasForeignKey(x => new { x.BusinessUnitId, x.DunningNoticeId })
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

        modelBuilder.Entity<FinanceOutboxMessage>(entity =>
        {
            entity.ToTable("FinanceOutboxMessages");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.AggregateType).HasMaxLength(50).IsRequired();
            entity.Property(x => x.EventType).HasMaxLength(120).IsRequired();
            entity.Property(x => x.Payload).HasColumnType("jsonb").IsRequired();
            entity.Property(x => x.SchemaVersion).HasDefaultValue(1);
            entity.Property(x => x.LeaseOwner).HasMaxLength(200);
            entity.Property(x => x.LastError).HasMaxLength(2000);
            entity.HasIndex(x => x.EventId).IsUnique().HasDatabaseName("UX_FinanceOutbox_EventId");
            entity.HasIndex(x => new { x.BusinessUnitId, x.AggregateType, x.AggregateId, x.AggregateVersion, x.EventType })
                .IsUnique().HasDatabaseName("UX_FinanceOutbox_AggregateVersionEvent");
            entity.HasIndex(x => new { x.AvailableOn, x.LeaseUntil, x.OccurredOn, x.Id })
                .HasFilter("\"ProcessedOn\" IS NULL AND \"DeadLetteredOn\" IS NULL")
                .HasDatabaseName("IX_FinanceOutbox_Ready");
            entity.HasCheckConstraint("CK_FinanceOutbox_State",
                "\"AttemptCount\" >= 0 AND \"SchemaVersion\" > 0 AND \"AggregateId\" > 0 AND \"AggregateVersion\" >= 0 AND trim(\"AggregateType\") <> '' AND trim(\"EventType\") <> '' AND ((\"LeaseOwner\" IS NULL) = (\"LeaseUntil\" IS NULL)) AND ((\"LeaseToken\" IS NULL) = (\"LeaseUntil\" IS NULL)) AND NOT (\"ProcessedOn\" IS NOT NULL AND \"DeadLetteredOn\" IS NOT NULL) AND ((\"ProcessedOn\" IS NULL AND \"DeadLetteredOn\" IS NULL) OR (\"LeaseOwner\" IS NULL AND \"LeaseUntil\" IS NULL AND \"LeaseToken\" IS NULL))");
            entity.HasOne<BusinessUnit>().WithMany().HasForeignKey(x => x.BusinessUnitId)
                .OnDelete(DeleteBehavior.Restrict);
        });
    }
}
