using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace ERP_RFQ_Automation.Migrations
{
    /// <inheritdoc />
    public partial class AddEvidenceRoutingCustomFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "custom_field_definitions",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    BusinessUnitId = table.Column<long>(type: "bigint", nullable: false),
                    EntityType = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    StableKey = table.Column<string>(type: "character varying(63)", maxLength: 63, nullable: false),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    ActiveVersionNumber = table.Column<int>(type: "integer", nullable: true),
                    CreatedOn = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    CreatedBy = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    RetiredOn = table.Column<DateTime>(type: "timestamp without time zone", nullable: true),
                    RetiredBy = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    RetirementReason = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_custom_field_definitions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "custom_field_records",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    BusinessUnitId = table.Column<long>(type: "bigint", nullable: false),
                    EntityType = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    EntityId = table.Column<long>(type: "bigint", nullable: false),
                    CreatedOn = table.Column<DateTime>(type: "timestamp without time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_custom_field_records", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "customer_identifiers",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    BusinessUnitId = table.Column<long>(type: "bigint", nullable: false),
                    CustomerId = table.Column<long>(type: "bigint", nullable: false),
                    IdentifierType = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    NormalizedValue = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false),
                    DisplayValue = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false),
                    IsVerified = table.Column<bool>(type: "boolean", nullable: false),
                    Confidence = table.Column<decimal>(type: "numeric(5,4)", precision: 5, scale: 4, nullable: false),
                    Source = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    EffectiveFrom = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    EffectiveTo = table.Column<DateTime>(type: "timestamp without time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_customer_identifiers", x => x.Id);
                    table.ForeignKey(
                        name: "FK_customer_identifiers_Customers_CustomerId",
                        column: x => x.CustomerId,
                        principalTable: "Customers",
                        principalColumn: "ID",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "customer_ownerships",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    BusinessUnitId = table.Column<long>(type: "bigint", nullable: false),
                    CustomerId = table.Column<long>(type: "bigint", nullable: false),
                    PrimaryUserId = table.Column<long>(type: "bigint", nullable: false),
                    BackupUserId = table.Column<long>(type: "bigint", nullable: true),
                    Scope = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    ScopeKey = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: true),
                    Priority = table.Column<int>(type: "integer", nullable: false),
                    EffectiveFrom = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    EffectiveTo = table.Column<DateTime>(type: "timestamp without time zone", nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    Source = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    Reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    Version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_customer_ownerships", x => x.Id);
                    table.ForeignKey(
                        name: "FK_customer_ownerships_Customers_CustomerId",
                        column: x => x.CustomerId,
                        principalTable: "Customers",
                        principalColumn: "ID",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_customer_ownerships_Users_BackupUserId",
                        column: x => x.BackupUserId,
                        principalTable: "Users",
                        principalColumn: "ID",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_customer_ownerships_Users_PrimaryUserId",
                        column: x => x.PrimaryUserId,
                        principalTable: "Users",
                        principalColumn: "ID",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "document_corpora",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    business_unit_id = table.Column<long>(type: "bigint", nullable: false),
                    batch_id = table.Column<Guid>(type: "uuid", nullable: false),
                    source_type = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    created_on = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_on = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_document_corpora", x => x.id);
                    table.CheckConstraint("ck_document_corpora_business_unit", "business_unit_id > 0");
                });

            migrationBuilder.CreateTable(
                name: "custom_field_versions",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    DefinitionId = table.Column<long>(type: "bigint", nullable: false),
                    VersionNumber = table.Column<int>(type: "integer", nullable: false),
                    Label = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    HelpText = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    DataType = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    IsRequired = table.Column<bool>(type: "boolean", nullable: false),
                    MinimumLength = table.Column<int>(type: "integer", nullable: true),
                    MaximumLength = table.Column<int>(type: "integer", nullable: true),
                    MinimumValue = table.Column<decimal>(type: "numeric(28,8)", precision: 28, scale: 8, nullable: true),
                    MaximumValue = table.Column<decimal>(type: "numeric(28,8)", precision: 28, scale: 8, nullable: true),
                    DefaultValueJson = table.Column<string>(type: "character varying(8000)", maxLength: 8000, nullable: true),
                    IsSensitive = table.Column<bool>(type: "boolean", nullable: false),
                    IsSearchable = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedOn = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    CreatedBy = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_custom_field_versions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_custom_field_versions_custom_field_definitions_DefinitionId",
                        column: x => x.DefinitionId,
                        principalTable: "custom_field_definitions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "custom_field_values",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    BusinessUnitId = table.Column<long>(type: "bigint", nullable: false),
                    RecordId = table.Column<long>(type: "bigint", nullable: false),
                    DefinitionId = table.Column<long>(type: "bigint", nullable: false),
                    DefinitionVersion = table.Column<int>(type: "integer", nullable: false),
                    TextValue = table.Column<string>(type: "text", nullable: true),
                    IntegerValue = table.Column<long>(type: "bigint", nullable: true),
                    DecimalValue = table.Column<decimal>(type: "numeric(28,8)", precision: 28, scale: 8, nullable: true),
                    BooleanValue = table.Column<bool>(type: "boolean", nullable: true),
                    DateValue = table.Column<DateOnly>(type: "date", nullable: true),
                    TimestampValue = table.Column<DateTime>(type: "timestamp without time zone", nullable: true),
                    JsonValue = table.Column<string>(type: "text", nullable: true),
                    ReferenceType = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: true),
                    ReferenceId = table.Column<long>(type: "bigint", nullable: true),
                    UpdatedOn = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    UpdatedBy = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_custom_field_values", x => x.Id);
                    table.ForeignKey(
                        name: "FK_custom_field_values_custom_field_definitions_DefinitionId",
                        column: x => x.DefinitionId,
                        principalTable: "custom_field_definitions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_custom_field_values_custom_field_records_RecordId",
                        column: x => x.RecordId,
                        principalTable: "custom_field_records",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "lead_routing_decisions",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    BusinessUnitId = table.Column<long>(type: "bigint", nullable: false),
                    LeadId = table.Column<long>(type: "bigint", nullable: false),
                    CustomerId = table.Column<long>(type: "bigint", nullable: true),
                    MatchedIdentifierId = table.Column<long>(type: "bigint", nullable: true),
                    OwnershipId = table.Column<long>(type: "bigint", nullable: true),
                    SuggestedUserId = table.Column<long>(type: "bigint", nullable: true),
                    SelectedUserId = table.Column<long>(type: "bigint", nullable: true),
                    MatchStatus = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Outcome = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    MatchConfidence = table.Column<decimal>(type: "numeric(5,4)", precision: 5, scale: 4, nullable: false),
                    DecisionCode = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    Explanation = table.Column<string>(type: "jsonb", nullable: false),
                    PolicyVersion = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    CorrelationId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    IdempotencyKey = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    CreatedOn = table.Column<DateTime>(type: "timestamp without time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_lead_routing_decisions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_lead_routing_decisions_Customers_CustomerId",
                        column: x => x.CustomerId,
                        principalTable: "Customers",
                        principalColumn: "ID",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_lead_routing_decisions_Leads_LeadId",
                        column: x => x.LeadId,
                        principalTable: "Leads",
                        principalColumn: "ID",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_lead_routing_decisions_customer_identifiers_MatchedIdentifi~",
                        column: x => x.MatchedIdentifierId,
                        principalTable: "customer_identifiers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_lead_routing_decisions_customer_ownerships_OwnershipId",
                        column: x => x.OwnershipId,
                        principalTable: "customer_ownerships",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "canonical_inquiries",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    business_unit_id = table.Column<long>(type: "bigint", nullable: false),
                    corpus_id = table.Column<long>(type: "bigint", nullable: false),
                    inquiry_number = table.Column<int>(type: "integer", nullable: false),
                    lead_id = table.Column<long>(type: "bigint", nullable: true),
                    customer_rfq_number = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    buyer_name = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    created_on = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_on = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_canonical_inquiries", x => x.id);
                    table.CheckConstraint("ck_canonical_inquiries_business_unit", "business_unit_id > 0");
                    table.CheckConstraint("ck_canonical_inquiries_number", "inquiry_number > 0");
                    table.ForeignKey(
                        name: "FK_canonical_inquiries_document_corpora_corpus_id",
                        column: x => x.corpus_id,
                        principalTable: "document_corpora",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "source_documents",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    business_unit_id = table.Column<long>(type: "bigint", nullable: false),
                    corpus_id = table.Column<long>(type: "bigint", nullable: false),
                    extraction_job_id = table.Column<long>(type: "bigint", nullable: true),
                    content_hash = table.Column<string>(type: "character(64)", fixedLength: true, maxLength: 64, nullable: false),
                    original_file_name = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    detected_mime_type = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    object_bucket = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    object_key = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: false),
                    object_version = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    byte_size = table.Column<long>(type: "bigint", nullable: false),
                    page_count = table.Column<int>(type: "integer", nullable: false),
                    security_status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    processing_status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    created_on = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_on = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_source_documents", x => x.id);
                    table.CheckConstraint("ck_source_documents_business_unit", "business_unit_id > 0");
                    table.CheckConstraint("ck_source_documents_byte_size", "byte_size >= 0");
                    table.CheckConstraint("ck_source_documents_content_hash", "content_hash ~ '^[0-9a-f]{64}$'");
                    table.CheckConstraint("ck_source_documents_page_count", "page_count >= 0");
                    table.ForeignKey(
                        name: "FK_source_documents_document_corpora_corpus_id",
                        column: x => x.corpus_id,
                        principalTable: "document_corpora",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "custom_field_dependencies",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    VersionId = table.Column<long>(type: "bigint", nullable: false),
                    DependsOnDefinitionId = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_custom_field_dependencies", x => x.Id);
                    table.ForeignKey(
                        name: "FK_custom_field_dependencies_custom_field_definitions_DependsO~",
                        column: x => x.DependsOnDefinitionId,
                        principalTable: "custom_field_definitions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_custom_field_dependencies_custom_field_versions_VersionId",
                        column: x => x.VersionId,
                        principalTable: "custom_field_versions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "custom_field_options",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    VersionId = table.Column<long>(type: "bigint", nullable: false),
                    StableKey = table.Column<string>(type: "character varying(63)", maxLength: 63, nullable: false),
                    Label = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    DisplayOrder = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_custom_field_options", x => x.Id);
                    table.ForeignKey(
                        name: "FK_custom_field_options_custom_field_versions_VersionId",
                        column: x => x.VersionId,
                        principalTable: "custom_field_versions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "custom_field_rules",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    VersionId = table.Column<long>(type: "bigint", nullable: false),
                    Effect = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    ConditionJson = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_custom_field_rules", x => x.Id);
                    table.ForeignKey(
                        name: "FK_custom_field_rules_custom_field_versions_VersionId",
                        column: x => x.VersionId,
                        principalTable: "custom_field_versions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "custom_field_value_history",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    BusinessUnitId = table.Column<long>(type: "bigint", nullable: false),
                    CustomFieldValueId = table.Column<long>(type: "bigint", nullable: false),
                    BeforeJson = table.Column<string>(type: "text", nullable: true),
                    AfterJson = table.Column<string>(type: "text", nullable: true),
                    ChangeType = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    ChangedBy = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    ChangedOn = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    Reason = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    CorrelationId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_custom_field_value_history", x => x.Id);
                    table.ForeignKey(
                        name: "FK_custom_field_value_history_custom_field_values_CustomFieldV~",
                        column: x => x.CustomFieldValueId,
                        principalTable: "custom_field_values",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "lead_assignments",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    BusinessUnitId = table.Column<long>(type: "bigint", nullable: false),
                    LeadId = table.Column<long>(type: "bigint", nullable: false),
                    FromUserId = table.Column<long>(type: "bigint", nullable: true),
                    ToUserId = table.Column<long>(type: "bigint", nullable: false),
                    AssignmentScope = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    OwnershipId = table.Column<long>(type: "bigint", nullable: true),
                    RoutingDecisionId = table.Column<long>(type: "bigint", nullable: false),
                    ReasonCode = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    Comment = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    EffectiveFrom = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    EffectiveTo = table.Column<DateTime>(type: "timestamp without time zone", nullable: true),
                    AssignedByUserId = table.Column<long>(type: "bigint", nullable: true),
                    CorrelationId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    IdempotencyKey = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_lead_assignments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_lead_assignments_Leads_LeadId",
                        column: x => x.LeadId,
                        principalTable: "Leads",
                        principalColumn: "ID",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_lead_assignments_Users_ToUserId",
                        column: x => x.ToUserId,
                        principalTable: "Users",
                        principalColumn: "ID",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_lead_assignments_customer_ownerships_OwnershipId",
                        column: x => x.OwnershipId,
                        principalTable: "customer_ownerships",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_lead_assignments_lead_routing_decisions_RoutingDecisionId",
                        column: x => x.RoutingDecisionId,
                        principalTable: "lead_routing_decisions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "unassigned_work_items",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    BusinessUnitId = table.Column<long>(type: "bigint", nullable: false),
                    LeadId = table.Column<long>(type: "bigint", nullable: false),
                    RoutingDecisionId = table.Column<long>(type: "bigint", nullable: false),
                    QueueType = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    ReasonCode = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    Status = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    Priority = table.Column<int>(type: "integer", nullable: false),
                    EnteredOn = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    SlaDueOn = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    SuggestedCustomerId = table.Column<long>(type: "bigint", nullable: true),
                    SuggestedUserId = table.Column<long>(type: "bigint", nullable: true),
                    MatchConfidence = table.Column<decimal>(type: "numeric(5,4)", precision: 5, scale: 4, nullable: false),
                    RequiredAction = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    ClaimedByUserId = table.Column<long>(type: "bigint", nullable: true),
                    ClaimedUntil = table.Column<DateTime>(type: "timestamp without time zone", nullable: true),
                    ResolvedOn = table.Column<DateTime>(type: "timestamp without time zone", nullable: true),
                    ResolutionCode = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: true),
                    IdempotencyKey = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_unassigned_work_items", x => x.Id);
                    table.ForeignKey(
                        name: "FK_unassigned_work_items_Leads_LeadId",
                        column: x => x.LeadId,
                        principalTable: "Leads",
                        principalColumn: "ID",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_unassigned_work_items_lead_routing_decisions_RoutingDecisio~",
                        column: x => x.RoutingDecisionId,
                        principalTable: "lead_routing_decisions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "canonical_line_items",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    business_unit_id = table.Column<long>(type: "bigint", nullable: false),
                    inquiry_id = table.Column<long>(type: "bigint", nullable: false),
                    line_number = table.Column<int>(type: "integer", nullable: false),
                    description = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false),
                    quantity = table.Column<decimal>(type: "numeric(20,6)", precision: 20, scale: 6, nullable: true),
                    unit_of_measure = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    manufacturer = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    manufacturer_part_number = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    currency_code = table.Column<string>(type: "character(3)", fixedLength: true, maxLength: 3, nullable: true),
                    raw_payload = table.Column<string>(type: "jsonb", nullable: true),
                    created_on = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_on = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_canonical_line_items", x => x.id);
                    table.CheckConstraint("ck_canonical_line_items_business_unit", "business_unit_id > 0");
                    table.CheckConstraint("ck_canonical_line_items_number", "line_number > 0");
                    table.CheckConstraint("ck_canonical_line_items_quantity", "quantity IS NULL OR quantity > 0");
                    table.ForeignKey(
                        name: "FK_canonical_line_items_canonical_inquiries_inquiry_id",
                        column: x => x.inquiry_id,
                        principalTable: "canonical_inquiries",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "document_pages",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    business_unit_id = table.Column<long>(type: "bigint", nullable: false),
                    document_id = table.Column<long>(type: "bigint", nullable: false),
                    page_number = table.Column<int>(type: "integer", nullable: false),
                    width = table.Column<decimal>(type: "numeric(12,4)", precision: 12, scale: 4, nullable: false),
                    height = table.Column<decimal>(type: "numeric(12,4)", precision: 12, scale: 4, nullable: false),
                    rotation = table.Column<int>(type: "integer", nullable: false),
                    text_hash = table.Column<string>(type: "character(64)", fixedLength: true, maxLength: 64, nullable: true),
                    ocr_status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    ocr_confidence = table.Column<decimal>(type: "numeric(6,5)", precision: 6, scale: 5, nullable: true),
                    created_on = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_on = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_document_pages", x => x.id);
                    table.CheckConstraint("ck_document_pages_business_unit", "business_unit_id > 0");
                    table.CheckConstraint("ck_document_pages_dimensions", "width > 0 AND height > 0");
                    table.CheckConstraint("ck_document_pages_number", "page_number > 0");
                    table.CheckConstraint("ck_document_pages_ocr_confidence", "ocr_confidence IS NULL OR (ocr_confidence >= 0 AND ocr_confidence <= 1)");
                    table.CheckConstraint("ck_document_pages_rotation", "rotation IN (0, 90, 180, 270)");
                    table.CheckConstraint("ck_document_pages_text_hash", "text_hash IS NULL OR text_hash ~ '^[0-9a-f]{64}$'");
                    table.ForeignKey(
                        name: "FK_document_pages_source_documents_document_id",
                        column: x => x.document_id,
                        principalTable: "source_documents",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "document_regions",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    business_unit_id = table.Column<long>(type: "bigint", nullable: false),
                    page_id = table.Column<long>(type: "bigint", nullable: false),
                    region_type = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    x = table.Column<decimal>(type: "numeric(12,4)", precision: 12, scale: 4, nullable: false),
                    y = table.Column<decimal>(type: "numeric(12,4)", precision: 12, scale: 4, nullable: false),
                    width = table.Column<decimal>(type: "numeric(12,4)", precision: 12, scale: 4, nullable: false),
                    height = table.Column<decimal>(type: "numeric(12,4)", precision: 12, scale: 4, nullable: false),
                    text = table.Column<string>(type: "character varying(100000)", maxLength: 100000, nullable: true),
                    confidence = table.Column<decimal>(type: "numeric(6,5)", precision: 6, scale: 5, nullable: false),
                    created_on = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_document_regions", x => x.id);
                    table.CheckConstraint("ck_document_regions_bounds", "x >= 0 AND y >= 0 AND width > 0 AND height > 0");
                    table.CheckConstraint("ck_document_regions_business_unit", "business_unit_id > 0");
                    table.CheckConstraint("ck_document_regions_confidence", "confidence >= 0 AND confidence <= 1");
                    table.ForeignKey(
                        name: "FK_document_regions_document_pages_page_id",
                        column: x => x.page_id,
                        principalTable: "document_pages",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "field_evidence",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    business_unit_id = table.Column<long>(type: "bigint", nullable: false),
                    region_id = table.Column<long>(type: "bigint", nullable: false),
                    inquiry_id = table.Column<long>(type: "bigint", nullable: true),
                    line_item_id = table.Column<long>(type: "bigint", nullable: true),
                    field_name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    raw_value = table.Column<string>(type: "character varying(100000)", maxLength: 100000, nullable: true),
                    normalized_value = table.Column<string>(type: "character varying(100000)", maxLength: 100000, nullable: true),
                    confidence = table.Column<decimal>(type: "numeric(6,5)", precision: 6, scale: 5, nullable: false),
                    extractor = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    run_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_on = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_field_evidence", x => x.id);
                    table.CheckConstraint("ck_field_evidence_business_unit", "business_unit_id > 0");
                    table.CheckConstraint("ck_field_evidence_confidence", "confidence >= 0 AND confidence <= 1");
                    table.CheckConstraint("ck_field_evidence_target", "(inquiry_id IS NOT NULL AND line_item_id IS NULL) OR (inquiry_id IS NULL AND line_item_id IS NOT NULL)");
                    table.ForeignKey(
                        name: "FK_field_evidence_canonical_inquiries_inquiry_id",
                        column: x => x.inquiry_id,
                        principalTable: "canonical_inquiries",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_field_evidence_canonical_line_items_line_item_id",
                        column: x => x.line_item_id,
                        principalTable: "canonical_line_items",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_field_evidence_document_regions_region_id",
                        column: x => x.region_id,
                        principalTable: "document_regions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_canonical_inquiries_lead",
                table: "canonical_inquiries",
                column: "lead_id");

            migrationBuilder.CreateIndex(
                name: "ix_canonical_inquiries_tenant_customer_rfq",
                table: "canonical_inquiries",
                columns: new[] { "business_unit_id", "customer_rfq_number" });

            migrationBuilder.CreateIndex(
                name: "ix_canonical_inquiries_tenant_status",
                table: "canonical_inquiries",
                columns: new[] { "business_unit_id", "status" });

            migrationBuilder.CreateIndex(
                name: "ux_canonical_inquiries_corpus_number",
                table: "canonical_inquiries",
                columns: new[] { "corpus_id", "inquiry_number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_canonical_line_items_tenant_inquiry",
                table: "canonical_line_items",
                columns: new[] { "business_unit_id", "inquiry_id" });

            migrationBuilder.CreateIndex(
                name: "ix_canonical_line_items_tenant_mpn",
                table: "canonical_line_items",
                columns: new[] { "business_unit_id", "manufacturer_part_number" });

            migrationBuilder.CreateIndex(
                name: "ux_canonical_line_items_inquiry_line",
                table: "canonical_line_items",
                columns: new[] { "inquiry_id", "line_number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_custom_field_definitions_BusinessUnitId_EntityType_StableKey",
                table: "custom_field_definitions",
                columns: new[] { "BusinessUnitId", "EntityType", "StableKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_custom_field_dependencies_DependsOnDefinitionId",
                table: "custom_field_dependencies",
                column: "DependsOnDefinitionId");

            migrationBuilder.CreateIndex(
                name: "IX_custom_field_dependencies_VersionId_DependsOnDefinitionId",
                table: "custom_field_dependencies",
                columns: new[] { "VersionId", "DependsOnDefinitionId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_custom_field_options_VersionId_StableKey",
                table: "custom_field_options",
                columns: new[] { "VersionId", "StableKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_custom_field_records_BusinessUnitId_EntityType_EntityId",
                table: "custom_field_records",
                columns: new[] { "BusinessUnitId", "EntityType", "EntityId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_custom_field_rules_VersionId",
                table: "custom_field_rules",
                column: "VersionId");

            migrationBuilder.CreateIndex(
                name: "IX_custom_field_value_history_BusinessUnitId_CustomFieldValueI~",
                table: "custom_field_value_history",
                columns: new[] { "BusinessUnitId", "CustomFieldValueId", "ChangedOn" });

            migrationBuilder.CreateIndex(
                name: "IX_custom_field_value_history_CustomFieldValueId",
                table: "custom_field_value_history",
                column: "CustomFieldValueId");

            migrationBuilder.CreateIndex(
                name: "IX_custom_field_values_BusinessUnitId_DefinitionId_DateValue",
                table: "custom_field_values",
                columns: new[] { "BusinessUnitId", "DefinitionId", "DateValue" });

            migrationBuilder.CreateIndex(
                name: "IX_custom_field_values_BusinessUnitId_DefinitionId_DecimalValue",
                table: "custom_field_values",
                columns: new[] { "BusinessUnitId", "DefinitionId", "DecimalValue" });

            migrationBuilder.CreateIndex(
                name: "IX_custom_field_values_BusinessUnitId_DefinitionId_IntegerValue",
                table: "custom_field_values",
                columns: new[] { "BusinessUnitId", "DefinitionId", "IntegerValue" });

            migrationBuilder.CreateIndex(
                name: "IX_custom_field_values_BusinessUnitId_DefinitionId_TextValue",
                table: "custom_field_values",
                columns: new[] { "BusinessUnitId", "DefinitionId", "TextValue" });

            migrationBuilder.CreateIndex(
                name: "IX_custom_field_values_DefinitionId",
                table: "custom_field_values",
                column: "DefinitionId");

            migrationBuilder.CreateIndex(
                name: "IX_custom_field_values_RecordId_DefinitionId",
                table: "custom_field_values",
                columns: new[] { "RecordId", "DefinitionId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_custom_field_versions_DefinitionId_VersionNumber",
                table: "custom_field_versions",
                columns: new[] { "DefinitionId", "VersionNumber" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_customer_identifiers_BusinessUnitId_CustomerId",
                table: "customer_identifiers",
                columns: new[] { "BusinessUnitId", "CustomerId" });

            migrationBuilder.CreateIndex(
                name: "IX_customer_identifiers_BusinessUnitId_IdentifierType_Normaliz~",
                table: "customer_identifiers",
                columns: new[] { "BusinessUnitId", "IdentifierType", "NormalizedValue" },
                unique: true,
                filter: "\"EffectiveTo\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_customer_identifiers_CustomerId",
                table: "customer_identifiers",
                column: "CustomerId");

            migrationBuilder.CreateIndex(
                name: "IX_customer_ownerships_BackupUserId",
                table: "customer_ownerships",
                column: "BackupUserId");

            migrationBuilder.CreateIndex(
                name: "IX_customer_ownerships_BusinessUnitId_CustomerId_IsActive",
                table: "customer_ownerships",
                columns: new[] { "BusinessUnitId", "CustomerId", "IsActive" });

            migrationBuilder.CreateIndex(
                name: "IX_customer_ownerships_BusinessUnitId_Scope_ScopeKey",
                table: "customer_ownerships",
                columns: new[] { "BusinessUnitId", "Scope", "ScopeKey" });

            migrationBuilder.CreateIndex(
                name: "IX_customer_ownerships_CustomerId",
                table: "customer_ownerships",
                column: "CustomerId");

            migrationBuilder.CreateIndex(
                name: "IX_customer_ownerships_PrimaryUserId",
                table: "customer_ownerships",
                column: "PrimaryUserId");

            migrationBuilder.CreateIndex(
                name: "ix_document_corpora_tenant_created",
                table: "document_corpora",
                columns: new[] { "business_unit_id", "created_on" });

            migrationBuilder.CreateIndex(
                name: "ix_document_corpora_tenant_status",
                table: "document_corpora",
                columns: new[] { "business_unit_id", "status" });

            migrationBuilder.CreateIndex(
                name: "ux_document_corpora_tenant_batch",
                table: "document_corpora",
                columns: new[] { "business_unit_id", "batch_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_document_pages_tenant_document",
                table: "document_pages",
                columns: new[] { "business_unit_id", "document_id" });

            migrationBuilder.CreateIndex(
                name: "ix_document_pages_tenant_ocr_status",
                table: "document_pages",
                columns: new[] { "business_unit_id", "ocr_status" });

            migrationBuilder.CreateIndex(
                name: "ux_document_pages_document_number",
                table: "document_pages",
                columns: new[] { "document_id", "page_number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_document_regions_page_id",
                table: "document_regions",
                column: "page_id");

            migrationBuilder.CreateIndex(
                name: "ix_document_regions_tenant_page",
                table: "document_regions",
                columns: new[] { "business_unit_id", "page_id" });

            migrationBuilder.CreateIndex(
                name: "ix_document_regions_tenant_type",
                table: "document_regions",
                columns: new[] { "business_unit_id", "region_type" });

            migrationBuilder.CreateIndex(
                name: "ix_field_evidence_inquiry_field",
                table: "field_evidence",
                columns: new[] { "business_unit_id", "inquiry_id", "field_name" });

            migrationBuilder.CreateIndex(
                name: "IX_field_evidence_inquiry_id",
                table: "field_evidence",
                column: "inquiry_id");

            migrationBuilder.CreateIndex(
                name: "ix_field_evidence_line_field",
                table: "field_evidence",
                columns: new[] { "business_unit_id", "line_item_id", "field_name" });

            migrationBuilder.CreateIndex(
                name: "IX_field_evidence_line_item_id",
                table: "field_evidence",
                column: "line_item_id");

            migrationBuilder.CreateIndex(
                name: "ix_field_evidence_region",
                table: "field_evidence",
                column: "region_id");

            migrationBuilder.CreateIndex(
                name: "ix_field_evidence_tenant_run",
                table: "field_evidence",
                columns: new[] { "business_unit_id", "run_id" });

            migrationBuilder.CreateIndex(
                name: "IX_lead_assignments_BusinessUnitId_IdempotencyKey",
                table: "lead_assignments",
                columns: new[] { "BusinessUnitId", "IdempotencyKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_lead_assignments_BusinessUnitId_LeadId",
                table: "lead_assignments",
                columns: new[] { "BusinessUnitId", "LeadId" },
                unique: true,
                filter: "\"EffectiveTo\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_lead_assignments_BusinessUnitId_LeadId_EffectiveTo",
                table: "lead_assignments",
                columns: new[] { "BusinessUnitId", "LeadId", "EffectiveTo" });

            migrationBuilder.CreateIndex(
                name: "IX_lead_assignments_LeadId",
                table: "lead_assignments",
                column: "LeadId");

            migrationBuilder.CreateIndex(
                name: "IX_lead_assignments_OwnershipId",
                table: "lead_assignments",
                column: "OwnershipId");

            migrationBuilder.CreateIndex(
                name: "IX_lead_assignments_RoutingDecisionId",
                table: "lead_assignments",
                column: "RoutingDecisionId");

            migrationBuilder.CreateIndex(
                name: "IX_lead_assignments_ToUserId",
                table: "lead_assignments",
                column: "ToUserId");

            migrationBuilder.CreateIndex(
                name: "IX_lead_routing_decisions_BusinessUnitId_IdempotencyKey",
                table: "lead_routing_decisions",
                columns: new[] { "BusinessUnitId", "IdempotencyKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_lead_routing_decisions_BusinessUnitId_LeadId_CreatedOn",
                table: "lead_routing_decisions",
                columns: new[] { "BusinessUnitId", "LeadId", "CreatedOn" });

            migrationBuilder.CreateIndex(
                name: "IX_lead_routing_decisions_CustomerId",
                table: "lead_routing_decisions",
                column: "CustomerId");

            migrationBuilder.CreateIndex(
                name: "IX_lead_routing_decisions_LeadId",
                table: "lead_routing_decisions",
                column: "LeadId");

            migrationBuilder.CreateIndex(
                name: "IX_lead_routing_decisions_MatchedIdentifierId",
                table: "lead_routing_decisions",
                column: "MatchedIdentifierId");

            migrationBuilder.CreateIndex(
                name: "IX_lead_routing_decisions_OwnershipId",
                table: "lead_routing_decisions",
                column: "OwnershipId");

            migrationBuilder.CreateIndex(
                name: "IX_source_documents_corpus_id",
                table: "source_documents",
                column: "corpus_id");

            migrationBuilder.CreateIndex(
                name: "ix_source_documents_extraction_job",
                table: "source_documents",
                column: "extraction_job_id");

            migrationBuilder.CreateIndex(
                name: "ix_source_documents_tenant_corpus",
                table: "source_documents",
                columns: new[] { "business_unit_id", "corpus_id" });

            migrationBuilder.CreateIndex(
                name: "ix_source_documents_tenant_security",
                table: "source_documents",
                columns: new[] { "business_unit_id", "security_status" });

            migrationBuilder.CreateIndex(
                name: "ux_source_documents_object_version",
                table: "source_documents",
                columns: new[] { "business_unit_id", "object_bucket", "object_key", "object_version" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_source_documents_tenant_hash",
                table: "source_documents",
                columns: new[] { "business_unit_id", "content_hash" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_unassigned_work_items_BusinessUnitId_IdempotencyKey",
                table: "unassigned_work_items",
                columns: new[] { "BusinessUnitId", "IdempotencyKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_unassigned_work_items_BusinessUnitId_LeadId",
                table: "unassigned_work_items",
                columns: new[] { "BusinessUnitId", "LeadId" },
                unique: true,
                filter: "\"Status\" IN ('Open', 'Claimed')");

            migrationBuilder.CreateIndex(
                name: "IX_unassigned_work_items_BusinessUnitId_LeadId_Status",
                table: "unassigned_work_items",
                columns: new[] { "BusinessUnitId", "LeadId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_unassigned_work_items_BusinessUnitId_Status_SlaDueOn",
                table: "unassigned_work_items",
                columns: new[] { "BusinessUnitId", "Status", "SlaDueOn" });

            migrationBuilder.CreateIndex(
                name: "IX_unassigned_work_items_LeadId",
                table: "unassigned_work_items",
                column: "LeadId");

            migrationBuilder.CreateIndex(
                name: "IX_unassigned_work_items_RoutingDecisionId",
                table: "unassigned_work_items",
                column: "RoutingDecisionId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "custom_field_dependencies");

            migrationBuilder.DropTable(
                name: "custom_field_options");

            migrationBuilder.DropTable(
                name: "custom_field_rules");

            migrationBuilder.DropTable(
                name: "custom_field_value_history");

            migrationBuilder.DropTable(
                name: "field_evidence");

            migrationBuilder.DropTable(
                name: "lead_assignments");

            migrationBuilder.DropTable(
                name: "unassigned_work_items");

            migrationBuilder.DropTable(
                name: "custom_field_versions");

            migrationBuilder.DropTable(
                name: "custom_field_values");

            migrationBuilder.DropTable(
                name: "canonical_line_items");

            migrationBuilder.DropTable(
                name: "document_regions");

            migrationBuilder.DropTable(
                name: "lead_routing_decisions");

            migrationBuilder.DropTable(
                name: "custom_field_definitions");

            migrationBuilder.DropTable(
                name: "custom_field_records");

            migrationBuilder.DropTable(
                name: "canonical_inquiries");

            migrationBuilder.DropTable(
                name: "document_pages");

            migrationBuilder.DropTable(
                name: "customer_identifiers");

            migrationBuilder.DropTable(
                name: "customer_ownerships");

            migrationBuilder.DropTable(
                name: "source_documents");

            migrationBuilder.DropTable(
                name: "document_corpora");
        }
    }
}
