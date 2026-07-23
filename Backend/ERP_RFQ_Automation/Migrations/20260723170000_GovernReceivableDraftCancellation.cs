using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ERP_RFQ_Automation.Migrations
{
    /// <inheritdoc />
    public partial class GovernReceivableDraftCancellation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_ReceivableDocuments_Issue",
                table: "ReceivableDocuments");

            migrationBuilder.AddColumn<string>(
                name: "VoidedBy",
                table: "ReceivableDocuments",
                type: "character varying(255)",
                maxLength: 255,
                nullable: true);

            migrationBuilder.Sql("""
                UPDATE public."ReceivableDocuments"
                SET "VoidedOn" = NULL, "VoidReason" = NULL, "VoidedBy" = NULL
                WHERE "Status" = 'Draft'
                  AND ("VoidedOn" IS NOT NULL OR "VoidReason" IS NOT NULL);
                """);

            migrationBuilder.AddCheckConstraint(
                name: "CK_ReceivableDocuments_Issue",
                table: "ReceivableDocuments",
                sql: "(\"Status\" = 'Draft' AND \"DocumentNumber\" IS NULL AND \"IssuedOn\" IS NULL AND \"VoidedOn\" IS NULL AND \"VoidReason\" IS NULL AND \"VoidedBy\" IS NULL) OR (\"Status\" = 'Cancelled' AND \"DocumentNumber\" IS NULL AND \"IssuedOn\" IS NULL AND \"VoidedOn\" IS NOT NULL AND \"VoidReason\" IS NOT NULL AND length(trim(\"VoidReason\")) > 0 AND \"VoidedBy\" IS NOT NULL AND length(trim(\"VoidedBy\")) > 0) OR (\"Status\" IN ('Issued', 'Void') AND \"DocumentNumber\" IS NOT NULL AND \"IssuedOn\" IS NOT NULL)");

            migrationBuilder.Sql("""
                CREATE OR REPLACE FUNCTION public.nexora_receivable_issued_immutable()
                RETURNS trigger LANGUAGE plpgsql AS $function$
                DECLARE legal_sequence bigint;
                DECLARE fiscal_year integer;
                DECLARE number_prefix text;
                DECLARE line_count integer;
                DECLARE line_subtotal numeric(18,2);
                DECLARE line_discount numeric(18,2);
                DECLARE line_tax numeric(18,2);
                DECLARE line_total numeric(18,2);
                BEGIN
                    IF TG_OP = 'DELETE' THEN
                        RAISE EXCEPTION 'receivable documents cannot be deleted' USING ERRCODE = '55000';
                    END IF;
                    IF OLD."Status" IN ('Issued', 'Void', 'Cancelled') THEN
                        RAISE EXCEPTION 'finalized receivable documents are immutable' USING ERRCODE = '55000';
                    END IF;

                    IF OLD."Status" = 'Draft' AND NEW."Status" = 'Issued' THEN
                        IF NEW."IssuedOn" IS NULL OR NEW."IssuedBy" IS NULL OR length(trim(NEW."IssuedBy")) = 0
                           OR NEW."Version" <> OLD."Version" + 1
                           OR NEW."VoidedOn" IS NOT NULL OR NEW."VoidReason" IS NOT NULL OR NEW."VoidedBy" IS NOT NULL
                           OR (NEW."BusinessUnitId", NEW."CommercialCaseId", NEW."CustomerId", NEW."OrderId",
                               NEW."ParentDocumentId", NEW."CurrencyId", NEW."DocumentType", NEW."DocumentDate",
                               NEW."DueDate", NEW."SubTotal", NEW."DiscountAmount", NEW."TaxAmount", NEW."TotalAmount",
                               NEW."IdempotencyKey", NEW."RequestHash", NEW."CreatedBy", NEW."CreatedOn")
                              IS DISTINCT FROM
                              (OLD."BusinessUnitId", OLD."CommercialCaseId", OLD."CustomerId", OLD."OrderId",
                               OLD."ParentDocumentId", OLD."CurrencyId", OLD."DocumentType", OLD."DocumentDate",
                               OLD."DueDate", OLD."SubTotal", OLD."DiscountAmount", OLD."TaxAmount", OLD."TotalAmount",
                               OLD."IdempotencyKey", OLD."RequestHash", OLD."CreatedBy", OLD."CreatedOn") THEN
                            RAISE EXCEPTION 'invalid governed receivable issue transition' USING ERRCODE = '55000';
                        END IF;

                        IF NEW."OrderId" IS NULL THEN
                            RAISE EXCEPTION 'an invoice must reference its source order' USING ERRCODE = '23514';
                        END IF;
                        PERFORM 1 FROM public."Orders" sales_order
                        WHERE sales_order."ID" = NEW."OrderId"
                          AND sales_order."BusinessUnitID" = NEW."BusinessUnitId"
                        FOR UPDATE;
                        IF NOT FOUND THEN
                            RAISE EXCEPTION 'the tenant source order does not exist' USING ERRCODE = '23503';
                        END IF;
                        IF NOT EXISTS (
                            SELECT 1
                            FROM public."Orders" sales_order
                            JOIN public."Setup_Master" order_status
                              ON order_status."SetupID" = sales_order."StatusID"
                             AND order_status."BusinessUnitID" = sales_order."BusinessUnitID"
                            LEFT JOIN public."Quotes" quote
                              ON quote."ID" = sales_order."QuoteID"
                             AND quote."BusinessUnitID" = sales_order."BusinessUnitID"
                            LEFT JOIN public."Setup_Master" quote_status
                              ON quote_status."SetupID" = quote."StatusID"
                             AND quote_status."BusinessUnitID" = quote."BusinessUnitID"
                            WHERE sales_order."ID" = NEW."OrderId"
                              AND sales_order."BusinessUnitID" = NEW."BusinessUnitId"
                              AND sales_order."IsActive"
                              AND (upper(coalesce(order_status."SetupCode", order_status."SetupValue", ''))
                                     IN ('CONFIRMED', 'COMPLETED', 'SHIPPED', 'DELIVERED')
                                   OR upper(coalesce(quote_status."SetupCode", quote_status."SetupValue", ''))
                                     IN ('ACCEPTED', 'ORDERED'))) THEN
                            RAISE EXCEPTION 'the source order is not eligible for invoicing' USING ERRCODE = '23514';
                        END IF;

                        SELECT count(*)::integer,
                               round(coalesce(sum(round(line."Quantity" * line."UnitPrice", 2)), 0), 2),
                               round(coalesce(sum(line."DiscountAmount"), 0), 2),
                               round(coalesce(sum(line."TaxAmount"), 0), 2),
                               round(coalesce(sum(line."LineTotal"), 0), 2)
                        INTO line_count, line_subtotal, line_discount, line_tax, line_total
                        FROM public."ReceivableDocumentLines" line
                        WHERE line."BusinessUnitId" = NEW."BusinessUnitId"
                          AND line."ReceivableDocumentId" = NEW."Id";
                        IF line_count = 0 OR NEW."TotalAmount" <= 0
                           OR line_subtotal <> NEW."SubTotal" OR line_discount <> NEW."DiscountAmount"
                           OR line_tax <> NEW."TaxAmount" OR line_total <> NEW."TotalAmount"
                           OR EXISTS (
                               SELECT 1 FROM public."ReceivableDocumentLines" line
                               WHERE line."BusinessUnitId" = NEW."BusinessUnitId"
                                 AND line."ReceivableDocumentId" = NEW."Id"
                                 AND line."LineTotal" <> round(
                                     round(line."Quantity" * line."UnitPrice", 2)
                                     - line."DiscountAmount" + line."TaxAmount", 2)) THEN
                            RAISE EXCEPTION 'receivable lines and header do not reconcile' USING ERRCODE = '23514';
                        END IF;

                        IF EXISTS (
                            SELECT 1
                            FROM public."ReceivableDocumentLines" line
                            LEFT JOIN public."OrderItems" order_line ON order_line."ID" = line."OrderItemId"
                            WHERE line."BusinessUnitId" = NEW."BusinessUnitId"
                              AND line."ReceivableDocumentId" = NEW."Id"
                              AND (line."OrderItemId" IS NULL OR order_line."ID" IS NULL
                                   OR order_line."OrderID" <> NEW."OrderId"
                                   OR line."Quantity" + coalesce((
                                       SELECT sum(prior_line."Quantity")
                                       FROM public."ReceivableDocumentLines" prior_line
                                       JOIN public."ReceivableDocuments" prior_document
                                         ON prior_document."Id" = prior_line."ReceivableDocumentId"
                                        AND prior_document."BusinessUnitId" = prior_line."BusinessUnitId"
                                       WHERE prior_line."BusinessUnitId" = NEW."BusinessUnitId"
                                         AND prior_line."OrderItemId" = line."OrderItemId"
                                         AND prior_document."Id" <> NEW."Id"
                                         AND prior_document."Status" = 'Issued'), 0) > order_line."Quantity")) THEN
                            RAISE EXCEPTION 'issuing would exceed or detach a source order line' USING ERRCODE = '23514';
                        END IF;

                        fiscal_year := extract(year from NEW."DocumentDate")::integer;
                        number_prefix := CASE NEW."DocumentType"
                            WHEN 'Invoice' THEN 'INV' WHEN 'CreditNote' THEN 'CRN'
                            WHEN 'DebitNote' THEN 'DBN' ELSE 'RCT' END;
                        INSERT INTO public."LegalDocumentCounters"
                            ("BusinessUnitId", "DocumentType", "FiscalYear", "NextNumber")
                        VALUES (NEW."BusinessUnitId", NEW."DocumentType", fiscal_year, 2)
                        ON CONFLICT ("BusinessUnitId", "DocumentType", "FiscalYear")
                        DO UPDATE SET "NextNumber" = public."LegalDocumentCounters"."NextNumber" + 1
                        RETURNING "NextNumber" - 1 INTO legal_sequence;
                        NEW."DocumentNumber" := format('%s-%s-%s', number_prefix, fiscal_year,
                            lpad(legal_sequence::text, 6, '0'));

                        INSERT INTO public."CommercialFinanceAudits"
                            ("BusinessUnitId", "AggregateType", "AggregateId", "Action", "Actor", "OccurredOn", "DetailJson")
                        VALUES (NEW."BusinessUnitId", 'ReceivableDocument', NEW."Id", 'Issued', NEW."IssuedBy",
                            NEW."IssuedOn", jsonb_build_object('number', NEW."DocumentNumber"));
                        RETURN NEW;
                    END IF;

                    IF OLD."Status" = 'Draft' AND NEW."Status" = 'Cancelled' THEN
                        IF NEW."DocumentNumber" IS NOT NULL OR NEW."IssuedOn" IS NOT NULL
                           OR NEW."VoidedOn" IS NULL OR NEW."VoidReason" IS NULL OR length(trim(NEW."VoidReason")) = 0
                           OR NEW."VoidedBy" IS NULL OR length(trim(NEW."VoidedBy")) = 0 OR NEW."Version" <> OLD."Version" + 1
                           OR (NEW."BusinessUnitId", NEW."CommercialCaseId", NEW."CustomerId", NEW."OrderId",
                               NEW."ParentDocumentId", NEW."CurrencyId", NEW."DocumentType", NEW."DocumentDate",
                               NEW."DueDate", NEW."SubTotal", NEW."DiscountAmount", NEW."TaxAmount", NEW."TotalAmount",
                               NEW."IdempotencyKey", NEW."RequestHash", NEW."CreatedBy", NEW."CreatedOn", NEW."IssuedBy")
                              IS DISTINCT FROM
                              (OLD."BusinessUnitId", OLD."CommercialCaseId", OLD."CustomerId", OLD."OrderId",
                               OLD."ParentDocumentId", OLD."CurrencyId", OLD."DocumentType", OLD."DocumentDate",
                               OLD."DueDate", OLD."SubTotal", OLD."DiscountAmount", OLD."TaxAmount", OLD."TotalAmount",
                               OLD."IdempotencyKey", OLD."RequestHash", OLD."CreatedBy", OLD."CreatedOn", OLD."IssuedBy") THEN
                            RAISE EXCEPTION 'invalid governed receivable cancellation transition' USING ERRCODE = '55000';
                        END IF;

                        INSERT INTO public."CommercialFinanceAudits"
                            ("BusinessUnitId", "AggregateType", "AggregateId", "Action", "Actor", "OccurredOn", "DetailJson")
                        VALUES (NEW."BusinessUnitId", 'ReceivableDocument', NEW."Id", 'DraftCancelled', NEW."VoidedBy",
                            NEW."VoidedOn", jsonb_build_object('reason', NEW."VoidReason"));
                        RETURN NEW;
                    END IF;

                    IF NEW."Status" IS DISTINCT FROM OLD."Status" THEN
                        RAISE EXCEPTION 'invalid receivable document status transition' USING ERRCODE = '55000';
                    END IF;
                    RETURN NEW;
                END
                $function$;

                CREATE OR REPLACE FUNCTION public.nexora_receivable_line_issued_immutable()
                RETURNS trigger LANGUAGE plpgsql AS $function$
                BEGIN
                    IF EXISTS (
                        SELECT 1 FROM public."ReceivableDocuments" document
                        WHERE document."Id" = OLD."ReceivableDocumentId"
                          AND document."BusinessUnitId" = OLD."BusinessUnitId"
                          AND document."Status" IN ('Issued', 'Void', 'Cancelled')) THEN
                        RAISE EXCEPTION 'finalized receivable document lines are immutable' USING ERRCODE = '55000';
                    END IF;
                    RETURN CASE WHEN TG_OP = 'DELETE' THEN OLD ELSE NEW END;
                END
                $function$;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                CREATE OR REPLACE FUNCTION public.nexora_receivable_issued_immutable()
                RETURNS trigger LANGUAGE plpgsql AS $function$
                BEGIN
                    IF OLD."Status" IN ('Issued', 'Void') THEN
                        RAISE EXCEPTION 'issued receivable documents are immutable' USING ERRCODE = '55000';
                    END IF;
                    RETURN CASE WHEN TG_OP = 'DELETE' THEN OLD ELSE NEW END;
                END
                $function$;

                CREATE OR REPLACE FUNCTION public.nexora_receivable_line_issued_immutable()
                RETURNS trigger LANGUAGE plpgsql AS $function$
                BEGIN
                    IF EXISTS (
                        SELECT 1 FROM public."ReceivableDocuments" document
                        WHERE document."Id" = OLD."ReceivableDocumentId"
                          AND document."BusinessUnitId" = OLD."BusinessUnitId"
                          AND document."Status" IN ('Issued', 'Void')) THEN
                        RAISE EXCEPTION 'issued receivable document lines are immutable' USING ERRCODE = '55000';
                    END IF;
                    RETURN CASE WHEN TG_OP = 'DELETE' THEN OLD ELSE NEW END;
                END
                $function$;

                UPDATE public."ReceivableDocuments"
                SET "Status" = 'Draft', "VoidedOn" = NULL, "VoidReason" = NULL, "VoidedBy" = NULL,
                    "Version" = "Version" + 1
                WHERE "Status" = 'Cancelled';
                """);

            migrationBuilder.DropCheckConstraint(
                name: "CK_ReceivableDocuments_Issue",
                table: "ReceivableDocuments");

            migrationBuilder.DropColumn(
                name: "VoidedBy",
                table: "ReceivableDocuments");

            migrationBuilder.AddCheckConstraint(
                name: "CK_ReceivableDocuments_Issue",
                table: "ReceivableDocuments",
                sql: "(\"Status\" = 'Draft' AND \"DocumentNumber\" IS NULL AND \"IssuedOn\" IS NULL) OR (\"Status\" IN ('Issued', 'Void') AND \"DocumentNumber\" IS NOT NULL AND \"IssuedOn\" IS NOT NULL)");
        }
    }
}
