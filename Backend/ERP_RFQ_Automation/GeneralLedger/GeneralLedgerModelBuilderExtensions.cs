using ERP_RFQ_Automation.Models;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.GeneralLedger;

public static class GeneralLedgerModelBuilderExtensions
{
    public static void ConfigureGeneralLedger(this ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<LedgerBook>(entity =>
        {
            entity.ToTable("LedgerBooks");
            entity.HasKey(x => x.Id);
            entity.HasAlternateKey(x => new { x.BusinessUnitId, x.Id });
            entity.Property(x => x.Name).HasMaxLength(160).IsRequired();
            entity.Property(x => x.TimeZoneId).HasMaxLength(100).IsRequired();
            entity.Property(x => x.IdempotencyKey).HasMaxLength(128).IsRequired();
            entity.Property(x => x.RequestHash).HasMaxLength(64).IsRequired();
            entity.Property(x => x.CreatedBy).HasMaxLength(255).IsRequired();
            entity.Property(x => x.Version).IsConcurrencyToken();
            entity.HasIndex(x => x.BusinessUnitId).IsUnique().HasDatabaseName("UX_LedgerBooks_BU");
            entity.HasIndex(x => new { x.BusinessUnitId, x.IdempotencyKey }).IsUnique()
                .HasDatabaseName("UX_LedgerBooks_BU_Idempotency");
            entity.HasCheckConstraint("CK_LedgerBooks_State",
                "\"FiscalYearStartMonth\" BETWEEN 1 AND 12 AND \"Version\" = 1");
            entity.HasOne<Currency>().WithMany().HasForeignKey(x => x.FunctionalCurrencyId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<LedgerAccount>(entity =>
        {
            entity.ToTable("LedgerAccounts");
            entity.HasKey(x => x.Id);
            entity.HasAlternateKey(x => new { x.BusinessUnitId, x.Id });
            entity.Property(x => x.Code).HasMaxLength(30).IsRequired();
            entity.Property(x => x.Name).HasMaxLength(160).IsRequired();
            entity.Property(x => x.Category).HasMaxLength(20).IsRequired();
            entity.Property(x => x.NormalBalance).HasMaxLength(10).IsRequired();
            entity.Property(x => x.IdempotencyKey).HasMaxLength(128).IsRequired();
            entity.Property(x => x.RequestHash).HasMaxLength(64).IsRequired();
            entity.Property(x => x.CreatedBy).HasMaxLength(255).IsRequired();
            entity.Property(x => x.DeactivatedBy).HasMaxLength(255);
            entity.Property(x => x.DeactivationReason).HasMaxLength(500);
            entity.Property(x => x.Version).IsConcurrencyToken();
            entity.HasIndex(x => new { x.BusinessUnitId, x.Code }).IsUnique()
                .HasDatabaseName("UX_LedgerAccounts_BU_Code");
            entity.HasIndex(x => new { x.BusinessUnitId, x.IdempotencyKey }).IsUnique()
                .HasDatabaseName("UX_LedgerAccounts_BU_Idempotency");
            entity.HasCheckConstraint("CK_LedgerAccounts_State",
                "((\"Category\" IN ('Asset','Expense') AND ((NOT \"IsContraAccount\" AND \"NormalBalance\" = 'Debit') OR (\"IsContraAccount\" AND \"NormalBalance\" = 'Credit'))) OR (\"Category\" IN ('Liability','Equity','Revenue') AND ((NOT \"IsContraAccount\" AND \"NormalBalance\" = 'Credit') OR (\"IsContraAccount\" AND \"NormalBalance\" = 'Debit')))) AND ((\"IsActive\" AND \"DeactivatedBy\" IS NULL AND \"DeactivatedOn\" IS NULL AND \"DeactivationReason\" IS NULL) OR (NOT \"IsActive\" AND \"DeactivatedBy\" IS NOT NULL AND \"DeactivatedOn\" IS NOT NULL AND length(trim(\"DeactivationReason\")) >= 20))");
            entity.HasOne<Currency>().WithMany().HasForeignKey(x => x.CurrencyId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<AccountingPeriod>(entity =>
        {
            entity.ToTable("AccountingPeriods");
            entity.HasKey(x => x.Id);
            entity.HasAlternateKey(x => new { x.BusinessUnitId, x.Id });
            entity.Property(x => x.Name).HasMaxLength(80).IsRequired();
            entity.Property(x => x.Status).HasMaxLength(20).IsRequired();
            entity.Property(x => x.IdempotencyKey).HasMaxLength(128).IsRequired();
            entity.Property(x => x.RequestHash).HasMaxLength(64).IsRequired();
            entity.Property(x => x.CreatedBy).HasMaxLength(255).IsRequired();
            entity.Property(x => x.SoftClosedBy).HasMaxLength(255);
            entity.Property(x => x.ClosedBy).HasMaxLength(255);
            entity.Property(x => x.CloseReason).HasMaxLength(500);
            entity.Property(x => x.CloseEvidenceReference).HasMaxLength(500);
            entity.Property(x => x.CloseTrialBalanceHash).HasMaxLength(64);
            entity.Property(x => x.CloseTotalDebit).HasPrecision(18, 2);
            entity.Property(x => x.CloseTotalCredit).HasPrecision(18, 2);
            entity.Property(x => x.ReopenedBy).HasMaxLength(255);
            entity.Property(x => x.ReopenReason).HasMaxLength(500);
            entity.Property(x => x.ReopenEvidenceReference).HasMaxLength(500);
            entity.Property(x => x.Version).IsConcurrencyToken();
            entity.HasIndex(x => new { x.BusinessUnitId, x.FiscalYear, x.PeriodNumber }).IsUnique()
                .HasDatabaseName("UX_AccountingPeriods_BU_Year_Period");
            entity.HasIndex(x => new { x.BusinessUnitId, x.IdempotencyKey }).IsUnique()
                .HasDatabaseName("UX_AccountingPeriods_BU_Idempotency");
            entity.HasCheckConstraint("CK_AccountingPeriods_Range",
                "\"FiscalYear\" BETWEEN 2000 AND 2200 AND \"PeriodNumber\" BETWEEN 1 AND 99 AND \"StartsOn\" <= \"EndsOn\"");
        });

        modelBuilder.Entity<JournalEntry>(entity =>
        {
            entity.ToTable("JournalEntries");
            entity.HasKey(x => x.Id);
            entity.HasAlternateKey(x => new { x.BusinessUnitId, x.Id });
            entity.Property(x => x.EntryNumber).HasMaxLength(50);
            entity.Property(x => x.Status).HasMaxLength(20).IsRequired();
            entity.Property(x => x.Description).HasMaxLength(500).IsRequired();
            entity.Property(x => x.SourceType).HasMaxLength(50).IsRequired();
            entity.Property(x => x.SourceReference).HasMaxLength(200);
            entity.Property(x => x.TotalDebit).HasPrecision(18, 2);
            entity.Property(x => x.TotalCredit).HasPrecision(18, 2);
            entity.Property(x => x.IdempotencyKey).HasMaxLength(128).IsRequired();
            entity.Property(x => x.RequestHash).HasMaxLength(64).IsRequired();
            entity.Property(x => x.CreatedBy).HasMaxLength(255).IsRequired();
            entity.Property(x => x.PostedBy).HasMaxLength(255);
            entity.Property(x => x.CancelledBy).HasMaxLength(255);
            entity.Property(x => x.CancellationReason).HasMaxLength(500);
            entity.Property(x => x.ReversedBy).HasMaxLength(255);
            entity.Property(x => x.ReversalReason).HasMaxLength(500);
            entity.Property(x => x.ReversalEvidenceReference).HasMaxLength(500);
            entity.Property(x => x.Version).IsConcurrencyToken();
            entity.HasIndex(x => new { x.BusinessUnitId, x.EntryNumber }).IsUnique()
                .HasFilter("\"EntryNumber\" IS NOT NULL").HasDatabaseName("UX_JournalEntries_BU_Number");
            entity.HasIndex(x => new { x.BusinessUnitId, x.IdempotencyKey }).IsUnique()
                .HasDatabaseName("UX_JournalEntries_BU_Idempotency");
            entity.HasIndex(x => new { x.BusinessUnitId, x.SourceType, x.SourceReference, x.SourceVersion })
                .IsUnique().HasFilter("\"SourceReference\" IS NOT NULL AND \"ReversesJournalEntryId\" IS NULL")
                .HasDatabaseName("UX_JournalEntries_BU_SourceVersion");
            entity.HasIndex(x => new { x.BusinessUnitId, x.ReversesJournalEntryId }).IsUnique()
                .HasFilter("\"ReversesJournalEntryId\" IS NOT NULL").HasDatabaseName("UX_JournalEntries_BU_Reversal");
            entity.HasCheckConstraint("CK_JournalEntries_Totals",
                "\"TotalDebit\" > 0 AND \"TotalDebit\" = \"TotalCredit\"");
            entity.HasOne(x => x.Period).WithMany(x => x.JournalEntries)
                .HasForeignKey(x => new { x.BusinessUnitId, x.AccountingPeriodId })
                .HasPrincipalKey(x => new { x.BusinessUnitId, x.Id }).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<Currency>().WithMany()
                .HasForeignKey(x => x.FunctionalCurrencyId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(x => x.ReversesJournalEntry).WithOne(x => x.ReversalJournalEntry)
                .HasForeignKey<JournalEntry>(x => new { x.BusinessUnitId, x.ReversesJournalEntryId })
                .HasPrincipalKey<JournalEntry>(x => new { x.BusinessUnitId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<JournalEntryLine>(entity =>
        {
            entity.ToTable("JournalEntryLines");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Description).HasMaxLength(500).IsRequired();
            entity.Property(x => x.ExchangeRate).HasPrecision(20, 10);
            entity.Property(x => x.TransactionDebit).HasPrecision(18, 2);
            entity.Property(x => x.TransactionCredit).HasPrecision(18, 2);
            entity.Property(x => x.FunctionalDebit).HasPrecision(18, 2);
            entity.Property(x => x.FunctionalCredit).HasPrecision(18, 2);
            entity.Property(x => x.SourceReference).HasMaxLength(200);
            entity.HasIndex(x => new { x.BusinessUnitId, x.JournalEntryId, x.Sequence }).IsUnique()
                .HasDatabaseName("UX_JournalEntryLines_BU_Journal_Sequence");
            entity.HasCheckConstraint("CK_JournalEntryLines_Amounts",
                "\"Sequence\" > 0 AND CAST(\"ExchangeRate\" AS NUMERIC) > 0 AND CAST(\"TransactionDebit\" AS NUMERIC) >= 0 AND CAST(\"TransactionCredit\" AS NUMERIC) >= 0 AND CAST(\"FunctionalDebit\" AS NUMERIC) >= 0 AND CAST(\"FunctionalCredit\" AS NUMERIC) >= 0 AND ((CAST(\"TransactionDebit\" AS NUMERIC) > 0 AND CAST(\"TransactionCredit\" AS NUMERIC) = 0 AND CAST(\"FunctionalDebit\" AS NUMERIC) > 0 AND CAST(\"FunctionalCredit\" AS NUMERIC) = 0) OR (CAST(\"TransactionCredit\" AS NUMERIC) > 0 AND CAST(\"TransactionDebit\" AS NUMERIC) = 0 AND CAST(\"FunctionalCredit\" AS NUMERIC) > 0 AND CAST(\"FunctionalDebit\" AS NUMERIC) = 0))");
            entity.HasOne(x => x.JournalEntry).WithMany(x => x.Lines)
                .HasForeignKey(x => new { x.BusinessUnitId, x.JournalEntryId })
                .HasPrincipalKey(x => new { x.BusinessUnitId, x.Id }).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(x => x.Account).WithMany(x => x.JournalLines)
                .HasForeignKey(x => new { x.BusinessUnitId, x.LedgerAccountId })
                .HasPrincipalKey(x => new { x.BusinessUnitId, x.Id }).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<Currency>().WithMany().HasForeignKey(x => x.TransactionCurrencyId)
                .OnDelete(DeleteBehavior.Restrict);
        });
    }
}
