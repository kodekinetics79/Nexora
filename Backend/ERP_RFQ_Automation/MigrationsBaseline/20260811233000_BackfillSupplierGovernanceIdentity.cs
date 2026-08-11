using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ERP_RFQ_Automation.Migrations
{
    /// <summary>
    /// Repairs public."Suppliers" rows left with a NULL <c>"ConcurrencyToken"</c> or a NULL
    /// <c>"EffectiveFrom"</c> by the bulk Excel importer.
    ///
    /// <para>THE DEFECT. Both columns are nullable with no store default and no trigger, and only
    /// <c>SupplierRepository.AddAsync</c> assigned them. <c>SupplierUploaderService</c> — the bulk
    /// import — writes suppliers straight through the DbContext and set neither. A bulk-imported
    /// supplier therefore landed with <c>"ConcurrencyToken" IS NULL</c>, and
    /// <c>SupplierGovernanceService.GovernAsync</c> compares the stored value against the caller's
    /// NON-nullable <c>ExpectedConcurrencyToken</c>. Null can never equal a Guid, so every
    /// governance decision on such a supplier was rejected with
    /// <c>SupplierGovernanceConflictException</c> — surfaced as HTTP 409 "The Supplier changed
    /// after it was loaded. Refresh and review the latest evidence." Refreshing returns the same
    /// null, so the supplier was permanently ungovernable behind an error instructing the operator
    /// to do the one thing that could not work. A quieter second consequence:
    /// <c>SupplierController.Update</c> only enforces the optimistic-concurrency check
    /// <c>if (existing.ConcurrencyToken.HasValue)</c>, so those same rows had no lost-update
    /// protection on the edit screen either.
    ///
    /// <para>WHY A BACKFILL IS NEEDED AS WELL AS THE CODE FIX. The assignment has moved to
    /// <c>SupplierGovernanceIdentityRules.Stamp</c>, called from
    /// <c>ErpRfqAutomationContext.SaveChanges</c>, which every write path passes through — so no
    /// NEW row can be written without both values, and an existing one is healed by the next save
    /// that touches it. But governance is precisely the path that cannot heal a row: it throws the
    /// conflict before it writes anything. An already-imported supplier would therefore stay
    /// ungovernable until somebody happened to edit it on the supplier screen. This backfill is
    /// what makes the repair unconditional.</para>
    ///
    /// <para>ON gen_random_uuid(). Supplied by the <c>pgcrypto</c> extension, which
    /// <c>Sql/01_schema_and_extensions.sql</c> creates and <c>Sql/03_tables_and_sequences.sql</c>
    /// already relies on for <c>"ResolutionBatchId"</c>. Each row gets its OWN token — a single
    /// shared value would let one supplier's stale token pass another supplier's concurrency
    /// check.</para>
    ///
    /// <para>ON ROW LEVEL SECURITY. public."Suppliers" has RLS ENABLED but not FORCED, and its one
    /// policy (<c>nexora_tenant_isolation</c>) is granted <c>TO nexora_tenant_app</c>. Migrations
    /// run as the table owner, which is neither forced nor named by the policy, so these statements
    /// see every tenant's rows rather than silently updating none.</para>
    ///
    /// <para>ON Down(). Deliberately empty. The inverse of this migration is "put the nulls back",
    /// which would re-break every row it fixed and destroy the tokens that any client has since
    /// read. Reverting the schema is not the same act as re-introducing a defect.</para>
    /// </summary>
    /// <inheritdoc />
    public partial class BackfillSupplierGovernanceIdentity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                UPDATE public."Suppliers"
                   SET "ConcurrencyToken" = gen_random_uuid()
                 WHERE "ConcurrencyToken" IS NULL;
                """);

            // CreatedOn is NOT NULL, so this always produces a value, and it reproduces exactly
            // what SupplierRepository.AddAsync stamped: governance is effective from the moment the
            // record came into existence, never from the date the repair happened to run.
            migrationBuilder.Sql("""
                UPDATE public."Suppliers"
                   SET "EffectiveFrom" = "CreatedOn"
                 WHERE "EffectiveFrom" IS NULL;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
        }
    }
}
