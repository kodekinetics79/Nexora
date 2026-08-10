using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ERP_RFQ_Automation.Migrations
{
    /// <summary>
    /// Four corrections, three of them to defects introduced by earlier migrations in this build.
    ///
    /// <para><b>1. <c>AI_NOT_AUTHORIZED</c> joins the outcome-state CHECK.</b> The enum member is
    /// declared at <c>EvidenceLedgerEntities.cs</c> and written at <c>ExtractionWorker.cs</c> when a
    /// tenant's policy refuses external AI processing. The constraint did not admit it. Both test
    /// lanes were green because the portable lane runs SQLite with
    /// <c>PRAGMA ignore_check_constraints = ON</c> and the model agreed with the migrations — so the
    /// first tenant to upload a document with AI processing disabled would have taken a 23514 on
    /// PostgreSQL and nowhere else. The entity's own doc comment states the rule this migration
    /// finally obeys: deploy the constraint BEFORE the code that writes the value.</para>
    ///
    /// <para><b>2. The append-only ledgers stop being deletable.</b>
    /// <c>20260810050406</c> granted <c>SELECT, INSERT, UPDATE, DELETE</c> on all fifteen tables it
    /// created, in one blanket statement. That contradicts the documented intent of eight of them
    /// and it contradicts <c>docs/WIRING_CONTRACT.md</c>, which states in terms that DELETE is
    /// deliberately not granted on <c>inventory_reorder_alerts</c> because an alert is resolved by a
    /// status transition and never removed. The worst of the eight is the delivery-proof pair:
    /// <c>delivery_proof_lines</c> cascades from <c>delivery_proofs</c>, and the accepted quantities
    /// on those lines are what caps the invoice — so deleting a POD silently un-caps the ceiling and
    /// the customer can be billed for goods they refused. No code path deletes any of these eight;
    /// a repo-wide search for <c>Remove</c>, <c>RemoveRange</c> and <c>ExecuteDelete</c> against
    /// each entity returns nothing. The grant was reach nobody asked for and nobody uses.</para>
    ///
    /// <para><b>3. The master-data audit trigger is created.</b> Four separate code comments assert
    /// that <c>trg_master_data_audit_append_only</c> enforces immutability at the database — the
    /// entity doc comment, the delivery model builder, the context configuration (which justifies a
    /// CASCADE on the grounds that "the header itself can never be deleted (append-only trigger)")
    /// and the wiring contract. The trigger did not exist. This is failure #7 in the contract's own
    /// list: a control that reports success while doing nothing, and here the report was four
    /// comments deep. The function shape is copied verbatim from
    /// <c>nexora_reject_procurement_event_mutation</c> in <c>20260726052445</c>, the closest
    /// existing precedent. A trigger, unlike row-level security, binds the table owner too, so it is
    /// a strictly stronger guarantee than the REVOKE above and both are kept.</para>
    ///
    /// <para><b>4. <c>QuoteNoResponseExpiryDays</c> is corrected from 0 to 90.</b>
    /// <c>20260809163319</c> added the column <c>NOT NULL DEFAULT 0</c> while the C# default is 90.
    /// The sweep predicate is <c>SentOn.AddDays(noResponseDays) &lt; now</c>, so 0 reduces it to
    /// <c>SentOn &lt; now</c> — true of every quote ever sent. Every tenant who had ever saved SLA
    /// settings would have had their entire open quote book stamped EXPIRED on the first five-minute
    /// tick after deploy, with no reopen path. That 0 is not a value the domain admits: the
    /// controller clamps writes to 1-365, so the migration was the only thing in the system that
    /// could produce it. This is failure #10 — a migration default that inverts meaning — and the
    /// same migration handled the sibling column's semantics change deliberately and correctly.
    /// Existing rows are corrected, the column default is moved to 90 so the two agree, and
    /// <c>SlaSweepWorker.ExpiryCandidates</c> now treats a non-positive window as "trigger
    /// disabled" rather than "expire everything", matching the fail-safe the two supplier sweeps
    /// already apply.</para>
    /// </summary>
    /// <inheritdoc />
    public partial class Gate2to8AppendOnlyLedgerGrantsAuditTriggerAndQuoteExpiryBackfill : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_source_document_occurrences_outcome_state",
                table: "source_document_occurrences");

            migrationBuilder.AddCheckConstraint(
                name: "ck_source_document_occurrences_outcome_state",
                table: "source_document_occurrences",
                sql: "outcome_state IN ('NONE','EXACT_DUPLICATE_PENDING_SECURITY','EXACT_DUPLICATE_CONFIRMED','BUSINESS_DUPLICATE_CONFIRMED','DUPLICATE_RESCAN_REQUIRED','REVISION','POSSIBLE_MATCH','SECURITY_SCAN_BLOCKED','MALWARE_DETECTED','UNSUPPORTED_FORMAT','SOURCE_OBJECT_UNAVAILABLE','EVIDENCE_INTEGRITY_FAILURE','AI_NOT_AUTHORIZED')");

            // Eight of the fifteen tables from 20260810050406. The seven left alone are genuinely
            // mutable working data: inbound_logistics_policies and ports_of_entry are tenant
            // configuration, supplier_shipments and supplier_shipment_lines are an in-flight
            // consignment record that is corrected while it travels, ReportSubscriptions is
            // user-managed and has a real delete path, and material_lots / material_lot_certificates
            // are left deletable deliberately — a lot is corrected through its status ladder today,
            // but no documented append-only intent exists for them and revoking a verb on a guess is
            // how you end up with an operational trap and no alternative route.
            migrationBuilder.Sql("""
                REVOKE DELETE ON TABLE
                    public."delivery_proofs",
                    public."delivery_proof_lines",
                    public."delivery_shortfall_decisions",
                    public."inventory_reorder_alerts",
                    public."material_lot_consumptions",
                    public."QuoteRemovalRecords",
                    public."MasterDataChangeEvents",
                    public."MasterDataFieldChanges"
                FROM nexora_tenant_app;

                -- The audit pair is the only one of the eight that must also refuse UPDATE. The
                -- other six record an event whose surrounding row still has a lifecycle: an alert
                -- moves OPEN -> ACKNOWLEDGED -> RESOLVED, a proof carries an optimistic-concurrency
                -- Version. A field-level before/after trail has no lifecycle at all — the only
                -- reason to update one is to change what it says happened.
                REVOKE UPDATE ON TABLE
                    public."MasterDataChangeEvents",
                    public."MasterDataFieldChanges"
                FROM nexora_tenant_app;
                """);

            migrationBuilder.Sql("""
                CREATE OR REPLACE FUNCTION nexora_reject_master_data_audit_mutation()
                RETURNS trigger LANGUAGE plpgsql AS $function$
                BEGIN
                    RAISE EXCEPTION USING
                        ERRCODE = '55000',
                        MESSAGE = 'master data audit rows are append-only';
                END
                $function$;

                DROP TRIGGER IF EXISTS trg_master_data_audit_append_only ON public."MasterDataChangeEvents";
                CREATE TRIGGER trg_master_data_audit_append_only
                    BEFORE UPDATE OR DELETE ON public."MasterDataChangeEvents"
                    FOR EACH ROW EXECUTE FUNCTION nexora_reject_master_data_audit_mutation();

                DROP TRIGGER IF EXISTS trg_master_data_audit_append_only ON public."MasterDataFieldChanges";
                CREATE TRIGGER trg_master_data_audit_append_only
                    BEFORE UPDATE OR DELETE ON public."MasterDataFieldChanges"
                    FOR EACH ROW EXECUTE FUNCTION nexora_reject_master_data_audit_mutation();
                """);

            // WHERE = 0 rather than an unconditional UPDATE: 0 is the only value the API cannot
            // write, so it identifies exactly the rows the bad backfill touched and leaves any
            // deliberate tenant setting alone. Then the column default is moved to match the code
            // default, so a row inserted by anything other than EF no longer inherits the
            // catastrophic value.
            migrationBuilder.Sql("""
                UPDATE public."SlaPolicies" SET "QuoteNoResponseExpiryDays" = 90
                WHERE "QuoteNoResponseExpiryDays" = 0;

                ALTER TABLE public."SlaPolicies"
                    ALTER COLUMN "QuoteNoResponseExpiryDays" SET DEFAULT 90;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                ALTER TABLE public."SlaPolicies"
                    ALTER COLUMN "QuoteNoResponseExpiryDays" SET DEFAULT 0;
                """);

            migrationBuilder.Sql("""
                DROP TRIGGER IF EXISTS trg_master_data_audit_append_only ON public."MasterDataFieldChanges";
                DROP TRIGGER IF EXISTS trg_master_data_audit_append_only ON public."MasterDataChangeEvents";
                DROP FUNCTION IF EXISTS nexora_reject_master_data_audit_mutation();
                """);

            migrationBuilder.Sql("""
                GRANT DELETE ON TABLE
                    public."delivery_proofs",
                    public."delivery_proof_lines",
                    public."delivery_shortfall_decisions",
                    public."inventory_reorder_alerts",
                    public."material_lot_consumptions",
                    public."QuoteRemovalRecords",
                    public."MasterDataChangeEvents",
                    public."MasterDataFieldChanges"
                TO nexora_tenant_app;

                GRANT UPDATE ON TABLE
                    public."MasterDataChangeEvents",
                    public."MasterDataFieldChanges"
                TO nexora_tenant_app;
                """);

            migrationBuilder.DropCheckConstraint(
                name: "ck_source_document_occurrences_outcome_state",
                table: "source_document_occurrences");

            migrationBuilder.AddCheckConstraint(
                name: "ck_source_document_occurrences_outcome_state",
                table: "source_document_occurrences",
                sql: "outcome_state IN ('NONE','EXACT_DUPLICATE_PENDING_SECURITY','EXACT_DUPLICATE_CONFIRMED','BUSINESS_DUPLICATE_CONFIRMED','DUPLICATE_RESCAN_REQUIRED','REVISION','POSSIBLE_MATCH','SECURITY_SCAN_BLOCKED','MALWARE_DETECTED','UNSUPPORTED_FORMAT','SOURCE_OBJECT_UNAVAILABLE','EVIDENCE_INTEGRITY_FAILURE')");
        }
    }
}
