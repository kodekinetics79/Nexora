using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace ERP_RFQ_Automation.Migrations
{
    /// <summary>
    /// CLIENT ORGANISATION IDENTITY.
    ///
    /// Production evidence this migration exists to fix: 26 leads, 0 with a CustomerID, every
    /// one UNRESOLVED, Leads.BuyersName holding a PERSON ("AMER S. AL-DOSSARI") and
    /// Leads.Clientemail holding the synthetic placeholder "extraction@pipeline.local" on 23
    /// of them. A sales rep looking at a lead could not tell which client company it came from.
    ///
    /// Schema added here:
    ///   * 11 Leads columns — WHY a client was (or was not) linked, plus the raw evidence read
    ///     off the document, so an unresolved lead shows what we DO know instead of a dead end.
    ///   * 4 customer_identifiers columns — learning provenance, so a later reversal can find
    ///     and expire exactly what a now-known-wrong review taught.
    ///   * lead_customer_match_candidates — the ranked machine proposals, RLS-protected and
    ///     tenant-scoped like every other tenant table.
    ///   * CK_Leads_CustomerIdentityStatus — the invariant that a "not decided" status can
    ///     never carry a customer, and a "decided" one always must.
    ///
    /// Deliberately NOT here: a data backfill of the 26 leads. Resolution is a runtime
    /// service (POST /api/Lead/resolve-clients); freezing a matching rule into schema history
    /// would make a wrong rule permanent instead of re-runnable.
    /// </summary>
    public partial class ClientOrganisationIdentity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // LATENT PRODUCTION BUG, fixed here because this migration is the first thing
            // that would trip it: the status vocabulary already contained
            // "CUSTOMER_CONFIRMED_CONTACT_UNRESOLVED" (37 characters) and the column is
            // varchar(32). Every review in which a person picked a customer but no contact
            // would have failed with PostgreSQL 22001. Widened first, so the repair statement
            // below can write that value.
            migrationBuilder.AlterColumn<string>(
                name: "CustomerMatchStatus",
                table: "Leads",
                type: "character varying(64)",
                maxLength: 64,
                nullable: false,
                defaultValue: "UNRESOLVED",
                oldClrType: typeof(string),
                oldType: "character varying(32)",
                oldMaxLength: 32,
                oldNullable: false,
                oldDefaultValue: "UNRESOLVED");

            migrationBuilder.AddColumn<string>(
                name: "CustomerBuyerEmailExtracted",
                table: "Leads",
                type: "character varying(255)",
                unicode: false,
                maxLength: 255,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CustomerCompanyEvidence",
                table: "Leads",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CustomerCompanyNameExtracted",
                table: "Leads",
                type: "character varying(320)",
                maxLength: 320,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CustomerCompanyRegistrationId",
                table: "Leads",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "CustomerMatchConfidence",
                table: "Leads",
                type: "numeric(5,4)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CustomerMatchExplanation",
                table: "Leads",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CustomerMatchReasonCode",
                table: "Leads",
                type: "character varying(40)",
                maxLength: 40,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "CustomerMatchedOn",
                table: "Leads",
                type: "timestamp without time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CustomerPortalNameExtracted",
                table: "Leads",
                type: "character varying(120)",
                maxLength: 120,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SupplierAccountRefOnDocument",
                table: "Leads",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SupplierNameOnDocument",
                table: "Leads",
                type: "character varying(320)",
                maxLength: 320,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "LastObservedOn",
                table: "customer_identifiers",
                type: "timestamp without time zone",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "LearnedFromLeadId",
                table: "customer_identifiers",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "LearnedFromReviewAuditId",
                table: "customer_identifiers",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ObservationCount",
                table: "customer_identifiers",
                type: "integer",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.CreateTable(
                name: "lead_customer_match_candidates",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    BusinessUnitId = table.Column<long>(type: "bigint", nullable: false),
                    LeadId = table.Column<long>(type: "bigint", nullable: false),
                    CustomerId = table.Column<long>(type: "bigint", nullable: false),
                    Rank = table.Column<int>(type: "integer", nullable: false),
                    Confidence = table.Column<decimal>(type: "numeric(5,4)", precision: 5, scale: 4, nullable: false),
                    ReasonCode = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    Explanation = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    CreatedOn = table.Column<DateTime>(type: "timestamp without time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_lead_customer_match_candidates", x => x.Id);
                    table.CheckConstraint("CK_lead_customer_match_candidates_Confidence", "\"Confidence\" >= 0 AND \"Confidence\" <= 1");
                    table.CheckConstraint("CK_lead_customer_match_candidates_Rank", "\"Rank\" BETWEEN 1 AND 5");
                    table.ForeignKey(
                        name: "FK_lead_customer_match_candidates_Customers_BusinessUnitId_Cus~",
                        columns: x => new { x.BusinessUnitId, x.CustomerId },
                        principalTable: "Customers",
                        principalColumns: new[] { "BUID", "ID" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_lead_customer_match_candidates_Leads_BusinessUnitId_LeadId",
                        columns: x => new { x.BusinessUnitId, x.LeadId },
                        principalTable: "Leads",
                        principalColumns: new[] { "BusinessUnitID", "ID" },
                        onDelete: ReferentialAction.Cascade);
                });

            // Reconcile any pre-existing row that would violate the invariant BEFORE the
            // constraint is validated, so an upgrade cannot fail on legacy data. Both
            // directions are repaired to the honest value rather than to whichever one makes
            // the constraint pass:
            //   * a row that HAS a customer but claims an unresolved status is a completed
            //     human/legacy link whose status was never written — it becomes CONFIRMED
            //     when a contact is known and CUSTOMER_CONFIRMED_CONTACT_UNRESOLVED otherwise;
            //   * a row that claims a decided status but has NO customer never actually
            //     linked anything — it becomes UNRESOLVED.
            migrationBuilder.Sql("""
                UPDATE "Leads"
                SET "CustomerMatchStatus" = CASE WHEN "ContactID" IS NOT NULL
                        THEN 'CONFIRMED' ELSE 'CUSTOMER_CONFIRMED_CONTACT_UNRESOLVED' END
                WHERE "CustomerID" IS NOT NULL
                  AND "CustomerMatchStatus" IN ('UNRESOLVED', 'SUGGESTED', 'AMBIGUOUS');

                UPDATE "Leads"
                SET "CustomerMatchStatus" = 'UNRESOLVED'
                WHERE "CustomerID" IS NULL
                  AND "CustomerMatchStatus" NOT IN ('UNRESOLVED', 'SUGGESTED', 'AMBIGUOUS');
                """);

            migrationBuilder.AddCheckConstraint(
                name: "CK_Leads_CustomerIdentityStatus",
                table: "Leads",
                sql: "CASE WHEN \"CustomerMatchStatus\" IN ('UNRESOLVED','SUGGESTED','AMBIGUOUS') THEN \"CustomerID\" IS NULL ELSE \"CustomerID\" IS NOT NULL END");

            migrationBuilder.CreateIndex(
                name: "IX_customer_identifiers_learned_from_lead",
                table: "customer_identifiers",
                columns: new[] { "BusinessUnitId", "LearnedFromLeadId" },
                filter: "\"LearnedFromLeadId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_lead_customer_match_candidates_customer",
                table: "lead_customer_match_candidates",
                columns: new[] { "BusinessUnitId", "CustomerId" });

            migrationBuilder.CreateIndex(
                name: "UX_lead_customer_match_candidates_lead_rank",
                table: "lead_customer_match_candidates",
                columns: new[] { "BusinessUnitId", "LeadId", "Rank" },
                unique: true);

            // Tenant isolation in the DATABASE, not only in EF. The application role gets
            // row-level security plus the four verbs it actually needs (candidates are
            // rewritten in place on every resolution pass), and USAGE — never SELECT/UPDATE —
            // on the identity sequence, so a compromised tenant session cannot read or reset
            // another tenant's id allocation. Mirrors
            // 20260730104456_PilotReadinessDeadLetterOperations.
            migrationBuilder.Sql("""
                ALTER TABLE public.lead_customer_match_candidates ENABLE ROW LEVEL SECURITY;
                ALTER TABLE public.lead_customer_match_candidates FORCE ROW LEVEL SECURITY;

                DO $security$
                DECLARE candidate_sequence text;
                BEGIN
                    IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'nexora_tenant_app') THEN
                        CREATE POLICY nexora_tenant_isolation ON public.lead_customer_match_candidates
                            TO nexora_tenant_app
                            USING ("BusinessUnitId" = NULLIF(current_setting('nexora.business_unit_id', true), '')::bigint)
                            WITH CHECK ("BusinessUnitId" = NULLIF(current_setting('nexora.business_unit_id', true), '')::bigint);
                        REVOKE ALL ON TABLE public.lead_customer_match_candidates FROM nexora_tenant_app;
                        GRANT SELECT, INSERT, UPDATE, DELETE ON TABLE public.lead_customer_match_candidates TO nexora_tenant_app;
                        candidate_sequence := pg_get_serial_sequence('public.lead_customer_match_candidates', 'Id');
                        IF candidate_sequence IS NOT NULL THEN
                            EXECUTE format('REVOKE ALL ON SEQUENCE %s FROM nexora_tenant_app', candidate_sequence);
                            EXECUTE format('GRANT USAGE ON SEQUENCE %s TO nexora_tenant_app', candidate_sequence);
                        END IF;
                    END IF;
                END
                $security$;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "lead_customer_match_candidates");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Leads_CustomerIdentityStatus",
                table: "Leads");

            migrationBuilder.DropIndex(
                name: "IX_customer_identifiers_learned_from_lead",
                table: "customer_identifiers");

            migrationBuilder.DropColumn(
                name: "CustomerBuyerEmailExtracted",
                table: "Leads");

            migrationBuilder.DropColumn(
                name: "CustomerCompanyEvidence",
                table: "Leads");

            migrationBuilder.DropColumn(
                name: "CustomerCompanyNameExtracted",
                table: "Leads");

            migrationBuilder.DropColumn(
                name: "CustomerCompanyRegistrationId",
                table: "Leads");

            migrationBuilder.DropColumn(
                name: "CustomerMatchConfidence",
                table: "Leads");

            migrationBuilder.DropColumn(
                name: "CustomerMatchExplanation",
                table: "Leads");

            migrationBuilder.DropColumn(
                name: "CustomerMatchReasonCode",
                table: "Leads");

            migrationBuilder.DropColumn(
                name: "CustomerMatchedOn",
                table: "Leads");

            migrationBuilder.DropColumn(
                name: "CustomerPortalNameExtracted",
                table: "Leads");

            migrationBuilder.DropColumn(
                name: "SupplierAccountRefOnDocument",
                table: "Leads");

            migrationBuilder.DropColumn(
                name: "SupplierNameOnDocument",
                table: "Leads");

            migrationBuilder.DropColumn(
                name: "LastObservedOn",
                table: "customer_identifiers");

            migrationBuilder.DropColumn(
                name: "LearnedFromLeadId",
                table: "customer_identifiers");

            migrationBuilder.DropColumn(
                name: "LearnedFromReviewAuditId",
                table: "customer_identifiers");

            migrationBuilder.DropColumn(
                name: "ObservationCount",
                table: "customer_identifiers");

            // Narrowing back would truncate any 33+ character status, so the rows that can
            // only exist at the wider width are normalised to their pre-vocabulary meaning
            // first. Down must be survivable, not merely reversible on paper.
            migrationBuilder.Sql("""
                UPDATE "Leads" SET "CustomerMatchStatus" = 'CUSTOMER_CONFIRMED'
                WHERE length("CustomerMatchStatus") > 32;
                """);

            migrationBuilder.AlterColumn<string>(
                name: "CustomerMatchStatus",
                table: "Leads",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "UNRESOLVED",
                oldClrType: typeof(string),
                oldType: "character varying(64)",
                oldMaxLength: 64,
                oldNullable: false,
                oldDefaultValue: "UNRESOLVED");
        }
    }
}
