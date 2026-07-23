using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace ERP_RFQ_Automation.Migrations
{
    /// <inheritdoc />
    public partial class GovernStatementsAndDunning : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("CREATE EXTENSION IF NOT EXISTS pgcrypto;");

            migrationBuilder.CreateTable(
                name: "CollectionControls",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    BusinessUnitId = table.Column<long>(type: "bigint", nullable: false),
                    CustomerId = table.Column<long>(type: "bigint", nullable: false),
                    CurrencyId = table.Column<long>(type: "bigint", nullable: true),
                    ReceivableDocumentId = table.Column<long>(type: "bigint", nullable: true),
                    ControlType = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    DisputedAmount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: true),
                    ReasonCode = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    EvidenceReference = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    EffectiveFrom = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    ReviewOn = table.Column<DateTime>(type: "timestamp without time zone", nullable: true),
                    ExpiresOn = table.Column<DateTime>(type: "timestamp without time zone", nullable: true),
                    Version = table.Column<long>(type: "bigint", nullable: false),
                    CreatedBy = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    CreatedOn = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    ResolvedBy = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    ResolvedOn = table.Column<DateTime>(type: "timestamp without time zone", nullable: true),
                    ResolutionReason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    ResolutionEvidenceReference = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CollectionControls", x => x.Id);
                    table.UniqueConstraint("AK_CollectionControls_BusinessUnitId_Id", x => new { x.BusinessUnitId, x.Id });
                    table.CheckConstraint("CK_CollectionControls_Dates", "\"ReviewOn\" IS NULL OR \"ReviewOn\" >= \"EffectiveFrom\"");
                    table.CheckConstraint("CK_CollectionControls_Dispute", "(\"ControlType\" = 'Dispute' AND \"ReceivableDocumentId\" IS NOT NULL AND \"DisputedAmount\" > 0) OR (\"ControlType\" IN ('CommunicationRestriction','LegalHold') AND \"DisputedAmount\" IS NULL)");
                    table.ForeignKey(
                        name: "FK_CollectionControls_Currency_CurrencyId",
                        column: x => x.CurrencyId,
                        principalTable: "Currency",
                        principalColumn: "ID",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CollectionControls_Customers_CustomerId",
                        column: x => x.CustomerId,
                        principalTable: "Customers",
                        principalColumn: "ID",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CollectionControls_ReceivableDocuments_BusinessUnitId_Recei~",
                        columns: x => new { x.BusinessUnitId, x.ReceivableDocumentId },
                        principalTable: "ReceivableDocuments",
                        principalColumns: new[] { "BusinessUnitId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "CustomerStatements",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    BusinessUnitId = table.Column<long>(type: "bigint", nullable: false),
                    CustomerId = table.Column<long>(type: "bigint", nullable: false),
                    CurrencyId = table.Column<long>(type: "bigint", nullable: true),
                    SupersedesStatementId = table.Column<long>(type: "bigint", nullable: true),
                    StatementNumber = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    PeriodStart = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    CutoffAt = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    CapturedOn = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    Revision = table.Column<int>(type: "integer", nullable: false),
                    OpeningBalance = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    DebitTotal = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    CreditTotal = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    UnappliedCash = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    ClosingBalance = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    NetCustomerPosition = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    AgingCurrent = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    Aging1To30 = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    Aging31To60 = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    Aging61To90 = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    AgingOver90 = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    SourceFingerprint = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    SnapshotHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ArtifactHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ArtifactReference = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    ArtifactMediaType = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    ArtifactContent = table.Column<string>(type: "text", nullable: false),
                    GeneratorVersion = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    TemplateVersion = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    IssuerNameSnapshot = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    CustomerNameSnapshot = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    BillingAddressSnapshot = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    IdempotencyKey = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    RequestHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false),
                    CreatedBy = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    CreatedOn = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    FinalizedBy = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    FinalizedOn = table.Column<DateTime>(type: "timestamp without time zone", nullable: true),
                    CancelledBy = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    CancelledOn = table.Column<DateTime>(type: "timestamp without time zone", nullable: true),
                    CancellationReason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    CorrectionReason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CustomerStatements", x => x.Id);
                    table.UniqueConstraint("AK_CustomerStatements_BusinessUnitId_Id", x => new { x.BusinessUnitId, x.Id });
                    table.CheckConstraint("CK_CustomerStatements_Aging", "\"AgingCurrent\" >= 0 AND \"Aging1To30\" >= 0 AND \"Aging31To60\" >= 0 AND \"Aging61To90\" >= 0 AND \"AgingOver90\" >= 0");
                    table.CheckConstraint("CK_CustomerStatements_Period", "\"PeriodStart\" <= \"CutoffAt\" AND \"CapturedOn\" >= \"CutoffAt\"");
                    table.CheckConstraint("CK_CustomerStatements_Reconciles", "\"ClosingBalance\" = round(\"OpeningBalance\" + \"DebitTotal\" - \"CreditTotal\", 2) AND \"NetCustomerPosition\" = \"ClosingBalance\"");
                    table.ForeignKey(
                        name: "FK_CustomerStatements_Currency_CurrencyId",
                        column: x => x.CurrencyId,
                        principalTable: "Currency",
                        principalColumn: "ID",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CustomerStatements_CustomerStatements_BusinessUnitId_Supers~",
                        columns: x => new { x.BusinessUnitId, x.SupersedesStatementId },
                        principalTable: "CustomerStatements",
                        principalColumns: new[] { "BusinessUnitId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CustomerStatements_Customers_CustomerId",
                        column: x => x.CustomerId,
                        principalTable: "Customers",
                        principalColumn: "ID",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "DunningPolicies",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    BusinessUnitId = table.Column<long>(type: "bigint", nullable: false),
                    PolicyVersion = table.Column<int>(type: "integer", nullable: false),
                    Name = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    JurisdictionCode = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    TimeZoneId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    GraceDays = table.Column<int>(type: "integer", nullable: false),
                    CadenceDays = table.Column<int>(type: "integer", nullable: false),
                    MaximumStage = table.Column<int>(type: "integer", nullable: false),
                    MinimumOverdueAmount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    QuietHoursStart = table.Column<int>(type: "integer", nullable: false),
                    QuietHoursEnd = table.Column<int>(type: "integer", nullable: false),
                    TemplateVersion = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    IdempotencyKey = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    RequestHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false),
                    CreatedBy = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    CreatedOn = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    ApprovedBy = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    ApprovedOn = table.Column<DateTime>(type: "timestamp without time zone", nullable: true),
                    ActivatedBy = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    ActivatedOn = table.Column<DateTime>(type: "timestamp without time zone", nullable: true),
                    RetiredBy = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    RetiredOn = table.Column<DateTime>(type: "timestamp without time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DunningPolicies", x => x.Id);
                    table.UniqueConstraint("AK_DunningPolicies_BusinessUnitId_Id", x => new { x.BusinessUnitId, x.Id });
                    table.CheckConstraint("CK_DunningPolicies_Rules", "\"PolicyVersion\" > 0 AND \"GraceDays\" >= 0 AND \"CadenceDays\" > 0 AND \"MaximumStage\" BETWEEN 1 AND 9 AND \"MinimumOverdueAmount\" >= 0 AND \"QuietHoursStart\" BETWEEN 0 AND 23 AND \"QuietHoursEnd\" BETWEEN 0 AND 23");
                });

            migrationBuilder.CreateTable(
                name: "FinanceCommunicationContacts",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    BusinessUnitId = table.Column<long>(type: "bigint", nullable: false),
                    CustomerId = table.Column<long>(type: "bigint", nullable: false),
                    Purpose = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    Channel = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    DestinationToken = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    MaskedDestination = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    IsVerified = table.Column<bool>(type: "boolean", nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    EffectiveFrom = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    EffectiveTo = table.Column<DateTime>(type: "timestamp without time zone", nullable: true),
                    VerificationEvidenceReference = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    VerificationProviderEventId = table.Column<Guid>(type: "uuid", nullable: false),
                    IdempotencyKey = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    RequestHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false),
                    CreatedBy = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    CreatedOn = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    DeactivatedBy = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    DeactivatedOn = table.Column<DateTime>(type: "timestamp without time zone", nullable: true),
                    DeactivationReason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FinanceCommunicationContacts", x => x.Id);
                    table.UniqueConstraint("AK_FinanceCommunicationContacts_BusinessUnitId_Id", x => new { x.BusinessUnitId, x.Id });
                    table.CheckConstraint("CK_FinanceCommunicationContacts_Effective", "\"EffectiveTo\" IS NULL OR \"EffectiveTo\" > \"EffectiveFrom\"");
                    table.ForeignKey(
                        name: "FK_FinanceCommunicationContacts_Customers_CustomerId",
                        column: x => x.CustomerId,
                        principalTable: "Customers",
                        principalColumn: "ID",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "CustomerStatementLines",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    BusinessUnitId = table.Column<long>(type: "bigint", nullable: false),
                    CustomerStatementId = table.Column<long>(type: "bigint", nullable: false),
                    Sequence = table.Column<int>(type: "integer", nullable: false),
                    SourceType = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    SourceId = table.Column<long>(type: "bigint", nullable: false),
                    SourceVersion = table.Column<long>(type: "bigint", nullable: false),
                    SourceNumber = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    CommercialCaseId = table.Column<long>(type: "bigint", nullable: true),
                    ActivityDate = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    DueDate = table.Column<DateTime>(type: "timestamp without time zone", nullable: true),
                    Description = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    DebitAmount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    CreditAmount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    AppliedAmount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    OutstandingAmount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    AgingBucket = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    RunningBalance = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CustomerStatementLines", x => x.Id);
                    table.CheckConstraint("CK_CustomerStatementLines_Money", "\"Sequence\" > 0 AND \"DebitAmount\" >= 0 AND \"CreditAmount\" >= 0 AND NOT (\"DebitAmount\" > 0 AND \"CreditAmount\" > 0) AND \"AppliedAmount\" >= 0 AND \"OutstandingAmount\" >= 0");
                    table.ForeignKey(
                        name: "FK_CustomerStatementLines_CustomerStatements_BusinessUnitId_Cu~",
                        columns: x => new { x.BusinessUnitId, x.CustomerStatementId },
                        principalTable: "CustomerStatements",
                        principalColumns: new[] { "BusinessUnitId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "DunningCases",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    BusinessUnitId = table.Column<long>(type: "bigint", nullable: false),
                    CustomerId = table.Column<long>(type: "bigint", nullable: false),
                    CurrencyId = table.Column<long>(type: "bigint", nullable: true),
                    DunningPolicyId = table.Column<long>(type: "bigint", nullable: false),
                    CustomerStatementId = table.Column<long>(type: "bigint", nullable: false),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    CurrentStage = table.Column<int>(type: "integer", nullable: false),
                    ExposureAtOpen = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    CurrentExposure = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    OldestDueDate = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    NextActionOn = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    AssignedTo = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    PromiseAmount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: true),
                    PromiseDueOn = table.Column<DateTime>(type: "timestamp without time zone", nullable: true),
                    IdempotencyKey = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    RequestHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false),
                    CreatedBy = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    CreatedOn = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    UpdatedBy = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    UpdatedOn = table.Column<DateTime>(type: "timestamp without time zone", nullable: true),
                    StatusReason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    EvidenceReference = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DunningCases", x => x.Id);
                    table.UniqueConstraint("AK_DunningCases_BusinessUnitId_Id", x => new { x.BusinessUnitId, x.Id });
                    table.CheckConstraint("CK_DunningCases_Exposure", "\"CurrentStage\" >= 0 AND \"ExposureAtOpen\" > 0 AND \"CurrentExposure\" >= 0");
                    table.ForeignKey(
                        name: "FK_DunningCases_Currency_CurrencyId",
                        column: x => x.CurrencyId,
                        principalTable: "Currency",
                        principalColumn: "ID",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_DunningCases_CustomerStatements_BusinessUnitId_CustomerStat~",
                        columns: x => new { x.BusinessUnitId, x.CustomerStatementId },
                        principalTable: "CustomerStatements",
                        principalColumns: new[] { "BusinessUnitId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_DunningCases_Customers_CustomerId",
                        column: x => x.CustomerId,
                        principalTable: "Customers",
                        principalColumn: "ID",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_DunningCases_DunningPolicies_BusinessUnitId_DunningPolicyId",
                        columns: x => new { x.BusinessUnitId, x.DunningPolicyId },
                        principalTable: "DunningPolicies",
                        principalColumns: new[] { "BusinessUnitId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "DunningPolicySteps",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    BusinessUnitId = table.Column<long>(type: "bigint", nullable: false),
                    DunningPolicyId = table.Column<long>(type: "bigint", nullable: false),
                    Stage = table.Column<int>(type: "integer", nullable: false),
                    MinimumDaysPastDue = table.Column<int>(type: "integer", nullable: false),
                    MinimumAmount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    WaitDaysAfterPriorStage = table.Column<int>(type: "integer", nullable: false),
                    Channel = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    TemplateVersion = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    RequiresApproval = table.Column<bool>(type: "boolean", nullable: false),
                    EscalationRole = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    MaximumAttempts = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DunningPolicySteps", x => x.Id);
                    table.CheckConstraint("CK_DunningPolicySteps_Rules", "\"Stage\" > 0 AND \"MinimumDaysPastDue\" >= 0 AND \"MinimumAmount\" >= 0 AND \"WaitDaysAfterPriorStage\" >= 0 AND \"MaximumAttempts\" BETWEEN 1 AND 20");
                    table.ForeignKey(
                        name: "FK_DunningPolicySteps_DunningPolicies_BusinessUnitId_DunningPo~",
                        columns: x => new { x.BusinessUnitId, x.DunningPolicyId },
                        principalTable: "DunningPolicies",
                        principalColumns: new[] { "BusinessUnitId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "DunningRuns",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    BusinessUnitId = table.Column<long>(type: "bigint", nullable: false),
                    DunningPolicyId = table.Column<long>(type: "bigint", nullable: false),
                    CutoffAt = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    CandidateCount = table.Column<int>(type: "integer", nullable: false),
                    NoticeCount = table.Column<int>(type: "integer", nullable: false),
                    SuppressedCount = table.Column<int>(type: "integer", nullable: false),
                    FailedCount = table.Column<int>(type: "integer", nullable: false),
                    IdempotencyKey = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    RequestHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    LeaseOwner = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    LeaseToken = table.Column<Guid>(type: "uuid", nullable: true),
                    LeaseUntil = table.Column<DateTime>(type: "timestamp without time zone", nullable: true),
                    Version = table.Column<long>(type: "bigint", nullable: false),
                    CreatedBy = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    CreatedOn = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    CompletedOn = table.Column<DateTime>(type: "timestamp without time zone", nullable: true),
                    CompletionEvidenceReference = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    FailureReason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    FailureEvidenceReference = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DunningRuns", x => x.Id);
                    table.UniqueConstraint("AK_DunningRuns_BusinessUnitId_Id", x => new { x.BusinessUnitId, x.Id });
                    table.CheckConstraint("CK_DunningRuns_Counts", "\"CandidateCount\" >= 0 AND \"NoticeCount\" >= 0 AND \"SuppressedCount\" >= 0 AND \"FailedCount\" >= 0 AND ((\"LeaseOwner\" IS NULL) = (\"LeaseToken\" IS NULL)) AND ((\"LeaseOwner\" IS NULL) = (\"LeaseUntil\" IS NULL))");
                    table.ForeignKey(
                        name: "FK_DunningRuns_DunningPolicies_BusinessUnitId_DunningPolicyId",
                        columns: x => new { x.BusinessUnitId, x.DunningPolicyId },
                        principalTable: "DunningPolicies",
                        principalColumns: new[] { "BusinessUnitId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "CustomerCollectionProfiles",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    BusinessUnitId = table.Column<long>(type: "bigint", nullable: false),
                    CustomerId = table.Column<long>(type: "bigint", nullable: false),
                    CurrencyId = table.Column<long>(type: "bigint", nullable: true),
                    DunningPolicyId = table.Column<long>(type: "bigint", nullable: false),
                    FinanceCommunicationContactId = table.Column<long>(type: "bigint", nullable: true),
                    Locale = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    TimeZoneId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Collector = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    AutomaticDeliveryAllowed = table.Column<bool>(type: "boolean", nullable: false),
                    IsOnHold = table.Column<bool>(type: "boolean", nullable: false),
                    HoldReason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    HoldEvidenceReference = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    Version = table.Column<long>(type: "bigint", nullable: false),
                    CreatedBy = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    CreatedOn = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    ModifiedBy = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    ModifiedOn = table.Column<DateTime>(type: "timestamp without time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CustomerCollectionProfiles", x => x.Id);
                    table.UniqueConstraint("AK_CustomerCollectionProfiles_BusinessUnitId_Id", x => new { x.BusinessUnitId, x.Id });
                    table.ForeignKey(
                        name: "FK_CustomerCollectionProfiles_Currency_CurrencyId",
                        column: x => x.CurrencyId,
                        principalTable: "Currency",
                        principalColumn: "ID",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CustomerCollectionProfiles_Customers_CustomerId",
                        column: x => x.CustomerId,
                        principalTable: "Customers",
                        principalColumn: "ID",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CustomerCollectionProfiles_DunningPolicies_BusinessUnitId_D~",
                        columns: x => new { x.BusinessUnitId, x.DunningPolicyId },
                        principalTable: "DunningPolicies",
                        principalColumns: new[] { "BusinessUnitId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CustomerCollectionProfiles_FinanceCommunicationContacts_Bus~",
                        columns: x => new { x.BusinessUnitId, x.FinanceCommunicationContactId },
                        principalTable: "FinanceCommunicationContacts",
                        principalColumns: new[] { "BusinessUnitId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "DunningNotices",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    BusinessUnitId = table.Column<long>(type: "bigint", nullable: false),
                    DunningCaseId = table.Column<long>(type: "bigint", nullable: false),
                    CustomerStatementId = table.Column<long>(type: "bigint", nullable: false),
                    FinanceCommunicationContactId = table.Column<long>(type: "bigint", nullable: false),
                    Stage = table.Column<int>(type: "integer", nullable: false),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    SnapshotExposure = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    SnapshotHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    TemplateVersion = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    Locale = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Subject = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    ArtifactMediaType = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    ArtifactContent = table.Column<string>(type: "text", nullable: false),
                    ArtifactHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    IdempotencyKey = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    RequestHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false),
                    CreatedBy = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    CreatedOn = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    ApprovedBy = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    ApprovedOn = table.Column<DateTime>(type: "timestamp without time zone", nullable: true),
                    ReleasedBy = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    ReleasedOn = table.Column<DateTime>(type: "timestamp without time zone", nullable: true),
                    DeliveryUpdatedBy = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    DeliveryUpdatedOn = table.Column<DateTime>(type: "timestamp without time zone", nullable: true),
                    ProviderReference = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    FailureCode = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    SuppressionReason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    CancelledBy = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    CancelledOn = table.Column<DateTime>(type: "timestamp without time zone", nullable: true),
                    CancellationReason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    CancellationEvidenceReference = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DunningNotices", x => x.Id);
                    table.UniqueConstraint("AK_DunningNotices_BusinessUnitId_Id", x => new { x.BusinessUnitId, x.Id });
                    table.CheckConstraint("CK_DunningNotices_Rules", "\"Stage\" > 0 AND \"SnapshotExposure\" > 0");
                    table.ForeignKey(
                        name: "FK_DunningNotices_CustomerStatements_BusinessUnitId_CustomerSt~",
                        columns: x => new { x.BusinessUnitId, x.CustomerStatementId },
                        principalTable: "CustomerStatements",
                        principalColumns: new[] { "BusinessUnitId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_DunningNotices_DunningCases_BusinessUnitId_DunningCaseId",
                        columns: x => new { x.BusinessUnitId, x.DunningCaseId },
                        principalTable: "DunningCases",
                        principalColumns: new[] { "BusinessUnitId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_DunningNotices_FinanceCommunicationContacts_BusinessUnitId_~",
                        columns: x => new { x.BusinessUnitId, x.FinanceCommunicationContactId },
                        principalTable: "FinanceCommunicationContacts",
                        principalColumns: new[] { "BusinessUnitId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "PromisesToPay",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    BusinessUnitId = table.Column<long>(type: "bigint", nullable: false),
                    DunningCaseId = table.Column<long>(type: "bigint", nullable: false),
                    Amount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    PromisedOn = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    DueOn = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    EvidenceReference = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false),
                    CreatedBy = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    CreatedOn = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    ClosedBy = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    ClosedOn = table.Column<DateTime>(type: "timestamp without time zone", nullable: true),
                    ClosureEvidenceReference = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    MatchedPaymentId = table.Column<long>(type: "bigint", nullable: true),
                    MatchedAmount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PromisesToPay", x => x.Id);
                    table.CheckConstraint("CK_PromisesToPay_Rules", "\"Amount\" > 0 AND \"DueOn\" >= \"PromisedOn\" AND ((\"Status\" = 'Kept' AND \"MatchedPaymentId\" IS NOT NULL AND \"MatchedAmount\" >= \"Amount\") OR (\"Status\" <> 'Kept' AND \"MatchedPaymentId\" IS NULL AND \"MatchedAmount\" IS NULL))");
                    table.ForeignKey(
                        name: "FK_PromisesToPay_DunningCases_BusinessUnitId_DunningCaseId",
                        columns: x => new { x.BusinessUnitId, x.DunningCaseId },
                        principalTable: "DunningCases",
                        principalColumns: new[] { "BusinessUnitId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PromisesToPay_CustomerPayments_BusinessUnitId_MatchedPaymentId",
                        columns: x => new { x.BusinessUnitId, x.MatchedPaymentId },
                        principalTable: "CustomerPayments",
                        principalColumns: new[] { "BusinessUnitId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "DunningDeliveryAttempts",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    BusinessUnitId = table.Column<long>(type: "bigint", nullable: false),
                    DunningNoticeId = table.Column<long>(type: "bigint", nullable: false),
                    ProviderEventId = table.Column<Guid>(type: "uuid", nullable: false),
                    AttemptNumber = table.Column<int>(type: "integer", nullable: false),
                    Status = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    MaskedDestination = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    ArtifactHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    TemplateVersion = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    ProviderReference = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    FailureCode = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    ProviderOccurredOn = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    SignedEvidenceReference = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    OccurredOn = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    RecordedBy = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DunningDeliveryAttempts", x => x.Id);
                    table.CheckConstraint("CK_DunningDeliveryAttempts_Number", "\"AttemptNumber\" > 0");
                    table.ForeignKey(
                        name: "FK_DunningDeliveryAttempts_DunningNotices_BusinessUnitId_Dunni~",
                        columns: x => new { x.BusinessUnitId, x.DunningNoticeId },
                        principalTable: "DunningNotices",
                        principalColumns: new[] { "BusinessUnitId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "DunningRunDecisions",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    BusinessUnitId = table.Column<long>(type: "bigint", nullable: false),
                    DunningRunId = table.Column<long>(type: "bigint", nullable: false),
                    CustomerId = table.Column<long>(type: "bigint", nullable: false),
                    CurrencyId = table.Column<long>(type: "bigint", nullable: true),
                    CustomerStatementId = table.Column<long>(type: "bigint", nullable: true),
                    DunningCaseId = table.Column<long>(type: "bigint", nullable: true),
                    DunningNoticeId = table.Column<long>(type: "bigint", nullable: true),
                    Outcome = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    ReasonCode = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    EvidenceHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    CreatedOn = table.Column<DateTime>(type: "timestamp without time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DunningRunDecisions", x => x.Id);
                    table.CheckConstraint("CK_DunningRunDecisions_Evidence", "\"Outcome\" IN ('NoticeCreated','Suppressed','Skipped','Failed') AND length(\"EvidenceHash\") = 64");
                    table.ForeignKey(
                        name: "FK_DunningRunDecisions_Currency_CurrencyId",
                        column: x => x.CurrencyId,
                        principalTable: "Currency",
                        principalColumn: "ID",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_DunningRunDecisions_CustomerStatements_BusinessUnitId_CustomerStatementId",
                        columns: x => new { x.BusinessUnitId, x.CustomerStatementId },
                        principalTable: "CustomerStatements",
                        principalColumns: new[] { "BusinessUnitId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_DunningRunDecisions_Customers_CustomerId",
                        column: x => x.CustomerId,
                        principalTable: "Customers",
                        principalColumn: "ID",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_DunningRunDecisions_DunningCases_BusinessUnitId_DunningCaseId",
                        columns: x => new { x.BusinessUnitId, x.DunningCaseId },
                        principalTable: "DunningCases",
                        principalColumns: new[] { "BusinessUnitId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_DunningRunDecisions_DunningNotices_BusinessUnitId_DunningNoticeId",
                        columns: x => new { x.BusinessUnitId, x.DunningNoticeId },
                        principalTable: "DunningNotices",
                        principalColumns: new[] { "BusinessUnitId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_DunningRunDecisions_DunningRuns_BusinessUnitId_DunningRunId",
                        columns: x => new { x.BusinessUnitId, x.DunningRunId },
                        principalTable: "DunningRuns",
                        principalColumns: new[] { "BusinessUnitId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CollectionControls_BU_Customer_Status_Type",
                table: "CollectionControls",
                columns: new[] { "BusinessUnitId", "CustomerId", "Status", "ControlType" });

            migrationBuilder.CreateIndex(
                name: "IX_CollectionControls_BusinessUnitId_ReceivableDocumentId",
                table: "CollectionControls",
                columns: new[] { "BusinessUnitId", "ReceivableDocumentId" });

            migrationBuilder.CreateIndex(
                name: "IX_CollectionControls_CurrencyId",
                table: "CollectionControls",
                column: "CurrencyId");

            migrationBuilder.CreateIndex(
                name: "IX_CollectionControls_CustomerId",
                table: "CollectionControls",
                column: "CustomerId");

            migrationBuilder.CreateIndex(
                name: "IX_CustomerCollectionProfiles_BusinessUnitId_DunningPolicyId",
                table: "CustomerCollectionProfiles",
                columns: new[] { "BusinessUnitId", "DunningPolicyId" });

            migrationBuilder.CreateIndex(
                name: "IX_CustomerCollectionProfiles_BusinessUnitId_FinanceCommunicat~",
                table: "CustomerCollectionProfiles",
                columns: new[] { "BusinessUnitId", "FinanceCommunicationContactId" });

            migrationBuilder.CreateIndex(
                name: "IX_CustomerCollectionProfiles_CurrencyId",
                table: "CustomerCollectionProfiles",
                column: "CurrencyId");

            migrationBuilder.CreateIndex(
                name: "IX_CustomerCollectionProfiles_CustomerId",
                table: "CustomerCollectionProfiles",
                column: "CustomerId");

            migrationBuilder.CreateIndex(
                name: "UX_CustomerCollectionProfiles_BU_Customer_Currency",
                table: "CustomerCollectionProfiles",
                columns: new[] { "BusinessUnitId", "CustomerId", "CurrencyId" },
                unique: true)
                .Annotation("Npgsql:NullsDistinct", false);

            migrationBuilder.CreateIndex(
                name: "UX_CustomerStatementLines_BU_Statement_Sequence",
                table: "CustomerStatementLines",
                columns: new[] { "BusinessUnitId", "CustomerStatementId", "Sequence" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CustomerStatements_CurrencyId",
                table: "CustomerStatements",
                column: "CurrencyId");

            migrationBuilder.CreateIndex(
                name: "IX_CustomerStatements_CustomerId",
                table: "CustomerStatements",
                column: "CustomerId");

            migrationBuilder.CreateIndex(
                name: "UX_CustomerStatements_BU_Customer_Currency_Cutoff_Revision",
                table: "CustomerStatements",
                columns: new[] { "BusinessUnitId", "CustomerId", "CurrencyId", "CutoffAt", "Revision" },
                unique: true,
                filter: "\"Status\" <> 'Cancelled'")
                .Annotation("Npgsql:NullsDistinct", false);

            migrationBuilder.CreateIndex(
                name: "UX_CustomerStatements_BU_Idempotency",
                table: "CustomerStatements",
                columns: new[] { "BusinessUnitId", "IdempotencyKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UX_CustomerStatements_BU_Number",
                table: "CustomerStatements",
                columns: new[] { "BusinessUnitId", "StatementNumber" },
                unique: true,
                filter: "\"StatementNumber\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "UX_CustomerStatements_BU_Successor",
                table: "CustomerStatements",
                columns: new[] { "BusinessUnitId", "SupersedesStatementId" },
                unique: true,
                filter: "\"SupersedesStatementId\" IS NOT NULL AND \"Status\" <> 'Cancelled'");

            migrationBuilder.CreateIndex(
                name: "IX_DunningCases_BusinessUnitId_CustomerStatementId",
                table: "DunningCases",
                columns: new[] { "BusinessUnitId", "CustomerStatementId" });

            migrationBuilder.CreateIndex(
                name: "IX_DunningCases_BusinessUnitId_DunningPolicyId",
                table: "DunningCases",
                columns: new[] { "BusinessUnitId", "DunningPolicyId" });

            migrationBuilder.CreateIndex(
                name: "IX_DunningCases_CurrencyId",
                table: "DunningCases",
                column: "CurrencyId");

            migrationBuilder.CreateIndex(
                name: "IX_DunningCases_CustomerId",
                table: "DunningCases",
                column: "CustomerId");

            migrationBuilder.CreateIndex(
                name: "UX_DunningCases_BU_ActiveCustomerCurrency",
                table: "DunningCases",
                columns: new[] { "BusinessUnitId", "CustomerId", "CurrencyId" },
                unique: true,
                filter: "\"Status\" IN ('Open','Held','Disputed')")
                .Annotation("Npgsql:NullsDistinct", false);

            migrationBuilder.CreateIndex(
                name: "UX_DunningCases_BU_Idempotency",
                table: "DunningCases",
                columns: new[] { "BusinessUnitId", "IdempotencyKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UX_DunningDeliveryAttempts_BU_Notice_Attempt",
                table: "DunningDeliveryAttempts",
                columns: new[] { "BusinessUnitId", "DunningNoticeId", "AttemptNumber" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UX_DunningDeliveryAttempts_BU_ProviderEvent",
                table: "DunningDeliveryAttempts",
                columns: new[] { "BusinessUnitId", "ProviderEventId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DunningNotices_BusinessUnitId_CustomerStatementId",
                table: "DunningNotices",
                columns: new[] { "BusinessUnitId", "CustomerStatementId" });

            migrationBuilder.CreateIndex(
                name: "IX_DunningNotices_BusinessUnitId_FinanceCommunicationContactId",
                table: "DunningNotices",
                columns: new[] { "BusinessUnitId", "FinanceCommunicationContactId" });

            migrationBuilder.CreateIndex(
                name: "UX_DunningNotices_BU_Case_Stage_Hash",
                table: "DunningNotices",
                columns: new[] { "BusinessUnitId", "DunningCaseId", "Stage", "SnapshotHash" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UX_DunningNotices_BU_Idempotency",
                table: "DunningNotices",
                columns: new[] { "BusinessUnitId", "IdempotencyKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UX_DunningPolicies_BU_Idempotency",
                table: "DunningPolicies",
                columns: new[] { "BusinessUnitId", "IdempotencyKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UX_DunningPolicies_BU_Version",
                table: "DunningPolicies",
                columns: new[] { "BusinessUnitId", "PolicyVersion" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DunningRunDecisions_BusinessUnitId_CustomerStatementId",
                table: "DunningRunDecisions",
                columns: new[] { "BusinessUnitId", "CustomerStatementId" });

            migrationBuilder.CreateIndex(
                name: "IX_DunningRunDecisions_BusinessUnitId_DunningCaseId",
                table: "DunningRunDecisions",
                columns: new[] { "BusinessUnitId", "DunningCaseId" });

            migrationBuilder.CreateIndex(
                name: "IX_DunningRunDecisions_BusinessUnitId_DunningNoticeId",
                table: "DunningRunDecisions",
                columns: new[] { "BusinessUnitId", "DunningNoticeId" });

            migrationBuilder.CreateIndex(
                name: "IX_DunningRunDecisions_BU_Run_Customer_Currency",
                table: "DunningRunDecisions",
                columns: new[] { "BusinessUnitId", "DunningRunId", "CustomerId", "CurrencyId" });

            migrationBuilder.CreateIndex(
                name: "IX_DunningRunDecisions_CurrencyId",
                table: "DunningRunDecisions",
                column: "CurrencyId");

            migrationBuilder.CreateIndex(
                name: "IX_DunningRunDecisions_CustomerId",
                table: "DunningRunDecisions",
                column: "CustomerId");

            migrationBuilder.CreateIndex(
                name: "UX_DunningPolicySteps_BU_Policy_Stage",
                table: "DunningPolicySteps",
                columns: new[] { "BusinessUnitId", "DunningPolicyId", "Stage" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DunningRuns_BusinessUnitId_DunningPolicyId",
                table: "DunningRuns",
                columns: new[] { "BusinessUnitId", "DunningPolicyId" });

            migrationBuilder.CreateIndex(
                name: "UX_DunningRuns_BU_Idempotency",
                table: "DunningRuns",
                columns: new[] { "BusinessUnitId", "IdempotencyKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_FinanceCommunicationContacts_BU_Customer_Purpose",
                table: "FinanceCommunicationContacts",
                columns: new[] { "BusinessUnitId", "CustomerId", "Purpose", "IsActive" });

            migrationBuilder.CreateIndex(
                name: "IX_FinanceCommunicationContacts_CustomerId",
                table: "FinanceCommunicationContacts",
                column: "CustomerId");

            migrationBuilder.CreateIndex(
                name: "UX_FinanceCommunicationContacts_BU_Idempotency",
                table: "FinanceCommunicationContacts",
                columns: new[] { "BusinessUnitId", "IdempotencyKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UX_FinanceCommunicationContacts_BU_Token",
                table: "FinanceCommunicationContacts",
                columns: new[] { "BusinessUnitId", "DestinationToken" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UX_FinanceCommunicationContacts_BU_VerificationEvent",
                table: "FinanceCommunicationContacts",
                columns: new[] { "BusinessUnitId", "VerificationProviderEventId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PromisesToPay_BusinessUnitId_DunningCaseId",
                table: "PromisesToPay",
                columns: new[] { "BusinessUnitId", "DunningCaseId" });

            migrationBuilder.CreateIndex(
                name: "UX_PromisesToPay_BU_MatchedPayment",
                table: "PromisesToPay",
                columns: new[] { "BusinessUnitId", "MatchedPaymentId" },
                unique: true,
                filter: "\"MatchedPaymentId\" IS NOT NULL");

            migrationBuilder.Sql("""
                CREATE TABLE IF NOT EXISTS public."FinanceProviderSecrets" (
                    "Name" character varying(80) PRIMARY KEY,
                    "Secret" text NOT NULL,
                    "UpdatedOn" timestamp without time zone NOT NULL,
                    CONSTRAINT "CK_FinanceProviderSecrets_Length" CHECK (octet_length("Secret") >= 32)
                );
                REVOKE ALL ON public."FinanceProviderSecrets" FROM PUBLIC;
                REVOKE ALL ON public."FinanceProviderSecrets" FROM nexora_tenant_app;

                ALTER TABLE public."FinanceCommunicationContacts"
                    ADD CONSTRAINT "CK_FinanceCommunicationContacts_State" CHECK (
                        "Purpose" IN ('Billing','Collections') AND "Channel" IN ('Email','Portal')
                        AND "VerificationProviderEventId" <> '00000000-0000-0000-0000-000000000000'::uuid
                        AND (("IsActive" AND "DeactivatedOn" IS NULL AND "DeactivatedBy" IS NULL AND "DeactivationReason" IS NULL)
                          OR (NOT "IsActive" AND "DeactivatedOn" IS NOT NULL AND "DeactivatedBy" IS NOT NULL
                              AND "DeactivationReason" IS NOT NULL AND length(trim("DeactivationReason")) >= 20)));
                ALTER TABLE public."CustomerStatements"
                    ADD CONSTRAINT "CK_CustomerStatements_State" CHECK (
                        "Status" IN ('Draft','Finalized','Cancelled','Superseded')
                        AND length("ArtifactContent") > 0
                        AND "ArtifactHash" = encode(digest(convert_to("ArtifactContent", 'UTF8'), 'sha256'), 'hex')
                        AND (("SupersedesStatementId" IS NULL AND "CorrectionReason" IS NULL)
                          OR ("SupersedesStatementId" IS NOT NULL AND length(trim("CorrectionReason")) >= 20))
                        AND (("Status" = 'Draft' AND "StatementNumber" IS NULL AND "FinalizedBy" IS NULL AND "FinalizedOn" IS NULL)
                          OR ("Status" IN ('Finalized','Superseded') AND "StatementNumber" IS NOT NULL
                              AND "FinalizedBy" IS NOT NULL AND "FinalizedOn" IS NOT NULL)
                          OR ("Status" = 'Cancelled' AND "CancelledBy" IS NOT NULL AND "CancelledOn" IS NOT NULL)));
                ALTER TABLE public."CustomerStatementLines"
                    ADD CONSTRAINT "CK_CustomerStatementLines_AgingBucket" CHECK (
                        "AgingBucket" IN ('Settled','Current','1-30','31-60','61-90','90+'));
                ALTER TABLE public."DunningPolicies"
                    ADD CONSTRAINT "CK_DunningPolicies_State" CHECK (
                        ("Status" = 'Draft' AND "ApprovedBy" IS NULL AND "ApprovedOn" IS NULL
                            AND "ActivatedBy" IS NULL AND "ActivatedOn" IS NULL AND "RetiredBy" IS NULL AND "RetiredOn" IS NULL)
                        OR ("Status" = 'Approved' AND "ApprovedBy" IS NOT NULL AND "ApprovedOn" IS NOT NULL
                            AND "ActivatedBy" IS NULL AND "ActivatedOn" IS NULL AND "RetiredBy" IS NULL AND "RetiredOn" IS NULL)
                        OR ("Status" = 'Active' AND "ApprovedBy" IS NOT NULL AND "ApprovedOn" IS NOT NULL
                            AND "ActivatedBy" IS NOT NULL AND "ActivatedOn" IS NOT NULL AND "RetiredBy" IS NULL AND "RetiredOn" IS NULL)
                        OR ("Status" = 'Retired' AND "ApprovedBy" IS NOT NULL AND "ApprovedOn" IS NOT NULL
                            AND "ActivatedBy" IS NOT NULL AND "ActivatedOn" IS NOT NULL AND "RetiredBy" IS NOT NULL AND "RetiredOn" IS NOT NULL));
                ALTER TABLE public."DunningPolicySteps"
                    ADD CONSTRAINT "CK_DunningPolicySteps_Channel" CHECK ("Channel" IN ('Email','Portal'));
                ALTER TABLE public."CollectionControls"
                    ADD CONSTRAINT "CK_CollectionControls_State" CHECK (
                        ("Status" = 'Active' AND "ResolvedBy" IS NULL AND "ResolvedOn" IS NULL
                            AND "ResolutionReason" IS NULL AND "ResolutionEvidenceReference" IS NULL)
                        OR ("Status" = 'Resolved' AND "ResolvedBy" IS NOT NULL AND "ResolvedOn" IS NOT NULL
                            AND "ResolutionReason" IS NOT NULL AND length(trim("ResolutionReason")) >= 20
                            AND "ResolutionEvidenceReference" IS NOT NULL AND length(trim("ResolutionEvidenceReference")) >= 8));
                ALTER TABLE public."DunningCases"
                    ADD CONSTRAINT "CK_DunningCases_State" CHECK (
                        "Status" IN ('Open','Held','Disputed','Resolved','Cancelled')
                        AND (("UpdatedBy" IS NULL AND "UpdatedOn" IS NULL) OR ("UpdatedBy" IS NOT NULL AND "UpdatedOn" IS NOT NULL))
                        AND ("Status" = 'Open' OR ("StatusReason" IS NOT NULL AND "EvidenceReference" IS NOT NULL)));
                ALTER TABLE public."PromisesToPay"
                    ADD CONSTRAINT "CK_PromisesToPay_State" CHECK (
                        ("Status" = 'Open' AND "ClosedBy" IS NULL AND "ClosedOn" IS NULL
                            AND "ClosureEvidenceReference" IS NULL AND "MatchedPaymentId" IS NULL AND "MatchedAmount" IS NULL)
                        OR ("Status" = 'Kept' AND "ClosedBy" IS NOT NULL AND "ClosedOn" IS NOT NULL
                            AND "ClosureEvidenceReference" IS NOT NULL AND length(trim("ClosureEvidenceReference")) >= 8 AND "MatchedPaymentId" IS NOT NULL
                            AND "MatchedAmount" >= "Amount")
                        OR ("Status" IN ('Broken','Withdrawn') AND "ClosedBy" IS NOT NULL AND "ClosedOn" IS NOT NULL
                            AND "ClosureEvidenceReference" IS NOT NULL AND length(trim("ClosureEvidenceReference")) >= 8
                            AND "MatchedPaymentId" IS NULL AND "MatchedAmount" IS NULL));
                ALTER TABLE public."DunningRuns"
                    ADD CONSTRAINT "CK_DunningRuns_State" CHECK (
                        ("Status" = 'Pending' AND "LeaseOwner" IS NULL AND "LeaseToken" IS NULL AND "LeaseUntil" IS NULL
                            AND "CompletedOn" IS NULL
                            AND "CompletionEvidenceReference" IS NULL AND "FailureReason" IS NULL AND "FailureEvidenceReference" IS NULL)
                        OR ("Status" = 'Running' AND "LeaseOwner" IS NOT NULL AND "LeaseToken" IS NOT NULL AND "LeaseUntil" IS NOT NULL
                            AND "CompletedOn" IS NULL
                            AND "CompletionEvidenceReference" IS NULL AND "FailureReason" IS NULL AND "FailureEvidenceReference" IS NULL)
                        OR ("Status" = 'Completed' AND "LeaseOwner" IS NULL AND "LeaseToken" IS NULL AND "LeaseUntil" IS NULL
                            AND "CompletedOn" IS NOT NULL
                            AND "CompletionEvidenceReference" IS NOT NULL AND "FailureReason" IS NULL AND "FailureEvidenceReference" IS NULL)
                        OR ("Status" = 'Failed' AND "LeaseOwner" IS NULL AND "LeaseToken" IS NULL AND "LeaseUntil" IS NULL
                            AND "CompletedOn" IS NOT NULL
                            AND "CompletionEvidenceReference" IS NULL AND "FailureReason" IS NOT NULL
                            AND length(trim("FailureReason")) >= 20 AND "FailureEvidenceReference" IS NOT NULL
                            AND length(trim("FailureEvidenceReference")) >= 8));
                ALTER TABLE public."DunningNotices"
                    ADD CONSTRAINT "CK_DunningNotices_State" CHECK (
                        length(trim("Subject")) > 0 AND length(trim("Locale")) > 0
                        AND length(trim("ArtifactMediaType")) > 0 AND length("ArtifactContent") > 0
                        AND "ArtifactHash" = encode(digest(convert_to("Subject" || E'\n' || "ArtifactMediaType" || E'\n' || "Locale" || E'\n' || "ArtifactContent", 'UTF8'), 'sha256'), 'hex')
                        AND (("Status" = 'Draft' AND "ApprovedBy" IS NULL AND "ApprovedOn" IS NULL
                                AND "ReleasedBy" IS NULL AND "ReleasedOn" IS NULL AND "DeliveryUpdatedBy" IS NULL
                                AND "DeliveryUpdatedOn" IS NULL AND "SuppressionReason" IS NULL AND "CancelledBy" IS NULL)
                            OR ("Status" = 'Suppressed' AND "SuppressionReason" IS NOT NULL AND length(trim("SuppressionReason")) > 0
                                AND "ApprovedBy" IS NULL AND "ReleasedBy" IS NULL AND "DeliveryUpdatedBy" IS NULL AND "CancelledBy" IS NULL)
                            OR ("Status" = 'Approved' AND "ApprovedBy" IS NOT NULL AND "ApprovedOn" IS NOT NULL
                                AND "ReleasedBy" IS NULL AND "DeliveryUpdatedBy" IS NULL AND "CancelledBy" IS NULL)
                            OR ("Status" = 'Released' AND "ReleasedBy" IS NOT NULL AND "ReleasedOn" IS NOT NULL
                                AND "DeliveryUpdatedBy" IS NULL AND "CancelledBy" IS NULL)
                            OR ("Status" = 'Delivered' AND "ReleasedBy" IS NOT NULL AND "ReleasedOn" IS NOT NULL
                                AND "DeliveryUpdatedBy" IS NOT NULL AND "DeliveryUpdatedOn" IS NOT NULL
                                AND "ProviderReference" IS NOT NULL AND "FailureCode" IS NULL AND "CancelledBy" IS NULL)
                            OR ("Status" = 'Failed' AND "ReleasedBy" IS NOT NULL AND "ReleasedOn" IS NOT NULL
                                AND "DeliveryUpdatedBy" IS NOT NULL AND "DeliveryUpdatedOn" IS NOT NULL
                                AND "ProviderReference" IS NOT NULL AND "FailureCode" IS NOT NULL AND "CancelledBy" IS NULL)
                            OR ("Status" = 'Cancelled' AND "CancelledBy" IS NOT NULL AND "CancelledOn" IS NOT NULL
                                AND "CancellationReason" IS NOT NULL AND length(trim("CancellationReason")) >= 20
                                AND "CancellationEvidenceReference" IS NOT NULL
                                AND length(trim("CancellationEvidenceReference")) >= 8)));
                ALTER TABLE public."DunningDeliveryAttempts"
                    ADD CONSTRAINT "CK_DunningDeliveryAttempts_State" CHECK (
                        "Status" IN ('Delivered','Failed') AND "ProviderEventId" <> '00000000-0000-0000-0000-000000000000'::uuid
                        AND length(trim("SignedEvidenceReference")) >= 8
                        AND "ProviderOccurredOn" <= "OccurredOn" + interval '5 minutes'
                        AND (("Status" = 'Delivered' AND "FailureCode" IS NULL)
                            OR ("Status" = 'Failed' AND "FailureCode" IS NOT NULL)));

                CREATE OR REPLACE FUNCTION public.nexora_ar_validate_tenant_reference()
                RETURNS trigger LANGUAGE plpgsql SECURITY DEFINER
                SET search_path = pg_catalog, public AS $function$
                DECLARE row_data jsonb := to_jsonb(NEW);
                DECLARE prior_data jsonb := CASE WHEN TG_OP = 'UPDATE' THEN to_jsonb(OLD) ELSE '{}'::jsonb END;
                DECLARE customer_id bigint;
                DECLARE currency_id bigint;
                BEGIN
                    IF NEW."BusinessUnitId" <= 0 THEN
                        RAISE EXCEPTION 'a valid business unit is required' USING ERRCODE = '23514';
                    END IF;
                    IF row_data ? 'CustomerId' THEN
                        customer_id := NULLIF(row_data->>'CustomerId', '')::bigint;
                        IF customer_id IS NOT NULL AND NOT EXISTS (
                            SELECT 1 FROM public."Customers" c WHERE c."ID" = customer_id
                              AND (c."BUID" = NEW."BusinessUnitId" OR c."BUID" IS NULL)) THEN
                            RAISE EXCEPTION 'the tenant customer does not exist' USING ERRCODE = '23503';
                        END IF;
                    END IF;
                    IF row_data ? 'CurrencyId' THEN
                        currency_id := NULLIF(row_data->>'CurrencyId', '')::bigint;
                        IF currency_id IS NOT NULL AND NOT EXISTS (
                            SELECT 1 FROM public."Currency" c WHERE c."ID" = currency_id
                              AND c."BusinessUnitID" = NEW."BusinessUnitId") THEN
                            RAISE EXCEPTION 'the tenant currency does not exist' USING ERRCODE = '23503';
                        END IF;
                    END IF;
                    IF TG_TABLE_NAME = 'DunningCases' THEN
                        IF NOT EXISTS (
                            SELECT 1 FROM public."CustomerStatements" statement
                            JOIN public."DunningPolicies" policy
                              ON policy."BusinessUnitId" = NEW."BusinessUnitId" AND policy."Id" = NEW."DunningPolicyId"
                            WHERE statement."BusinessUnitId" = NEW."BusinessUnitId"
                              AND statement."Id" = NEW."CustomerStatementId"
                              AND statement."CustomerId" = NEW."CustomerId"
                              AND statement."CurrencyId" IS NOT DISTINCT FROM NEW."CurrencyId"
                              AND statement."Status" = 'Finalized' AND policy."Status" = 'Active') THEN
                            RAISE EXCEPTION 'the dunning case customer, currency, statement, and active policy do not form one tenant accounting chain'
                                USING ERRCODE = '23514';
                        END IF;
                    ELSIF TG_TABLE_NAME = 'DunningRunDecisions' THEN
                        IF NOT EXISTS (SELECT 1 FROM public."DunningRuns" r
                            WHERE r."BusinessUnitId" = NEW."BusinessUnitId" AND r."Id" = NEW."DunningRunId") THEN
                            RAISE EXCEPTION 'the tenant dunning run does not exist' USING ERRCODE = '23503';
                        END IF;
                        IF NEW."CustomerStatementId" IS NOT NULL AND NOT EXISTS (
                            SELECT 1 FROM public."CustomerStatements" s
                            WHERE s."BusinessUnitId" = NEW."BusinessUnitId" AND s."Id" = NEW."CustomerStatementId"
                              AND s."CustomerId" = NEW."CustomerId" AND s."CurrencyId" IS NOT DISTINCT FROM NEW."CurrencyId") THEN
                            RAISE EXCEPTION 'the decision statement does not match its tenant customer and currency' USING ERRCODE = '23514';
                        END IF;
                        IF NEW."DunningCaseId" IS NOT NULL AND NOT EXISTS (
                            SELECT 1 FROM public."DunningCases" c
                            WHERE c."BusinessUnitId" = NEW."BusinessUnitId" AND c."Id" = NEW."DunningCaseId"
                              AND c."CustomerId" = NEW."CustomerId" AND c."CurrencyId" IS NOT DISTINCT FROM NEW."CurrencyId") THEN
                            RAISE EXCEPTION 'the decision case does not match its tenant customer and currency' USING ERRCODE = '23514';
                        END IF;
                        IF NEW."DunningNoticeId" IS NOT NULL AND NOT EXISTS (
                            SELECT 1 FROM public."DunningNotices" n
                            JOIN public."DunningCases" c ON c."BusinessUnitId" = n."BusinessUnitId" AND c."Id" = n."DunningCaseId"
                            WHERE n."BusinessUnitId" = NEW."BusinessUnitId" AND n."Id" = NEW."DunningNoticeId"
                              AND c."CustomerId" = NEW."CustomerId" AND c."CurrencyId" IS NOT DISTINCT FROM NEW."CurrencyId"
                              AND (NEW."DunningCaseId" IS NULL OR n."DunningCaseId" = NEW."DunningCaseId")
                              AND (NEW."CustomerStatementId" IS NULL OR n."CustomerStatementId" = NEW."CustomerStatementId")) THEN
                            RAISE EXCEPTION 'the decision notice does not match its tenant evidence chain' USING ERRCODE = '23514';
                        END IF;
                    ELSIF TG_TABLE_NAME = 'DunningNotices' THEN
                        IF NOT EXISTS (
                            SELECT 1
                            FROM public."DunningCases" c
                            JOIN public."CustomerStatements" s
                              ON s."BusinessUnitId" = c."BusinessUnitId" AND s."Id" = NEW."CustomerStatementId"
                             AND s."Id" = c."CustomerStatementId"
                             AND s."CustomerId" = c."CustomerId"
                             AND s."CurrencyId" IS NOT DISTINCT FROM c."CurrencyId"
                            JOIN public."FinanceCommunicationContacts" contact
                              ON contact."BusinessUnitId" = c."BusinessUnitId" AND contact."Id" = NEW."FinanceCommunicationContactId"
                             AND contact."CustomerId" = c."CustomerId"
                             AND contact."IsActive" AND contact."IsVerified" AND contact."Purpose" = 'Collections'
                             AND contact."EffectiveFrom" <= NEW."CreatedOn"
                             AND (contact."EffectiveTo" IS NULL OR contact."EffectiveTo" > NEW."CreatedOn")
                            JOIN public."DunningPolicySteps" step
                             ON step."BusinessUnitId" = c."BusinessUnitId" AND step."DunningPolicyId" = c."DunningPolicyId"
                             AND step."Stage" = NEW."Stage"
                             AND step."Channel" = contact."Channel"
                            WHERE c."BusinessUnitId" = NEW."BusinessUnitId" AND c."Id" = NEW."DunningCaseId") THEN
                            RAISE EXCEPTION 'the notice customer, statement, contact, case, and policy step do not form one tenant evidence chain'
                                USING ERRCODE = '23514';
                        END IF;
                    ELSIF TG_TABLE_NAME = 'DunningDeliveryAttempts' THEN
                        IF NOT EXISTS (
                            SELECT 1 FROM public."DunningNotices" n
                            WHERE n."BusinessUnitId" = NEW."BusinessUnitId"
                              AND n."Id" = NEW."DunningNoticeId"
                              AND n."ArtifactHash" = NEW."ArtifactHash"
                              AND n."TemplateVersion" = NEW."TemplateVersion") THEN
                            RAISE EXCEPTION 'delivery evidence does not match the governed notice artifact' USING ERRCODE = '23514';
                        END IF;
                    END IF;
                    RETURN NEW;
                END
                $function$;

                CREATE OR REPLACE FUNCTION public.nexora_ar_governed_mutation()
                RETURNS trigger LANGUAGE plpgsql SECURITY DEFINER
                SET search_path = pg_catalog, public AS $function$
                DECLARE old_data jsonb;
                DECLARE new_data jsonb;
                DECLARE parent_status text;
                DECLARE payment_amount numeric;
                DECLARE requires_approval boolean;
                DECLARE trusted_actor text;
                DECLARE actor_signature text;
                DECLARE actor_secret text;
                DECLARE expected_actor text;
                BEGIN
                    IF TG_OP = 'DELETE' THEN
                        RAISE EXCEPTION 'governed receivables records cannot be deleted' USING ERRCODE = '55000';
                    END IF;
                    IF TG_OP = 'INSERT' THEN
                        new_data := to_jsonb(NEW);
                        IF current_setting('role', true) = 'nexora_tenant_app' THEN
                            trusted_actor := NULLIF(current_setting('nexora.actor_id', true), '');
                            actor_signature := NULLIF(current_setting('nexora.actor_signature', true), '');
                            SELECT "Secret" INTO actor_secret FROM public."FinanceProviderSecrets" WHERE "Name" = 'AuditActor';
                            IF trusted_actor IS NULL OR actor_secret IS NULL OR actor_signature IS NULL
                               OR actor_signature <> encode(hmac(convert_to(NEW."BusinessUnitId"::text || E'\n' || trusted_actor, 'UTF8'),
                                    convert_to(actor_secret, 'UTF8'), 'sha256'), 'hex') THEN
                                RAISE EXCEPTION 'a signed authenticated transaction actor is required' USING ERRCODE = '42501';
                            END IF;
                            expected_actor := CASE TG_TABLE_NAME
                                WHEN 'DunningDeliveryAttempts' THEN new_data->>'RecordedBy'
                                WHEN 'CustomerStatementLines' THEN NULL
                                WHEN 'DunningPolicySteps' THEN NULL
                                WHEN 'DunningRunDecisions' THEN NULL
                                ELSE new_data->>'CreatedBy' END;
                            IF expected_actor IS NOT NULL AND expected_actor <> trusted_actor THEN
                                RAISE EXCEPTION 'the mutation actor does not match the authenticated transaction actor' USING ERRCODE = '42501';
                            END IF;
                        END IF;
                        IF new_data ? 'Version' AND (new_data->>'Version')::bigint <> 1 THEN
                            RAISE EXCEPTION 'governed aggregates must begin at version one' USING ERRCODE = '55000';
                        END IF;
                        IF TG_TABLE_NAME = 'FinanceCommunicationContacts' THEN
                            IF NOT NEW."IsActive" OR NOT NEW."IsVerified" OR NEW."DeactivatedBy" IS NOT NULL OR NEW."DeactivatedOn" IS NOT NULL THEN
                                RAISE EXCEPTION 'communication contacts must begin active and verified' USING ERRCODE = '55000';
                            END IF;
                        ELSIF TG_TABLE_NAME = 'CustomerStatements' THEN
                            IF NEW."Status" <> 'Draft' OR NEW."StatementNumber" IS NOT NULL OR NEW."FinalizedBy" IS NOT NULL
                               OR NEW."FinalizedOn" IS NOT NULL OR NEW."CancelledBy" IS NOT NULL OR NEW."CancelledOn" IS NOT NULL THEN
                                RAISE EXCEPTION 'customer statements must begin as unnumbered drafts' USING ERRCODE = '55000';
                            END IF;
                        ELSIF TG_TABLE_NAME = 'CustomerStatementLines' THEN
                            SELECT s."Status" INTO parent_status FROM public."CustomerStatements" s
                             WHERE s."BusinessUnitId" = NEW."BusinessUnitId" AND s."Id" = NEW."CustomerStatementId" FOR UPDATE;
                            IF parent_status <> 'Draft' THEN
                                RAISE EXCEPTION 'statement lines can only be added to a draft' USING ERRCODE = '55000';
                            END IF;
                        ELSIF TG_TABLE_NAME = 'DunningPolicies' THEN
                            IF NEW."Status" <> 'Draft' OR NEW."ApprovedBy" IS NOT NULL OR NEW."ApprovedOn" IS NOT NULL
                               OR NEW."ActivatedBy" IS NOT NULL OR NEW."ActivatedOn" IS NOT NULL
                               OR NEW."RetiredBy" IS NOT NULL OR NEW."RetiredOn" IS NOT NULL THEN
                                RAISE EXCEPTION 'dunning policies must begin as unapproved drafts' USING ERRCODE = '55000';
                            END IF;
                        ELSIF TG_TABLE_NAME = 'DunningPolicySteps' THEN
                            SELECT p."Status" INTO parent_status FROM public."DunningPolicies" p
                             WHERE p."BusinessUnitId" = NEW."BusinessUnitId" AND p."Id" = NEW."DunningPolicyId" FOR UPDATE;
                            IF parent_status <> 'Draft' THEN
                                RAISE EXCEPTION 'policy steps can only be added to a draft' USING ERRCODE = '55000';
                            END IF;
                        ELSIF TG_TABLE_NAME = 'CustomerCollectionProfiles' THEN
                            IF NEW."ModifiedBy" IS NOT NULL OR NEW."ModifiedOn" IS NOT NULL THEN
                                RAISE EXCEPTION 'collection profiles cannot begin with modification evidence' USING ERRCODE = '55000';
                            END IF;
                        ELSIF TG_TABLE_NAME = 'CollectionControls' THEN
                            IF NEW."Status" <> 'Active' OR NEW."ResolvedBy" IS NOT NULL OR NEW."ResolvedOn" IS NOT NULL THEN
                                RAISE EXCEPTION 'collection controls must begin active and unresolved' USING ERRCODE = '55000';
                            END IF;
                        ELSIF TG_TABLE_NAME = 'DunningCases' THEN
                            IF NEW."Status" <> 'Open' OR NEW."CurrentStage" <> 0 OR NEW."UpdatedBy" IS NOT NULL
                               OR NEW."UpdatedOn" IS NOT NULL OR NEW."PromiseAmount" IS NOT NULL OR NEW."PromiseDueOn" IS NOT NULL THEN
                                RAISE EXCEPTION 'dunning cases must begin open at stage zero' USING ERRCODE = '55000';
                            END IF;
                        ELSIF TG_TABLE_NAME = 'PromisesToPay' THEN
                            IF NEW."Status" <> 'Open' OR NEW."ClosedBy" IS NOT NULL OR NEW."ClosedOn" IS NOT NULL
                               OR NEW."MatchedPaymentId" IS NOT NULL OR NEW."MatchedAmount" IS NOT NULL THEN
                                RAISE EXCEPTION 'promises must begin open without settlement evidence' USING ERRCODE = '55000';
                            END IF;
                        ELSIF TG_TABLE_NAME = 'DunningRuns' THEN
                            IF NEW."Status" <> 'Pending' OR NEW."LeaseOwner" IS NOT NULL OR NEW."LeaseToken" IS NOT NULL
                               OR NEW."LeaseUntil" IS NOT NULL OR NEW."CompletedOn" IS NOT NULL
                               OR NEW."CompletionEvidenceReference" IS NOT NULL OR NEW."FailureReason" IS NOT NULL
                               OR NEW."FailureEvidenceReference" IS NOT NULL THEN
                                RAISE EXCEPTION 'dunning runs must begin pending' USING ERRCODE = '55000';
                            END IF;
                        ELSIF TG_TABLE_NAME = 'DunningNotices' THEN
                            IF NEW."Status" NOT IN ('Draft','Suppressed') OR NEW."ApprovedBy" IS NOT NULL
                               OR NEW."ReleasedBy" IS NOT NULL OR NEW."DeliveryUpdatedBy" IS NOT NULL OR NEW."CancelledBy" IS NOT NULL THEN
                                RAISE EXCEPTION 'dunning notices must begin draft or suppressed without terminal actors' USING ERRCODE = '55000';
                            END IF;
                        END IF;
                        RETURN NEW;
                    END IF;
                    old_data := to_jsonb(OLD); new_data := to_jsonb(NEW);
                    IF NEW."BusinessUnitId" <> OLD."BusinessUnitId" THEN
                        RAISE EXCEPTION 'business unit ownership is immutable' USING ERRCODE = '55000';
                    END IF;
                    IF current_setting('role', true) = 'nexora_tenant_app' THEN
                        trusted_actor := NULLIF(current_setting('nexora.actor_id', true), '');
                        actor_signature := NULLIF(current_setting('nexora.actor_signature', true), '');
                        SELECT "Secret" INTO actor_secret FROM public."FinanceProviderSecrets" WHERE "Name" = 'AuditActor';
                        IF trusted_actor IS NULL OR actor_secret IS NULL OR actor_signature IS NULL
                           OR actor_signature <> encode(hmac(convert_to(NEW."BusinessUnitId"::text || E'\n' || trusted_actor, 'UTF8'),
                                convert_to(actor_secret, 'UTF8'), 'sha256'), 'hex') THEN
                            RAISE EXCEPTION 'a signed authenticated transaction actor is required' USING ERRCODE = '42501';
                        END IF;
                        expected_actor := CASE TG_TABLE_NAME
                            WHEN 'FinanceCommunicationContacts' THEN new_data->>'DeactivatedBy'
                            WHEN 'CustomerStatements' THEN CASE new_data->>'Status'
                                WHEN 'Finalized' THEN new_data->>'FinalizedBy'
                                WHEN 'Cancelled' THEN new_data->>'CancelledBy' ELSE NULL END
                            WHEN 'DunningPolicies' THEN CASE new_data->>'Status'
                                WHEN 'Approved' THEN new_data->>'ApprovedBy'
                                WHEN 'Active' THEN new_data->>'ActivatedBy'
                                WHEN 'Retired' THEN new_data->>'RetiredBy' ELSE NULL END
                            WHEN 'CustomerCollectionProfiles' THEN new_data->>'ModifiedBy'
                            WHEN 'CollectionControls' THEN new_data->>'ResolvedBy'
                            WHEN 'DunningCases' THEN new_data->>'UpdatedBy'
                            WHEN 'PromisesToPay' THEN new_data->>'ClosedBy'
                            WHEN 'DunningRuns' THEN CASE new_data->>'Status'
                                WHEN 'Running' THEN new_data->>'LeaseOwner' ELSE old_data->>'LeaseOwner' END
                            WHEN 'DunningNotices' THEN CASE new_data->>'Status'
                                WHEN 'Approved' THEN new_data->>'ApprovedBy'
                                WHEN 'Released' THEN new_data->>'ReleasedBy'
                                WHEN 'Delivered' THEN new_data->>'DeliveryUpdatedBy'
                                WHEN 'Failed' THEN new_data->>'DeliveryUpdatedBy'
                                WHEN 'Cancelled' THEN new_data->>'CancelledBy' ELSE NULL END
                            ELSE NULL END;
                        IF expected_actor IS NOT NULL AND expected_actor <> trusted_actor THEN
                            RAISE EXCEPTION 'the mutation actor does not match the authenticated transaction actor' USING ERRCODE = '42501';
                        END IF;
                    END IF;
                    IF old_data ? 'Version' AND (new_data->>'Version')::bigint <> (old_data->>'Version')::bigint + 1 THEN
                        RAISE EXCEPTION 'aggregate version must advance exactly once' USING ERRCODE = '40001';
                    END IF;
                    IF TG_TABLE_NAME = 'FinanceCommunicationContacts' THEN
                        IF OLD."IsActive" AND NOT NEW."IsActive" THEN
                            IF (new_data - ARRAY['IsActive','EffectiveTo','DeactivatedBy','DeactivatedOn','DeactivationReason','Version'])
                               IS DISTINCT FROM (old_data - ARRAY['IsActive','EffectiveTo','DeactivatedBy','DeactivatedOn','DeactivationReason','Version']) THEN
                                RAISE EXCEPTION 'communication contact identity and verification evidence are immutable' USING ERRCODE = '55000';
                            END IF;
                        ELSE
                            RAISE EXCEPTION 'invalid or immutable communication contact transition' USING ERRCODE = '55000';
                        END IF;
                    ELSIF TG_TABLE_NAME = 'CustomerStatements' THEN
                        IF OLD."Status" = 'Draft' AND NEW."Status" = 'Finalized' THEN
                            IF (new_data - ARRAY['Status','StatementNumber','FinalizedBy','FinalizedOn','ArtifactReference','ArtifactContent','ArtifactHash','Version'])
                               IS DISTINCT FROM
                               (old_data - ARRAY['Status','StatementNumber','FinalizedBy','FinalizedOn','ArtifactReference','ArtifactContent','ArtifactHash','Version']) THEN
                                RAISE EXCEPTION 'statement snapshot changed during finalization' USING ERRCODE = '55000';
                            END IF;
                            IF position('{{STATEMENT_NUMBER}}' in OLD."ArtifactContent") = 0
                               OR position('{{STATEMENT_NUMBER}}' in NEW."ArtifactContent") > 0
                               OR NEW."ArtifactContent" <> replace(OLD."ArtifactContent", '{{STATEMENT_NUMBER}}', NEW."StatementNumber")
                               OR NEW."ArtifactHash" = OLD."ArtifactHash"
                               OR NEW."ArtifactHash" <> encode(digest(convert_to(NEW."ArtifactContent", 'UTF8'), 'sha256'), 'hex')
                               OR NEW."FinalizedBy" IS NULL OR NEW."FinalizedBy" = OLD."CreatedBy" THEN
                                RAISE EXCEPTION 'the finalized statement artifact is not the governed numbered rendering' USING ERRCODE = '55000';
                            END IF;
                        ELSIF OLD."Status" = 'Draft' AND NEW."Status" = 'Cancelled' THEN
                            IF (new_data - ARRAY['Status','CancelledBy','CancelledOn','CancellationReason','Version'])
                               IS DISTINCT FROM
                               (old_data - ARRAY['Status','CancelledBy','CancelledOn','CancellationReason','Version']) THEN
                                RAISE EXCEPTION 'statement snapshot changed during cancellation' USING ERRCODE = '55000';
                            END IF;
                        ELSIF OLD."Status" = 'Finalized' AND NEW."Status" = 'Superseded' THEN
                            IF (new_data - ARRAY['Status','Version']) IS DISTINCT FROM (old_data - ARRAY['Status','Version']) THEN
                                RAISE EXCEPTION 'superseded statements are immutable' USING ERRCODE = '55000';
                            END IF;
                        ELSE
                            RAISE EXCEPTION 'invalid or immutable statement transition' USING ERRCODE = '55000';
                        END IF;
                    ELSIF TG_TABLE_NAME = 'CustomerStatementLines' THEN
                        RAISE EXCEPTION 'statement snapshot lines are append-only' USING ERRCODE = '55000';
                    ELSIF TG_TABLE_NAME = 'DunningPolicySteps' THEN
                        RAISE EXCEPTION 'dunning policy steps are append-only' USING ERRCODE = '55000';
                    ELSIF TG_TABLE_NAME = 'DunningDeliveryAttempts' THEN
                        RAISE EXCEPTION 'delivery evidence is append-only' USING ERRCODE = '55000';
                    ELSIF TG_TABLE_NAME = 'DunningRunDecisions' THEN
                        RAISE EXCEPTION 'dunning run decisions are append-only' USING ERRCODE = '55000';
                    ELSIF TG_TABLE_NAME = 'DunningPolicies' THEN
                        IF OLD."Status" = 'Draft' AND NEW."Status" = 'Approved' THEN
                            IF (new_data - ARRAY['Status','ApprovedBy','ApprovedOn','Version']) IS DISTINCT FROM
                               (old_data - ARRAY['Status','ApprovedBy','ApprovedOn','Version']) THEN
                                RAISE EXCEPTION 'policy content changed during approval' USING ERRCODE = '55000';
                            END IF;
                            IF NEW."ApprovedBy" IS NULL OR NEW."ApprovedBy" = OLD."CreatedBy" THEN
                                RAISE EXCEPTION 'policy approval requires an independent checker' USING ERRCODE = '55000';
                            END IF;
                        ELSIF OLD."Status" = 'Approved' AND NEW."Status" = 'Active' THEN
                            IF (new_data - ARRAY['Status','ActivatedBy','ActivatedOn','Version']) IS DISTINCT FROM
                               (old_data - ARRAY['Status','ActivatedBy','ActivatedOn','Version']) THEN
                                RAISE EXCEPTION 'approved policy content is immutable' USING ERRCODE = '55000';
                            END IF;
                            IF NEW."ActivatedBy" IS NULL OR NEW."ActivatedBy" IN (OLD."CreatedBy", OLD."ApprovedBy") THEN
                                RAISE EXCEPTION 'policy activation requires an independent operator' USING ERRCODE = '55000';
                            END IF;
                        ELSIF OLD."Status" = 'Active' AND NEW."Status" = 'Retired' THEN
                            IF (new_data - ARRAY['Status','RetiredBy','RetiredOn','Version']) IS DISTINCT FROM
                               (old_data - ARRAY['Status','RetiredBy','RetiredOn','Version']) THEN
                                RAISE EXCEPTION 'active policy content is immutable' USING ERRCODE = '55000';
                            END IF;
                        ELSE
                            RAISE EXCEPTION 'invalid or immutable dunning policy transition' USING ERRCODE = '55000';
                        END IF;
                    ELSIF TG_TABLE_NAME = 'CustomerCollectionProfiles' THEN
                        IF (new_data - ARRAY['DunningPolicyId','FinanceCommunicationContactId','Locale','TimeZoneId','Collector',
                                'AutomaticDeliveryAllowed','IsOnHold','HoldReason','HoldEvidenceReference','ModifiedBy','ModifiedOn','Version'])
                           IS DISTINCT FROM
                           (old_data - ARRAY['DunningPolicyId','FinanceCommunicationContactId','Locale','TimeZoneId','Collector',
                                'AutomaticDeliveryAllowed','IsOnHold','HoldReason','HoldEvidenceReference','ModifiedBy','ModifiedOn','Version'])
                           OR NEW."ModifiedBy" IS NULL OR NEW."ModifiedOn" IS NULL THEN
                            RAISE EXCEPTION 'invalid collection profile update' USING ERRCODE = '55000';
                        END IF;
                    ELSIF TG_TABLE_NAME = 'CollectionControls' THEN
                        IF OLD."Status" <> 'Active' OR NEW."Status" <> 'Resolved'
                           OR (new_data - ARRAY['Status','ResolvedBy','ResolvedOn','ResolutionReason','ResolutionEvidenceReference','Version'])
                              IS DISTINCT FROM
                              (old_data - ARRAY['Status','ResolvedBy','ResolvedOn','ResolutionReason','ResolutionEvidenceReference','Version']) THEN
                            RAISE EXCEPTION 'invalid or immutable collection control transition' USING ERRCODE = '55000';
                        END IF;
                    ELSIF TG_TABLE_NAME = 'DunningCases' THEN
                        IF OLD."Status" IN ('Resolved','Cancelled') OR NOT (
                            (OLD."Status" = NEW."Status" AND OLD."Status" IN ('Open','Held','Disputed')) OR
                            (OLD."Status", NEW."Status") IN (('Open','Held'),('Open','Disputed'),('Held','Open'),('Disputed','Open'),
                                ('Open','Resolved'),('Held','Resolved'),('Disputed','Resolved'),
                                ('Open','Cancelled'),('Held','Cancelled'),('Disputed','Cancelled')))
                           OR (new_data - ARRAY['Status','CurrentStage','CurrentExposure','NextActionOn','AssignedTo','PromiseAmount',
                                'PromiseDueOn','UpdatedBy','UpdatedOn','StatusReason','EvidenceReference','Version'])
                              IS DISTINCT FROM
                              (old_data - ARRAY['Status','CurrentStage','CurrentExposure','NextActionOn','AssignedTo','PromiseAmount',
                                'PromiseDueOn','UpdatedBy','UpdatedOn','StatusReason','EvidenceReference','Version'])
                           OR NEW."UpdatedBy" IS NULL OR NEW."UpdatedOn" IS NULL THEN
                            RAISE EXCEPTION 'invalid or immutable dunning case transition' USING ERRCODE = '55000';
                        END IF;
                    ELSIF TG_TABLE_NAME = 'PromisesToPay' THEN
                        IF OLD."Status" = 'Kept' AND NEW."Status" = 'Broken' THEN
                            IF (new_data - ARRAY['Status','ClosedBy','ClosedOn','ClosureEvidenceReference','MatchedPaymentId','MatchedAmount','Version'])
                               IS DISTINCT FROM
                               (old_data - ARRAY['Status','ClosedBy','ClosedOn','ClosureEvidenceReference','MatchedPaymentId','MatchedAmount','Version'])
                               OR NEW."ClosedBy" IS NULL OR NEW."ClosedOn" IS NULL
                               OR NEW."ClosureEvidenceReference" IS NULL
                               OR NEW."MatchedPaymentId" IS NOT NULL OR NEW."MatchedAmount" IS NOT NULL THEN
                                RAISE EXCEPTION 'invalid kept-promise accounting reversal' USING ERRCODE = '55000';
                            END IF;
                        ELSIF OLD."Status" <> 'Open' OR NEW."Status" NOT IN ('Kept','Broken','Withdrawn')
                           OR (new_data - ARRAY['Status','ClosedBy','ClosedOn','ClosureEvidenceReference','MatchedPaymentId','MatchedAmount','Version'])
                              IS DISTINCT FROM
                              (old_data - ARRAY['Status','ClosedBy','ClosedOn','ClosureEvidenceReference','MatchedPaymentId','MatchedAmount','Version']) THEN
                            RAISE EXCEPTION 'invalid or immutable promise transition' USING ERRCODE = '55000';
                        END IF;
                        IF NEW."Status" = 'Kept' THEN
                            SELECT p."Amount" - COALESCE(SUM(r."Amount") FILTER (
                                WHERE r."Status" = 'Released' AND r."ReleasedOn" <= NEW."ClosedOn"
                                  AND (r."ReversedOn" IS NULL OR r."ReversedOn" > NEW."ClosedOn")), 0)
                            INTO payment_amount
                            FROM public."CustomerPayments" p
                            JOIN public."DunningCases" c ON c."BusinessUnitId" = NEW."BusinessUnitId" AND c."Id" = NEW."DunningCaseId"
                            LEFT JOIN public."CustomerRefunds" r ON r."BusinessUnitId" = p."BusinessUnitId"
                                AND r."SourcePaymentId" = p."Id"
                            WHERE p."BusinessUnitId" = NEW."BusinessUnitId" AND p."Id" = NEW."MatchedPaymentId"
                              AND p."Status" = 'Posted' AND p."ReversedOn" IS NULL AND p."CustomerId" = c."CustomerId"
                              AND p."CurrencyId" IS NOT DISTINCT FROM c."CurrencyId"
                              AND p."PaymentDate" >= NEW."PromisedOn" AND p."PaymentDate" <= NEW."ClosedOn"
                            GROUP BY p."Amount";
                            IF payment_amount IS NULL OR NEW."MatchedAmount" < NEW."Amount" OR NEW."MatchedAmount" > payment_amount THEN
                                RAISE EXCEPTION 'a kept promise requires a matching posted tenant payment' USING ERRCODE = '23514';
                            END IF;
                        END IF;
                    ELSIF TG_TABLE_NAME = 'DunningNotices' THEN
                        IF OLD."Status" = 'Draft' AND NEW."Status" = 'Approved' THEN
                            IF (new_data - ARRAY['Status','ApprovedBy','ApprovedOn','Version']) IS DISTINCT FROM
                               (old_data - ARRAY['Status','ApprovedBy','ApprovedOn','Version'])
                               OR NEW."ApprovedBy" IS NULL OR NEW."ApprovedBy" = OLD."CreatedBy" THEN
                                RAISE EXCEPTION 'invalid notice approval' USING ERRCODE = '55000';
                            END IF;
                        ELSIF OLD."Status" IN ('Draft','Approved','Failed') AND NEW."Status" = 'Released' THEN
                            SELECT step."RequiresApproval" INTO requires_approval
                            FROM public."DunningCases" c
                            JOIN public."DunningPolicySteps" step
                              ON step."BusinessUnitId" = c."BusinessUnitId" AND step."DunningPolicyId" = c."DunningPolicyId"
                             AND step."Stage" = NEW."Stage"
                            WHERE c."BusinessUnitId" = NEW."BusinessUnitId" AND c."Id" = NEW."DunningCaseId";
                            IF (new_data - ARRAY['Status','ReleasedBy','ReleasedOn','ProviderReference','FailureCode','Version'])
                               IS DISTINCT FROM
                               (old_data - ARRAY['Status','ReleasedBy','ReleasedOn','ProviderReference','FailureCode','Version'])
                               OR NEW."ReleasedBy" IS NULL OR NEW."ReleasedBy" = OLD."CreatedBy"
                               OR NEW."ReleasedBy" IS NOT DISTINCT FROM OLD."ApprovedBy"
                               OR NEW."ReleasedOn" < (clock_timestamp() AT TIME ZONE 'UTC') - interval '5 minutes'
                               OR NEW."ReleasedOn" > (clock_timestamp() AT TIME ZONE 'UTC') + interval '1 minute'
                               OR NEW."ProviderReference" IS NOT NULL OR NEW."FailureCode" IS NOT NULL
                               OR requires_approval IS NULL
                               OR (requires_approval AND OLD."Status" = 'Draft')
                               OR NOT EXISTS (
                                    SELECT 1 FROM public."DunningCases" c
                                    JOIN public."CustomerStatements" statement
                                      ON statement."BusinessUnitId" = c."BusinessUnitId"
                                     AND statement."Id" = c."CustomerStatementId"
                                     AND statement."Status" = 'Finalized'
                                    JOIN public."DunningPolicies" policy
                                      ON policy."BusinessUnitId" = c."BusinessUnitId"
                                     AND policy."Id" = c."DunningPolicyId" AND policy."Status" = 'Active'
                                    JOIN public."FinanceCommunicationContacts" contact
                                      ON contact."BusinessUnitId" = c."BusinessUnitId"
                                     AND contact."Id" = NEW."FinanceCommunicationContactId"
                                     AND contact."CustomerId" = c."CustomerId"
                                     AND contact."IsActive" AND contact."IsVerified"
                                     AND contact."Purpose" = 'Collections'
                                     AND contact."EffectiveFrom" <= (clock_timestamp() AT TIME ZONE 'UTC')
                                     AND (contact."EffectiveTo" IS NULL OR contact."EffectiveTo" > (clock_timestamp() AT TIME ZONE 'UTC'))
                                    JOIN public."DunningPolicySteps" release_step
                                      ON release_step."BusinessUnitId" = c."BusinessUnitId"
                                     AND release_step."DunningPolicyId" = c."DunningPolicyId"
                                     AND release_step."Stage" = NEW."Stage"
                                     AND release_step."Channel" = contact."Channel"
                                     AND release_step."TemplateVersion" = NEW."TemplateVersion"
                                    WHERE c."BusinessUnitId" = NEW."BusinessUnitId"
                                      AND c."Id" = NEW."DunningCaseId" AND c."Status" = 'Open'
                                      AND NEW."CustomerStatementId" = c."CustomerStatementId") THEN
                                RAISE EXCEPTION 'invalid notice release' USING ERRCODE = '55000';
                            END IF;
                        ELSIF OLD."Status" = 'Released' AND NEW."Status" IN ('Delivered','Failed') THEN
                            IF (new_data - ARRAY['Status','DeliveryUpdatedBy','DeliveryUpdatedOn','ProviderReference','FailureCode','Version'])
                               IS DISTINCT FROM
                               (old_data - ARRAY['Status','DeliveryUpdatedBy','DeliveryUpdatedOn','ProviderReference','FailureCode','Version']) THEN
                                RAISE EXCEPTION 'invalid notice delivery result' USING ERRCODE = '55000';
                            END IF;
                        ELSIF OLD."Status" IN ('Draft','Approved','Released','Failed') AND NEW."Status" = 'Cancelled' THEN
                            IF (new_data - ARRAY['Status','CancelledBy','CancelledOn','CancellationReason','CancellationEvidenceReference','Version'])
                               IS DISTINCT FROM
                               (old_data - ARRAY['Status','CancelledBy','CancelledOn','CancellationReason','CancellationEvidenceReference','Version']) THEN
                                RAISE EXCEPTION 'invalid notice cancellation' USING ERRCODE = '55000';
                            END IF;
                        ELSE
                            RAISE EXCEPTION 'invalid or immutable dunning notice transition' USING ERRCODE = '55000';
                        END IF;
                    ELSIF TG_TABLE_NAME = 'DunningRuns' THEN
                        IF OLD."Status" = 'Pending' AND NEW."Status" = 'Running' THEN
                            IF (new_data - ARRAY['Status','LeaseOwner','LeaseToken','LeaseUntil','Version']) IS DISTINCT FROM
                               (old_data - ARRAY['Status','LeaseOwner','LeaseToken','LeaseUntil','Version']) THEN
                                RAISE EXCEPTION 'invalid dunning run start' USING ERRCODE = '55000';
                            END IF;
                        ELSIF OLD."Status" = 'Running' AND NEW."Status" = 'Running' THEN
                            IF NEW."LeaseOwner" = OLD."LeaseOwner" AND NEW."LeaseToken" = OLD."LeaseToken" THEN
                                IF (new_data - ARRAY['LeaseUntil','Version']) IS DISTINCT FROM
                                   (old_data - ARRAY['LeaseUntil','Version'])
                                   OR OLD."LeaseUntil" < (clock_timestamp() AT TIME ZONE 'UTC')
                                   OR NEW."LeaseUntil" <= OLD."LeaseUntil"
                                   OR NEW."LeaseUntil" <= (clock_timestamp() AT TIME ZONE 'UTC') THEN
                                    RAISE EXCEPTION 'invalid dunning run lease heartbeat' USING ERRCODE = '55000';
                                END IF;
                            ELSIF OLD."LeaseUntil" >= (clock_timestamp() AT TIME ZONE 'UTC')
                               OR (new_data - ARRAY['LeaseOwner','LeaseToken','LeaseUntil','Version']) IS DISTINCT FROM
                                  (old_data - ARRAY['LeaseOwner','LeaseToken','LeaseUntil','Version'])
                               OR NEW."LeaseToken" IS NOT DISTINCT FROM OLD."LeaseToken"
                               OR NEW."LeaseUntil" <= (clock_timestamp() AT TIME ZONE 'UTC') THEN
                                RAISE EXCEPTION 'only an expired dunning run lease can be recovered' USING ERRCODE = '55000';
                            END IF;
                        ELSIF OLD."Status" = 'Running' AND NEW."Status" IN ('Completed','Failed') THEN
                            IF (new_data - ARRAY['Status','CandidateCount','NoticeCount','SuppressedCount','FailedCount',
                                    'LeaseOwner','LeaseToken','LeaseUntil','CompletedOn','CompletionEvidenceReference',
                                    'FailureReason','FailureEvidenceReference','Version'])
                               IS DISTINCT FROM
                               (old_data - ARRAY['Status','CandidateCount','NoticeCount','SuppressedCount','FailedCount',
                                    'LeaseOwner','LeaseToken','LeaseUntil','CompletedOn','CompletionEvidenceReference',
                                    'FailureReason','FailureEvidenceReference','Version'])
                               OR OLD."LeaseUntil" < (clock_timestamp() AT TIME ZONE 'UTC') THEN
                                RAISE EXCEPTION 'invalid dunning run completion' USING ERRCODE = '55000';
                            END IF;
                        ELSE
                            RAISE EXCEPTION 'invalid or immutable dunning run transition' USING ERRCODE = '55000';
                        END IF;
                    END IF;
                    RETURN NEW;
                END
                $function$;

                CREATE OR REPLACE FUNCTION public.nexora_ar_evidence_event()
                RETURNS trigger LANGUAGE plpgsql SECURITY DEFINER
                SET search_path = pg_catalog, public AS $function$
                DECLARE row_data jsonb := to_jsonb(NEW);
                DECLARE prior_data jsonb := CASE WHEN TG_OP = 'UPDATE' THEN to_jsonb(OLD) ELSE '{}'::jsonb END;
                DECLARE aggregate_type text;
                DECLARE aggregate_version bigint;
                DECLARE event_action text;
                DECLARE event_actor text;
                DECLARE event_time timestamp without time zone := now();
                BEGIN
                    IF TG_TABLE_NAME = 'DunningNotices' AND TG_OP = 'UPDATE'
                       AND row_data->>'Status' IN ('Delivered','Failed') THEN
                        IF NOT EXISTS (
                            SELECT 1 FROM public."DunningDeliveryAttempts" attempt
                            WHERE attempt."BusinessUnitId" = NEW."BusinessUnitId"
                              AND attempt."DunningNoticeId" = NEW."Id"
                              AND attempt."Status" = row_data->>'Status'
                              AND attempt."ProviderReference" = row_data->>'ProviderReference'
                              AND attempt."ArtifactHash" = row_data->>'ArtifactHash'
                              AND attempt."TemplateVersion" = row_data->>'TemplateVersion'
                              AND attempt."FailureCode" IS NOT DISTINCT FROM row_data->>'FailureCode'
                              AND attempt."OccurredOn" >= (row_data->>'ReleasedOn')::timestamp
                              AND attempt."ProviderOccurredOn" >= (row_data->>'ReleasedOn')::timestamp) THEN
                            RAISE EXCEPTION 'a terminal notice requires matching immutable delivery evidence'
                                USING ERRCODE = '23514';
                        END IF;
                    END IF;
                    aggregate_type := CASE TG_TABLE_NAME
                        WHEN 'FinanceCommunicationContacts' THEN 'FinanceCommunicationContact'
                        WHEN 'CustomerStatements' THEN 'CustomerStatement'
                        WHEN 'CustomerStatementLines' THEN 'CustomerStatementLine'
                        WHEN 'DunningPolicies' THEN 'DunningPolicy'
                        WHEN 'DunningPolicySteps' THEN 'DunningPolicyStep'
                        WHEN 'CustomerCollectionProfiles' THEN 'CustomerCollectionProfile'
                        WHEN 'CollectionControls' THEN 'CollectionControl'
                        WHEN 'DunningCases' THEN 'DunningCase'
                        WHEN 'PromisesToPay' THEN 'PromiseToPay'
                        WHEN 'DunningRuns' THEN 'DunningRun'
                        WHEN 'DunningRunDecisions' THEN 'DunningRunDecision'
                        WHEN 'DunningNotices' THEN 'DunningNotice'
                        ELSE 'DunningDeliveryAttempt' END;
                    aggregate_version := COALESCE(NULLIF(row_data->>'Version','')::bigint, 1);
                    event_action := CASE WHEN TG_OP = 'INSERT' THEN 'Created'
                        ELSE COALESCE(row_data->>'Status', 'Updated') END;
                    IF TG_TABLE_NAME = 'DunningRunDecisions' THEN
                        SELECT r."CreatedBy" INTO event_actor FROM public."DunningRuns" r
                         WHERE r."BusinessUnitId" = NEW."BusinessUnitId" AND r."Id" = NEW."DunningRunId";
                    ELSIF TG_OP = 'INSERT' THEN
                        event_actor := COALESCE(row_data->>'RecordedBy', row_data->>'CreatedBy', 'database');
                    ELSE
                        event_actor := CASE TG_TABLE_NAME
                            WHEN 'FinanceCommunicationContacts' THEN row_data->>'DeactivatedBy'
                            WHEN 'CustomerStatements' THEN CASE row_data->>'Status'
                                WHEN 'Finalized' THEN row_data->>'FinalizedBy'
                                WHEN 'Cancelled' THEN row_data->>'CancelledBy'
                                WHEN 'Superseded' THEN (
                                    SELECT successor."FinalizedBy" FROM public."CustomerStatements" successor
                                    WHERE successor."BusinessUnitId" = NEW."BusinessUnitId"
                                      AND successor."SupersedesStatementId" = NEW."Id"
                                      AND successor."Status" = 'Finalized'
                                    ORDER BY successor."Revision" DESC LIMIT 1)
                                ELSE NULL END
                            WHEN 'DunningPolicies' THEN CASE row_data->>'Status'
                                WHEN 'Approved' THEN row_data->>'ApprovedBy'
                                WHEN 'Active' THEN row_data->>'ActivatedBy'
                                WHEN 'Retired' THEN row_data->>'RetiredBy' ELSE NULL END
                            WHEN 'CustomerCollectionProfiles' THEN row_data->>'ModifiedBy'
                            WHEN 'CollectionControls' THEN row_data->>'ResolvedBy'
                            WHEN 'DunningCases' THEN row_data->>'UpdatedBy'
                            WHEN 'PromisesToPay' THEN row_data->>'ClosedBy'
                            WHEN 'DunningRuns' THEN CASE row_data->>'Status'
                                WHEN 'Running' THEN row_data->>'LeaseOwner'
                                ELSE COALESCE(prior_data->>'LeaseOwner', row_data->>'CreatedBy') END
                            WHEN 'DunningNotices' THEN CASE row_data->>'Status'
                                WHEN 'Approved' THEN row_data->>'ApprovedBy'
                                WHEN 'Released' THEN row_data->>'ReleasedBy'
                                WHEN 'Delivered' THEN row_data->>'DeliveryUpdatedBy'
                                WHEN 'Failed' THEN row_data->>'DeliveryUpdatedBy'
                                WHEN 'Cancelled' THEN row_data->>'CancelledBy' ELSE NULL END
                            ELSE COALESCE(row_data->>'RecordedBy', row_data->>'CreatedBy') END;
                    END IF;
                    event_actor := COALESCE(event_actor, 'database');
                    INSERT INTO public."CommercialFinanceAudits"
                        ("BusinessUnitId", "AggregateType", "AggregateId", "Action", "Actor", "OccurredOn", "DetailJson")
                    VALUES (NEW."BusinessUnitId", aggregate_type, NEW."Id", event_action, event_actor,
                        event_time, jsonb_build_object('id', NEW."Id", 'status', row_data->>'Status',
                            'version', aggregate_version, 'evidenceFingerprint', md5(row_data::text)));
                    PERFORM public.nexora_write_finance_outbox(NEW."BusinessUnitId", aggregate_type,
                        NEW."Id", aggregate_version, 'finance.receivables.' || lower(TG_TABLE_NAME) || '.' || lower(event_action),
                        jsonb_build_object('Id', NEW."Id", 'Status', row_data->>'Status',
                            'Version', aggregate_version, 'Actor', event_actor), event_time);
                    RETURN NEW;
                END
                $function$;

                CREATE OR REPLACE FUNCTION public.nexora_ar_reconcile_kept_promise_payment()
                RETURNS trigger LANGUAGE plpgsql SECURITY DEFINER
                SET search_path = pg_catalog, public AS $function$
                DECLARE row_data jsonb := to_jsonb(NEW);
                DECLARE prior_data jsonb := to_jsonb(OLD);
                DECLARE business_unit_id bigint := (to_jsonb(NEW)->>'BusinessUnitId')::bigint;
                DECLARE accounting_actor text;
                DECLARE accounting_time timestamp without time zone;
                DECLARE payment_id bigint;
                BEGIN
                    IF TG_TABLE_NAME = 'CustomerPayments'
                       AND (row_data->>'Status' = 'Reversed' OR row_data->>'ReversedOn' IS NOT NULL)
                       AND (prior_data->>'Status' IS DISTINCT FROM row_data->>'Status'
                            OR prior_data->>'ReversedOn' IS DISTINCT FROM row_data->>'ReversedOn')
                    THEN
                        accounting_actor := COALESCE(NULLIF(current_setting('nexora.actor_id', true), ''),
                            row_data->>'ReversedBy', 'payment-reversal');
                        accounting_time := COALESCE((row_data->>'ReversedOn')::timestamp, now());
                        payment_id := (row_data->>'Id')::bigint;
                    ELSIF TG_TABLE_NAME = 'CustomerRefunds'
                       AND row_data->>'Status' = 'Released' AND row_data->>'ReversedOn' IS NULL
                       AND (prior_data->>'Status' IS DISTINCT FROM row_data->>'Status'
                            OR prior_data->>'ReleasedOn' IS DISTINCT FROM row_data->>'ReleasedOn')
                    THEN
                        payment_id := (row_data->>'SourcePaymentId')::bigint;
                        IF NOT EXISTS (
                            SELECT 1 FROM public."PromisesToPay" promise
                            JOIN public."CustomerPayments" payment
                              ON payment."BusinessUnitId" = promise."BusinessUnitId" AND payment."Id" = promise."MatchedPaymentId"
                            WHERE promise."BusinessUnitId" = business_unit_id AND promise."MatchedPaymentId" = payment_id
                              AND promise."Status" = 'Kept'
                              AND payment."Amount" - (COALESCE((SELECT SUM(existing."Amount")
                                  FROM public."CustomerRefunds" existing
                                  WHERE existing."BusinessUnitId" = business_unit_id
                                    AND existing."SourcePaymentId" = payment_id
                                    AND existing."Id" <> (row_data->>'Id')::bigint
                                    AND existing."Status" = 'Released' AND existing."ReversedOn" IS NULL), 0)
                                  + (row_data->>'Amount')::numeric) < promise."Amount") THEN
                            RETURN NEW;
                        END IF;
                        accounting_actor := COALESCE(NULLIF(current_setting('nexora.actor_id', true), ''),
                            row_data->>'ReleasedBy', 'refund-release');
                        accounting_time := COALESCE((row_data->>'ReleasedOn')::timestamp, now());
                    END IF;
                    IF payment_id IS NOT NULL THEN
                        UPDATE public."PromisesToPay" promise
                        SET "Status" = 'Broken', "ClosedBy" = accounting_actor, "ClosedOn" = accounting_time,
                            "ClosureEvidenceReference" = 'payment-accounting-reversal:' || TG_TABLE_NAME || ':' || (row_data->>'Id'),
                            "MatchedPaymentId" = NULL, "MatchedAmount" = NULL, "Version" = "Version" + 1
                        WHERE promise."BusinessUnitId" = business_unit_id
                          AND promise."MatchedPaymentId" = payment_id AND promise."Status" = 'Kept';
                    END IF;
                    RETURN NEW;
                END
                $function$;

                DROP TRIGGER IF EXISTS trg_customerpayments_protect_kept_promise ON public."CustomerPayments";
                CREATE TRIGGER trg_customerpayments_protect_kept_promise
                    BEFORE UPDATE ON public."CustomerPayments" FOR EACH ROW
                    EXECUTE FUNCTION public.nexora_ar_reconcile_kept_promise_payment();
                DROP TRIGGER IF EXISTS trg_customerrefunds_protect_kept_promise ON public."CustomerRefunds";
                CREATE TRIGGER trg_customerrefunds_protect_kept_promise
                    BEFORE UPDATE ON public."CustomerRefunds" FOR EACH ROW
                    EXECUTE FUNCTION public.nexora_ar_reconcile_kept_promise_payment();

                DO $block$
                DECLARE governed_table text;
                BEGIN
                    FOREACH governed_table IN ARRAY ARRAY[
                        'FinanceCommunicationContacts','CustomerStatements','CustomerStatementLines',
                        'DunningPolicies','DunningPolicySteps','CustomerCollectionProfiles','CollectionControls',
                        'DunningCases','PromisesToPay','DunningRuns','DunningNotices','DunningDeliveryAttempts',
                        'DunningRunDecisions']
                    LOOP
                        EXECUTE format('ALTER TABLE public.%I ENABLE ROW LEVEL SECURITY', governed_table);
                        EXECUTE format('ALTER TABLE public.%I FORCE ROW LEVEL SECURITY', governed_table);
                        EXECUTE format('DROP POLICY IF EXISTS nexora_tenant_isolation ON public.%I', governed_table);
                        EXECUTE format(
                            'CREATE POLICY nexora_tenant_isolation ON public.%I TO nexora_tenant_app USING ("BusinessUnitId" = NULLIF(current_setting(''nexora.business_unit_id'', true), '''')::bigint) WITH CHECK ("BusinessUnitId" = NULLIF(current_setting(''nexora.business_unit_id'', true), '''')::bigint)',
                            governed_table);
                        EXECUTE format('CREATE TRIGGER trg_%s_tenant_reference BEFORE INSERT OR UPDATE ON public.%I FOR EACH ROW EXECUTE FUNCTION public.nexora_ar_validate_tenant_reference()', lower(governed_table), governed_table);
                        EXECUTE format('CREATE TRIGGER trg_%s_governed BEFORE INSERT OR UPDATE OR DELETE ON public.%I FOR EACH ROW EXECUTE FUNCTION public.nexora_ar_governed_mutation()', lower(governed_table), governed_table);
                        EXECUTE format('CREATE CONSTRAINT TRIGGER trg_%s_evidence AFTER INSERT OR UPDATE ON public.%I DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION public.nexora_ar_evidence_event()', lower(governed_table), governed_table);
                        EXECUTE format('CREATE TRIGGER trg_%s_reject_truncate BEFORE TRUNCATE ON public.%I FOR EACH STATEMENT EXECUTE FUNCTION public.nexora_finance_reject_truncate()', lower(governed_table), governed_table);
                    END LOOP;
                    IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'nexora_tenant_app') THEN
                        GRANT SELECT, INSERT, UPDATE ON public."FinanceCommunicationContacts", public."CustomerStatements",
                            public."DunningPolicies", public."CustomerCollectionProfiles", public."CollectionControls",
                            public."DunningCases", public."PromisesToPay", public."DunningRuns", public."DunningNotices" TO nexora_tenant_app;
                        GRANT SELECT, INSERT ON public."CustomerStatementLines", public."DunningPolicySteps",
                            public."DunningDeliveryAttempts", public."DunningRunDecisions" TO nexora_tenant_app;
                        REVOKE DELETE, TRUNCATE ON public."FinanceCommunicationContacts", public."CustomerStatements",
                            public."CustomerStatementLines", public."DunningPolicies", public."DunningPolicySteps",
                            public."CustomerCollectionProfiles", public."CollectionControls", public."DunningCases",
                            public."PromisesToPay", public."DunningRuns", public."DunningNotices",
                            public."DunningDeliveryAttempts", public."DunningRunDecisions" FROM nexora_tenant_app;
                        GRANT USAGE ON SEQUENCE public."FinanceCommunicationContacts_Id_seq",
                            public."CustomerStatements_Id_seq", public."CustomerStatementLines_Id_seq",
                            public."DunningPolicies_Id_seq", public."DunningPolicySteps_Id_seq",
                            public."CustomerCollectionProfiles_Id_seq", public."CollectionControls_Id_seq",
                            public."DunningCases_Id_seq", public."PromisesToPay_Id_seq",
                            public."DunningRuns_Id_seq", public."DunningNotices_Id_seq",
                            public."DunningDeliveryAttempts_Id_seq", public."DunningRunDecisions_Id_seq" TO nexora_tenant_app;
                    END IF;
                END
                $block$;

                INSERT INTO public."Module" ("ModuleName", "Description", "IsActive", "CreatedBy", "CreatedOn")
                VALUES
                    ('Customer Statements', 'Immutable governed customer statement snapshots and corrections', true, 'migration:statements-dunning:v1', now()),
                    ('Dunning Policies', 'Approved collections policy versions and customer profiles', true, 'migration:statements-dunning:v1', now()),
                    ('Collection Controls', 'Disputes, communication restrictions and legal holds', true, 'migration:statements-dunning:v1', now()),
                    ('Dunning Cases', 'Governed collection cases and promises to pay', true, 'migration:statements-dunning:v1', now()),
                    ('Dunning Notices', 'Maker-checker collection notices and delivery evidence', true, 'migration:statements-dunning:v1', now())
                ON CONFLICT ("ModuleName") DO NOTHING;

                INSERT INTO public."RolePermissions"
                    ("RoleID", "ModuleID", "BusinessUnitID", "CanCreate", "CanEdit", "CanDelete", "CreatedBy", "CreatedOn")
                SELECT role."SetupID", module."ID", role."BusinessUnitID", true, true, false,
                       'migration:statements-dunning:v1', now()
                FROM public."Setup_Master" role CROSS JOIN public."Module" module
                WHERE lower(replace(role."SetupType", ' ', '')) = 'role'
                  AND module."ModuleName" IN ('Customer Statements','Dunning Policies','Collection Controls','Dunning Cases','Dunning Notices')
                  AND (upper(coalesce(role."SetupCode", '')) ~ '(FINANCE|ACCOUNT|ADMIN)'
                    OR upper(coalesce(role."SetupValue", '')) ~ '(FINANCE|ACCOUNT|ADMIN)')
                  AND NOT EXISTS (SELECT 1 FROM public."RolePermissions" existing
                    WHERE existing."RoleID" = role."SetupID" AND existing."BusinessUnitID" = role."BusinessUnitID"
                      AND existing."ModuleID" = module."ID");

                REVOKE ALL ON FUNCTION public.nexora_ar_validate_tenant_reference() FROM PUBLIC;
                REVOKE ALL ON FUNCTION public.nexora_ar_governed_mutation() FROM PUBLIC;
                REVOKE ALL ON FUNCTION public.nexora_ar_evidence_event() FROM PUBLIC;
                REVOKE ALL ON FUNCTION public.nexora_ar_reconcile_kept_promise_payment() FROM PUBLIC;
                GRANT EXECUTE ON FUNCTION public.nexora_ar_validate_tenant_reference(),
                    public.nexora_ar_governed_mutation(), public.nexora_ar_evidence_event(),
                    public.nexora_ar_reconcile_kept_promise_payment() TO nexora_tenant_app;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DELETE FROM public."RolePermissions" WHERE "CreatedBy" = 'migration:statements-dunning:v1';
                DROP TRIGGER IF EXISTS trg_customerpayments_protect_kept_promise ON public."CustomerPayments";
                DROP TRIGGER IF EXISTS trg_customerrefunds_protect_kept_promise ON public."CustomerRefunds";
                DROP FUNCTION IF EXISTS public.nexora_ar_reconcile_kept_promise_payment();
                DELETE FROM public."Module" WHERE "CreatedBy" = 'migration:statements-dunning:v1';
                DROP FUNCTION IF EXISTS public.nexora_ar_evidence_event() CASCADE;
                DROP FUNCTION IF EXISTS public.nexora_ar_governed_mutation() CASCADE;
                DROP FUNCTION IF EXISTS public.nexora_ar_validate_tenant_reference() CASCADE;
                DROP TABLE IF EXISTS public."FinanceProviderSecrets";
                """);
            migrationBuilder.DropTable(
                name: "DunningRunDecisions");

            migrationBuilder.DropTable(
                name: "CollectionControls");

            migrationBuilder.DropTable(
                name: "CustomerCollectionProfiles");

            migrationBuilder.DropTable(
                name: "CustomerStatementLines");

            migrationBuilder.DropTable(
                name: "DunningDeliveryAttempts");

            migrationBuilder.DropTable(
                name: "DunningPolicySteps");

            migrationBuilder.DropTable(
                name: "DunningRuns");

            migrationBuilder.DropTable(
                name: "PromisesToPay");

            migrationBuilder.DropTable(
                name: "DunningNotices");

            migrationBuilder.DropTable(
                name: "DunningCases");

            migrationBuilder.DropTable(
                name: "FinanceCommunicationContacts");

            migrationBuilder.DropTable(
                name: "CustomerStatements");

            migrationBuilder.DropTable(
                name: "DunningPolicies");
        }
    }
}
