using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace ERP_RFQ_Automation.Migrations
{
    /// <inheritdoc />
    public partial class GovernFinanceExceptions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "CustomerRefunds",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    BusinessUnitId = table.Column<long>(type: "bigint", nullable: false),
                    SourcePaymentId = table.Column<long>(type: "bigint", nullable: false),
                    CustomerId = table.Column<long>(type: "bigint", nullable: false),
                    CommercialCaseId = table.Column<long>(type: "bigint", nullable: true),
                    CurrencyId = table.Column<long>(type: "bigint", nullable: true),
                    RefundNumber = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    RequestedExecutionDate = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    Amount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    Method = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    DestinationReference = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    DestinationVerified = table.Column<bool>(type: "boolean", nullable: false),
                    ReasonCode = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    EvidenceReference = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    PostingStatus = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    JournalReference = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    IdempotencyKey = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    RequestHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false),
                    CreatedBy = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    CreatedOn = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    ApprovedBy = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    ApprovedOn = table.Column<DateTime>(type: "timestamp without time zone", nullable: true),
                    ReleasedBy = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    ReleasedOn = table.Column<DateTime>(type: "timestamp without time zone", nullable: true),
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
                    table.PrimaryKey("PK_CustomerRefunds", x => x.Id);
                    table.UniqueConstraint("AK_CustomerRefunds_BusinessUnitId_Id", x => new { x.BusinessUnitId, x.Id });
                    table.CheckConstraint("CK_CustomerRefunds_Amount", "\"Amount\" > 0");
                    table.ForeignKey(
                        name: "FK_CustomerRefunds_CommercialCases_BusinessUnitId_CommercialCa~",
                        columns: x => new { x.BusinessUnitId, x.CommercialCaseId },
                        principalTable: "CommercialCases",
                        principalColumns: new[] { "BusinessUnitID", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CustomerRefunds_Currency_CurrencyId",
                        column: x => x.CurrencyId,
                        principalTable: "Currency",
                        principalColumn: "ID",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CustomerRefunds_CustomerPayments_BusinessUnitId_SourcePayme~",
                        columns: x => new { x.BusinessUnitId, x.SourcePaymentId },
                        principalTable: "CustomerPayments",
                        principalColumns: new[] { "BusinessUnitId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CustomerRefunds_Customers_CustomerId",
                        column: x => x.CustomerId,
                        principalTable: "Customers",
                        principalColumn: "ID",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ReceivableWriteOffs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    BusinessUnitId = table.Column<long>(type: "bigint", nullable: false),
                    CustomerId = table.Column<long>(type: "bigint", nullable: false),
                    CommercialCaseId = table.Column<long>(type: "bigint", nullable: true),
                    CurrencyId = table.Column<long>(type: "bigint", nullable: true),
                    WriteOffNumber = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    AccountingDate = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    TotalAmount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    ReasonCode = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    EvidenceReference = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    PostingStatus = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    JournalReference = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    IdempotencyKey = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    RequestHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false),
                    CreatedBy = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    CreatedOn = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    ApprovedBy = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    ApprovedOn = table.Column<DateTime>(type: "timestamp without time zone", nullable: true),
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
                    table.PrimaryKey("PK_ReceivableWriteOffs", x => x.Id);
                    table.UniqueConstraint("AK_ReceivableWriteOffs_BusinessUnitId_Id", x => new { x.BusinessUnitId, x.Id });
                    table.CheckConstraint("CK_ReceivableWriteOffs_Amount", "\"TotalAmount\" > 0");
                    table.ForeignKey(
                        name: "FK_ReceivableWriteOffs_CommercialCases_BusinessUnitId_Commerci~",
                        columns: x => new { x.BusinessUnitId, x.CommercialCaseId },
                        principalTable: "CommercialCases",
                        principalColumns: new[] { "BusinessUnitID", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ReceivableWriteOffs_Currency_CurrencyId",
                        column: x => x.CurrencyId,
                        principalTable: "Currency",
                        principalColumn: "ID",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ReceivableWriteOffs_Customers_CustomerId",
                        column: x => x.CustomerId,
                        principalTable: "Customers",
                        principalColumn: "ID",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "WriteOffAllocations",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    BusinessUnitId = table.Column<long>(type: "bigint", nullable: false),
                    ReceivableWriteOffId = table.Column<long>(type: "bigint", nullable: false),
                    ReceivableDocumentId = table.Column<long>(type: "bigint", nullable: false),
                    Amount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    BalanceBefore = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    BalanceAfter = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WriteOffAllocations", x => x.Id);
                    table.CheckConstraint("CK_WriteOffAllocations_Amount", "\"Amount\" > 0 AND \"BalanceBefore\" >= \"Amount\" AND \"BalanceAfter\" = round(\"BalanceBefore\" - \"Amount\", 2)");
                    table.ForeignKey(
                        name: "FK_WriteOffAllocations_ReceivableDocuments_BusinessUnitId_Rece~",
                        columns: x => new { x.BusinessUnitId, x.ReceivableDocumentId },
                        principalTable: "ReceivableDocuments",
                        principalColumns: new[] { "BusinessUnitId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_WriteOffAllocations_ReceivableWriteOffs_BusinessUnitId_Rece~",
                        columns: x => new { x.BusinessUnitId, x.ReceivableWriteOffId },
                        principalTable: "ReceivableWriteOffs",
                        principalColumns: new[] { "BusinessUnitId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CustomerRefunds_BusinessUnitId_CommercialCaseId",
                table: "CustomerRefunds",
                columns: new[] { "BusinessUnitId", "CommercialCaseId" });

            migrationBuilder.CreateIndex(
                name: "IX_CustomerRefunds_BusinessUnitId_SourcePaymentId",
                table: "CustomerRefunds",
                columns: new[] { "BusinessUnitId", "SourcePaymentId" });

            migrationBuilder.CreateIndex(
                name: "IX_CustomerRefunds_CurrencyId",
                table: "CustomerRefunds",
                column: "CurrencyId");

            migrationBuilder.CreateIndex(
                name: "IX_CustomerRefunds_CustomerId",
                table: "CustomerRefunds",
                column: "CustomerId");

            migrationBuilder.CreateIndex(
                name: "UX_CustomerRefunds_BU_Idempotency",
                table: "CustomerRefunds",
                columns: new[] { "BusinessUnitId", "IdempotencyKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UX_CustomerRefunds_BU_Number",
                table: "CustomerRefunds",
                columns: new[] { "BusinessUnitId", "RefundNumber" },
                unique: true,
                filter: "\"RefundNumber\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_ReceivableWriteOffs_BusinessUnitId_CommercialCaseId",
                table: "ReceivableWriteOffs",
                columns: new[] { "BusinessUnitId", "CommercialCaseId" });

            migrationBuilder.CreateIndex(
                name: "IX_ReceivableWriteOffs_CurrencyId",
                table: "ReceivableWriteOffs",
                column: "CurrencyId");

            migrationBuilder.CreateIndex(
                name: "IX_ReceivableWriteOffs_CustomerId",
                table: "ReceivableWriteOffs",
                column: "CustomerId");

            migrationBuilder.CreateIndex(
                name: "UX_ReceivableWriteOffs_BU_Idempotency",
                table: "ReceivableWriteOffs",
                columns: new[] { "BusinessUnitId", "IdempotencyKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UX_ReceivableWriteOffs_BU_Number",
                table: "ReceivableWriteOffs",
                columns: new[] { "BusinessUnitId", "WriteOffNumber" },
                unique: true,
                filter: "\"WriteOffNumber\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_WriteOffAllocations_BusinessUnitId_ReceivableDocumentId",
                table: "WriteOffAllocations",
                columns: new[] { "BusinessUnitId", "ReceivableDocumentId" });

            migrationBuilder.CreateIndex(
                name: "UX_WriteOffAllocations_BU_WriteOff_Document",
                table: "WriteOffAllocations",
                columns: new[] { "BusinessUnitId", "ReceivableWriteOffId", "ReceivableDocumentId" },
                unique: true);

            migrationBuilder.Sql("""
                ALTER TABLE public."ReceivableWriteOffs"
                    ADD CONSTRAINT "CK_ReceivableWriteOffs_Status"
                    CHECK ("Status" IN ('Draft', 'Posted', 'Cancelled', 'Reversed'));
                ALTER TABLE public."CustomerRefunds"
                    ADD CONSTRAINT "CK_CustomerRefunds_Status"
                    CHECK ("Status" IN ('Draft', 'Approved', 'Released', 'Cancelled', 'Reversed'));

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
                        WHERE document."BusinessUnitId" = business_unit_id AND document."Id" = aggregate_id;
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
                        WHERE payment."BusinessUnitId" = business_unit_id AND payment."Id" = aggregate_id;
                        IF NOT FOUND OR audit_action NOT IN ('Posted', 'Reversed')
                           OR (audit_action = 'Posted' AND aggregate_status <> 'Posted')
                           OR (audit_action = 'Reversed' AND aggregate_status <> 'Reversed') THEN
                            RAISE EXCEPTION 'audit action is inconsistent with the customer payment' USING ERRCODE = '23514';
                        END IF;
                    ELSIF aggregate_type = 'ReceivableWriteOff' THEN
                        SELECT write_off."Status" INTO aggregate_status
                        FROM public."ReceivableWriteOffs" write_off
                        WHERE write_off."BusinessUnitId" = business_unit_id AND write_off."Id" = aggregate_id;
                        IF NOT FOUND OR audit_action NOT IN ('DraftCreated', 'Posted', 'Cancelled', 'Reversed')
                           OR aggregate_status <> (CASE audit_action
                                WHEN 'DraftCreated' THEN 'Draft' ELSE audit_action END) THEN
                            RAISE EXCEPTION 'audit action is inconsistent with the receivable write-off' USING ERRCODE = '23514';
                        END IF;
                    ELSIF aggregate_type = 'CustomerRefund' THEN
                        SELECT refund."Status" INTO aggregate_status
                        FROM public."CustomerRefunds" refund
                        WHERE refund."BusinessUnitId" = business_unit_id AND refund."Id" = aggregate_id;
                        IF NOT FOUND OR audit_action NOT IN ('DraftCreated', 'Approved', 'Released', 'Cancelled', 'Reversed',
                                                             'DisbursementConfirmed', 'DisbursementFailed')
                           OR aggregate_status <> (CASE audit_action
                                WHEN 'DraftCreated' THEN 'Draft'
                                WHEN 'DisbursementConfirmed' THEN 'Released'
                                WHEN 'DisbursementFailed' THEN 'Released'
                                ELSE audit_action END) THEN
                            RAISE EXCEPTION 'audit action is inconsistent with the customer refund' USING ERRCODE = '23514';
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

                CREATE OR REPLACE FUNCTION public.nexora_receivable_live_outstanding(
                    business_unit_id bigint, document_id bigint)
                RETURNS numeric LANGUAGE plpgsql SECURITY DEFINER
                SET search_path = pg_catalog, public AS $function$
                DECLARE document_row record;
                DECLARE issued_credits numeric(18,2);
                DECLARE posted_payments numeric(18,2);
                DECLARE posted_write_offs numeric(18,2);
                BEGIN
                    SELECT document.* INTO document_row
                    FROM public."ReceivableDocuments" document
                    WHERE document."BusinessUnitId" = business_unit_id AND document."Id" = document_id
                    FOR UPDATE;
                    IF NOT FOUND OR document_row."Status" <> 'Issued'
                       OR document_row."DocumentType" NOT IN ('Invoice', 'DebitNote') THEN
                        RAISE EXCEPTION 'write-off requires a same-tenant issued invoice or debit note' USING ERRCODE = '23514';
                    END IF;
                    issued_credits := 0;
                    IF document_row."DocumentType" = 'Invoice' THEN
                        SELECT coalesce(sum(credit."TotalAmount"), 0) INTO issued_credits
                        FROM public."ReceivableDocuments" credit
                        WHERE credit."BusinessUnitId" = business_unit_id
                          AND credit."ParentDocumentId" = document_id
                          AND credit."DocumentType" = 'CreditNote' AND credit."Status" = 'Issued';
                    END IF;
                    SELECT coalesce(sum(allocation."Amount"), 0) INTO posted_payments
                    FROM public."PaymentAllocations" allocation
                    JOIN public."CustomerPayments" payment
                      ON payment."BusinessUnitId" = allocation."BusinessUnitId"
                     AND payment."Id" = allocation."CustomerPaymentId"
                    WHERE allocation."BusinessUnitId" = business_unit_id
                      AND allocation."ReceivableDocumentId" = document_id
                      AND payment."Status" = 'Posted';
                    SELECT coalesce(sum(allocation."Amount"), 0) INTO posted_write_offs
                    FROM public."WriteOffAllocations" allocation
                    JOIN public."ReceivableWriteOffs" write_off
                      ON write_off."BusinessUnitId" = allocation."BusinessUnitId"
                     AND write_off."Id" = allocation."ReceivableWriteOffId"
                    WHERE allocation."BusinessUnitId" = business_unit_id
                      AND allocation."ReceivableDocumentId" = document_id
                      AND write_off."Status" = 'Posted';
                    RETURN round(document_row."TotalAmount" - issued_credits - posted_payments - posted_write_offs, 2);
                END
                $function$;

                CREATE OR REPLACE FUNCTION public.nexora_write_off_allocation_governed()
                RETURNS trigger LANGUAGE plpgsql SECURITY DEFINER
                SET search_path = pg_catalog, public AS $function$
                DECLARE write_off_row record;
                DECLARE document_row record;
                DECLARE live_outstanding numeric(18,2);
                BEGIN
                    IF TG_OP <> 'INSERT' THEN
                        RAISE EXCEPTION 'write-off allocations are immutable' USING ERRCODE = '55000';
                    END IF;
                    SELECT write_off.* INTO write_off_row
                    FROM public."ReceivableWriteOffs" write_off
                    WHERE write_off."BusinessUnitId" = NEW."BusinessUnitId"
                      AND write_off."Id" = NEW."ReceivableWriteOffId" FOR UPDATE;
                    IF NOT FOUND OR write_off_row."Status" <> 'Draft' THEN
                        RAISE EXCEPTION 'allocations may only be appended to a new draft write-off' USING ERRCODE = '55000';
                    END IF;
                    SELECT document.* INTO document_row
                    FROM public."ReceivableDocuments" document
                    WHERE document."BusinessUnitId" = NEW."BusinessUnitId"
                      AND document."Id" = NEW."ReceivableDocumentId" FOR UPDATE;
                    IF NOT FOUND OR document_row."Status" <> 'Issued'
                       OR document_row."DocumentType" NOT IN ('Invoice', 'DebitNote')
                       OR (document_row."CustomerId", document_row."CurrencyId", document_row."CommercialCaseId")
                          IS DISTINCT FROM
                          (write_off_row."CustomerId", write_off_row."CurrencyId", write_off_row."CommercialCaseId") THEN
                        RAISE EXCEPTION 'write-off allocation source identity is invalid' USING ERRCODE = '23514';
                    END IF;
                    live_outstanding := public.nexora_receivable_live_outstanding(
                        NEW."BusinessUnitId", NEW."ReceivableDocumentId");
                    IF NEW."Amount" <= 0 OR NEW."Amount" > live_outstanding
                       OR NEW."BalanceBefore" <> live_outstanding
                       OR NEW."BalanceAfter" <> round(live_outstanding - NEW."Amount", 2) THEN
                        RAISE EXCEPTION 'write-off allocation exceeds or misstates the live document balance' USING ERRCODE = '23514';
                    END IF;
                    RETURN NEW;
                END
                $function$;

                CREATE OR REPLACE FUNCTION public.nexora_write_off_governed()
                RETURNS trigger LANGUAGE plpgsql SECURITY DEFINER
                SET search_path = pg_catalog, public AS $function$
                DECLARE legal_sequence bigint;
                DECLARE fiscal_year integer;
                DECLARE allocation_row record;
                DECLARE allocation_total numeric(18,2);
                DECLARE live_outstanding numeric(18,2);
                BEGIN
                    IF TG_OP = 'INSERT' THEN
                        IF NEW."Status" <> 'Draft' OR NEW."WriteOffNumber" IS NOT NULL OR NEW."Version" <> 1
                           OR NEW."ApprovedBy" IS NOT NULL OR NEW."ApprovedOn" IS NOT NULL
                           OR NEW."CancelledBy" IS NOT NULL OR NEW."CancelledOn" IS NOT NULL OR NEW."CancellationReason" IS NOT NULL
                           OR NEW."ReversedBy" IS NOT NULL OR NEW."ReversedOn" IS NOT NULL
                           OR NEW."ReversalReason" IS NOT NULL OR NEW."ReversalEvidenceReference" IS NOT NULL
                           OR NEW."PostingStatus" <> 'NotPosted' OR NEW."JournalReference" IS NOT NULL
                           OR length(trim(NEW."CreatedBy")) = 0 OR length(trim(NEW."ReasonCode")) = 0
                           OR length(trim(NEW."Reason")) = 0 THEN
                            RAISE EXCEPTION 'write-offs must be created as clean version-one drafts' USING ERRCODE = '23514';
                        END IF;
                        RETURN NEW;
                    END IF;
                    IF TG_OP = 'DELETE' THEN
                        RAISE EXCEPTION 'write-offs cannot be deleted' USING ERRCODE = '55000';
                    END IF;
                    IF OLD."Status" = 'Draft' AND NEW."Status" = 'Posted' THEN
                        IF NEW."ApprovedBy" IS NULL OR length(trim(NEW."ApprovedBy")) = 0
                           OR lower(trim(NEW."ApprovedBy")) = lower(trim(OLD."CreatedBy"))
                           OR NEW."ApprovedOn" IS NULL OR NEW."Version" <> OLD."Version" + 1
                           OR NEW."PostingStatus" <> 'PendingExport' OR NEW."JournalReference" IS NOT NULL
                           OR NEW."WriteOffNumber" IS NULL
                           OR NEW."CancelledBy" IS NOT NULL OR NEW."CancelledOn" IS NOT NULL OR NEW."CancellationReason" IS NOT NULL
                           OR NEW."ReversedBy" IS NOT NULL OR NEW."ReversedOn" IS NOT NULL
                           OR NEW."ReversalReason" IS NOT NULL OR NEW."ReversalEvidenceReference" IS NOT NULL
                           OR (NEW."BusinessUnitId", NEW."CustomerId", NEW."CommercialCaseId", NEW."CurrencyId",
                               NEW."AccountingDate", NEW."TotalAmount", NEW."ReasonCode", NEW."Reason",
                               NEW."EvidenceReference", NEW."IdempotencyKey", NEW."RequestHash", NEW."CreatedBy", NEW."CreatedOn")
                              IS DISTINCT FROM
                              (OLD."BusinessUnitId", OLD."CustomerId", OLD."CommercialCaseId", OLD."CurrencyId",
                               OLD."AccountingDate", OLD."TotalAmount", OLD."ReasonCode", OLD."Reason",
                               OLD."EvidenceReference", OLD."IdempotencyKey", OLD."RequestHash", OLD."CreatedBy", OLD."CreatedOn") THEN
                            RAISE EXCEPTION 'invalid governed write-off posting transition' USING ERRCODE = '55000';
                        END IF;
                        SELECT coalesce(sum(allocation."Amount"), 0) INTO allocation_total
                        FROM public."WriteOffAllocations" allocation
                        WHERE allocation."BusinessUnitId" = NEW."BusinessUnitId"
                          AND allocation."ReceivableWriteOffId" = NEW."Id";
                        IF allocation_total <> NEW."TotalAmount" OR allocation_total <= 0 THEN
                            RAISE EXCEPTION 'write-off allocations do not reconcile to the header' USING ERRCODE = '23514';
                        END IF;
                        FOR allocation_row IN
                            SELECT allocation.* FROM public."WriteOffAllocations" allocation
                            WHERE allocation."BusinessUnitId" = NEW."BusinessUnitId"
                              AND allocation."ReceivableWriteOffId" = NEW."Id"
                            ORDER BY allocation."ReceivableDocumentId"
                        LOOP
                            live_outstanding := public.nexora_receivable_live_outstanding(
                                allocation_row."BusinessUnitId", allocation_row."ReceivableDocumentId");
                            IF allocation_row."Amount" > live_outstanding
                               OR allocation_row."BalanceBefore" <> live_outstanding
                               OR allocation_row."BalanceAfter" <> round(live_outstanding - allocation_row."Amount", 2) THEN
                                RAISE EXCEPTION 'write-off posting exceeds or misstates a live document balance' USING ERRCODE = '23514';
                            END IF;
                        END LOOP;
                        fiscal_year := extract(year from NEW."AccountingDate")::integer;
                        INSERT INTO public."LegalDocumentCounters"
                            ("BusinessUnitId", "DocumentType", "FiscalYear", "NextNumber")
                        VALUES (NEW."BusinessUnitId", 'WriteOff', fiscal_year, 2)
                        ON CONFLICT ("BusinessUnitId", "DocumentType", "FiscalYear")
                        DO UPDATE SET "NextNumber" = public."LegalDocumentCounters"."NextNumber" + 1
                        RETURNING "NextNumber" - 1 INTO legal_sequence;
                        NEW."WriteOffNumber" := format('WOF-%s-%s', fiscal_year, lpad(legal_sequence::text, 6, '0'));
                        RETURN NEW;
                    END IF;
                    IF OLD."Status" = 'Draft' AND NEW."Status" = 'Cancelled' THEN
                        IF NEW."CancelledBy" IS NULL OR length(trim(NEW."CancelledBy")) = 0
                           OR NEW."CancelledOn" IS NULL OR NEW."CancellationReason" IS NULL
                           OR length(trim(NEW."CancellationReason")) = 0 OR NEW."Version" <> OLD."Version" + 1
                           OR NEW."WriteOffNumber" IS NOT NULL OR NEW."PostingStatus" <> OLD."PostingStatus"
                           OR NEW."ApprovedBy" IS NOT NULL OR NEW."ApprovedOn" IS NOT NULL
                           OR NEW."ReversedBy" IS NOT NULL OR NEW."ReversedOn" IS NOT NULL
                           OR NEW."ReversalReason" IS NOT NULL OR NEW."ReversalEvidenceReference" IS NOT NULL
                           OR (NEW."BusinessUnitId", NEW."CustomerId", NEW."CommercialCaseId", NEW."CurrencyId",
                               NEW."AccountingDate", NEW."TotalAmount", NEW."ReasonCode", NEW."Reason", NEW."EvidenceReference",
                               NEW."JournalReference", NEW."IdempotencyKey", NEW."RequestHash", NEW."CreatedBy", NEW."CreatedOn")
                              IS DISTINCT FROM
                              (OLD."BusinessUnitId", OLD."CustomerId", OLD."CommercialCaseId", OLD."CurrencyId",
                               OLD."AccountingDate", OLD."TotalAmount", OLD."ReasonCode", OLD."Reason", OLD."EvidenceReference",
                               OLD."JournalReference", OLD."IdempotencyKey", OLD."RequestHash", OLD."CreatedBy", OLD."CreatedOn") THEN
                            RAISE EXCEPTION 'invalid governed write-off cancellation transition' USING ERRCODE = '55000';
                        END IF;
                        RETURN NEW;
                    END IF;
                    IF OLD."Status" = 'Posted' AND NEW."Status" = 'Reversed' THEN
                        IF NEW."ReversedBy" IS NULL OR length(trim(NEW."ReversedBy")) = 0
                           OR lower(trim(NEW."ReversedBy")) IN (lower(trim(OLD."CreatedBy")), lower(trim(OLD."ApprovedBy")))
                           OR NEW."ReversedOn" IS NULL OR NEW."ReversalReason" IS NULL
                           OR length(trim(NEW."ReversalReason")) = 0
                           OR NEW."ReversalEvidenceReference" IS NULL OR length(trim(NEW."ReversalEvidenceReference")) = 0
                           OR NEW."Version" <> OLD."Version" + 1 OR NEW."PostingStatus" <> 'ReversalPendingExport'
                           OR (NEW."BusinessUnitId", NEW."CustomerId", NEW."CommercialCaseId", NEW."CurrencyId",
                               NEW."WriteOffNumber", NEW."AccountingDate", NEW."TotalAmount", NEW."ReasonCode", NEW."Reason",
                               NEW."EvidenceReference", NEW."JournalReference", NEW."IdempotencyKey", NEW."RequestHash",
                               NEW."CreatedBy", NEW."CreatedOn", NEW."ApprovedBy", NEW."ApprovedOn",
                               NEW."CancelledBy", NEW."CancelledOn", NEW."CancellationReason")
                              IS DISTINCT FROM
                              (OLD."BusinessUnitId", OLD."CustomerId", OLD."CommercialCaseId", OLD."CurrencyId",
                               OLD."WriteOffNumber", OLD."AccountingDate", OLD."TotalAmount", OLD."ReasonCode", OLD."Reason",
                               OLD."EvidenceReference", OLD."JournalReference", OLD."IdempotencyKey", OLD."RequestHash",
                               OLD."CreatedBy", OLD."CreatedOn", OLD."ApprovedBy", OLD."ApprovedOn",
                               OLD."CancelledBy", OLD."CancelledOn", OLD."CancellationReason") THEN
                            RAISE EXCEPTION 'invalid governed write-off reversal transition' USING ERRCODE = '55000';
                        END IF;
                        RETURN NEW;
                    END IF;
                    RAISE EXCEPTION 'write-off fields and lifecycle are immutable outside governed transitions' USING ERRCODE = '55000';
                END
                $function$;

                CREATE OR REPLACE FUNCTION public.nexora_refund_governed()
                RETURNS trigger LANGUAGE plpgsql SECURITY DEFINER
                SET search_path = pg_catalog, public AS $function$
                DECLARE payment_row record;
                DECLARE allocated_amount numeric(18,2);
                DECLARE reserved_amount numeric(18,2);
                DECLARE legal_sequence bigint;
                DECLARE fiscal_year integer;
                BEGIN
                    IF TG_OP = 'DELETE' THEN
                        RAISE EXCEPTION 'customer refunds cannot be deleted' USING ERRCODE = '55000';
                    END IF;
                    SELECT payment.* INTO payment_row
                    FROM public."CustomerPayments" payment
                    WHERE payment."BusinessUnitId" = NEW."BusinessUnitId"
                      AND payment."Id" = NEW."SourcePaymentId" FOR UPDATE;
                    IF NOT FOUND OR payment_row."Status" <> 'Posted'
                       OR (NEW."CustomerId", NEW."CurrencyId", NEW."CommercialCaseId") IS DISTINCT FROM
                          (payment_row."CustomerId", payment_row."CurrencyId", payment_row."CommercialCaseId") THEN
                        RAISE EXCEPTION 'refund source receipt identity or status is invalid' USING ERRCODE = '23514';
                    END IF;
                    IF TG_OP = 'INSERT' THEN
                        IF NEW."Status" <> 'Draft' OR NEW."RefundNumber" IS NOT NULL OR NEW."Version" <> 1
                           OR NOT NEW."DestinationVerified" OR length(trim(NEW."DestinationReference")) = 0
                           OR length(trim(NEW."Method")) = 0 OR length(trim(NEW."ReasonCode")) = 0
                           OR length(trim(NEW."Reason")) = 0 OR length(trim(NEW."CreatedBy")) = 0
                           OR NEW."PostingStatus" <> 'NotReleased' OR NEW."JournalReference" IS NOT NULL
                           OR NEW."ApprovedBy" IS NOT NULL OR NEW."ApprovedOn" IS NOT NULL
                           OR NEW."ReleasedBy" IS NOT NULL OR NEW."ReleasedOn" IS NOT NULL
                           OR NEW."DisbursementUpdatedBy" IS NOT NULL OR NEW."DisbursementUpdatedOn" IS NOT NULL
                           OR NEW."DisbursementFailureReason" IS NOT NULL
                           OR NEW."CancelledBy" IS NOT NULL OR NEW."CancelledOn" IS NOT NULL OR NEW."CancellationReason" IS NOT NULL
                           OR NEW."ReversedBy" IS NOT NULL OR NEW."ReversedOn" IS NOT NULL
                           OR NEW."ReversalReason" IS NOT NULL OR NEW."ReversalEvidenceReference" IS NOT NULL THEN
                            RAISE EXCEPTION 'refunds must be created as clean version-one drafts' USING ERRCODE = '23514';
                        END IF;
                        SELECT coalesce(sum(allocation."Amount"), 0) INTO allocated_amount
                        FROM public."PaymentAllocations" allocation
                        WHERE allocation."BusinessUnitId" = NEW."BusinessUnitId"
                          AND allocation."CustomerPaymentId" = NEW."SourcePaymentId";
                        SELECT coalesce(sum(refund."Amount"), 0) INTO reserved_amount
                        FROM public."CustomerRefunds" refund
                        WHERE refund."BusinessUnitId" = NEW."BusinessUnitId"
                          AND refund."SourcePaymentId" = NEW."SourcePaymentId"
                          AND refund."Status" IN ('Approved', 'Released');
                        IF NEW."Amount" > round(payment_row."Amount" - allocated_amount - reserved_amount, 2) THEN
                            RAISE EXCEPTION 'refund exceeds the live unapplied receipt balance' USING ERRCODE = '23514';
                        END IF;
                        RETURN NEW;
                    END IF;
                    IF (NEW."BusinessUnitId", NEW."SourcePaymentId", NEW."CustomerId", NEW."CommercialCaseId", NEW."CurrencyId",
                        NEW."RequestedExecutionDate", NEW."Amount", NEW."Method", NEW."DestinationReference", NEW."DestinationVerified",
                        NEW."ReasonCode", NEW."Reason", NEW."EvidenceReference", NEW."IdempotencyKey", NEW."RequestHash",
                        NEW."CreatedBy", NEW."CreatedOn") IS DISTINCT FROM
                       (OLD."BusinessUnitId", OLD."SourcePaymentId", OLD."CustomerId", OLD."CommercialCaseId", OLD."CurrencyId",
                        OLD."RequestedExecutionDate", OLD."Amount", OLD."Method", OLD."DestinationReference", OLD."DestinationVerified",
                        OLD."ReasonCode", OLD."Reason", OLD."EvidenceReference", OLD."IdempotencyKey", OLD."RequestHash",
                        OLD."CreatedBy", OLD."CreatedOn") OR NEW."Version" <> OLD."Version" + 1 THEN
                        RAISE EXCEPTION 'refund identity and request fields are immutable' USING ERRCODE = '55000';
                    END IF;
                    IF OLD."Status" = 'Draft' AND NEW."Status" = 'Approved' THEN
                        IF NEW."ApprovedBy" IS NULL OR length(trim(NEW."ApprovedBy")) = 0
                           OR lower(trim(NEW."ApprovedBy")) = lower(trim(OLD."CreatedBy")) OR NEW."ApprovedOn" IS NULL
                           OR NEW."PostingStatus" <> 'Reserved' OR NEW."RefundNumber" IS NOT NULL
                           OR NEW."ReleasedBy" IS NOT NULL OR NEW."ReleasedOn" IS NOT NULL
                           OR NEW."DisbursementUpdatedBy" IS NOT NULL OR NEW."DisbursementUpdatedOn" IS NOT NULL
                           OR NEW."DisbursementFailureReason" IS NOT NULL
                           OR NEW."CancelledBy" IS NOT NULL OR NEW."CancelledOn" IS NOT NULL OR NEW."CancellationReason" IS NOT NULL
                           OR NEW."ReversedBy" IS NOT NULL OR NEW."ReversedOn" IS NOT NULL
                           OR NEW."ReversalReason" IS NOT NULL OR NEW."ReversalEvidenceReference" IS NOT NULL
                           OR NEW."JournalReference" IS DISTINCT FROM OLD."JournalReference" THEN
                            RAISE EXCEPTION 'invalid governed refund approval transition' USING ERRCODE = '55000';
                        END IF;
                        SELECT coalesce(sum(allocation."Amount"), 0) INTO allocated_amount
                        FROM public."PaymentAllocations" allocation
                        WHERE allocation."BusinessUnitId" = NEW."BusinessUnitId"
                          AND allocation."CustomerPaymentId" = NEW."SourcePaymentId";
                        SELECT coalesce(sum(refund."Amount"), 0) INTO reserved_amount
                        FROM public."CustomerRefunds" refund
                        WHERE refund."BusinessUnitId" = NEW."BusinessUnitId"
                          AND refund."SourcePaymentId" = NEW."SourcePaymentId"
                          AND refund."Id" <> NEW."Id" AND refund."Status" IN ('Approved', 'Released');
                        IF NEW."Amount" > round(payment_row."Amount" - allocated_amount - reserved_amount, 2) THEN
                            RAISE EXCEPTION 'refund approval exceeds the live unapplied receipt balance' USING ERRCODE = '23514';
                        END IF;
                        RETURN NEW;
                    END IF;
                    IF OLD."Status" = 'Approved' AND NEW."Status" = 'Released' THEN
                        IF NEW."ReleasedBy" IS NULL OR length(trim(NEW."ReleasedBy")) = 0
                           OR lower(trim(NEW."ReleasedBy")) IN (lower(trim(OLD."CreatedBy")), lower(trim(OLD."ApprovedBy")))
                           OR NEW."ReleasedOn" IS NULL OR NEW."RefundNumber" IS NULL
                           OR NEW."PostingStatus" <> 'PendingDisbursement'
                           OR NEW."DisbursementUpdatedBy" IS NOT NULL OR NEW."DisbursementUpdatedOn" IS NOT NULL
                           OR NEW."DisbursementFailureReason" IS NOT NULL
                           OR NEW."ApprovedBy" IS DISTINCT FROM OLD."ApprovedBy" OR NEW."ApprovedOn" IS DISTINCT FROM OLD."ApprovedOn"
                           OR NEW."CancelledBy" IS NOT NULL OR NEW."CancelledOn" IS NOT NULL OR NEW."CancellationReason" IS NOT NULL
                           OR NEW."ReversedBy" IS NOT NULL OR NEW."ReversedOn" IS NOT NULL
                           OR NEW."ReversalReason" IS NOT NULL OR NEW."ReversalEvidenceReference" IS NOT NULL
                           OR NEW."JournalReference" IS DISTINCT FROM OLD."JournalReference" THEN
                            RAISE EXCEPTION 'invalid governed refund release transition' USING ERRCODE = '55000';
                        END IF;
                        SELECT coalesce(sum(allocation."Amount"), 0) INTO allocated_amount
                        FROM public."PaymentAllocations" allocation
                        WHERE allocation."BusinessUnitId" = NEW."BusinessUnitId"
                          AND allocation."CustomerPaymentId" = NEW."SourcePaymentId";
                        SELECT coalesce(sum(refund."Amount"), 0) INTO reserved_amount
                        FROM public."CustomerRefunds" refund
                        WHERE refund."BusinessUnitId" = NEW."BusinessUnitId"
                          AND refund."SourcePaymentId" = NEW."SourcePaymentId"
                          AND refund."Id" <> NEW."Id" AND refund."Status" IN ('Approved', 'Released');
                        IF NEW."Amount" > round(payment_row."Amount" - allocated_amount - reserved_amount, 2) THEN
                            RAISE EXCEPTION 'refund release exceeds the live unapplied receipt balance' USING ERRCODE = '23514';
                        END IF;
                        fiscal_year := extract(year from NEW."RequestedExecutionDate")::integer;
                        INSERT INTO public."LegalDocumentCounters"
                            ("BusinessUnitId", "DocumentType", "FiscalYear", "NextNumber")
                        VALUES (NEW."BusinessUnitId", 'Refund', fiscal_year, 2)
                        ON CONFLICT ("BusinessUnitId", "DocumentType", "FiscalYear")
                        DO UPDATE SET "NextNumber" = public."LegalDocumentCounters"."NextNumber" + 1
                        RETURNING "NextNumber" - 1 INTO legal_sequence;
                        NEW."RefundNumber" := format('RFD-%s-%s', fiscal_year, lpad(legal_sequence::text, 6, '0'));
                        RETURN NEW;
                    END IF;
                    IF OLD."Status" = 'Released' AND NEW."Status" = 'Released'
                       AND OLD."PostingStatus" = 'PendingDisbursement'
                       AND NEW."PostingStatus" IN ('Settled', 'Failed') THEN
                        IF NEW."DisbursementUpdatedBy" IS NULL OR length(trim(NEW."DisbursementUpdatedBy")) = 0
                           OR lower(trim(NEW."DisbursementUpdatedBy")) IN
                              (lower(trim(OLD."CreatedBy")), lower(trim(OLD."ApprovedBy")), lower(trim(OLD."ReleasedBy")))
                           OR NEW."DisbursementUpdatedOn" IS NULL OR NEW."JournalReference" IS NULL
                           OR length(trim(NEW."JournalReference")) = 0
                           OR NEW."RefundNumber" IS DISTINCT FROM OLD."RefundNumber"
                           OR (NEW."PostingStatus" = 'Settled' AND NEW."DisbursementFailureReason" IS NOT NULL)
                           OR (NEW."PostingStatus" = 'Failed' AND
                               (NEW."DisbursementFailureReason" IS NULL OR length(trim(NEW."DisbursementFailureReason")) = 0))
                           OR NEW."ApprovedBy" IS DISTINCT FROM OLD."ApprovedBy" OR NEW."ApprovedOn" IS DISTINCT FROM OLD."ApprovedOn"
                           OR NEW."ReleasedBy" IS DISTINCT FROM OLD."ReleasedBy" OR NEW."ReleasedOn" IS DISTINCT FROM OLD."ReleasedOn"
                           OR NEW."CancelledBy" IS DISTINCT FROM OLD."CancelledBy" OR NEW."CancelledOn" IS DISTINCT FROM OLD."CancelledOn"
                           OR NEW."CancellationReason" IS DISTINCT FROM OLD."CancellationReason"
                           OR NEW."ReversedBy" IS DISTINCT FROM OLD."ReversedBy" OR NEW."ReversedOn" IS DISTINCT FROM OLD."ReversedOn"
                           OR NEW."ReversalReason" IS DISTINCT FROM OLD."ReversalReason"
                           OR NEW."ReversalEvidenceReference" IS DISTINCT FROM OLD."ReversalEvidenceReference" THEN
                            RAISE EXCEPTION 'invalid governed refund disbursement transition' USING ERRCODE = '55000';
                        END IF;
                        RETURN NEW;
                    END IF;
                    IF OLD."Status" IN ('Draft', 'Approved') AND NEW."Status" = 'Cancelled' THEN
                        IF NEW."CancelledBy" IS NULL OR length(trim(NEW."CancelledBy")) = 0
                           OR NEW."CancelledOn" IS NULL OR NEW."CancellationReason" IS NULL
                           OR length(trim(NEW."CancellationReason")) = 0 OR NEW."PostingStatus" <> 'Cancelled'
                           OR NEW."RefundNumber" IS NOT NULL OR NEW."ReleasedBy" IS NOT NULL OR NEW."ReleasedOn" IS NOT NULL
                           OR NEW."DisbursementUpdatedBy" IS NOT NULL OR NEW."DisbursementUpdatedOn" IS NOT NULL
                           OR NEW."DisbursementFailureReason" IS NOT NULL
                           OR NEW."ReversedBy" IS NOT NULL OR NEW."ReversedOn" IS NOT NULL
                           OR NEW."ReversalReason" IS NOT NULL OR NEW."ReversalEvidenceReference" IS NOT NULL
                           OR (OLD."Status" = 'Approved' AND lower(trim(NEW."CancelledBy")) = lower(trim(OLD."CreatedBy")))
                           OR NEW."ApprovedBy" IS DISTINCT FROM OLD."ApprovedBy" OR NEW."ApprovedOn" IS DISTINCT FROM OLD."ApprovedOn"
                           OR NEW."JournalReference" IS DISTINCT FROM OLD."JournalReference" THEN
                            RAISE EXCEPTION 'invalid governed refund cancellation transition' USING ERRCODE = '55000';
                        END IF;
                        RETURN NEW;
                    END IF;
                    IF OLD."Status" = 'Released' AND NEW."Status" = 'Reversed' THEN
                        IF NEW."ReversedBy" IS NULL OR length(trim(NEW."ReversedBy")) = 0
                           OR lower(trim(NEW."ReversedBy")) IN
                              (lower(trim(OLD."CreatedBy")), lower(trim(OLD."ApprovedBy")), lower(trim(OLD."ReleasedBy")))
                           OR NEW."ReversedOn" IS NULL OR NEW."ReversalReason" IS NULL
                           OR length(trim(NEW."ReversalReason")) = 0
                           OR NEW."ReversalEvidenceReference" IS NULL OR length(trim(NEW."ReversalEvidenceReference")) = 0
                           OR OLD."PostingStatus" <> 'Failed' OR NEW."PostingStatus" <> 'ReversalPendingExport'
                           OR NEW."RefundNumber" IS DISTINCT FROM OLD."RefundNumber"
                           OR NEW."ApprovedBy" IS DISTINCT FROM OLD."ApprovedBy" OR NEW."ApprovedOn" IS DISTINCT FROM OLD."ApprovedOn"
                           OR NEW."ReleasedBy" IS DISTINCT FROM OLD."ReleasedBy" OR NEW."ReleasedOn" IS DISTINCT FROM OLD."ReleasedOn"
                           OR NEW."DisbursementUpdatedBy" IS DISTINCT FROM OLD."DisbursementUpdatedBy"
                           OR NEW."DisbursementUpdatedOn" IS DISTINCT FROM OLD."DisbursementUpdatedOn"
                           OR NEW."DisbursementFailureReason" IS DISTINCT FROM OLD."DisbursementFailureReason"
                           OR NEW."CancelledBy" IS DISTINCT FROM OLD."CancelledBy" OR NEW."CancelledOn" IS DISTINCT FROM OLD."CancelledOn"
                           OR NEW."CancellationReason" IS DISTINCT FROM OLD."CancellationReason"
                           OR NEW."JournalReference" IS DISTINCT FROM OLD."JournalReference" THEN
                            RAISE EXCEPTION 'invalid governed refund reversal transition' USING ERRCODE = '55000';
                        END IF;
                        RETURN NEW;
                    END IF;
                    RAISE EXCEPTION 'refund fields and lifecycle are immutable outside governed transitions' USING ERRCODE = '55000';
                END
                $function$;

                CREATE OR REPLACE FUNCTION public.nexora_payment_posted_immutable()
                RETURNS trigger LANGUAGE plpgsql SECURITY DEFINER
                SET search_path = pg_catalog, public AS $function$
                BEGIN
                    IF TG_OP = 'DELETE' THEN
                        RAISE EXCEPTION 'posted customer payments cannot be deleted' USING ERRCODE = '55000';
                    END IF;
                    IF OLD."Status" = 'Posted' AND NEW."Status" = 'Reversed'
                       AND NEW."ReversedOn" IS NOT NULL AND length(trim(NEW."ReversalReason")) > 0
                       AND NEW."Version" = OLD."Version" + 1
                       AND NOT EXISTS (
                           SELECT 1 FROM public."CustomerRefunds" refund
                           WHERE refund."BusinessUnitId" = OLD."BusinessUnitId"
                             AND refund."SourcePaymentId" = OLD."Id"
                             AND refund."Status" IN ('Approved', 'Released'))
                       AND (NEW."BusinessUnitId", NEW."CustomerId", NEW."CommercialCaseId", NEW."CurrencyId",
                            NEW."ReceiptNumber", NEW."PaymentDate", NEW."Amount", NEW."Method", NEW."BankReference",
                            NEW."IdempotencyKey", NEW."RequestHash", NEW."CreatedBy", NEW."CreatedOn")
                           IS NOT DISTINCT FROM
                           (OLD."BusinessUnitId", OLD."CustomerId", OLD."CommercialCaseId", OLD."CurrencyId",
                            OLD."ReceiptNumber", OLD."PaymentDate", OLD."Amount", OLD."Method", OLD."BankReference",
                            OLD."IdempotencyKey", OLD."RequestHash", OLD."CreatedBy", OLD."CreatedOn") THEN
                        RETURN NEW;
                    END IF;
                    RAISE EXCEPTION 'posted customer payments are immutable or reserved by an active refund' USING ERRCODE = '55000';
                END
                $function$;

                CREATE OR REPLACE FUNCTION public.nexora_payment_allocation_valid()
                RETURNS trigger LANGUAGE plpgsql SECURITY DEFINER
                SET search_path = pg_catalog, public AS $function$
                DECLARE payment_row record;
                DECLARE document_row record;
                DECLARE payment_allocated numeric(18,2);
                DECLARE refund_reserved numeric(18,2);
                DECLARE document_allocated numeric(18,2);
                DECLARE issued_credits numeric(18,2);
                DECLARE posted_write_offs numeric(18,2);
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
                    SELECT coalesce(sum(refund."Amount"), 0) INTO refund_reserved
                    FROM public."CustomerRefunds" refund
                    WHERE refund."BusinessUnitId" = NEW."BusinessUnitId"
                      AND refund."SourcePaymentId" = NEW."CustomerPaymentId"
                      AND refund."Status" IN ('Approved', 'Released');
                    IF payment_allocated + refund_reserved + NEW."Amount" > payment_row."Amount" THEN
                        RAISE EXCEPTION 'payment allocations exceed the unreserved payment amount' USING ERRCODE = '23514';
                    END IF;
                    SELECT coalesce(sum(allocation."Amount"), 0) INTO document_allocated
                    FROM public."PaymentAllocations" allocation
                    JOIN public."CustomerPayments" payment
                      ON payment."BusinessUnitId" = allocation."BusinessUnitId"
                     AND payment."Id" = allocation."CustomerPaymentId"
                    WHERE allocation."BusinessUnitId" = NEW."BusinessUnitId"
                      AND allocation."ReceivableDocumentId" = NEW."ReceivableDocumentId"
                      AND allocation."Id" <> coalesce(NEW."Id", 0) AND payment."Status" = 'Posted';
                    issued_credits := 0;
                    IF document_row."DocumentType" = 'Invoice' THEN
                        SELECT coalesce(sum(credit."TotalAmount"), 0) INTO issued_credits
                        FROM public."ReceivableDocuments" credit
                        WHERE credit."BusinessUnitId" = NEW."BusinessUnitId"
                          AND credit."ParentDocumentId" = NEW."ReceivableDocumentId"
                          AND credit."DocumentType" = 'CreditNote' AND credit."Status" = 'Issued';
                    END IF;
                    SELECT coalesce(sum(allocation."Amount"), 0) INTO posted_write_offs
                    FROM public."WriteOffAllocations" allocation
                    JOIN public."ReceivableWriteOffs" write_off
                      ON write_off."BusinessUnitId" = allocation."BusinessUnitId"
                     AND write_off."Id" = allocation."ReceivableWriteOffId"
                    WHERE allocation."BusinessUnitId" = NEW."BusinessUnitId"
                      AND allocation."ReceivableDocumentId" = NEW."ReceivableDocumentId"
                      AND write_off."Status" = 'Posted';
                    document_outstanding := round(document_row."TotalAmount" - issued_credits
                        - document_allocated - posted_write_offs, 2);
                    IF NEW."Amount" > document_outstanding THEN
                        RAISE EXCEPTION 'payment allocation exceeds live receivable outstanding' USING ERRCODE = '23514';
                    END IF;
                    RETURN NEW;
                END
                $function$;

                DROP TRIGGER IF EXISTS trg_write_off_governed ON public."ReceivableWriteOffs";
                CREATE TRIGGER trg_write_off_governed
                    BEFORE INSERT OR UPDATE OR DELETE ON public."ReceivableWriteOffs"
                    FOR EACH ROW EXECUTE FUNCTION public.nexora_write_off_governed();
                DROP TRIGGER IF EXISTS trg_write_off_allocation_governed ON public."WriteOffAllocations";
                CREATE TRIGGER trg_write_off_allocation_governed
                    BEFORE INSERT OR UPDATE OR DELETE ON public."WriteOffAllocations"
                    FOR EACH ROW EXECUTE FUNCTION public.nexora_write_off_allocation_governed();
                DROP TRIGGER IF EXISTS trg_refund_governed ON public."CustomerRefunds";
                CREATE TRIGGER trg_refund_governed
                    BEFORE INSERT OR UPDATE OR DELETE ON public."CustomerRefunds"
                    FOR EACH ROW EXECUTE FUNCTION public.nexora_refund_governed();
                DROP TRIGGER IF EXISTS trg_payment_posted_immutable ON public."CustomerPayments";
                CREATE TRIGGER trg_payment_posted_immutable
                    BEFORE UPDATE OR DELETE ON public."CustomerPayments"
                    FOR EACH ROW EXECUTE FUNCTION public.nexora_payment_posted_immutable();

                CREATE OR REPLACE FUNCTION public.nexora_write_off_outbox_event()
                RETURNS trigger LANGUAGE plpgsql SECURITY DEFINER
                SET search_path = pg_catalog, public AS $function$
                DECLARE event_action text;
                DECLARE event_time timestamp without time zone;
                DECLARE event_actor text;
                BEGIN
                    IF TG_OP = 'INSERT' AND NEW."Status" = 'Draft' THEN
                        event_action := 'DraftCreated'; event_time := NEW."CreatedOn"; event_actor := NEW."CreatedBy";
                    ELSIF TG_OP = 'UPDATE' AND OLD."Status" = 'Draft' AND NEW."Status" = 'Posted' THEN
                        event_action := 'Posted'; event_time := NEW."ApprovedOn"; event_actor := NEW."ApprovedBy";
                    ELSIF TG_OP = 'UPDATE' AND OLD."Status" = 'Draft' AND NEW."Status" = 'Cancelled' THEN
                        event_action := 'Cancelled'; event_time := NEW."CancelledOn"; event_actor := NEW."CancelledBy";
                    ELSIF TG_OP = 'UPDATE' AND OLD."Status" = 'Posted' AND NEW."Status" = 'Reversed' THEN
                        event_action := 'Reversed'; event_time := NEW."ReversedOn"; event_actor := NEW."ReversedBy";
                    ELSE RETURN NEW;
                    END IF;
                    PERFORM public.nexora_write_finance_audit(NEW."BusinessUnitId", 'ReceivableWriteOff',
                        NEW."Id", event_action, event_actor, jsonb_build_object(
                            'number', NEW."WriteOffNumber", 'amount', NEW."TotalAmount",
                            'reasonCode', NEW."ReasonCode", 'version', NEW."Version"), event_time);
                    PERFORM public.nexora_write_finance_outbox(NEW."BusinessUnitId", 'ReceivableWriteOff',
                        NEW."Id", NEW."Version", 'finance.write-off.' || CASE event_action
                            WHEN 'DraftCreated' THEN 'draft-created' ELSE lower(event_action) END,
                        jsonb_build_object('Id', NEW."Id", 'Status', NEW."Status",
                            'WriteOffNumber', NEW."WriteOffNumber", 'CustomerId', NEW."CustomerId",
                            'CommercialCaseId', NEW."CommercialCaseId", 'CurrencyId', NEW."CurrencyId",
                            'TotalAmount', NEW."TotalAmount", 'ReasonCode', NEW."ReasonCode",
                            'Actor', event_actor, 'Version', NEW."Version"), event_time);
                    RETURN NEW;
                END
                $function$;

                CREATE OR REPLACE FUNCTION public.nexora_refund_outbox_event()
                RETURNS trigger LANGUAGE plpgsql SECURITY DEFINER
                SET search_path = pg_catalog, public AS $function$
                DECLARE event_action text;
                DECLARE event_time timestamp without time zone;
                DECLARE event_actor text;
                BEGIN
                    IF TG_OP = 'INSERT' AND NEW."Status" = 'Draft' THEN
                        event_action := 'DraftCreated'; event_time := NEW."CreatedOn"; event_actor := NEW."CreatedBy";
                    ELSIF TG_OP = 'UPDATE' AND OLD."Status" = 'Draft' AND NEW."Status" = 'Approved' THEN
                        event_action := 'Approved'; event_time := NEW."ApprovedOn"; event_actor := NEW."ApprovedBy";
                    ELSIF TG_OP = 'UPDATE' AND OLD."Status" = 'Approved' AND NEW."Status" = 'Released' THEN
                        event_action := 'Released'; event_time := NEW."ReleasedOn"; event_actor := NEW."ReleasedBy";
                    ELSIF TG_OP = 'UPDATE' AND OLD."Status" = 'Released' AND NEW."Status" = 'Released'
                          AND OLD."PostingStatus" = 'PendingDisbursement' AND NEW."PostingStatus" = 'Settled' THEN
                        event_action := 'DisbursementConfirmed'; event_time := NEW."DisbursementUpdatedOn"; event_actor := NEW."DisbursementUpdatedBy";
                    ELSIF TG_OP = 'UPDATE' AND OLD."Status" = 'Released' AND NEW."Status" = 'Released'
                          AND OLD."PostingStatus" = 'PendingDisbursement' AND NEW."PostingStatus" = 'Failed' THEN
                        event_action := 'DisbursementFailed'; event_time := NEW."DisbursementUpdatedOn"; event_actor := NEW."DisbursementUpdatedBy";
                    ELSIF TG_OP = 'UPDATE' AND OLD."Status" IN ('Draft', 'Approved') AND NEW."Status" = 'Cancelled' THEN
                        event_action := 'Cancelled'; event_time := NEW."CancelledOn"; event_actor := NEW."CancelledBy";
                    ELSIF TG_OP = 'UPDATE' AND OLD."Status" = 'Released' AND NEW."Status" = 'Reversed' THEN
                        event_action := 'Reversed'; event_time := NEW."ReversedOn"; event_actor := NEW."ReversedBy";
                    ELSE RETURN NEW;
                    END IF;
                    PERFORM public.nexora_write_finance_audit(NEW."BusinessUnitId", 'CustomerRefund',
                        NEW."Id", event_action, event_actor, jsonb_build_object(
                            'number', NEW."RefundNumber", 'sourcePaymentId', NEW."SourcePaymentId",
                            'amount', NEW."Amount", 'reasonCode', NEW."ReasonCode", 'version', NEW."Version"), event_time);
                    PERFORM public.nexora_write_finance_outbox(NEW."BusinessUnitId", 'CustomerRefund',
                        NEW."Id", NEW."Version", 'finance.refund.' || CASE event_action
                            WHEN 'DraftCreated' THEN 'draft-created'
                            WHEN 'DisbursementConfirmed' THEN 'disbursement-confirmed'
                            WHEN 'DisbursementFailed' THEN 'disbursement-failed'
                            ELSE lower(event_action) END,
                        jsonb_build_object('Id', NEW."Id", 'Status', NEW."Status",
                            'RefundNumber', NEW."RefundNumber", 'SourcePaymentId', NEW."SourcePaymentId",
                            'CustomerId', NEW."CustomerId", 'CommercialCaseId', NEW."CommercialCaseId",
                            'CurrencyId', NEW."CurrencyId", 'Amount', NEW."Amount",
                            'DestinationToken', CASE WHEN event_action = 'Released' THEN NEW."DestinationReference" ELSE NULL END,
                            'ReasonCode', NEW."ReasonCode", 'PostingStatus', NEW."PostingStatus",
                            'ProviderReference', NEW."JournalReference",
                            'Actor', event_actor, 'Version', NEW."Version"), event_time);
                    RETURN NEW;
                END
                $function$;

                DROP TRIGGER IF EXISTS trg_write_off_outbox_event ON public."ReceivableWriteOffs";
                CREATE CONSTRAINT TRIGGER trg_write_off_outbox_event
                    AFTER INSERT OR UPDATE ON public."ReceivableWriteOffs"
                    DEFERRABLE INITIALLY DEFERRED FOR EACH ROW
                    EXECUTE FUNCTION public.nexora_write_off_outbox_event();
                DROP TRIGGER IF EXISTS trg_refund_outbox_event ON public."CustomerRefunds";
                CREATE CONSTRAINT TRIGGER trg_refund_outbox_event
                    AFTER INSERT OR UPDATE ON public."CustomerRefunds"
                    DEFERRABLE INITIALLY DEFERRED FOR EACH ROW
                    EXECUTE FUNCTION public.nexora_refund_outbox_event();

                DROP TRIGGER IF EXISTS trg_receivable_write_offs_reject_truncate ON public."ReceivableWriteOffs";
                CREATE TRIGGER trg_receivable_write_offs_reject_truncate BEFORE TRUNCATE ON public."ReceivableWriteOffs"
                    FOR EACH STATEMENT EXECUTE FUNCTION public.nexora_finance_reject_truncate();
                DROP TRIGGER IF EXISTS trg_write_off_allocations_reject_truncate ON public."WriteOffAllocations";
                CREATE TRIGGER trg_write_off_allocations_reject_truncate BEFORE TRUNCATE ON public."WriteOffAllocations"
                    FOR EACH STATEMENT EXECUTE FUNCTION public.nexora_finance_reject_truncate();
                DROP TRIGGER IF EXISTS trg_customer_refunds_reject_truncate ON public."CustomerRefunds";
                CREATE TRIGGER trg_customer_refunds_reject_truncate BEFORE TRUNCATE ON public."CustomerRefunds"
                    FOR EACH STATEMENT EXECUTE FUNCTION public.nexora_finance_reject_truncate();
                DROP TRIGGER IF EXISTS trg_customer_payments_reject_truncate ON public."CustomerPayments";
                CREATE TRIGGER trg_customer_payments_reject_truncate BEFORE TRUNCATE ON public."CustomerPayments"
                    FOR EACH STATEMENT EXECUTE FUNCTION public.nexora_finance_reject_truncate();

                INSERT INTO public."Module" ("ModuleName", "Description", "IsActive", "CreatedBy", "CreatedOn")
                VALUES
                    ('Receivable Write-offs', 'Governed receivable write-off preparation, posting and reversal',
                        true, 'migration:finance-exceptions:v1', now()),
                    ('Customer Refunds', 'Governed receipt refund approval, release and reversal',
                        true, 'migration:finance-exceptions:v1', now())
                ON CONFLICT ("ModuleName") DO NOTHING;

                INSERT INTO public."RolePermissions"
                    ("RoleID", "ModuleID", "BusinessUnitID", "CanCreate", "CanEdit", "CanDelete", "CreatedBy", "CreatedOn")
                SELECT role."SetupID", module."ID", role."BusinessUnitID", true, true, false,
                       'migration:finance-exceptions:v1', now()
                FROM public."Setup_Master" role
                CROSS JOIN public."Module" module
                WHERE lower(replace(role."SetupType", ' ', '')) = 'role'
                  AND module."ModuleName" IN ('Receivable Write-offs', 'Customer Refunds')
                  AND (upper(coalesce(role."SetupCode", '')) ~ '(FINANCE|ACCOUNT|ADMIN)'
                       OR upper(coalesce(role."SetupValue", '')) ~ '(FINANCE|ACCOUNT|ADMIN)')
                  AND NOT EXISTS (
                      SELECT 1 FROM public."RolePermissions" existing
                      WHERE existing."RoleID" = role."SetupID"
                        AND existing."BusinessUnitID" = role."BusinessUnitID"
                        AND existing."ModuleID" = module."ID");

                DO $block$
                DECLARE governed_table text;
                BEGIN
                    FOREACH governed_table IN ARRAY ARRAY[
                        'ReceivableWriteOffs', 'WriteOffAllocations', 'CustomerRefunds', 'CustomerPayments']
                    LOOP
                        EXECUTE format('ALTER TABLE public.%I ENABLE ROW LEVEL SECURITY', governed_table);
                        EXECUTE format('ALTER TABLE public.%I FORCE ROW LEVEL SECURITY', governed_table);
                        EXECUTE format('DROP POLICY IF EXISTS nexora_tenant_isolation ON public.%I', governed_table);
                        EXECUTE format(
                            'CREATE POLICY nexora_tenant_isolation ON public.%I TO nexora_tenant_app USING ("BusinessUnitId" = NULLIF(current_setting(''nexora.business_unit_id'', true), '''')::bigint) WITH CHECK ("BusinessUnitId" = NULLIF(current_setting(''nexora.business_unit_id'', true), '''')::bigint)',
                            governed_table);
                    END LOOP;
                    IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'nexora_tenant_app') THEN
                        GRANT SELECT, INSERT, UPDATE ON public."ReceivableWriteOffs", public."CustomerRefunds" TO nexora_tenant_app;
                        GRANT SELECT, INSERT ON public."WriteOffAllocations" TO nexora_tenant_app;
                        REVOKE DELETE, TRUNCATE ON public."ReceivableWriteOffs", public."WriteOffAllocations",
                            public."CustomerRefunds", public."CustomerPayments" FROM nexora_tenant_app;
                        GRANT USAGE ON SEQUENCE public."ReceivableWriteOffs_Id_seq",
                            public."WriteOffAllocations_Id_seq", public."CustomerRefunds_Id_seq" TO nexora_tenant_app;
                        REVOKE INSERT, UPDATE, DELETE, TRUNCATE ON public."CommercialFinanceAudits",
                            public."FinanceOutboxMessages" FROM nexora_tenant_app;
                        REVOKE ALL ON SEQUENCE public."CommercialFinanceAudits_Id_seq",
                            public."FinanceOutboxMessages_Id_seq" FROM nexora_tenant_app;
                        GRANT SELECT ON public."CommercialFinanceAudits", public."FinanceOutboxMessages" TO nexora_tenant_app;
                    END IF;
                END
                $block$;

                REVOKE ALL ON FUNCTION public.nexora_write_finance_audit(bigint, text, bigint, text, text, jsonb, timestamp without time zone) FROM PUBLIC;
                REVOKE ALL ON FUNCTION public.nexora_write_finance_audit(bigint, text, bigint, text, text, jsonb, timestamp without time zone) FROM nexora_tenant_app;
                REVOKE ALL ON FUNCTION public.nexora_receivable_live_outstanding(bigint, bigint) FROM PUBLIC;
                REVOKE ALL ON FUNCTION public.nexora_receivable_live_outstanding(bigint, bigint) FROM nexora_tenant_app;
                REVOKE ALL ON FUNCTION public.nexora_write_finance_outbox(bigint, text, bigint, bigint, text, jsonb, timestamp without time zone) FROM PUBLIC;
                REVOKE ALL ON FUNCTION public.nexora_write_finance_outbox(bigint, text, bigint, bigint, text, jsonb, timestamp without time zone) FROM nexora_tenant_app;
                REVOKE ALL ON FUNCTION public.nexora_write_off_governed() FROM PUBLIC;
                REVOKE ALL ON FUNCTION public.nexora_write_off_allocation_governed() FROM PUBLIC;
                REVOKE ALL ON FUNCTION public.nexora_refund_governed() FROM PUBLIC;
                REVOKE ALL ON FUNCTION public.nexora_payment_posted_immutable() FROM PUBLIC;
                REVOKE ALL ON FUNCTION public.nexora_payment_allocation_valid() FROM PUBLIC;
                REVOKE ALL ON FUNCTION public.nexora_write_off_outbox_event() FROM PUBLIC;
                REVOKE ALL ON FUNCTION public.nexora_refund_outbox_event() FROM PUBLIC;
                GRANT EXECUTE ON FUNCTION public.nexora_write_off_governed(),
                    public.nexora_write_off_allocation_governed(), public.nexora_refund_governed(),
                    public.nexora_payment_posted_immutable(), public.nexora_payment_allocation_valid(),
                    public.nexora_write_off_outbox_event(), public.nexora_refund_outbox_event()
                    TO nexora_tenant_app;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DO $block$
                BEGIN
                    IF EXISTS (SELECT 1 FROM public."ReceivableWriteOffs")
                       OR EXISTS (SELECT 1 FROM public."WriteOffAllocations")
                       OR EXISTS (SELECT 1 FROM public."CustomerRefunds")
                       OR EXISTS (
                           SELECT 1 FROM public."CommercialFinanceAudits"
                           WHERE "AggregateType" IN ('ReceivableWriteOff', 'CustomerRefund'))
                       OR EXISTS (
                           SELECT 1 FROM public."FinanceOutboxMessages"
                           WHERE "AggregateType" IN ('ReceivableWriteOff', 'CustomerRefund')) THEN
                        RAISE EXCEPTION 'cannot remove finance exception governance while write-off/refund records or evidence exist';
                    END IF;
                END
                $block$;

                DELETE FROM public."RolePermissions"
                WHERE "CreatedBy" = 'migration:finance-exceptions:v1';
                DELETE FROM public."Module"
                WHERE "CreatedBy" = 'migration:finance-exceptions:v1'
                  AND "ModuleName" IN ('Receivable Write-offs', 'Customer Refunds');

                DROP TRIGGER IF EXISTS trg_write_off_outbox_event ON public."ReceivableWriteOffs";
                DROP TRIGGER IF EXISTS trg_refund_outbox_event ON public."CustomerRefunds";
                DROP TRIGGER IF EXISTS trg_write_off_governed ON public."ReceivableWriteOffs";
                DROP TRIGGER IF EXISTS trg_write_off_allocation_governed ON public."WriteOffAllocations";
                DROP TRIGGER IF EXISTS trg_refund_governed ON public."CustomerRefunds";
                DROP TRIGGER IF EXISTS trg_receivable_write_offs_reject_truncate ON public."ReceivableWriteOffs";
                DROP TRIGGER IF EXISTS trg_write_off_allocations_reject_truncate ON public."WriteOffAllocations";
                DROP TRIGGER IF EXISTS trg_customer_refunds_reject_truncate ON public."CustomerRefunds";
                DROP TRIGGER IF EXISTS trg_customer_payments_reject_truncate ON public."CustomerPayments";
                DROP TRIGGER IF EXISTS trg_payment_posted_immutable ON public."CustomerPayments";

                DROP FUNCTION IF EXISTS public.nexora_write_off_outbox_event();
                DROP FUNCTION IF EXISTS public.nexora_refund_outbox_event();
                DROP FUNCTION IF EXISTS public.nexora_write_off_governed();
                DROP FUNCTION IF EXISTS public.nexora_write_off_allocation_governed();
                DROP FUNCTION IF EXISTS public.nexora_refund_governed();

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
                        WHERE document."BusinessUnitId" = business_unit_id AND document."Id" = aggregate_id;
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
                        WHERE payment."BusinessUnitId" = business_unit_id AND payment."Id" = aggregate_id;
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
                CREATE TRIGGER trg_payment_posted_immutable
                    BEFORE UPDATE OR DELETE ON public."CustomerPayments"
                    FOR EACH ROW EXECUTE FUNCTION public.nexora_payment_posted_immutable();

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
                      AND allocation."Id" <> coalesce(NEW."Id", 0) AND payment."Status" = 'Posted';
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

                DROP FUNCTION IF EXISTS public.nexora_receivable_live_outstanding(bigint, bigint);
                ALTER TABLE public."CustomerPayments" NO FORCE ROW LEVEL SECURITY;

                REVOKE ALL ON FUNCTION public.nexora_write_finance_audit(bigint, text, bigint, text, text, jsonb, timestamp without time zone) FROM PUBLIC;
                REVOKE ALL ON FUNCTION public.nexora_write_finance_audit(bigint, text, bigint, text, text, jsonb, timestamp without time zone) FROM nexora_tenant_app;
                REVOKE ALL ON FUNCTION public.nexora_payment_posted_immutable() FROM PUBLIC;
                REVOKE ALL ON FUNCTION public.nexora_payment_allocation_valid() FROM PUBLIC;
                GRANT EXECUTE ON FUNCTION public.nexora_payment_posted_immutable(),
                    public.nexora_payment_allocation_valid() TO nexora_tenant_app;
                """);

            migrationBuilder.DropTable(
                name: "CustomerRefunds");

            migrationBuilder.DropTable(
                name: "WriteOffAllocations");

            migrationBuilder.DropTable(
                name: "ReceivableWriteOffs");
        }
    }
}
