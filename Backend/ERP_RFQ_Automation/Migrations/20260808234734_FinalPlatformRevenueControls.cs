using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace ERP_RFQ_Automation.Migrations
{
    /// <inheritdoc />
    public partial class FinalPlatformRevenueControls : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_AccountingOutbox_TenantId_SubscriptionInvoiceId",
                schema: "platform",
                table: "AccountingOutbox");

            migrationBuilder.DropIndex(
                name: "UX_AccountingOutbox_Invoice_MessageType",
                schema: "platform",
                table: "AccountingOutbox");

            migrationBuilder.AddColumn<decimal>(
                name: "RefundedAmount",
                schema: "platform",
                table: "SubscriptionInvoices",
                type: "numeric(14,2)",
                precision: 14,
                scale: 2,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "ReversedPaymentAmount",
                schema: "platform",
                table: "SubscriptionInvoices",
                type: "numeric(14,2)",
                precision: 14,
                scale: 2,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<DateTime>(
                name: "TaxDeterminedAtUtc",
                schema: "platform",
                table: "SubscriptionInvoices",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TaxEvidenceJson",
                schema: "platform",
                table: "SubscriptionInvoices",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TaxEvidenceSha256",
                schema: "platform",
                table: "SubscriptionInvoices",
                type: "character(64)",
                fixedLength: true,
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TaxJurisdictionCode",
                schema: "platform",
                table: "SubscriptionInvoices",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "TaxRuleId",
                schema: "platform",
                table: "SubscriptionInvoices",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "TaxRuleVersion",
                schema: "platform",
                table: "SubscriptionInvoices",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "WrittenOffAmount",
                schema: "platform",
                table: "SubscriptionInvoices",
                type: "numeric(14,2)",
                precision: 14,
                scale: 2,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<long>(
                name: "SubscriptionRevenueActionId",
                schema: "platform",
                table: "AccountingOutbox",
                type: "bigint",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "SubscriptionRevenueActions",
                schema: "platform",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    TenantId = table.Column<long>(type: "bigint", nullable: false),
                    SubscriptionInvoiceId = table.Column<long>(type: "bigint", nullable: false),
                    Kind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    IdempotencyKey = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Amount = table.Column<decimal>(type: "numeric(14,2)", precision: 14, scale: 2, nullable: false),
                    Currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    Reason = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    EvidenceSha256 = table.Column<string>(type: "character(64)", fixedLength: true, maxLength: 64, nullable: false),
                    ExternalReference = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    ProposedByPlatformUserId = table.Column<long>(type: "bigint", nullable: true),
                    ProposedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ApprovedByPlatformUserId = table.Column<long>(type: "bigint", nullable: true),
                    ApprovedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CompletedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SubscriptionRevenueActions", x => x.Id);
                    table.UniqueConstraint("AK_SubscriptionRevenueActions_TenantId_SubscriptionInvoiceId_Id", x => new { x.TenantId, x.SubscriptionInvoiceId, x.Id });
                    table.ForeignKey(
                        name: "FK_SubscriptionRevenueActions_PlatformUsers_ApprovedByPlatform~",
                        column: x => x.ApprovedByPlatformUserId,
                        principalSchema: "platform",
                        principalTable: "PlatformUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SubscriptionRevenueActions_PlatformUsers_ProposedByPlatform~",
                        column: x => x.ProposedByPlatformUserId,
                        principalSchema: "platform",
                        principalTable: "PlatformUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SubscriptionRevenueActions_SubscriptionInvoices_TenantId_Su~",
                        columns: x => new { x.TenantId, x.SubscriptionInvoiceId },
                        principalSchema: "platform",
                        principalTable: "SubscriptionInvoices",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "SubscriptionTaxRules",
                schema: "platform",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    JurisdictionCode = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    BuyerCountryCode = table.Column<string>(type: "character varying(2)", maxLength: 2, nullable: false),
                    Currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    Treatment = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    RatePercent = table.Column<decimal>(type: "numeric(7,4)", precision: 7, scale: 4, nullable: false),
                    LegalAuthorityReference = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    EvidenceSha256 = table.Column<string>(type: "character(64)", fixedLength: true, maxLength: 64, nullable: false),
                    EffectiveFromUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    EffectiveToUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false, defaultValue: 1L),
                    ProposedByPlatformUserId = table.Column<long>(type: "bigint", nullable: false),
                    ProposedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ApprovedByPlatformUserId = table.Column<long>(type: "bigint", nullable: true),
                    ApprovedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SubscriptionTaxRules", x => x.Id);
                    table.UniqueConstraint("AK_SubscriptionTaxRules_Id_Version", x => new { x.Id, x.Version });
                    table.ForeignKey(
                        name: "FK_SubscriptionTaxRules_PlatformUsers_ApprovedByPlatformUserId",
                        column: x => x.ApprovedByPlatformUserId,
                        principalSchema: "platform",
                        principalTable: "PlatformUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SubscriptionTaxRules_PlatformUsers_ProposedByPlatformUserId",
                        column: x => x.ProposedByPlatformUserId,
                        principalSchema: "platform",
                        principalTable: "PlatformUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SubscriptionInvoices_TaxRuleId_TaxRuleVersion",
                schema: "platform",
                table: "SubscriptionInvoices",
                columns: new[] { "TaxRuleId", "TaxRuleVersion" });

            migrationBuilder.CreateIndex(
                name: "IX_AccountingOutbox_Invoice_MessageType",
                schema: "platform",
                table: "AccountingOutbox",
                columns: new[] { "SubscriptionInvoiceId", "MessageType" });

            migrationBuilder.CreateIndex(
                name: "IX_AccountingOutbox_SubscriptionRevenueActionId",
                schema: "platform",
                table: "AccountingOutbox",
                column: "SubscriptionRevenueActionId");

            migrationBuilder.CreateIndex(
                name: "IX_AccountingOutbox_TenantId_SubscriptionInvoiceId_Subscriptio~",
                schema: "platform",
                table: "AccountingOutbox",
                columns: new[] { "TenantId", "SubscriptionInvoiceId", "SubscriptionRevenueActionId" });

            migrationBuilder.CreateIndex(
                name: "IX_SubscriptionRevenueActions_ApprovedByPlatformUserId",
                schema: "platform",
                table: "SubscriptionRevenueActions",
                column: "ApprovedByPlatformUserId");

            migrationBuilder.CreateIndex(
                name: "IX_SubscriptionRevenueActions_IdempotencyKey",
                schema: "platform",
                table: "SubscriptionRevenueActions",
                column: "IdempotencyKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SubscriptionRevenueActions_ProposedByPlatformUserId",
                schema: "platform",
                table: "SubscriptionRevenueActions",
                column: "ProposedByPlatformUserId");

            migrationBuilder.CreateIndex(
                name: "IX_SubscriptionRevenueActions_TenantId_SubscriptionInvoiceId_K~",
                schema: "platform",
                table: "SubscriptionRevenueActions",
                columns: new[] { "TenantId", "SubscriptionInvoiceId", "Kind", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_SubscriptionTaxRules_ApprovedByPlatformUserId",
                schema: "platform",
                table: "SubscriptionTaxRules",
                column: "ApprovedByPlatformUserId");

            migrationBuilder.CreateIndex(
                name: "IX_SubscriptionTaxRules_JurisdictionCode_BuyerCountryCode_Curr~",
                schema: "platform",
                table: "SubscriptionTaxRules",
                columns: new[] { "JurisdictionCode", "BuyerCountryCode", "Currency", "EffectiveFromUtc" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SubscriptionTaxRules_ProposedByPlatformUserId",
                schema: "platform",
                table: "SubscriptionTaxRules",
                column: "ProposedByPlatformUserId");

            migrationBuilder.CreateIndex(
                name: "IX_SubscriptionTaxRules_Status_BuyerCountryCode_Currency",
                schema: "platform",
                table: "SubscriptionTaxRules",
                columns: new[] { "Status", "BuyerCountryCode", "Currency" });

            migrationBuilder.AddForeignKey(
                name: "FK_AccountingOutbox_SubscriptionRevenueActions_TenantId_Subscr~",
                schema: "platform",
                table: "AccountingOutbox",
                columns: new[] { "TenantId", "SubscriptionInvoiceId", "SubscriptionRevenueActionId" },
                principalSchema: "platform",
                principalTable: "SubscriptionRevenueActions",
                principalColumns: new[] { "TenantId", "SubscriptionInvoiceId", "Id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_SubscriptionInvoices_SubscriptionTaxRules_TaxRuleId_TaxRule~",
                schema: "platform",
                table: "SubscriptionInvoices",
                columns: new[] { "TaxRuleId", "TaxRuleVersion" },
                principalSchema: "platform",
                principalTable: "SubscriptionTaxRules",
                principalColumns: new[] { "Id", "Version" },
                onDelete: ReferentialAction.Restrict);

            if (migrationBuilder.ActiveProvider?.Contains("Npgsql", StringComparison.OrdinalIgnoreCase) == true)
            {
                migrationBuilder.Sql("""
                    CREATE EXTENSION IF NOT EXISTS btree_gist;

                    ALTER TABLE platform."SubscriptionRevenueActions"
                        ADD CONSTRAINT "CK_SubscriptionRevenueActions_Kind"
                            CHECK ("Kind" IN ('Void','Refund','PaymentReversal','WriteOff','Dunning')),
                        ADD CONSTRAINT "CK_SubscriptionRevenueActions_Status"
                            CHECK ("Status" IN ('Proposed','Approved','Completed','Failed')),
                        ADD CONSTRAINT "CK_SubscriptionRevenueActions_Value"
                            CHECK (("Kind"='Dunning' AND "Amount"=0) OR ("Kind"<>'Dunning' AND "Amount">0)),
                        ADD CONSTRAINT "CK_SubscriptionRevenueActions_Currency"
                            CHECK ("Currency" ~ '^[A-Z]{3}$'),
                        ADD CONSTRAINT "CK_SubscriptionRevenueActions_Evidence"
                            CHECK ("EvidenceSha256" ~ '^[0-9a-f]{64}$' AND char_length(btrim("Reason")) BETWEEN 10 AND 1000),
                        ADD CONSTRAINT "CK_SubscriptionRevenueActions_Actors"
                            CHECK (("Kind"='Dunning' AND "ProposedByPlatformUserId" IS NULL AND "ApprovedByPlatformUserId" IS NULL
                                    AND "Status" IN ('Approved','Completed') AND "ApprovedAtUtc" IS NOT NULL)
                                OR ("Kind"<>'Dunning' AND "ProposedByPlatformUserId" IS NOT NULL
                                    AND (("Status"='Proposed' AND "ApprovedByPlatformUserId" IS NULL AND "ApprovedAtUtc" IS NULL AND "CompletedAtUtc" IS NULL)
                                      OR ("Status" IN ('Approved','Completed') AND "ApprovedByPlatformUserId" IS NOT NULL
                                          AND "ApprovedByPlatformUserId"<>"ProposedByPlatformUserId" AND "ApprovedAtUtc" IS NOT NULL)
                                      OR "Status"='Failed'))),
                        ADD CONSTRAINT "CK_SubscriptionRevenueActions_Completion"
                            CHECK (("Status"='Completed') = ("CompletedAtUtc" IS NOT NULL));

                    ALTER TABLE platform."SubscriptionTaxRules"
                        ADD CONSTRAINT "CK_SubscriptionTaxRules_Status"
                            CHECK ("Status" IN ('Draft','Approved','Retired')),
                        ADD CONSTRAINT "CK_SubscriptionTaxRules_Identity"
                            CHECK (char_length(btrim("JurisdictionCode"))>0 AND "BuyerCountryCode" ~ '^[A-Z]{2}$'
                                   AND "Currency" ~ '^[A-Z]{3}$' AND "Version">0),
                        ADD CONSTRAINT "CK_SubscriptionTaxRules_RateIntervalEvidence"
                            CHECK ("RatePercent" BETWEEN 0 AND 100
                                   AND ("EffectiveToUtc" IS NULL OR "EffectiveToUtc">"EffectiveFromUtc")
                                   AND "EvidenceSha256" ~ '^[0-9a-f]{64}$'),
                        ADD CONSTRAINT "CK_SubscriptionTaxRules_Actors"
                            CHECK ("ProposedByPlatformUserId">0 AND
                                  (("Status"='Draft' AND "ApprovedByPlatformUserId" IS NULL AND "ApprovedAtUtc" IS NULL)
                                   OR ("Status" IN ('Approved','Retired') AND "ApprovedByPlatformUserId" IS NOT NULL
                                       AND "ApprovedByPlatformUserId"<>"ProposedByPlatformUserId" AND "ApprovedAtUtc" IS NOT NULL))),
                        ADD CONSTRAINT "EX_SubscriptionTaxRules_ApprovedInterval"
                            EXCLUDE USING gist ("JurisdictionCode" WITH =, "BuyerCountryCode" WITH =, "Currency" WITH =,
                                tstzrange("EffectiveFromUtc", COALESCE("EffectiveToUtc", 'infinity'::timestamptz), '[)') WITH &&)
                            WHERE ("Status"='Approved');

                    ALTER TABLE platform."SubscriptionInvoices"
                        DROP CONSTRAINT "CK_SubscriptionInvoices_Amounts",
                        ADD CONSTRAINT "CK_SubscriptionInvoices_RevenueAmounts"
                            CHECK ("Subtotal">=0 AND "TaxRatePercent" BETWEEN 0 AND 100 AND "TaxAmount">=0 AND "TotalAmount">=0
                                   AND "CreditedAmount">=0 AND "PaidAmount">=0 AND "RefundedAmount">=0
                                   AND "ReversedPaymentAmount">=0 AND "WrittenOffAmount">=0
                                   AND "TotalAmount"="Subtotal"+"TaxAmount"
                                   AND "RefundedAmount"+"ReversedPaymentAmount"<="PaidAmount"
                                   AND "CreditedAmount"+("PaidAmount"-"RefundedAmount"-"ReversedPaymentAmount")+"WrittenOffAmount"<="TotalAmount"),
                        ADD CONSTRAINT "CK_SubscriptionInvoices_TaxEvidenceTuple"
                            CHECK (("TaxRuleId" IS NULL AND "TaxRuleVersion" IS NULL AND "TaxEvidenceJson" IS NULL
                                    AND "TaxEvidenceSha256" IS NULL AND "TaxDeterminedAtUtc" IS NULL AND "TaxJurisdictionCode" IS NULL)
                                OR ("TaxRuleId" IS NOT NULL AND "TaxRuleVersion" IS NOT NULL AND "TaxEvidenceJson" IS NOT NULL
                                    AND "TaxEvidenceSha256" ~ '^[0-9a-f]{64}$' AND "TaxDeterminedAtUtc" IS NOT NULL
                                    AND char_length(btrim("TaxJurisdictionCode"))>0));

                    CREATE OR REPLACE FUNCTION platform.nexora_guard_subscription_revenue_action()
                    RETURNS trigger LANGUAGE plpgsql AS $guard$
                    BEGIN
                        IF TG_OP='DELETE' THEN RAISE EXCEPTION 'subscription revenue actions are append-only'; END IF;
                        IF NEW."Id" IS DISTINCT FROM OLD."Id" OR NEW."TenantId" IS DISTINCT FROM OLD."TenantId"
                           OR NEW."SubscriptionInvoiceId" IS DISTINCT FROM OLD."SubscriptionInvoiceId"
                           OR NEW."Kind" IS DISTINCT FROM OLD."Kind" OR NEW."IdempotencyKey" IS DISTINCT FROM OLD."IdempotencyKey"
                           OR NEW."Amount" IS DISTINCT FROM OLD."Amount" OR NEW."Currency" IS DISTINCT FROM OLD."Currency"
                           OR NEW."Reason" IS DISTINCT FROM OLD."Reason" OR NEW."EvidenceSha256" IS DISTINCT FROM OLD."EvidenceSha256"
                           OR NEW."ExternalReference" IS DISTINCT FROM OLD."ExternalReference"
                           OR NEW."ProposedByPlatformUserId" IS DISTINCT FROM OLD."ProposedByPlatformUserId"
                           OR NEW."ProposedAtUtc" IS DISTINCT FROM OLD."ProposedAtUtc"
                        THEN RAISE EXCEPTION 'subscription revenue action identity and evidence are immutable'; END IF;
                        IF NOT ((OLD."Status"='Proposed' AND NEW."Status" IN ('Completed','Failed'))
                             OR (OLD."Status"='Approved' AND NEW."Status" IN ('Completed','Failed')))
                        THEN RAISE EXCEPTION 'invalid subscription revenue action transition % -> %',OLD."Status",NEW."Status"; END IF;
                        RETURN NEW;
                    END $guard$;
                    REVOKE ALL ON FUNCTION platform.nexora_guard_subscription_revenue_action() FROM PUBLIC;
                    CREATE TRIGGER subscription_revenue_actions_guard
                        BEFORE UPDATE OR DELETE ON platform."SubscriptionRevenueActions"
                        FOR EACH ROW EXECUTE FUNCTION platform.nexora_guard_subscription_revenue_action();
                    ALTER TABLE platform."SubscriptionRevenueActions" ENABLE ALWAYS TRIGGER subscription_revenue_actions_guard;

                    CREATE OR REPLACE FUNCTION platform.nexora_guard_subscription_tax_rule()
                    RETURNS trigger LANGUAGE plpgsql AS $guard$
                    BEGIN
                        IF TG_OP='DELETE' THEN RAISE EXCEPTION 'subscription tax rules are immutable'; END IF;
                        IF NEW."Id" IS DISTINCT FROM OLD."Id" OR NEW."JurisdictionCode" IS DISTINCT FROM OLD."JurisdictionCode"
                           OR NEW."BuyerCountryCode" IS DISTINCT FROM OLD."BuyerCountryCode" OR NEW."Currency" IS DISTINCT FROM OLD."Currency"
                           OR NEW."Treatment" IS DISTINCT FROM OLD."Treatment" OR NEW."RatePercent" IS DISTINCT FROM OLD."RatePercent"
                           OR NEW."LegalAuthorityReference" IS DISTINCT FROM OLD."LegalAuthorityReference"
                           OR NEW."EvidenceSha256" IS DISTINCT FROM OLD."EvidenceSha256" OR NEW."EffectiveFromUtc" IS DISTINCT FROM OLD."EffectiveFromUtc"
                           OR NEW."EffectiveToUtc" IS DISTINCT FROM OLD."EffectiveToUtc" OR NEW."Version" IS DISTINCT FROM OLD."Version"
                           OR NEW."ProposedByPlatformUserId" IS DISTINCT FROM OLD."ProposedByPlatformUserId"
                           OR NEW."ProposedAtUtc" IS DISTINCT FROM OLD."ProposedAtUtc"
                        THEN RAISE EXCEPTION 'subscription tax rule legal evidence is immutable'; END IF;
                        IF NOT ((OLD."Status"='Draft' AND NEW."Status"='Approved') OR (OLD."Status"='Approved' AND NEW."Status"='Retired'))
                        THEN RAISE EXCEPTION 'invalid subscription tax rule transition % -> %',OLD."Status",NEW."Status"; END IF;
                        RETURN NEW;
                    END $guard$;
                    REVOKE ALL ON FUNCTION platform.nexora_guard_subscription_tax_rule() FROM PUBLIC;
                    CREATE TRIGGER subscription_tax_rules_guard
                        BEFORE UPDATE OR DELETE ON platform."SubscriptionTaxRules"
                        FOR EACH ROW EXECUTE FUNCTION platform.nexora_guard_subscription_tax_rule();
                    ALTER TABLE platform."SubscriptionTaxRules" ENABLE ALWAYS TRIGGER subscription_tax_rules_guard;

                    CREATE OR REPLACE FUNCTION platform.nexora_guard_subscription_invoice()
                    RETURNS trigger LANGUAGE plpgsql AS $guard$
                    BEGIN
                        IF TG_OP='DELETE' THEN RAISE EXCEPTION 'Subscription invoices are immutable; use a governed revenue action'; END IF;
                        IF OLD."Status"='Draft' AND NEW."Status" NOT IN ('Draft','Finalized') THEN RAISE EXCEPTION 'A draft subscription invoice may only be finalized'; END IF;
                        IF OLD."Status"<>'Draft' AND NEW."Status"='Draft' THEN RAISE EXCEPTION 'A posted subscription invoice can never return to draft'; END IF;
                        IF OLD."Status"='Void' AND NEW."Status"<>'Void' THEN RAISE EXCEPTION 'A void subscription invoice is terminal'; END IF;
                        IF OLD."Status"='Draft' AND NEW."Status"='Finalized' AND NEW."TaxAmount">0 AND NEW."TaxRuleId" IS NULL
                        THEN RAISE EXCEPTION 'A taxable invoice requires governed tax determination evidence'; END IF;
                        IF OLD."Status"='Draft' AND NEW."Status"='Finalized' AND (
                            NEW."TenantId" IS DISTINCT FROM OLD."TenantId" OR NEW."BillingStatementId" IS DISTINCT FROM OLD."BillingStatementId"
                            OR NEW."Currency" IS DISTINCT FROM OLD."Currency" OR NEW."Subtotal" IS DISTINCT FROM OLD."Subtotal"
                            OR NEW."TaxRatePercent" IS DISTINCT FROM OLD."TaxRatePercent" OR NEW."TaxAmount" IS DISTINCT FROM OLD."TaxAmount"
                            OR NEW."TotalAmount" IS DISTINCT FROM OLD."TotalAmount" OR NEW."IssuedAtUtc" IS DISTINCT FROM OLD."IssuedAtUtc"
                            OR NEW."DueAtUtc" IS DISTINCT FROM OLD."DueAtUtc" OR NEW."SellerSnapshotJson" IS DISTINCT FROM OLD."SellerSnapshotJson"
                            OR NEW."BuyerSnapshotJson" IS DISTINCT FROM OLD."BuyerSnapshotJson" OR NEW."TaxTreatment" IS DISTINCT FROM OLD."TaxTreatment"
                            OR NEW."TaxJurisdictionCode" IS DISTINCT FROM OLD."TaxJurisdictionCode" OR NEW."TaxRuleId" IS DISTINCT FROM OLD."TaxRuleId"
                            OR NEW."TaxRuleVersion" IS DISTINCT FROM OLD."TaxRuleVersion" OR NEW."TaxEvidenceJson" IS DISTINCT FROM OLD."TaxEvidenceJson"
                            OR NEW."TaxEvidenceSha256" IS DISTINCT FROM OLD."TaxEvidenceSha256" OR NEW."TaxDeterminedAtUtc" IS DISTINCT FROM OLD."TaxDeterminedAtUtc"
                            OR NEW."SourceEvidenceJson" IS DISTINCT FROM OLD."SourceEvidenceJson" OR NEW."SourceEvidenceSha256" IS DISTINCT FROM OLD."SourceEvidenceSha256"
                            OR NEW."CreatedBy" IS DISTINCT FROM OLD."CreatedBy" OR NEW."CreatedAtUtc" IS DISTINCT FROM OLD."CreatedAtUtc")
                        THEN RAISE EXCEPTION 'Invoice source and tax evidence cannot change during finalization'; END IF;
                        IF OLD."Status"<>'Draft' AND (
                            NEW."TenantId" IS DISTINCT FROM OLD."TenantId" OR NEW."BillingStatementId" IS DISTINCT FROM OLD."BillingStatementId"
                            OR NEW."InvoiceNumber" IS DISTINCT FROM OLD."InvoiceNumber" OR NEW."Currency" IS DISTINCT FROM OLD."Currency"
                            OR NEW."Subtotal" IS DISTINCT FROM OLD."Subtotal" OR NEW."TaxRatePercent" IS DISTINCT FROM OLD."TaxRatePercent"
                            OR NEW."TaxAmount" IS DISTINCT FROM OLD."TaxAmount" OR NEW."TotalAmount" IS DISTINCT FROM OLD."TotalAmount"
                            OR NEW."IssuedAtUtc" IS DISTINCT FROM OLD."IssuedAtUtc" OR NEW."DueAtUtc" IS DISTINCT FROM OLD."DueAtUtc"
                            OR NEW."SellerSnapshotJson" IS DISTINCT FROM OLD."SellerSnapshotJson" OR NEW."BuyerSnapshotJson" IS DISTINCT FROM OLD."BuyerSnapshotJson"
                            OR NEW."TaxTreatment" IS DISTINCT FROM OLD."TaxTreatment" OR NEW."TaxJurisdictionCode" IS DISTINCT FROM OLD."TaxJurisdictionCode"
                            OR NEW."TaxRuleId" IS DISTINCT FROM OLD."TaxRuleId" OR NEW."TaxRuleVersion" IS DISTINCT FROM OLD."TaxRuleVersion"
                            OR NEW."TaxEvidenceJson" IS DISTINCT FROM OLD."TaxEvidenceJson" OR NEW."TaxEvidenceSha256" IS DISTINCT FROM OLD."TaxEvidenceSha256"
                            OR NEW."TaxDeterminedAtUtc" IS DISTINCT FROM OLD."TaxDeterminedAtUtc"
                            OR NEW."SourceEvidenceJson" IS DISTINCT FROM OLD."SourceEvidenceJson" OR NEW."SourceEvidenceSha256" IS DISTINCT FROM OLD."SourceEvidenceSha256"
                            OR NEW."CreatedBy" IS DISTINCT FROM OLD."CreatedBy" OR NEW."CreatedAtUtc" IS DISTINCT FROM OLD."CreatedAtUtc"
                            OR NEW."FinalizedBy" IS DISTINCT FROM OLD."FinalizedBy" OR NEW."FinalizedAtUtc" IS DISTINCT FROM OLD."FinalizedAtUtc")
                        THEN RAISE EXCEPTION 'Finalized subscription invoice identity and evidence are immutable'; END IF;
                        IF NEW."CreditedAmount"<OLD."CreditedAmount" OR NEW."PaidAmount"<OLD."PaidAmount"
                           OR NEW."RefundedAmount"<OLD."RefundedAmount" OR NEW."ReversedPaymentAmount"<OLD."ReversedPaymentAmount"
                           OR NEW."WrittenOffAmount"<OLD."WrittenOffAmount"
                        THEN RAISE EXCEPTION 'Subscription revenue rollups are monotonic append-only totals'; END IF;
                        RETURN NEW;
                    END $guard$;

                    CREATE OR REPLACE FUNCTION platform.nexora_guard_accounting_outbox() RETURNS trigger LANGUAGE plpgsql AS $guard$
                    BEGIN
                        IF TG_OP='DELETE' THEN RAISE EXCEPTION 'accounting outbox records are immutable'; END IF;
                        IF NEW."Id" IS DISTINCT FROM OLD."Id" OR NEW."TenantId" IS DISTINCT FROM OLD."TenantId"
                           OR NEW."SubscriptionInvoiceId" IS DISTINCT FROM OLD."SubscriptionInvoiceId"
                           OR NEW."SubscriptionRevenueActionId" IS DISTINCT FROM OLD."SubscriptionRevenueActionId"
                           OR NEW."MessageType" IS DISTINCT FROM OLD."MessageType" OR NEW."IdempotencyKey" IS DISTINCT FROM OLD."IdempotencyKey"
                           OR NEW."PayloadJson" IS DISTINCT FROM OLD."PayloadJson" OR NEW."PayloadSha256" IS DISTINCT FROM OLD."PayloadSha256"
                           OR NEW."CreatedAtUtc" IS DISTINCT FROM OLD."CreatedAtUtc" OR NEW."MaxAttempts" IS DISTINCT FROM OLD."MaxAttempts"
                        THEN RAISE EXCEPTION 'accounting outbox identity and payload are immutable'; END IF;
                        IF NOT ((OLD."Status" IN ('Pending','RetryScheduled') AND NEW."Status"='InFlight')
                            OR (OLD."Status"='InFlight' AND NEW."Status" IN ('InFlight','Acknowledged','RetryScheduled','Poison'))
                            OR (OLD."Status"='Poison' AND NEW."Status"='Pending'))
                        THEN RAISE EXCEPTION 'invalid accounting outbox transition % -> %',OLD."Status",NEW."Status"; END IF;
                        RETURN NEW;
                    END $guard$;

                    CREATE OR REPLACE FUNCTION platform.nexora_reconcile_subscription_invoice_rollups()
                    RETURNS trigger LANGUAGE plpgsql AS $reconcile$
                    DECLARE
                        invoice_id bigint;
                        invoice_row platform."SubscriptionInvoices"%ROWTYPE;
                        credited numeric(14,2);
                        paid numeric(14,2);
                        refunded numeric(14,2);
                        reversed numeric(14,2);
                        written_off numeric(14,2);
                    BEGIN
                        IF TG_RELID='platform."SubscriptionInvoices"'::regclass THEN
                            invoice_id := NEW."Id";
                        ELSE
                            invoice_id := NEW."SubscriptionInvoiceId";
                        END IF;
                        SELECT * INTO invoice_row FROM platform."SubscriptionInvoices" WHERE "Id"=invoice_id;
                        IF NOT FOUND THEN RETURN NULL; END IF;
                        SELECT COALESCE(sum("Amount"),0) INTO credited FROM platform."SubscriptionCreditNotes" WHERE "SubscriptionInvoiceId"=invoice_id;
                        SELECT COALESCE(sum("Amount"),0) INTO paid FROM platform."SubscriptionPayments" WHERE "SubscriptionInvoiceId"=invoice_id;
                        SELECT COALESCE(sum("Amount") FILTER (WHERE "Kind"='Refund' AND "Status"='Completed'),0),
                               COALESCE(sum("Amount") FILTER (WHERE "Kind"='PaymentReversal' AND "Status"='Completed'),0),
                               COALESCE(sum("Amount") FILTER (WHERE "Kind"='WriteOff' AND "Status"='Completed'),0)
                          INTO refunded,reversed,written_off
                          FROM platform."SubscriptionRevenueActions" WHERE "SubscriptionInvoiceId"=invoice_id;
                        IF invoice_row."CreditedAmount"<>credited OR invoice_row."PaidAmount"<>paid
                           OR invoice_row."RefundedAmount"<>refunded OR invoice_row."ReversedPaymentAmount"<>reversed
                           OR invoice_row."WrittenOffAmount"<>written_off
                        THEN RAISE EXCEPTION 'subscription invoice rollups do not reconcile to append-only revenue records'; END IF;
                        RETURN NULL;
                    END $reconcile$;
                    REVOKE ALL ON FUNCTION platform.nexora_reconcile_subscription_invoice_rollups() FROM PUBLIC;
                    CREATE CONSTRAINT TRIGGER subscription_invoice_rollups_reconcile
                        AFTER INSERT OR UPDATE ON platform."SubscriptionInvoices" DEFERRABLE INITIALLY DEFERRED
                        FOR EACH ROW EXECUTE FUNCTION platform.nexora_reconcile_subscription_invoice_rollups();
                    CREATE CONSTRAINT TRIGGER subscription_credit_rollups_reconcile
                        AFTER INSERT OR UPDATE ON platform."SubscriptionCreditNotes" DEFERRABLE INITIALLY DEFERRED
                        FOR EACH ROW EXECUTE FUNCTION platform.nexora_reconcile_subscription_invoice_rollups();
                    CREATE CONSTRAINT TRIGGER subscription_payment_rollups_reconcile
                        AFTER INSERT OR UPDATE ON platform."SubscriptionPayments" DEFERRABLE INITIALLY DEFERRED
                        FOR EACH ROW EXECUTE FUNCTION platform.nexora_reconcile_subscription_invoice_rollups();
                    CREATE CONSTRAINT TRIGGER subscription_action_rollups_reconcile
                        AFTER INSERT OR UPDATE ON platform."SubscriptionRevenueActions" DEFERRABLE INITIALLY DEFERRED
                        FOR EACH ROW EXECUTE FUNCTION platform.nexora_reconcile_subscription_invoice_rollups();
                    ALTER TABLE platform."SubscriptionInvoices" ENABLE ALWAYS TRIGGER subscription_invoice_rollups_reconcile;
                    ALTER TABLE platform."SubscriptionCreditNotes" ENABLE ALWAYS TRIGGER subscription_credit_rollups_reconcile;
                    ALTER TABLE platform."SubscriptionPayments" ENABLE ALWAYS TRIGGER subscription_payment_rollups_reconcile;
                    ALTER TABLE platform."SubscriptionRevenueActions" ENABLE ALWAYS TRIGGER subscription_action_rollups_reconcile;

                    ALTER TABLE platform."SubscriptionRevenueActions" ENABLE ROW LEVEL SECURITY;
                    ALTER TABLE platform."SubscriptionRevenueActions" FORCE ROW LEVEL SECURITY;
                    ALTER TABLE platform."SubscriptionTaxRules" ENABLE ROW LEVEL SECURITY;
                    ALTER TABLE platform."SubscriptionTaxRules" FORCE ROW LEVEL SECURITY;

                    DO $platform_revenue_security$
                    BEGIN
                        REVOKE ALL ON TABLE platform."SubscriptionRevenueActions", platform."SubscriptionTaxRules" FROM PUBLIC;
                        IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname='nexora_pipeline_app') THEN
                            CREATE POLICY subscription_revenue_actions_platform_fleet ON platform."SubscriptionRevenueActions"
                                FOR ALL TO nexora_pipeline_app USING (true) WITH CHECK (true);
                            CREATE POLICY subscription_tax_rules_platform_fleet ON platform."SubscriptionTaxRules"
                                FOR ALL TO nexora_pipeline_app USING (true) WITH CHECK (true);
                            GRANT SELECT, INSERT, UPDATE ON TABLE platform."SubscriptionRevenueActions", platform."SubscriptionTaxRules" TO nexora_pipeline_app;
                            GRANT USAGE, SELECT ON SEQUENCE platform."SubscriptionRevenueActions_Id_seq", platform."SubscriptionTaxRules_Id_seq" TO nexora_pipeline_app;
                            REVOKE DELETE, TRUNCATE ON TABLE platform."SubscriptionRevenueActions", platform."SubscriptionTaxRules" FROM nexora_pipeline_app;
                        END IF;
                        IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname='nexora_tenant_app') THEN
                            REVOKE ALL ON TABLE platform."SubscriptionRevenueActions", platform."SubscriptionTaxRules" FROM nexora_tenant_app;
                            REVOKE ALL ON SEQUENCE platform."SubscriptionRevenueActions_Id_seq", platform."SubscriptionTaxRules_Id_seq" FROM nexora_tenant_app;
                        END IF;
                        IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname='nexora_identity_app') THEN
                            REVOKE ALL ON TABLE platform."SubscriptionRevenueActions", platform."SubscriptionTaxRules" FROM nexora_identity_app;
                            REVOKE ALL ON SEQUENCE platform."SubscriptionRevenueActions_Id_seq", platform."SubscriptionTaxRules_Id_seq" FROM nexora_identity_app;
                        END IF;
                    END $platform_revenue_security$;
                    """);
            }
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            if (migrationBuilder.ActiveProvider?.Contains("Npgsql", StringComparison.OrdinalIgnoreCase) == true)
            {
                migrationBuilder.Sql("""
                    DO $irreversible_revenue_data$
                    BEGIN
                        IF EXISTS (SELECT 1 FROM platform."SubscriptionRevenueActions")
                           OR EXISTS (SELECT 1 FROM platform."SubscriptionTaxRules")
                           OR EXISTS (SELECT 1 FROM platform."AccountingOutbox" WHERE "SubscriptionRevenueActionId" IS NOT NULL)
                           OR EXISTS (SELECT 1 FROM platform."SubscriptionInvoices"
                                      WHERE "RefundedAmount"<>0 OR "ReversedPaymentAmount"<>0 OR "WrittenOffAmount"<>0
                                         OR "TaxRuleId" IS NOT NULL OR "TaxEvidenceJson" IS NOT NULL) THEN
                            RAISE EXCEPTION 'FinalPlatformRevenueControls contains legal or revenue data and cannot be downgraded safely';
                        END IF;
                    END $irreversible_revenue_data$;

                    DROP TRIGGER IF EXISTS subscription_invoice_rollups_reconcile ON platform."SubscriptionInvoices";
                    DROP TRIGGER IF EXISTS subscription_credit_rollups_reconcile ON platform."SubscriptionCreditNotes";
                    DROP TRIGGER IF EXISTS subscription_payment_rollups_reconcile ON platform."SubscriptionPayments";
                    DROP TRIGGER IF EXISTS subscription_action_rollups_reconcile ON platform."SubscriptionRevenueActions";
                    -- Every dependent object is a trigger created by this same migration. CASCADE is
                    -- intentionally scoped to this migration-owned function and makes rollback robust
                    -- if PostgreSQL retains a deferred trigger dependency until transaction end.
                    DROP FUNCTION IF EXISTS platform.nexora_reconcile_subscription_invoice_rollups() CASCADE;
                    """);
            }

            migrationBuilder.DropForeignKey(
                name: "FK_AccountingOutbox_SubscriptionRevenueActions_TenantId_Subscr~",
                schema: "platform",
                table: "AccountingOutbox");

            migrationBuilder.DropForeignKey(
                name: "FK_SubscriptionInvoices_SubscriptionTaxRules_TaxRuleId_TaxRule~",
                schema: "platform",
                table: "SubscriptionInvoices");

            migrationBuilder.DropTable(
                name: "SubscriptionRevenueActions",
                schema: "platform");

            migrationBuilder.DropTable(
                name: "SubscriptionTaxRules",
                schema: "platform");

            migrationBuilder.DropIndex(
                name: "IX_SubscriptionInvoices_TaxRuleId_TaxRuleVersion",
                schema: "platform",
                table: "SubscriptionInvoices");

            migrationBuilder.DropIndex(
                name: "IX_AccountingOutbox_Invoice_MessageType",
                schema: "platform",
                table: "AccountingOutbox");

            migrationBuilder.DropIndex(
                name: "IX_AccountingOutbox_SubscriptionRevenueActionId",
                schema: "platform",
                table: "AccountingOutbox");

            migrationBuilder.DropIndex(
                name: "IX_AccountingOutbox_TenantId_SubscriptionInvoiceId_Subscriptio~",
                schema: "platform",
                table: "AccountingOutbox");

            migrationBuilder.DropColumn(
                name: "RefundedAmount",
                schema: "platform",
                table: "SubscriptionInvoices");

            migrationBuilder.DropColumn(
                name: "ReversedPaymentAmount",
                schema: "platform",
                table: "SubscriptionInvoices");

            migrationBuilder.DropColumn(
                name: "TaxDeterminedAtUtc",
                schema: "platform",
                table: "SubscriptionInvoices");

            migrationBuilder.DropColumn(
                name: "TaxEvidenceJson",
                schema: "platform",
                table: "SubscriptionInvoices");

            migrationBuilder.DropColumn(
                name: "TaxEvidenceSha256",
                schema: "platform",
                table: "SubscriptionInvoices");

            migrationBuilder.DropColumn(
                name: "TaxJurisdictionCode",
                schema: "platform",
                table: "SubscriptionInvoices");

            migrationBuilder.DropColumn(
                name: "TaxRuleId",
                schema: "platform",
                table: "SubscriptionInvoices");

            migrationBuilder.DropColumn(
                name: "TaxRuleVersion",
                schema: "platform",
                table: "SubscriptionInvoices");

            migrationBuilder.DropColumn(
                name: "WrittenOffAmount",
                schema: "platform",
                table: "SubscriptionInvoices");

            migrationBuilder.DropColumn(
                name: "SubscriptionRevenueActionId",
                schema: "platform",
                table: "AccountingOutbox");

            migrationBuilder.CreateIndex(
                name: "IX_AccountingOutbox_TenantId_SubscriptionInvoiceId",
                schema: "platform",
                table: "AccountingOutbox",
                columns: new[] { "TenantId", "SubscriptionInvoiceId" });

            migrationBuilder.CreateIndex(
                name: "UX_AccountingOutbox_Invoice_MessageType",
                schema: "platform",
                table: "AccountingOutbox",
                columns: new[] { "SubscriptionInvoiceId", "MessageType" },
                unique: true);

            if (migrationBuilder.ActiveProvider?.Contains("Npgsql", StringComparison.OrdinalIgnoreCase) == true)
            {
                migrationBuilder.Sql("""
                    ALTER TABLE platform."SubscriptionInvoices"
                        DROP CONSTRAINT IF EXISTS "CK_SubscriptionInvoices_RevenueAmounts",
                        DROP CONSTRAINT IF EXISTS "CK_SubscriptionInvoices_TaxEvidenceTuple",
                        ADD CONSTRAINT "CK_SubscriptionInvoices_Amounts"
                            CHECK ("Subtotal">=0 AND "TaxAmount">=0 AND "TotalAmount">=0
                                   AND "CreditedAmount">=0 AND "PaidAmount">=0
                                   AND "TotalAmount"="Subtotal"+"TaxAmount"
                                   AND "CreditedAmount"+"PaidAmount"<="TotalAmount");

                    DROP FUNCTION IF EXISTS platform.nexora_guard_subscription_revenue_action();
                    DROP FUNCTION IF EXISTS platform.nexora_guard_subscription_tax_rule();

                    CREATE OR REPLACE FUNCTION platform.nexora_guard_subscription_invoice()
                    RETURNS trigger LANGUAGE plpgsql AS $guard$
                    BEGIN
                        IF TG_OP='DELETE' THEN RAISE EXCEPTION 'Subscription invoices are immutable; use a credit or void record'; END IF;
                        IF OLD."Status"='Draft' AND NEW."Status" NOT IN ('Draft','Finalized') THEN RAISE EXCEPTION 'A draft subscription invoice may only be finalized'; END IF;
                        IF OLD."Status"<>'Draft' AND NEW."Status"='Draft' THEN RAISE EXCEPTION 'A posted subscription invoice can never return to draft'; END IF;
                        IF OLD."Status"='Paid' AND NEW."Status"<>'Paid' THEN RAISE EXCEPTION 'A paid subscription invoice requires a governed reversal record'; END IF;
                        IF OLD."Status"='Void' AND NEW."Status"<>'Void' THEN RAISE EXCEPTION 'A void subscription invoice is terminal'; END IF;
                        IF OLD."Status"<>'Draft' AND (
                            NEW."TenantId" IS DISTINCT FROM OLD."TenantId" OR NEW."BillingStatementId" IS DISTINCT FROM OLD."BillingStatementId"
                            OR NEW."InvoiceNumber" IS DISTINCT FROM OLD."InvoiceNumber" OR NEW."Currency" IS DISTINCT FROM OLD."Currency"
                            OR NEW."Subtotal" IS DISTINCT FROM OLD."Subtotal" OR NEW."TaxRatePercent" IS DISTINCT FROM OLD."TaxRatePercent"
                            OR NEW."TaxAmount" IS DISTINCT FROM OLD."TaxAmount" OR NEW."TotalAmount" IS DISTINCT FROM OLD."TotalAmount"
                            OR NEW."IssuedAtUtc" IS DISTINCT FROM OLD."IssuedAtUtc" OR NEW."DueAtUtc" IS DISTINCT FROM OLD."DueAtUtc"
                            OR NEW."SellerSnapshotJson" IS DISTINCT FROM OLD."SellerSnapshotJson" OR NEW."BuyerSnapshotJson" IS DISTINCT FROM OLD."BuyerSnapshotJson"
                            OR NEW."TaxTreatment" IS DISTINCT FROM OLD."TaxTreatment" OR NEW."SourceEvidenceJson" IS DISTINCT FROM OLD."SourceEvidenceJson"
                            OR NEW."SourceEvidenceSha256" IS DISTINCT FROM OLD."SourceEvidenceSha256" OR NEW."CreatedBy" IS DISTINCT FROM OLD."CreatedBy"
                            OR NEW."CreatedAtUtc" IS DISTINCT FROM OLD."CreatedAtUtc" OR NEW."FinalizedBy" IS DISTINCT FROM OLD."FinalizedBy"
                            OR NEW."FinalizedAtUtc" IS DISTINCT FROM OLD."FinalizedAtUtc")
                        THEN RAISE EXCEPTION 'Finalized subscription invoice identity and source evidence are immutable'; END IF;
                        RETURN NEW;
                    END $guard$;

                    CREATE OR REPLACE FUNCTION platform.nexora_guard_accounting_outbox() RETURNS trigger LANGUAGE plpgsql AS $guard$
                    BEGIN
                        IF TG_OP='DELETE' THEN RAISE EXCEPTION 'accounting outbox records are immutable'; END IF;
                        IF NEW."Id" IS DISTINCT FROM OLD."Id" OR NEW."TenantId" IS DISTINCT FROM OLD."TenantId"
                           OR NEW."SubscriptionInvoiceId" IS DISTINCT FROM OLD."SubscriptionInvoiceId"
                           OR NEW."MessageType" IS DISTINCT FROM OLD."MessageType" OR NEW."IdempotencyKey" IS DISTINCT FROM OLD."IdempotencyKey"
                           OR NEW."PayloadJson" IS DISTINCT FROM OLD."PayloadJson" OR NEW."PayloadSha256" IS DISTINCT FROM OLD."PayloadSha256"
                           OR NEW."CreatedAtUtc" IS DISTINCT FROM OLD."CreatedAtUtc" OR NEW."MaxAttempts" IS DISTINCT FROM OLD."MaxAttempts"
                        THEN RAISE EXCEPTION 'accounting outbox identity and payload are immutable'; END IF;
                        IF NOT ((OLD."Status" IN ('Pending','RetryScheduled') AND NEW."Status"='InFlight')
                            OR (OLD."Status"='InFlight' AND NEW."Status" IN ('InFlight','Acknowledged','RetryScheduled','Poison'))
                            OR (OLD."Status"='Poison' AND NEW."Status"='Pending'))
                        THEN RAISE EXCEPTION 'invalid accounting outbox transition % -> %',OLD."Status",NEW."Status"; END IF;
                        RETURN NEW;
                    END $guard$;
                    """);
            }
        }
    }
}
