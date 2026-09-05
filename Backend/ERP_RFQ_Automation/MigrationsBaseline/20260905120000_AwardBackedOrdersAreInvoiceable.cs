using ERP_RFQ_Automation.Models;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ERP_RFQ_Automation.Migrations
{
    /// <summary>
    /// The database half of "finance can invoice an order the customer accepted through a Client PO".
    ///
    /// <para><c>CommercialFinanceApplicationService.IsInvoiceEligibleOrderAsync</c> and the issue
    /// trigger <c>nexora_receivable_issued_immutable()</c> (squashed baseline, 02_functions.sql) state
    /// the same rule twice: an invoice's order must be CONFIRMED / COMPLETED / SHIPPED / DELIVERED or
    /// backed by an ACCEPTED / ORDERED quote. An order raised from a confirmed Client PO
    /// (<c>CustomerAwardApplicationService.ConvertToOrder</c>) is created DRAFT, locked by its first
    /// shipment, and moved to DELIVERED only when every line is fully accepted — so after one short
    /// delivery the service refused for ever, and once the service admitted award-backed orders the
    /// trigger refused the same document at issue with 23514 "the source order is not eligible for
    /// invoicing" (the API said "The request conflicts with a concurrent or existing financial
    /// record"). The function is redefined with the one extra clause; nothing else in it changes.</para>
    /// </summary>
    [DbContext(typeof(ErpRfqAutomationContext))]
    [Migration("20260905120000_AwardBackedOrdersAreInvoiceable")]
    public partial class AwardBackedOrdersAreInvoiceable : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            if (migrationBuilder.ActiveProvider != "Npgsql.EntityFrameworkCore.PostgreSQL") return;
            migrationBuilder.Sql("""
CREATE OR REPLACE FUNCTION public.nexora_receivable_issued_immutable() RETURNS trigger
    LANGUAGE plpgsql SECURITY DEFINER
    SET search_path TO 'pg_catalog', 'public'
    AS $$
DECLARE legal_sequence bigint;
DECLARE fiscal_year integer;
DECLARE number_prefix text;
DECLARE line_count integer;
DECLARE line_subtotal numeric(18,2);
DECLARE line_discount numeric(18,2);
DECLARE line_tax numeric(18,2);
DECLARE line_total numeric(18,2);
DECLARE parent_document record;
DECLARE live_outstanding numeric(18,2);
BEGIN
    IF TG_OP = 'INSERT' THEN
        IF NEW."Status" <> 'Draft' OR NEW."DocumentNumber" IS NOT NULL
           OR NEW."IssuedOn" IS NOT NULL OR NEW."IssuedBy" IS NOT NULL
           OR NEW."VoidedOn" IS NOT NULL OR NEW."VoidReason" IS NOT NULL
           OR NEW."VoidedBy" IS NOT NULL OR NEW."Version" <> 1 THEN
            RAISE EXCEPTION 'receivable documents must be created as version-one drafts'
                USING ERRCODE = '23514';
        END IF;
        RETURN NEW;
    END IF;
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
               NEW."ParentDocumentId", NEW."AdjustmentReasonCode", NEW."AdjustmentReason",
               NEW."CurrencyId", NEW."DocumentType", NEW."DocumentDate", NEW."DueDate",
               NEW."SubTotal", NEW."DiscountAmount", NEW."TaxAmount", NEW."TotalAmount",
               NEW."IdempotencyKey", NEW."RequestHash", NEW."CreatedBy", NEW."CreatedOn")
              IS DISTINCT FROM
              (OLD."BusinessUnitId", OLD."CommercialCaseId", OLD."CustomerId", OLD."OrderId",
               OLD."ParentDocumentId", OLD."AdjustmentReasonCode", OLD."AdjustmentReason",
               OLD."CurrencyId", OLD."DocumentType", OLD."DocumentDate", OLD."DueDate",
               OLD."SubTotal", OLD."DiscountAmount", OLD."TaxAmount", OLD."TotalAmount",
               OLD."IdempotencyKey", OLD."RequestHash", OLD."CreatedBy", OLD."CreatedOn") THEN
            RAISE EXCEPTION 'invalid governed receivable issue transition' USING ERRCODE = '55000';
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
                 AND line."LineTotal" <> round(round(line."Quantity" * line."UnitPrice", 2)
                     - line."DiscountAmount" + line."TaxAmount", 2)) THEN
            RAISE EXCEPTION 'receivable lines and header do not reconcile' USING ERRCODE = '23514';
        END IF;

        IF NEW."DocumentType" = 'Invoice' THEN
            IF NEW."OrderId" IS NULL THEN
                RAISE EXCEPTION 'an invoice must reference its source order' USING ERRCODE = '23514';
            END IF;
            PERFORM 1 FROM public."Orders" sales_order
            WHERE sales_order."ID" = NEW."OrderId"
              AND sales_order."BusinessUnitID" = NEW."BusinessUnitId" FOR UPDATE;
            IF NOT FOUND THEN
                RAISE EXCEPTION 'the tenant source order does not exist' USING ERRCODE = '23503';
            END IF;
            IF NOT EXISTS (
                SELECT 1 FROM public."Orders" sales_order
                JOIN public."Setup_Master" order_status
                  ON order_status."SetupID" = sales_order."StatusID"
                 AND order_status."BusinessUnitID" = sales_order."BusinessUnitID"
                LEFT JOIN public."Quotes" quote ON quote."ID" = sales_order."QuoteID"
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
                         IN ('ACCEPTED', 'ORDERED')
                       -- An order raised from a confirmed Client PO: the PO is the customer's acceptance.
                       OR (sales_order."SourceType" = 'CUSTOMER_AWARD'
                           AND sales_order."CustomerAwardID" IS NOT NULL))) THEN
                RAISE EXCEPTION 'the source order is not eligible for invoicing' USING ERRCODE = '23514';
            END IF;
            IF EXISTS (
                SELECT 1 FROM public."ReceivableDocumentLines" line
                LEFT JOIN public."OrderItems" order_line ON order_line."ID" = line."OrderItemId"
                WHERE line."BusinessUnitId" = NEW."BusinessUnitId"
                  AND line."ReceivableDocumentId" = NEW."Id"
                  AND (line."ParentDocumentLineId" IS NOT NULL OR line."OrderItemId" IS NULL
                       OR order_line."ID" IS NULL OR order_line."OrderID" <> NEW."OrderId"
                       OR line."Quantity" + coalesce((
                           SELECT sum(prior_line."Quantity")
                           FROM public."ReceivableDocumentLines" prior_line
                           JOIN public."ReceivableDocuments" prior_document
                             ON prior_document."Id" = prior_line."ReceivableDocumentId"
                            AND prior_document."BusinessUnitId" = prior_line."BusinessUnitId"
                           WHERE prior_line."BusinessUnitId" = NEW."BusinessUnitId"
                             AND prior_line."OrderItemId" = line."OrderItemId"
                             AND prior_document."Id" <> NEW."Id"
                             AND prior_document."DocumentType" = 'Invoice'
                             AND prior_document."Status" = 'Issued'), 0) > order_line."Quantity")) THEN
                RAISE EXCEPTION 'issuing would exceed or detach a source order line' USING ERRCODE = '23514';
            END IF;
            number_prefix := 'INV';
        ELSIF NEW."DocumentType" IN ('CreditNote', 'DebitNote') THEN
            IF lower(trim(NEW."CreatedBy")) = lower(trim(NEW."IssuedBy")) THEN
                RAISE EXCEPTION 'adjustment maker and checker must be different' USING ERRCODE = '23514';
            END IF;
            SELECT parent.* INTO parent_document
            FROM public."ReceivableDocuments" parent
            WHERE parent."BusinessUnitId" = NEW."BusinessUnitId"
              AND parent."Id" = NEW."ParentDocumentId" FOR UPDATE;
            IF NOT FOUND OR parent_document."DocumentType" <> 'Invoice'
               OR parent_document."Status" <> 'Issued' THEN
                RAISE EXCEPTION 'adjustment parent must be a same-tenant issued invoice' USING ERRCODE = '23514';
            END IF;
            IF (NEW."CustomerId", NEW."CurrencyId", NEW."CommercialCaseId", NEW."OrderId")
               IS DISTINCT FROM
               (parent_document."CustomerId", parent_document."CurrencyId",
                parent_document."CommercialCaseId", parent_document."OrderId") THEN
                RAISE EXCEPTION 'adjustment ownership must match its parent invoice' USING ERRCODE = '23514';
            END IF;
            IF EXISTS (
                SELECT 1
                FROM public."ReceivableDocumentLines" line
                LEFT JOIN public."ReceivableDocumentLines" parent_line
                  ON parent_line."BusinessUnitId" = line."BusinessUnitId"
                 AND parent_line."Id" = line."ParentDocumentLineId"
                 AND parent_line."ReceivableDocumentId" = NEW."ParentDocumentId"
                WHERE line."BusinessUnitId" = NEW."BusinessUnitId"
                  AND line."ReceivableDocumentId" = NEW."Id"
                  AND (line."ParentDocumentLineId" IS NULL OR parent_line."Id" IS NULL
                       OR line."OrderItemId" IS DISTINCT FROM parent_line."OrderItemId"
                       OR line."Quantity" > parent_line."Quantity"
                       OR line."UnitPrice" <> parent_line."UnitPrice"
                       OR line."DiscountAmount" <> round(parent_line."DiscountAmount"
                           * line."Quantity" / parent_line."Quantity", 2)
                       OR line."TaxAmount" <> round(parent_line."TaxAmount"
                           * line."Quantity" / parent_line."Quantity", 2))) THEN
                RAISE EXCEPTION 'adjustment lines must preserve parent-line ownership and economics' USING ERRCODE = '23514';
            END IF;
            IF NEW."DocumentType" = 'CreditNote' THEN
                IF EXISTS (
                    SELECT 1
                    FROM public."ReceivableDocumentLines" line
                    JOIN public."ReceivableDocumentLines" parent_line
                      ON parent_line."BusinessUnitId" = line."BusinessUnitId"
                     AND parent_line."Id" = line."ParentDocumentLineId"
                    WHERE line."BusinessUnitId" = NEW."BusinessUnitId"
                      AND line."ReceivableDocumentId" = NEW."Id"
                      AND line."Quantity" + coalesce((
                          SELECT sum(prior_line."Quantity")
                          FROM public."ReceivableDocumentLines" prior_line
                          JOIN public."ReceivableDocuments" prior_credit
                            ON prior_credit."BusinessUnitId" = prior_line."BusinessUnitId"
                           AND prior_credit."Id" = prior_line."ReceivableDocumentId"
                          WHERE prior_line."BusinessUnitId" = NEW."BusinessUnitId"
                            AND prior_line."ParentDocumentLineId" = line."ParentDocumentLineId"
                            AND prior_credit."ParentDocumentId" = NEW."ParentDocumentId"
                            AND prior_credit."DocumentType" = 'CreditNote'
                            AND prior_credit."Status" = 'Issued'
                            AND prior_credit."Id" <> NEW."Id"), 0) > parent_line."Quantity") THEN
                    RAISE EXCEPTION 'issued credit quantity exceeds the parent invoice line' USING ERRCODE = '23514';
                END IF;
                SELECT round(parent_document."TotalAmount"
                    - coalesce((SELECT sum(credit."TotalAmount")
                        FROM public."ReceivableDocuments" credit
                        WHERE credit."BusinessUnitId" = NEW."BusinessUnitId"
                          AND credit."ParentDocumentId" = NEW."ParentDocumentId"
                          AND credit."DocumentType" = 'CreditNote'
                          AND credit."Status" = 'Issued'
                          AND credit."Id" <> NEW."Id"), 0)
                    - coalesce((SELECT sum(allocation."Amount")
                        FROM public."PaymentAllocations" allocation
                        JOIN public."CustomerPayments" payment
                          ON payment."BusinessUnitId" = allocation."BusinessUnitId"
                         AND payment."Id" = allocation."CustomerPaymentId"
                        WHERE allocation."BusinessUnitId" = NEW."BusinessUnitId"
                          AND allocation."ReceivableDocumentId" = NEW."ParentDocumentId"
                          AND payment."Status" = 'Posted'), 0), 2)
                INTO live_outstanding;
                IF NEW."TotalAmount" > live_outstanding THEN
                    RAISE EXCEPTION 'credit note exceeds the parent invoice live outstanding balance' USING ERRCODE = '23514';
                END IF;
                number_prefix := 'CRN';
            ELSE
                IF EXISTS (
                    SELECT 1
                    FROM public."ReceivableDocumentLines" line
                    JOIN public."ReceivableDocumentLines" parent_line
                      ON parent_line."BusinessUnitId" = line."BusinessUnitId"
                     AND parent_line."Id" = line."ParentDocumentLineId"
                    WHERE line."BusinessUnitId" = NEW."BusinessUnitId"
                      AND line."ReceivableDocumentId" = NEW."Id"
                      AND line."Quantity" + coalesce((
                          SELECT sum(prior_line."Quantity")
                          FROM public."ReceivableDocumentLines" prior_line
                          JOIN public."ReceivableDocuments" prior_debit
                            ON prior_debit."BusinessUnitId" = prior_line."BusinessUnitId"
                           AND prior_debit."Id" = prior_line."ReceivableDocumentId"
                          WHERE prior_line."BusinessUnitId" = NEW."BusinessUnitId"
                            AND prior_line."ParentDocumentLineId" = line."ParentDocumentLineId"
                            AND prior_debit."ParentDocumentId" = NEW."ParentDocumentId"
                            AND prior_debit."DocumentType" = 'DebitNote'
                            AND prior_debit."Status" = 'Issued'
                            AND prior_debit."Id" <> NEW."Id"), 0) > parent_line."Quantity") THEN
                    RAISE EXCEPTION 'issued debit quantity exceeds the parent invoice line' USING ERRCODE = '23514';
                END IF;
                number_prefix := 'DBN';
            END IF;
        ELSE
            RAISE EXCEPTION 'unsupported receivable document type' USING ERRCODE = '23514';
        END IF;

        fiscal_year := extract(year from NEW."DocumentDate")::integer;
        INSERT INTO public."LegalDocumentCounters"
            ("BusinessUnitId", "DocumentType", "FiscalYear", "NextNumber")
        VALUES (NEW."BusinessUnitId", NEW."DocumentType", fiscal_year, 2)
        ON CONFLICT ("BusinessUnitId", "DocumentType", "FiscalYear")
        DO UPDATE SET "NextNumber" = public."LegalDocumentCounters"."NextNumber" + 1
        RETURNING "NextNumber" - 1 INTO legal_sequence;
        NEW."DocumentNumber" := format('%s-%s-%s', number_prefix, fiscal_year,
            lpad(legal_sequence::text, 6, '0'));
        PERFORM public.nexora_write_finance_audit(NEW."BusinessUnitId", 'ReceivableDocument',
            NEW."Id", 'Issued', NEW."IssuedBy", jsonb_build_object(
                'number', NEW."DocumentNumber", 'documentType', NEW."DocumentType",
                'parentDocumentId', NEW."ParentDocumentId", 'reasonCode', NEW."AdjustmentReasonCode",
                'amount', NEW."TotalAmount", 'version', NEW."Version"), NEW."IssuedOn");
        RETURN NEW;
    END IF;

    IF OLD."Status" = 'Draft' AND NEW."Status" = 'Cancelled' THEN
        IF NEW."DocumentNumber" IS NOT NULL OR NEW."IssuedOn" IS NOT NULL
           OR NEW."VoidedOn" IS NULL OR NEW."VoidReason" IS NULL OR length(trim(NEW."VoidReason")) = 0
           OR NEW."VoidedBy" IS NULL OR length(trim(NEW."VoidedBy")) = 0
           OR NEW."Version" <> OLD."Version" + 1
           OR (NEW."BusinessUnitId", NEW."CommercialCaseId", NEW."CustomerId", NEW."OrderId",
               NEW."ParentDocumentId", NEW."AdjustmentReasonCode", NEW."AdjustmentReason",
               NEW."CurrencyId", NEW."DocumentType", NEW."DocumentDate", NEW."DueDate",
               NEW."SubTotal", NEW."DiscountAmount", NEW."TaxAmount", NEW."TotalAmount",
               NEW."IdempotencyKey", NEW."RequestHash", NEW."CreatedBy", NEW."CreatedOn", NEW."IssuedBy")
              IS DISTINCT FROM
              (OLD."BusinessUnitId", OLD."CommercialCaseId", OLD."CustomerId", OLD."OrderId",
               OLD."ParentDocumentId", OLD."AdjustmentReasonCode", OLD."AdjustmentReason",
               OLD."CurrencyId", OLD."DocumentType", OLD."DocumentDate", OLD."DueDate",
               OLD."SubTotal", OLD."DiscountAmount", OLD."TaxAmount", OLD."TotalAmount",
               OLD."IdempotencyKey", OLD."RequestHash", OLD."CreatedBy", OLD."CreatedOn", OLD."IssuedBy") THEN
            RAISE EXCEPTION 'invalid governed receivable cancellation transition' USING ERRCODE = '55000';
        END IF;
        PERFORM public.nexora_write_finance_audit(NEW."BusinessUnitId", 'ReceivableDocument',
            NEW."Id", 'DraftCancelled', NEW."VoidedBy",
            jsonb_build_object('reason', NEW."VoidReason", 'documentType', NEW."DocumentType",
                'parentDocumentId', NEW."ParentDocumentId", 'amount', NEW."TotalAmount",
                'version', NEW."Version"), NEW."VoidedOn");
        RETURN NEW;
    END IF;
    IF NEW."Status" IS DISTINCT FROM OLD."Status" THEN
        RAISE EXCEPTION 'invalid receivable document status transition' USING ERRCODE = '55000';
    END IF;
    RAISE EXCEPTION 'receivable drafts are immutable; cancel and recreate the draft' USING ERRCODE = '55000';
END
$$;
""");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            if (migrationBuilder.ActiveProvider != "Npgsql.EntityFrameworkCore.PostgreSQL") return;
            migrationBuilder.Sql("""
CREATE OR REPLACE FUNCTION public.nexora_receivable_issued_immutable() RETURNS trigger
    LANGUAGE plpgsql SECURITY DEFINER
    SET search_path TO 'pg_catalog', 'public'
    AS $$
DECLARE legal_sequence bigint;
DECLARE fiscal_year integer;
DECLARE number_prefix text;
DECLARE line_count integer;
DECLARE line_subtotal numeric(18,2);
DECLARE line_discount numeric(18,2);
DECLARE line_tax numeric(18,2);
DECLARE line_total numeric(18,2);
DECLARE parent_document record;
DECLARE live_outstanding numeric(18,2);
BEGIN
    IF TG_OP = 'INSERT' THEN
        IF NEW."Status" <> 'Draft' OR NEW."DocumentNumber" IS NOT NULL
           OR NEW."IssuedOn" IS NOT NULL OR NEW."IssuedBy" IS NOT NULL
           OR NEW."VoidedOn" IS NOT NULL OR NEW."VoidReason" IS NOT NULL
           OR NEW."VoidedBy" IS NOT NULL OR NEW."Version" <> 1 THEN
            RAISE EXCEPTION 'receivable documents must be created as version-one drafts'
                USING ERRCODE = '23514';
        END IF;
        RETURN NEW;
    END IF;
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
               NEW."ParentDocumentId", NEW."AdjustmentReasonCode", NEW."AdjustmentReason",
               NEW."CurrencyId", NEW."DocumentType", NEW."DocumentDate", NEW."DueDate",
               NEW."SubTotal", NEW."DiscountAmount", NEW."TaxAmount", NEW."TotalAmount",
               NEW."IdempotencyKey", NEW."RequestHash", NEW."CreatedBy", NEW."CreatedOn")
              IS DISTINCT FROM
              (OLD."BusinessUnitId", OLD."CommercialCaseId", OLD."CustomerId", OLD."OrderId",
               OLD."ParentDocumentId", OLD."AdjustmentReasonCode", OLD."AdjustmentReason",
               OLD."CurrencyId", OLD."DocumentType", OLD."DocumentDate", OLD."DueDate",
               OLD."SubTotal", OLD."DiscountAmount", OLD."TaxAmount", OLD."TotalAmount",
               OLD."IdempotencyKey", OLD."RequestHash", OLD."CreatedBy", OLD."CreatedOn") THEN
            RAISE EXCEPTION 'invalid governed receivable issue transition' USING ERRCODE = '55000';
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
                 AND line."LineTotal" <> round(round(line."Quantity" * line."UnitPrice", 2)
                     - line."DiscountAmount" + line."TaxAmount", 2)) THEN
            RAISE EXCEPTION 'receivable lines and header do not reconcile' USING ERRCODE = '23514';
        END IF;

        IF NEW."DocumentType" = 'Invoice' THEN
            IF NEW."OrderId" IS NULL THEN
                RAISE EXCEPTION 'an invoice must reference its source order' USING ERRCODE = '23514';
            END IF;
            PERFORM 1 FROM public."Orders" sales_order
            WHERE sales_order."ID" = NEW."OrderId"
              AND sales_order."BusinessUnitID" = NEW."BusinessUnitId" FOR UPDATE;
            IF NOT FOUND THEN
                RAISE EXCEPTION 'the tenant source order does not exist' USING ERRCODE = '23503';
            END IF;
            IF NOT EXISTS (
                SELECT 1 FROM public."Orders" sales_order
                JOIN public."Setup_Master" order_status
                  ON order_status."SetupID" = sales_order."StatusID"
                 AND order_status."BusinessUnitID" = sales_order."BusinessUnitID"
                LEFT JOIN public."Quotes" quote ON quote."ID" = sales_order."QuoteID"
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
            IF EXISTS (
                SELECT 1 FROM public."ReceivableDocumentLines" line
                LEFT JOIN public."OrderItems" order_line ON order_line."ID" = line."OrderItemId"
                WHERE line."BusinessUnitId" = NEW."BusinessUnitId"
                  AND line."ReceivableDocumentId" = NEW."Id"
                  AND (line."ParentDocumentLineId" IS NOT NULL OR line."OrderItemId" IS NULL
                       OR order_line."ID" IS NULL OR order_line."OrderID" <> NEW."OrderId"
                       OR line."Quantity" + coalesce((
                           SELECT sum(prior_line."Quantity")
                           FROM public."ReceivableDocumentLines" prior_line
                           JOIN public."ReceivableDocuments" prior_document
                             ON prior_document."Id" = prior_line."ReceivableDocumentId"
                            AND prior_document."BusinessUnitId" = prior_line."BusinessUnitId"
                           WHERE prior_line."BusinessUnitId" = NEW."BusinessUnitId"
                             AND prior_line."OrderItemId" = line."OrderItemId"
                             AND prior_document."Id" <> NEW."Id"
                             AND prior_document."DocumentType" = 'Invoice'
                             AND prior_document."Status" = 'Issued'), 0) > order_line."Quantity")) THEN
                RAISE EXCEPTION 'issuing would exceed or detach a source order line' USING ERRCODE = '23514';
            END IF;
            number_prefix := 'INV';
        ELSIF NEW."DocumentType" IN ('CreditNote', 'DebitNote') THEN
            IF lower(trim(NEW."CreatedBy")) = lower(trim(NEW."IssuedBy")) THEN
                RAISE EXCEPTION 'adjustment maker and checker must be different' USING ERRCODE = '23514';
            END IF;
            SELECT parent.* INTO parent_document
            FROM public."ReceivableDocuments" parent
            WHERE parent."BusinessUnitId" = NEW."BusinessUnitId"
              AND parent."Id" = NEW."ParentDocumentId" FOR UPDATE;
            IF NOT FOUND OR parent_document."DocumentType" <> 'Invoice'
               OR parent_document."Status" <> 'Issued' THEN
                RAISE EXCEPTION 'adjustment parent must be a same-tenant issued invoice' USING ERRCODE = '23514';
            END IF;
            IF (NEW."CustomerId", NEW."CurrencyId", NEW."CommercialCaseId", NEW."OrderId")
               IS DISTINCT FROM
               (parent_document."CustomerId", parent_document."CurrencyId",
                parent_document."CommercialCaseId", parent_document."OrderId") THEN
                RAISE EXCEPTION 'adjustment ownership must match its parent invoice' USING ERRCODE = '23514';
            END IF;
            IF EXISTS (
                SELECT 1
                FROM public."ReceivableDocumentLines" line
                LEFT JOIN public."ReceivableDocumentLines" parent_line
                  ON parent_line."BusinessUnitId" = line."BusinessUnitId"
                 AND parent_line."Id" = line."ParentDocumentLineId"
                 AND parent_line."ReceivableDocumentId" = NEW."ParentDocumentId"
                WHERE line."BusinessUnitId" = NEW."BusinessUnitId"
                  AND line."ReceivableDocumentId" = NEW."Id"
                  AND (line."ParentDocumentLineId" IS NULL OR parent_line."Id" IS NULL
                       OR line."OrderItemId" IS DISTINCT FROM parent_line."OrderItemId"
                       OR line."Quantity" > parent_line."Quantity"
                       OR line."UnitPrice" <> parent_line."UnitPrice"
                       OR line."DiscountAmount" <> round(parent_line."DiscountAmount"
                           * line."Quantity" / parent_line."Quantity", 2)
                       OR line."TaxAmount" <> round(parent_line."TaxAmount"
                           * line."Quantity" / parent_line."Quantity", 2))) THEN
                RAISE EXCEPTION 'adjustment lines must preserve parent-line ownership and economics' USING ERRCODE = '23514';
            END IF;
            IF NEW."DocumentType" = 'CreditNote' THEN
                IF EXISTS (
                    SELECT 1
                    FROM public."ReceivableDocumentLines" line
                    JOIN public."ReceivableDocumentLines" parent_line
                      ON parent_line."BusinessUnitId" = line."BusinessUnitId"
                     AND parent_line."Id" = line."ParentDocumentLineId"
                    WHERE line."BusinessUnitId" = NEW."BusinessUnitId"
                      AND line."ReceivableDocumentId" = NEW."Id"
                      AND line."Quantity" + coalesce((
                          SELECT sum(prior_line."Quantity")
                          FROM public."ReceivableDocumentLines" prior_line
                          JOIN public."ReceivableDocuments" prior_credit
                            ON prior_credit."BusinessUnitId" = prior_line."BusinessUnitId"
                           AND prior_credit."Id" = prior_line."ReceivableDocumentId"
                          WHERE prior_line."BusinessUnitId" = NEW."BusinessUnitId"
                            AND prior_line."ParentDocumentLineId" = line."ParentDocumentLineId"
                            AND prior_credit."ParentDocumentId" = NEW."ParentDocumentId"
                            AND prior_credit."DocumentType" = 'CreditNote'
                            AND prior_credit."Status" = 'Issued'
                            AND prior_credit."Id" <> NEW."Id"), 0) > parent_line."Quantity") THEN
                    RAISE EXCEPTION 'issued credit quantity exceeds the parent invoice line' USING ERRCODE = '23514';
                END IF;
                SELECT round(parent_document."TotalAmount"
                    - coalesce((SELECT sum(credit."TotalAmount")
                        FROM public."ReceivableDocuments" credit
                        WHERE credit."BusinessUnitId" = NEW."BusinessUnitId"
                          AND credit."ParentDocumentId" = NEW."ParentDocumentId"
                          AND credit."DocumentType" = 'CreditNote'
                          AND credit."Status" = 'Issued'
                          AND credit."Id" <> NEW."Id"), 0)
                    - coalesce((SELECT sum(allocation."Amount")
                        FROM public."PaymentAllocations" allocation
                        JOIN public."CustomerPayments" payment
                          ON payment."BusinessUnitId" = allocation."BusinessUnitId"
                         AND payment."Id" = allocation."CustomerPaymentId"
                        WHERE allocation."BusinessUnitId" = NEW."BusinessUnitId"
                          AND allocation."ReceivableDocumentId" = NEW."ParentDocumentId"
                          AND payment."Status" = 'Posted'), 0), 2)
                INTO live_outstanding;
                IF NEW."TotalAmount" > live_outstanding THEN
                    RAISE EXCEPTION 'credit note exceeds the parent invoice live outstanding balance' USING ERRCODE = '23514';
                END IF;
                number_prefix := 'CRN';
            ELSE
                IF EXISTS (
                    SELECT 1
                    FROM public."ReceivableDocumentLines" line
                    JOIN public."ReceivableDocumentLines" parent_line
                      ON parent_line."BusinessUnitId" = line."BusinessUnitId"
                     AND parent_line."Id" = line."ParentDocumentLineId"
                    WHERE line."BusinessUnitId" = NEW."BusinessUnitId"
                      AND line."ReceivableDocumentId" = NEW."Id"
                      AND line."Quantity" + coalesce((
                          SELECT sum(prior_line."Quantity")
                          FROM public."ReceivableDocumentLines" prior_line
                          JOIN public."ReceivableDocuments" prior_debit
                            ON prior_debit."BusinessUnitId" = prior_line."BusinessUnitId"
                           AND prior_debit."Id" = prior_line."ReceivableDocumentId"
                          WHERE prior_line."BusinessUnitId" = NEW."BusinessUnitId"
                            AND prior_line."ParentDocumentLineId" = line."ParentDocumentLineId"
                            AND prior_debit."ParentDocumentId" = NEW."ParentDocumentId"
                            AND prior_debit."DocumentType" = 'DebitNote'
                            AND prior_debit."Status" = 'Issued'
                            AND prior_debit."Id" <> NEW."Id"), 0) > parent_line."Quantity") THEN
                    RAISE EXCEPTION 'issued debit quantity exceeds the parent invoice line' USING ERRCODE = '23514';
                END IF;
                number_prefix := 'DBN';
            END IF;
        ELSE
            RAISE EXCEPTION 'unsupported receivable document type' USING ERRCODE = '23514';
        END IF;

        fiscal_year := extract(year from NEW."DocumentDate")::integer;
        INSERT INTO public."LegalDocumentCounters"
            ("BusinessUnitId", "DocumentType", "FiscalYear", "NextNumber")
        VALUES (NEW."BusinessUnitId", NEW."DocumentType", fiscal_year, 2)
        ON CONFLICT ("BusinessUnitId", "DocumentType", "FiscalYear")
        DO UPDATE SET "NextNumber" = public."LegalDocumentCounters"."NextNumber" + 1
        RETURNING "NextNumber" - 1 INTO legal_sequence;
        NEW."DocumentNumber" := format('%s-%s-%s', number_prefix, fiscal_year,
            lpad(legal_sequence::text, 6, '0'));
        PERFORM public.nexora_write_finance_audit(NEW."BusinessUnitId", 'ReceivableDocument',
            NEW."Id", 'Issued', NEW."IssuedBy", jsonb_build_object(
                'number', NEW."DocumentNumber", 'documentType', NEW."DocumentType",
                'parentDocumentId', NEW."ParentDocumentId", 'reasonCode', NEW."AdjustmentReasonCode",
                'amount', NEW."TotalAmount", 'version', NEW."Version"), NEW."IssuedOn");
        RETURN NEW;
    END IF;

    IF OLD."Status" = 'Draft' AND NEW."Status" = 'Cancelled' THEN
        IF NEW."DocumentNumber" IS NOT NULL OR NEW."IssuedOn" IS NOT NULL
           OR NEW."VoidedOn" IS NULL OR NEW."VoidReason" IS NULL OR length(trim(NEW."VoidReason")) = 0
           OR NEW."VoidedBy" IS NULL OR length(trim(NEW."VoidedBy")) = 0
           OR NEW."Version" <> OLD."Version" + 1
           OR (NEW."BusinessUnitId", NEW."CommercialCaseId", NEW."CustomerId", NEW."OrderId",
               NEW."ParentDocumentId", NEW."AdjustmentReasonCode", NEW."AdjustmentReason",
               NEW."CurrencyId", NEW."DocumentType", NEW."DocumentDate", NEW."DueDate",
               NEW."SubTotal", NEW."DiscountAmount", NEW."TaxAmount", NEW."TotalAmount",
               NEW."IdempotencyKey", NEW."RequestHash", NEW."CreatedBy", NEW."CreatedOn", NEW."IssuedBy")
              IS DISTINCT FROM
              (OLD."BusinessUnitId", OLD."CommercialCaseId", OLD."CustomerId", OLD."OrderId",
               OLD."ParentDocumentId", OLD."AdjustmentReasonCode", OLD."AdjustmentReason",
               OLD."CurrencyId", OLD."DocumentType", OLD."DocumentDate", OLD."DueDate",
               OLD."SubTotal", OLD."DiscountAmount", OLD."TaxAmount", OLD."TotalAmount",
               OLD."IdempotencyKey", OLD."RequestHash", OLD."CreatedBy", OLD."CreatedOn", OLD."IssuedBy") THEN
            RAISE EXCEPTION 'invalid governed receivable cancellation transition' USING ERRCODE = '55000';
        END IF;
        PERFORM public.nexora_write_finance_audit(NEW."BusinessUnitId", 'ReceivableDocument',
            NEW."Id", 'DraftCancelled', NEW."VoidedBy",
            jsonb_build_object('reason', NEW."VoidReason", 'documentType', NEW."DocumentType",
                'parentDocumentId', NEW."ParentDocumentId", 'amount', NEW."TotalAmount",
                'version', NEW."Version"), NEW."VoidedOn");
        RETURN NEW;
    END IF;
    IF NEW."Status" IS DISTINCT FROM OLD."Status" THEN
        RAISE EXCEPTION 'invalid receivable document status transition' USING ERRCODE = '55000';
    END IF;
    RAISE EXCEPTION 'receivable drafts are immutable; cancel and recreate the draft' USING ERRCODE = '55000';
END
$$;
""");
        }
    }
}
