using ERP_RFQ_Automation.GeneralLedger;
using ERP_RFQ_Automation.Models;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.BankReconciliation;

public static class BankReconciliationModelBuilderExtensions
{
    public static void ConfigureBankReconciliation(this ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<BankMatchingRule>(e =>
        {
            e.ToTable("BankMatchingRules"); e.HasKey(x => x.Id);
            e.HasAlternateKey(x => new { x.BusinessUnitId, x.Id });
            e.Property(x => x.Code).HasMaxLength(80).IsRequired(); e.Property(x => x.Name).HasMaxLength(160).IsRequired();
            e.Property(x => x.EvaluatorType).HasMaxLength(40).IsRequired(); e.Property(x => x.ReferenceMode).HasMaxLength(30).IsRequired();
            e.Property(x => x.AmountTolerance).HasPrecision(18, 2); e.Property(x => x.DefinitionHash).HasMaxLength(64).IsRequired();
            e.Property(x => x.Status).HasMaxLength(20).IsRequired(); e.Property(x => x.RecordVersion).IsConcurrencyToken();
            e.Property(x => x.IdempotencyKey).HasMaxLength(128).IsRequired(); e.Property(x => x.RequestHash).HasMaxLength(64).IsRequired();
            e.Property(x => x.CreatedBy).HasMaxLength(255).IsRequired();
            foreach (var p in new[] { nameof(BankMatchingRule.ApprovedBy), nameof(BankMatchingRule.ActivatedBy), nameof(BankMatchingRule.RetiredBy) }) e.Property(p).HasMaxLength(255);
            e.Property(x => x.LifecycleReason).HasMaxLength(500); e.Property(x => x.EvidenceReference).HasMaxLength(500);
            e.HasIndex(x => new { x.BusinessUnitId, x.Code, x.RuleVersion }).IsUnique().HasDatabaseName("UX_BankMatchingRules_BU_Code_Version");
            e.HasIndex(x => new { x.BusinessUnitId, x.IdempotencyKey }).IsUnique().HasDatabaseName("UX_BankMatchingRules_BU_Idempotency");
            e.HasIndex(x => new { x.BusinessUnitId, x.Code, x.BankAccountId }).IsUnique()
                .HasFilter("\"Status\" = 'Active'").HasDatabaseName("UX_BankMatchingRules_BU_ActiveScope");
            e.HasCheckConstraint("CK_BankMatchingRules_Definition", "\"RuleVersion\" > 0 AND \"RecordVersion\" > 0 AND \"Priority\" BETWEEN 1 AND 10000 AND \"AmountTolerance\" >= 0 AND \"BookingDateToleranceDays\" BETWEEN 0 AND 31 AND \"RequireUniquePair\" = TRUE");
            e.HasCheckConstraint("CK_BankMatchingRules_Type", "\"EvaluatorType\" = 'ExactAmountDirection' AND \"ReferenceMode\" IN ('Ignore','NormalizedExact') AND \"Status\" IN ('Draft','Approved','Active','Retired')");
            e.HasOne(x => x.BankAccount).WithMany().HasForeignKey(x => new { x.BusinessUnitId, x.BankAccountId })
                .HasPrincipalKey(x => new { x.BusinessUnitId, x.Id }).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.SupersedesRule).WithMany().HasForeignKey(x => new { x.BusinessUnitId, x.SupersedesRuleId })
                .HasPrincipalKey(x => new { x.BusinessUnitId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        });
        modelBuilder.Entity<ReconciliationRunRule>(e =>
        {
            e.ToTable("ReconciliationRunRules"); e.HasKey(x => x.Id);
            e.Property(x => x.DefinitionHash).HasMaxLength(64).IsRequired();
            e.HasIndex(x => new { x.BusinessUnitId, x.ReconciliationRunId, x.BankMatchingRuleId }).IsUnique().HasDatabaseName("UX_ReconciliationRunRules_Evidence");
            e.HasIndex(x => new { x.BusinessUnitId, x.ReconciliationRunId, x.EvaluationOrder }).IsUnique().HasDatabaseName("UX_ReconciliationRunRules_Order");
            e.HasCheckConstraint("CK_ReconciliationRunRules_Order", "\"EvaluationOrder\" > 0");
            e.HasOne(x => x.Run).WithMany(x => x.Rules).HasForeignKey(x => new { x.BusinessUnitId, x.ReconciliationRunId })
                .HasPrincipalKey(x => new { x.BusinessUnitId, x.Id }).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.Rule).WithMany().HasForeignKey(x => new { x.BusinessUnitId, x.BankMatchingRuleId })
                .HasPrincipalKey(x => new { x.BusinessUnitId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        });
        modelBuilder.Entity<BankAdjustment>(e =>
        {
            e.ToTable("BankAdjustments"); e.HasKey(x => x.Id); e.HasAlternateKey(x => new { x.BusinessUnitId, x.Id });
            e.Property(x => x.AdjustmentType).HasMaxLength(50).IsRequired(); e.Property(x => x.Description).HasMaxLength(500).IsRequired();
            e.Property(x => x.Amount).HasPrecision(18, 2); e.Property(x => x.EvidenceReference).HasMaxLength(500).IsRequired();
            e.Property(x => x.Status).HasMaxLength(20).IsRequired(); e.Property(x => x.IdempotencyKey).HasMaxLength(128).IsRequired();
            e.Property(x => x.RequestHash).HasMaxLength(64).IsRequired(); e.Property(x => x.Version).IsConcurrencyToken();
            foreach (var p in new[] { nameof(BankAdjustment.PreparedBy), nameof(BankAdjustment.SubmittedBy), nameof(BankAdjustment.ApprovedBy), nameof(BankAdjustment.RejectedBy), nameof(BankAdjustment.CancelledBy), nameof(BankAdjustment.ReversedBy) }) e.Property(p).HasMaxLength(255);
            foreach (var p in new[] { nameof(BankAdjustment.RejectionReason), nameof(BankAdjustment.CancellationReason), nameof(BankAdjustment.ReversalReason), nameof(BankAdjustment.ReversalEvidenceReference) }) e.Property(p).HasMaxLength(500);
            e.HasIndex(x => new { x.BusinessUnitId, x.IdempotencyKey }).IsUnique().HasDatabaseName("UX_BankAdjustments_BU_Idempotency");
            e.HasIndex(x => new { x.BusinessUnitId, x.JournalEntryId }).IsUnique().HasFilter("\"JournalEntryId\" IS NOT NULL").HasDatabaseName("UX_BankAdjustments_BU_Journal");
            e.HasCheckConstraint("CK_BankAdjustments_State", "\"Amount\" > 0 AND \"Version\" > 0 AND \"Status\" IN ('Draft','InReview','Posted','Rejected','Cancelled','Reversed')");
            e.HasOne(x => x.BankAccount).WithMany().HasForeignKey(x => new { x.BusinessUnitId, x.BankAccountId })
                .HasPrincipalKey(x => new { x.BusinessUnitId, x.Id }).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.BankStatementLine).WithMany().HasForeignKey(x => new { x.BusinessUnitId, x.BankStatementLineId, x.BankAccountId })
                .HasPrincipalKey(x => new { x.BusinessUnitId, x.Id, x.BankAccountId }).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<AccountingPeriod>().WithMany().HasForeignKey(x => new { x.BusinessUnitId, x.AccountingPeriodId })
                .HasPrincipalKey(x => new { x.BusinessUnitId, x.Id }).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<JournalEntry>().WithMany().HasForeignKey(x => new { x.BusinessUnitId, x.JournalEntryId })
                .HasPrincipalKey(x => new { x.BusinessUnitId, x.Id }).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<JournalEntry>().WithMany().HasForeignKey(x => new { x.BusinessUnitId, x.ReversalJournalEntryId })
                .HasPrincipalKey(x => new { x.BusinessUnitId, x.Id }).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<JournalEntryLine>().WithMany().HasForeignKey(x => new { x.BusinessUnitId, x.BankJournalEntryLineId })
                .HasPrincipalKey(x => new { x.BusinessUnitId, x.Id }).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<JournalEntryLine>().WithMany().HasForeignKey(x => new { x.BusinessUnitId, x.ReversalBankJournalEntryLineId })
                .HasPrincipalKey(x => new { x.BusinessUnitId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        });
        modelBuilder.Entity<BankAdjustmentDistribution>(e =>
        {
            e.ToTable("BankAdjustmentDistributions"); e.HasKey(x => x.Id);
            e.Property(x => x.Amount).HasPrecision(18, 2); e.Property(x => x.Description).HasMaxLength(500).IsRequired();
            e.HasIndex(x => new { x.BusinessUnitId, x.BankAdjustmentId, x.Sequence }).IsUnique().HasDatabaseName("UX_BankAdjustmentDistributions_Order");
            e.HasCheckConstraint("CK_BankAdjustmentDistributions_Amount", "\"Sequence\" > 0 AND \"Amount\" > 0");
            e.HasOne(x => x.Adjustment).WithMany(x => x.Distributions)
                .HasForeignKey(x => new { x.BusinessUnitId, x.BankAdjustmentId })
                .HasPrincipalKey(x => new { x.BusinessUnitId, x.Id }).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<LedgerAccount>().WithMany().HasForeignKey(x => new { x.BusinessUnitId, x.LedgerAccountId })
                .HasPrincipalKey(x => new { x.BusinessUnitId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        });
        modelBuilder.Entity<BankAccount>(e =>
        {
            e.ToTable("BankAccounts"); e.HasKey(x => x.Id); e.HasAlternateKey(x => new { x.BusinessUnitId, x.Id });
            e.HasAlternateKey(x => new { x.BusinessUnitId, x.Id, x.CurrencyId });
            e.Property(x => x.Name).HasMaxLength(160).IsRequired(); e.Property(x => x.InstitutionName).HasMaxLength(160).IsRequired();
            e.Property(x => x.MaskedAccountNumber).HasMaxLength(64).IsRequired(); e.Property(x => x.AccountFingerprint).HasMaxLength(64).IsRequired();
            e.Property(x => x.Status).HasMaxLength(20).IsRequired(); e.Property(x => x.IdempotencyKey).HasMaxLength(128).IsRequired();
            e.Property(x => x.RequestHash).HasMaxLength(64).IsRequired(); e.Property(x => x.CreatedBy).HasMaxLength(255).IsRequired();
            e.Property(x => x.StatusChangedBy).HasMaxLength(255); e.Property(x => x.StatusReason).HasMaxLength(500); e.Property(x => x.Version).IsConcurrencyToken();
            e.HasIndex(x => new { x.BusinessUnitId, x.AccountFingerprint }).IsUnique().HasDatabaseName("UX_BankAccounts_BU_Fingerprint");
            e.HasIndex(x => new { x.BusinessUnitId, x.IdempotencyKey }).IsUnique().HasDatabaseName("UX_BankAccounts_BU_Idempotency");
            e.HasCheckConstraint("CK_BankAccounts_Status", "\"Status\" IN ('Active','Suspended','Closed') AND \"Version\" > 0");
            e.HasOne<Currency>().WithMany().HasForeignKey(x => new { x.BusinessUnitId, Id = x.CurrencyId })
                .HasPrincipalKey(x => new { x.BusinessUnitId, x.Id }).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<LedgerAccount>().WithMany().HasForeignKey(x => new { x.BusinessUnitId, x.LedgerAccountId })
                .HasPrincipalKey(x => new { x.BusinessUnitId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        });
        modelBuilder.Entity<BankStatementImport>(e =>
        {
            e.ToTable("BankStatementImports"); e.HasKey(x => x.Id); e.HasAlternateKey(x => new { x.BusinessUnitId, x.Id });
            e.HasAlternateKey(x => new { x.BusinessUnitId, x.Id, x.BankAccountId });
            foreach (var name in new[] { nameof(BankStatementImport.SourceHash), nameof(BankStatementImport.RequestHash) }) e.Property(name).HasMaxLength(64).IsRequired();
            e.Property(x => x.SourceType).HasMaxLength(30).IsRequired(); e.Property(x => x.OriginalFileName).HasMaxLength(255).IsRequired();
            e.Property(x => x.RawObjectReference).HasMaxLength(500).IsRequired(); e.Property(x => x.ParserVersion).HasMaxLength(50).IsRequired();
            e.Property(x => x.RawPayload).HasColumnType("bytea");
            e.Property(x => x.Status).HasMaxLength(20).IsRequired(); e.Property(x => x.IdempotencyKey).HasMaxLength(128).IsRequired(); e.Property(x => x.ImportedBy).HasMaxLength(255).IsRequired();
            e.HasIndex(x => new { x.BusinessUnitId, x.BankAccountId, x.SourceHash }).IsUnique().HasDatabaseName("UX_BankImports_BU_Account_SourceHash");
            e.HasIndex(x => new { x.BusinessUnitId, x.IdempotencyKey }).IsUnique().HasDatabaseName("UX_BankImports_BU_Idempotency");
            e.HasCheckConstraint("CK_BankStatementImports_Status", "\"Status\" IN ('Validated','Rejected')");
            e.HasOne<BankAccount>().WithMany().HasForeignKey(x => new { x.BusinessUnitId, x.BankAccountId }).HasPrincipalKey(x => new { x.BusinessUnitId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        });
        modelBuilder.Entity<BankStatement>(e =>
        {
            e.ToTable("BankStatements"); e.HasKey(x => x.Id); e.HasAlternateKey(x => new { x.BusinessUnitId, x.Id });
            e.HasAlternateKey(x => new { x.BusinessUnitId, x.Id, x.BankAccountId });
            e.Property(x => x.StatementReference).HasMaxLength(200).IsRequired(); e.Property(x => x.ContentHash).HasMaxLength(64).IsRequired();
            e.Property(x => x.OpeningBalance).HasPrecision(18, 2); e.Property(x => x.ClosingBalance).HasPrecision(18, 2); e.Property(x => x.CalculatedClosingBalance).HasPrecision(18, 2);
            e.HasIndex(x => new { x.BusinessUnitId, x.BankAccountId, x.StatementReference }).IsUnique().HasDatabaseName("UX_BankStatements_BU_Account_Reference");
            e.HasCheckConstraint("CK_BankStatements_Period", "\"PeriodStart\" <= \"PeriodEnd\" AND \"Version\" > 0");
            e.HasCheckConstraint("CK_BankStatements_Balance", "\"CalculatedClosingBalance\" = \"ClosingBalance\"");
            e.HasOne<BankStatementImport>().WithOne(x => x.Statement)
                .HasForeignKey<BankStatement>(x => new { x.BusinessUnitId, x.BankStatementImportId, x.BankAccountId })
                .HasPrincipalKey<BankStatementImport>(x => new { x.BusinessUnitId, x.Id, x.BankAccountId }).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<BankAccount>().WithMany()
                .HasForeignKey(x => new { x.BusinessUnitId, x.BankAccountId, x.CurrencyId })
                .HasPrincipalKey(x => new { x.BusinessUnitId, x.Id, x.CurrencyId }).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<Currency>().WithMany().HasForeignKey(x => new { x.BusinessUnitId, Id = x.CurrencyId })
                .HasPrincipalKey(x => new { x.BusinessUnitId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        });
        modelBuilder.Entity<BankStatementLine>(e =>
        {
            e.ToTable("BankStatementLines"); e.HasKey(x => x.Id); e.HasAlternateKey(x => new { x.BusinessUnitId, x.Id });
            e.HasAlternateKey(x => new { x.BusinessUnitId, x.Id, x.BankAccountId });
            e.Property(x => x.SignedAmount).HasPrecision(18, 2); e.Property(x => x.Direction).HasMaxLength(10).IsRequired(); e.Property(x => x.OriginalAmountText).HasMaxLength(80).IsRequired();
            e.Property(x => x.ExternalTransactionId).HasMaxLength(200); e.Property(x => x.BankReference).HasMaxLength(200); e.Property(x => x.TransactionCode).HasMaxLength(80);
            e.Property(x => x.Counterparty).HasMaxLength(255); e.Property(x => x.RemittanceText).HasMaxLength(1000); e.Property(x => x.NormalizedReference).HasMaxLength(500); e.Property(x => x.LineFingerprint).HasMaxLength(64).IsRequired();
            e.HasIndex(x => new { x.BusinessUnitId, x.BankStatementId, x.SourceOrdinal }).IsUnique().HasDatabaseName("UX_BankLines_BU_Statement_Ordinal");
            e.HasIndex(x => new { x.BusinessUnitId, x.BankAccountId, x.LineFingerprint }).IsUnique().HasDatabaseName("UX_BankLines_BU_Account_Fingerprint");
            e.HasIndex(x => new { x.BusinessUnitId, x.BankAccountId, x.ExternalTransactionId }).IsUnique().HasFilter("\"ExternalTransactionId\" IS NOT NULL").HasDatabaseName("UX_BankLines_BU_Account_ExternalId");
            e.HasCheckConstraint("CK_BankStatementLines_Amount", "\"SourceOrdinal\" > 0 AND \"SignedAmount\" <> 0 AND ((\"SignedAmount\" > 0 AND \"Direction\" = 'Credit') OR (\"SignedAmount\" < 0 AND \"Direction\" = 'Debit'))");
            e.HasCheckConstraint("CK_BankStatementLines_Dates", "\"BookingDate\" <= \"ValueDate\"");
            e.HasOne(x => x.Statement).WithMany(x => x.Lines)
                .HasForeignKey(x => new { x.BusinessUnitId, x.BankStatementId, x.BankAccountId })
                .HasPrincipalKey(x => new { x.BusinessUnitId, x.Id, x.BankAccountId }).OnDelete(DeleteBehavior.Restrict);
        });
        modelBuilder.Entity<ReconciliationRun>(e =>
        {
            e.ToTable("ReconciliationRuns"); e.HasKey(x => x.Id); e.HasAlternateKey(x => new { x.BusinessUnitId, x.Id });
            e.Property(x => x.Status).HasMaxLength(20).IsRequired(); e.Property(x => x.IdempotencyKey).HasMaxLength(128).IsRequired(); e.Property(x => x.RequestHash).HasMaxLength(64).IsRequired(); e.Property(x => x.Version).IsConcurrencyToken();
            foreach (var p in new[] { nameof(ReconciliationRun.BankClosingBalance), nameof(ReconciliationRun.BookClosingBalance), nameof(ReconciliationRun.MatchedAmount), nameof(ReconciliationRun.UnexplainedDifference) }) e.Property(p).HasPrecision(18, 2);
            foreach (var p in new[] { nameof(ReconciliationRun.PreparedBy), nameof(ReconciliationRun.SubmittedBy), nameof(ReconciliationRun.ApprovedBy), nameof(ReconciliationRun.ReopenedBy) }) e.Property(p).HasMaxLength(255);
            foreach (var p in new[] { nameof(ReconciliationRun.ApprovalReason), nameof(ReconciliationRun.EvidenceReference), nameof(ReconciliationRun.ReopenReason), nameof(ReconciliationRun.ReopenEvidenceReference) }) e.Property(p).HasMaxLength(500);
            e.Property(x => x.CertificateHash).HasMaxLength(64); e.HasIndex(x => new { x.BusinessUnitId, x.IdempotencyKey }).IsUnique().HasDatabaseName("UX_ReconciliationRuns_BU_Idempotency");
            e.Property(x => x.RuleSetHash).HasMaxLength(64).IsRequired();
            e.HasIndex(x => new { x.BusinessUnitId, x.BankStatementId }).IsUnique().HasFilter("\"Status\" <> 'Reopened'").HasDatabaseName("UX_ReconciliationRuns_BU_ActiveStatement");
            e.HasCheckConstraint("CK_ReconciliationRuns_Status", "\"Status\" IN ('Draft','InReview','Approved','Reopened') AND \"Version\" > 0");
            e.HasCheckConstraint("CK_ReconciliationRuns_Certificate", "(\"Status\" = 'Approved' AND \"ApprovedBy\" IS NOT NULL AND \"ApprovedOn\" IS NOT NULL AND \"CertificateHash\" IS NOT NULL AND \"CertificateLineCount\" IS NOT NULL AND \"CertificateJournalCount\" IS NOT NULL AND \"UnexplainedDifference\" = 0) OR (\"Status\" <> 'Approved')");
            e.HasOne<BankAccount>().WithMany().HasForeignKey(x => new { x.BusinessUnitId, x.BankAccountId }).HasPrincipalKey(x => new { x.BusinessUnitId, x.Id }).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<BankStatement>().WithMany()
                .HasForeignKey(x => new { x.BusinessUnitId, x.BankStatementId, x.BankAccountId })
                .HasPrincipalKey(x => new { x.BusinessUnitId, x.Id, x.BankAccountId }).OnDelete(DeleteBehavior.Restrict);
        });
        modelBuilder.Entity<ReconciliationMatch>(e =>
        {
            e.ToTable("ReconciliationMatches"); e.HasKey(x => x.Id); e.HasAlternateKey(x => new { x.BusinessUnitId, x.Id });
            e.Property(x => x.MatchType).HasMaxLength(30).IsRequired(); e.Property(x => x.Confidence).HasPrecision(5, 4); e.Property(x => x.RuleCode).HasMaxLength(80).IsRequired(); e.Property(x => x.Status).HasMaxLength(20).IsRequired();
            e.Property(x => x.RuleDefinitionHash).HasMaxLength(64);
            e.Property(x => x.MatchReason).HasMaxLength(500); e.Property(x => x.EvidenceReference).HasMaxLength(500);
            e.Property(x => x.IdempotencyKey).HasMaxLength(128).IsRequired(); e.Property(x => x.RequestHash).HasMaxLength(64).IsRequired(); e.Property(x => x.Version).IsConcurrencyToken();
            foreach (var p in new[] { nameof(ReconciliationMatch.CreatedBy), nameof(ReconciliationMatch.ConfirmedBy), nameof(ReconciliationMatch.VoidedBy) }) e.Property(p).HasMaxLength(255); e.Property(x => x.VoidReason).HasMaxLength(500);
            e.HasIndex(x => new { x.BusinessUnitId, x.IdempotencyKey }).IsUnique().HasDatabaseName("UX_ReconciliationMatches_BU_Idempotency");
            e.HasCheckConstraint("CK_ReconciliationMatches_State", "\"Status\" IN ('Proposed','Confirmed','Voided') AND \"Version\" > 0 AND \"RuleVersion\" > 0 AND \"Confidence\" >= 0 AND \"Confidence\" <= 1");
            e.HasOne(x => x.Run).WithMany(x => x.Matches).HasForeignKey(x => new { x.BusinessUnitId, x.ReconciliationRunId }).HasPrincipalKey(x => new { x.BusinessUnitId, x.Id }).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.MatchingRule).WithMany().HasForeignKey(x => new { x.BusinessUnitId, x.BankMatchingRuleId })
                .HasPrincipalKey(x => new { x.BusinessUnitId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        });
        modelBuilder.Entity<ReconciliationAllocation>(e =>
        {
            e.ToTable("ReconciliationAllocations"); e.HasKey(x => x.Id); e.Property(x => x.BankAmount).HasPrecision(18, 2); e.Property(x => x.FunctionalAmount).HasPrecision(18, 2);
            e.HasIndex(x => new { x.BusinessUnitId, x.ReconciliationMatchId, x.BankStatementLineId, x.JournalEntryLineId }).IsUnique().HasDatabaseName("UX_ReconciliationAllocations_Evidence");
            e.HasCheckConstraint("CK_ReconciliationAllocations_Amounts", "\"BankAmount\" > 0 AND \"FunctionalAmount\" > 0");
            e.HasOne(x => x.Match).WithMany(x => x.Allocations).HasForeignKey(x => new { x.BusinessUnitId, x.ReconciliationMatchId }).HasPrincipalKey(x => new { x.BusinessUnitId, x.Id }).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<BankStatementLine>().WithMany().HasForeignKey(x => new { x.BusinessUnitId, x.BankStatementLineId }).HasPrincipalKey(x => new { x.BusinessUnitId, x.Id }).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<JournalEntryLine>().WithMany().HasForeignKey(x => new { x.BusinessUnitId, x.JournalEntryLineId })
                .HasPrincipalKey(x => new { x.BusinessUnitId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        });
    }
}
