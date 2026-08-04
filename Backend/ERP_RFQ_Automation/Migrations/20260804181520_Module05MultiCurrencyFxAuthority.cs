using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace ERP_RFQ_Automation.Migrations
{
    /// <summary>
    /// Multi-currency FX authority for the commercial path.
    ///
    /// Currency.ExchangeRate was a single mutable decimal(18,6) column with no date, no pair, no
    /// approval and no history. It was CRUD'd and filtered but never applied, so quote, order,
    /// lead and dashboard totals summed AED, USD and EUR as bare decimals. This migration adds the
    /// two tables the conversion engine reads:
    ///
    ///   FxRates          effective-dated, approval-gated rates for an ordered currency pair.
    ///                    Rate is numeric(20,10) — the same scale as JournalEntryLine.ExchangeRate
    ///                    in the General Ledger, so a rate cannot change scale crossing modules.
    ///
    ///   FxRateSnapshots  the approved snapshot: the rate actually applied to a given document,
    ///                    frozen at the moment of application. This is what makes "what rate was
    ///                    used on this quote?" answerable with a stable value after the fact —
    ///                    correcting or re-dating an FxRate later cannot restate a converted quote.
    ///
    /// Currency.ExchangeRate is intentionally left in place and untouched for backward
    /// compatibility; it is no longer authoritative for any conversion.
    ///
    /// NOTE ON THE MIGRATION CHAIN: both entity types were already present in the model snapshot
    /// captured by 20260804180919_Module05ProcurementDeadlineAndPoNumberAuthority, which was
    /// generated concurrently from a model that already contained them but did not create the
    /// tables. `ef migrations add` therefore produced an empty diff here, and the Up/Down below are
    /// written by hand to match that snapshot exactly. Without this the snapshot would claim two
    /// tables that no migration ever creates.
    /// </summary>
    public partial class Module05MultiCurrencyFxAuthority : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "FxRates",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    BusinessUnitId = table.Column<long>(type: "bigint", nullable: false),
                    FromCurrencyId = table.Column<long>(type: "bigint", nullable: false),
                    ToCurrencyId = table.Column<long>(type: "bigint", nullable: false),
                    Rate = table.Column<decimal>(type: "numeric(20,10)", precision: 20, scale: 10, nullable: false),
                    EffectiveFrom = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    EffectiveTo = table.Column<DateTime>(type: "timestamp without time zone", nullable: true),
                    Source = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    ApprovedBy = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    ApprovedOn = table.Column<DateTime>(type: "timestamp without time zone", nullable: true),
                    Version = table.Column<long>(type: "bigint", nullable: false),
                    CreatedBy = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    CreatedOn = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    Note = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FxRates", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "FxRateSnapshots",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    BusinessUnitId = table.Column<long>(type: "bigint", nullable: false),
                    DocumentType = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    DocumentId = table.Column<long>(type: "bigint", nullable: false),
                    FromCurrencyId = table.Column<long>(type: "bigint", nullable: false),
                    ToCurrencyId = table.Column<long>(type: "bigint", nullable: false),
                    Rate = table.Column<decimal>(type: "numeric(20,10)", precision: 20, scale: 10, nullable: false),
                    FxRateId = table.Column<long>(type: "bigint", nullable: true),
                    RateEffectiveFrom = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    ResolutionPath = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    AsOf = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    CapturedOn = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    CapturedBy = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FxRateSnapshots", x => x.Id);
                });

            // One rate per (tenant, ordered pair, effective date): a correction is a new window,
            // never an in-place edit of an existing one.
            migrationBuilder.CreateIndex(
                name: "UX_FxRates_BU_Pair_EffectiveFrom",
                table: "FxRates",
                columns: new[] { "BusinessUnitId", "FromCurrencyId", "ToCurrencyId", "EffectiveFrom" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_FxRates_BU_Pair_Status",
                table: "FxRates",
                columns: new[] { "BusinessUnitId", "FromCurrencyId", "ToCurrencyId", "Status" });

            // One frozen rate per (tenant, document, ordered pair) — the idempotency guarantee
            // behind a stable audit answer.
            migrationBuilder.CreateIndex(
                name: "UX_FxRateSnapshots_BU_Document_Pair",
                table: "FxRateSnapshots",
                columns: new[] { "BusinessUnitId", "DocumentType", "DocumentId", "FromCurrencyId", "ToCurrencyId" },
                unique: true);

            // Invariants that data annotations cannot express. These live in the migration rather
            // than the model because the FX module could not add a fluent-configuration splice
            // point to ErpRfqAutomationContext.Tenancy.cs (see Models/ErpRfqAutomationContext.Fx.cs).
            migrationBuilder.Sql("""
                ALTER TABLE public."FxRates"
                    ADD CONSTRAINT "CK_FxRates_Rate" CHECK ("Rate" > 0);
                ALTER TABLE public."FxRates"
                    ADD CONSTRAINT "CK_FxRates_Effective"
                    CHECK ("EffectiveTo" IS NULL OR "EffectiveTo" > "EffectiveFrom");
                ALTER TABLE public."FxRates"
                    ADD CONSTRAINT "CK_FxRates_Pair" CHECK ("FromCurrencyId" <> "ToCurrencyId");
                ALTER TABLE public."FxRates"
                    ADD CONSTRAINT "CK_FxRates_Status"
                    CHECK ("Status" IN ('Pending','Approved','Superseded'));
                -- An approved rate must carry who approved it and when.
                ALTER TABLE public."FxRates"
                    ADD CONSTRAINT "CK_FxRates_Approval"
                    CHECK (("Status" <> 'Approved') OR ("ApprovedBy" IS NOT NULL AND "ApprovedOn" IS NOT NULL));
                ALTER TABLE public."FxRateSnapshots"
                    ADD CONSTRAINT "CK_FxRateSnapshots_Rate" CHECK ("Rate" > 0);
                """);

            // Tenant grants, matching the pattern used by the other tenant-scoped tables.
            // FxConversionService also filters BusinessUnitId explicitly in every query, so
            // isolation does not depend on this alone.
            migrationBuilder.Sql("""
                DO $grant$
                BEGIN
                    IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'nexora_tenant_app') THEN
                        EXECUTE 'GRANT SELECT, INSERT, UPDATE, DELETE ON public."FxRates" TO nexora_tenant_app';
                        EXECUTE 'GRANT SELECT, INSERT, UPDATE, DELETE ON public."FxRateSnapshots" TO nexora_tenant_app';
                    END IF;
                END
                $grant$;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "FxRateSnapshots");
            migrationBuilder.DropTable(name: "FxRates");
        }
    }
}
