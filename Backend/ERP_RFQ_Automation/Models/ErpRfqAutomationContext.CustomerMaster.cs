using ERP_RFQ_Automation.MasterData;
using ERP_RFQ_Automation.Tax;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.Models;

/// <summary>
/// FR-CST-01 / FR-CST-02 column configuration for <see cref="Customer"/>.
///
/// <para>Applied through an extension method from <c>OnModelCreatingPartial</c>, matching
/// <c>ApplyReportingModel</c> and friends — the partial-method hooks each allow exactly one call
/// site and are already spoken for.</para>
///
/// <para><b>No new table, therefore no new RLS policy and no new GRANT.</b> Every column below
/// lands inside <c>public."Customers"</c>, which already carries both. The tenant column on this
/// table is spelled <c>"BUID"</c> (the legacy spelling), which matters for the migration owner: the
/// existing policy names it, and nothing here changes it.</para>
///
/// <para>The format rules are stated twice on purpose, exactly as the tax-registration module does
/// it: <see cref="CommercialRegistrationNumbers"/> and <see cref="TaxRegistrationNumbers"/> are the
/// definition every write path uses and the source of the operator-facing message; the CHECK
/// constraints below are the backstop for anything that reaches the columns another way (a script,
/// a future importer, a direct UPDATE). The constraints use POSIX regex and are therefore emitted
/// only on PostgreSQL — the portable (SQLite) lane has no '~' operator and would fail at
/// CREATE TABLE, and additionally runs with <c>PRAGMA ignore_check_constraints = ON</c>. The
/// application-level validators run on both lanes, so the portable suite still certifies the rule.</para>
/// </summary>
public static class CustomerMasterModelBuilderExtensions
{
    // Same shape as CK_Suppliers_TaxRegistrationNumber, against the customer's own column.
    private const string TaxRegistrationCheck =
        "\"TaxRegistrationNumber\" IS NULL OR (" +
        "\"TaxRegistrationNumber\" ~ '^[A-Z0-9./]{5,50}$' AND (" +
        "\"TaxRegistrationNumber\" !~ '^3[0-9]*$' OR \"TaxRegistrationNumber\" ~ '^3[0-9]{13}3$'))";

    // An all-digit value is a Saudi CLAIM and must then be exactly 10 digits; anything else is a
    // foreign registration carrying its country prefix.
    private const string CommercialRegistrationCheck =
        "\"CommercialRegistrationNumber\" IS NULL OR (" +
        "\"CommercialRegistrationNumber\" ~ '^[A-Z0-9]{5,30}$' AND (" +
        "\"CommercialRegistrationNumber\" !~ '^[0-9]+$' OR " +
        "\"CommercialRegistrationNumber\" ~ '^[0-9]{10}$'))";

    private const string SectorCheck =
        "\"Sector\" IS NULL OR \"Sector\" IN ('GOVERNMENT', 'SEMI_GOVERNMENT', 'PRIVATE')";

    public static ModelBuilder ApplyCustomerMasterModel(this ModelBuilder modelBuilder, bool postgres)
    {
        modelBuilder.Entity<Customer>(entity =>
        {
            entity.Property(x => x.CommercialRegistrationNumber)
                .HasMaxLength(CommercialRegistrationNumbers.MaxLength);
            entity.Property(x => x.TaxRegistrationNumber)
                .HasMaxLength(TaxRegistrationNumbers.MaxLength);
            entity.Property(x => x.Sector)
                .HasMaxLength(CustomerSectors.MaxLength);

            // Indexed, NOT unique — the same reasoning as IX_Suppliers_BU_TaxRegistrationNumber.
            // Two customer records sharing one CR or one VAT number is worth surfacing (it is
            // usually the same legal entity entered twice, and a receivable split across two
            // customer records is exactly the reconciliation an auditor pulls on) but it is not
            // always wrong: a KSA VAT GROUP registers several legal entities under one number, and
            // each may be invoiced separately. A unique constraint would make that legitimate case
            // unrecordable. Filtered, so the many customers with neither value are not indexed.
            entity.HasIndex(x => new { x.Buid, x.CommercialRegistrationNumber })
                .HasFilter("\"CommercialRegistrationNumber\" IS NOT NULL")
                .HasDatabaseName("IX_Customers_BU_CommercialRegistrationNumber");
            entity.HasIndex(x => new { x.Buid, x.TaxRegistrationNumber })
                .HasFilter("\"TaxRegistrationNumber\" IS NOT NULL")
                .HasDatabaseName("IX_Customers_BU_TaxRegistrationNumber");

            // THE read predicate of FR-CST-02: "which customers belong to a team in my scope".
            // Filtered because an unassigned customer is never selected by it — see
            // Customer.AccountTeamId's remarks on why null is not "restricted to nobody".
            entity.HasIndex(x => new { x.Buid, x.AccountTeamId })
                .HasFilter("\"AccountTeamId\" IS NOT NULL")
                .HasDatabaseName("IX_Customers_BU_AccountTeam");

            entity.HasIndex(x => new { x.Buid, x.RegionStateId })
                .HasFilter("\"RegionStateId\" IS NOT NULL")
                .HasDatabaseName("IX_Customers_BU_RegionState");

            // Restrict, not cascade and not set-null. A team that still owns accounts has to be
            // reassigned before it can be removed: silently setting the accounts to "no team"
            // would hand every one of them back to the whole tenant without anybody deciding to.
            entity.HasOne(x => x.AccountTeam)
                .WithMany()
                .HasForeignKey(x => x.AccountTeamId)
                .OnDelete(DeleteBehavior.Restrict)
                .HasConstraintName("FK_Customers_AccountTeam");

            entity.HasOne(x => x.RegionState)
                .WithMany()
                .HasForeignKey(x => x.RegionStateId)
                .OnDelete(DeleteBehavior.Restrict)
                .HasConstraintName("FK_Customers_RegionState");

            if (postgres)
            {
                entity.HasCheckConstraint("CK_Customers_TaxRegistrationNumber", TaxRegistrationCheck);
                entity.HasCheckConstraint("CK_Customers_CommercialRegistrationNumber", CommercialRegistrationCheck);
                entity.HasCheckConstraint("CK_Customers_Sector", SectorCheck);
            }
        });

        return modelBuilder;
    }
}
