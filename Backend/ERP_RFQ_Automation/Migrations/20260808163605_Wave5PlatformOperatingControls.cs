using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace ERP_RFQ_Automation.Migrations
{
    /// <inheritdoc />
    public partial class Wave5PlatformOperatingControls : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "MfaAuthenticatedAtUtc",
                schema: "platform",
                table: "PlatformSessions",
                type: "timestamp without time zone",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "PlatformMfaChallenges",
                schema: "platform",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    PlatformUserId = table.Column<long>(type: "bigint", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    ExpiresAtUtc = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    FailedAttempts = table.Column<int>(type: "integer", nullable: false),
                    ConsumedAtUtc = table.Column<DateTime>(type: "timestamp without time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PlatformMfaChallenges", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PlatformMfaChallenges_PlatformUsers_PlatformUserId",
                        column: x => x.PlatformUserId,
                        principalSchema: "platform",
                        principalTable: "PlatformUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "PlatformMfaCredentials",
                schema: "platform",
                columns: table => new
                {
                    PlatformUserId = table.Column<long>(type: "bigint", nullable: false),
                    TotpSecretProtected = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: false),
                    EnabledAtUtc = table.Column<DateTime>(type: "timestamp without time zone", nullable: true),
                    LastAcceptedTotpStep = table.Column<long>(type: "bigint", nullable: true),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp without time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PlatformMfaCredentials", x => x.PlatformUserId);
                    table.ForeignKey(
                        name: "FK_PlatformMfaCredentials_PlatformUsers_PlatformUserId",
                        column: x => x.PlatformUserId,
                        principalSchema: "platform",
                        principalTable: "PlatformUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "PlatformMfaRecoveryCodes",
                schema: "platform",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    PlatformUserId = table.Column<long>(type: "bigint", nullable: false),
                    CodeHash = table.Column<string>(type: "character(64)", fixedLength: true, maxLength: 64, nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    UsedAtUtc = table.Column<DateTime>(type: "timestamp without time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PlatformMfaRecoveryCodes", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PlatformMfaRecoveryCodes_PlatformUsers_PlatformUserId",
                        column: x => x.PlatformUserId,
                        principalSchema: "platform",
                        principalTable: "PlatformUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "SubscriptionInvoices",
                schema: "platform",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    TenantId = table.Column<long>(type: "bigint", nullable: false),
                    BillingStatementId = table.Column<long>(type: "bigint", nullable: false),
                    InvoiceNumber = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Status = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    Currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    Subtotal = table.Column<decimal>(type: "numeric(14,2)", precision: 14, scale: 2, nullable: false),
                    TaxRatePercent = table.Column<decimal>(type: "numeric(7,4)", precision: 7, scale: 4, nullable: false),
                    TaxAmount = table.Column<decimal>(type: "numeric(14,2)", precision: 14, scale: 2, nullable: false),
                    TotalAmount = table.Column<decimal>(type: "numeric(14,2)", precision: 14, scale: 2, nullable: false),
                    CreditedAmount = table.Column<decimal>(type: "numeric(14,2)", precision: 14, scale: 2, nullable: false),
                    PaidAmount = table.Column<decimal>(type: "numeric(14,2)", precision: 14, scale: 2, nullable: false),
                    IssuedAtUtc = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    DueAtUtc = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    SellerSnapshotJson = table.Column<string>(type: "jsonb", nullable: false),
                    BuyerSnapshotJson = table.Column<string>(type: "jsonb", nullable: false),
                    TaxTreatment = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    SourceEvidenceJson = table.Column<string>(type: "jsonb", nullable: false),
                    SourceEvidenceSha256 = table.Column<string>(type: "character(64)", fixedLength: true, maxLength: 64, nullable: false),
                    CreatedBy = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    FinalizedBy = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    FinalizedAtUtc = table.Column<DateTime>(type: "timestamp without time zone", nullable: true),
                    Version = table.Column<long>(type: "bigint", nullable: false, defaultValue: 1L)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SubscriptionInvoices", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SubscriptionInvoices_BillingStatements_BillingStatementId",
                        column: x => x.BillingStatementId,
                        principalSchema: "platform",
                        principalTable: "BillingStatements",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SubscriptionInvoices_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalSchema: "platform",
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "TenantDataAssets",
                schema: "platform",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    TenantId = table.Column<long>(type: "bigint", nullable: false),
                    LogicalKey = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    AssetType = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    OpaqueProviderReference = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    Region = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Classification = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Disposition = table.Column<string>(type: "character varying(96)", maxLength: 96, nullable: false),
                    BackupPolicyReference = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    BackupPolicyVersion = table.Column<int>(type: "integer", nullable: false),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    VerifiedBusinessUnitId = table.Column<long>(type: "bigint", nullable: true),
                    VerificationEvidenceReference = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    VerificationEvidenceSha256 = table.Column<string>(type: "character(64)", fixedLength: true, maxLength: 64, nullable: true),
                    VerificationVersion = table.Column<int>(type: "integer", nullable: false),
                    VerifiedOn = table.Column<DateTime>(type: "timestamp without time zone", nullable: true),
                    VerifiedBy = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    Version = table.Column<long>(type: "bigint", nullable: false),
                    CreatedOn = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    CreatedBy = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    ModifiedOn = table.Column<DateTime>(type: "timestamp without time zone", nullable: true),
                    ModifiedBy = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TenantDataAssets", x => x.Id);
                    table.CheckConstraint("CK_TenantDataAssets_BackupPolicyVersion", "\"BackupPolicyVersion\" > 0");
                    table.CheckConstraint("CK_TenantDataAssets_VerificationVersion", "\"VerificationVersion\" >= 0");
                    table.CheckConstraint("CK_TenantDataAssets_Version", "\"Version\" > 0");
                    table.ForeignKey(
                        name: "FK_TenantDataAssets_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalSchema: "platform",
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "SubscriptionCreditNotes",
                schema: "platform",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    SubscriptionInvoiceId = table.Column<long>(type: "bigint", nullable: false),
                    CreditNumber = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    IdempotencyKey = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Amount = table.Column<decimal>(type: "numeric(14,2)", precision: 14, scale: 2, nullable: false),
                    Reason = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    CreatedBy = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp without time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SubscriptionCreditNotes", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SubscriptionCreditNotes_SubscriptionInvoices_SubscriptionIn~",
                        column: x => x.SubscriptionInvoiceId,
                        principalSchema: "platform",
                        principalTable: "SubscriptionInvoices",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "SubscriptionPayments",
                schema: "platform",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    SubscriptionInvoiceId = table.Column<long>(type: "bigint", nullable: false),
                    ExternalReference = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Amount = table.Column<decimal>(type: "numeric(14,2)", precision: 14, scale: 2, nullable: false),
                    ReceivedAtUtc = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    RecordedBy = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    RecordedAtUtc = table.Column<DateTime>(type: "timestamp without time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SubscriptionPayments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SubscriptionPayments_SubscriptionInvoices_SubscriptionInvoi~",
                        column: x => x.SubscriptionInvoiceId,
                        principalSchema: "platform",
                        principalTable: "SubscriptionInvoices",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PlatformMfaChallenges_PlatformUserId_ExpiresAtUtc_ConsumedA~",
                schema: "platform",
                table: "PlatformMfaChallenges",
                columns: new[] { "PlatformUserId", "ExpiresAtUtc", "ConsumedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_PlatformMfaRecoveryCodes_PlatformUserId_CodeHash",
                schema: "platform",
                table: "PlatformMfaRecoveryCodes",
                columns: new[] { "PlatformUserId", "CodeHash" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SubscriptionCreditNotes_CreditNumber",
                schema: "platform",
                table: "SubscriptionCreditNotes",
                column: "CreditNumber",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SubscriptionCreditNotes_SubscriptionInvoiceId",
                schema: "platform",
                table: "SubscriptionCreditNotes",
                column: "SubscriptionInvoiceId");

            migrationBuilder.CreateIndex(
                name: "IX_SubscriptionCreditNotes_IdempotencyKey",
                schema: "platform",
                table: "SubscriptionCreditNotes",
                column: "IdempotencyKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SubscriptionInvoices_BillingStatementId",
                schema: "platform",
                table: "SubscriptionInvoices",
                column: "BillingStatementId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SubscriptionInvoices_InvoiceNumber",
                schema: "platform",
                table: "SubscriptionInvoices",
                column: "InvoiceNumber",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SubscriptionInvoices_TenantId_Status_DueAtUtc",
                schema: "platform",
                table: "SubscriptionInvoices",
                columns: new[] { "TenantId", "Status", "DueAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_SubscriptionPayments_ExternalReference",
                schema: "platform",
                table: "SubscriptionPayments",
                column: "ExternalReference",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SubscriptionPayments_SubscriptionInvoiceId",
                schema: "platform",
                table: "SubscriptionPayments",
                column: "SubscriptionInvoiceId");

            migrationBuilder.CreateIndex(
                name: "IX_TenantDataAssets_TenantId_LogicalKey",
                schema: "platform",
                table: "TenantDataAssets",
                columns: new[] { "TenantId", "LogicalKey" },
                unique: true);

            if (migrationBuilder.ActiveProvider?.Contains("Npgsql", StringComparison.OrdinalIgnoreCase) == true)
            {
                migrationBuilder.Sql("""
                    CREATE OR REPLACE FUNCTION platform.nexora_guard_subscription_invoice()
                    RETURNS trigger LANGUAGE plpgsql AS $guard$
                    BEGIN
                        IF TG_OP = 'DELETE' THEN
                            RAISE EXCEPTION 'Subscription invoices are immutable; use a credit or void record';
                        END IF;
                        IF OLD."Status" = 'Draft' AND NEW."Status" NOT IN ('Draft', 'Finalized') THEN
                            RAISE EXCEPTION 'A draft subscription invoice may only be finalized';
                        END IF;
                        IF OLD."Status" <> 'Draft' AND NEW."Status" = 'Draft' THEN
                            RAISE EXCEPTION 'A posted subscription invoice can never return to draft';
                        END IF;
                        IF OLD."Status" = 'Paid' AND NEW."Status" <> 'Paid' THEN
                            RAISE EXCEPTION 'A paid subscription invoice requires a governed reversal record';
                        END IF;
                        IF OLD."Status" = 'Void' AND NEW."Status" <> 'Void' THEN
                            RAISE EXCEPTION 'A void subscription invoice is terminal';
                        END IF;
                        IF OLD."Status" = 'Draft' AND NEW."Status" = 'Finalized' AND (
                            NEW."TenantId" IS DISTINCT FROM OLD."TenantId" OR
                            NEW."BillingStatementId" IS DISTINCT FROM OLD."BillingStatementId" OR
                            NEW."Currency" IS DISTINCT FROM OLD."Currency" OR
                            NEW."Subtotal" IS DISTINCT FROM OLD."Subtotal" OR
                            NEW."TaxRatePercent" IS DISTINCT FROM OLD."TaxRatePercent" OR
                            NEW."TaxAmount" IS DISTINCT FROM OLD."TaxAmount" OR
                            NEW."TotalAmount" IS DISTINCT FROM OLD."TotalAmount" OR
                            NEW."IssuedAtUtc" IS DISTINCT FROM OLD."IssuedAtUtc" OR
                            NEW."DueAtUtc" IS DISTINCT FROM OLD."DueAtUtc" OR
                            NEW."SellerSnapshotJson" IS DISTINCT FROM OLD."SellerSnapshotJson" OR
                            NEW."BuyerSnapshotJson" IS DISTINCT FROM OLD."BuyerSnapshotJson" OR
                            NEW."TaxTreatment" IS DISTINCT FROM OLD."TaxTreatment" OR
                            NEW."SourceEvidenceJson" IS DISTINCT FROM OLD."SourceEvidenceJson" OR
                            NEW."SourceEvidenceSha256" IS DISTINCT FROM OLD."SourceEvidenceSha256" OR
                            NEW."CreatedBy" IS DISTINCT FROM OLD."CreatedBy" OR
                            NEW."CreatedAtUtc" IS DISTINCT FROM OLD."CreatedAtUtc") THEN
                            RAISE EXCEPTION 'Invoice source and tax evidence cannot change during finalization';
                        END IF;
                        IF OLD."Status" <> 'Draft' AND (
                            NEW."TenantId" IS DISTINCT FROM OLD."TenantId" OR
                            NEW."BillingStatementId" IS DISTINCT FROM OLD."BillingStatementId" OR
                            NEW."InvoiceNumber" IS DISTINCT FROM OLD."InvoiceNumber" OR
                            NEW."Currency" IS DISTINCT FROM OLD."Currency" OR
                            NEW."Subtotal" IS DISTINCT FROM OLD."Subtotal" OR
                            NEW."TaxRatePercent" IS DISTINCT FROM OLD."TaxRatePercent" OR
                            NEW."TaxAmount" IS DISTINCT FROM OLD."TaxAmount" OR
                            NEW."TotalAmount" IS DISTINCT FROM OLD."TotalAmount" OR
                            NEW."IssuedAtUtc" IS DISTINCT FROM OLD."IssuedAtUtc" OR
                            NEW."DueAtUtc" IS DISTINCT FROM OLD."DueAtUtc" OR
                            NEW."SellerSnapshotJson" IS DISTINCT FROM OLD."SellerSnapshotJson" OR
                            NEW."BuyerSnapshotJson" IS DISTINCT FROM OLD."BuyerSnapshotJson" OR
                            NEW."TaxTreatment" IS DISTINCT FROM OLD."TaxTreatment" OR
                            NEW."SourceEvidenceJson" IS DISTINCT FROM OLD."SourceEvidenceJson" OR
                            NEW."SourceEvidenceSha256" IS DISTINCT FROM OLD."SourceEvidenceSha256" OR
                            NEW."CreatedBy" IS DISTINCT FROM OLD."CreatedBy" OR
                            NEW."CreatedAtUtc" IS DISTINCT FROM OLD."CreatedAtUtc" OR
                            NEW."FinalizedBy" IS DISTINCT FROM OLD."FinalizedBy" OR
                            NEW."FinalizedAtUtc" IS DISTINCT FROM OLD."FinalizedAtUtc") THEN
                            RAISE EXCEPTION 'Finalized subscription invoice identity and source evidence are immutable';
                        END IF;
                        RETURN NEW;
                    END $guard$;

                    CREATE TRIGGER subscription_invoices_guard
                        BEFORE UPDATE OR DELETE ON platform."SubscriptionInvoices"
                        FOR EACH ROW EXECUTE FUNCTION platform.nexora_guard_subscription_invoice();
                    ALTER TABLE platform."SubscriptionInvoices" ENABLE ALWAYS TRIGGER subscription_invoices_guard;

                    ALTER TABLE platform."SubscriptionInvoices"
                        ADD CONSTRAINT "CK_SubscriptionInvoices_Amounts"
                        CHECK ("Subtotal" >= 0 AND "TaxAmount" >= 0 AND "TotalAmount" >= 0
                               AND "CreditedAmount" >= 0 AND "PaidAmount" >= 0
                               AND "TotalAmount" = "Subtotal" + "TaxAmount"
                               AND "CreditedAmount" + "PaidAmount" <= "TotalAmount");

                    CREATE TRIGGER subscription_credit_notes_immutable
                        BEFORE UPDATE OR DELETE ON platform."SubscriptionCreditNotes"
                        FOR EACH ROW EXECUTE FUNCTION platform.nexora_guard_append_only_record();
                    ALTER TABLE platform."SubscriptionCreditNotes" ENABLE ALWAYS TRIGGER subscription_credit_notes_immutable;
                    CREATE TRIGGER subscription_payments_immutable
                        BEFORE UPDATE OR DELETE ON platform."SubscriptionPayments"
                        FOR EACH ROW EXECUTE FUNCTION platform.nexora_guard_append_only_record();
                    ALTER TABLE platform."SubscriptionPayments" ENABLE ALWAYS TRIGGER subscription_payments_immutable;

                    DO $platform_security_grants$
                    BEGIN
                        IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'nexora_pipeline_app') THEN
                            RETURN;
                        END IF;
                        GRANT SELECT, INSERT, UPDATE ON TABLE
                            platform."PlatformMfaCredentials", platform."PlatformMfaRecoveryCodes",
                            platform."PlatformMfaChallenges", platform."TenantDataAssets",
                            platform."SubscriptionInvoices", platform."SubscriptionCreditNotes",
                            platform."SubscriptionPayments" TO nexora_pipeline_app;
                        GRANT USAGE, SELECT, UPDATE ON SEQUENCE
                            platform."PlatformMfaRecoveryCodes_Id_seq", platform."TenantDataAssets_Id_seq",
                            platform."SubscriptionInvoices_Id_seq", platform."SubscriptionCreditNotes_Id_seq",
                            platform."SubscriptionPayments_Id_seq" TO nexora_pipeline_app;
                        REVOKE DELETE, TRUNCATE ON TABLE
                            platform."PlatformMfaCredentials", platform."PlatformMfaRecoveryCodes",
                            platform."PlatformMfaChallenges", platform."TenantDataAssets",
                            platform."SubscriptionInvoices", platform."SubscriptionCreditNotes",
                            platform."SubscriptionPayments" FROM nexora_pipeline_app;
                        GRANT SELECT ("Features") ON TABLE platform."Plans"
                            TO nexora_tenant_app, nexora_identity_app;
                        REVOKE ALL PRIVILEGES ON TABLE
                            platform."PlatformMfaCredentials", platform."PlatformMfaRecoveryCodes",
                            platform."PlatformMfaChallenges", platform."TenantDataAssets",
                            platform."SubscriptionInvoices", platform."SubscriptionCreditNotes",
                            platform."SubscriptionPayments" FROM nexora_tenant_app, nexora_identity_app;
                    END $platform_security_grants$;
                    """);
            }
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PlatformMfaChallenges",
                schema: "platform");

            migrationBuilder.DropTable(
                name: "PlatformMfaCredentials",
                schema: "platform");

            migrationBuilder.DropTable(
                name: "PlatformMfaRecoveryCodes",
                schema: "platform");

            migrationBuilder.DropTable(
                name: "SubscriptionCreditNotes",
                schema: "platform");

            migrationBuilder.DropTable(
                name: "SubscriptionPayments",
                schema: "platform");

            migrationBuilder.DropTable(
                name: "TenantDataAssets",
                schema: "platform");

            migrationBuilder.DropTable(
                name: "SubscriptionInvoices",
                schema: "platform");

            migrationBuilder.DropColumn(
                name: "MfaAuthenticatedAtUtc",
                schema: "platform",
                table: "PlatformSessions");

            if (migrationBuilder.ActiveProvider?.Contains("Npgsql", StringComparison.OrdinalIgnoreCase) == true)
                migrationBuilder.Sql("DROP FUNCTION IF EXISTS platform.nexora_guard_subscription_invoice();");
        }
    }
}
