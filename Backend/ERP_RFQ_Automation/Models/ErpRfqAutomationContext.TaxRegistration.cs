using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.Models;

// Tax registration numbers on the two parties to a purchase: the supplier that charges the input
// tax, and the business unit that would reclaim it.
//
// Configured in a partial for the same reason as the PriceAttestation / QuoteValidity modules: the
// large scaffolded context file stays untouched, and ErpRfqAutomationContext.Tenancy.cs's
// OnModelCreatingPartial makes ONE delegating call to ConfigureTaxRegistrationModel.
//
// The format rule is stated twice on purpose. ERP_RFQ_Automation.Tax.TaxRegistrationNumbers is the
// definition every write path uses and the source of the operator-facing message; the CHECK
// constraint below is the backstop for anything that reaches the column another way (a script, a
// future importer, a direct UPDATE). The predicates are the same rule:
//
//   value IS NULL                                   -> allowed, "not captured"
//   value matches ^[A-Z0-9./]{5,50}$                -> plausible registration identifier
//   value matches ^3[0-9]*$ (a Saudi CLAIM)         -> must also match ^3[0-9]{13}3$
//
// The constraint uses POSIX regex and is therefore emitted only on PostgreSQL; the portable
// (SQLite) lane has no '~' operator and would fail at CREATE TABLE. The application-level
// validator runs on both, so the portable suite still certifies the rule.
public partial class ErpRfqAutomationContext
{
    // Defining declaration for the hook called from the Tenancy partial.
    partial void ConfigureTaxRegistrationModel(ModelBuilder modelBuilder);

    partial void ConfigureTaxRegistrationModel(ModelBuilder modelBuilder)
    {
        var postgres = Database.IsNpgsql();

        modelBuilder.Entity<Supplier>(entity =>
        {
            entity.Property(x => x.TaxRegistrationNumber)
                .HasMaxLength(ERP_RFQ_Automation.Tax.TaxRegistrationNumbers.MaxLength);

            // Indexed, NOT unique. Two supplier records sharing one registration number is worth
            // looking at — it is usually the same counterparty entered twice, and a reclaim split
            // across two supplier records is exactly the reconciliation an auditor pulls on — but
            // it is not always wrong: a KSA VAT GROUP registers several legal entities under one
            // number, and each may invoice us separately. A unique constraint would make that
            // legitimate case unrecordable, so the duplicate is a signal to surface, not a write
            // to refuse. Filtered so the (many) unregistered suppliers are not indexed at all.
            entity.HasIndex(x => new { x.Buid, x.TaxRegistrationNumber })
                .HasFilter("\"TaxRegistrationNumber\" IS NOT NULL AND \"BUID\" IS NOT NULL")
                .HasDatabaseName("IX_Suppliers_BU_TaxRegistrationNumber");

            if (postgres)
                entity.HasCheckConstraint("CK_Suppliers_TaxRegistrationNumber", TaxRegistrationCheck);
        });

        modelBuilder.Entity<BusinessUnit>(entity =>
        {
            entity.Property(x => x.TaxRegistrationNumber)
                .HasMaxLength(ERP_RFQ_Automation.Tax.TaxRegistrationNumbers.MaxLength);

            if (postgres)
                entity.HasCheckConstraint("CK_BusinessUnits_TaxRegistrationNumber", TaxRegistrationCheck);
        });
    }

    private const string TaxRegistrationCheck =
        "\"TaxRegistrationNumber\" IS NULL OR (" +
        "\"TaxRegistrationNumber\" ~ '^[A-Z0-9./]{5,50}$' AND (" +
        "\"TaxRegistrationNumber\" !~ '^3[0-9]*$' OR \"TaxRegistrationNumber\" ~ '^3[0-9]{13}3$'))";
}
