using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace ERP_RFQ_Automation.Migrations
{
    /// <inheritdoc />
    public partial class Release01ACanonicalLeadIdentity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "CurrentInquiryFingerprint",
                table: "Leads",
                type: "character(64)",
                fixedLength: true,
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CurrentOccurrenceClassification",
                table: "Leads",
                type: "character varying(48)",
                maxLength: 48,
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "CurrentRevisionId",
                table: "Leads",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "CurrentRevisionNumber",
                table: "Leads",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "IdentityVersion",
                table: "Leads",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "IngestedAtUtc",
                table: "Leads",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "SourceReceivedAtUtc",
                table: "Leads",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "LeadIdentityAuditEvents",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    BusinessUnitId = table.Column<long>(type: "bigint", nullable: false),
                    LeadId = table.Column<long>(type: "bigint", nullable: true),
                    OccurrenceId = table.Column<long>(type: "bigint", nullable: false),
                    EventType = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    PayloadJson = table.Column<string>(type: "jsonb", nullable: false),
                    ActorType = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    ActorId = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    CorrelationId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    IdempotencyKey = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    OccurredAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LeadIdentityAuditEvents", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "LeadIngestionBatches",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    BusinessUnitId = table.Column<long>(type: "bigint", nullable: false),
                    SourceChannel = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    CreatedBy = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Version = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LeadIngestionBatches", x => x.Id);
                    table.UniqueConstraint("AK_LeadIngestionBatches_BusinessUnitId_Id", x => new { x.BusinessUnitId, x.Id });
                });

            migrationBuilder.CreateTable(
                name: "LeadIngestionOccurrences",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    BusinessUnitId = table.Column<long>(type: "bigint", nullable: false),
                    BatchId = table.Column<Guid>(type: "uuid", nullable: false),
                    LeadId = table.Column<long>(type: "bigint", nullable: true),
                    LeadRevisionId = table.Column<long>(type: "bigint", nullable: true),
                    SourceDocumentId = table.Column<long>(type: "bigint", nullable: true),
                    ExtractionJobId = table.Column<long>(type: "bigint", nullable: true),
                    SourceChannel = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    IdempotencyKey = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    ExternalSourceId = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    EmailThreadId = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    SourceSystem = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    Sender = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    Subject = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    OriginalFileName = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    MimeType = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    FileSize = table.Column<long>(type: "bigint", nullable: true),
                    ContentHash = table.Column<string>(type: "character(64)", fixedLength: true, maxLength: 64, nullable: true),
                    CustomerScopeKey = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    LogicalInquiryFingerprint = table.Column<string>(type: "character(64)", fixedLength: true, maxLength: 64, nullable: false),
                    Classification = table.Column<string>(type: "character varying(48)", maxLength: 48, nullable: false),
                    Confidence = table.Column<decimal>(type: "numeric(6,5)", precision: 6, scale: 5, nullable: false),
                    DecisionReasonsJson = table.Column<string>(type: "jsonb", nullable: false),
                    PolicyVersion = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ProcessingPath = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    ExternalAiUsed = table.Column<bool>(type: "boolean", nullable: false),
                    ExternalCost = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: false),
                    SourceReceivedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    IngestedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ActorType = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    ActorId = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    CorrelationId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Version = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LeadIngestionOccurrences", x => x.Id);
                    table.UniqueConstraint("AK_LeadIngestionOccurrences_BusinessUnitId_Id", x => new { x.BusinessUnitId, x.Id });
                    table.ForeignKey(
                        name: "FK_LeadIngestionOccurrences_ExtractionJobs_BusinessUnitId_Extr~",
                        columns: x => new { x.BusinessUnitId, x.ExtractionJobId },
                        principalTable: "ExtractionJobs",
                        principalColumns: new[] { "BusinessUnitId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_LeadIngestionOccurrences_LeadIngestionBatches_BusinessUnitI~",
                        columns: x => new { x.BusinessUnitId, x.BatchId },
                        principalTable: "LeadIngestionBatches",
                        principalColumns: new[] { "BusinessUnitId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_LeadIngestionOccurrences_Leads_BusinessUnitId_LeadId",
                        columns: x => new { x.BusinessUnitId, x.LeadId },
                        principalTable: "Leads",
                        principalColumns: new[] { "BusinessUnitID", "ID" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_LeadIngestionOccurrences_source_documents_BusinessUnitId_So~",
                        columns: x => new { x.BusinessUnitId, x.SourceDocumentId },
                        principalTable: "source_documents",
                        principalColumns: new[] { "business_unit_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "LeadMatchCandidates",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    BusinessUnitId = table.Column<long>(type: "bigint", nullable: false),
                    OccurrenceId = table.Column<long>(type: "bigint", nullable: false),
                    CandidateLeadId = table.Column<long>(type: "bigint", nullable: false),
                    Confidence = table.Column<decimal>(type: "numeric(6,5)", precision: 6, scale: 5, nullable: false),
                    MatchEvidenceJson = table.Column<string>(type: "jsonb", nullable: false),
                    DifferencesJson = table.Column<string>(type: "jsonb", nullable: false),
                    DownstreamImpactJson = table.Column<string>(type: "jsonb", nullable: false),
                    ReviewState = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    ReviewedBy = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    ReviewedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ReviewReason = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    Version = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LeadMatchCandidates", x => x.Id);
                    table.ForeignKey(
                        name: "FK_LeadMatchCandidates_LeadIngestionOccurrences_BusinessUnitId~",
                        columns: x => new { x.BusinessUnitId, x.OccurrenceId },
                        principalTable: "LeadIngestionOccurrences",
                        principalColumns: new[] { "BusinessUnitId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_LeadMatchCandidates_Leads_BusinessUnitId_CandidateLeadId",
                        columns: x => new { x.BusinessUnitId, x.CandidateLeadId },
                        principalTable: "Leads",
                        principalColumns: new[] { "BusinessUnitID", "ID" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "LeadRevisions",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    BusinessUnitId = table.Column<long>(type: "bigint", nullable: false),
                    LeadId = table.Column<long>(type: "bigint", nullable: false),
                    RevisionNumber = table.Column<int>(type: "integer", nullable: false),
                    EstablishedByOccurrenceId = table.Column<long>(type: "bigint", nullable: false),
                    LogicalInquiryFingerprint = table.Column<string>(type: "character(64)", fixedLength: true, maxLength: 64, nullable: false),
                    SnapshotJson = table.Column<string>(type: "jsonb", nullable: false),
                    CustomerRfqReference = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    NormalizedCustomerRfqReference = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    CustomerIdSnapshot = table.Column<long>(type: "bigint", nullable: true),
                    ContactIdSnapshot = table.Column<long>(type: "bigint", nullable: true),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CreatedBy = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    ProcessingPath = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    ExternalAiUsed = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LeadRevisions", x => x.Id);
                    table.UniqueConstraint("AK_LeadRevisions_BusinessUnitId_Id", x => new { x.BusinessUnitId, x.Id });
                    table.ForeignKey(
                        name: "FK_LeadRevisions_LeadIngestionOccurrences_BusinessUnitId_Estab~",
                        columns: x => new { x.BusinessUnitId, x.EstablishedByOccurrenceId },
                        principalTable: "LeadIngestionOccurrences",
                        principalColumns: new[] { "BusinessUnitId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_LeadRevisions_Leads_BusinessUnitId_LeadId",
                        columns: x => new { x.BusinessUnitId, x.LeadId },
                        principalTable: "Leads",
                        principalColumns: new[] { "BusinessUnitID", "ID" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "LeadItemRevisions",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    BusinessUnitId = table.Column<long>(type: "bigint", nullable: false),
                    LeadRevisionId = table.Column<long>(type: "bigint", nullable: false),
                    LineNumber = table.Column<int>(type: "integer", nullable: false),
                    LineFingerprint = table.Column<string>(type: "character(64)", fixedLength: true, maxLength: 64, nullable: false),
                    SnapshotJson = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LeadItemRevisions", x => x.Id);
                    table.UniqueConstraint("AK_LeadItemRevisions_BusinessUnitId_Id", x => new { x.BusinessUnitId, x.Id });
                    table.ForeignKey(
                        name: "FK_LeadItemRevisions_LeadRevisions_BusinessUnitId_LeadRevision~",
                        columns: x => new { x.BusinessUnitId, x.LeadRevisionId },
                        principalTable: "LeadRevisions",
                        principalColumns: new[] { "BusinessUnitId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "LeadRevisionDifferences",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    BusinessUnitId = table.Column<long>(type: "bigint", nullable: false),
                    LeadRevisionId = table.Column<long>(type: "bigint", nullable: false),
                    ChangeType = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Scope = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Path = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    PreviousValueJson = table.Column<string>(type: "jsonb", nullable: true),
                    CurrentValueJson = table.Column<string>(type: "jsonb", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LeadRevisionDifferences", x => x.Id);
                    table.ForeignKey(
                        name: "FK_LeadRevisionDifferences_LeadRevisions_BusinessUnitId_LeadRe~",
                        columns: x => new { x.BusinessUnitId, x.LeadRevisionId },
                        principalTable: "LeadRevisions",
                        principalColumns: new[] { "BusinessUnitId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "LeadRevisionImpacts",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    BusinessUnitId = table.Column<long>(type: "bigint", nullable: false),
                    LeadId = table.Column<long>(type: "bigint", nullable: false),
                    LeadRevisionId = table.Column<long>(type: "bigint", nullable: false),
                    AggregateType = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    AggregateId = table.Column<long>(type: "bigint", nullable: false),
                    ImpactType = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    DetailsJson = table.Column<string>(type: "jsonb", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ResolvedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LeadRevisionImpacts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_LeadRevisionImpacts_LeadRevisions_BusinessUnitId_LeadRevisi~",
                        columns: x => new { x.BusinessUnitId, x.LeadRevisionId },
                        principalTable: "LeadRevisions",
                        principalColumns: new[] { "BusinessUnitId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_LeadRevisionImpacts_Leads_BusinessUnitId_LeadId",
                        columns: x => new { x.BusinessUnitId, x.LeadId },
                        principalTable: "Leads",
                        principalColumns: new[] { "BusinessUnitID", "ID" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Leads_BusinessUnitID_CurrentInquiryFingerprint",
                table: "Leads",
                columns: new[] { "BusinessUnitID", "CurrentInquiryFingerprint" });

            migrationBuilder.CreateIndex(
                name: "IX_Leads_BusinessUnitID_CurrentRevisionId",
                table: "Leads",
                columns: new[] { "BusinessUnitID", "CurrentRevisionId" });

            migrationBuilder.CreateIndex(
                name: "IX_canonical_inquiries_business_unit_id_lead_id",
                table: "canonical_inquiries",
                columns: new[] { "business_unit_id", "lead_id" });

            migrationBuilder.CreateIndex(
                name: "IX_LeadIdentityAuditEvents_BusinessUnitId_IdempotencyKey",
                table: "LeadIdentityAuditEvents",
                columns: new[] { "BusinessUnitId", "IdempotencyKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_LeadIdentityAuditEvents_BusinessUnitId_LeadId_OccurredAtUtc",
                table: "LeadIdentityAuditEvents",
                columns: new[] { "BusinessUnitId", "LeadId", "OccurredAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_LeadIngestionBatches_BusinessUnitId_CreatedAtUtc",
                table: "LeadIngestionBatches",
                columns: new[] { "BusinessUnitId", "CreatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_LeadIngestionOccurrences_BusinessUnitId_BatchId",
                table: "LeadIngestionOccurrences",
                columns: new[] { "BusinessUnitId", "BatchId" });

            migrationBuilder.CreateIndex(
                name: "IX_LeadIngestionOccurrences_BusinessUnitId_Classification_Crea~",
                table: "LeadIngestionOccurrences",
                columns: new[] { "BusinessUnitId", "Classification", "CreatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_LeadIngestionOccurrences_BusinessUnitId_ContentHash_Custome~",
                table: "LeadIngestionOccurrences",
                columns: new[] { "BusinessUnitId", "ContentHash", "CustomerScopeKey" });

            migrationBuilder.CreateIndex(
                name: "IX_LeadIngestionOccurrences_BusinessUnitId_ExtractionJobId",
                table: "LeadIngestionOccurrences",
                columns: new[] { "BusinessUnitId", "ExtractionJobId" });

            migrationBuilder.CreateIndex(
                name: "IX_LeadIngestionOccurrences_BusinessUnitId_IdempotencyKey",
                table: "LeadIngestionOccurrences",
                columns: new[] { "BusinessUnitId", "IdempotencyKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_LeadIngestionOccurrences_BusinessUnitId_LeadId",
                table: "LeadIngestionOccurrences",
                columns: new[] { "BusinessUnitId", "LeadId" });

            migrationBuilder.CreateIndex(
                name: "IX_LeadIngestionOccurrences_BusinessUnitId_LogicalInquiryFinge~",
                table: "LeadIngestionOccurrences",
                columns: new[] { "BusinessUnitId", "LogicalInquiryFingerprint" });

            migrationBuilder.CreateIndex(
                name: "IX_LeadIngestionOccurrences_BusinessUnitId_SourceDocumentId",
                table: "LeadIngestionOccurrences",
                columns: new[] { "BusinessUnitId", "SourceDocumentId" });

            migrationBuilder.CreateIndex(
                name: "IX_LeadItemRevisions_BusinessUnitId_LeadRevisionId_LineNumber",
                table: "LeadItemRevisions",
                columns: new[] { "BusinessUnitId", "LeadRevisionId", "LineNumber" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_LeadMatchCandidates_BusinessUnitId_CandidateLeadId",
                table: "LeadMatchCandidates",
                columns: new[] { "BusinessUnitId", "CandidateLeadId" });

            migrationBuilder.CreateIndex(
                name: "IX_LeadMatchCandidates_BusinessUnitId_OccurrenceId_CandidateLe~",
                table: "LeadMatchCandidates",
                columns: new[] { "BusinessUnitId", "OccurrenceId", "CandidateLeadId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_LeadRevisionDifferences_BusinessUnitId_LeadRevisionId",
                table: "LeadRevisionDifferences",
                columns: new[] { "BusinessUnitId", "LeadRevisionId" });

            migrationBuilder.CreateIndex(
                name: "IX_LeadRevisionImpacts_BusinessUnitId_LeadId",
                table: "LeadRevisionImpacts",
                columns: new[] { "BusinessUnitId", "LeadId" });

            migrationBuilder.CreateIndex(
                name: "IX_LeadRevisionImpacts_BusinessUnitId_LeadRevisionId_Aggregate~",
                table: "LeadRevisionImpacts",
                columns: new[] { "BusinessUnitId", "LeadRevisionId", "AggregateType", "AggregateId", "ImpactType" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_LeadRevisions_BusinessUnitId_EstablishedByOccurrenceId",
                table: "LeadRevisions",
                columns: new[] { "BusinessUnitId", "EstablishedByOccurrenceId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_LeadRevisions_BusinessUnitId_LeadId_RevisionNumber",
                table: "LeadRevisions",
                columns: new[] { "BusinessUnitId", "LeadId", "RevisionNumber" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_LeadRevisions_BusinessUnitId_LogicalInquiryFingerprint",
                table: "LeadRevisions",
                columns: new[] { "BusinessUnitId", "LogicalInquiryFingerprint" });

            migrationBuilder.Sql("""
                INSERT INTO "LeadIngestionBatches"
                    ("Id", "BusinessUnitId", "SourceChannel", "CreatedBy", "CreatedAtUtc", "UpdatedAtUtc", "Version")
                SELECT md5('release-01a-legacy-batch:' || l."BusinessUnitID"::text)::uuid,
                       l."BusinessUnitID", 'Legacy', 'migration:release-01a',
                       MIN(l."CreatedDate" AT TIME ZONE 'UTC'),
                       MAX(COALESCE(l."ModifiedDate", l."CreatedDate") AT TIME ZONE 'UTC'), 1
                FROM "Leads" l
                GROUP BY l."BusinessUnitID"
                ON CONFLICT ("Id") DO NOTHING;

                INSERT INTO "LeadIngestionOccurrences"
                    ("BusinessUnitId", "BatchId", "LeadId", "SourceChannel", "IdempotencyKey",
                     "LogicalInquiryFingerprint", "Classification", "Confidence", "DecisionReasonsJson",
                     "PolicyVersion", "ProcessingPath", "ExternalAiUsed", "ExternalCost",
                     "SourceReceivedAtUtc", "IngestedAtUtc", "CreatedAtUtc",
                     "ActorType", "ActorId", "CorrelationId", "Version")
                SELECT l."BusinessUnitID",
                       md5('release-01a-legacy-batch:' || l."BusinessUnitID"::text)::uuid,
                       l."ID", 'Legacy', 'legacy-lead:' || l."BusinessUnitID"::text || ':' || l."ID"::text,
                       md5('legacy-unclassified:' || l."BusinessUnitID"::text || ':' || l."ID"::text) ||
                       md5('legacy-unclassified:' || l."BusinessUnitID"::text || ':' || l."ID"::text),
                       'New', 1.0,
                       '["Migrated canonical Lead; historical source receipt and content fingerprint were not inferred."]'::jsonb,
                       'release-01a/legacy-backfill', 'HumanReview', FALSE, 0,
                       NULL, l."CreatedDate" AT TIME ZONE 'UTC', now(),
                       'Migration', 'migration:release-01a', 'migration:release-01a:' || l."ID"::text, 1
                FROM "Leads" l
                ON CONFLICT ("BusinessUnitId", "IdempotencyKey") DO NOTHING;

                INSERT INTO "LeadRevisions"
                    ("BusinessUnitId", "LeadId", "RevisionNumber", "EstablishedByOccurrenceId",
                     "LogicalInquiryFingerprint", "SnapshotJson", "CustomerRfqReference",
                     "NormalizedCustomerRfqReference", "CustomerIdSnapshot", "ContactIdSnapshot",
                     "CreatedAtUtc", "CreatedBy", "ProcessingPath", "ExternalAiUsed")
                SELECT l."BusinessUnitID", l."ID", 1, o."Id", o."LogicalInquiryFingerprint",
                       jsonb_build_object(
                         'legacy', true, 'rfq', l."RFQNo", 'buyer', l."BuyersName",
                         'closing', l."BidClosingDate", 'lineCount', l."NoOfLineItems"),
                       l."RFQNo", lower(regexp_replace(COALESCE(l."RFQNo", ''), '[^a-zA-Z0-9]+', '', 'g')),
                       l."CustomerID", l."ContactID", l."CreatedDate" AT TIME ZONE 'UTC',
                       'migration:release-01a', 'HumanReview', FALSE
                FROM "Leads" l
                JOIN "LeadIngestionOccurrences" o
                  ON o."BusinessUnitId" = l."BusinessUnitID"
                 AND o."IdempotencyKey" = 'legacy-lead:' || l."BusinessUnitID"::text || ':' || l."ID"::text
                ON CONFLICT ("BusinessUnitId", "LeadId", "RevisionNumber") DO NOTHING;

                INSERT INTO "LeadItemRevisions"
                    ("BusinessUnitId", "LeadRevisionId", "LineNumber", "LineFingerprint", "SnapshotJson")
                SELECT l."BusinessUnitID", r."Id",
                       row_number() OVER (PARTITION BY li."LeadID" ORDER BY li."ID")::integer,
                       md5('legacy-line:' || l."BusinessUnitID"::text || ':' || li."ID"::text) ||
                       md5('legacy-line:' || l."BusinessUnitID"::text || ':' || li."ID"::text),
                       jsonb_build_object(
                         'line', li."LineItemNo", 'part', COALESCE(li."ManufacturerPartNumber", li."ItemMaterialCode"),
                         'description', COALESCE(li."ProductShortDescription", li."ItemText"),
                         'quantity', li."Quantity", 'uom', li."UnitOfMeasure")
                FROM "LeadItems" li
                JOIN "Leads" l ON l."ID" = li."LeadID"
                JOIN "LeadRevisions" r
                  ON r."BusinessUnitId" = l."BusinessUnitID" AND r."LeadId" = l."ID" AND r."RevisionNumber" = 1
                ON CONFLICT ("BusinessUnitId", "LeadRevisionId", "LineNumber") DO NOTHING;

                UPDATE "LeadIngestionOccurrences" o
                SET "LeadRevisionId" = r."Id"
                FROM "LeadRevisions" r
                WHERE r."BusinessUnitId" = o."BusinessUnitId"
                  AND r."LeadId" = o."LeadId"
                  AND r."RevisionNumber" = 1
                  AND o."LeadRevisionId" IS NULL;

                UPDATE "Leads" l
                SET "CurrentRevisionId" = r."Id",
                    "CurrentRevisionNumber" = 1,
                    "IdentityVersion" = 1,
                    "CurrentInquiryFingerprint" = r."LogicalInquiryFingerprint",
                    "CurrentOccurrenceClassification" = 'New',
                    "IngestedAtUtc" = l."CreatedDate" AT TIME ZONE 'UTC'
                FROM "LeadRevisions" r
                WHERE r."BusinessUnitId" = l."BusinessUnitID"
                  AND r."LeadId" = l."ID"
                  AND r."RevisionNumber" = 1;

                INSERT INTO "LeadIdentityAuditEvents"
                    ("BusinessUnitId", "LeadId", "OccurrenceId", "EventType", "PayloadJson",
                     "ActorType", "ActorId", "CorrelationId", "IdempotencyKey", "OccurredAtUtc")
                SELECT o."BusinessUnitId", o."LeadId", o."Id", 'LEGACY_CANONICAL_IDENTITY_BACKFILLED',
                       jsonb_build_object('revision', 1, 'sourceFactsInferred', false),
                       'Migration', 'migration:release-01a', o."CorrelationId",
                       o."IdempotencyKey" || ':LEGACY_CANONICAL_IDENTITY_BACKFILLED', now()
                FROM "LeadIngestionOccurrences" o
                WHERE o."SourceChannel" = 'Legacy'
                ON CONFLICT ("BusinessUnitId", "IdempotencyKey") DO NOTHING;
                """);

            migrationBuilder.Sql("""
                DO $$
                DECLARE t text;
                BEGIN
                  FOREACH t IN ARRAY ARRAY[
                    'LeadIngestionBatches','LeadIngestionOccurrences','LeadRevisions',
                    'LeadItemRevisions','LeadRevisionDifferences','LeadMatchCandidates',
                    'LeadIdentityAuditEvents','LeadRevisionImpacts'
                  ] LOOP
                    EXECUTE format('ALTER TABLE %I ENABLE ROW LEVEL SECURITY', t);
                    EXECUTE format('ALTER TABLE %I FORCE ROW LEVEL SECURITY', t);
                    EXECUTE format(
                      'CREATE POLICY %I ON %I TO nexora_tenant_app USING ("BusinessUnitId" = NULLIF(current_setting(''nexora.business_unit_id'', true), '''')::bigint) WITH CHECK ("BusinessUnitId" = NULLIF(current_setting(''nexora.business_unit_id'', true), '''')::bigint)',
                      'nexora_tenant_isolation', t);
                    EXECUTE format('REVOKE DELETE, TRUNCATE ON TABLE %I FROM nexora_tenant_app', t);
                  END LOOP;
                END $$;

                GRANT SELECT, INSERT, UPDATE ON TABLE
                  "LeadIngestionBatches", "LeadIngestionOccurrences", "LeadMatchCandidates"
                  TO nexora_tenant_app;
                GRANT SELECT, INSERT ON TABLE
                  "LeadRevisions", "LeadItemRevisions", "LeadRevisionDifferences", "LeadIdentityAuditEvents", "LeadRevisionImpacts"
                  TO nexora_tenant_app;
                GRANT USAGE ON SEQUENCE
                  "LeadIngestionOccurrences_Id_seq", "LeadRevisions_Id_seq", "LeadItemRevisions_Id_seq",
                  "LeadRevisionDifferences_Id_seq", "LeadMatchCandidates_Id_seq",
                  "LeadIdentityAuditEvents_Id_seq", "LeadRevisionImpacts_Id_seq"
                  TO nexora_tenant_app;

                CREATE OR REPLACE FUNCTION nexora_release01a_forbid_history_mutation()
                RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                  RAISE EXCEPTION 'Release 01A identity history is append-only';
                END $$;

                CREATE TRIGGER trg_lead_revisions_append_only
                  BEFORE UPDATE OR DELETE ON "LeadRevisions"
                  FOR EACH ROW EXECUTE FUNCTION nexora_release01a_forbid_history_mutation();
                CREATE TRIGGER trg_lead_item_revisions_append_only
                  BEFORE UPDATE OR DELETE ON "LeadItemRevisions"
                  FOR EACH ROW EXECUTE FUNCTION nexora_release01a_forbid_history_mutation();
                CREATE TRIGGER trg_lead_revision_differences_append_only
                  BEFORE UPDATE OR DELETE ON "LeadRevisionDifferences"
                  FOR EACH ROW EXECUTE FUNCTION nexora_release01a_forbid_history_mutation();
                CREATE TRIGGER trg_lead_identity_audit_append_only
                  BEFORE UPDATE OR DELETE ON "LeadIdentityAuditEvents"
                  FOR EACH ROW EXECUTE FUNCTION nexora_release01a_forbid_history_mutation();
                CREATE TRIGGER trg_lead_revision_impacts_append_only
                  BEFORE UPDATE OR DELETE ON "LeadRevisionImpacts"
                  FOR EACH ROW EXECUTE FUNCTION nexora_release01a_forbid_history_mutation();

                CREATE OR REPLACE FUNCTION nexora_release01a_occurrence_guard()
                RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                  IF NEW."BusinessUnitId" IS DISTINCT FROM OLD."BusinessUnitId"
                     OR NEW."BatchId" IS DISTINCT FROM OLD."BatchId"
                     OR NEW."SourceChannel" IS DISTINCT FROM OLD."SourceChannel"
                     OR NEW."IdempotencyKey" IS DISTINCT FROM OLD."IdempotencyKey"
                     OR NEW."ExternalSourceId" IS DISTINCT FROM OLD."ExternalSourceId"
                     OR NEW."EmailThreadId" IS DISTINCT FROM OLD."EmailThreadId"
                     OR NEW."SourceSystem" IS DISTINCT FROM OLD."SourceSystem"
                     OR NEW."Sender" IS DISTINCT FROM OLD."Sender"
                     OR NEW."Subject" IS DISTINCT FROM OLD."Subject"
                     OR NEW."OriginalFileName" IS DISTINCT FROM OLD."OriginalFileName"
                     OR NEW."MimeType" IS DISTINCT FROM OLD."MimeType"
                     OR NEW."FileSize" IS DISTINCT FROM OLD."FileSize"
                     OR NEW."ContentHash" IS DISTINCT FROM OLD."ContentHash"
                     OR NEW."SourceDocumentId" IS DISTINCT FROM OLD."SourceDocumentId"
                     OR NEW."ExtractionJobId" IS DISTINCT FROM OLD."ExtractionJobId"
                     OR NEW."CustomerScopeKey" IS DISTINCT FROM OLD."CustomerScopeKey"
                     OR NEW."LogicalInquiryFingerprint" IS DISTINCT FROM OLD."LogicalInquiryFingerprint"
                     OR NEW."PolicyVersion" IS DISTINCT FROM OLD."PolicyVersion"
                     OR NEW."ProcessingPath" IS DISTINCT FROM OLD."ProcessingPath"
                     OR NEW."ExternalAiUsed" IS DISTINCT FROM OLD."ExternalAiUsed"
                     OR NEW."ExternalCost" IS DISTINCT FROM OLD."ExternalCost"
                     OR NEW."SourceReceivedAtUtc" IS DISTINCT FROM OLD."SourceReceivedAtUtc"
                     OR NEW."IngestedAtUtc" IS DISTINCT FROM OLD."IngestedAtUtc"
                     OR NEW."CreatedAtUtc" IS DISTINCT FROM OLD."CreatedAtUtc"
                     OR NEW."ActorType" IS DISTINCT FROM OLD."ActorType"
                     OR NEW."ActorId" IS DISTINCT FROM OLD."ActorId"
                     OR NEW."CorrelationId" IS DISTINCT FROM OLD."CorrelationId" THEN
                    RAISE EXCEPTION 'Ingestion provenance is immutable';
                  END IF;
                  RETURN NEW;
                END $$;
                CREATE TRIGGER trg_lead_occurrence_provenance_guard
                  BEFORE UPDATE ON "LeadIngestionOccurrences"
                  FOR EACH ROW EXECUTE FUNCTION nexora_release01a_occurrence_guard();
                """);

            migrationBuilder.AddForeignKey(
                name: "FK_canonical_inquiries_Leads_business_unit_id_lead_id",
                table: "canonical_inquiries",
                columns: new[] { "business_unit_id", "lead_id" },
                principalTable: "Leads",
                principalColumns: new[] { "BusinessUnitID", "ID" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Leads_LeadRevisions_BusinessUnitID_CurrentRevisionId",
                table: "Leads",
                columns: new[] { "BusinessUnitID", "CurrentRevisionId" },
                principalTable: "LeadRevisions",
                principalColumns: new[] { "BusinessUnitId", "Id" },
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_canonical_inquiries_Leads_business_unit_id_lead_id",
                table: "canonical_inquiries");

            migrationBuilder.DropForeignKey(
                name: "FK_Leads_LeadRevisions_BusinessUnitID_CurrentRevisionId",
                table: "Leads");

            migrationBuilder.DropTable(
                name: "LeadIdentityAuditEvents");

            migrationBuilder.DropTable(
                name: "LeadItemRevisions");

            migrationBuilder.DropTable(
                name: "LeadMatchCandidates");

            migrationBuilder.DropTable(
                name: "LeadRevisionDifferences");

            migrationBuilder.DropTable(
                name: "LeadRevisionImpacts");

            migrationBuilder.DropTable(
                name: "LeadRevisions");

            migrationBuilder.DropTable(
                name: "LeadIngestionOccurrences");

            migrationBuilder.DropTable(
                name: "LeadIngestionBatches");

            migrationBuilder.DropIndex(
                name: "IX_Leads_BusinessUnitID_CurrentInquiryFingerprint",
                table: "Leads");

            migrationBuilder.DropIndex(
                name: "IX_Leads_BusinessUnitID_CurrentRevisionId",
                table: "Leads");

            migrationBuilder.DropIndex(
                name: "IX_canonical_inquiries_business_unit_id_lead_id",
                table: "canonical_inquiries");

            migrationBuilder.DropColumn(
                name: "CurrentInquiryFingerprint",
                table: "Leads");

            migrationBuilder.DropColumn(
                name: "CurrentOccurrenceClassification",
                table: "Leads");

            migrationBuilder.DropColumn(
                name: "CurrentRevisionId",
                table: "Leads");

            migrationBuilder.DropColumn(
                name: "CurrentRevisionNumber",
                table: "Leads");

            migrationBuilder.DropColumn(
                name: "IdentityVersion",
                table: "Leads");

            migrationBuilder.DropColumn(
                name: "IngestedAtUtc",
                table: "Leads");

            migrationBuilder.DropColumn(
                name: "SourceReceivedAtUtc",
                table: "Leads");
        }
    }
}
