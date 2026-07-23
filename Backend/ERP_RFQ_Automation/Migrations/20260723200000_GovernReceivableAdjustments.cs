using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ERP_RFQ_Automation.Migrations
{
    /// <inheritdoc />
    public partial class GovernReceivableAdjustments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AdjustmentReason",
                table: "ReceivableDocuments",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AdjustmentReasonCode",
                table: "ReceivableDocuments",
                type: "character varying(50)",
                maxLength: 50,
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "ParentDocumentLineId",
                table: "ReceivableDocumentLines",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddUniqueConstraint(
                name: "AK_ReceivableDocumentLines_BusinessUnitId_Id",
                table: "ReceivableDocumentLines",
                columns: new[] { "BusinessUnitId", "Id" });

            migrationBuilder.AddCheckConstraint(
                name: "CK_ReceivableDocuments_Type",
                table: "ReceivableDocuments",
                sql: "(\"DocumentType\" = 'Invoice' AND \"ParentDocumentId\" IS NULL AND \"AdjustmentReasonCode\" IS NULL AND \"AdjustmentReason\" IS NULL) OR (\"DocumentType\" IN ('CreditNote','DebitNote') AND \"ParentDocumentId\" IS NOT NULL AND \"AdjustmentReasonCode\" IS NOT NULL AND length(trim(\"AdjustmentReasonCode\")) > 0 AND \"AdjustmentReason\" IS NOT NULL AND length(trim(\"AdjustmentReason\")) > 0)");

            migrationBuilder.CreateIndex(
                name: "IX_ReceivableDocumentLines_BusinessUnitId_ParentDocumentLineId",
                table: "ReceivableDocumentLines",
                columns: new[] { "BusinessUnitId", "ParentDocumentLineId" });

            migrationBuilder.AddForeignKey(
                name: "FK_ReceivableDocumentLines_ReceivableDocumentLines_BusinessUni~",
                table: "ReceivableDocumentLines",
                columns: new[] { "BusinessUnitId", "ParentDocumentLineId" },
                principalTable: "ReceivableDocumentLines",
                principalColumns: new[] { "BusinessUnitId", "Id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.Sql("""
                CREATE OR REPLACE FUNCTION public.nexora_write_finance_audit(
                    business_unit_id bigint, aggregate_type text, aggregate_id bigint,
                    audit_action text, audit_actor text, audit_detail jsonb,
                    occurred_on timestamp without time zone)
                RETURNS void LANGUAGE plpgsql SECURITY DEFINER
                SET search_path = pg_catalog, public AS $function$
                DECLARE aggregate_status text;
                DECLARE aggregate_document_type text;
                BEGIN
                    IF business_unit_id <= 0 OR aggregate_id <= 0
                       OR audit_actor IS NULL OR length(trim(audit_actor)) = 0
                       OR audit_detail IS NULL OR jsonb_typeof(audit_detail) <> 'object'
                       OR occurred_on IS NULL THEN
                        RAISE EXCEPTION 'invalid commercial finance audit evidence' USING ERRCODE = '23514';
                    END IF;
                    IF current_setting('role', true) = 'nexora_tenant_app'
                       AND business_unit_id IS DISTINCT FROM
                           NULLIF(current_setting('nexora.business_unit_id', true), '')::bigint THEN
                        RAISE EXCEPTION 'commercial finance audit tenant mismatch' USING ERRCODE = '42501';
                    END IF;

                    IF aggregate_type = 'ReceivableDocument' THEN
                        SELECT document."Status", document."DocumentType"
                        INTO aggregate_status, aggregate_document_type
                        FROM public."ReceivableDocuments" document
                        WHERE document."BusinessUnitId" = business_unit_id
                          AND document."Id" = aggregate_id;
                        IF NOT FOUND OR audit_action NOT IN
                            ('DraftCreated', 'AdjustmentDraftCreated', 'Issued', 'DraftCancelled')
                           OR (audit_action = 'DraftCreated' AND
                               (aggregate_status <> 'Draft' OR aggregate_document_type <> 'Invoice'))
                           OR (audit_action = 'AdjustmentDraftCreated' AND
                               (aggregate_status <> 'Draft' OR aggregate_document_type NOT IN ('CreditNote', 'DebitNote')))
                           OR (audit_action IN ('Issued', 'DraftCancelled') AND aggregate_status <> 'Draft') THEN
                            RAISE EXCEPTION 'audit action is inconsistent with the receivable document' USING ERRCODE = '23514';
                        END IF;
                    ELSIF aggregate_type = 'CustomerPayment' THEN
                        SELECT payment."Status" INTO aggregate_status
                        FROM public."CustomerPayments" payment
                        WHERE payment."BusinessUnitId" = business_unit_id
                          AND payment."Id" = aggregate_id;
                        IF NOT FOUND OR audit_action NOT IN ('Posted', 'Reversed')
                           OR (audit_action = 'Posted' AND aggregate_status <> 'Posted')
                           OR (audit_action = 'Reversed' AND aggregate_status <> 'Reversed') THEN
                            RAISE EXCEPTION 'audit action is inconsistent with the customer payment' USING ERRCODE = '23514';
                        END IF;
                    ELSE
                        RAISE EXCEPTION 'unsupported commercial finance audit aggregate' USING ERRCODE = '23514';
                    END IF;

                    INSERT INTO public."CommercialFinanceAudits"
                        ("BusinessUnitId", "AggregateType", "AggregateId", "Action", "Actor", "OccurredOn", "DetailJson")
                    VALUES (business_unit_id, aggregate_type, aggregate_id, audit_action,
                        audit_actor, occurred_on, audit_detail);
                END
                $function$;

                CREATE OR REPLACE FUNCTION public.nexora_receivable_issued_immutable()
                RETURNS trigger LANGUAGE plpgsql SECURITY DEFINER
                SET search_path = pg_catalog, public AS $function$
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
                $function$;
                DROP TRIGGER IF EXISTS trg_receivable_document_issued_immutable ON public."ReceivableDocuments";
                CREATE TRIGGER trg_receivable_document_issued_immutable
                    BEFORE INSERT OR UPDATE OR DELETE ON public."ReceivableDocuments"
                    FOR EACH ROW EXECUTE FUNCTION public.nexora_receivable_issued_immutable();

                CREATE OR REPLACE FUNCTION public.nexora_receivable_line_issued_immutable()
                RETURNS trigger LANGUAGE plpgsql SECURITY DEFINER
                SET search_path = pg_catalog, public AS $function$
                DECLARE target_document_id bigint;
                DECLARE target_business_unit_id bigint;
                DECLARE target_status text;
                BEGIN
                    target_document_id := CASE WHEN TG_OP = 'DELETE' THEN OLD."ReceivableDocumentId" ELSE NEW."ReceivableDocumentId" END;
                    target_business_unit_id := CASE WHEN TG_OP = 'DELETE' THEN OLD."BusinessUnitId" ELSE NEW."BusinessUnitId" END;
                    SELECT document."Status" INTO target_status
                    FROM public."ReceivableDocuments" document
                    WHERE document."Id" = target_document_id
                      AND document."BusinessUnitId" = target_business_unit_id;
                    IF NOT FOUND THEN
                        RAISE EXCEPTION 'receivable line tenant document does not exist' USING ERRCODE = '23503';
                    END IF;
                    IF TG_OP = 'INSERT' THEN
                        IF target_status <> 'Draft' OR EXISTS (
                            SELECT 1 FROM public."FinanceOutboxMessages" evidence
                            WHERE evidence."BusinessUnitId" = target_business_unit_id
                              AND evidence."AggregateType" = 'ReceivableDocument'
                              AND evidence."AggregateId" = target_document_id) THEN
                            RAISE EXCEPTION 'receivable lines may only be inserted with their new draft document' USING ERRCODE = '55000';
                        END IF;
                        RETURN NEW;
                    END IF;
                    RAISE EXCEPTION 'receivable document lines are immutable; cancel and recreate the draft' USING ERRCODE = '55000';
                END
                $function$;
                DROP TRIGGER IF EXISTS trg_receivable_line_issued_immutable ON public."ReceivableDocumentLines";
                CREATE TRIGGER trg_receivable_line_issued_immutable
                    BEFORE INSERT OR UPDATE OR DELETE ON public."ReceivableDocumentLines"
                    FOR EACH ROW EXECUTE FUNCTION public.nexora_receivable_line_issued_immutable();

                CREATE OR REPLACE FUNCTION public.nexora_payment_allocation_valid()
                RETURNS trigger LANGUAGE plpgsql SECURITY DEFINER
                SET search_path = pg_catalog, public AS $function$
                DECLARE payment_row record;
                DECLARE document_row record;
                DECLARE payment_allocated numeric(18,2);
                DECLARE document_allocated numeric(18,2);
                DECLARE issued_credits numeric(18,2);
                DECLARE document_outstanding numeric(18,2);
                BEGIN
                    SELECT document.* INTO document_row
                    FROM public."ReceivableDocuments" document
                    WHERE document."Id" = NEW."ReceivableDocumentId"
                      AND document."BusinessUnitId" = NEW."BusinessUnitId" FOR UPDATE;
                    IF NOT FOUND OR document_row."Status" <> 'Issued'
                       OR document_row."DocumentType" NOT IN ('Invoice', 'DebitNote') THEN
                        RAISE EXCEPTION 'payments require a same-tenant issued invoice or debit note' USING ERRCODE = '23514';
                    END IF;
                    SELECT payment.* INTO payment_row
                    FROM public."CustomerPayments" payment
                    WHERE payment."Id" = NEW."CustomerPaymentId"
                      AND payment."BusinessUnitId" = NEW."BusinessUnitId" FOR UPDATE;
                    IF NOT FOUND OR payment_row."Status" <> 'Posted' THEN
                        RAISE EXCEPTION 'payment allocation parent is invalid' USING ERRCODE = '23503';
                    END IF;
                    IF (payment_row."CustomerId", payment_row."CurrencyId") IS DISTINCT FROM
                       (document_row."CustomerId", document_row."CurrencyId") THEN
                        RAISE EXCEPTION 'payment customer and currency must match the receivable document' USING ERRCODE = '23514';
                    END IF;
                    SELECT coalesce(sum(allocation."Amount"), 0) INTO payment_allocated
                    FROM public."PaymentAllocations" allocation
                    WHERE allocation."BusinessUnitId" = NEW."BusinessUnitId"
                      AND allocation."CustomerPaymentId" = NEW."CustomerPaymentId"
                      AND allocation."Id" <> coalesce(NEW."Id", 0);
                    IF payment_allocated + NEW."Amount" > payment_row."Amount" THEN
                        RAISE EXCEPTION 'payment allocations exceed payment amount' USING ERRCODE = '23514';
                    END IF;
                    SELECT coalesce(sum(allocation."Amount"), 0) INTO document_allocated
                    FROM public."PaymentAllocations" allocation
                    JOIN public."CustomerPayments" payment
                      ON payment."BusinessUnitId" = allocation."BusinessUnitId"
                     AND payment."Id" = allocation."CustomerPaymentId"
                    WHERE allocation."BusinessUnitId" = NEW."BusinessUnitId"
                      AND allocation."ReceivableDocumentId" = NEW."ReceivableDocumentId"
                      AND allocation."Id" <> coalesce(NEW."Id", 0)
                      AND payment."Status" = 'Posted';
                    issued_credits := 0;
                    IF document_row."DocumentType" = 'Invoice' THEN
                        SELECT coalesce(sum(credit."TotalAmount"), 0) INTO issued_credits
                        FROM public."ReceivableDocuments" credit
                        WHERE credit."BusinessUnitId" = NEW."BusinessUnitId"
                          AND credit."ParentDocumentId" = NEW."ReceivableDocumentId"
                          AND credit."DocumentType" = 'CreditNote' AND credit."Status" = 'Issued';
                    END IF;
                    document_outstanding := round(document_row."TotalAmount" - issued_credits - document_allocated, 2);
                    IF NEW."Amount" > document_outstanding THEN
                        RAISE EXCEPTION 'payment allocation exceeds live receivable outstanding' USING ERRCODE = '23514';
                    END IF;
                    RETURN NEW;
                END
                $function$;

                CREATE OR REPLACE FUNCTION public.nexora_receivable_outbox_event()
                RETURNS trigger LANGUAGE plpgsql SECURITY DEFINER
                SET search_path = pg_catalog, public AS $function$
                DECLARE event_type text;
                DECLARE event_time timestamp without time zone;
                DECLARE event_payload jsonb;
                DECLARE type_segment text;
                BEGIN
                    type_segment := CASE NEW."DocumentType"
                        WHEN 'CreditNote' THEN 'credit-note'
                        WHEN 'DebitNote' THEN 'debit-note'
                        ELSE 'receivable' END;
                    IF TG_OP = 'INSERT' AND NEW."Status" = 'Draft' THEN
                        event_type := 'finance.' || type_segment || '.draft-created';
                        event_time := coalesce(NEW."CreatedOn", (CURRENT_TIMESTAMP AT TIME ZONE 'UTC'));
                        PERFORM public.nexora_write_finance_audit(NEW."BusinessUnitId", 'ReceivableDocument',
                            NEW."Id", CASE NEW."DocumentType" WHEN 'Invoice' THEN 'DraftCreated'
                                ELSE 'AdjustmentDraftCreated' END, NEW."CreatedBy",
                            jsonb_build_object('documentType', NEW."DocumentType",
                                'parentDocumentId', NEW."ParentDocumentId", 'amount', NEW."TotalAmount",
                                'version', NEW."Version"), event_time);
                    ELSIF TG_OP = 'UPDATE' AND OLD."Status" = 'Draft' AND NEW."Status" IN ('Issued', 'Cancelled') THEN
                        event_type := 'finance.' || type_segment ||
                            CASE NEW."Status" WHEN 'Issued' THEN '.issued' ELSE '.cancelled' END;
                        event_time := CASE NEW."Status" WHEN 'Issued' THEN NEW."IssuedOn" ELSE NEW."VoidedOn" END;
                    ELSE
                        RETURN NEW;
                    END IF;
                    event_payload := jsonb_build_object(
                        'Id', NEW."Id", 'OrderId', NEW."OrderId", 'ParentDocumentId', NEW."ParentDocumentId",
                        'DocumentType', NEW."DocumentType", 'Status', NEW."Status",
                        'DocumentNumber', NEW."DocumentNumber", 'TotalAmount', NEW."TotalAmount",
                        'CurrencyId', NEW."CurrencyId", 'CustomerId', NEW."CustomerId",
                        'CommercialCaseId', NEW."CommercialCaseId", 'ReasonCode', NEW."AdjustmentReasonCode",
                        'Actor', coalesce(NEW."IssuedBy", NEW."VoidedBy", NEW."CreatedBy"),
                        'CreatedBy', NEW."CreatedBy", 'IssuedBy', NEW."IssuedBy", 'Version', NEW."Version");
                    PERFORM public.nexora_write_finance_outbox(NEW."BusinessUnitId", 'ReceivableDocument',
                        NEW."Id", NEW."Version", event_type, event_payload, event_time);
                    RETURN NEW;
                END
                $function$;
                DROP TRIGGER IF EXISTS trg_receivable_outbox_event ON public."ReceivableDocuments";
                CREATE CONSTRAINT TRIGGER trg_receivable_outbox_event
                    AFTER INSERT OR UPDATE ON public."ReceivableDocuments"
                    DEFERRABLE INITIALLY DEFERRED
                    FOR EACH ROW EXECUTE FUNCTION public.nexora_receivable_outbox_event();

                CREATE OR REPLACE FUNCTION public.nexora_payment_outbox_event()
                RETURNS trigger LANGUAGE plpgsql SECURITY DEFINER
                SET search_path = pg_catalog, public AS $function$
                DECLARE event_type text;
                DECLARE event_time timestamp without time zone;
                BEGIN
                    IF TG_OP = 'INSERT' AND NEW."Status" = 'Posted' THEN
                        event_type := 'finance.payment.posted';
                        event_time := coalesce(NEW."CreatedOn", (CURRENT_TIMESTAMP AT TIME ZONE 'UTC'));
                        PERFORM public.nexora_write_finance_audit(NEW."BusinessUnitId", 'CustomerPayment',
                            NEW."Id", 'Posted', NEW."CreatedBy", jsonb_build_object(
                                'receiptNumber', NEW."ReceiptNumber", 'amount', NEW."Amount",
                                'version', NEW."Version"), event_time);
                    ELSIF TG_OP = 'UPDATE' AND OLD."Status" = 'Posted' AND NEW."Status" = 'Reversed' THEN
                        event_type := 'finance.payment.reversed';
                        event_time := coalesce(NEW."ReversedOn", (CURRENT_TIMESTAMP AT TIME ZONE 'UTC'));
                        PERFORM public.nexora_write_finance_audit(NEW."BusinessUnitId", 'CustomerPayment',
                            NEW."Id", 'Reversed', NEW."CreatedBy", jsonb_build_object(
                                'receiptNumber', NEW."ReceiptNumber", 'amount', NEW."Amount",
                                'version', NEW."Version"), event_time);
                    ELSE
                        RETURN NEW;
                    END IF;
                    PERFORM public.nexora_write_finance_outbox(NEW."BusinessUnitId", 'CustomerPayment',
                        NEW."Id", NEW."Version", event_type, jsonb_build_object(
                            'Id', NEW."Id", 'Status', NEW."Status", 'ReceiptNumber', NEW."ReceiptNumber",
                            'CustomerId', NEW."CustomerId", 'CommercialCaseId', NEW."CommercialCaseId",
                            'CurrencyId', NEW."CurrencyId", 'Actor', NEW."CreatedBy", 'Version', NEW."Version"),
                        event_time);
                    RETURN NEW;
                END
                $function$;

                CREATE OR REPLACE FUNCTION public.nexora_finance_reject_truncate()
                RETURNS trigger LANGUAGE plpgsql SECURITY DEFINER
                SET search_path = pg_catalog, public AS $function$
                BEGIN
                    RAISE EXCEPTION 'governed commercial finance records cannot be truncated' USING ERRCODE = '55000';
                END
                $function$;
                CREATE TRIGGER trg_receivable_documents_reject_truncate
                    BEFORE TRUNCATE ON public."ReceivableDocuments"
                    FOR EACH STATEMENT EXECUTE FUNCTION public.nexora_finance_reject_truncate();
                CREATE TRIGGER trg_receivable_lines_reject_truncate
                    BEFORE TRUNCATE ON public."ReceivableDocumentLines"
                    FOR EACH STATEMENT EXECUTE FUNCTION public.nexora_finance_reject_truncate();
                CREATE TRIGGER trg_payment_allocations_reject_truncate
                    BEFORE TRUNCATE ON public."PaymentAllocations"
                    FOR EACH STATEMENT EXECUTE FUNCTION public.nexora_finance_reject_truncate();
                CREATE TRIGGER trg_commercial_finance_audits_reject_truncate
                    BEFORE TRUNCATE ON public."CommercialFinanceAudits"
                    FOR EACH STATEMENT EXECUTE FUNCTION public.nexora_finance_reject_truncate();
                CREATE TRIGGER trg_legal_document_counters_reject_truncate
                    BEFORE TRUNCATE ON public."LegalDocumentCounters"
                    FOR EACH STATEMENT EXECUTE FUNCTION public.nexora_finance_reject_truncate();

                INSERT INTO public."Module" ("ModuleName", "Description", "IsActive", "CreatedBy", "CreatedOn")
                VALUES ('Receivable Adjustments', 'Governed credit and debit note creation and approval',
                        true, 'migration:receivable-adjustments:v1', now())
                ON CONFLICT ("ModuleName") DO NOTHING;

                INSERT INTO public."RolePermissions"
                    ("RoleID", "ModuleID", "BusinessUnitID", "CanCreate", "CanEdit", "CanDelete", "CreatedBy", "CreatedOn")
                SELECT role."SetupID", module."ID", role."BusinessUnitID", true, true, false,
                       'migration:receivable-adjustments:v1', now()
                FROM public."Setup_Master" role
                CROSS JOIN public."Module" module
                WHERE lower(replace(role."SetupType", ' ', '')) = 'role'
                  AND module."ModuleName" = 'Receivable Adjustments'
                  AND (upper(COALESCE(role."SetupCode", '')) ~ '(FINANCE|ACCOUNT|ADMIN)'
                       OR upper(COALESCE(role."SetupValue", '')) ~ '(FINANCE|ACCOUNT|ADMIN)')
                  AND NOT EXISTS (
                      SELECT 1 FROM public."RolePermissions" existing
                      WHERE existing."RoleID" = role."SetupID"
                        AND existing."BusinessUnitID" = role."BusinessUnitID"
                        AND existing."ModuleID" = module."ID");

                ALTER TABLE public."ReceivableDocuments" ENABLE ROW LEVEL SECURITY;
                ALTER TABLE public."ReceivableDocumentLines" ENABLE ROW LEVEL SECURITY;
                ALTER TABLE public."PaymentAllocations" ENABLE ROW LEVEL SECURITY;
                ALTER TABLE public."LegalDocumentCounters" ENABLE ROW LEVEL SECURITY;
                ALTER TABLE public."CommercialFinanceAudits" ENABLE ROW LEVEL SECURITY;
                ALTER TABLE public."FinanceOutboxMessages" ENABLE ROW LEVEL SECURITY;
                ALTER TABLE public."ReceivableDocuments" FORCE ROW LEVEL SECURITY;
                ALTER TABLE public."ReceivableDocumentLines" FORCE ROW LEVEL SECURITY;
                ALTER TABLE public."PaymentAllocations" FORCE ROW LEVEL SECURITY;
                ALTER TABLE public."LegalDocumentCounters" FORCE ROW LEVEL SECURITY;
                ALTER TABLE public."CommercialFinanceAudits" FORCE ROW LEVEL SECURITY;
                ALTER TABLE public."FinanceOutboxMessages" FORCE ROW LEVEL SECURITY;

                REVOKE INSERT, UPDATE, DELETE, TRUNCATE ON public."CommercialFinanceAudits" FROM nexora_tenant_app;
                REVOKE ALL ON SEQUENCE public."CommercialFinanceAudits_Id_seq" FROM nexora_tenant_app;
                GRANT SELECT ON public."CommercialFinanceAudits" TO nexora_tenant_app;
                REVOKE ALL ON FUNCTION public.nexora_write_finance_audit(bigint, text, bigint, text, text, jsonb, timestamp without time zone) FROM PUBLIC;
                REVOKE ALL ON FUNCTION public.nexora_write_finance_audit(bigint, text, bigint, text, text, jsonb, timestamp without time zone) FROM nexora_tenant_app;
                REVOKE ALL ON FUNCTION public.nexora_receivable_issued_immutable() FROM PUBLIC;
                REVOKE ALL ON FUNCTION public.nexora_receivable_line_issued_immutable() FROM PUBLIC;
                REVOKE ALL ON FUNCTION public.nexora_payment_allocation_valid() FROM PUBLIC;
                REVOKE ALL ON FUNCTION public.nexora_receivable_outbox_event() FROM PUBLIC;
                REVOKE ALL ON FUNCTION public.nexora_payment_outbox_event() FROM PUBLIC;
                REVOKE ALL ON FUNCTION public.nexora_finance_reject_truncate() FROM PUBLIC;
                GRANT EXECUTE ON FUNCTION public.nexora_receivable_issued_immutable() TO nexora_tenant_app;
                GRANT EXECUTE ON FUNCTION public.nexora_receivable_line_issued_immutable() TO nexora_tenant_app;
                GRANT EXECUTE ON FUNCTION public.nexora_payment_allocation_valid() TO nexora_tenant_app;
                GRANT EXECUTE ON FUNCTION public.nexora_receivable_outbox_event() TO nexora_tenant_app;
                GRANT EXECUTE ON FUNCTION public.nexora_payment_outbox_event() TO nexora_tenant_app;
                GRANT EXECUTE ON FUNCTION public.nexora_finance_reject_truncate() TO nexora_tenant_app;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DELETE FROM public."RolePermissions"
                WHERE "CreatedBy" = 'migration:receivable-adjustments:v1';
                DELETE FROM public."Module"
                WHERE "CreatedBy" = 'migration:receivable-adjustments:v1'
                  AND "ModuleName" = 'Receivable Adjustments';

                DO $block$
                BEGIN
                    IF EXISTS (SELECT 1 FROM public."ReceivableDocuments"
                               WHERE "DocumentType" IN ('CreditNote', 'DebitNote')) THEN
                        RAISE EXCEPTION 'cannot remove receivable adjustment controls while adjustment documents exist';
                    END IF;
                END
                $block$;
                """);
            migrationBuilder.DropForeignKey(
                name: "FK_ReceivableDocumentLines_ReceivableDocumentLines_BusinessUni~",
                table: "ReceivableDocumentLines");

            migrationBuilder.DropCheckConstraint(
                name: "CK_ReceivableDocuments_Type",
                table: "ReceivableDocuments");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_ReceivableDocumentLines_BusinessUnitId_Id",
                table: "ReceivableDocumentLines");

            migrationBuilder.DropIndex(
                name: "IX_ReceivableDocumentLines_BusinessUnitId_ParentDocumentLineId",
                table: "ReceivableDocumentLines");

            migrationBuilder.DropColumn(
                name: "AdjustmentReason",
                table: "ReceivableDocuments");

            migrationBuilder.DropColumn(
                name: "AdjustmentReasonCode",
                table: "ReceivableDocuments");

            migrationBuilder.DropColumn(
                name: "ParentDocumentLineId",
                table: "ReceivableDocumentLines");

            migrationBuilder.Sql("""
                DROP FUNCTION IF EXISTS public.nexora_write_finance_audit(bigint, text, bigint, text, text, jsonb, timestamp without time zone);
                DROP FUNCTION IF EXISTS public.nexora_finance_reject_truncate() CASCADE;

                CREATE OR REPLACE FUNCTION public.nexora_receivable_issued_immutable()
                RETURNS trigger LANGUAGE plpgsql AS $function$
                DECLARE legal_sequence bigint;
                DECLARE fiscal_year integer;
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
                        IF NEW."DocumentType" <> 'Invoice' OR NEW."OrderId" IS NULL THEN
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
                           OR line_tax <> NEW."TaxAmount" OR line_total <> NEW."TotalAmount" THEN
                            RAISE EXCEPTION 'receivable lines and header do not reconcile' USING ERRCODE = '23514';
                        END IF;
                        IF EXISTS (
                            SELECT 1 FROM public."ReceivableDocumentLines" line
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
                        INSERT INTO public."LegalDocumentCounters"
                            ("BusinessUnitId", "DocumentType", "FiscalYear", "NextNumber")
                        VALUES (NEW."BusinessUnitId", NEW."DocumentType", fiscal_year, 2)
                        ON CONFLICT ("BusinessUnitId", "DocumentType", "FiscalYear")
                        DO UPDATE SET "NextNumber" = public."LegalDocumentCounters"."NextNumber" + 1
                        RETURNING "NextNumber" - 1 INTO legal_sequence;
                        NEW."DocumentNumber" := format('INV-%s-%s', fiscal_year,
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
                           OR NEW."VoidedBy" IS NULL OR length(trim(NEW."VoidedBy")) = 0
                           OR NEW."Version" <> OLD."Version" + 1 THEN
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
                DROP TRIGGER IF EXISTS trg_receivable_document_issued_immutable ON public."ReceivableDocuments";
                CREATE TRIGGER trg_receivable_document_issued_immutable
                    BEFORE UPDATE OR DELETE ON public."ReceivableDocuments"
                    FOR EACH ROW EXECUTE FUNCTION public.nexora_receivable_issued_immutable();

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
                DROP TRIGGER IF EXISTS trg_receivable_line_issued_immutable ON public."ReceivableDocumentLines";
                CREATE TRIGGER trg_receivable_line_issued_immutable
                    BEFORE UPDATE OR DELETE ON public."ReceivableDocumentLines"
                    FOR EACH ROW EXECUTE FUNCTION public.nexora_receivable_line_issued_immutable();

                CREATE OR REPLACE FUNCTION public.nexora_payment_allocation_valid()
                RETURNS trigger LANGUAGE plpgsql AS $function$
                DECLARE payment_amount numeric(18,2);
                DECLARE allocated_amount numeric(18,2);
                BEGIN
                    SELECT payment."Amount" INTO payment_amount
                    FROM public."CustomerPayments" payment
                    WHERE payment."Id" = NEW."CustomerPaymentId"
                      AND payment."BusinessUnitId" = NEW."BusinessUnitId" FOR UPDATE;
                    IF payment_amount IS NULL THEN
                        RAISE EXCEPTION 'payment allocation parent is invalid' USING ERRCODE = '23503';
                    END IF;
                    SELECT coalesce(sum(allocation."Amount"), 0) INTO allocated_amount
                    FROM public."PaymentAllocations" allocation
                    WHERE allocation."CustomerPaymentId" = NEW."CustomerPaymentId"
                      AND allocation."BusinessUnitId" = NEW."BusinessUnitId"
                      AND allocation."Id" <> coalesce(NEW."Id", 0);
                    IF allocated_amount + NEW."Amount" > payment_amount THEN
                        RAISE EXCEPTION 'payment allocations exceed payment amount' USING ERRCODE = '23514';
                    END IF;
                    RETURN NEW;
                END
                $function$;

                CREATE OR REPLACE FUNCTION public.nexora_receivable_outbox_event()
                RETURNS trigger LANGUAGE plpgsql SECURITY DEFINER
                SET search_path = pg_catalog, public AS $function$
                DECLARE event_type text;
                DECLARE event_time timestamp without time zone;
                DECLARE event_payload jsonb;
                BEGIN
                    IF TG_OP = 'INSERT' AND NEW."Status" = 'Draft' THEN
                        event_type := 'finance.receivable.draft-created';
                        event_time := coalesce(NEW."CreatedOn", (CURRENT_TIMESTAMP AT TIME ZONE 'UTC'));
                    ELSIF TG_OP = 'UPDATE' AND OLD."Status" = 'Draft' AND NEW."Status" IN ('Issued', 'Cancelled') THEN
                        event_type := CASE NEW."Status" WHEN 'Issued' THEN 'finance.receivable.issued'
                            ELSE 'finance.receivable.cancelled' END;
                        event_time := CASE NEW."Status" WHEN 'Issued' THEN NEW."IssuedOn" ELSE NEW."VoidedOn" END;
                    ELSE
                        RETURN NEW;
                    END IF;
                    event_payload := jsonb_build_object('Id', NEW."Id", 'OrderId', NEW."OrderId",
                        'Status', NEW."Status", 'DocumentNumber', NEW."DocumentNumber", 'Version', NEW."Version");
                    PERFORM public.nexora_write_finance_outbox(NEW."BusinessUnitId", 'ReceivableDocument',
                        NEW."Id", NEW."Version", event_type, event_payload, event_time);
                    RETURN NEW;
                END
                $function$;
                DROP TRIGGER IF EXISTS trg_receivable_outbox_event ON public."ReceivableDocuments";
                CREATE TRIGGER trg_receivable_outbox_event
                    AFTER INSERT OR UPDATE ON public."ReceivableDocuments"
                    FOR EACH ROW EXECUTE FUNCTION public.nexora_receivable_outbox_event();

                CREATE OR REPLACE FUNCTION public.nexora_payment_outbox_event()
                RETURNS trigger LANGUAGE plpgsql SECURITY DEFINER
                SET search_path = pg_catalog, public AS $function$
                DECLARE event_type text;
                DECLARE event_time timestamp without time zone;
                BEGIN
                    IF TG_OP = 'INSERT' AND NEW."Status" = 'Posted' THEN
                        event_type := 'finance.payment.posted';
                        event_time := COALESCE(NEW."CreatedOn", (CURRENT_TIMESTAMP AT TIME ZONE 'UTC'));
                    ELSIF TG_OP = 'UPDATE' AND OLD."Status" = 'Posted' AND NEW."Status" = 'Reversed' THEN
                        event_type := 'finance.payment.reversed';
                        event_time := COALESCE(NEW."ReversedOn", (CURRENT_TIMESTAMP AT TIME ZONE 'UTC'));
                    ELSE
                        RETURN NEW;
                    END IF;
                    PERFORM public.nexora_write_finance_outbox(NEW."BusinessUnitId", 'CustomerPayment',
                        NEW."Id", NEW."Version", event_type,
                        jsonb_build_object('Id', NEW."Id", 'Status', NEW."Status",
                            'ReceiptNumber', NEW."ReceiptNumber", 'Version', NEW."Version"), event_time);
                    RETURN NEW;
                END
                $function$;

                ALTER TABLE public."ReceivableDocuments" NO FORCE ROW LEVEL SECURITY;
                ALTER TABLE public."ReceivableDocumentLines" NO FORCE ROW LEVEL SECURITY;
                ALTER TABLE public."PaymentAllocations" NO FORCE ROW LEVEL SECURITY;
                ALTER TABLE public."LegalDocumentCounters" NO FORCE ROW LEVEL SECURITY;
                ALTER TABLE public."CommercialFinanceAudits" NO FORCE ROW LEVEL SECURITY;
                ALTER TABLE public."FinanceOutboxMessages" NO FORCE ROW LEVEL SECURITY;

                GRANT SELECT, INSERT ON public."CommercialFinanceAudits" TO nexora_tenant_app;
                GRANT USAGE ON SEQUENCE public."CommercialFinanceAudits_Id_seq" TO nexora_tenant_app;
                """);
        }
    }
}
