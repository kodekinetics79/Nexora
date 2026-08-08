using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace ERP_RFQ_Automation.Migrations
{
    /// <inheritdoc />
    public partial class Wave6PlatformFoundations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddUniqueConstraint(
                name: "AK_SubscriptionInvoices_TenantId_Id",
                schema: "platform",
                table: "SubscriptionInvoices",
                columns: new[] { "TenantId", "Id" });

            migrationBuilder.CreateTable(
                name: "AccountingOutbox",
                schema: "platform",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<long>(type: "bigint", nullable: false),
                    SubscriptionInvoiceId = table.Column<long>(type: "bigint", nullable: false),
                    MessageType = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    IdempotencyKey = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    PayloadJson = table.Column<string>(type: "jsonb", nullable: false),
                    PayloadSha256 = table.Column<string>(type: "character(64)", fixedLength: true, maxLength: 64, nullable: false),
                    Status = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    ReconciliationStatus = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    AttemptCount = table.Column<int>(type: "integer", nullable: false),
                    MaxAttempts = table.Column<int>(type: "integer", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    AvailableAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    LastAttemptAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LeaseExpiresAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LeaseToken = table.Column<Guid>(type: "uuid", nullable: true),
                    WorkerId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    LastFailureCode = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    ExternalReference = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    ExternalReceiptSha256 = table.Column<string>(type: "character(64)", fixedLength: true, maxLength: 64, nullable: true),
                    AcknowledgedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    AcknowledgedBy = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    RedrivenAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    RedrivenBy = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    RedriveReason = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AccountingOutbox", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AccountingOutbox_SubscriptionInvoices_TenantId_Subscription~",
                        columns: x => new { x.TenantId, x.SubscriptionInvoiceId },
                        principalSchema: "platform",
                        principalTable: "SubscriptionInvoices",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "TenantDataRecoveryEvidence",
                schema: "platform",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    TenantId = table.Column<long>(type: "bigint", nullable: false),
                    TenantDataAssetId = table.Column<long>(type: "bigint", nullable: true),
                    ScopeKey = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    EvidenceType = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    OpaqueProviderReference = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    OpaqueBackupSetReference = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    RecoveryPointUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    OperationStartedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CompletedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ConfiguredRpoSeconds = table.Column<int>(type: "integer", nullable: true),
                    ConfiguredRtoSeconds = table.Column<int>(type: "integer", nullable: true),
                    ActualRecoverySeconds = table.Column<int>(type: "integer", nullable: true),
                    RetainUntilUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CustomerRowsObserved = table.Column<long>(type: "bigint", nullable: true),
                    EvidenceReference = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    EvidenceSha256 = table.Column<string>(type: "character(64)", fixedLength: true, maxLength: 64, nullable: false),
                    CorrelationId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    IdempotencyKey = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    ActorPlatformUserId = table.Column<long>(type: "bigint", nullable: false),
                    ActorEmail = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false),
                    Reason = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    RecordedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TenantDataRecoveryEvidence", x => x.Id);
                    table.CheckConstraint("CK_TenantDataRecoveryEvidence_Rows", "\"CustomerRowsObserved\" IS NULL OR \"CustomerRowsObserved\" >= 0");
                    table.CheckConstraint("CK_TenantDataRecoveryEvidence_Rpo", "\"ConfiguredRpoSeconds\" IS NULL OR \"ConfiguredRpoSeconds\" > 0");
                    table.CheckConstraint("CK_TenantDataRecoveryEvidence_Rto", "\"ConfiguredRtoSeconds\" IS NULL OR \"ConfiguredRtoSeconds\" > 0");
                });

            migrationBuilder.CreateTable(
                name: "TenantDeletionCertificates",
                schema: "platform",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    TenantId = table.Column<long>(type: "bigint", nullable: false),
                    TenantSlug = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    PurgedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CertifiedUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ActorPlatformUserId = table.Column<long>(type: "bigint", nullable: false),
                    ActorEmail = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false),
                    EvidenceManifestSha256 = table.Column<string>(type: "character(64)", fixedLength: true, maxLength: 64, nullable: false),
                    EvidenceIdsJson = table.Column<string>(type: "jsonb", nullable: false),
                    Reason = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TenantDeletionCertificates", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "UsageEvents",
                schema: "platform",
                columns: table => new
                {
                    UsageEventId = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<long>(type: "bigint", nullable: false),
                    Kind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    EventType = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Quantity = table.Column<decimal>(type: "numeric(20,6)", precision: 20, scale: 6, nullable: false),
                    Unit = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    OccurredAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ReceivedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    SourceRecordType = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    SourceRecordId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    SourceSystem = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ActorId = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    Provider = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    Model = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    CorrelationId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    IdempotencyKey = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    CostAmount = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: false),
                    Currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    EvidenceSha256 = table.Column<string>(type: "character(64)", fixedLength: true, maxLength: 64, nullable: false),
                    RatingStatus = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    AdjustsUsageEventId = table.Column<Guid>(type: "uuid", nullable: true),
                    RateCardId = table.Column<long>(type: "bigint", nullable: true),
                    RateCardLineId = table.Column<long>(type: "bigint", nullable: true),
                    RateCardVersion = table.Column<long>(type: "bigint", nullable: true),
                    AllowanceApplied = table.Column<decimal>(type: "numeric(20,6)", precision: 20, scale: 6, nullable: false),
                    OverageQuantity = table.Column<decimal>(type: "numeric(20,6)", precision: 20, scale: 6, nullable: false),
                    UnitPrice = table.Column<decimal>(type: "numeric(18,8)", precision: 18, scale: 8, nullable: true),
                    RatedAmount = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UsageEvents", x => x.UsageEventId);
                    table.UniqueConstraint("AK_UsageEvents_TenantId_UsageEventId", x => new { x.TenantId, x.UsageEventId });
                    table.ForeignKey(
                        name: "FK_UsageEvents_UsageEvents_TenantId_AdjustsUsageEventId",
                        columns: x => new { x.TenantId, x.AdjustsUsageEventId },
                        principalSchema: "platform",
                        principalTable: "UsageEvents",
                        principalColumns: new[] { "TenantId", "UsageEventId" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "UsageMinuteAggregates",
                schema: "platform",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    TenantId = table.Column<long>(type: "bigint", nullable: false),
                    EventType = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Unit = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    MinuteUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Quantity = table.Column<decimal>(type: "numeric(20,6)", precision: 20, scale: 6, nullable: false),
                    CostAmount = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: false),
                    EventCount = table.Column<int>(type: "integer", nullable: false),
                    RefreshedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UsageMinuteAggregates", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AccountingOutbox_Status_AvailableAtUtc",
                schema: "platform",
                table: "AccountingOutbox",
                columns: new[] { "Status", "AvailableAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_AccountingOutbox_TenantId_SubscriptionInvoiceId",
                schema: "platform",
                table: "AccountingOutbox",
                columns: new[] { "TenantId", "SubscriptionInvoiceId" });

            migrationBuilder.CreateIndex(
                name: "UX_AccountingOutbox_IdempotencyKey",
                schema: "platform",
                table: "AccountingOutbox",
                column: "IdempotencyKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UX_AccountingOutbox_Invoice_MessageType",
                schema: "platform",
                table: "AccountingOutbox",
                columns: new[] { "SubscriptionInvoiceId", "MessageType" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TenantDataRecoveryEvidence_TenantId_IdempotencyKey",
                schema: "platform",
                table: "TenantDataRecoveryEvidence",
                columns: new[] { "TenantId", "IdempotencyKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TenantDataRecoveryEvidence_TenantId_ScopeKey_EvidenceType_C~",
                schema: "platform",
                table: "TenantDataRecoveryEvidence",
                columns: new[] { "TenantId", "ScopeKey", "EvidenceType", "CompletedUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_TenantDeletionCertificates_TenantId",
                schema: "platform",
                table: "TenantDeletionCertificates",
                column: "TenantId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_UsageEvents_AdjustsUsageEventId",
                schema: "platform",
                table: "UsageEvents",
                column: "AdjustsUsageEventId");

            migrationBuilder.CreateIndex(
                name: "IX_UsageEvents_TenantId_AdjustsUsageEventId",
                schema: "platform",
                table: "UsageEvents",
                columns: new[] { "TenantId", "AdjustsUsageEventId" });

            migrationBuilder.CreateIndex(
                name: "IX_UsageEvents_TenantId_EventType_OccurredAtUtc",
                schema: "platform",
                table: "UsageEvents",
                columns: new[] { "TenantId", "EventType", "OccurredAtUtc" });

            migrationBuilder.CreateIndex(
                name: "UX_UsageEvents_Tenant_IdempotencyKey",
                schema: "platform",
                table: "UsageEvents",
                columns: new[] { "TenantId", "IdempotencyKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UX_UsageMinuteAggregates_Bucket",
                schema: "platform",
                table: "UsageMinuteAggregates",
                columns: new[] { "TenantId", "EventType", "Unit", "MinuteUtc" },
                unique: true);

            migrationBuilder.Sql("""
                ALTER TABLE platform."UsageEvents"
                    ADD CONSTRAINT "CK_UsageEvents_Kind" CHECK ("Kind" IN ('Consumption', 'Adjustment')),
                    ADD CONSTRAINT "CK_UsageEvents_RatingStatus" CHECK ("RatingStatus" IN ('Pending', 'Ready', 'BlockedUncertifiedMeter', 'Rated')),
                    ADD CONSTRAINT "CK_UsageEvents_Currency" CHECK ("Currency" ~ '^[A-Z]{3}$'),
                    ADD CONSTRAINT "CK_UsageEvents_EvidenceHash" CHECK ("EvidenceSha256" ~ '^[0-9a-f]{64}$'),
                    ADD CONSTRAINT "CK_UsageEvents_Quantity" CHECK (
                        ("Kind" = 'Consumption' AND "AdjustsUsageEventId" IS NULL AND "Quantity" > 0 AND "CostAmount" >= 0)
                        OR ("Kind" = 'Adjustment' AND "AdjustsUsageEventId" IS NOT NULL AND "Quantity" <> 0)),
                    ADD CONSTRAINT "CK_UsageEvents_Rating" CHECK (
                        "AllowanceApplied" >= 0 AND "AllowanceApplied" <= GREATEST("Quantity", 0)
                        AND ("UnitPrice" IS NULL OR "UnitPrice" >= 0)),
                    ADD CONSTRAINT "CK_UsageEvents_Meter" CHECK (("EventType", "Unit") IN (
                        ('processing.minutes','minute'), ('documents','document'), ('pages.processed','page'),
                        ('rfqs','rfq'), ('quotes','quote'), ('orders','order'), ('emails','email'),
                        ('pages.ocr','page'), ('ai.tokens','token'), ('api.calls','call'),
                        ('storage.gb-hours','gb-hour'), ('supplier.searches','search'), ('automation.runs','run'),
                        ('base.subscription','subscription'), ('users','user'), ('dedicated.infrastructure','instance')));

                ALTER TABLE platform."TenantDataRecoveryEvidence"
                    ADD CONSTRAINT "CK_TenantDataRecoveryEvidence_Type" CHECK ("EvidenceType" IN (
                        'BackupObserved','RestoreDrill','TombstoneReapplied','BackupDestroyed',
                        'SubprocessorDeletionRequested','SubprocessorDeletionConfirmed','ResidencyVerified')),
                    ADD CONSTRAINT "CK_TenantDataRecoveryEvidence_Hash" CHECK ("EvidenceSha256" ~ '^[0-9a-f]{64}$'),
                    ADD CONSTRAINT "CK_TenantDataRecoveryEvidence_ActualRecovery" CHECK ("ActualRecoverySeconds" IS NULL OR "ActualRecoverySeconds" >= 0);

                ALTER TABLE platform."TenantDeletionCertificates"
                    ADD CONSTRAINT "CK_TenantDeletionCertificates_Hash" CHECK ("EvidenceManifestSha256" ~ '^[0-9a-f]{64}$');

                ALTER TABLE platform."AccountingOutbox"
                    ADD CONSTRAINT "CK_AccountingOutbox_Status" CHECK ("Status" IN ('Pending','InFlight','RetryScheduled','Acknowledged','Poison')),
                    ADD CONSTRAINT "CK_AccountingOutbox_Reconciliation" CHECK ("ReconciliationStatus" IN ('NotSent','AwaitingAcknowledgement','Reconciled','Exception')),
                    ADD CONSTRAINT "CK_AccountingOutbox_Attempts" CHECK ("MaxAttempts" > 0 AND "AttemptCount" >= 0 AND "AttemptCount" <= "MaxAttempts"),
                    ADD CONSTRAINT "CK_AccountingOutbox_PayloadHash" CHECK ("PayloadSha256" ~ '^[0-9a-f]{64}$'),
                    ADD CONSTRAINT "CK_AccountingOutbox_ReceiptHash" CHECK ("ExternalReceiptSha256" IS NULL OR "ExternalReceiptSha256" ~ '^[0-9a-f]{64}$'),
                    ADD CONSTRAINT "CK_AccountingOutbox_Lease" CHECK (
                        ("Status" = 'InFlight' AND "LeaseToken" IS NOT NULL AND "LeaseExpiresAtUtc" IS NOT NULL AND "WorkerId" IS NOT NULL)
                        OR ("Status" <> 'InFlight' AND "LeaseToken" IS NULL AND "LeaseExpiresAtUtc" IS NULL AND "WorkerId" IS NULL)),
                    ADD CONSTRAINT "CK_AccountingOutbox_Acknowledgement" CHECK (
                        ("Status" = 'Acknowledged' AND "AcknowledgedAtUtc" IS NOT NULL AND "ExternalReference" IS NOT NULL AND "ExternalReceiptSha256" IS NOT NULL AND "ReconciliationStatus" = 'Reconciled')
                        OR "Status" <> 'Acknowledged');

                CREATE TRIGGER usage_events_immutable
                    BEFORE UPDATE OR DELETE ON platform."UsageEvents"
                    FOR EACH ROW EXECUTE FUNCTION platform.nexora_guard_append_only_record();
                ALTER TABLE platform."UsageEvents" ENABLE ALWAYS TRIGGER usage_events_immutable;
                CREATE TRIGGER tenant_data_recovery_evidence_immutable
                    BEFORE UPDATE OR DELETE ON platform."TenantDataRecoveryEvidence"
                    FOR EACH ROW EXECUTE FUNCTION platform.nexora_guard_append_only_record();
                ALTER TABLE platform."TenantDataRecoveryEvidence" ENABLE ALWAYS TRIGGER tenant_data_recovery_evidence_immutable;
                CREATE TRIGGER tenant_deletion_certificates_immutable
                    BEFORE UPDATE OR DELETE ON platform."TenantDeletionCertificates"
                    FOR EACH ROW EXECUTE FUNCTION platform.nexora_guard_append_only_record();
                ALTER TABLE platform."TenantDeletionCertificates" ENABLE ALWAYS TRIGGER tenant_deletion_certificates_immutable;

                CREATE OR REPLACE FUNCTION platform.nexora_guard_accounting_outbox()
                RETURNS trigger LANGUAGE plpgsql AS $guard$
                BEGIN
                    IF TG_OP = 'DELETE' THEN
                        RAISE EXCEPTION 'accounting outbox records are immutable';
                    END IF;
                    IF NEW."Id" IS DISTINCT FROM OLD."Id"
                       OR NEW."TenantId" IS DISTINCT FROM OLD."TenantId"
                       OR NEW."SubscriptionInvoiceId" IS DISTINCT FROM OLD."SubscriptionInvoiceId"
                       OR NEW."MessageType" IS DISTINCT FROM OLD."MessageType"
                       OR NEW."IdempotencyKey" IS DISTINCT FROM OLD."IdempotencyKey"
                       OR NEW."PayloadJson" IS DISTINCT FROM OLD."PayloadJson"
                       OR NEW."PayloadSha256" IS DISTINCT FROM OLD."PayloadSha256"
                       OR NEW."CreatedAtUtc" IS DISTINCT FROM OLD."CreatedAtUtc"
                       OR NEW."MaxAttempts" IS DISTINCT FROM OLD."MaxAttempts" THEN
                        RAISE EXCEPTION 'accounting outbox identity and payload are immutable';
                    END IF;
                    IF NOT ((OLD."Status" IN ('Pending','RetryScheduled') AND NEW."Status" = 'InFlight')
                            OR (OLD."Status" = 'InFlight' AND NEW."Status" IN ('InFlight','Acknowledged','RetryScheduled','Poison'))
                            OR (OLD."Status" = 'Poison' AND NEW."Status" = 'Pending')) THEN
                        RAISE EXCEPTION 'invalid accounting outbox transition % -> %', OLD."Status", NEW."Status";
                    END IF;
                    RETURN NEW;
                END $guard$;
                REVOKE ALL ON FUNCTION platform.nexora_guard_accounting_outbox() FROM PUBLIC;
                CREATE TRIGGER accounting_outbox_guard
                    BEFORE UPDATE OR DELETE ON platform."AccountingOutbox"
                    FOR EACH ROW EXECUTE FUNCTION platform.nexora_guard_accounting_outbox();
                ALTER TABLE platform."AccountingOutbox" ENABLE ALWAYS TRIGGER accounting_outbox_guard;

                REVOKE ALL ON TABLE platform."UsageEvents", platform."UsageMinuteAggregates",
                    platform."AccountingOutbox", platform."TenantDataRecoveryEvidence",
                    platform."TenantDeletionCertificates" FROM PUBLIC;
                DO $roles$
                BEGIN
                    IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'nexora_pipeline_app') THEN
                        GRANT SELECT, INSERT ON platform."UsageEvents", platform."TenantDataRecoveryEvidence", platform."TenantDeletionCertificates" TO nexora_pipeline_app;
                        GRANT SELECT, INSERT, UPDATE ON platform."UsageMinuteAggregates", platform."AccountingOutbox" TO nexora_pipeline_app;
                        GRANT USAGE, SELECT ON SEQUENCE platform."UsageMinuteAggregates_Id_seq", platform."TenantDataRecoveryEvidence_Id_seq", platform."TenantDeletionCertificates_Id_seq" TO nexora_pipeline_app;
                        REVOKE DELETE, TRUNCATE ON platform."UsageEvents", platform."UsageMinuteAggregates", platform."AccountingOutbox", platform."TenantDataRecoveryEvidence", platform."TenantDeletionCertificates" FROM nexora_pipeline_app;
                        REVOKE UPDATE ON platform."UsageEvents", platform."TenantDataRecoveryEvidence", platform."TenantDeletionCertificates" FROM nexora_pipeline_app;
                    END IF;
                    IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'nexora_tenant_app') THEN
                        REVOKE ALL ON platform."UsageEvents", platform."UsageMinuteAggregates", platform."AccountingOutbox", platform."TenantDataRecoveryEvidence", platform."TenantDeletionCertificates" FROM nexora_tenant_app;
                    END IF;
                    IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'nexora_identity_app') THEN
                        REVOKE ALL ON platform."UsageEvents", platform."UsageMinuteAggregates", platform."AccountingOutbox", platform."TenantDataRecoveryEvidence", platform."TenantDeletionCertificates" FROM nexora_identity_app;
                    END IF;
                END $roles$;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP FUNCTION IF EXISTS platform.nexora_guard_accounting_outbox();");

            migrationBuilder.DropTable(
                name: "AccountingOutbox",
                schema: "platform");

            migrationBuilder.DropTable(
                name: "TenantDataRecoveryEvidence",
                schema: "platform");

            migrationBuilder.DropTable(
                name: "TenantDeletionCertificates",
                schema: "platform");

            migrationBuilder.DropTable(
                name: "UsageEvents",
                schema: "platform");

            migrationBuilder.DropTable(
                name: "UsageMinuteAggregates",
                schema: "platform");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_SubscriptionInvoices_TenantId_Id",
                schema: "platform",
                table: "SubscriptionInvoices");
        }
    }
}
