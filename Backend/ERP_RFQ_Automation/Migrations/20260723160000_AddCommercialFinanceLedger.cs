using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace ERP_RFQ_Automation.Migrations
{
    /// <inheritdoc />
    public partial class AddCommercialFinanceLedger : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddUniqueConstraint(
                name: "AK_Orders_BusinessUnitID_ID",
                table: "Orders",
                columns: new[] { "BusinessUnitID", "ID" });

            migrationBuilder.CreateTable(
                name: "CommercialFinanceAudits",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    BusinessUnitId = table.Column<long>(type: "bigint", nullable: false),
                    AggregateType = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    AggregateId = table.Column<long>(type: "bigint", nullable: false),
                    Action = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    Actor = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    OccurredOn = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    DetailJson = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CommercialFinanceAudits", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "CustomerPayments",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    BusinessUnitId = table.Column<long>(type: "bigint", nullable: false),
                    CustomerId = table.Column<long>(type: "bigint", nullable: false),
                    CommercialCaseId = table.Column<long>(type: "bigint", nullable: true),
                    CurrencyId = table.Column<long>(type: "bigint", nullable: true),
                    ReceiptNumber = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    PaymentDate = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    Amount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    Method = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    BankReference = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    IdempotencyKey = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    RequestHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false),
                    ReversedOn = table.Column<DateTime>(type: "timestamp without time zone", nullable: true),
                    ReversalReason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    CreatedBy = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    CreatedOn = table.Column<DateTime>(type: "timestamp without time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CustomerPayments", x => x.Id);
                    table.UniqueConstraint("AK_CustomerPayments_BusinessUnitId_Id", x => new { x.BusinessUnitId, x.Id });
                    table.CheckConstraint("CK_CustomerPayments_Amount", "\"Amount\" > 0");
                    table.ForeignKey(
                        name: "FK_CustomerPayments_CommercialCases_BusinessUnitId_CommercialC~",
                        columns: x => new { x.BusinessUnitId, x.CommercialCaseId },
                        principalTable: "CommercialCases",
                        principalColumns: new[] { "BusinessUnitID", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CustomerPayments_Currency_CurrencyId",
                        column: x => x.CurrencyId,
                        principalTable: "Currency",
                        principalColumn: "ID",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CustomerPayments_Customers_CustomerId",
                        column: x => x.CustomerId,
                        principalTable: "Customers",
                        principalColumn: "ID",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "LegalDocumentCounters",
                columns: table => new
                {
                    BusinessUnitId = table.Column<long>(type: "bigint", nullable: false),
                    DocumentType = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    FiscalYear = table.Column<int>(type: "integer", nullable: false),
                    NextNumber = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LegalDocumentCounters", x => new { x.BusinessUnitId, x.DocumentType, x.FiscalYear });
                    table.CheckConstraint("CK_LegalDocumentCounters_Next", "\"NextNumber\" > 0");
                });

            migrationBuilder.CreateTable(
                name: "ReceivableDocuments",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    BusinessUnitId = table.Column<long>(type: "bigint", nullable: false),
                    CommercialCaseId = table.Column<long>(type: "bigint", nullable: true),
                    CustomerId = table.Column<long>(type: "bigint", nullable: false),
                    OrderId = table.Column<long>(type: "bigint", nullable: true),
                    ParentDocumentId = table.Column<long>(type: "bigint", nullable: true),
                    CurrencyId = table.Column<long>(type: "bigint", nullable: true),
                    DocumentType = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    DocumentNumber = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    DocumentDate = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    DueDate = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    IssuedOn = table.Column<DateTime>(type: "timestamp without time zone", nullable: true),
                    VoidedOn = table.Column<DateTime>(type: "timestamp without time zone", nullable: true),
                    VoidReason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    SubTotal = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    DiscountAmount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    TaxAmount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    TotalAmount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    IdempotencyKey = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    RequestHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false),
                    CreatedBy = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    CreatedOn = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    IssuedBy = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ReceivableDocuments", x => x.Id);
                    table.UniqueConstraint("AK_ReceivableDocuments_BusinessUnitId_Id", x => new { x.BusinessUnitId, x.Id });
                    table.CheckConstraint("CK_ReceivableDocuments_Issue", "(\"Status\" = 'Draft' AND \"DocumentNumber\" IS NULL AND \"IssuedOn\" IS NULL) OR (\"Status\" IN ('Issued', 'Void') AND \"DocumentNumber\" IS NOT NULL AND \"IssuedOn\" IS NOT NULL)");
                    table.CheckConstraint("CK_ReceivableDocuments_Reconciles", "\"TotalAmount\" = round(\"SubTotal\" - \"DiscountAmount\" + \"TaxAmount\", 2)");
                    table.CheckConstraint("CK_ReceivableDocuments_Total", "\"TotalAmount\" >= 0 AND \"SubTotal\" >= 0 AND \"DiscountAmount\" >= 0 AND \"TaxAmount\" >= 0");
                    table.ForeignKey(
                        name: "FK_ReceivableDocuments_CommercialCases_BusinessUnitId_Commerci~",
                        columns: x => new { x.BusinessUnitId, x.CommercialCaseId },
                        principalTable: "CommercialCases",
                        principalColumns: new[] { "BusinessUnitID", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ReceivableDocuments_Currency_CurrencyId",
                        column: x => x.CurrencyId,
                        principalTable: "Currency",
                        principalColumn: "ID",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ReceivableDocuments_Customers_CustomerId",
                        column: x => x.CustomerId,
                        principalTable: "Customers",
                        principalColumn: "ID",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ReceivableDocuments_Orders_BusinessUnitId_OrderId",
                        columns: x => new { x.BusinessUnitId, x.OrderId },
                        principalTable: "Orders",
                        principalColumns: new[] { "BusinessUnitID", "ID" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ReceivableDocuments_ReceivableDocuments_BusinessUnitId_ParentDocumentId",
                        columns: x => new { x.BusinessUnitId, x.ParentDocumentId },
                        principalTable: "ReceivableDocuments",
                        principalColumns: new[] { "BusinessUnitId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "PaymentAllocations",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    BusinessUnitId = table.Column<long>(type: "bigint", nullable: false),
                    CustomerPaymentId = table.Column<long>(type: "bigint", nullable: false),
                    ReceivableDocumentId = table.Column<long>(type: "bigint", nullable: false),
                    Amount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    CreatedOn = table.Column<DateTime>(type: "timestamp without time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PaymentAllocations", x => x.Id);
                    table.CheckConstraint("CK_PaymentAllocations_Amount", "\"Amount\" > 0");
                    table.ForeignKey(
                        name: "FK_PaymentAllocations_CustomerPayments_BusinessUnitId_Customer~",
                        columns: x => new { x.BusinessUnitId, x.CustomerPaymentId },
                        principalTable: "CustomerPayments",
                        principalColumns: new[] { "BusinessUnitId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PaymentAllocations_ReceivableDocuments_BusinessUnitId_Recei~",
                        columns: x => new { x.BusinessUnitId, x.ReceivableDocumentId },
                        principalTable: "ReceivableDocuments",
                        principalColumns: new[] { "BusinessUnitId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ReceivableDocumentLines",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    BusinessUnitId = table.Column<long>(type: "bigint", nullable: false),
                    ReceivableDocumentId = table.Column<long>(type: "bigint", nullable: false),
                    OrderItemId = table.Column<long>(type: "bigint", nullable: true),
                    Description = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    Quantity = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: false),
                    UnitPrice = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    DiscountAmount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    TaxAmount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    LineTotal = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ReceivableDocumentLines", x => x.Id);
                    table.CheckConstraint("CK_ReceivableDocumentLines_Money", "\"Quantity\" > 0 AND \"UnitPrice\" >= 0 AND \"DiscountAmount\" >= 0 AND \"TaxAmount\" >= 0 AND \"LineTotal\" >= 0");
                    table.CheckConstraint("CK_ReceivableDocumentLines_Reconciles", "\"LineTotal\" = round(round(\"Quantity\" * \"UnitPrice\", 2) - \"DiscountAmount\" + \"TaxAmount\", 2)");
                    table.ForeignKey(
                        name: "FK_ReceivableDocumentLines_OrderItems_OrderItemId",
                        column: x => x.OrderItemId,
                        principalTable: "OrderItems",
                        principalColumn: "ID",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ReceivableDocumentLines_ReceivableDocuments_BusinessUnitId_~",
                        columns: x => new { x.BusinessUnitId, x.ReceivableDocumentId },
                        principalTable: "ReceivableDocuments",
                        principalColumns: new[] { "BusinessUnitId", "Id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CommercialFinanceAudits_BusinessUnitId_AggregateType_Aggreg~",
                table: "CommercialFinanceAudits",
                columns: new[] { "BusinessUnitId", "AggregateType", "AggregateId", "OccurredOn" });

            migrationBuilder.CreateIndex(
                name: "IX_CustomerPayments_BusinessUnitId_CommercialCaseId",
                table: "CustomerPayments",
                columns: new[] { "BusinessUnitId", "CommercialCaseId" });

            migrationBuilder.CreateIndex(
                name: "IX_CustomerPayments_CurrencyId",
                table: "CustomerPayments",
                column: "CurrencyId");

            migrationBuilder.CreateIndex(
                name: "IX_CustomerPayments_CustomerId",
                table: "CustomerPayments",
                column: "CustomerId");

            migrationBuilder.CreateIndex(
                name: "UX_CustomerPayments_BU_Idempotency",
                table: "CustomerPayments",
                columns: new[] { "BusinessUnitId", "IdempotencyKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UX_CustomerPayments_BU_Number",
                table: "CustomerPayments",
                columns: new[] { "BusinessUnitId", "ReceiptNumber" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PaymentAllocations_BusinessUnitId_ReceivableDocumentId",
                table: "PaymentAllocations",
                columns: new[] { "BusinessUnitId", "ReceivableDocumentId" });

            migrationBuilder.CreateIndex(
                name: "UX_PaymentAllocations_BU_Payment_Document",
                table: "PaymentAllocations",
                columns: new[] { "BusinessUnitId", "CustomerPaymentId", "ReceivableDocumentId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ReceivableDocumentLines_BusinessUnitId_ReceivableDocumentId",
                table: "ReceivableDocumentLines",
                columns: new[] { "BusinessUnitId", "ReceivableDocumentId" });

            migrationBuilder.CreateIndex(
                name: "IX_ReceivableDocumentLines_OrderItemId",
                table: "ReceivableDocumentLines",
                column: "OrderItemId");

            migrationBuilder.CreateIndex(
                name: "IX_ReceivableDocuments_BU_Status_Due",
                table: "ReceivableDocuments",
                columns: new[] { "BusinessUnitId", "Status", "DueDate" });

            migrationBuilder.CreateIndex(
                name: "IX_ReceivableDocuments_BusinessUnitId_CommercialCaseId",
                table: "ReceivableDocuments",
                columns: new[] { "BusinessUnitId", "CommercialCaseId" });

            migrationBuilder.CreateIndex(
                name: "IX_ReceivableDocuments_BusinessUnitId_OrderId",
                table: "ReceivableDocuments",
                columns: new[] { "BusinessUnitId", "OrderId" });

            migrationBuilder.CreateIndex(
                name: "IX_ReceivableDocuments_CurrencyId",
                table: "ReceivableDocuments",
                column: "CurrencyId");

            migrationBuilder.CreateIndex(
                name: "IX_ReceivableDocuments_CustomerId",
                table: "ReceivableDocuments",
                column: "CustomerId");

            migrationBuilder.CreateIndex(
                name: "IX_ReceivableDocuments_BusinessUnitId_ParentDocumentId",
                table: "ReceivableDocuments",
                columns: new[] { "BusinessUnitId", "ParentDocumentId" });

            migrationBuilder.CreateIndex(
                name: "UX_ReceivableDocuments_BU_Idempotency",
                table: "ReceivableDocuments",
                columns: new[] { "BusinessUnitId", "IdempotencyKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UX_ReceivableDocuments_BU_Number",
                table: "ReceivableDocuments",
                columns: new[] { "BusinessUnitId", "DocumentNumber" },
                unique: true,
                filter: "\"DocumentNumber\" IS NOT NULL");

            migrationBuilder.Sql("""
                CREATE OR REPLACE FUNCTION public.nexora_finance_audit_append_only()
                RETURNS trigger LANGUAGE plpgsql AS $function$
                BEGIN
                    RAISE EXCEPTION 'commercial finance audit records are append-only' USING ERRCODE = '55000';
                END
                $function$;

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

                CREATE OR REPLACE FUNCTION public.nexora_payment_posted_immutable()
                RETURNS trigger LANGUAGE plpgsql AS $function$
                BEGIN
                    IF TG_OP = 'DELETE' THEN
                        RAISE EXCEPTION 'posted customer payments cannot be deleted' USING ERRCODE = '55000';
                    END IF;
                    IF OLD."Status" = 'Posted' AND NEW."Status" = 'Reversed'
                       AND NEW."ReversedOn" IS NOT NULL AND length(trim(NEW."ReversalReason")) > 0
                       AND NEW."Version" = OLD."Version" + 1
                       AND (NEW."BusinessUnitId", NEW."CustomerId", NEW."CommercialCaseId", NEW."CurrencyId",
                            NEW."ReceiptNumber", NEW."PaymentDate", NEW."Amount", NEW."Method", NEW."BankReference",
                            NEW."IdempotencyKey", NEW."RequestHash", NEW."CreatedBy", NEW."CreatedOn")
                           IS NOT DISTINCT FROM
                           (OLD."BusinessUnitId", OLD."CustomerId", OLD."CommercialCaseId", OLD."CurrencyId",
                            OLD."ReceiptNumber", OLD."PaymentDate", OLD."Amount", OLD."Method", OLD."BankReference",
                            OLD."IdempotencyKey", OLD."RequestHash", OLD."CreatedBy", OLD."CreatedOn") THEN
                        RETURN NEW;
                    END IF;
                    RAISE EXCEPTION 'posted customer payments are immutable; use a governed reversal' USING ERRCODE = '55000';
                END
                $function$;

                CREATE OR REPLACE FUNCTION public.nexora_payment_allocation_valid()
                RETURNS trigger LANGUAGE plpgsql AS $function$
                DECLARE payment_amount numeric(18,2);
                DECLARE allocated_amount numeric(18,2);
                BEGIN
                    SELECT payment."Amount" INTO payment_amount
                    FROM public."CustomerPayments" payment
                    WHERE payment."Id" = NEW."CustomerPaymentId"
                      AND payment."BusinessUnitId" = NEW."BusinessUnitId"
                    FOR UPDATE;
                    IF payment_amount IS NULL THEN
                        RAISE EXCEPTION 'payment allocation parent is invalid' USING ERRCODE = '23503';
                    END IF;
                    SELECT COALESCE(sum(allocation."Amount"), 0) INTO allocated_amount
                    FROM public."PaymentAllocations" allocation
                    WHERE allocation."CustomerPaymentId" = NEW."CustomerPaymentId"
                      AND allocation."BusinessUnitId" = NEW."BusinessUnitId"
                      AND allocation."Id" <> COALESCE(NEW."Id", 0);
                    IF allocated_amount + NEW."Amount" > payment_amount THEN
                        RAISE EXCEPTION 'payment allocations exceed payment amount' USING ERRCODE = '23514';
                    END IF;
                    RETURN NEW;
                END
                $function$;

                CREATE OR REPLACE FUNCTION public.nexora_receivable_order_item_valid()
                RETURNS trigger LANGUAGE plpgsql AS $function$
                BEGIN
                    IF NEW."OrderItemId" IS NOT NULL AND NOT EXISTS (
                        SELECT 1
                        FROM public."ReceivableDocuments" document
                        JOIN public."OrderItems" item ON item."ID" = NEW."OrderItemId"
                        JOIN public."Orders" sales_order ON sales_order."ID" = item."OrderID"
                        WHERE document."Id" = NEW."ReceivableDocumentId"
                          AND document."BusinessUnitId" = NEW."BusinessUnitId"
                          AND sales_order."ID" = document."OrderId"
                          AND sales_order."BusinessUnitID" = NEW."BusinessUnitId") THEN
                        RAISE EXCEPTION 'receivable line order item does not belong to the tenant order' USING ERRCODE = '23503';
                    END IF;
                    RETURN NEW;
                END
                $function$;

                CREATE TRIGGER trg_commercial_finance_audit_append_only
                    BEFORE UPDATE OR DELETE ON public."CommercialFinanceAudits"
                    FOR EACH ROW EXECUTE FUNCTION public.nexora_finance_audit_append_only();
                CREATE TRIGGER trg_receivable_document_issued_immutable
                    BEFORE UPDATE OR DELETE ON public."ReceivableDocuments"
                    FOR EACH ROW EXECUTE FUNCTION public.nexora_receivable_issued_immutable();
                CREATE TRIGGER trg_receivable_line_issued_immutable
                    BEFORE UPDATE OR DELETE ON public."ReceivableDocumentLines"
                    FOR EACH ROW EXECUTE FUNCTION public.nexora_receivable_line_issued_immutable();
                CREATE TRIGGER trg_payment_posted_immutable
                    BEFORE UPDATE OR DELETE ON public."CustomerPayments"
                    FOR EACH ROW EXECUTE FUNCTION public.nexora_payment_posted_immutable();
                CREATE TRIGGER trg_payment_allocation_append_only
                    BEFORE UPDATE OR DELETE ON public."PaymentAllocations"
                    FOR EACH ROW EXECUTE FUNCTION public.nexora_finance_audit_append_only();
                CREATE TRIGGER trg_payment_allocation_amount
                    BEFORE INSERT OR UPDATE ON public."PaymentAllocations"
                    FOR EACH ROW EXECUTE FUNCTION public.nexora_payment_allocation_valid();
                CREATE TRIGGER trg_receivable_order_item_ownership
                    BEFORE INSERT OR UPDATE ON public."ReceivableDocumentLines"
                    FOR EACH ROW EXECUTE FUNCTION public.nexora_receivable_order_item_valid();

                INSERT INTO public."Module" ("ModuleName", "Description", "IsActive", "CreatedBy", "CreatedOn")
                VALUES
                    ('Accounts Receivable', 'Governed invoices and accounts receivable', true, 'migration:commercial-finance:v1', now()),
                    ('Customer Payments', 'Governed customer receipts and reversals', true, 'migration:commercial-finance:v1', now())
                ON CONFLICT ("ModuleName") DO NOTHING;

                INSERT INTO public."RolePermissions"
                    ("RoleID", "ModuleID", "BusinessUnitID", "CanCreate", "CanEdit", "CanDelete", "CreatedBy", "CreatedOn")
                SELECT role."SetupID", module."ID", role."BusinessUnitID", true, true, false,
                       'migration:commercial-finance:v1', now()
                FROM public."Setup_Master" role
                CROSS JOIN public."Module" module
                WHERE lower(replace(role."SetupType", ' ', '')) = 'role'
                  AND module."ModuleName" IN ('Accounts Receivable', 'Customer Payments')
                  AND (upper(COALESCE(role."SetupCode", '')) ~ '(FINANCE|ACCOUNT|ADMIN)'
                       OR upper(COALESCE(role."SetupValue", '')) ~ '(FINANCE|ACCOUNT|ADMIN)')
                  AND NOT EXISTS (
                      SELECT 1 FROM public."RolePermissions" existing
                      WHERE existing."RoleID" = role."SetupID"
                        AND existing."BusinessUnitID" = role."BusinessUnitID"
                        AND existing."ModuleID" = module."ID");

                DO $block$
                DECLARE finance_table text;
                BEGIN
                    FOREACH finance_table IN ARRAY ARRAY[
                        'ReceivableDocuments', 'ReceivableDocumentLines', 'CustomerPayments',
                        'PaymentAllocations', 'LegalDocumentCounters', 'CommercialFinanceAudits']
                    LOOP
                        EXECUTE format('ALTER TABLE public.%I ENABLE ROW LEVEL SECURITY', finance_table);
                        EXECUTE format('DROP POLICY IF EXISTS nexora_tenant_isolation ON public.%I', finance_table);
                        EXECUTE format(
                            'CREATE POLICY nexora_tenant_isolation ON public.%I TO nexora_tenant_app USING ("BusinessUnitId" = NULLIF(current_setting(''nexora.business_unit_id'', true), '''')::bigint) WITH CHECK ("BusinessUnitId" = NULLIF(current_setting(''nexora.business_unit_id'', true), '''')::bigint)',
                            finance_table);
                    END LOOP;

                    IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'nexora_tenant_app') THEN
                        GRANT SELECT, INSERT, UPDATE ON public."ReceivableDocuments", public."ReceivableDocumentLines",
                            public."CustomerPayments", public."PaymentAllocations", public."LegalDocumentCounters" TO nexora_tenant_app;
                        GRANT SELECT, INSERT ON public."CommercialFinanceAudits" TO nexora_tenant_app;
                        GRANT USAGE ON SEQUENCE public."ReceivableDocuments_Id_seq",
                            public."ReceivableDocumentLines_Id_seq", public."CustomerPayments_Id_seq",
                            public."PaymentAllocations_Id_seq", public."CommercialFinanceAudits_Id_seq" TO nexora_tenant_app;
                    END IF;
                END
                $block$;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DELETE FROM public."RolePermissions"
                WHERE "CreatedBy" = 'migration:commercial-finance:v1';
                DELETE FROM public."Module"
                WHERE "CreatedBy" = 'migration:commercial-finance:v1'
                  AND "ModuleName" IN ('Accounts Receivable', 'Customer Payments');
                """);

            migrationBuilder.DropTable(
                name: "CommercialFinanceAudits");

            migrationBuilder.DropTable(
                name: "LegalDocumentCounters");

            migrationBuilder.DropTable(
                name: "PaymentAllocations");

            migrationBuilder.DropTable(
                name: "ReceivableDocumentLines");

            migrationBuilder.DropTable(
                name: "CustomerPayments");

            migrationBuilder.DropTable(
                name: "ReceivableDocuments");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_Orders_BusinessUnitID_ID",
                table: "Orders");

            migrationBuilder.Sql("""
                DROP FUNCTION IF EXISTS public.nexora_payment_posted_immutable();
                DROP FUNCTION IF EXISTS public.nexora_payment_allocation_valid();
                DROP FUNCTION IF EXISTS public.nexora_receivable_order_item_valid();
                DROP FUNCTION IF EXISTS public.nexora_receivable_line_issued_immutable();
                DROP FUNCTION IF EXISTS public.nexora_receivable_issued_immutable();
                DROP FUNCTION IF EXISTS public.nexora_finance_audit_append_only();
                """);
        }
    }
}
