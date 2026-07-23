using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace ERP_RFQ_Automation.Migrations
{
    /// <inheritdoc />
    public partial class GovernBankReconciliation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddUniqueConstraint(
                name: "AK_JournalEntryLines_BusinessUnitId_Id",
                table: "JournalEntryLines",
                columns: new[] { "BusinessUnitId", "Id" });

            migrationBuilder.CreateTable(
                name: "BankAccounts",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    BusinessUnitId = table.Column<long>(type: "bigint", nullable: false),
                    Name = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    InstitutionName = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    MaskedAccountNumber = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    AccountFingerprint = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    CurrencyId = table.Column<long>(type: "bigint", nullable: false),
                    LedgerAccountId = table.Column<long>(type: "bigint", nullable: false),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    OpeningDate = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    IdempotencyKey = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    RequestHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false),
                    CreatedBy = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    CreatedOn = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    StatusChangedBy = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    StatusChangedOn = table.Column<DateTime>(type: "timestamp without time zone", nullable: true),
                    StatusReason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BankAccounts", x => x.Id);
                    table.UniqueConstraint("AK_BankAccounts_BusinessUnitId_Id", x => new { x.BusinessUnitId, x.Id });
                    table.UniqueConstraint("AK_BankAccounts_BusinessUnitId_Id_CurrencyId", x => new { x.BusinessUnitId, x.Id, x.CurrencyId });
                    table.CheckConstraint("CK_BankAccounts_Status", "\"Status\" IN ('Active','Suspended','Closed') AND \"Version\" > 0");
                    table.ForeignKey(
                        name: "FK_BankAccounts_Currency_BusinessUnitId_CurrencyId",
                        columns: x => new { x.BusinessUnitId, x.CurrencyId },
                        principalTable: "Currency",
                        principalColumns: new[] { "BusinessUnitID", "ID" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_BankAccounts_LedgerAccounts_BusinessUnitId_LedgerAccountId",
                        columns: x => new { x.BusinessUnitId, x.LedgerAccountId },
                        principalTable: "LedgerAccounts",
                        principalColumns: new[] { "BusinessUnitId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "BankStatementImports",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    BusinessUnitId = table.Column<long>(type: "bigint", nullable: false),
                    BankAccountId = table.Column<long>(type: "bigint", nullable: false),
                    SourceType = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    OriginalFileName = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    RawObjectReference = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    SourceHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ParserVersion = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    IdempotencyKey = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    RequestHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ImportedBy = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    ImportedOn = table.Column<DateTime>(type: "timestamp without time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BankStatementImports", x => x.Id);
                    table.UniqueConstraint("AK_BankStatementImports_BusinessUnitId_Id", x => new { x.BusinessUnitId, x.Id });
                    table.UniqueConstraint("AK_BankStatementImports_BusinessUnitId_Id_BankAccountId", x => new { x.BusinessUnitId, x.Id, x.BankAccountId });
                    table.CheckConstraint("CK_BankStatementImports_Status", "\"Status\" IN ('Validated','Rejected')");
                    table.ForeignKey(
                        name: "FK_BankStatementImports_BankAccounts_BusinessUnitId_BankAccoun~",
                        columns: x => new { x.BusinessUnitId, x.BankAccountId },
                        principalTable: "BankAccounts",
                        principalColumns: new[] { "BusinessUnitId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "BankStatements",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    BusinessUnitId = table.Column<long>(type: "bigint", nullable: false),
                    BankStatementImportId = table.Column<long>(type: "bigint", nullable: false),
                    BankAccountId = table.Column<long>(type: "bigint", nullable: false),
                    CurrencyId = table.Column<long>(type: "bigint", nullable: false),
                    StatementReference = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    PeriodStart = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    PeriodEnd = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    OpeningBalance = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    ClosingBalance = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    CalculatedClosingBalance = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    ContentHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BankStatements", x => x.Id);
                    table.UniqueConstraint("AK_BankStatements_BusinessUnitId_Id", x => new { x.BusinessUnitId, x.Id });
                    table.UniqueConstraint("AK_BankStatements_BusinessUnitId_Id_BankAccountId", x => new { x.BusinessUnitId, x.Id, x.BankAccountId });
                    table.CheckConstraint("CK_BankStatements_Balance", "\"CalculatedClosingBalance\" = \"ClosingBalance\"");
                    table.CheckConstraint("CK_BankStatements_Period", "\"PeriodStart\" <= \"PeriodEnd\" AND \"Version\" > 0");
                    table.ForeignKey(
                        name: "FK_BankStatements_BankAccounts_BusinessUnitId_BankAccountId_Cu~",
                        columns: x => new { x.BusinessUnitId, x.BankAccountId, x.CurrencyId },
                        principalTable: "BankAccounts",
                        principalColumns: new[] { "BusinessUnitId", "Id", "CurrencyId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_BankStatements_BankStatementImports_BusinessUnitId_BankStat~",
                        columns: x => new { x.BusinessUnitId, x.BankStatementImportId, x.BankAccountId },
                        principalTable: "BankStatementImports",
                        principalColumns: new[] { "BusinessUnitId", "Id", "BankAccountId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_BankStatements_Currency_BusinessUnitId_CurrencyId",
                        columns: x => new { x.BusinessUnitId, x.CurrencyId },
                        principalTable: "Currency",
                        principalColumns: new[] { "BusinessUnitID", "ID" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "BankStatementLines",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    BusinessUnitId = table.Column<long>(type: "bigint", nullable: false),
                    BankStatementId = table.Column<long>(type: "bigint", nullable: false),
                    BankAccountId = table.Column<long>(type: "bigint", nullable: false),
                    SourceOrdinal = table.Column<int>(type: "integer", nullable: false),
                    BookingDate = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    ValueDate = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    SignedAmount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    Direction = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    OriginalAmountText = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    ExternalTransactionId = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    BankReference = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    TransactionCode = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: true),
                    Counterparty = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    RemittanceText = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    NormalizedReference = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    LineFingerprint = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BankStatementLines", x => x.Id);
                    table.UniqueConstraint("AK_BankStatementLines_BusinessUnitId_Id", x => new { x.BusinessUnitId, x.Id });
                    table.CheckConstraint("CK_BankStatementLines_Amount", "\"SourceOrdinal\" > 0 AND \"SignedAmount\" <> 0 AND ((\"SignedAmount\" > 0 AND \"Direction\" = 'Credit') OR (\"SignedAmount\" < 0 AND \"Direction\" = 'Debit'))");
                    table.CheckConstraint("CK_BankStatementLines_Dates", "\"BookingDate\" <= \"ValueDate\"");
                    table.ForeignKey(
                        name: "FK_BankStatementLines_BankStatements_BusinessUnitId_BankStatem~",
                        columns: x => new { x.BusinessUnitId, x.BankStatementId, x.BankAccountId },
                        principalTable: "BankStatements",
                        principalColumns: new[] { "BusinessUnitId", "Id", "BankAccountId" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ReconciliationRuns",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    BusinessUnitId = table.Column<long>(type: "bigint", nullable: false),
                    BankAccountId = table.Column<long>(type: "bigint", nullable: false),
                    BankStatementId = table.Column<long>(type: "bigint", nullable: false),
                    ReconciliationThrough = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    BankClosingBalance = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    BookClosingBalance = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    MatchedAmount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    UnexplainedDifference = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    IdempotencyKey = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    RequestHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false),
                    PreparedBy = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    PreparedOn = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    SubmittedBy = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    SubmittedOn = table.Column<DateTime>(type: "timestamp without time zone", nullable: true),
                    ApprovedBy = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    ApprovedOn = table.Column<DateTime>(type: "timestamp without time zone", nullable: true),
                    ApprovalReason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    EvidenceReference = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    CertificateHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    CertificateLineCount = table.Column<int>(type: "integer", nullable: true),
                    CertificateJournalCount = table.Column<int>(type: "integer", nullable: true),
                    ReopenedBy = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    ReopenedOn = table.Column<DateTime>(type: "timestamp without time zone", nullable: true),
                    ReopenReason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    ReopenEvidenceReference = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ReconciliationRuns", x => x.Id);
                    table.UniqueConstraint("AK_ReconciliationRuns_BusinessUnitId_Id", x => new { x.BusinessUnitId, x.Id });
                    table.CheckConstraint("CK_ReconciliationRuns_Certificate", "(\"Status\" = 'Approved' AND \"ApprovedBy\" IS NOT NULL AND \"ApprovedOn\" IS NOT NULL AND \"CertificateHash\" IS NOT NULL AND \"CertificateLineCount\" IS NOT NULL AND \"CertificateJournalCount\" IS NOT NULL AND \"UnexplainedDifference\" = 0) OR (\"Status\" <> 'Approved')");
                    table.CheckConstraint("CK_ReconciliationRuns_Status", "\"Status\" IN ('Draft','InReview','Approved','Reopened') AND \"Version\" > 0");
                    table.ForeignKey(
                        name: "FK_ReconciliationRuns_BankAccounts_BusinessUnitId_BankAccountId",
                        columns: x => new { x.BusinessUnitId, x.BankAccountId },
                        principalTable: "BankAccounts",
                        principalColumns: new[] { "BusinessUnitId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ReconciliationRuns_BankStatements_BusinessUnitId_BankStatem~",
                        columns: x => new { x.BusinessUnitId, x.BankStatementId, x.BankAccountId },
                        principalTable: "BankStatements",
                        principalColumns: new[] { "BusinessUnitId", "Id", "BankAccountId" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ReconciliationMatches",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    BusinessUnitId = table.Column<long>(type: "bigint", nullable: false),
                    ReconciliationRunId = table.Column<long>(type: "bigint", nullable: false),
                    MatchType = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    Confidence = table.Column<decimal>(type: "numeric(5,4)", precision: 5, scale: 4, nullable: false),
                    RuleCode = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    RuleVersion = table.Column<int>(type: "integer", nullable: false),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    IdempotencyKey = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    RequestHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false),
                    CreatedBy = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    CreatedOn = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    ConfirmedBy = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    ConfirmedOn = table.Column<DateTime>(type: "timestamp without time zone", nullable: true),
                    VoidedBy = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    VoidedOn = table.Column<DateTime>(type: "timestamp without time zone", nullable: true),
                    VoidReason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ReconciliationMatches", x => x.Id);
                    table.UniqueConstraint("AK_ReconciliationMatches_BusinessUnitId_Id", x => new { x.BusinessUnitId, x.Id });
                    table.CheckConstraint("CK_ReconciliationMatches_State", "\"Status\" IN ('Proposed','Confirmed','Voided') AND \"Version\" > 0 AND \"RuleVersion\" > 0 AND \"Confidence\" >= 0 AND \"Confidence\" <= 1");
                    table.ForeignKey(
                        name: "FK_ReconciliationMatches_ReconciliationRuns_BusinessUnitId_Rec~",
                        columns: x => new { x.BusinessUnitId, x.ReconciliationRunId },
                        principalTable: "ReconciliationRuns",
                        principalColumns: new[] { "BusinessUnitId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ReconciliationAllocations",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    BusinessUnitId = table.Column<long>(type: "bigint", nullable: false),
                    ReconciliationMatchId = table.Column<long>(type: "bigint", nullable: false),
                    BankStatementLineId = table.Column<long>(type: "bigint", nullable: false),
                    JournalEntryLineId = table.Column<long>(type: "bigint", nullable: false),
                    BankAmount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    FunctionalAmount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ReconciliationAllocations", x => x.Id);
                    table.CheckConstraint("CK_ReconciliationAllocations_Amounts", "\"BankAmount\" > 0 AND \"FunctionalAmount\" > 0");
                    table.ForeignKey(
                        name: "FK_ReconciliationAllocations_BankStatementLines_BusinessUnitId~",
                        columns: x => new { x.BusinessUnitId, x.BankStatementLineId },
                        principalTable: "BankStatementLines",
                        principalColumns: new[] { "BusinessUnitId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ReconciliationAllocations_JournalEntryLines_BusinessUnitId_~",
                        columns: x => new { x.BusinessUnitId, x.JournalEntryLineId },
                        principalTable: "JournalEntryLines",
                        principalColumns: new[] { "BusinessUnitId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ReconciliationAllocations_ReconciliationMatches_BusinessUni~",
                        columns: x => new { x.BusinessUnitId, x.ReconciliationMatchId },
                        principalTable: "ReconciliationMatches",
                        principalColumns: new[] { "BusinessUnitId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_BankAccounts_BusinessUnitId_CurrencyId",
                table: "BankAccounts",
                columns: new[] { "BusinessUnitId", "CurrencyId" });

            migrationBuilder.CreateIndex(
                name: "IX_BankAccounts_BusinessUnitId_LedgerAccountId",
                table: "BankAccounts",
                columns: new[] { "BusinessUnitId", "LedgerAccountId" });

            migrationBuilder.CreateIndex(
                name: "UX_BankAccounts_BU_Fingerprint",
                table: "BankAccounts",
                columns: new[] { "BusinessUnitId", "AccountFingerprint" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UX_BankAccounts_BU_Idempotency",
                table: "BankAccounts",
                columns: new[] { "BusinessUnitId", "IdempotencyKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UX_BankImports_BU_Account_SourceHash",
                table: "BankStatementImports",
                columns: new[] { "BusinessUnitId", "BankAccountId", "SourceHash" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UX_BankImports_BU_Idempotency",
                table: "BankStatementImports",
                columns: new[] { "BusinessUnitId", "IdempotencyKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_BankStatementLines_BusinessUnitId_BankStatementId_BankAccou~",
                table: "BankStatementLines",
                columns: new[] { "BusinessUnitId", "BankStatementId", "BankAccountId" });

            migrationBuilder.CreateIndex(
                name: "UX_BankLines_BU_Account_ExternalId",
                table: "BankStatementLines",
                columns: new[] { "BusinessUnitId", "BankAccountId", "ExternalTransactionId" },
                unique: true,
                filter: "\"ExternalTransactionId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "UX_BankLines_BU_Account_Fingerprint",
                table: "BankStatementLines",
                columns: new[] { "BusinessUnitId", "BankAccountId", "LineFingerprint" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UX_BankLines_BU_Statement_Ordinal",
                table: "BankStatementLines",
                columns: new[] { "BusinessUnitId", "BankStatementId", "SourceOrdinal" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_BankStatements_BusinessUnitId_BankAccountId_CurrencyId",
                table: "BankStatements",
                columns: new[] { "BusinessUnitId", "BankAccountId", "CurrencyId" });

            migrationBuilder.CreateIndex(
                name: "IX_BankStatements_BusinessUnitId_BankStatementImportId_BankAcc~",
                table: "BankStatements",
                columns: new[] { "BusinessUnitId", "BankStatementImportId", "BankAccountId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_BankStatements_BusinessUnitId_CurrencyId",
                table: "BankStatements",
                columns: new[] { "BusinessUnitId", "CurrencyId" });

            migrationBuilder.CreateIndex(
                name: "UX_BankStatements_BU_Account_Reference",
                table: "BankStatements",
                columns: new[] { "BusinessUnitId", "BankAccountId", "StatementReference" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ReconciliationAllocations_BusinessUnitId_BankStatementLineId",
                table: "ReconciliationAllocations",
                columns: new[] { "BusinessUnitId", "BankStatementLineId" });

            migrationBuilder.CreateIndex(
                name: "IX_ReconciliationAllocations_BusinessUnitId_JournalEntryLineId",
                table: "ReconciliationAllocations",
                columns: new[] { "BusinessUnitId", "JournalEntryLineId" });

            migrationBuilder.CreateIndex(
                name: "UX_ReconciliationAllocations_Evidence",
                table: "ReconciliationAllocations",
                columns: new[] { "BusinessUnitId", "ReconciliationMatchId", "BankStatementLineId", "JournalEntryLineId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ReconciliationMatches_BusinessUnitId_ReconciliationRunId",
                table: "ReconciliationMatches",
                columns: new[] { "BusinessUnitId", "ReconciliationRunId" });

            migrationBuilder.CreateIndex(
                name: "UX_ReconciliationMatches_BU_Idempotency",
                table: "ReconciliationMatches",
                columns: new[] { "BusinessUnitId", "IdempotencyKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ReconciliationRuns_BusinessUnitId_BankAccountId",
                table: "ReconciliationRuns",
                columns: new[] { "BusinessUnitId", "BankAccountId" });

            migrationBuilder.CreateIndex(
                name: "IX_ReconciliationRuns_BusinessUnitId_BankStatementId_BankAccou~",
                table: "ReconciliationRuns",
                columns: new[] { "BusinessUnitId", "BankStatementId", "BankAccountId" });

            migrationBuilder.CreateIndex(
                name: "UX_ReconciliationRuns_BU_ActiveStatement",
                table: "ReconciliationRuns",
                columns: new[] { "BusinessUnitId", "BankStatementId" },
                unique: true,
                filter: "\"Status\" <> 'Reopened'");

            migrationBuilder.CreateIndex(
                name: "UX_ReconciliationRuns_BU_Idempotency",
                table: "ReconciliationRuns",
                columns: new[] { "BusinessUnitId", "IdempotencyKey" },
                unique: true);

            migrationBuilder.Sql("""
                CREATE OR REPLACE FUNCTION public.nexora_bank_immutable_evidence()
                RETURNS trigger LANGUAGE plpgsql SET search_path = pg_catalog, public AS $function$
                BEGIN
                    RAISE EXCEPTION 'bank statement evidence is append-only' USING ERRCODE = '55000';
                END
                $function$;

                CREATE OR REPLACE FUNCTION public.nexora_bank_guard_account()
                RETURNS trigger LANGUAGE plpgsql SET search_path = pg_catalog, public AS $function$
                BEGIN
                    IF TG_OP = 'DELETE' THEN
                        RAISE EXCEPTION 'bank accounts cannot be deleted' USING ERRCODE = '55000';
                    END IF;
                    IF TG_OP = 'INSERT' THEN
                        IF NEW."Status" <> 'Active' OR NEW."Version" <> 1 THEN
                            RAISE EXCEPTION 'bank accounts must begin active at version one' USING ERRCODE = '23514';
                        END IF;
                        IF NOT EXISTS (SELECT 1 FROM public."LedgerBooks" book
                            WHERE book."BusinessUnitId" = NEW."BusinessUnitId"
                              AND book."FunctionalCurrencyId" = NEW."CurrencyId") THEN
                            RAISE EXCEPTION 'bank account currency must equal the accounting book functional currency' USING ERRCODE = '23514';
                        END IF;
                        RETURN NEW;
                    END IF;
                    IF NEW."BusinessUnitId" <> OLD."BusinessUnitId" OR NEW."Id" <> OLD."Id"
                       OR NEW."Name" <> OLD."Name" OR NEW."InstitutionName" <> OLD."InstitutionName"
                       OR NEW."MaskedAccountNumber" <> OLD."MaskedAccountNumber"
                       OR NEW."AccountFingerprint" <> OLD."AccountFingerprint"
                       OR NEW."CurrencyId" <> OLD."CurrencyId" OR NEW."LedgerAccountId" <> OLD."LedgerAccountId"
                       OR NEW."OpeningDate" <> OLD."OpeningDate" OR NEW."IdempotencyKey" <> OLD."IdempotencyKey"
                       OR NEW."RequestHash" <> OLD."RequestHash" OR NEW."CreatedBy" <> OLD."CreatedBy"
                       OR NEW."CreatedOn" <> OLD."CreatedOn" OR NEW."Version" <> OLD."Version" + 1
                       OR NEW."StatusChangedBy" IS NULL OR NEW."StatusChangedOn" IS NULL
                       OR length(trim(NEW."StatusReason")) < 10
                       OR NOT ((OLD."Status" = 'Active' AND NEW."Status" IN ('Suspended','Closed'))
                            OR (OLD."Status" = 'Suspended' AND NEW."Status" IN ('Active','Closed'))) THEN
                        RAISE EXCEPTION 'invalid governed bank-account transition' USING ERRCODE = '55000';
                    END IF;
                    RETURN NEW;
                END
                $function$;

                CREATE OR REPLACE FUNCTION public.nexora_bank_validate_match(match_id bigint)
                RETURNS void LANGUAGE plpgsql SET search_path = pg_catalog, public AS $function$
                DECLARE match_row record; DECLARE allocation_row record; DECLARE allocated numeric(18,2);
                BEGIN
                    SELECT m.*, r."BankStatementId", r."BankAccountId" INTO STRICT match_row
                    FROM public."ReconciliationMatches" m JOIN public."ReconciliationRuns" r
                      ON r."BusinessUnitId" = m."BusinessUnitId" AND r."Id" = m."ReconciliationRunId"
                    WHERE m."Id" = match_id FOR UPDATE OF m, r;
                    FOR allocation_row IN
                        SELECT a.*, line."SignedAmount", journal_line."FunctionalDebit", journal_line."FunctionalCredit",
                               journal_line."LedgerAccountId", journal."Status" AS journal_status,
                               journal."AccountingDate" AS journal_date,
                               account."LedgerAccountId" AS cash_account_id
                        FROM public."ReconciliationAllocations" a
                        JOIN public."BankStatementLines" line
                          ON line."BusinessUnitId" = a."BusinessUnitId" AND line."Id" = a."BankStatementLineId"
                        JOIN public."JournalEntryLines" journal_line
                          ON journal_line."BusinessUnitId" = a."BusinessUnitId" AND journal_line."Id" = a."JournalEntryLineId"
                        JOIN public."JournalEntries" journal
                          ON journal."BusinessUnitId" = journal_line."BusinessUnitId" AND journal."Id" = journal_line."JournalEntryId"
                        JOIN public."BankAccounts" account
                          ON account."BusinessUnitId" = a."BusinessUnitId" AND account."Id" = match_row."BankAccountId"
                        WHERE a."ReconciliationMatchId" = match_id ORDER BY a."BankStatementLineId", a."JournalEntryLineId"
                    LOOP
                        PERFORM 1 FROM public."BankStatementLines" WHERE "Id" = allocation_row."BankStatementLineId" FOR UPDATE;
                        PERFORM 1 FROM public."JournalEntryLines" WHERE "Id" = allocation_row."JournalEntryLineId" FOR UPDATE;
                        IF NOT EXISTS (SELECT 1 FROM public."BankStatementLines" line
                            WHERE line."Id" = allocation_row."BankStatementLineId"
                              AND line."BusinessUnitId" = match_row."BusinessUnitId"
                              AND line."BankStatementId" = match_row."BankStatementId")
                           OR allocation_row."LedgerAccountId" <> allocation_row.cash_account_id
                           OR allocation_row.journal_status <> 'Posted'
                           OR allocation_row.journal_date > (SELECT "ReconciliationThrough" FROM public."ReconciliationRuns" WHERE "Id" = match_row."ReconciliationRunId")
                           OR (allocation_row."SignedAmount" > 0 AND allocation_row."FunctionalDebit" <= 0)
                           OR (allocation_row."SignedAmount" < 0 AND allocation_row."FunctionalCredit" <= 0) THEN
                            RAISE EXCEPTION 'allocation evidence is not eligible for this reconciliation' USING ERRCODE = '23514';
                        END IF;
                        IF match_row."Status" = 'Confirmed' THEN
                            SELECT COALESCE(sum(a."BankAmount"),0) INTO allocated
                            FROM public."ReconciliationAllocations" a JOIN public."ReconciliationMatches" m
                              ON m."BusinessUnitId" = a."BusinessUnitId" AND m."Id" = a."ReconciliationMatchId"
                            WHERE a."BusinessUnitId" = match_row."BusinessUnitId"
                              AND a."BankStatementLineId" = allocation_row."BankStatementLineId" AND m."Status" = 'Confirmed';
                            IF allocated > abs(allocation_row."SignedAmount") THEN
                                RAISE EXCEPTION 'bank statement line is over-allocated' USING ERRCODE = '23514';
                            END IF;
                            SELECT COALESCE(sum(a."FunctionalAmount"),0) INTO allocated
                            FROM public."ReconciliationAllocations" a JOIN public."ReconciliationMatches" m
                              ON m."BusinessUnitId" = a."BusinessUnitId" AND m."Id" = a."ReconciliationMatchId"
                            WHERE a."BusinessUnitId" = match_row."BusinessUnitId"
                              AND a."JournalEntryLineId" = allocation_row."JournalEntryLineId" AND m."Status" = 'Confirmed';
                            IF (allocation_row."SignedAmount" > 0 AND allocated > allocation_row."FunctionalDebit")
                               OR (allocation_row."SignedAmount" < 0 AND allocated > allocation_row."FunctionalCredit") THEN
                                RAISE EXCEPTION 'journal line is over-allocated' USING ERRCODE = '23514';
                            END IF;
                        END IF;
                    END LOOP;
                END
                $function$;

                CREATE OR REPLACE FUNCTION public.nexora_bank_guard_match()
                RETURNS trigger LANGUAGE plpgsql SET search_path = pg_catalog, public AS $function$
                BEGIN
                    IF TG_OP = 'DELETE' THEN RAISE EXCEPTION 'reconciliation matches cannot be deleted' USING ERRCODE = '55000'; END IF;
                    IF TG_OP = 'INSERT' AND (NEW."Status" <> 'Proposed' OR NEW."Version" <> 1) THEN
                        RAISE EXCEPTION 'matches must begin proposed at version one' USING ERRCODE = '23514';
                    ELSIF TG_OP = 'INSERT' AND NOT EXISTS (SELECT 1 FROM public."ReconciliationRuns" run
                        WHERE run."BusinessUnitId" = NEW."BusinessUnitId" AND run."Id" = NEW."ReconciliationRunId"
                          AND run."Status" IN ('Draft','Reopened')) THEN
                        RAISE EXCEPTION 'matches can only be added to an editable reconciliation' USING ERRCODE = '55000';
                    ELSIF TG_OP = 'UPDATE' THEN
                        IF NOT EXISTS (SELECT 1 FROM public."ReconciliationRuns" run
                            WHERE run."BusinessUnitId" = NEW."BusinessUnitId" AND run."Id" = NEW."ReconciliationRunId"
                              AND run."Status" IN ('Draft','Reopened')) THEN
                            RAISE EXCEPTION 'matches can only change within an editable reconciliation' USING ERRCODE = '55000';
                        END IF;
                        IF NEW."BusinessUnitId" <> OLD."BusinessUnitId" OR NEW."Id" <> OLD."Id"
                           OR NEW."ReconciliationRunId" <> OLD."ReconciliationRunId" OR NEW."MatchType" <> OLD."MatchType"
                           OR NEW."Confidence" <> OLD."Confidence" OR NEW."RuleCode" <> OLD."RuleCode"
                           OR NEW."RuleVersion" <> OLD."RuleVersion" OR NEW."IdempotencyKey" <> OLD."IdempotencyKey"
                           OR NEW."RequestHash" <> OLD."RequestHash" OR NEW."CreatedBy" <> OLD."CreatedBy"
                           OR NEW."CreatedOn" <> OLD."CreatedOn" OR NEW."Version" <> OLD."Version" + 1
                           OR NOT ((OLD."Status" = 'Proposed' AND NEW."Status" IN ('Confirmed','Voided'))
                                OR (OLD."Status" = 'Confirmed' AND NEW."Status" = 'Voided')) THEN
                            RAISE EXCEPTION 'invalid reconciliation-match transition' USING ERRCODE = '55000';
                        END IF;
                    END IF;
                    RETURN NEW;
                END
                $function$;

                CREATE OR REPLACE FUNCTION public.nexora_bank_guard_allocation()
                RETURNS trigger LANGUAGE plpgsql SET search_path = pg_catalog, public AS $function$
                BEGIN
                    IF TG_OP <> 'INSERT' THEN
                        RAISE EXCEPTION 'reconciliation allocations are append-only' USING ERRCODE = '55000';
                    END IF;
                    IF NEW."BankAmount" <> NEW."FunctionalAmount" THEN
                        RAISE EXCEPTION 'functional-currency reconciliation requires equal allocation amounts' USING ERRCODE = '23514';
                    END IF;
                    IF NOT EXISTS (SELECT 1 FROM public."ReconciliationMatches" match
                        JOIN public."ReconciliationRuns" run ON run."BusinessUnitId" = match."BusinessUnitId"
                          AND run."Id" = match."ReconciliationRunId"
                        WHERE match."BusinessUnitId" = NEW."BusinessUnitId" AND match."Id" = NEW."ReconciliationMatchId"
                          AND match."Status" = 'Proposed' AND run."Status" IN ('Draft','Reopened')) THEN
                        RAISE EXCEPTION 'allocations can only be added to a proposed match in an editable run' USING ERRCODE = '55000';
                    END IF;
                    RETURN NEW;
                END
                $function$;

                CREATE OR REPLACE FUNCTION public.nexora_bank_check_match_trigger()
                RETURNS trigger LANGUAGE plpgsql SET search_path = pg_catalog, public AS $function$
                DECLARE target_match_id bigint;
                BEGIN
                    IF TG_TABLE_NAME = 'ReconciliationMatches' THEN
                        target_match_id := NEW."Id";
                    ELSE
                        target_match_id := NEW."ReconciliationMatchId";
                    END IF;
                    PERFORM public.nexora_bank_validate_match(target_match_id);
                    RETURN NULL;
                END
                $function$;

                CREATE OR REPLACE FUNCTION public.nexora_bank_certify_run()
                RETURNS trigger LANGUAGE plpgsql SET search_path = pg_catalog, public AS $function$
                DECLARE line_count integer; DECLARE journal_count integer; DECLARE incomplete_count integer;
                DECLARE matched numeric(18,2); DECLARE book_balance numeric(18,2); DECLARE canonical text;
                BEGIN
                    IF TG_OP = 'DELETE' THEN
                        RAISE EXCEPTION 'reconciliation runs cannot be deleted' USING ERRCODE = '55000';
                    END IF;
                    IF TG_OP = 'INSERT' THEN
                        IF NEW."Status" <> 'Draft' OR NEW."Version" <> 1 OR NEW."CertificateHash" IS NOT NULL THEN
                            RAISE EXCEPTION 'reconciliation runs must begin as uncertified drafts' USING ERRCODE = '23514';
                        END IF;
                        RETURN NEW;
                    END IF;
                    IF NEW."BusinessUnitId" <> OLD."BusinessUnitId" OR NEW."Id" <> OLD."Id"
                       OR NEW."BankAccountId" <> OLD."BankAccountId" OR NEW."BankStatementId" <> OLD."BankStatementId"
                       OR NEW."ReconciliationThrough" <> OLD."ReconciliationThrough"
                       OR NEW."IdempotencyKey" <> OLD."IdempotencyKey" OR NEW."RequestHash" <> OLD."RequestHash"
                       OR NEW."PreparedBy" <> OLD."PreparedBy" OR NEW."PreparedOn" <> OLD."PreparedOn"
                       OR NEW."Version" <> OLD."Version" + 1
                       OR NOT ((OLD."Status" IN ('Draft','Reopened') AND NEW."Status" = 'InReview')
                            OR (OLD."Status" = 'InReview' AND NEW."Status" = 'Approved')
                            OR (OLD."Status" = 'Approved' AND NEW."Status" = 'Reopened')) THEN
                        RAISE EXCEPTION 'invalid reconciliation-run transition' USING ERRCODE = '55000';
                    END IF;
                    IF OLD."Status" = 'InReview' AND NEW."Status" = 'Approved' THEN
                        PERFORM pg_advisory_xact_lock(hashtextextended('nexora:bank-reconciliation:' || NEW."BusinessUnitId"::text || ':' || NEW."BankAccountId"::text, 0));
                        IF NEW."ApprovedBy" IS NULL OR NEW."ApprovedBy" = NEW."PreparedBy"
                           OR NEW."ApprovedBy" = NEW."SubmittedBy"
                           OR length(trim(NEW."ApprovalReason")) < 10 OR length(trim(NEW."EvidenceReference")) < 8 THEN
                            RAISE EXCEPTION 'independent approval with reason and evidence is required' USING ERRCODE = '23514';
                        END IF;
                        IF NOT EXISTS (SELECT 1 FROM public."BankStatements" statement
                            WHERE statement."BusinessUnitId" = NEW."BusinessUnitId" AND statement."Id" = NEW."BankStatementId"
                              AND statement."ClosingBalance" = NEW."BankClosingBalance") THEN
                            RAISE EXCEPTION 'run bank balance must equal immutable statement closing balance' USING ERRCODE = '23514';
                        END IF;
                        SELECT count(*), COALESCE(sum(abs(line."SignedAmount")),0),
                               count(*) FILTER (WHERE COALESCE(confirmed.amount,0) <> abs(line."SignedAmount"))
                        INTO line_count, matched, incomplete_count
                        FROM public."BankStatementLines" line
                        LEFT JOIN (SELECT a."BankStatementLineId", sum(a."BankAmount") amount
                            FROM public."ReconciliationAllocations" a JOIN public."ReconciliationMatches" m
                              ON m."BusinessUnitId" = a."BusinessUnitId" AND m."Id" = a."ReconciliationMatchId"
                            WHERE m."BusinessUnitId" = NEW."BusinessUnitId" AND m."ReconciliationRunId" = NEW."Id"
                              AND m."Status" = 'Confirmed' GROUP BY a."BankStatementLineId") confirmed ON confirmed."BankStatementLineId" = line."Id"
                        WHERE line."BusinessUnitId" = NEW."BusinessUnitId" AND line."BankStatementId" = NEW."BankStatementId";
                        IF line_count = 0 OR incomplete_count <> 0 OR EXISTS (SELECT 1 FROM public."ReconciliationMatches"
                            WHERE "BusinessUnitId" = NEW."BusinessUnitId" AND "ReconciliationRunId" = NEW."Id" AND "Status" = 'Proposed') THEN
                            RAISE EXCEPTION 'all statement lines must be exactly confirmed before approval' USING ERRCODE = '23514';
                        END IF;
                        SELECT count(DISTINCT jl."JournalEntryId") INTO journal_count
                        FROM public."ReconciliationAllocations" a JOIN public."ReconciliationMatches" m
                          ON m."BusinessUnitId" = a."BusinessUnitId" AND m."Id" = a."ReconciliationMatchId"
                        JOIN public."JournalEntryLines" jl ON jl."BusinessUnitId" = a."BusinessUnitId" AND jl."Id" = a."JournalEntryLineId"
                        WHERE m."BusinessUnitId" = NEW."BusinessUnitId" AND m."ReconciliationRunId" = NEW."Id" AND m."Status" = 'Confirmed';
                        IF EXISTS (SELECT 1 FROM public."ReconciliationAllocations" a
                            JOIN public."ReconciliationMatches" m ON m."BusinessUnitId" = a."BusinessUnitId" AND m."Id" = a."ReconciliationMatchId"
                            JOIN public."JournalEntryLines" jl ON jl."BusinessUnitId" = a."BusinessUnitId" AND jl."Id" = a."JournalEntryLineId"
                            JOIN public."JournalEntries" journal ON journal."BusinessUnitId" = jl."BusinessUnitId" AND journal."Id" = jl."JournalEntryId"
                            JOIN public."BankAccounts" account ON account."BusinessUnitId" = jl."BusinessUnitId" AND account."Id" = NEW."BankAccountId"
                            WHERE m."BusinessUnitId" = NEW."BusinessUnitId" AND m."ReconciliationRunId" = NEW."Id"
                              AND m."Status" = 'Confirmed' AND (journal."Status" <> 'Posted'
                                OR journal."AccountingDate" > NEW."ReconciliationThrough"
                                OR jl."LedgerAccountId" <> account."LedgerAccountId")) THEN
                            RAISE EXCEPTION 'all allocated journals must remain posted, timely, and on the bank ledger account' USING ERRCODE = '23514';
                        END IF;
                        SELECT COALESCE(sum(jl."FunctionalDebit" - jl."FunctionalCredit"),0) INTO book_balance
                        FROM public."JournalEntryLines" jl JOIN public."JournalEntries" journal
                          ON journal."BusinessUnitId" = jl."BusinessUnitId" AND journal."Id" = jl."JournalEntryId"
                        JOIN public."BankAccounts" account ON account."BusinessUnitId" = jl."BusinessUnitId"
                          AND account."Id" = NEW."BankAccountId" AND account."LedgerAccountId" = jl."LedgerAccountId"
                        WHERE journal."BusinessUnitId" = NEW."BusinessUnitId" AND journal."Status" = 'Posted'
                          AND journal."AccountingDate" <= NEW."ReconciliationThrough";
                        IF book_balance <> NEW."BankClosingBalance" THEN
                            RAISE EXCEPTION 'bank and book closing balances must agree before approval' USING ERRCODE = '23514';
                        END IF;
                        SELECT string_agg(line."LineFingerprint" || ':' || a."JournalEntryLineId"::text || ':'
                            || to_char(a."BankAmount", 'FM9999999999999990.00') || ':' || to_char(a."FunctionalAmount", 'FM9999999999999990.00'),
                            '|' ORDER BY line."LineFingerprint", a."JournalEntryLineId") INTO canonical
                        FROM public."ReconciliationAllocations" a JOIN public."ReconciliationMatches" m
                          ON m."BusinessUnitId" = a."BusinessUnitId" AND m."Id" = a."ReconciliationMatchId"
                        JOIN public."BankStatementLines" line ON line."BusinessUnitId" = a."BusinessUnitId" AND line."Id" = a."BankStatementLineId"
                        WHERE m."BusinessUnitId" = NEW."BusinessUnitId" AND m."ReconciliationRunId" = NEW."Id" AND m."Status" = 'Confirmed';
                        NEW."MatchedAmount" := matched; NEW."UnexplainedDifference" := 0;
                        NEW."BookClosingBalance" := book_balance; NEW."CertificateLineCount" := line_count;
                        NEW."CertificateJournalCount" := journal_count;
                        NEW."CertificateHash" := encode(digest(convert_to(COALESCE(canonical,'') || ':' || NEW."BankClosingBalance"::text, 'UTF8'), 'sha256'), 'hex');
                    ELSIF OLD."Status" = 'Approved' THEN
                        IF NEW."ReopenedBy" IS NULL OR NEW."ReopenedBy" = NEW."ApprovedBy"
                           OR length(trim(NEW."ReopenReason")) < 10 OR length(trim(NEW."ReopenEvidenceReference")) < 8
                           OR NEW."BankClosingBalance" <> OLD."BankClosingBalance"
                           OR NEW."BookClosingBalance" <> OLD."BookClosingBalance"
                           OR NEW."MatchedAmount" <> OLD."MatchedAmount"
                           OR NEW."UnexplainedDifference" <> OLD."UnexplainedDifference"
                           OR NEW."SubmittedBy" IS DISTINCT FROM OLD."SubmittedBy"
                           OR NEW."SubmittedOn" IS DISTINCT FROM OLD."SubmittedOn"
                           OR NEW."ApprovedBy" IS DISTINCT FROM OLD."ApprovedBy"
                           OR NEW."ApprovedOn" IS DISTINCT FROM OLD."ApprovedOn"
                           OR NEW."ApprovalReason" IS DISTINCT FROM OLD."ApprovalReason"
                           OR NEW."EvidenceReference" IS DISTINCT FROM OLD."EvidenceReference"
                           OR NEW."CertificateHash" IS DISTINCT FROM OLD."CertificateHash"
                           OR NEW."CertificateLineCount" IS DISTINCT FROM OLD."CertificateLineCount"
                           OR NEW."CertificateJournalCount" IS DISTINCT FROM OLD."CertificateJournalCount" THEN
                            RAISE EXCEPTION 'reopening requires independent evidence and preserves the certificate' USING ERRCODE = '23514';
                        END IF;
                    END IF;
                    IF NEW."Status" = 'InReview' AND (NEW."SubmittedBy" IS NULL OR NEW."SubmittedOn" IS NULL) THEN
                        RAISE EXCEPTION 'submission requires an identified submitter and timestamp' USING ERRCODE = '23514';
                    END IF;
                    RETURN NEW;
                END
                $function$;

                CREATE OR REPLACE FUNCTION public.nexora_bank_validate_statement()
                RETURNS trigger LANGUAGE plpgsql SET search_path = pg_catalog, public AS $function$
                DECLARE calculated numeric(18,2);
                BEGIN
                    SELECT NEW."OpeningBalance" + COALESCE(sum(line."SignedAmount"),0) INTO calculated
                    FROM public."BankStatementLines" line WHERE line."BusinessUnitId" = NEW."BusinessUnitId"
                      AND line."BankStatementId" = NEW."Id";
                    IF calculated <> NEW."ClosingBalance" OR calculated <> NEW."CalculatedClosingBalance" THEN
                        RAISE EXCEPTION 'statement lines do not reconcile opening and closing balances' USING ERRCODE = '23514';
                    END IF;
                    RETURN NULL;
                END
                $function$;

                CREATE OR REPLACE FUNCTION public.nexora_bank_evidence_event()
                RETURNS trigger LANGUAGE plpgsql SECURITY DEFINER
                SET search_path = pg_catalog, public AS $function$
                DECLARE aggregate_type text; DECLARE aggregate_version bigint; DECLARE action_name text;
                DECLARE event_name text; DECLARE actor_id text; DECLARE occurred_at timestamp without time zone;
                DECLARE payload jsonb; DECLARE event_id uuid; DECLARE seed text;
                BEGIN
                    aggregate_type := CASE TG_TABLE_NAME WHEN 'BankAccounts' THEN 'BankAccount'
                        WHEN 'BankStatementImports' THEN 'BankStatementImport'
                        WHEN 'ReconciliationRuns' THEN 'ReconciliationRun' ELSE 'ReconciliationMatch' END;
                    aggregate_version := COALESCE((to_jsonb(NEW)->>'Version')::bigint, 1);
                    action_name := CASE WHEN TG_OP = 'INSERT' THEN 'Created'
                        ELSE COALESCE(to_jsonb(NEW)->>'Status', 'Updated') END;
                    actor_id := COALESCE(to_jsonb(NEW)->>'ApprovedBy', to_jsonb(NEW)->>'ReopenedBy',
                        to_jsonb(NEW)->>'ConfirmedBy', to_jsonb(NEW)->>'VoidedBy',
                        to_jsonb(NEW)->>'SubmittedBy', to_jsonb(NEW)->>'StatusChangedBy',
                        to_jsonb(NEW)->>'ImportedBy', to_jsonb(NEW)->>'CreatedBy',
                        to_jsonb(NEW)->>'PreparedBy', 'system:treasury');
                    occurred_at := clock_timestamp() AT TIME ZONE 'UTC'; payload := to_jsonb(NEW) - 'RawPayload';
                    event_name := 'finance.' || lower(aggregate_type) || '.' || lower(action_name);
                    seed := NEW."BusinessUnitId"::text || ':' || aggregate_type || ':' || NEW."Id"::text
                        || ':' || aggregate_version::text || ':' || event_name;
                    event_id := (substr(md5(seed),1,8)||'-'||substr(md5(seed),9,4)||'-4'||
                        substr(md5(seed),14,3)||'-a'||substr(md5(seed),18,3)||'-'||substr(md5(seed),21,12))::uuid;
                    INSERT INTO public."CommercialFinanceAudits"
                        ("BusinessUnitId","AggregateType","AggregateId","Action","Actor","OccurredOn","DetailJson")
                    VALUES (NEW."BusinessUnitId",aggregate_type,NEW."Id",action_name,actor_id,occurred_at,payload);
                    INSERT INTO public."FinanceOutboxMessages"
                        ("BusinessUnitId","EventId","AggregateType","AggregateId","AggregateVersion","EventType",
                         "Payload","SchemaVersion","OccurredOn","AvailableOn","AttemptCount")
                    VALUES (NEW."BusinessUnitId",event_id,aggregate_type,NEW."Id",aggregate_version,event_name,
                        payload,1,occurred_at,occurred_at,0);
                    RETURN NULL;
                END
                $function$;

                CREATE TRIGGER trg_bankaccounts_guard BEFORE INSERT OR UPDATE OR DELETE ON public."BankAccounts"
                    FOR EACH ROW EXECUTE FUNCTION public.nexora_bank_guard_account();
                CREATE TRIGGER trg_bankimports_immutable BEFORE UPDATE OR DELETE ON public."BankStatementImports"
                    FOR EACH ROW EXECUTE FUNCTION public.nexora_bank_immutable_evidence();
                CREATE TRIGGER trg_bankstatements_immutable BEFORE UPDATE OR DELETE ON public."BankStatements"
                    FOR EACH ROW EXECUTE FUNCTION public.nexora_bank_immutable_evidence();
                CREATE TRIGGER trg_banklines_immutable BEFORE UPDATE OR DELETE ON public."BankStatementLines"
                    FOR EACH ROW EXECUTE FUNCTION public.nexora_bank_immutable_evidence();
                CREATE CONSTRAINT TRIGGER trg_bankstatements_balance AFTER INSERT ON public."BankStatements"
                    DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION public.nexora_bank_validate_statement();
                CREATE TRIGGER trg_reconciliationmatches_guard BEFORE INSERT OR UPDATE OR DELETE ON public."ReconciliationMatches"
                    FOR EACH ROW EXECUTE FUNCTION public.nexora_bank_guard_match();
                CREATE TRIGGER trg_reconciliationallocations_guard BEFORE INSERT OR UPDATE OR DELETE ON public."ReconciliationAllocations"
                    FOR EACH ROW EXECUTE FUNCTION public.nexora_bank_guard_allocation();
                CREATE CONSTRAINT TRIGGER trg_reconciliationallocations_validate AFTER INSERT ON public."ReconciliationAllocations"
                    DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION public.nexora_bank_check_match_trigger();
                CREATE CONSTRAINT TRIGGER trg_reconciliationmatches_validate AFTER INSERT OR UPDATE ON public."ReconciliationMatches"
                    DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION public.nexora_bank_check_match_trigger();
                CREATE TRIGGER trg_reconciliationruns_certify BEFORE INSERT OR UPDATE OR DELETE ON public."ReconciliationRuns"
                    FOR EACH ROW EXECUTE FUNCTION public.nexora_bank_certify_run();
                CREATE CONSTRAINT TRIGGER trg_bankaccounts_evidence AFTER INSERT OR UPDATE ON public."BankAccounts"
                    DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION public.nexora_bank_evidence_event();
                CREATE CONSTRAINT TRIGGER trg_bankimports_evidence AFTER INSERT ON public."BankStatementImports"
                    DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION public.nexora_bank_evidence_event();
                CREATE CONSTRAINT TRIGGER trg_reconciliationruns_evidence AFTER INSERT OR UPDATE ON public."ReconciliationRuns"
                    DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION public.nexora_bank_evidence_event();
                CREATE CONSTRAINT TRIGGER trg_reconciliationmatches_evidence AFTER INSERT OR UPDATE ON public."ReconciliationMatches"
                    DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION public.nexora_bank_evidence_event();

                DO $block$
                DECLARE table_name text;
                BEGIN
                    FOREACH table_name IN ARRAY ARRAY['BankAccounts','BankStatementImports','BankStatements','BankStatementLines',
                        'ReconciliationRuns','ReconciliationMatches','ReconciliationAllocations'] LOOP
                        EXECUTE format('ALTER TABLE public.%I ENABLE ROW LEVEL SECURITY', table_name);
                        EXECUTE format('ALTER TABLE public.%I FORCE ROW LEVEL SECURITY', table_name);
                        EXECUTE format('CREATE POLICY nexora_tenant_isolation ON public.%I TO nexora_tenant_app USING ("BusinessUnitId" = NULLIF(current_setting(''nexora.business_unit_id'', true), '''')::bigint) WITH CHECK ("BusinessUnitId" = NULLIF(current_setting(''nexora.business_unit_id'', true), '''')::bigint)', table_name);
                        EXECUTE format('CREATE TRIGGER %I BEFORE TRUNCATE ON public.%I FOR EACH STATEMENT EXECUTE FUNCTION public.nexora_finance_reject_truncate()', lower(table_name) || '_reject_truncate', table_name);
                        EXECUTE format('GRANT SELECT, INSERT, UPDATE ON public.%I TO nexora_tenant_app', table_name);
                        EXECUTE format('REVOKE DELETE, TRUNCATE ON public.%I FROM nexora_tenant_app', table_name);
                    END LOOP;
                END
                $block$;
                REVOKE UPDATE ON public."BankStatementImports", public."BankStatements", public."BankStatementLines", public."ReconciliationAllocations" FROM nexora_tenant_app;
                GRANT USAGE ON SEQUENCE public."BankAccounts_Id_seq", public."BankStatementImports_Id_seq",
                    public."BankStatements_Id_seq", public."BankStatementLines_Id_seq", public."ReconciliationRuns_Id_seq",
                    public."ReconciliationMatches_Id_seq", public."ReconciliationAllocations_Id_seq" TO nexora_tenant_app;

                INSERT INTO public."Module" ("ModuleName", "Description", "IsActive", "CreatedBy", "CreatedOn") VALUES
                    ('Bank Accounts', 'Tenant bank account register', true, 'migration:bank-reconciliation:v1', now()),
                    ('Bank Statement Import', 'Immutable bank statement evidence import', true, 'migration:bank-reconciliation:v1', now()),
                    ('Bank Reconciliation', 'Statement-to-ledger matching and preparation', true, 'migration:bank-reconciliation:v1', now()),
                    ('Bank Reconciliation Approval', 'Independent reconciliation approval and reopening', true, 'migration:bank-reconciliation:v1', now())
                ON CONFLICT ("ModuleName") DO NOTHING;
                INSERT INTO public."RolePermissions"
                    ("RoleID", "ModuleID", "BusinessUnitID", "CanCreate", "CanEdit", "CanDelete", "CreatedBy", "CreatedOn")
                SELECT role."SetupID", module."ID", role."BusinessUnitID", true, true, false,
                    'migration:bank-reconciliation:v1', now()
                FROM public."Setup_Master" role CROSS JOIN public."Module" module
                WHERE lower(replace(role."SetupType", ' ', '')) = 'role'
                  AND module."ModuleName" IN ('Bank Accounts','Bank Statement Import','Bank Reconciliation','Bank Reconciliation Approval')
                  AND ((module."ModuleName" = 'Bank Reconciliation Approval'
                        AND (upper(coalesce(role."SetupCode", '')) ~ '(CONTROLLER|ADMIN)'
                          OR upper(coalesce(role."SetupValue", '')) ~ '(CONTROLLER|ADMIN)'))
                    OR (module."ModuleName" <> 'Bank Reconciliation Approval'
                        AND (upper(coalesce(role."SetupCode", '')) ~ '(TREASUR|FINANCE|ACCOUNT|ADMIN)'
                          OR upper(coalesce(role."SetupValue", '')) ~ '(TREASUR|FINANCE|ACCOUNT|ADMIN)')))
                  AND NOT EXISTS (SELECT 1 FROM public."RolePermissions" existing
                    WHERE existing."RoleID" = role."SetupID" AND existing."BusinessUnitID" = role."BusinessUnitID"
                      AND existing."ModuleID" = module."ID");
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DELETE FROM public."RolePermissions" permissions USING public."Module" module
                WHERE permissions."ModuleID" = module."ID" AND module."ModuleName" IN
                    ('Bank Accounts','Bank Statement Import','Bank Reconciliation','Bank Reconciliation Approval')
                  AND permissions."CreatedBy" = 'migration:bank-reconciliation:v1';
                DELETE FROM public."Module" WHERE "ModuleName" IN
                    ('Bank Accounts','Bank Statement Import','Bank Reconciliation','Bank Reconciliation Approval')
                  AND "CreatedBy" = 'migration:bank-reconciliation:v1';
                DROP FUNCTION IF EXISTS public.nexora_bank_check_match_trigger() CASCADE;
                DROP FUNCTION IF EXISTS public.nexora_bank_validate_match(bigint) CASCADE;
                DROP FUNCTION IF EXISTS public.nexora_bank_guard_match() CASCADE;
                DROP FUNCTION IF EXISTS public.nexora_bank_guard_allocation() CASCADE;
                DROP FUNCTION IF EXISTS public.nexora_bank_certify_run() CASCADE;
                DROP FUNCTION IF EXISTS public.nexora_bank_guard_account() CASCADE;
                DROP FUNCTION IF EXISTS public.nexora_bank_immutable_evidence() CASCADE;
                DROP FUNCTION IF EXISTS public.nexora_bank_validate_statement() CASCADE;
                DROP FUNCTION IF EXISTS public.nexora_bank_evidence_event() CASCADE;
                """);
            migrationBuilder.DropTable(
                name: "ReconciliationAllocations");

            migrationBuilder.DropTable(
                name: "BankStatementLines");

            migrationBuilder.DropTable(
                name: "ReconciliationMatches");

            migrationBuilder.DropTable(
                name: "ReconciliationRuns");

            migrationBuilder.DropTable(
                name: "BankStatements");

            migrationBuilder.DropTable(
                name: "BankStatementImports");

            migrationBuilder.DropTable(
                name: "BankAccounts");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_JournalEntryLines_BusinessUnitId_Id",
                table: "JournalEntryLines");
        }
    }
}
