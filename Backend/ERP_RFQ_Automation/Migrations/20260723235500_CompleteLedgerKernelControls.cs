using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace ERP_RFQ_Automation.Migrations
{
    /// <inheritdoc />
    public partial class CompleteLedgerKernelControls : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_LedgerAccounts_State",
                table: "LedgerAccounts");

            migrationBuilder.AddColumn<bool>(
                name: "IsContraAccount",
                table: "LedgerAccounts",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "CloseEvidenceReference",
                table: "AccountingPeriods",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "CloseJournalCount",
                table: "AccountingPeriods",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CloseReason",
                table: "AccountingPeriods",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "CloseTotalCredit",
                table: "AccountingPeriods",
                type: "numeric(18,2)",
                precision: 18,
                scale: 2,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "CloseTotalDebit",
                table: "AccountingPeriods",
                type: "numeric(18,2)",
                precision: 18,
                scale: 2,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CloseTrialBalanceHash",
                table: "AccountingPeriods",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "LedgerBooks",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    BusinessUnitId = table.Column<long>(type: "bigint", nullable: false),
                    Name = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    FunctionalCurrencyId = table.Column<long>(type: "bigint", nullable: false),
                    TimeZoneId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    FiscalYearStartMonth = table.Column<int>(type: "integer", nullable: false),
                    IdempotencyKey = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    RequestHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false),
                    CreatedBy = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    CreatedOn = table.Column<DateTime>(type: "timestamp without time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LedgerBooks", x => x.Id);
                    table.UniqueConstraint("AK_LedgerBooks_BusinessUnitId_Id", x => new { x.BusinessUnitId, x.Id });
                    table.CheckConstraint("CK_LedgerBooks_State", "\"FiscalYearStartMonth\" BETWEEN 1 AND 12 AND \"Version\" = 1");
                    table.ForeignKey(
                        name: "FK_LedgerBooks_Currency_FunctionalCurrencyId",
                        column: x => x.FunctionalCurrencyId,
                        principalTable: "Currency",
                        principalColumn: "ID",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.AddCheckConstraint(
                name: "CK_LedgerAccounts_State",
                table: "LedgerAccounts",
                sql: "((\"Category\" IN ('Asset','Expense') AND ((NOT \"IsContraAccount\" AND \"NormalBalance\" = 'Debit') OR (\"IsContraAccount\" AND \"NormalBalance\" = 'Credit'))) OR (\"Category\" IN ('Liability','Equity','Revenue') AND ((NOT \"IsContraAccount\" AND \"NormalBalance\" = 'Credit') OR (\"IsContraAccount\" AND \"NormalBalance\" = 'Debit')))) AND ((\"IsActive\" AND \"DeactivatedBy\" IS NULL AND \"DeactivatedOn\" IS NULL AND \"DeactivationReason\" IS NULL) OR (NOT \"IsActive\" AND \"DeactivatedBy\" IS NOT NULL AND \"DeactivatedOn\" IS NOT NULL AND length(trim(\"DeactivationReason\")) >= 20))");

            migrationBuilder.CreateIndex(
                name: "IX_LedgerBooks_FunctionalCurrencyId",
                table: "LedgerBooks",
                column: "FunctionalCurrencyId");

            migrationBuilder.CreateIndex(
                name: "UX_LedgerBooks_BU",
                table: "LedgerBooks",
                column: "BusinessUnitId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UX_LedgerBooks_BU_Idempotency",
                table: "LedgerBooks",
                columns: new[] { "BusinessUnitId", "IdempotencyKey" },
                unique: true);

            migrationBuilder.Sql("""
                CREATE UNIQUE INDEX IF NOT EXISTS "UX_Currency_BusinessUnitID_ID"
                    ON public."Currency" ("BusinessUnitID", "ID");
                ALTER TABLE public."LedgerAccounts" ADD CONSTRAINT "FK_LedgerAccounts_Currency_Tenant"
                    FOREIGN KEY ("BusinessUnitId", "CurrencyId") REFERENCES public."Currency" ("BusinessUnitID", "ID")
                    ON DELETE RESTRICT;
                ALTER TABLE public."JournalEntries" ADD CONSTRAINT "FK_JournalEntries_Currency_Tenant"
                    FOREIGN KEY ("BusinessUnitId", "FunctionalCurrencyId") REFERENCES public."Currency" ("BusinessUnitID", "ID")
                    ON DELETE RESTRICT;
                ALTER TABLE public."JournalEntryLines" ADD CONSTRAINT "FK_JournalEntryLines_Currency_Tenant"
                    FOREIGN KEY ("BusinessUnitId", "TransactionCurrencyId") REFERENCES public."Currency" ("BusinessUnitID", "ID")
                    ON DELETE RESTRICT;
                ALTER TABLE public."LedgerBooks" ADD CONSTRAINT "FK_LedgerBooks_Currency_Tenant"
                    FOREIGN KEY ("BusinessUnitId", "FunctionalCurrencyId") REFERENCES public."Currency" ("BusinessUnitID", "ID")
                    ON DELETE RESTRICT;
                ALTER TABLE public."AccountingPeriods" ADD CONSTRAINT "CK_AccountingPeriods_CloseEvidence" CHECK (
                    ("Status" = 'Closed' AND "ClosedBy" IS NOT NULL AND "ClosedOn" IS NOT NULL
                        AND length(trim("CloseReason")) >= 20 AND length(trim("CloseEvidenceReference")) >= 8
                        AND "CloseTrialBalanceHash" ~ '^[0-9a-f]{64}$' AND "CloseTotalDebit" >= 0
                        AND "CloseTotalDebit" = "CloseTotalCredit" AND "CloseJournalCount" >= 0)
                    OR ("Status" <> 'Closed' AND "ClosedBy" IS NULL AND "ClosedOn" IS NULL
                        AND "CloseReason" IS NULL AND "CloseEvidenceReference" IS NULL
                        AND "CloseTrialBalanceHash" IS NULL AND "CloseTotalDebit" IS NULL
                        AND "CloseTotalCredit" IS NULL AND "CloseJournalCount" IS NULL));

                CREATE TABLE public."LedgerActorNonces" (
                    "Nonce" uuid PRIMARY KEY,
                    "BusinessUnitId" bigint NOT NULL,
                    "Actor" character varying(255) NOT NULL,
                    "TransactionId" bigint NOT NULL,
                    "ExpiresOn" timestamp with time zone NOT NULL
                );
                CREATE INDEX "IX_LedgerActorNonces_ExpiresOn" ON public."LedgerActorNonces" ("ExpiresOn");
                ALTER TABLE public."LedgerActorNonces" ENABLE ROW LEVEL SECURITY;
                REVOKE ALL ON public."LedgerActorNonces" FROM PUBLIC, nexora_tenant_app;

                CREATE OR REPLACE FUNCTION public.nexora_gl_authenticated_actor(business_unit_id bigint)
                RETURNS text LANGUAGE plpgsql SECURITY DEFINER
                SET search_path = pg_catalog, public AS $function$
                DECLARE actor_id text; DECLARE actor_secret text; DECLARE issued_at bigint;
                DECLARE expires_at bigint; DECLARE nonce_value uuid; DECLARE envelope_signature text;
                DECLARE expected_signature text; DECLARE inserted_count integer;
                BEGIN
                    actor_id := NULLIF(current_setting('nexora.actor_id', true), '');
                    issued_at := NULLIF(current_setting('nexora.gl_issued_at', true), '')::bigint;
                    expires_at := NULLIF(current_setting('nexora.gl_expires_at', true), '')::bigint;
                    nonce_value := NULLIF(current_setting('nexora.gl_nonce', true), '')::uuid;
                    envelope_signature := NULLIF(current_setting('nexora.gl_signature', true), '');
                    SELECT "Secret" INTO actor_secret FROM public."FinanceProviderSecrets" WHERE "Name" = 'AuditActor';
                    IF actor_id IS NULL OR actor_secret IS NULL OR issued_at IS NULL OR expires_at IS NULL
                       OR nonce_value IS NULL OR envelope_signature IS NULL OR expires_at - issued_at > 60
                       OR issued_at > extract(epoch FROM clock_timestamp())::bigint + 5
                       OR expires_at < extract(epoch FROM clock_timestamp())::bigint THEN
                        RAISE EXCEPTION 'a current signed ledger actor envelope is required' USING ERRCODE = '42501';
                    END IF;
                    expected_signature := encode(hmac(convert_to(business_unit_id::text || E'\n' || actor_id || E'\n'
                        || issued_at::text || E'\n' || expires_at::text || E'\n' || nonce_value::text, 'UTF8'),
                        convert_to(actor_secret, 'UTF8'), 'sha256'), 'hex');
                    IF envelope_signature <> expected_signature THEN
                        RAISE EXCEPTION 'the signed ledger actor envelope is invalid' USING ERRCODE = '42501';
                    END IF;
                    DELETE FROM public."LedgerActorNonces" WHERE "ExpiresOn" < clock_timestamp() - interval '5 minutes';
                    INSERT INTO public."LedgerActorNonces" ("Nonce","BusinessUnitId","Actor","TransactionId","ExpiresOn")
                    VALUES (nonce_value,business_unit_id,actor_id,txid_current(),to_timestamp(expires_at))
                    ON CONFLICT ("Nonce") DO NOTHING;
                    GET DIAGNOSTICS inserted_count = ROW_COUNT;
                    IF inserted_count = 0 AND NOT EXISTS (SELECT 1 FROM public."LedgerActorNonces"
                        WHERE "Nonce" = nonce_value AND "BusinessUnitId" = business_unit_id AND "Actor" = actor_id
                          AND "TransactionId" = txid_current()) THEN
                        RAISE EXCEPTION 'the signed ledger actor envelope was already consumed' USING ERRCODE = '42501';
                    END IF;
                    RETURN actor_id;
                END
                $function$;

                CREATE OR REPLACE FUNCTION public.nexora_gl_guard_book()
                RETURNS trigger LANGUAGE plpgsql SECURITY DEFINER
                SET search_path = pg_catalog, public AS $function$
                DECLARE actor_id text;
                BEGIN
                    IF TG_OP <> 'INSERT' THEN
                        RAISE EXCEPTION 'the accounting book is immutable and cannot be changed or deleted' USING ERRCODE = '55000';
                    END IF;
                    IF current_setting('role', true) = 'nexora_tenant_app' THEN
                        actor_id := public.nexora_gl_authenticated_actor(NEW."BusinessUnitId");
                    END IF;
                    IF NEW."Version" <> 1 OR (actor_id IS NOT NULL AND NEW."CreatedBy" <> actor_id)
                       OR NOT EXISTS (SELECT 1 FROM public."Currency" c WHERE c."BusinessUnitID" = NEW."BusinessUnitId"
                            AND c."ID" = NEW."FunctionalCurrencyId" AND c."IsActive" IS TRUE AND c."IsBaseCurrency" IS TRUE) THEN
                        RAISE EXCEPTION 'the accounting book requires the tenant active base currency and authenticated creator' USING ERRCODE = '23514';
                    END IF;
                    RETURN NEW;
                END
                $function$;

                CREATE OR REPLACE FUNCTION public.nexora_gl_enforce_book_currency()
                RETURNS trigger LANGUAGE plpgsql SECURITY DEFINER
                SET search_path = pg_catalog, public AS $function$
                BEGIN
                    IF TG_TABLE_NAME <> 'LedgerBooks' AND NOT EXISTS (
                        SELECT 1 FROM public."LedgerBooks" b WHERE b."BusinessUnitId" = NEW."BusinessUnitId") THEN
                        RAISE EXCEPTION 'a tenant accounting book is required' USING ERRCODE = '23514';
                    END IF;
                    IF TG_TABLE_NAME = 'JournalEntries' AND NOT EXISTS (
                        SELECT 1 FROM public."LedgerBooks" b WHERE b."BusinessUnitId" = NEW."BusinessUnitId"
                          AND b."FunctionalCurrencyId" = (to_jsonb(NEW)->>'FunctionalCurrencyId')::bigint) THEN
                        RAISE EXCEPTION 'journal functional currency must match the immutable accounting book' USING ERRCODE = '23514';
                    END IF;
                    RETURN NEW;
                END
                $function$;

                CREATE OR REPLACE FUNCTION public.nexora_gl_certify_period_close()
                RETURNS trigger LANGUAGE plpgsql SECURITY DEFINER
                SET search_path = pg_catalog, public AS $function$
                DECLARE book_currency bigint;
                DECLARE computed_debit numeric(18,2);
                DECLARE computed_credit numeric(18,2);
                DECLARE computed_count integer;
                DECLARE canonical text;
                BEGIN
                    IF TG_OP = 'INSERT' THEN
                        IF NEW."CloseReason" IS NOT NULL OR NEW."CloseTrialBalanceHash" IS NOT NULL THEN
                            RAISE EXCEPTION 'accounting periods cannot begin with close evidence' USING ERRCODE = '55000';
                        END IF;
                        RETURN NEW;
                    END IF;
                    IF OLD."Status" = 'SoftClosed' AND NEW."Status" = 'Closed' THEN
                        PERFORM pg_advisory_xact_lock(hashtextextended('nexora:gl-period:' || NEW."BusinessUnitId"::text, 0));
                        PERFORM pg_advisory_xact_lock(hashtextextended('nexora:gl-period:' || NEW."BusinessUnitId"::text
                            || ':' || NEW."Id"::text, 0));
                        IF length(trim(NEW."CloseReason")) < 20 OR length(trim(NEW."CloseEvidenceReference")) < 8 THEN
                            RAISE EXCEPTION 'hard close requires reason and evidence' USING ERRCODE = '23514';
                        END IF;
                        SELECT b."FunctionalCurrencyId" INTO STRICT book_currency FROM public."LedgerBooks" b
                            WHERE b."BusinessUnitId" = NEW."BusinessUnitId";
                        SELECT COALESCE(sum(j."TotalDebit"),0), COALESCE(sum(j."TotalCredit"),0), count(*)
                        INTO computed_debit, computed_credit, computed_count
                        FROM public."JournalEntries" j WHERE j."BusinessUnitId" = NEW."BusinessUnitId"
                          AND j."FunctionalCurrencyId" = book_currency AND j."AccountingDate" <= NEW."EndsOn"
                          AND j."Status" IN ('Posted','Reversed');
                        IF computed_debit <> computed_credit THEN
                            RAISE EXCEPTION 'ledger totals do not balance for hard close' USING ERRCODE = '23514';
                        END IF;
                        SELECT COALESCE(string_agg(balance."LedgerAccountId"::text || ':'
                            || to_char(balance.debit, 'FM9999999999999990.00') || ':'
                            || to_char(balance.credit, 'FM9999999999999990.00'), '|' ORDER BY balance."LedgerAccountId"), '')
                        INTO canonical FROM (
                            SELECT line."LedgerAccountId", sum(line."FunctionalDebit") AS debit,
                                sum(line."FunctionalCredit") AS credit
                            FROM public."JournalEntryLines" line JOIN public."JournalEntries" journal
                              ON journal."BusinessUnitId" = line."BusinessUnitId" AND journal."Id" = line."JournalEntryId"
                            WHERE journal."BusinessUnitId" = NEW."BusinessUnitId"
                              AND journal."FunctionalCurrencyId" = book_currency
                              AND journal."AccountingDate" <= NEW."EndsOn" AND journal."Status" IN ('Posted','Reversed')
                            GROUP BY line."LedgerAccountId") balance;
                        NEW."CloseTotalDebit" := computed_debit;
                        NEW."CloseTotalCredit" := computed_credit;
                        NEW."CloseJournalCount" := computed_count;
                        NEW."CloseTrialBalanceHash" := encode(digest(convert_to(canonical, 'UTF8'), 'sha256'), 'hex');
                    ELSIF NEW."CloseReason" IS DISTINCT FROM OLD."CloseReason"
                       OR NEW."CloseEvidenceReference" IS DISTINCT FROM OLD."CloseEvidenceReference"
                       OR NEW."CloseTrialBalanceHash" IS DISTINCT FROM OLD."CloseTrialBalanceHash"
                       OR NEW."CloseTotalDebit" IS DISTINCT FROM OLD."CloseTotalDebit"
                       OR NEW."CloseTotalCredit" IS DISTINCT FROM OLD."CloseTotalCredit"
                       OR NEW."CloseJournalCount" IS DISTINCT FROM OLD."CloseJournalCount" THEN
                        RAISE EXCEPTION 'period close certification evidence is immutable' USING ERRCODE = '55000';
                    END IF;
                    RETURN NEW;
                END
                $function$;

                CREATE OR REPLACE FUNCTION public.nexora_gl_guard_account()
                RETURNS trigger LANGUAGE plpgsql SECURITY DEFINER
                SET search_path = pg_catalog, public AS $function$
                DECLARE actor_id text;
                BEGIN
                    IF TG_OP = 'DELETE' THEN RAISE EXCEPTION 'ledger accounts cannot be deleted' USING ERRCODE = '55000'; END IF;
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
                    IF NEW."BusinessUnitId" <> OLD."BusinessUnitId" OR NEW."Id" <> OLD."Id" OR NEW."Code" <> OLD."Code"
                       OR NEW."Name" <> OLD."Name" OR NEW."Category" <> OLD."Category" OR NEW."NormalBalance" <> OLD."NormalBalance"
                       OR NEW."CurrencyId" IS DISTINCT FROM OLD."CurrencyId" OR NEW."IsControlAccount" <> OLD."IsControlAccount"
                       OR NEW."IsContraAccount" <> OLD."IsContraAccount" OR NEW."AllowsManualPosting" <> OLD."AllowsManualPosting"
                       OR NEW."IdempotencyKey" <> OLD."IdempotencyKey" OR NEW."RequestHash" <> OLD."RequestHash"
                       OR NEW."CreatedBy" <> OLD."CreatedBy" OR NEW."CreatedOn" <> OLD."CreatedOn"
                       OR NOT OLD."IsActive" OR NEW."IsActive" OR NEW."Version" <> OLD."Version" + 1
                       OR NEW."DeactivatedBy" IS NULL OR NEW."DeactivatedOn" IS NULL OR length(trim(NEW."DeactivationReason")) < 20
                       OR (actor_id IS NOT NULL AND NEW."DeactivatedBy" <> actor_id) THEN
                        RAISE EXCEPTION 'ledger account changes require the governed deactivation transition' USING ERRCODE = '55000';
                    END IF;
                    RETURN NEW;
                END
                $function$;

                CREATE OR REPLACE FUNCTION public.nexora_gl_evidence_event()
                RETURNS trigger LANGUAGE plpgsql SECURITY DEFINER
                SET search_path = pg_catalog, public AS $function$
                DECLARE aggregate_type text; DECLARE aggregate_version bigint; DECLARE action_name text;
                DECLARE event_name text; DECLARE actor_id text; DECLARE occurred_at timestamp without time zone;
                DECLARE payload jsonb; DECLARE event_id uuid;
                BEGIN
                    aggregate_type := CASE TG_TABLE_NAME WHEN 'LedgerBooks' THEN 'LedgerBook'
                        WHEN 'LedgerAccounts' THEN 'LedgerAccount' WHEN 'AccountingPeriods' THEN 'AccountingPeriod'
                        ELSE 'JournalEntry' END;
                    aggregate_version := NEW."Version";
                    action_name := CASE WHEN TG_OP = 'INSERT' THEN 'Created' ELSE to_jsonb(NEW)->>'Status' END;
                    IF TG_TABLE_NAME = 'LedgerAccounts' AND TG_OP = 'UPDATE' THEN action_name := 'Deactivated'; END IF;
                    IF TG_TABLE_NAME = 'AccountingPeriods' AND TG_OP = 'UPDATE'
                       AND to_jsonb(OLD)->>'Status' = 'SoftClosed' AND to_jsonb(NEW)->>'Status' = 'Open' THEN
                        action_name := 'Reopened';
                    END IF;
                    actor_id := COALESCE(NULLIF(current_setting('nexora.actor_id', true), ''),
                        to_jsonb(NEW)->>'PostedBy', to_jsonb(NEW)->>'CreatedBy', 'system:ledger');
                    occurred_at := clock_timestamp() AT TIME ZONE 'UTC'; payload := to_jsonb(NEW);
                    event_name := 'finance.' || lower(aggregate_type) || '.' || lower(action_name);
                    event_id := (substr(md5(NEW."BusinessUnitId"::text || ':' || aggregate_type || ':' || NEW."Id"::text || ':' || aggregate_version::text || ':' || event_name),1,8)||'-'||
                        substr(md5(NEW."BusinessUnitId"::text || ':' || aggregate_type || ':' || NEW."Id"::text || ':' || aggregate_version::text || ':' || event_name),9,4)||'-4'||
                        substr(md5(NEW."BusinessUnitId"::text || ':' || aggregate_type || ':' || NEW."Id"::text || ':' || aggregate_version::text || ':' || event_name),14,3)||'-a'||
                        substr(md5(NEW."BusinessUnitId"::text || ':' || aggregate_type || ':' || NEW."Id"::text || ':' || aggregate_version::text || ':' || event_name),18,3)||'-'||
                        substr(md5(NEW."BusinessUnitId"::text || ':' || aggregate_type || ':' || NEW."Id"::text || ':' || aggregate_version::text || ':' || event_name),21,12))::uuid;
                    INSERT INTO public."CommercialFinanceAudits" ("BusinessUnitId","AggregateType","AggregateId","Action","Actor","OccurredOn","DetailJson")
                    VALUES (NEW."BusinessUnitId",aggregate_type,NEW."Id",action_name,actor_id,occurred_at,payload);
                    INSERT INTO public."FinanceOutboxMessages" ("BusinessUnitId","EventId","AggregateType","AggregateId","AggregateVersion","EventType","Payload","SchemaVersion","OccurredOn","AvailableOn","AttemptCount")
                    VALUES (NEW."BusinessUnitId",event_id,aggregate_type,NEW."Id",aggregate_version,event_name,payload,1,occurred_at,occurred_at,0);
                    RETURN NULL;
                END
                $function$;

                CREATE TRIGGER trg_ledgerbooks_guard BEFORE INSERT OR UPDATE OR DELETE ON public."LedgerBooks"
                    FOR EACH ROW EXECUTE FUNCTION public.nexora_gl_guard_book();
                CREATE TRIGGER trg_ledgerbooks_currency BEFORE INSERT ON public."LedgerBooks"
                    FOR EACH ROW EXECUTE FUNCTION public.nexora_gl_enforce_book_currency();
                CREATE TRIGGER trg_accountingperiods_book BEFORE INSERT ON public."AccountingPeriods"
                    FOR EACH ROW EXECUTE FUNCTION public.nexora_gl_enforce_book_currency();
                CREATE TRIGGER trg_journalentries_book BEFORE INSERT OR UPDATE ON public."JournalEntries"
                    FOR EACH ROW EXECUTE FUNCTION public.nexora_gl_enforce_book_currency();
                CREATE TRIGGER trg_accountingperiods_certification BEFORE INSERT OR UPDATE ON public."AccountingPeriods"
                    FOR EACH ROW EXECUTE FUNCTION public.nexora_gl_certify_period_close();
                CREATE CONSTRAINT TRIGGER trg_ledgerbooks_evidence AFTER INSERT ON public."LedgerBooks"
                    DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION public.nexora_gl_evidence_event();
                CREATE TRIGGER trg_ledgerbooks_reject_truncate BEFORE TRUNCATE ON public."LedgerBooks"
                    FOR EACH STATEMENT EXECUTE FUNCTION public.nexora_finance_reject_truncate();
                ALTER TABLE public."LedgerBooks" ENABLE ROW LEVEL SECURITY;
                ALTER TABLE public."LedgerBooks" FORCE ROW LEVEL SECURITY;
                CREATE POLICY nexora_tenant_isolation ON public."LedgerBooks" TO nexora_tenant_app
                    USING ("BusinessUnitId" = NULLIF(current_setting('nexora.business_unit_id', true), '')::bigint)
                    WITH CHECK ("BusinessUnitId" = NULLIF(current_setting('nexora.business_unit_id', true), '')::bigint);
                GRANT SELECT, INSERT ON public."LedgerBooks" TO nexora_tenant_app;
                REVOKE UPDATE, DELETE, TRUNCATE ON public."LedgerBooks" FROM nexora_tenant_app;
                GRANT USAGE ON SEQUENCE public."LedgerBooks_Id_seq" TO nexora_tenant_app;
                REVOKE ALL ON FUNCTION public.nexora_gl_guard_book(), public.nexora_gl_enforce_book_currency(),
                    public.nexora_gl_certify_period_close() FROM PUBLIC;
                REVOKE ALL ON FUNCTION public.nexora_gl_authenticated_actor(bigint) FROM PUBLIC;
                GRANT EXECUTE ON FUNCTION public.nexora_gl_guard_book(), public.nexora_gl_enforce_book_currency(),
                    public.nexora_gl_certify_period_close() TO nexora_tenant_app;
                GRANT EXECUTE ON FUNCTION public.nexora_gl_authenticated_actor(bigint) TO nexora_tenant_app;

                INSERT INTO public."Module" ("ModuleName", "Description", "IsActive", "CreatedBy", "CreatedOn") VALUES
                    ('General Ledger Posting', 'Independent journal posting approval', true, 'migration:general-ledger:v2', now()),
                    ('Period Close', 'Independent accounting period hard-close approval', true, 'migration:general-ledger:v2', now()),
                    ('Ledger Control', 'Controller-only reversals and period reopening', true, 'migration:general-ledger:v2', now())
                ON CONFLICT ("ModuleName") DO NOTHING;
                INSERT INTO public."RolePermissions"
                    ("RoleID", "ModuleID", "BusinessUnitID", "CanCreate", "CanEdit", "CanDelete", "CreatedBy", "CreatedOn")
                SELECT role."SetupID", module."ID", role."BusinessUnitID", false, true, false,
                    'migration:general-ledger:v2', now()
                FROM public."Setup_Master" role CROSS JOIN public."Module" module
                WHERE lower(replace(role."SetupType", ' ', '')) = 'role'
                  AND module."ModuleName" IN ('General Ledger Posting','Period Close','Ledger Control')
                  AND (upper(coalesce(role."SetupCode", '')) ~ '(CONTROLLER|ADMIN)'
                    OR upper(coalesce(role."SetupValue", '')) ~ '(CONTROLLER|ADMIN)'
                    OR (module."ModuleName" = 'General Ledger Posting' AND
                        (upper(coalesce(role."SetupCode", '')) ~ '(FINANCE|ACCOUNT)'
                         OR upper(coalesce(role."SetupValue", '')) ~ '(FINANCE|ACCOUNT)')))
                  AND NOT EXISTS (SELECT 1 FROM public."RolePermissions" existing
                    WHERE existing."RoleID" = role."SetupID" AND existing."BusinessUnitID" = role."BusinessUnitID"
                      AND existing."ModuleID" = module."ID");
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DELETE FROM public."RolePermissions" permissions USING public."Module" module
                WHERE permissions."ModuleID" = module."ID"
                  AND module."ModuleName" IN ('General Ledger Posting','Period Close','Ledger Control');
                DELETE FROM public."Module"
                WHERE "ModuleName" IN ('General Ledger Posting','Period Close','Ledger Control');
                DROP TABLE IF EXISTS public."LedgerActorNonces";
                CREATE OR REPLACE FUNCTION public.nexora_gl_authenticated_actor(business_unit_id bigint)
                RETURNS text LANGUAGE plpgsql SECURITY DEFINER
                SET search_path = pg_catalog, public AS $function$
                DECLARE actor_id text; DECLARE actor_signature text; DECLARE actor_secret text;
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
                ALTER TABLE public."LedgerAccounts" DROP CONSTRAINT IF EXISTS "FK_LedgerAccounts_Currency_Tenant";
                ALTER TABLE public."JournalEntries" DROP CONSTRAINT IF EXISTS "FK_JournalEntries_Currency_Tenant";
                ALTER TABLE public."JournalEntryLines" DROP CONSTRAINT IF EXISTS "FK_JournalEntryLines_Currency_Tenant";
                ALTER TABLE public."LedgerBooks" DROP CONSTRAINT IF EXISTS "FK_LedgerBooks_Currency_Tenant";
                ALTER TABLE public."AccountingPeriods" DROP CONSTRAINT IF EXISTS "CK_AccountingPeriods_CloseEvidence";
                DROP FUNCTION IF EXISTS public.nexora_gl_certify_period_close() CASCADE;
                DROP FUNCTION IF EXISTS public.nexora_gl_enforce_book_currency() CASCADE;
                DROP FUNCTION IF EXISTS public.nexora_gl_guard_book() CASCADE;
                DROP INDEX IF EXISTS public."UX_Currency_BusinessUnitID_ID";
                """);
            migrationBuilder.DropTable(
                name: "LedgerBooks");

            migrationBuilder.DropCheckConstraint(
                name: "CK_LedgerAccounts_State",
                table: "LedgerAccounts");

            migrationBuilder.DropColumn(
                name: "IsContraAccount",
                table: "LedgerAccounts");

            migrationBuilder.DropColumn(
                name: "CloseEvidenceReference",
                table: "AccountingPeriods");

            migrationBuilder.DropColumn(
                name: "CloseJournalCount",
                table: "AccountingPeriods");

            migrationBuilder.DropColumn(
                name: "CloseReason",
                table: "AccountingPeriods");

            migrationBuilder.DropColumn(
                name: "CloseTotalCredit",
                table: "AccountingPeriods");

            migrationBuilder.DropColumn(
                name: "CloseTotalDebit",
                table: "AccountingPeriods");

            migrationBuilder.DropColumn(
                name: "CloseTrialBalanceHash",
                table: "AccountingPeriods");

            migrationBuilder.AddCheckConstraint(
                name: "CK_LedgerAccounts_State",
                table: "LedgerAccounts",
                sql: "((\"Category\" IN ('Asset','Expense') AND \"NormalBalance\" = 'Debit') OR (\"Category\" IN ('Liability','Equity','Revenue') AND \"NormalBalance\" = 'Credit')) AND ((\"IsActive\" AND \"DeactivatedBy\" IS NULL AND \"DeactivatedOn\" IS NULL AND \"DeactivationReason\" IS NULL) OR (NOT \"IsActive\" AND \"DeactivatedBy\" IS NOT NULL AND \"DeactivatedOn\" IS NOT NULL AND length(trim(\"DeactivationReason\")) >= 20))");
        }
    }
}
