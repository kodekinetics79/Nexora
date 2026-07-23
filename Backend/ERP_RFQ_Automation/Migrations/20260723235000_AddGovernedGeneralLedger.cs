using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace ERP_RFQ_Automation.Migrations
{
    /// <inheritdoc />
    public partial class AddGovernedGeneralLedger : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AccountingPeriods",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    BusinessUnitId = table.Column<long>(type: "bigint", nullable: false),
                    FiscalYear = table.Column<int>(type: "integer", nullable: false),
                    PeriodNumber = table.Column<int>(type: "integer", nullable: false),
                    Name = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    StartsOn = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    EndsOn = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    IdempotencyKey = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    RequestHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false),
                    CreatedBy = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    CreatedOn = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    SoftClosedBy = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    SoftClosedOn = table.Column<DateTime>(type: "timestamp without time zone", nullable: true),
                    ClosedBy = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    ClosedOn = table.Column<DateTime>(type: "timestamp without time zone", nullable: true),
                    ReopenedBy = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    ReopenedOn = table.Column<DateTime>(type: "timestamp without time zone", nullable: true),
                    ReopenReason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    ReopenEvidenceReference = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AccountingPeriods", x => x.Id);
                    table.UniqueConstraint("AK_AccountingPeriods_BusinessUnitId_Id", x => new { x.BusinessUnitId, x.Id });
                    table.CheckConstraint("CK_AccountingPeriods_Range", "\"FiscalYear\" BETWEEN 2000 AND 2200 AND \"PeriodNumber\" BETWEEN 1 AND 99 AND \"StartsOn\" <= \"EndsOn\"");
                });

            migrationBuilder.CreateTable(
                name: "LedgerAccounts",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    BusinessUnitId = table.Column<long>(type: "bigint", nullable: false),
                    Code = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    Name = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    Category = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    NormalBalance = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    CurrencyId = table.Column<long>(type: "bigint", nullable: true),
                    IsControlAccount = table.Column<bool>(type: "boolean", nullable: false),
                    AllowsManualPosting = table.Column<bool>(type: "boolean", nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    IdempotencyKey = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    RequestHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false),
                    CreatedBy = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    CreatedOn = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    DeactivatedBy = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    DeactivatedOn = table.Column<DateTime>(type: "timestamp without time zone", nullable: true),
                    DeactivationReason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LedgerAccounts", x => x.Id);
                    table.UniqueConstraint("AK_LedgerAccounts_BusinessUnitId_Id", x => new { x.BusinessUnitId, x.Id });
                    table.CheckConstraint("CK_LedgerAccounts_State", "((\"Category\" IN ('Asset','Expense') AND \"NormalBalance\" = 'Debit') OR (\"Category\" IN ('Liability','Equity','Revenue') AND \"NormalBalance\" = 'Credit')) AND ((\"IsActive\" AND \"DeactivatedBy\" IS NULL AND \"DeactivatedOn\" IS NULL AND \"DeactivationReason\" IS NULL) OR (NOT \"IsActive\" AND \"DeactivatedBy\" IS NOT NULL AND \"DeactivatedOn\" IS NOT NULL AND length(trim(\"DeactivationReason\")) >= 20))");
                    table.ForeignKey(
                        name: "FK_LedgerAccounts_Currency_CurrencyId",
                        column: x => x.CurrencyId,
                        principalTable: "Currency",
                        principalColumn: "ID",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "JournalEntries",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    BusinessUnitId = table.Column<long>(type: "bigint", nullable: false),
                    AccountingPeriodId = table.Column<long>(type: "bigint", nullable: false),
                    FunctionalCurrencyId = table.Column<long>(type: "bigint", nullable: false),
                    EntryNumber = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    AccountingDate = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Description = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    SourceType = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    SourceReference = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    SourceVersion = table.Column<long>(type: "bigint", nullable: true),
                    TotalDebit = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    TotalCredit = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    ReversesJournalEntryId = table.Column<long>(type: "bigint", nullable: true),
                    IdempotencyKey = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    RequestHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false),
                    CreatedBy = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    CreatedOn = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    PostedBy = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    PostedOn = table.Column<DateTime>(type: "timestamp without time zone", nullable: true),
                    CancelledBy = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    CancelledOn = table.Column<DateTime>(type: "timestamp without time zone", nullable: true),
                    CancellationReason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    ReversedBy = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    ReversedOn = table.Column<DateTime>(type: "timestamp without time zone", nullable: true),
                    ReversalReason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    ReversalEvidenceReference = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_JournalEntries", x => x.Id);
                    table.UniqueConstraint("AK_JournalEntries_BusinessUnitId_Id", x => new { x.BusinessUnitId, x.Id });
                    table.CheckConstraint("CK_JournalEntries_Totals", "\"TotalDebit\" > 0 AND \"TotalDebit\" = \"TotalCredit\"");
                    table.ForeignKey(
                        name: "FK_JournalEntries_AccountingPeriods_BusinessUnitId_AccountingP~",
                        columns: x => new { x.BusinessUnitId, x.AccountingPeriodId },
                        principalTable: "AccountingPeriods",
                        principalColumns: new[] { "BusinessUnitId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_JournalEntries_Currency_FunctionalCurrencyId",
                        column: x => x.FunctionalCurrencyId,
                        principalTable: "Currency",
                        principalColumn: "ID",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_JournalEntries_JournalEntries_BusinessUnitId_ReversesJourna~",
                        columns: x => new { x.BusinessUnitId, x.ReversesJournalEntryId },
                        principalTable: "JournalEntries",
                        principalColumns: new[] { "BusinessUnitId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "JournalEntryLines",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    BusinessUnitId = table.Column<long>(type: "bigint", nullable: false),
                    JournalEntryId = table.Column<long>(type: "bigint", nullable: false),
                    Sequence = table.Column<int>(type: "integer", nullable: false),
                    LedgerAccountId = table.Column<long>(type: "bigint", nullable: false),
                    Description = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    TransactionCurrencyId = table.Column<long>(type: "bigint", nullable: false),
                    ExchangeRate = table.Column<decimal>(type: "numeric(20,10)", precision: 20, scale: 10, nullable: false),
                    TransactionDebit = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    TransactionCredit = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    FunctionalDebit = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    FunctionalCredit = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    SourceReference = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_JournalEntryLines", x => x.Id);
                    table.CheckConstraint("CK_JournalEntryLines_Amounts", "\"Sequence\" > 0 AND CAST(\"ExchangeRate\" AS NUMERIC) > 0 AND CAST(\"TransactionDebit\" AS NUMERIC) >= 0 AND CAST(\"TransactionCredit\" AS NUMERIC) >= 0 AND CAST(\"FunctionalDebit\" AS NUMERIC) >= 0 AND CAST(\"FunctionalCredit\" AS NUMERIC) >= 0 AND ((CAST(\"TransactionDebit\" AS NUMERIC) > 0 AND CAST(\"TransactionCredit\" AS NUMERIC) = 0 AND CAST(\"FunctionalDebit\" AS NUMERIC) > 0 AND CAST(\"FunctionalCredit\" AS NUMERIC) = 0) OR (CAST(\"TransactionCredit\" AS NUMERIC) > 0 AND CAST(\"TransactionDebit\" AS NUMERIC) = 0 AND CAST(\"FunctionalCredit\" AS NUMERIC) > 0 AND CAST(\"FunctionalDebit\" AS NUMERIC) = 0))");
                    table.ForeignKey(
                        name: "FK_JournalEntryLines_Currency_TransactionCurrencyId",
                        column: x => x.TransactionCurrencyId,
                        principalTable: "Currency",
                        principalColumn: "ID",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_JournalEntryLines_JournalEntries_BusinessUnitId_JournalEntr~",
                        columns: x => new { x.BusinessUnitId, x.JournalEntryId },
                        principalTable: "JournalEntries",
                        principalColumns: new[] { "BusinessUnitId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_JournalEntryLines_LedgerAccounts_BusinessUnitId_LedgerAccou~",
                        columns: x => new { x.BusinessUnitId, x.LedgerAccountId },
                        principalTable: "LedgerAccounts",
                        principalColumns: new[] { "BusinessUnitId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "UX_AccountingPeriods_BU_Idempotency",
                table: "AccountingPeriods",
                columns: new[] { "BusinessUnitId", "IdempotencyKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UX_AccountingPeriods_BU_Year_Period",
                table: "AccountingPeriods",
                columns: new[] { "BusinessUnitId", "FiscalYear", "PeriodNumber" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_JournalEntries_BusinessUnitId_AccountingPeriodId",
                table: "JournalEntries",
                columns: new[] { "BusinessUnitId", "AccountingPeriodId" });

            migrationBuilder.CreateIndex(
                name: "IX_JournalEntries_FunctionalCurrencyId",
                table: "JournalEntries",
                column: "FunctionalCurrencyId");

            migrationBuilder.CreateIndex(
                name: "UX_JournalEntries_BU_Idempotency",
                table: "JournalEntries",
                columns: new[] { "BusinessUnitId", "IdempotencyKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UX_JournalEntries_BU_Number",
                table: "JournalEntries",
                columns: new[] { "BusinessUnitId", "EntryNumber" },
                unique: true,
                filter: "\"EntryNumber\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "UX_JournalEntries_BU_Reversal",
                table: "JournalEntries",
                columns: new[] { "BusinessUnitId", "ReversesJournalEntryId" },
                unique: true,
                filter: "\"ReversesJournalEntryId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "UX_JournalEntries_BU_SourceVersion",
                table: "JournalEntries",
                columns: new[] { "BusinessUnitId", "SourceType", "SourceReference", "SourceVersion" },
                unique: true,
                filter: "\"SourceReference\" IS NOT NULL AND \"ReversesJournalEntryId\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_JournalEntryLines_BusinessUnitId_LedgerAccountId",
                table: "JournalEntryLines",
                columns: new[] { "BusinessUnitId", "LedgerAccountId" });

            migrationBuilder.CreateIndex(
                name: "IX_JournalEntryLines_TransactionCurrencyId",
                table: "JournalEntryLines",
                column: "TransactionCurrencyId");

            migrationBuilder.CreateIndex(
                name: "UX_JournalEntryLines_BU_Journal_Sequence",
                table: "JournalEntryLines",
                columns: new[] { "BusinessUnitId", "JournalEntryId", "Sequence" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_LedgerAccounts_CurrencyId",
                table: "LedgerAccounts",
                column: "CurrencyId");

            migrationBuilder.CreateIndex(
                name: "UX_LedgerAccounts_BU_Code",
                table: "LedgerAccounts",
                columns: new[] { "BusinessUnitId", "Code" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UX_LedgerAccounts_BU_Idempotency",
                table: "LedgerAccounts",
                columns: new[] { "BusinessUnitId", "IdempotencyKey" },
                unique: true);

            migrationBuilder.Sql("""
                ALTER TABLE public."AccountingPeriods"
                    ADD CONSTRAINT "CK_AccountingPeriods_Status" CHECK ("Status" IN ('Open','SoftClosed','Closed'));
                ALTER TABLE public."JournalEntries"
                    ADD CONSTRAINT "CK_JournalEntries_State" CHECK (
                        "Status" IN ('Draft','Posted','Cancelled','Reversed')
                        AND "SourceType" IN ('Manual','JournalReversal')
                        AND (("Status" = 'Draft' AND "EntryNumber" IS NULL AND "PostedBy" IS NULL AND "PostedOn" IS NULL
                              AND "CancelledBy" IS NULL AND "CancelledOn" IS NULL AND "ReversedBy" IS NULL AND "ReversedOn" IS NULL)
                          OR ("Status" = 'Posted' AND "EntryNumber" IS NOT NULL AND "PostedBy" IS NOT NULL AND "PostedOn" IS NOT NULL
                              AND "CancelledBy" IS NULL AND "CancelledOn" IS NULL AND "ReversedBy" IS NULL AND "ReversedOn" IS NULL)
                          OR ("Status" = 'Cancelled' AND "EntryNumber" IS NULL AND "PostedBy" IS NULL AND "PostedOn" IS NULL
                              AND "CancelledBy" IS NOT NULL AND "CancelledOn" IS NOT NULL AND length(trim("CancellationReason")) >= 20)
                          OR ("Status" = 'Reversed' AND "EntryNumber" IS NOT NULL AND "PostedBy" IS NOT NULL AND "PostedOn" IS NOT NULL
                              AND "ReversedBy" IS NOT NULL AND "ReversedOn" IS NOT NULL
                              AND length(trim("ReversalReason")) >= 20 AND length(trim("ReversalEvidenceReference")) >= 8)));

                CREATE OR REPLACE FUNCTION public.nexora_gl_authenticated_actor(business_unit_id bigint)
                RETURNS text LANGUAGE plpgsql SECURITY DEFINER
                SET search_path = pg_catalog, public AS $function$
                DECLARE actor_id text;
                DECLARE actor_signature text;
                DECLARE actor_secret text;
                BEGIN
                    actor_id := NULLIF(current_setting('nexora.actor_id', true), '');
                    actor_signature := NULLIF(current_setting('nexora.actor_signature', true), '');
                    SELECT "Secret" INTO actor_secret FROM public."FinanceProviderSecrets" WHERE "Name" = 'AuditActor';
                    IF actor_id IS NULL OR actor_signature IS NULL OR actor_secret IS NULL
                       OR actor_signature <> encode(hmac(convert_to(business_unit_id::text || E'\n' || actor_id, 'UTF8'),
                            convert_to(actor_secret, 'UTF8'), 'sha256'), 'hex') THEN
                        RAISE EXCEPTION 'a signed authenticated ledger actor is required' USING ERRCODE = '42501';
                    END IF;
                    RETURN actor_id;
                END
                $function$;

                CREATE OR REPLACE FUNCTION public.nexora_gl_guard_account()
                RETURNS trigger LANGUAGE plpgsql SECURITY DEFINER
                SET search_path = pg_catalog, public AS $function$
                DECLARE actor_id text;
                BEGIN
                    IF TG_OP = 'DELETE' THEN
                        RAISE EXCEPTION 'ledger accounts cannot be deleted' USING ERRCODE = '55000';
                    END IF;
                    IF current_setting('role', true) = 'nexora_tenant_app' THEN
                        actor_id := public.nexora_gl_authenticated_actor(COALESCE(NEW."BusinessUnitId", OLD."BusinessUnitId"));
                    END IF;
                    IF TG_OP = 'INSERT' THEN
                        IF NEW."Version" <> 1 OR NOT NEW."IsActive" OR NEW."DeactivatedBy" IS NOT NULL
                           OR (actor_id IS NOT NULL AND NEW."CreatedBy" <> actor_id) THEN
                            RAISE EXCEPTION 'invalid initial ledger account state' USING ERRCODE = '55000';
                        END IF;
                        IF NEW."CurrencyId" IS NOT NULL AND NOT EXISTS (
                            SELECT 1 FROM public."Currency" c WHERE c."ID" = NEW."CurrencyId"
                              AND c."BusinessUnitID" = NEW."BusinessUnitId" AND c."IsActive" IS TRUE) THEN
                            RAISE EXCEPTION 'ledger account currency must belong to the tenant and be active' USING ERRCODE = '23514';
                        END IF;
                        RETURN NEW;
                    END IF;
                    IF NEW."BusinessUnitId" <> OLD."BusinessUnitId" OR NEW."Id" <> OLD."Id"
                       OR NEW."Code" <> OLD."Code" OR NEW."Name" <> OLD."Name"
                       OR NEW."Category" <> OLD."Category" OR NEW."NormalBalance" <> OLD."NormalBalance"
                       OR NEW."CurrencyId" IS DISTINCT FROM OLD."CurrencyId"
                       OR NEW."IsControlAccount" <> OLD."IsControlAccount"
                       OR NEW."AllowsManualPosting" <> OLD."AllowsManualPosting"
                       OR NEW."IdempotencyKey" <> OLD."IdempotencyKey" OR NEW."RequestHash" <> OLD."RequestHash"
                       OR NEW."CreatedBy" <> OLD."CreatedBy" OR NEW."CreatedOn" <> OLD."CreatedOn"
                       OR NOT OLD."IsActive" OR NEW."IsActive" OR NEW."Version" <> OLD."Version" + 1
                       OR NEW."DeactivatedBy" IS NULL OR NEW."DeactivatedOn" IS NULL
                       OR length(trim(NEW."DeactivationReason")) < 20
                       OR (actor_id IS NOT NULL AND NEW."DeactivatedBy" <> actor_id) THEN
                        RAISE EXCEPTION 'ledger account changes require the governed deactivation transition' USING ERRCODE = '55000';
                    END IF;
                    RETURN NEW;
                END
                $function$;

                CREATE OR REPLACE FUNCTION public.nexora_gl_guard_period()
                RETURNS trigger LANGUAGE plpgsql SECURITY DEFINER
                SET search_path = pg_catalog, public AS $function$
                DECLARE actor_id text;
                BEGIN
                    IF TG_OP = 'DELETE' THEN
                        RAISE EXCEPTION 'accounting periods cannot be deleted' USING ERRCODE = '55000';
                    END IF;
                    IF current_setting('role', true) = 'nexora_tenant_app' THEN
                        actor_id := public.nexora_gl_authenticated_actor(COALESCE(NEW."BusinessUnitId", OLD."BusinessUnitId"));
                    END IF;
                    IF TG_OP = 'INSERT' THEN
                        PERFORM pg_advisory_xact_lock(hashtextextended('nexora:gl-period:' || NEW."BusinessUnitId"::text, 0));
                        IF NEW."Status" <> 'Open' OR NEW."Version" <> 1
                           OR NEW."SoftClosedBy" IS NOT NULL OR NEW."ClosedBy" IS NOT NULL OR NEW."ReopenedBy" IS NOT NULL
                           OR (actor_id IS NOT NULL AND NEW."CreatedBy" <> actor_id) THEN
                            RAISE EXCEPTION 'invalid initial accounting period state' USING ERRCODE = '55000';
                        END IF;
                        IF EXISTS (SELECT 1 FROM public."AccountingPeriods" p
                            WHERE p."BusinessUnitId" = NEW."BusinessUnitId" AND p."StartsOn" <= NEW."EndsOn"
                              AND p."EndsOn" >= NEW."StartsOn") THEN
                            RAISE EXCEPTION 'accounting periods cannot overlap' USING ERRCODE = '23P01';
                        END IF;
                        IF EXISTS (SELECT 1 FROM public."AccountingPeriods" p
                            WHERE p."BusinessUnitId" = NEW."BusinessUnitId" AND p."Status" = 'Closed'
                              AND p."EndsOn" >= NEW."StartsOn") THEN
                            RAISE EXCEPTION 'periods cannot be inserted before or within a certified close horizon' USING ERRCODE = '55000';
                        END IF;
                        RETURN NEW;
                    END IF;
                    IF NEW."BusinessUnitId" <> OLD."BusinessUnitId" OR NEW."Id" <> OLD."Id"
                       OR NEW."FiscalYear" <> OLD."FiscalYear" OR NEW."PeriodNumber" <> OLD."PeriodNumber"
                       OR NEW."Name" <> OLD."Name" OR NEW."StartsOn" <> OLD."StartsOn" OR NEW."EndsOn" <> OLD."EndsOn"
                       OR NEW."IdempotencyKey" <> OLD."IdempotencyKey" OR NEW."RequestHash" <> OLD."RequestHash"
                       OR NEW."CreatedBy" <> OLD."CreatedBy" OR NEW."CreatedOn" <> OLD."CreatedOn"
                       OR NEW."Version" <> OLD."Version" + 1 THEN
                        RAISE EXCEPTION 'accounting period identity and dates are immutable' USING ERRCODE = '55000';
                    END IF;
                    IF OLD."Status" = 'Open' AND NEW."Status" = 'SoftClosed' THEN
                        PERFORM pg_advisory_xact_lock(hashtextextended('nexora:gl-period:' || NEW."BusinessUnitId"::text
                            || ':' || NEW."Id"::text, 0));
                        IF NEW."SoftClosedBy" IS NULL OR NEW."SoftClosedOn" IS NULL
                           OR NEW."SoftClosedBy" = OLD."CreatedBy"
                           OR (actor_id IS NOT NULL AND NEW."SoftClosedBy" <> actor_id)
                           OR EXISTS (SELECT 1 FROM public."JournalEntries" j WHERE j."BusinessUnitId" = NEW."BusinessUnitId"
                                AND j."AccountingPeriodId" = NEW."Id" AND j."Status" = 'Draft')
                           OR NEW."ClosedBy" IS DISTINCT FROM OLD."ClosedBy" OR NEW."ClosedOn" IS DISTINCT FROM OLD."ClosedOn"
                           OR NEW."ReopenedBy" IS DISTINCT FROM OLD."ReopenedBy" OR NEW."ReopenedOn" IS DISTINCT FROM OLD."ReopenedOn"
                           OR NEW."ReopenReason" IS DISTINCT FROM OLD."ReopenReason"
                           OR NEW."ReopenEvidenceReference" IS DISTINCT FROM OLD."ReopenEvidenceReference" THEN
                            RAISE EXCEPTION 'period soft close requires an independent actor and no draft journals' USING ERRCODE = '55000';
                        END IF;
                    ELSIF OLD."Status" = 'SoftClosed' AND NEW."Status" = 'Closed' THEN
                        PERFORM pg_advisory_xact_lock(hashtextextended('nexora:gl-period:' || NEW."BusinessUnitId"::text, 0));
                        PERFORM pg_advisory_xact_lock(hashtextextended('nexora:gl-period:' || NEW."BusinessUnitId"::text
                            || ':' || NEW."Id"::text, 0));
                        IF NEW."ClosedBy" IS NULL OR NEW."ClosedOn" IS NULL OR NEW."ClosedBy" IN (OLD."CreatedBy", OLD."SoftClosedBy")
                           OR (actor_id IS NOT NULL AND NEW."ClosedBy" <> actor_id)
                           OR EXISTS (SELECT 1 FROM public."AccountingPeriods" p WHERE p."BusinessUnitId" = NEW."BusinessUnitId"
                                AND p."EndsOn" < NEW."StartsOn" AND p."Status" <> 'Closed')
                           OR EXISTS (SELECT 1 FROM public."JournalEntries" j WHERE j."BusinessUnitId" = NEW."BusinessUnitId"
                                AND j."AccountingPeriodId" = NEW."Id" AND j."Status" = 'Draft')
                           OR NEW."SoftClosedBy" IS DISTINCT FROM OLD."SoftClosedBy"
                           OR NEW."SoftClosedOn" IS DISTINCT FROM OLD."SoftClosedOn"
                           OR NEW."ReopenedBy" IS DISTINCT FROM OLD."ReopenedBy" OR NEW."ReopenedOn" IS DISTINCT FROM OLD."ReopenedOn"
                           OR NEW."ReopenReason" IS DISTINCT FROM OLD."ReopenReason"
                           OR NEW."ReopenEvidenceReference" IS DISTINCT FROM OLD."ReopenEvidenceReference" THEN
                            RAISE EXCEPTION 'period close requires an independent controller and all preceding periods closed' USING ERRCODE = '55000';
                        END IF;
                    ELSIF OLD."Status" = 'SoftClosed' AND NEW."Status" = 'Open' THEN
                        IF NEW."ReopenedBy" IS NULL OR NEW."ReopenedOn" IS NULL OR NEW."ReopenedBy" = OLD."SoftClosedBy"
                           OR length(trim(NEW."ReopenReason")) < 20 OR length(trim(NEW."ReopenEvidenceReference")) < 8
                           OR (actor_id IS NOT NULL AND NEW."ReopenedBy" <> actor_id)
                           OR NEW."SoftClosedBy" IS DISTINCT FROM OLD."SoftClosedBy"
                           OR NEW."SoftClosedOn" IS DISTINCT FROM OLD."SoftClosedOn"
                           OR NEW."ClosedBy" IS DISTINCT FROM OLD."ClosedBy" OR NEW."ClosedOn" IS DISTINCT FROM OLD."ClosedOn" THEN
                            RAISE EXCEPTION 'period reopening requires independent approval, reason and evidence' USING ERRCODE = '55000';
                        END IF;
                    ELSE
                        RAISE EXCEPTION 'unsupported accounting period transition' USING ERRCODE = '55000';
                    END IF;
                    RETURN NEW;
                END
                $function$;

                CREATE OR REPLACE FUNCTION public.nexora_gl_guard_journal()
                RETURNS trigger LANGUAGE plpgsql SECURITY DEFINER
                SET search_path = pg_catalog, public AS $function$
                DECLARE actor_id text;
                DECLARE allocated_number bigint;
                DECLARE fiscal_year integer;
                BEGIN
                    IF TG_OP = 'DELETE' THEN
                        RAISE EXCEPTION 'journals cannot be deleted' USING ERRCODE = '55000';
                    END IF;
                    IF current_setting('role', true) = 'nexora_tenant_app' THEN
                        actor_id := public.nexora_gl_authenticated_actor(COALESCE(NEW."BusinessUnitId", OLD."BusinessUnitId"));
                    END IF;
                    IF TG_OP = 'INSERT' THEN
                        PERFORM pg_advisory_xact_lock(hashtextextended('nexora:gl-period:' || NEW."BusinessUnitId"::text
                            || ':' || NEW."AccountingPeriodId"::text, 0));
                        IF NEW."Status" <> 'Draft' OR NEW."EntryNumber" IS NOT NULL OR NEW."Version" <> 1
                           OR (NEW."SourceType" = 'Manual' AND actor_id IS NOT NULL AND NEW."CreatedBy" <> actor_id)
                           OR (NEW."SourceType" = 'JournalReversal' AND (NEW."ReversesJournalEntryId" IS NULL
                               OR NEW."CreatedBy" <> 'system:journal-reversal')) THEN
                            RAISE EXCEPTION 'invalid initial journal state' USING ERRCODE = '55000';
                        END IF;
                        IF NOT EXISTS (SELECT 1 FROM public."AccountingPeriods" p WHERE p."BusinessUnitId" = NEW."BusinessUnitId"
                            AND p."Id" = NEW."AccountingPeriodId" AND p."Status" = 'Open'
                            AND NEW."AccountingDate" BETWEEN p."StartsOn" AND p."EndsOn")
                           OR NOT EXISTS (SELECT 1 FROM public."Currency" c WHERE c."ID" = NEW."FunctionalCurrencyId"
                            AND c."BusinessUnitID" = NEW."BusinessUnitId" AND c."IsActive" IS TRUE) THEN
                            RAISE EXCEPTION 'journal period and currency must belong to the tenant' USING ERRCODE = '23514';
                        END IF;
                        IF NEW."SourceType" = 'JournalReversal' AND NOT EXISTS (
                            SELECT 1 FROM public."JournalEntries" source
                            WHERE source."BusinessUnitId" = NEW."BusinessUnitId"
                              AND source."Id" = NEW."ReversesJournalEntryId" AND source."Status" = 'Posted'
                              AND source."FunctionalCurrencyId" = NEW."FunctionalCurrencyId"
                              AND source."EntryNumber" IS NOT DISTINCT FROM NEW."SourceReference"
                              AND source."Version" = NEW."SourceVersion") THEN
                            RAISE EXCEPTION 'a reversal draft requires the posted source journal identity and version' USING ERRCODE = '23514';
                        END IF;
                        RETURN NEW;
                    END IF;
                    IF NEW."BusinessUnitId" <> OLD."BusinessUnitId" OR NEW."Id" <> OLD."Id"
                       OR NEW."AccountingPeriodId" <> OLD."AccountingPeriodId" OR NEW."FunctionalCurrencyId" <> OLD."FunctionalCurrencyId"
                       OR NEW."AccountingDate" <> OLD."AccountingDate" OR NEW."Description" <> OLD."Description"
                       OR NEW."SourceType" <> OLD."SourceType" OR NEW."SourceReference" IS DISTINCT FROM OLD."SourceReference"
                       OR NEW."SourceVersion" IS DISTINCT FROM OLD."SourceVersion"
                       OR NEW."TotalDebit" <> OLD."TotalDebit" OR NEW."TotalCredit" <> OLD."TotalCredit"
                       OR NEW."ReversesJournalEntryId" IS DISTINCT FROM OLD."ReversesJournalEntryId"
                       OR NEW."IdempotencyKey" <> OLD."IdempotencyKey" OR NEW."RequestHash" <> OLD."RequestHash"
                       OR NEW."CreatedBy" <> OLD."CreatedBy" OR NEW."CreatedOn" <> OLD."CreatedOn"
                       OR NEW."Version" <> OLD."Version" + 1 THEN
                        RAISE EXCEPTION 'journal accounting content is immutable' USING ERRCODE = '55000';
                    END IF;
                    IF OLD."Status" = 'Draft' AND NEW."Status" = 'Posted' THEN
                        PERFORM pg_advisory_xact_lock(hashtextextended('nexora:gl-period:' || NEW."BusinessUnitId"::text
                            || ':' || NEW."AccountingPeriodId"::text, 0));
                        IF NEW."PostedBy" IS NULL OR NEW."PostedOn" IS NULL OR NEW."PostedBy" = OLD."CreatedBy"
                           OR (actor_id IS NOT NULL AND NEW."PostedBy" <> actor_id)
                           OR NEW."CancelledBy" IS DISTINCT FROM OLD."CancelledBy" OR NEW."CancelledOn" IS DISTINCT FROM OLD."CancelledOn"
                           OR NEW."CancellationReason" IS DISTINCT FROM OLD."CancellationReason"
                           OR NEW."ReversedBy" IS DISTINCT FROM OLD."ReversedBy" OR NEW."ReversedOn" IS DISTINCT FROM OLD."ReversedOn"
                           OR NEW."ReversalReason" IS DISTINCT FROM OLD."ReversalReason"
                           OR NEW."ReversalEvidenceReference" IS DISTINCT FROM OLD."ReversalEvidenceReference" THEN
                            RAISE EXCEPTION 'journal posting requires an independent authenticated actor' USING ERRCODE = '55000';
                        END IF;
                        SELECT p."FiscalYear" INTO STRICT fiscal_year FROM public."AccountingPeriods" p
                        WHERE p."BusinessUnitId" = NEW."BusinessUnitId" AND p."Id" = NEW."AccountingPeriodId";
                        INSERT INTO public."LegalDocumentCounters" ("BusinessUnitId", "DocumentType", "FiscalYear", "NextNumber")
                        VALUES (NEW."BusinessUnitId", 'Journal', fiscal_year, 2)
                        ON CONFLICT ("BusinessUnitId", "DocumentType", "FiscalYear")
                        DO UPDATE SET "NextNumber" = public."LegalDocumentCounters"."NextNumber" + 1
                        RETURNING "NextNumber" - 1 INTO allocated_number;
                        NEW."EntryNumber" := 'JRN-' || fiscal_year::text || '-'
                            || lpad(allocated_number::text, 8, '0');
                    ELSIF OLD."Status" = 'Draft' AND NEW."Status" = 'Cancelled' THEN
                        IF NEW."CancelledBy" IS NULL OR NEW."CancelledOn" IS NULL OR length(trim(NEW."CancellationReason")) < 20
                           OR (actor_id IS NOT NULL AND NEW."CancelledBy" <> actor_id)
                           OR NEW."EntryNumber" IS DISTINCT FROM OLD."EntryNumber"
                           OR NEW."PostedBy" IS DISTINCT FROM OLD."PostedBy" OR NEW."PostedOn" IS DISTINCT FROM OLD."PostedOn"
                           OR NEW."ReversedBy" IS DISTINCT FROM OLD."ReversedBy" OR NEW."ReversedOn" IS DISTINCT FROM OLD."ReversedOn"
                           OR NEW."ReversalReason" IS DISTINCT FROM OLD."ReversalReason"
                           OR NEW."ReversalEvidenceReference" IS DISTINCT FROM OLD."ReversalEvidenceReference" THEN
                            RAISE EXCEPTION 'journal cancellation requires an authenticated actor and reason' USING ERRCODE = '55000';
                        END IF;
                    ELSIF OLD."Status" = 'Posted' AND NEW."Status" = 'Reversed' THEN
                        IF NEW."ReversedBy" IS NULL OR NEW."ReversedOn" IS NULL
                           OR NEW."ReversedBy" IN (OLD."CreatedBy", OLD."PostedBy")
                           OR length(trim(NEW."ReversalReason")) < 20 OR length(trim(NEW."ReversalEvidenceReference")) < 8
                           OR (actor_id IS NOT NULL AND NEW."ReversedBy" <> actor_id)
                           OR NEW."EntryNumber" IS DISTINCT FROM OLD."EntryNumber"
                           OR NEW."PostedBy" IS DISTINCT FROM OLD."PostedBy" OR NEW."PostedOn" IS DISTINCT FROM OLD."PostedOn"
                           OR NEW."CancelledBy" IS DISTINCT FROM OLD."CancelledBy" OR NEW."CancelledOn" IS DISTINCT FROM OLD."CancelledOn"
                           OR NEW."CancellationReason" IS DISTINCT FROM OLD."CancellationReason" THEN
                            RAISE EXCEPTION 'journal reversal requires an independent authenticated controller' USING ERRCODE = '55000';
                        END IF;
                    ELSE
                        RAISE EXCEPTION 'unsupported journal transition' USING ERRCODE = '55000';
                    END IF;
                    RETURN NEW;
                END
                $function$;

                CREATE OR REPLACE FUNCTION public.nexora_gl_guard_line()
                RETURNS trigger LANGUAGE plpgsql SECURITY DEFINER
                SET search_path = pg_catalog, public AS $function$
                DECLARE actor_id text;
                BEGIN
                    IF TG_OP <> 'INSERT' THEN
                        RAISE EXCEPTION 'journal lines are append-only and cannot be changed or deleted' USING ERRCODE = '55000';
                    END IF;
                    IF current_setting('role', true) = 'nexora_tenant_app' THEN
                        actor_id := public.nexora_gl_authenticated_actor(NEW."BusinessUnitId");
                    END IF;
                    IF NOT EXISTS (SELECT 1 FROM public."JournalEntries" j WHERE j."BusinessUnitId" = NEW."BusinessUnitId"
                           AND j."Id" = NEW."JournalEntryId" AND j."Status" = 'Draft')
                       OR NOT EXISTS (SELECT 1 FROM public."LedgerAccounts" a WHERE a."BusinessUnitId" = NEW."BusinessUnitId"
                           AND a."Id" = NEW."LedgerAccountId" AND a."IsActive"
                           AND (a."CurrencyId" IS NULL OR a."CurrencyId" = NEW."TransactionCurrencyId"))
                       OR NOT EXISTS (SELECT 1 FROM public."Currency" c WHERE c."ID" = NEW."TransactionCurrencyId"
                           AND c."BusinessUnitID" = NEW."BusinessUnitId" AND c."IsActive" IS TRUE) THEN
                        RAISE EXCEPTION 'journal line references must be active tenant records' USING ERRCODE = '23514';
                    END IF;
                    RETURN NEW;
                END
                $function$;

                CREATE OR REPLACE FUNCTION public.nexora_gl_validate_posting()
                RETURNS trigger LANGUAGE plpgsql SECURITY DEFINER
                SET search_path = pg_catalog, public AS $function$
                DECLARE line_count integer;
                DECLARE debit_total numeric(18,2);
                DECLARE credit_total numeric(18,2);
                DECLARE invalid_accounts integer;
                DECLARE invalid_currency_balances integer;
                DECLARE invalid_exchange_amounts integer;
                DECLARE mismatch_count integer;
                BEGIN
                    IF NEW."Status" = 'Posted' AND OLD."Status" = 'Draft' THEN
                        SELECT count(*), COALESCE(sum(l."FunctionalDebit"), 0), COALESCE(sum(l."FunctionalCredit"), 0),
                               count(*) FILTER (WHERE NOT a."IsActive" OR (NEW."SourceType" = 'Manual'
                                   AND (NOT a."AllowsManualPosting" OR a."IsControlAccount"))
                                   OR (a."CurrencyId" IS NOT NULL AND a."CurrencyId" <> l."TransactionCurrencyId"))
                        INTO line_count, debit_total, credit_total, invalid_accounts
                        FROM public."JournalEntryLines" l JOIN public."LedgerAccounts" a
                          ON a."BusinessUnitId" = l."BusinessUnitId" AND a."Id" = l."LedgerAccountId"
                        WHERE l."BusinessUnitId" = NEW."BusinessUnitId" AND l."JournalEntryId" = NEW."Id";
                        IF line_count < 2 OR debit_total <= 0 OR debit_total <> credit_total
                           OR debit_total <> NEW."TotalDebit" OR credit_total <> NEW."TotalCredit" OR invalid_accounts > 0
                           OR NOT EXISTS (SELECT 1 FROM public."AccountingPeriods" p WHERE p."BusinessUnitId" = NEW."BusinessUnitId"
                                AND p."Id" = NEW."AccountingPeriodId" AND p."Status" = 'Open'
                                AND NEW."AccountingDate" BETWEEN p."StartsOn" AND p."EndsOn")
                           OR NOT EXISTS (SELECT 1 FROM public."Currency" c
                                WHERE c."BusinessUnitID" = NEW."BusinessUnitId"
                                  AND c."ID" = NEW."FunctionalCurrencyId" AND c."IsActive" IS TRUE) THEN
                            RAISE EXCEPTION 'journal posting failed balance, account, or open-period controls' USING ERRCODE = '23514';
                        END IF;
                        SELECT count(*) INTO invalid_currency_balances FROM (
                            SELECT l."TransactionCurrencyId"
                            FROM public."JournalEntryLines" l
                            WHERE l."BusinessUnitId" = NEW."BusinessUnitId" AND l."JournalEntryId" = NEW."Id"
                            GROUP BY l."TransactionCurrencyId"
                            HAVING sum(l."TransactionDebit") <> sum(l."TransactionCredit")) currency_imbalance;
                        SELECT count(*) INTO invalid_exchange_amounts
                        FROM public."JournalEntryLines" l
                        WHERE l."BusinessUnitId" = NEW."BusinessUnitId" AND l."JournalEntryId" = NEW."Id"
                          AND (round(l."TransactionDebit" * l."ExchangeRate", 2) <> l."FunctionalDebit"
                            OR round(l."TransactionCredit" * l."ExchangeRate", 2) <> l."FunctionalCredit"
                            OR (l."TransactionCurrencyId" = NEW."FunctionalCurrencyId" AND l."ExchangeRate" <> 1)
                            OR NOT EXISTS (SELECT 1 FROM public."Currency" c
                                WHERE c."BusinessUnitID" = l."BusinessUnitId" AND c."ID" = l."TransactionCurrencyId"
                                  AND c."IsActive" IS TRUE));
                        IF invalid_currency_balances > 0 OR invalid_exchange_amounts > 0 THEN
                            RAISE EXCEPTION 'journal transaction currencies and snapshotted exchange amounts must reconcile' USING ERRCODE = '23514';
                        END IF;
                        IF NEW."ReversesJournalEntryId" IS NOT NULL THEN
                            SELECT count(*) INTO mismatch_count FROM (
                                (SELECT "Sequence", "LedgerAccountId", "TransactionCurrencyId", "ExchangeRate",
                                    "TransactionCredit" AS debit, "TransactionDebit" AS credit,
                                    "FunctionalCredit" AS fdebit, "FunctionalDebit" AS fcredit
                                 FROM public."JournalEntryLines" WHERE "BusinessUnitId" = NEW."BusinessUnitId"
                                   AND "JournalEntryId" = NEW."ReversesJournalEntryId"
                                 EXCEPT
                                 SELECT "Sequence", "LedgerAccountId", "TransactionCurrencyId", "ExchangeRate",
                                    "TransactionDebit", "TransactionCredit", "FunctionalDebit", "FunctionalCredit"
                                 FROM public."JournalEntryLines" WHERE "BusinessUnitId" = NEW."BusinessUnitId"
                                   AND "JournalEntryId" = NEW."Id")
                                UNION ALL
                                (SELECT "Sequence", "LedgerAccountId", "TransactionCurrencyId", "ExchangeRate",
                                    "TransactionDebit", "TransactionCredit", "FunctionalDebit", "FunctionalCredit"
                                 FROM public."JournalEntryLines" WHERE "BusinessUnitId" = NEW."BusinessUnitId"
                                   AND "JournalEntryId" = NEW."Id"
                                 EXCEPT
                                 SELECT "Sequence", "LedgerAccountId", "TransactionCurrencyId", "ExchangeRate",
                                    "TransactionCredit", "TransactionDebit", "FunctionalCredit", "FunctionalDebit"
                                 FROM public."JournalEntryLines" WHERE "BusinessUnitId" = NEW."BusinessUnitId"
                                   AND "JournalEntryId" = NEW."ReversesJournalEntryId")) differences;
                            IF mismatch_count > 0 THEN
                                RAISE EXCEPTION 'reversal journal must exactly negate every original line' USING ERRCODE = '23514';
                            END IF;
                        END IF;
                    ELSIF NEW."Status" = 'Reversed' AND OLD."Status" = 'Posted' THEN
                        IF NOT EXISTS (SELECT 1 FROM public."JournalEntries" r WHERE r."BusinessUnitId" = NEW."BusinessUnitId"
                            AND r."ReversesJournalEntryId" = NEW."Id" AND r."Status" = 'Posted') THEN
                            RAISE EXCEPTION 'a posted exact reversal is required before marking a journal reversed' USING ERRCODE = '23514';
                        END IF;
                    END IF;
                    RETURN NULL;
                END
                $function$;

                CREATE OR REPLACE FUNCTION public.nexora_gl_evidence_event()
                RETURNS trigger LANGUAGE plpgsql SECURITY DEFINER
                SET search_path = pg_catalog, public AS $function$
                DECLARE aggregate_type text;
                DECLARE aggregate_version bigint;
                DECLARE action_name text;
                DECLARE event_name text;
                DECLARE actor_id text;
                DECLARE occurred_at timestamp without time zone;
                DECLARE payload jsonb;
                DECLARE event_id uuid;
                BEGIN
                    aggregate_type := CASE TG_TABLE_NAME WHEN 'LedgerAccounts' THEN 'LedgerAccount'
                        WHEN 'AccountingPeriods' THEN 'AccountingPeriod' ELSE 'JournalEntry' END;
                    aggregate_version := NEW."Version";
                    action_name := CASE WHEN TG_OP = 'INSERT' THEN 'Created' ELSE to_jsonb(NEW)->>'Status' END;
                    IF TG_TABLE_NAME = 'LedgerAccounts' AND TG_OP = 'UPDATE' THEN action_name := 'Deactivated'; END IF;
                    actor_id := COALESCE(NULLIF(current_setting('nexora.actor_id', true), ''),
                        to_jsonb(NEW)->>'PostedBy', to_jsonb(NEW)->>'CreatedBy', 'system:ledger');
                    occurred_at := clock_timestamp() AT TIME ZONE 'UTC';
                    payload := to_jsonb(NEW);
                    event_name := 'finance.' || lower(aggregate_type) || '.' || lower(action_name);
                    event_id := (substr(md5(NEW."BusinessUnitId"::text || ':' || aggregate_type || ':' || NEW."Id"::text
                        || ':' || aggregate_version::text || ':' || event_name), 1, 8) || '-' ||
                        substr(md5(NEW."BusinessUnitId"::text || ':' || aggregate_type || ':' || NEW."Id"::text
                        || ':' || aggregate_version::text || ':' || event_name), 9, 4) || '-4' ||
                        substr(md5(NEW."BusinessUnitId"::text || ':' || aggregate_type || ':' || NEW."Id"::text
                        || ':' || aggregate_version::text || ':' || event_name), 14, 3) || '-a' ||
                        substr(md5(NEW."BusinessUnitId"::text || ':' || aggregate_type || ':' || NEW."Id"::text
                        || ':' || aggregate_version::text || ':' || event_name), 18, 3) || '-' ||
                        substr(md5(NEW."BusinessUnitId"::text || ':' || aggregate_type || ':' || NEW."Id"::text
                        || ':' || aggregate_version::text || ':' || event_name), 21, 12))::uuid;
                    INSERT INTO public."CommercialFinanceAudits" ("BusinessUnitId", "AggregateType", "AggregateId",
                        "Action", "Actor", "OccurredOn", "DetailJson")
                    VALUES (NEW."BusinessUnitId", aggregate_type, NEW."Id", action_name, actor_id, occurred_at, payload);
                    INSERT INTO public."FinanceOutboxMessages" ("BusinessUnitId", "EventId", "AggregateType", "AggregateId",
                        "AggregateVersion", "EventType", "Payload", "SchemaVersion", "OccurredOn", "AvailableOn", "AttemptCount")
                    VALUES (NEW."BusinessUnitId", event_id, aggregate_type, NEW."Id", aggregate_version, event_name,
                        payload, 1, occurred_at, occurred_at, 0);
                    RETURN NULL;
                END
                $function$;

                CREATE OR REPLACE FUNCTION public.nexora_finance_reject_truncate()
                RETURNS trigger LANGUAGE plpgsql AS $function$
                BEGIN
                    RAISE EXCEPTION 'governed finance tables cannot be truncated' USING ERRCODE = '55000';
                END
                $function$;

                CREATE TRIGGER trg_ledgeraccounts_guard BEFORE INSERT OR UPDATE OR DELETE ON public."LedgerAccounts"
                    FOR EACH ROW EXECUTE FUNCTION public.nexora_gl_guard_account();
                CREATE TRIGGER trg_accountingperiods_guard BEFORE INSERT OR UPDATE OR DELETE ON public."AccountingPeriods"
                    FOR EACH ROW EXECUTE FUNCTION public.nexora_gl_guard_period();
                CREATE TRIGGER trg_journalentries_guard BEFORE INSERT OR UPDATE OR DELETE ON public."JournalEntries"
                    FOR EACH ROW EXECUTE FUNCTION public.nexora_gl_guard_journal();
                CREATE TRIGGER trg_journalentrylines_guard BEFORE INSERT OR UPDATE OR DELETE ON public."JournalEntryLines"
                    FOR EACH ROW EXECUTE FUNCTION public.nexora_gl_guard_line();
                CREATE CONSTRAINT TRIGGER trg_journalentries_validate AFTER UPDATE ON public."JournalEntries"
                    DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION public.nexora_gl_validate_posting();
                CREATE CONSTRAINT TRIGGER trg_ledgeraccounts_evidence AFTER INSERT OR UPDATE ON public."LedgerAccounts"
                    DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION public.nexora_gl_evidence_event();
                CREATE CONSTRAINT TRIGGER trg_accountingperiods_evidence AFTER INSERT OR UPDATE ON public."AccountingPeriods"
                    DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION public.nexora_gl_evidence_event();
                CREATE CONSTRAINT TRIGGER trg_journalentries_evidence AFTER INSERT OR UPDATE ON public."JournalEntries"
                    DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION public.nexora_gl_evidence_event();
                CREATE TRIGGER trg_ledgeraccounts_reject_truncate BEFORE TRUNCATE ON public."LedgerAccounts"
                    FOR EACH STATEMENT EXECUTE FUNCTION public.nexora_finance_reject_truncate();
                CREATE TRIGGER trg_accountingperiods_reject_truncate BEFORE TRUNCATE ON public."AccountingPeriods"
                    FOR EACH STATEMENT EXECUTE FUNCTION public.nexora_finance_reject_truncate();
                CREATE TRIGGER trg_journalentries_reject_truncate BEFORE TRUNCATE ON public."JournalEntries"
                    FOR EACH STATEMENT EXECUTE FUNCTION public.nexora_finance_reject_truncate();
                CREATE TRIGGER trg_journalentrylines_reject_truncate BEFORE TRUNCATE ON public."JournalEntryLines"
                    FOR EACH STATEMENT EXECUTE FUNCTION public.nexora_finance_reject_truncate();

                DO $block$
                DECLARE governed_table text;
                BEGIN
                    FOREACH governed_table IN ARRAY ARRAY['LedgerAccounts','AccountingPeriods','JournalEntries','JournalEntryLines'] LOOP
                        EXECUTE format('ALTER TABLE public.%I ENABLE ROW LEVEL SECURITY', governed_table);
                        EXECUTE format('ALTER TABLE public.%I FORCE ROW LEVEL SECURITY', governed_table);
                        EXECUTE format('CREATE POLICY nexora_tenant_isolation ON public.%I TO nexora_tenant_app USING ("BusinessUnitId" = NULLIF(current_setting(''nexora.business_unit_id'', true), '''')::bigint) WITH CHECK ("BusinessUnitId" = NULLIF(current_setting(''nexora.business_unit_id'', true), '''')::bigint)', governed_table);
                    END LOOP;
                    GRANT SELECT, INSERT, UPDATE ON public."LedgerAccounts", public."AccountingPeriods", public."JournalEntries" TO nexora_tenant_app;
                    GRANT SELECT, INSERT ON public."JournalEntryLines" TO nexora_tenant_app;
                    REVOKE DELETE, TRUNCATE ON public."LedgerAccounts", public."AccountingPeriods", public."JournalEntries", public."JournalEntryLines" FROM nexora_tenant_app;
                    GRANT USAGE ON SEQUENCE public."LedgerAccounts_Id_seq", public."AccountingPeriods_Id_seq",
                        public."JournalEntries_Id_seq", public."JournalEntryLines_Id_seq" TO nexora_tenant_app;
                    REVOKE INSERT, UPDATE, DELETE, TRUNCATE ON public."LegalDocumentCounters" FROM nexora_tenant_app;
                END
                $block$;

                INSERT INTO public."Module" ("ModuleName", "Description", "IsActive", "CreatedBy", "CreatedOn") VALUES
                    ('General Ledger', 'Governed chart of accounts, journals, reversals and trial balance', true, 'migration:general-ledger:v1', now()),
                    ('Accounting Periods', 'Maker-checker fiscal period close controls', true, 'migration:general-ledger:v1', now())
                ON CONFLICT ("ModuleName") DO NOTHING;
                INSERT INTO public."RolePermissions"
                    ("RoleID", "ModuleID", "BusinessUnitID", "CanCreate", "CanEdit", "CanDelete", "CreatedBy", "CreatedOn")
                SELECT role."SetupID", module."ID", role."BusinessUnitID", true, true, false, 'migration:general-ledger:v1', now()
                FROM public."Setup_Master" role CROSS JOIN public."Module" module
                WHERE lower(replace(role."SetupType", ' ', '')) = 'role'
                  AND module."ModuleName" IN ('General Ledger','Accounting Periods')
                  AND (upper(coalesce(role."SetupCode", '')) ~ '(FINANCE|ACCOUNT|ADMIN)'
                    OR upper(coalesce(role."SetupValue", '')) ~ '(FINANCE|ACCOUNT|ADMIN)')
                  AND NOT EXISTS (SELECT 1 FROM public."RolePermissions" existing
                    WHERE existing."RoleID" = role."SetupID" AND existing."BusinessUnitID" = role."BusinessUnitID"
                      AND existing."ModuleID" = module."ID");

                REVOKE ALL ON FUNCTION public.nexora_gl_authenticated_actor(bigint), public.nexora_gl_guard_account(),
                    public.nexora_gl_guard_period(), public.nexora_gl_guard_journal(), public.nexora_gl_guard_line(),
                    public.nexora_gl_validate_posting(), public.nexora_gl_evidence_event() FROM PUBLIC;
                GRANT EXECUTE ON FUNCTION public.nexora_gl_authenticated_actor(bigint), public.nexora_gl_guard_account(),
                    public.nexora_gl_guard_period(), public.nexora_gl_guard_journal(), public.nexora_gl_guard_line(),
                    public.nexora_gl_validate_posting(), public.nexora_gl_evidence_event() TO nexora_tenant_app;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DELETE FROM public."RolePermissions" permissions USING public."Module" module
                WHERE permissions."ModuleID" = module."ID" AND module."ModuleName" IN ('General Ledger','Accounting Periods');
                DELETE FROM public."Module" WHERE "ModuleName" IN ('General Ledger','Accounting Periods');
                DROP FUNCTION IF EXISTS public.nexora_gl_evidence_event() CASCADE;
                DROP FUNCTION IF EXISTS public.nexora_gl_validate_posting() CASCADE;
                DROP FUNCTION IF EXISTS public.nexora_gl_guard_line() CASCADE;
                DROP FUNCTION IF EXISTS public.nexora_gl_guard_journal() CASCADE;
                DROP FUNCTION IF EXISTS public.nexora_gl_guard_period() CASCADE;
                DROP FUNCTION IF EXISTS public.nexora_gl_guard_account() CASCADE;
                DROP FUNCTION IF EXISTS public.nexora_gl_authenticated_actor(bigint) CASCADE;
                """);
            migrationBuilder.DropTable(
                name: "JournalEntryLines");

            migrationBuilder.DropTable(
                name: "JournalEntries");

            migrationBuilder.DropTable(
                name: "LedgerAccounts");

            migrationBuilder.DropTable(
                name: "AccountingPeriods");
        }
    }
}
