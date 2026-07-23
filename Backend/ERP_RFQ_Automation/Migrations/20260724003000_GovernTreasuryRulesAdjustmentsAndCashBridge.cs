using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace ERP_RFQ_Automation.Migrations
{
    /// <inheritdoc />
    public partial class GovernTreasuryRulesAdjustmentsAndCashBridge : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_LedgerBooks_State",
                table: "LedgerBooks");

            migrationBuilder.AddColumn<string>(
                name: "RuleSetHash",
                table: "ReconciliationRuns",
                type: "character varying(64)",
                maxLength: 64,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<DateTime>(
                name: "RuleSetSnapshotOn",
                table: "ReconciliationRuns",
                type: "timestamp without time zone",
                nullable: false,
                defaultValue: new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified));

            migrationBuilder.AddColumn<long>(
                name: "BankMatchingRuleId",
                table: "ReconciliationMatches",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RuleDefinitionHash",
                table: "ReconciliationMatches",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "ReceivablesControlAccountId",
                table: "LedgerBooks",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "UnappliedCashAccountId",
                table: "LedgerBooks",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "BankAccountId",
                table: "CustomerRefunds",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "JournalEntryId",
                table: "CustomerRefunds",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "BankAccountId",
                table: "CustomerPayments",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "AccountingBridgeRequired",
                table: "CustomerPayments",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<long>(
                name: "JournalEntryId",
                table: "CustomerPayments",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "ReversalJournalEntryId",
                table: "CustomerPayments",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ReversedBy",
                table: "CustomerPayments",
                type: "character varying(255)",
                maxLength: 255,
                nullable: true);

            migrationBuilder.AddUniqueConstraint(
                name: "AK_BankStatementLines_BusinessUnitId_Id_BankAccountId",
                table: "BankStatementLines",
                columns: new[] { "BusinessUnitId", "Id", "BankAccountId" });

            migrationBuilder.CreateTable(
                name: "BankAdjustments",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    BusinessUnitId = table.Column<long>(type: "bigint", nullable: false),
                    BankAccountId = table.Column<long>(type: "bigint", nullable: false),
                    BankStatementLineId = table.Column<long>(type: "bigint", nullable: false),
                    AccountingPeriodId = table.Column<long>(type: "bigint", nullable: false),
                    AccountingDate = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    AdjustmentType = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Description = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    Amount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    EvidenceReference = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    JournalEntryId = table.Column<long>(type: "bigint", nullable: true),
                    BankJournalEntryLineId = table.Column<long>(type: "bigint", nullable: true),
                    ReversalJournalEntryId = table.Column<long>(type: "bigint", nullable: true),
                    ReversalBankJournalEntryLineId = table.Column<long>(type: "bigint", nullable: true),
                    IdempotencyKey = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    RequestHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false),
                    PreparedBy = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    PreparedOn = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    SubmittedBy = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    SubmittedOn = table.Column<DateTime>(type: "timestamp without time zone", nullable: true),
                    ApprovedBy = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    ApprovedOn = table.Column<DateTime>(type: "timestamp without time zone", nullable: true),
                    RejectedBy = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    RejectedOn = table.Column<DateTime>(type: "timestamp without time zone", nullable: true),
                    RejectionReason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
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
                    table.PrimaryKey("PK_BankAdjustments", x => x.Id);
                    table.UniqueConstraint("AK_BankAdjustments_BusinessUnitId_Id", x => new { x.BusinessUnitId, x.Id });
                    table.CheckConstraint("CK_BankAdjustments_State", "\"Amount\" > 0 AND \"Version\" > 0 AND \"Status\" IN ('Draft','InReview','Posted','Rejected','Cancelled','Reversed')");
                    table.ForeignKey(
                        name: "FK_BankAdjustments_AccountingPeriods_BusinessUnitId_Accounting~",
                        columns: x => new { x.BusinessUnitId, x.AccountingPeriodId },
                        principalTable: "AccountingPeriods",
                        principalColumns: new[] { "BusinessUnitId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_BankAdjustments_BankAccounts_BusinessUnitId_BankAccountId",
                        columns: x => new { x.BusinessUnitId, x.BankAccountId },
                        principalTable: "BankAccounts",
                        principalColumns: new[] { "BusinessUnitId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_BankAdjustments_BankStatementLines_BusinessUnitId_BankState~",
                        columns: x => new { x.BusinessUnitId, x.BankStatementLineId, x.BankAccountId },
                        principalTable: "BankStatementLines",
                        principalColumns: new[] { "BusinessUnitId", "Id", "BankAccountId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_BankAdjustments_JournalEntries_BusinessUnitId_JournalEntryId",
                        columns: x => new { x.BusinessUnitId, x.JournalEntryId },
                        principalTable: "JournalEntries",
                        principalColumns: new[] { "BusinessUnitId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_BankAdjustments_JournalEntries_BusinessUnitId_ReversalJourn~",
                        columns: x => new { x.BusinessUnitId, x.ReversalJournalEntryId },
                        principalTable: "JournalEntries",
                        principalColumns: new[] { "BusinessUnitId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_BankAdjustments_JournalEntryLines_BusinessUnitId_BankJourna~",
                        columns: x => new { x.BusinessUnitId, x.BankJournalEntryLineId },
                        principalTable: "JournalEntryLines",
                        principalColumns: new[] { "BusinessUnitId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_BankAdjustments_JournalEntryLines_BusinessUnitId_ReversalBa~",
                        columns: x => new { x.BusinessUnitId, x.ReversalBankJournalEntryLineId },
                        principalTable: "JournalEntryLines",
                        principalColumns: new[] { "BusinessUnitId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "BankMatchingRules",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    BusinessUnitId = table.Column<long>(type: "bigint", nullable: false),
                    BankAccountId = table.Column<long>(type: "bigint", nullable: true),
                    Code = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    RuleVersion = table.Column<int>(type: "integer", nullable: false),
                    Name = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    EvaluatorType = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    Priority = table.Column<int>(type: "integer", nullable: false),
                    AmountTolerance = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    BookingDateToleranceDays = table.Column<int>(type: "integer", nullable: false),
                    ReferenceMode = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    RequireUniquePair = table.Column<bool>(type: "boolean", nullable: false),
                    DefinitionHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    SupersedesRuleId = table.Column<long>(type: "bigint", nullable: true),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    IdempotencyKey = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    RequestHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    RecordVersion = table.Column<long>(type: "bigint", nullable: false),
                    CreatedBy = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    CreatedOn = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    ApprovedBy = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    ApprovedOn = table.Column<DateTime>(type: "timestamp without time zone", nullable: true),
                    ActivatedBy = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    ActivatedOn = table.Column<DateTime>(type: "timestamp without time zone", nullable: true),
                    RetiredBy = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    RetiredOn = table.Column<DateTime>(type: "timestamp without time zone", nullable: true),
                    LifecycleReason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    EvidenceReference = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BankMatchingRules", x => x.Id);
                    table.UniqueConstraint("AK_BankMatchingRules_BusinessUnitId_Id", x => new { x.BusinessUnitId, x.Id });
                    table.CheckConstraint("CK_BankMatchingRules_Definition", "\"RuleVersion\" > 0 AND \"RecordVersion\" > 0 AND \"Priority\" BETWEEN 1 AND 10000 AND \"AmountTolerance\" >= 0 AND \"BookingDateToleranceDays\" BETWEEN 0 AND 31 AND \"RequireUniquePair\" = TRUE");
                    table.CheckConstraint("CK_BankMatchingRules_Type", "\"EvaluatorType\" = 'ExactAmountDirection' AND \"ReferenceMode\" IN ('Ignore','NormalizedExact') AND \"Status\" IN ('Draft','Approved','Active','Retired')");
                    table.ForeignKey(
                        name: "FK_BankMatchingRules_BankAccounts_BusinessUnitId_BankAccountId",
                        columns: x => new { x.BusinessUnitId, x.BankAccountId },
                        principalTable: "BankAccounts",
                        principalColumns: new[] { "BusinessUnitId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_BankMatchingRules_BankMatchingRules_BusinessUnitId_Supersed~",
                        columns: x => new { x.BusinessUnitId, x.SupersedesRuleId },
                        principalTable: "BankMatchingRules",
                        principalColumns: new[] { "BusinessUnitId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "BankAdjustmentDistributions",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    BusinessUnitId = table.Column<long>(type: "bigint", nullable: false),
                    BankAdjustmentId = table.Column<long>(type: "bigint", nullable: false),
                    Sequence = table.Column<int>(type: "integer", nullable: false),
                    LedgerAccountId = table.Column<long>(type: "bigint", nullable: false),
                    Amount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    Description = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BankAdjustmentDistributions", x => x.Id);
                    table.CheckConstraint("CK_BankAdjustmentDistributions_Amount", "\"Sequence\" > 0 AND \"Amount\" > 0");
                    table.ForeignKey(
                        name: "FK_BankAdjustmentDistributions_BankAdjustments_BusinessUnitId_~",
                        columns: x => new { x.BusinessUnitId, x.BankAdjustmentId },
                        principalTable: "BankAdjustments",
                        principalColumns: new[] { "BusinessUnitId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_BankAdjustmentDistributions_LedgerAccounts_BusinessUnitId_L~",
                        columns: x => new { x.BusinessUnitId, x.LedgerAccountId },
                        principalTable: "LedgerAccounts",
                        principalColumns: new[] { "BusinessUnitId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ReconciliationRunRules",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    BusinessUnitId = table.Column<long>(type: "bigint", nullable: false),
                    ReconciliationRunId = table.Column<long>(type: "bigint", nullable: false),
                    BankMatchingRuleId = table.Column<long>(type: "bigint", nullable: false),
                    EvaluationOrder = table.Column<int>(type: "integer", nullable: false),
                    DefinitionHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ReconciliationRunRules", x => x.Id);
                    table.CheckConstraint("CK_ReconciliationRunRules_Order", "\"EvaluationOrder\" > 0");
                    table.ForeignKey(
                        name: "FK_ReconciliationRunRules_BankMatchingRules_BusinessUnitId_Ban~",
                        columns: x => new { x.BusinessUnitId, x.BankMatchingRuleId },
                        principalTable: "BankMatchingRules",
                        principalColumns: new[] { "BusinessUnitId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ReconciliationRunRules_ReconciliationRuns_BusinessUnitId_Re~",
                        columns: x => new { x.BusinessUnitId, x.ReconciliationRunId },
                        principalTable: "ReconciliationRuns",
                        principalColumns: new[] { "BusinessUnitId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ReconciliationMatches_BusinessUnitId_BankMatchingRuleId",
                table: "ReconciliationMatches",
                columns: new[] { "BusinessUnitId", "BankMatchingRuleId" });

            migrationBuilder.CreateIndex(
                name: "IX_LedgerBooks_BusinessUnitId_ReceivablesControlAccountId",
                table: "LedgerBooks",
                columns: new[] { "BusinessUnitId", "ReceivablesControlAccountId" });

            migrationBuilder.CreateIndex(
                name: "IX_LedgerBooks_BusinessUnitId_UnappliedCashAccountId",
                table: "LedgerBooks",
                columns: new[] { "BusinessUnitId", "UnappliedCashAccountId" });

            migrationBuilder.AddCheckConstraint(
                name: "CK_LedgerBooks_State",
                table: "LedgerBooks",
                sql: "\"FiscalYearStartMonth\" BETWEEN 1 AND 12 AND \"Version\" > 0");

            migrationBuilder.CreateIndex(
                name: "IX_CustomerRefunds_BusinessUnitId_BankAccountId",
                table: "CustomerRefunds",
                columns: new[] { "BusinessUnitId", "BankAccountId" });

            migrationBuilder.CreateIndex(
                name: "UX_CustomerRefunds_BU_Journal",
                table: "CustomerRefunds",
                columns: new[] { "BusinessUnitId", "JournalEntryId" },
                unique: true,
                filter: "\"JournalEntryId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_CustomerPayments_BusinessUnitId_BankAccountId",
                table: "CustomerPayments",
                columns: new[] { "BusinessUnitId", "BankAccountId" });

            migrationBuilder.CreateIndex(
                name: "IX_CustomerPayments_BusinessUnitId_ReversalJournalEntryId",
                table: "CustomerPayments",
                columns: new[] { "BusinessUnitId", "ReversalJournalEntryId" });

            migrationBuilder.CreateIndex(
                name: "UX_CustomerPayments_BU_Journal",
                table: "CustomerPayments",
                columns: new[] { "BusinessUnitId", "JournalEntryId" },
                unique: true,
                filter: "\"JournalEntryId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_BankAdjustmentDistributions_BusinessUnitId_LedgerAccountId",
                table: "BankAdjustmentDistributions",
                columns: new[] { "BusinessUnitId", "LedgerAccountId" });

            migrationBuilder.CreateIndex(
                name: "UX_BankAdjustmentDistributions_Order",
                table: "BankAdjustmentDistributions",
                columns: new[] { "BusinessUnitId", "BankAdjustmentId", "Sequence" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_BankAdjustments_BusinessUnitId_AccountingPeriodId",
                table: "BankAdjustments",
                columns: new[] { "BusinessUnitId", "AccountingPeriodId" });

            migrationBuilder.CreateIndex(
                name: "IX_BankAdjustments_BusinessUnitId_BankAccountId",
                table: "BankAdjustments",
                columns: new[] { "BusinessUnitId", "BankAccountId" });

            migrationBuilder.CreateIndex(
                name: "IX_BankAdjustments_BusinessUnitId_BankJournalEntryLineId",
                table: "BankAdjustments",
                columns: new[] { "BusinessUnitId", "BankJournalEntryLineId" });

            migrationBuilder.CreateIndex(
                name: "IX_BankAdjustments_BusinessUnitId_BankStatementLineId_BankAcco~",
                table: "BankAdjustments",
                columns: new[] { "BusinessUnitId", "BankStatementLineId", "BankAccountId" });

            migrationBuilder.CreateIndex(
                name: "IX_BankAdjustments_BusinessUnitId_ReversalBankJournalEntryLine~",
                table: "BankAdjustments",
                columns: new[] { "BusinessUnitId", "ReversalBankJournalEntryLineId" });

            migrationBuilder.CreateIndex(
                name: "IX_BankAdjustments_BusinessUnitId_ReversalJournalEntryId",
                table: "BankAdjustments",
                columns: new[] { "BusinessUnitId", "ReversalJournalEntryId" });

            migrationBuilder.CreateIndex(
                name: "UX_BankAdjustments_BU_Idempotency",
                table: "BankAdjustments",
                columns: new[] { "BusinessUnitId", "IdempotencyKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UX_BankAdjustments_BU_Journal",
                table: "BankAdjustments",
                columns: new[] { "BusinessUnitId", "JournalEntryId" },
                unique: true,
                filter: "\"JournalEntryId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_BankMatchingRules_BusinessUnitId_BankAccountId",
                table: "BankMatchingRules",
                columns: new[] { "BusinessUnitId", "BankAccountId" });

            migrationBuilder.CreateIndex(
                name: "IX_BankMatchingRules_BusinessUnitId_SupersedesRuleId",
                table: "BankMatchingRules",
                columns: new[] { "BusinessUnitId", "SupersedesRuleId" });

            migrationBuilder.CreateIndex(
                name: "UX_BankMatchingRules_BU_ActiveScope",
                table: "BankMatchingRules",
                columns: new[] { "BusinessUnitId", "Code", "BankAccountId" },
                unique: true,
                filter: "\"Status\" = 'Active'");

            migrationBuilder.CreateIndex(
                name: "UX_BankMatchingRules_BU_Code_Version",
                table: "BankMatchingRules",
                columns: new[] { "BusinessUnitId", "Code", "RuleVersion" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UX_BankMatchingRules_BU_Idempotency",
                table: "BankMatchingRules",
                columns: new[] { "BusinessUnitId", "IdempotencyKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ReconciliationRunRules_BusinessUnitId_BankMatchingRuleId",
                table: "ReconciliationRunRules",
                columns: new[] { "BusinessUnitId", "BankMatchingRuleId" });

            migrationBuilder.CreateIndex(
                name: "UX_ReconciliationRunRules_Evidence",
                table: "ReconciliationRunRules",
                columns: new[] { "BusinessUnitId", "ReconciliationRunId", "BankMatchingRuleId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UX_ReconciliationRunRules_Order",
                table: "ReconciliationRunRules",
                columns: new[] { "BusinessUnitId", "ReconciliationRunId", "EvaluationOrder" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_CustomerPayments_BankAccounts_BusinessUnitId_BankAccountId",
                table: "CustomerPayments",
                columns: new[] { "BusinessUnitId", "BankAccountId" },
                principalTable: "BankAccounts",
                principalColumns: new[] { "BusinessUnitId", "Id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_CustomerPayments_JournalEntries_BusinessUnitId_JournalEntry~",
                table: "CustomerPayments",
                columns: new[] { "BusinessUnitId", "JournalEntryId" },
                principalTable: "JournalEntries",
                principalColumns: new[] { "BusinessUnitId", "Id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_CustomerPayments_JournalEntries_BusinessUnitId_ReversalJour~",
                table: "CustomerPayments",
                columns: new[] { "BusinessUnitId", "ReversalJournalEntryId" },
                principalTable: "JournalEntries",
                principalColumns: new[] { "BusinessUnitId", "Id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_CustomerRefunds_BankAccounts_BusinessUnitId_BankAccountId",
                table: "CustomerRefunds",
                columns: new[] { "BusinessUnitId", "BankAccountId" },
                principalTable: "BankAccounts",
                principalColumns: new[] { "BusinessUnitId", "Id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_CustomerRefunds_JournalEntries_BusinessUnitId_JournalEntryId",
                table: "CustomerRefunds",
                columns: new[] { "BusinessUnitId", "JournalEntryId" },
                principalTable: "JournalEntries",
                principalColumns: new[] { "BusinessUnitId", "Id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_LedgerBooks_LedgerAccounts_BusinessUnitId_ReceivablesContro~",
                table: "LedgerBooks",
                columns: new[] { "BusinessUnitId", "ReceivablesControlAccountId" },
                principalTable: "LedgerAccounts",
                principalColumns: new[] { "BusinessUnitId", "Id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_LedgerBooks_LedgerAccounts_BusinessUnitId_UnappliedCashAcco~",
                table: "LedgerBooks",
                columns: new[] { "BusinessUnitId", "UnappliedCashAccountId" },
                principalTable: "LedgerAccounts",
                principalColumns: new[] { "BusinessUnitId", "Id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_ReconciliationMatches_BankMatchingRules_BusinessUnitId_Bank~",
                table: "ReconciliationMatches",
                columns: new[] { "BusinessUnitId", "BankMatchingRuleId" },
                principalTable: "BankMatchingRules",
                principalColumns: new[] { "BusinessUnitId", "Id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.Sql("""
                ALTER TABLE public."JournalEntries" DROP CONSTRAINT IF EXISTS "CK_JournalEntries_State";
                ALTER TABLE public."CustomerPayments" ALTER COLUMN "AccountingBridgeRequired" SET DEFAULT TRUE;
                ALTER TABLE public."CustomerPayments" ADD CONSTRAINT "CK_CustomerPayments_AccountingBridge"
                    CHECK ("AccountingBridgeRequired" OR ("BankAccountId" IS NULL AND "JournalEntryId" IS NULL
                        AND "ReversalJournalEntryId" IS NULL));
                ALTER TABLE public."JournalEntries" ADD CONSTRAINT "CK_JournalEntries_State" CHECK (
                    "Status" IN ('Draft','Posted','Cancelled','Reversed')
                    AND "SourceType" IN ('Manual','JournalReversal','BankAdjustment','CustomerPayment','CustomerRefund')
                    AND (("Status" = 'Draft' AND "EntryNumber" IS NULL AND "PostedBy" IS NULL AND "PostedOn" IS NULL
                          AND "CancelledBy" IS NULL AND "CancelledOn" IS NULL AND "ReversedBy" IS NULL AND "ReversedOn" IS NULL)
                      OR ("Status" = 'Posted' AND "EntryNumber" IS NOT NULL AND "PostedBy" IS NOT NULL AND "PostedOn" IS NOT NULL
                          AND "CancelledBy" IS NULL AND "CancelledOn" IS NULL AND "ReversedBy" IS NULL AND "ReversedOn" IS NULL)
                      OR ("Status" = 'Cancelled' AND "EntryNumber" IS NULL AND "PostedBy" IS NULL AND "PostedOn" IS NULL
                          AND "CancelledBy" IS NOT NULL AND "CancelledOn" IS NOT NULL AND length(trim("CancellationReason")) >= 20)
                      OR ("Status" = 'Reversed' AND "EntryNumber" IS NOT NULL AND "PostedBy" IS NOT NULL AND "PostedOn" IS NOT NULL
                          AND "ReversedBy" IS NOT NULL AND "ReversedOn" IS NOT NULL
                          AND length(trim("ReversalReason")) >= 20 AND length(trim("ReversalEvidenceReference")) >= 8)));

                CREATE OR REPLACE FUNCTION public.nexora_gl_guard_book()
                RETURNS trigger LANGUAGE plpgsql SECURITY DEFINER
                SET search_path = pg_catalog, public AS $function$
                DECLARE actor_id text;
                BEGIN
                    IF TG_OP = 'DELETE' THEN
                        RAISE EXCEPTION 'the accounting book cannot be deleted' USING ERRCODE = '55000';
                    END IF;
                    IF current_setting('role', true) = 'nexora_tenant_app' THEN
                        actor_id := public.nexora_gl_authenticated_actor(COALESCE(NEW."BusinessUnitId", OLD."BusinessUnitId"));
                    END IF;
                    IF TG_OP = 'INSERT' THEN
                        IF NEW."Version" <> 1 OR (actor_id IS NOT NULL AND NEW."CreatedBy" <> actor_id)
                           OR NOT EXISTS (SELECT 1 FROM public."Currency" c
                                WHERE c."BusinessUnitID" = NEW."BusinessUnitId" AND c."ID" = NEW."FunctionalCurrencyId"
                                  AND c."IsActive" IS TRUE AND c."IsBaseCurrency" IS TRUE) THEN
                            RAISE EXCEPTION 'the accounting book requires the tenant active base currency and authenticated creator' USING ERRCODE = '23514';
                        END IF;
                        RETURN NEW;
                    END IF;
                    IF OLD."ReceivablesControlAccountId" IS NOT NULL OR OLD."UnappliedCashAccountId" IS NOT NULL
                       OR NEW."ReceivablesControlAccountId" IS NULL OR NEW."UnappliedCashAccountId" IS NULL
                       OR NEW."ReceivablesControlAccountId" = NEW."UnappliedCashAccountId"
                       OR NEW."Version" <> OLD."Version" + 1
                       OR (NEW."BusinessUnitId", NEW."Id", NEW."Name", NEW."FunctionalCurrencyId", NEW."TimeZoneId",
                            NEW."FiscalYearStartMonth", NEW."IdempotencyKey", NEW."RequestHash", NEW."CreatedBy", NEW."CreatedOn")
                          IS DISTINCT FROM
                          (OLD."BusinessUnitId", OLD."Id", OLD."Name", OLD."FunctionalCurrencyId", OLD."TimeZoneId",
                            OLD."FiscalYearStartMonth", OLD."IdempotencyKey", OLD."RequestHash", OLD."CreatedBy", OLD."CreatedOn")
                       OR NOT EXISTS (SELECT 1 FROM public."LedgerAccounts" account
                            WHERE account."BusinessUnitId" = NEW."BusinessUnitId"
                              AND account."Id" = NEW."ReceivablesControlAccountId" AND account."IsActive" IS TRUE
                              AND account."IsControlAccount" IS TRUE AND account."Category" = 'Asset')
                       OR NOT EXISTS (SELECT 1 FROM public."LedgerAccounts" account
                            WHERE account."BusinessUnitId" = NEW."BusinessUnitId"
                              AND account."Id" = NEW."UnappliedCashAccountId" AND account."IsActive" IS TRUE
                              AND account."IsControlAccount" IS FALSE AND account."Category" = 'Liability') THEN
                        RAISE EXCEPTION 'the accounting book permits only one governed receivables posting configuration' USING ERRCODE = '55000';
                    END IF;
                    RETURN NEW;
                END; $function$;

                ALTER TABLE public."ReconciliationMatches" DROP CONSTRAINT IF EXISTS "CK_ReconciliationMatches_ManualEvidence";

                DROP TRIGGER IF EXISTS trg_reconciliationruns_certify ON public."ReconciliationRuns";
                DROP TRIGGER IF EXISTS trg_reconciliationruns_evidence ON public."ReconciliationRuns";
                DROP TRIGGER IF EXISTS trg_reconciliationmatches_guard ON public."ReconciliationMatches";
                DROP TRIGGER IF EXISTS trg_reconciliationmatches_evidence ON public."ReconciliationMatches";

                INSERT INTO public."BankMatchingRules" ("BusinessUnitId","BankAccountId","Code","RuleVersion","Name",
                    "EvaluatorType","Priority","AmountTolerance","BookingDateToleranceDays","ReferenceMode",
                    "RequireUniquePair","DefinitionHash","SupersedesRuleId","Status","IdempotencyKey","RequestHash",
                    "RecordVersion","CreatedBy","CreatedOn","ApprovedBy","ApprovedOn","ActivatedBy","ActivatedOn")
                SELECT DISTINCT account."BusinessUnitId", NULL::bigint, 'EXACT_AMOUNT_DIRECTION', 1,
                    'System exact amount and direction', 'ExactAmountDirection', 1000, 0, 31, 'Ignore', TRUE,
                    encode(digest(convert_to('*|EXACT_AMOUNT_DIRECTION|1|System exact amount and direction|ExactAmountDirection|1000|0.00|31|Ignore|true','UTF8'),'sha256'),'hex'),
                    NULL::bigint, 'Active', 'system:default-exact-rule:v1',
                    encode(digest(convert_to('*|EXACT_AMOUNT_DIRECTION|1|System exact amount and direction|ExactAmountDirection|1000|0.00|31|Ignore|true','UTF8'),'sha256'),'hex'),
                    1, 'system:bank-rule-bootstrap', statement_timestamp(), 'system:bank-rule-bootstrap', statement_timestamp(),
                    'system:bank-rule-bootstrap', statement_timestamp()
                FROM public."BankAccounts" account
                WHERE NOT EXISTS (SELECT 1 FROM public."BankMatchingRules" existing
                    WHERE existing."BusinessUnitId" = account."BusinessUnitId" AND existing."Code" = 'EXACT_AMOUNT_DIRECTION'
                      AND existing."BankAccountId" IS NULL AND existing."Status" = 'Active');

                UPDATE public."ReconciliationRuns" run SET "RuleSetHash" = encode(digest(convert_to(rule."DefinitionHash",'UTF8'),'sha256'),'hex'),
                    "RuleSetSnapshotOn" = run."PreparedOn"
                FROM public."BankMatchingRules" rule
                WHERE rule."BusinessUnitId" = run."BusinessUnitId" AND rule."Code" = 'EXACT_AMOUNT_DIRECTION'
                  AND rule."BankAccountId" IS NULL AND rule."Status" = 'Active';
                INSERT INTO public."ReconciliationRunRules" ("BusinessUnitId","ReconciliationRunId","BankMatchingRuleId","EvaluationOrder","DefinitionHash")
                SELECT run."BusinessUnitId", run."Id", rule."Id", 1, rule."DefinitionHash"
                FROM public."ReconciliationRuns" run JOIN public."BankMatchingRules" rule
                  ON rule."BusinessUnitId" = run."BusinessUnitId" AND rule."Code" = 'EXACT_AMOUNT_DIRECTION'
                 AND rule."BankAccountId" IS NULL AND rule."Status" = 'Active'
                ON CONFLICT DO NOTHING;
                UPDATE public."ReconciliationMatches" match SET "BankMatchingRuleId" = rule."Id",
                    "RuleDefinitionHash" = rule."DefinitionHash", "RuleCode" = rule."Code", "RuleVersion" = rule."RuleVersion"
                FROM public."BankMatchingRules" rule
                WHERE match."BusinessUnitId" = rule."BusinessUnitId" AND match."MatchType" = 'DeterministicExact'
                  AND rule."Code" = 'EXACT_AMOUNT_DIRECTION' AND rule."BankAccountId" IS NULL AND rule."Status" = 'Active';

                SET CONSTRAINTS ALL IMMEDIATE;
                ALTER TABLE public."ReconciliationMatches" ADD CONSTRAINT "CK_ReconciliationMatches_ManualEvidence" CHECK (
                    ("MatchType" = 'Manual' AND length(trim("MatchReason")) >= 20
                        AND length(trim("EvidenceReference")) >= 8 AND "RuleCode" = 'MANUAL_REVIEWED_V1'
                        AND "RuleVersion" = 1 AND "Confidence" = 1 AND "BankMatchingRuleId" IS NULL
                        AND "RuleDefinitionHash" IS NULL)
                    OR ("MatchType" = 'DeterministicExact' AND "MatchReason" IS NULL
                        AND "EvidenceReference" IS NULL AND "Confidence" = 1 AND "BankMatchingRuleId" IS NOT NULL
                        AND "RuleDefinitionHash" IS NOT NULL));

                CREATE TRIGGER trg_reconciliationruns_certify BEFORE INSERT OR UPDATE OR DELETE ON public."ReconciliationRuns"
                    FOR EACH ROW EXECUTE FUNCTION public.nexora_bank_certify_run();
                CREATE CONSTRAINT TRIGGER trg_reconciliationruns_evidence AFTER INSERT OR UPDATE ON public."ReconciliationRuns"
                    DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION public.nexora_bank_evidence_event();
                CREATE TRIGGER trg_reconciliationmatches_guard BEFORE INSERT OR UPDATE OR DELETE ON public."ReconciliationMatches"
                    FOR EACH ROW EXECUTE FUNCTION public.nexora_bank_guard_match();
                CREATE CONSTRAINT TRIGGER trg_reconciliationmatches_evidence AFTER INSERT OR UPDATE ON public."ReconciliationMatches"
                    DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION public.nexora_bank_evidence_event();

                CREATE UNIQUE INDEX IF NOT EXISTS "UX_BankMatchingRules_BU_ActiveTenant"
                    ON public."BankMatchingRules" ("BusinessUnitId","Code")
                    WHERE "Status" = 'Active' AND "BankAccountId" IS NULL;

                CREATE OR REPLACE FUNCTION public.nexora_treasury_guard_rule()
                RETURNS trigger LANGUAGE plpgsql SET search_path = pg_catalog, public AS $function$
                DECLARE canonical text; DECLARE actor_id text;
                BEGIN
                    IF TG_OP = 'DELETE' THEN RAISE EXCEPTION 'bank matching rules cannot be deleted' USING ERRCODE = '55000'; END IF;
                    IF current_setting('role', true) = 'nexora_tenant_app' THEN
                        actor_id := public.nexora_gl_authenticated_actor(COALESCE(NEW."BusinessUnitId", OLD."BusinessUnitId"));
                    END IF;
                    IF TG_OP = 'INSERT' THEN
                        canonical := COALESCE(NEW."BankAccountId"::text, '*') || '|' || NEW."Code" || '|'
                            || NEW."RuleVersion"::text || '|' || NEW."Name" || '|' || NEW."EvaluatorType" || '|'
                            || NEW."Priority"::text || '|' || NEW."AmountTolerance"::text || '|'
                            || NEW."BookingDateToleranceDays"::text || '|' || NEW."ReferenceMode" || '|'
                            || lower(NEW."RequireUniquePair"::text);
                        IF NEW."Code" !~ '^[A-Z][A-Z0-9_]{2,79}$'
                           OR NEW."DefinitionHash" <> encode(digest(convert_to(canonical,'UTF8'),'sha256'),'hex') THEN
                            RAISE EXCEPTION 'matching-rule code or canonical definition hash is invalid' USING ERRCODE = '23514';
                        END IF;
                        IF (actor_id IS NOT NULL AND NEW."CreatedBy" <> actor_id) OR NOT ((NEW."Status" = 'Draft' AND NEW."RecordVersion" = 1
                              AND NEW."ApprovedBy" IS NULL AND NEW."ActivatedBy" IS NULL AND NEW."RetiredBy" IS NULL)
                            OR (NEW."Status" = 'Active' AND NEW."CreatedBy" = 'system:bank-rule-bootstrap'
                              AND NEW."ApprovedBy" = 'system:bank-rule-bootstrap' AND NEW."ActivatedBy" = 'system:bank-rule-bootstrap')) THEN
                            RAISE EXCEPTION 'invalid initial matching-rule state' USING ERRCODE = '23514';
                        END IF;
                    ELSE
                        IF NEW."BusinessUnitId" <> OLD."BusinessUnitId" OR NEW."Id" <> OLD."Id"
                           OR NEW."BankAccountId" IS DISTINCT FROM OLD."BankAccountId" OR NEW."Code" <> OLD."Code"
                           OR NEW."RuleVersion" <> OLD."RuleVersion" OR NEW."Name" <> OLD."Name"
                           OR NEW."EvaluatorType" <> OLD."EvaluatorType" OR NEW."Priority" <> OLD."Priority"
                           OR NEW."AmountTolerance" <> OLD."AmountTolerance"
                           OR NEW."BookingDateToleranceDays" <> OLD."BookingDateToleranceDays"
                           OR NEW."ReferenceMode" <> OLD."ReferenceMode" OR NEW."RequireUniquePair" <> OLD."RequireUniquePair"
                           OR NEW."DefinitionHash" <> OLD."DefinitionHash" OR NEW."SupersedesRuleId" IS DISTINCT FROM OLD."SupersedesRuleId"
                           OR NEW."IdempotencyKey" <> OLD."IdempotencyKey" OR NEW."RequestHash" <> OLD."RequestHash"
                           OR NEW."CreatedBy" <> OLD."CreatedBy" OR NEW."CreatedOn" <> OLD."CreatedOn"
                           OR NEW."RecordVersion" <> OLD."RecordVersion" + 1
                           OR NOT ((OLD."Status" = 'Draft' AND NEW."Status" = 'Approved'
                                    AND NEW."ApprovedBy" IS NOT NULL AND NEW."ApprovedOn" IS NOT NULL
                                    AND lower(trim(NEW."ApprovedBy")) <> lower(trim(NEW."CreatedBy"))
                                    AND (actor_id IS NULL OR NEW."ApprovedBy" = actor_id)
                                    AND NEW."ActivatedBy" IS NOT DISTINCT FROM OLD."ActivatedBy"
                                    AND NEW."ActivatedOn" IS NOT DISTINCT FROM OLD."ActivatedOn"
                                    AND NEW."RetiredBy" IS NOT DISTINCT FROM OLD."RetiredBy"
                                    AND NEW."RetiredOn" IS NOT DISTINCT FROM OLD."RetiredOn")
                                OR (OLD."Status" = 'Approved' AND NEW."Status" = 'Active'
                                    AND NEW."ApprovedBy" IS NOT DISTINCT FROM OLD."ApprovedBy"
                                    AND NEW."ApprovedOn" IS NOT DISTINCT FROM OLD."ApprovedOn"
                                    AND NEW."ActivatedBy" IS NOT NULL AND NEW."ActivatedOn" IS NOT NULL
                                    AND lower(trim(NEW."ActivatedBy")) <> lower(trim(NEW."CreatedBy"))
                                    AND (actor_id IS NULL OR NEW."ActivatedBy" = actor_id)
                                    AND NEW."RetiredBy" IS NOT DISTINCT FROM OLD."RetiredBy"
                                    AND NEW."RetiredOn" IS NOT DISTINCT FROM OLD."RetiredOn")
                                OR (OLD."Status" = 'Active' AND NEW."Status" = 'Retired'
                                    AND NEW."ApprovedBy" IS NOT DISTINCT FROM OLD."ApprovedBy"
                                    AND NEW."ApprovedOn" IS NOT DISTINCT FROM OLD."ApprovedOn"
                                    AND NEW."ActivatedBy" IS NOT DISTINCT FROM OLD."ActivatedBy"
                                    AND NEW."ActivatedOn" IS NOT DISTINCT FROM OLD."ActivatedOn"
                                    AND NEW."RetiredBy" IS NOT NULL AND NEW."RetiredOn" IS NOT NULL
                                    AND (actor_id IS NULL OR NEW."RetiredBy" = actor_id)))
                           OR length(trim(NEW."LifecycleReason")) < 20 OR length(trim(NEW."EvidenceReference")) < 8 THEN
                            RAISE EXCEPTION 'matching-rule definitions are immutable and transitions require independent evidence' USING ERRCODE = '55000';
                        END IF;
                    END IF;
                    RETURN NEW;
                END $function$;

                CREATE OR REPLACE FUNCTION public.nexora_treasury_guard_snapshot()
                RETURNS trigger LANGUAGE plpgsql SET search_path = pg_catalog, public AS $function$
                BEGIN
                    IF TG_OP <> 'INSERT' THEN RAISE EXCEPTION 'reconciliation rule snapshots are append-only' USING ERRCODE = '55000'; END IF;
                    IF NOT EXISTS (SELECT 1 FROM public."BankMatchingRules" rule
                        JOIN public."ReconciliationRuns" run ON run."BusinessUnitId" = rule."BusinessUnitId"
                          AND run."Id" = NEW."ReconciliationRunId"
                        WHERE rule."BusinessUnitId" = NEW."BusinessUnitId" AND rule."Id" = NEW."BankMatchingRuleId"
                          AND rule."DefinitionHash" = NEW."DefinitionHash" AND rule."Status" = 'Active'
                          AND (rule."BankAccountId" IS NULL OR rule."BankAccountId" = run."BankAccountId")) THEN
                        RAISE EXCEPTION 'rule snapshot must preserve an active applicable immutable definition' USING ERRCODE = '23514';
                    END IF;
                    RETURN NEW;
                END $function$;

                CREATE OR REPLACE FUNCTION public.nexora_treasury_validate_run_rules()
                RETURNS trigger LANGUAGE plpgsql SET search_path = pg_catalog, public AS $function$
                DECLARE canonical text;
                BEGIN
                    SELECT string_agg(snapshot."DefinitionHash", '|' ORDER BY snapshot."EvaluationOrder") INTO canonical
                    FROM public."ReconciliationRunRules" snapshot WHERE snapshot."BusinessUnitId" = NEW."BusinessUnitId"
                      AND snapshot."ReconciliationRunId" = NEW."Id";
                    IF canonical IS NULL OR encode(digest(convert_to(canonical,'UTF8'),'sha256'),'hex') <> NEW."RuleSetHash" THEN
                        RAISE EXCEPTION 'reconciliation requires a complete immutable rule-set snapshot' USING ERRCODE = '23514';
                    END IF;
                    RETURN NULL;
                END $function$;

                CREATE OR REPLACE FUNCTION public.nexora_treasury_validate_match_rule()
                RETURNS trigger LANGUAGE plpgsql SET search_path = pg_catalog, public AS $function$
                BEGIN
                    IF TG_OP = 'UPDATE' AND (NEW."BankMatchingRuleId" IS DISTINCT FROM OLD."BankMatchingRuleId"
                       OR NEW."RuleDefinitionHash" IS DISTINCT FROM OLD."RuleDefinitionHash") THEN
                        RAISE EXCEPTION 'matching-rule provenance is immutable' USING ERRCODE = '55000';
                    END IF;
                    IF NEW."MatchType" = 'DeterministicExact' AND NOT EXISTS (
                        SELECT 1 FROM public."BankMatchingRules" rule
                        JOIN public."ReconciliationRunRules" snapshot ON snapshot."BusinessUnitId" = rule."BusinessUnitId"
                          AND snapshot."BankMatchingRuleId" = rule."Id" AND snapshot."ReconciliationRunId" = NEW."ReconciliationRunId"
                        WHERE rule."BusinessUnitId" = NEW."BusinessUnitId" AND rule."Id" = NEW."BankMatchingRuleId"
                          AND rule."Code" = NEW."RuleCode" AND rule."RuleVersion" = NEW."RuleVersion"
                          AND rule."DefinitionHash" = NEW."RuleDefinitionHash" AND snapshot."DefinitionHash" = rule."DefinitionHash") THEN
                        RAISE EXCEPTION 'deterministic match must reference a snapshotted rule definition' USING ERRCODE = '23514';
                    END IF;
                    RETURN NEW;
                END $function$;

                CREATE TRIGGER trg_bankmatchingrules_guard BEFORE INSERT OR UPDATE OR DELETE ON public."BankMatchingRules"
                    FOR EACH ROW EXECUTE FUNCTION public.nexora_treasury_guard_rule();
                CREATE TRIGGER trg_reconciliationrunrules_guard BEFORE INSERT OR UPDATE OR DELETE ON public."ReconciliationRunRules"
                    FOR EACH ROW EXECUTE FUNCTION public.nexora_treasury_guard_snapshot();
                CREATE CONSTRAINT TRIGGER trg_reconciliationruns_rules AFTER INSERT OR UPDATE ON public."ReconciliationRuns"
                    DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION public.nexora_treasury_validate_run_rules();
                CREATE TRIGGER trg_reconciliationmatches_rule BEFORE INSERT OR UPDATE ON public."ReconciliationMatches"
                    FOR EACH ROW EXECUTE FUNCTION public.nexora_treasury_validate_match_rule();
                """);

            migrationBuilder.Sql("""
                CREATE OR REPLACE FUNCTION public.nexora_treasury_guard_adjustment()
                RETURNS trigger LANGUAGE plpgsql SET search_path = pg_catalog, public AS $function$
                DECLARE line_amount numeric(18,2); DECLARE allocated numeric(18,2); DECLARE actor_id text;
                BEGIN
                    IF TG_OP = 'DELETE' THEN RAISE EXCEPTION 'bank adjustments cannot be deleted' USING ERRCODE = '55000'; END IF;
                    IF current_setting('role', true) = 'nexora_tenant_app' THEN
                        actor_id := public.nexora_gl_authenticated_actor(COALESCE(NEW."BusinessUnitId", OLD."BusinessUnitId"));
                    END IF;
                    IF TG_OP = 'INSERT' THEN
                        IF NEW."Status" <> 'Draft' OR NEW."Version" <> 1 OR NEW."JournalEntryId" IS NOT NULL
                           OR NEW."ReversalJournalEntryId" IS NOT NULL
                           OR (actor_id IS NOT NULL AND NEW."PreparedBy" <> actor_id) THEN
                            RAISE EXCEPTION 'bank adjustments must begin as unposted drafts' USING ERRCODE = '23514';
                        END IF;
                    ELSE
                        IF NEW."BusinessUnitId" <> OLD."BusinessUnitId" OR NEW."Id" <> OLD."Id"
                           OR NEW."BankAccountId" <> OLD."BankAccountId" OR NEW."BankStatementLineId" <> OLD."BankStatementLineId"
                           OR NEW."AccountingPeriodId" <> OLD."AccountingPeriodId" OR NEW."AccountingDate" <> OLD."AccountingDate"
                           OR NEW."AdjustmentType" <> OLD."AdjustmentType" OR NEW."Description" <> OLD."Description"
                           OR NEW."Amount" <> OLD."Amount" OR NEW."EvidenceReference" <> OLD."EvidenceReference"
                           OR NEW."IdempotencyKey" <> OLD."IdempotencyKey" OR NEW."RequestHash" <> OLD."RequestHash"
                           OR NEW."PreparedBy" <> OLD."PreparedBy" OR NEW."PreparedOn" <> OLD."PreparedOn"
                           OR ((OLD."Status", NEW."Status") <> ('Draft','InReview') AND
                               (NEW."SubmittedBy" IS DISTINCT FROM OLD."SubmittedBy" OR NEW."SubmittedOn" IS DISTINCT FROM OLD."SubmittedOn"))
                           OR ((OLD."Status", NEW."Status") <> ('InReview','Posted') AND
                               (NEW."ApprovedBy" IS DISTINCT FROM OLD."ApprovedBy" OR NEW."ApprovedOn" IS DISTINCT FROM OLD."ApprovedOn"
                                OR NEW."JournalEntryId" IS DISTINCT FROM OLD."JournalEntryId"
                                OR NEW."BankJournalEntryLineId" IS DISTINCT FROM OLD."BankJournalEntryLineId"))
                           OR ((OLD."Status", NEW."Status") <> ('InReview','Rejected') AND
                               (NEW."RejectedBy" IS DISTINCT FROM OLD."RejectedBy" OR NEW."RejectedOn" IS DISTINCT FROM OLD."RejectedOn"
                                OR NEW."RejectionReason" IS DISTINCT FROM OLD."RejectionReason"))
                           OR ((OLD."Status", NEW."Status") <> ('Draft','Cancelled') AND
                               (NEW."CancelledBy" IS DISTINCT FROM OLD."CancelledBy" OR NEW."CancelledOn" IS DISTINCT FROM OLD."CancelledOn"
                                OR NEW."CancellationReason" IS DISTINCT FROM OLD."CancellationReason"))
                           OR ((OLD."Status", NEW."Status") <> ('Posted','Reversed') AND
                               (NEW."ReversedBy" IS DISTINCT FROM OLD."ReversedBy" OR NEW."ReversedOn" IS DISTINCT FROM OLD."ReversedOn"
                                OR NEW."ReversalReason" IS DISTINCT FROM OLD."ReversalReason"
                                OR NEW."ReversalEvidenceReference" IS DISTINCT FROM OLD."ReversalEvidenceReference"
                                OR NEW."ReversalJournalEntryId" IS DISTINCT FROM OLD."ReversalJournalEntryId"
                                OR NEW."ReversalBankJournalEntryLineId" IS DISTINCT FROM OLD."ReversalBankJournalEntryLineId"))
                           OR NEW."Version" <> OLD."Version" + 1 THEN
                            RAISE EXCEPTION 'bank adjustment accounting content is immutable' USING ERRCODE = '55000';
                        END IF;
                        IF OLD."Status" = 'Draft' AND NEW."Status" = 'InReview' THEN
                            IF NEW."SubmittedBy" IS NULL OR NEW."SubmittedOn" IS NULL
                               OR (actor_id IS NOT NULL AND NEW."SubmittedBy" <> actor_id) THEN RAISE EXCEPTION 'adjustment submission identity is required' USING ERRCODE = '23514'; END IF;
                        ELSIF OLD."Status" = 'Draft' AND NEW."Status" = 'Cancelled' THEN
                            IF NEW."CancelledBy" IS NULL OR length(trim(NEW."CancellationReason")) < 20
                               OR (actor_id IS NOT NULL AND NEW."CancelledBy" <> actor_id) THEN RAISE EXCEPTION 'adjustment cancellation evidence is required' USING ERRCODE = '23514'; END IF;
                        ELSIF OLD."Status" = 'InReview' AND NEW."Status" = 'Rejected' THEN
                            IF NEW."RejectedBy" IS NULL OR lower(trim(NEW."RejectedBy")) IN (lower(trim(NEW."PreparedBy")),lower(trim(NEW."SubmittedBy"))) OR length(trim(NEW."RejectionReason")) < 20
                               OR (actor_id IS NOT NULL AND NEW."RejectedBy" <> actor_id) THEN RAISE EXCEPTION 'independent adjustment rejection evidence is required' USING ERRCODE = '23514'; END IF;
                        ELSIF OLD."Status" = 'InReview' AND NEW."Status" = 'Posted' THEN
                            IF NEW."ApprovedBy" IS NULL OR lower(trim(NEW."ApprovedBy")) IN (lower(trim(NEW."PreparedBy")),lower(trim(NEW."SubmittedBy")))
                               OR (actor_id IS NOT NULL AND NEW."ApprovedBy" <> actor_id)
                               OR NEW."JournalEntryId" IS NULL OR NEW."BankJournalEntryLineId" IS NULL THEN
                                RAISE EXCEPTION 'independent adjustment approval and journal evidence are required' USING ERRCODE = '23514';
                            END IF;
                            SELECT abs(line."SignedAmount") INTO STRICT line_amount FROM public."BankStatementLines" line
                                WHERE line."BusinessUnitId" = NEW."BusinessUnitId" AND line."Id" = NEW."BankStatementLineId" FOR UPDATE;
                            SELECT COALESCE(sum(other."Amount"),0) INTO allocated FROM public."BankAdjustments" other
                                WHERE other."BusinessUnitId" = NEW."BusinessUnitId" AND other."BankStatementLineId" = NEW."BankStatementLineId"
                                  AND other."Id" <> NEW."Id" AND other."Status" = 'Posted';
                            IF allocated + NEW."Amount" > line_amount THEN RAISE EXCEPTION 'posted adjustments over-allocate the statement line' USING ERRCODE = '23514'; END IF;
                        ELSIF OLD."Status" = 'Posted' AND NEW."Status" = 'Reversed' THEN
                            IF NEW."ReversedBy" IS NULL OR lower(trim(NEW."ReversedBy")) IN (lower(trim(NEW."PreparedBy")),lower(trim(NEW."ApprovedBy")))
                               OR (actor_id IS NOT NULL AND NEW."ReversedBy" <> actor_id)
                               OR length(trim(NEW."ReversalReason")) < 20 OR length(trim(NEW."ReversalEvidenceReference")) < 8
                               OR NEW."ReversalJournalEntryId" IS NULL OR NEW."ReversalBankJournalEntryLineId" IS NULL THEN
                                RAISE EXCEPTION 'independent adjustment reversal evidence is required' USING ERRCODE = '23514';
                            END IF;
                        ELSE RAISE EXCEPTION 'invalid bank-adjustment transition' USING ERRCODE = '55000';
                        END IF;
                    END IF;
                    RETURN NEW;
                END $function$;

                CREATE OR REPLACE FUNCTION public.nexora_treasury_guard_distribution()
                RETURNS trigger LANGUAGE plpgsql SET search_path = pg_catalog, public AS $function$
                BEGIN
                    IF TG_OP <> 'INSERT' THEN RAISE EXCEPTION 'bank adjustment distributions are append-only' USING ERRCODE = '55000'; END IF;
                    IF NOT EXISTS (SELECT 1 FROM public."BankAdjustments" adjustment
                        WHERE adjustment."BusinessUnitId" = NEW."BusinessUnitId" AND adjustment."Id" = NEW."BankAdjustmentId"
                          AND adjustment."Status" = 'Draft') THEN
                        RAISE EXCEPTION 'distributions can only be added to a draft adjustment' USING ERRCODE = '55000';
                    END IF;
                    RETURN NEW;
                END $function$;

                CREATE OR REPLACE FUNCTION public.nexora_treasury_validate_adjustment()
                RETURNS trigger LANGUAGE plpgsql SET search_path = pg_catalog, public AS $function$
                DECLARE current_row public."BankAdjustments"%ROWTYPE; DECLARE distribution_total numeric(18,2);
                DECLARE bank_signed numeric(18,2); DECLARE bank_ledger bigint; DECLARE mismatch integer;
                DECLARE distribution_count integer; DECLARE journal_line_count integer;
                BEGIN
                    SELECT * INTO current_row FROM public."BankAdjustments" WHERE "BusinessUnitId" = NEW."BusinessUnitId" AND "Id" = NEW."Id";
                    IF NOT FOUND THEN RETURN NULL; END IF;
                    SELECT COALESCE(sum("Amount"),0) INTO distribution_total FROM public."BankAdjustmentDistributions"
                        WHERE "BusinessUnitId" = current_row."BusinessUnitId" AND "BankAdjustmentId" = current_row."Id";
                    IF distribution_total <> current_row."Amount" THEN RAISE EXCEPTION 'adjustment distributions must equal the adjustment amount' USING ERRCODE = '23514'; END IF;
                    IF current_row."Status" IN ('Posted','Reversed') THEN
                        SELECT line."SignedAmount", account."LedgerAccountId" INTO STRICT bank_signed, bank_ledger
                        FROM public."BankStatementLines" line JOIN public."BankAccounts" account
                          ON account."BusinessUnitId" = line."BusinessUnitId" AND account."Id" = line."BankAccountId"
                        WHERE line."BusinessUnitId" = current_row."BusinessUnitId" AND line."Id" = current_row."BankStatementLineId"
                          AND account."Id" = current_row."BankAccountId";
                        SELECT count(*) INTO mismatch FROM public."JournalEntries" journal
                        WHERE journal."BusinessUnitId" = current_row."BusinessUnitId" AND journal."Id" = current_row."JournalEntryId"
                          AND journal."Status" IN ('Posted','Reversed') AND journal."SourceType" = 'BankAdjustment'
                          AND journal."SourceReference" = current_row."Id"::text
                          AND journal."SourceVersion" = CASE WHEN current_row."Status" = 'Posted'
                              THEN current_row."Version" - 1 ELSE current_row."Version" - 2 END
                          AND journal."TotalDebit" = current_row."Amount" AND journal."TotalCredit" = current_row."Amount";
                        SELECT count(*) INTO distribution_count FROM public."BankAdjustmentDistributions" distribution
                            WHERE distribution."BusinessUnitId" = current_row."BusinessUnitId"
                              AND distribution."BankAdjustmentId" = current_row."Id";
                        SELECT count(*) INTO journal_line_count FROM public."JournalEntryLines" jl
                            WHERE jl."BusinessUnitId" = current_row."BusinessUnitId"
                              AND jl."JournalEntryId" = current_row."JournalEntryId";
                        IF mismatch <> 1 OR NOT EXISTS (SELECT 1 FROM public."JournalEntryLines" jl
                            WHERE jl."BusinessUnitId" = current_row."BusinessUnitId" AND jl."Id" = current_row."BankJournalEntryLineId"
                              AND jl."JournalEntryId" = current_row."JournalEntryId" AND jl."LedgerAccountId" = bank_ledger
                              AND jl."Sequence" = 1 AND jl."SourceReference" = 'BADJ:' || current_row."Id"::text || ':BANK'
                              AND ((bank_signed > 0 AND jl."FunctionalDebit" = current_row."Amount" AND jl."FunctionalCredit" = 0)
                                OR (bank_signed < 0 AND jl."FunctionalCredit" = current_row."Amount" AND jl."FunctionalDebit" = 0)))
                           OR journal_line_count <> distribution_count + 1
                           OR EXISTS (SELECT 1 FROM public."BankAdjustmentDistributions" distribution
                                LEFT JOIN public."LedgerAccounts" account
                                  ON account."BusinessUnitId" = distribution."BusinessUnitId"
                                 AND account."Id" = distribution."LedgerAccountId"
                                WHERE distribution."BusinessUnitId" = current_row."BusinessUnitId"
                                  AND distribution."BankAdjustmentId" = current_row."Id"
                                  AND (account."Id" IS NULL OR account."IsActive" IS NOT TRUE
                                    OR account."IsControlAccount" IS TRUE OR account."Id" = bank_ledger
                                    OR NOT EXISTS (SELECT 1 FROM public."JournalEntryLines" jl
                                        WHERE jl."BusinessUnitId" = current_row."BusinessUnitId"
                                          AND jl."JournalEntryId" = current_row."JournalEntryId"
                                          AND jl."Sequence" = distribution."Sequence" + 1
                                          AND jl."LedgerAccountId" = distribution."LedgerAccountId"
                                          AND jl."SourceReference" = 'BADJ:' || current_row."Id"::text || ':DIST:' || distribution."Sequence"::text
                                          AND ((bank_signed > 0 AND jl."FunctionalDebit" = 0 AND jl."FunctionalCredit" = distribution."Amount")
                                            OR (bank_signed < 0 AND jl."FunctionalDebit" = distribution."Amount" AND jl."FunctionalCredit" = 0))))) THEN
                            RAISE EXCEPTION 'posted adjustment journal does not match immutable treasury evidence' USING ERRCODE = '23514';
                        END IF;
                        IF current_row."Status" = 'Reversed' AND (NOT EXISTS (SELECT 1 FROM public."JournalEntries" reversal
                                WHERE reversal."BusinessUnitId" = current_row."BusinessUnitId"
                                  AND reversal."Id" = current_row."ReversalJournalEntryId"
                                  AND reversal."ReversesJournalEntryId" = current_row."JournalEntryId"
                                  AND reversal."SourceType" = 'JournalReversal' AND reversal."Status" = 'Posted'
                                  AND reversal."TotalDebit" = current_row."Amount" AND reversal."TotalCredit" = current_row."Amount")
                            OR NOT EXISTS (SELECT 1 FROM public."JournalEntryLines" reversal_line
                                WHERE reversal_line."BusinessUnitId" = current_row."BusinessUnitId"
                                  AND reversal_line."Id" = current_row."ReversalBankJournalEntryLineId"
                                  AND reversal_line."JournalEntryId" = current_row."ReversalJournalEntryId"
                                  AND reversal_line."LedgerAccountId" = bank_ledger
                                  AND ((bank_signed > 0 AND reversal_line."FunctionalDebit" = 0
                                        AND reversal_line."FunctionalCredit" = current_row."Amount")
                                    OR (bank_signed < 0 AND reversal_line."FunctionalDebit" = current_row."Amount"
                                        AND reversal_line."FunctionalCredit" = 0)))) THEN
                            RAISE EXCEPTION 'reversed adjustment requires an exact posted journal reversal' USING ERRCODE = '23514';
                        END IF;
                    END IF;
                    RETURN NULL;
                END $function$;

                CREATE OR REPLACE FUNCTION public.nexora_treasury_validate_cash_bridge()
                RETURNS trigger LANGUAGE plpgsql SET search_path = pg_catalog, public AS $function$
                DECLARE payment public."CustomerPayments"%ROWTYPE; DECLARE refund public."CustomerRefunds"%ROWTYPE;
                DECLARE bank_ledger bigint; DECLARE ar_ledger bigint; DECLARE unapplied_ledger bigint;
                DECLARE bank_currency bigint; DECLARE bank_ledger_currency bigint; DECLARE book_currency bigint;
                DECLARE allocated numeric(18,2); DECLARE unapplied numeric(18,2); DECLARE line_count integer;
                DECLARE expected_line_count integer; DECLARE expected_source_version bigint; DECLARE unapplied_sequence integer;
                BEGIN
                    IF TG_TABLE_NAME = 'CustomerPayments' THEN
                        SELECT * INTO payment FROM public."CustomerPayments" WHERE "BusinessUnitId" = NEW."BusinessUnitId" AND "Id" = NEW."Id";
                        IF NOT FOUND THEN RETURN NULL; END IF;
                        IF payment."AccountingBridgeRequired" IS FALSE THEN RETURN NULL; END IF;
                        IF payment."BankAccountId" IS NULL OR payment."JournalEntryId" IS NULL THEN
                            RAISE EXCEPTION 'new customer payments require durable bank and journal evidence' USING ERRCODE = '23514';
                        END IF;
                        SELECT bank."LedgerAccountId", bank."CurrencyId", ledger."CurrencyId"
                            INTO STRICT bank_ledger, bank_currency, bank_ledger_currency
                            FROM public."BankAccounts" bank JOIN public."LedgerAccounts" ledger
                              ON ledger."BusinessUnitId" = bank."BusinessUnitId" AND ledger."Id" = bank."LedgerAccountId"
                            WHERE bank."BusinessUnitId" = payment."BusinessUnitId" AND bank."Id" = payment."BankAccountId";
                        SELECT "ReceivablesControlAccountId", "UnappliedCashAccountId", "FunctionalCurrencyId"
                            INTO STRICT ar_ledger, unapplied_ledger, book_currency
                            FROM public."LedgerBooks" WHERE "BusinessUnitId" = payment."BusinessUnitId";
                        SELECT COALESCE(sum("Amount"),0) INTO allocated FROM public."PaymentAllocations"
                            WHERE "BusinessUnitId" = payment."BusinessUnitId" AND "CustomerPaymentId" = payment."Id";
                        unapplied := payment."Amount" - allocated;
                        expected_line_count := 1;
                        unapplied_sequence := 2;
                        IF allocated > 0 THEN expected_line_count := expected_line_count + 1; END IF;
                        IF allocated > 0 THEN unapplied_sequence := 3; END IF;
                        IF unapplied > 0 THEN expected_line_count := expected_line_count + 1; END IF;
                        expected_source_version := payment."Version";
                        IF payment."Status" = 'Reversed' THEN expected_source_version := expected_source_version - 1; END IF;
                        SELECT count(*) INTO line_count FROM public."JournalEntryLines" line
                            WHERE line."BusinessUnitId" = payment."BusinessUnitId" AND line."JournalEntryId" = payment."JournalEntryId";
                        IF payment."CurrencyId" <> bank_currency OR payment."CurrencyId" <> bank_ledger_currency
                           OR payment."CurrencyId" <> book_currency
                           OR NOT EXISTS (SELECT 1 FROM public."JournalEntries" journal WHERE journal."BusinessUnitId" = payment."BusinessUnitId"
                            AND journal."Id" = payment."JournalEntryId" AND journal."SourceType" = 'CustomerPayment'
                            AND journal."SourceReference" = payment."Id"::text AND journal."Status" IN ('Posted','Reversed')
                            AND journal."SourceVersion" = expected_source_version AND journal."FunctionalCurrencyId" = payment."CurrencyId"
                            AND journal."TotalDebit" = payment."Amount" AND journal."TotalCredit" = payment."Amount")
                           OR allocated < 0 OR allocated > payment."Amount"
                           OR line_count <> expected_line_count
                           OR NOT EXISTS (SELECT 1 FROM public."JournalEntryLines" line
                            WHERE line."BusinessUnitId" = payment."BusinessUnitId" AND line."JournalEntryId" = payment."JournalEntryId"
                              AND line."Sequence" = 1 AND line."LedgerAccountId" = bank_ledger
                              AND line."SourceReference" = 'PAY:' || payment."Id"::text || ':BANK'
                              AND line."TransactionCurrencyId" = payment."CurrencyId" AND line."ExchangeRate" = 1
                              AND line."TransactionDebit" = payment."Amount" AND line."TransactionCredit" = 0
                              AND line."FunctionalDebit" = payment."Amount" AND line."FunctionalCredit" = 0)
                           OR (allocated > 0 AND NOT EXISTS (SELECT 1 FROM public."JournalEntryLines" line
                                WHERE line."BusinessUnitId" = payment."BusinessUnitId" AND line."JournalEntryId" = payment."JournalEntryId"
                                  AND line."Sequence" = 2 AND line."LedgerAccountId" = ar_ledger
                                  AND line."SourceReference" = 'PAY:' || payment."Id"::text || ':AR'
                                  AND line."TransactionCurrencyId" = payment."CurrencyId" AND line."ExchangeRate" = 1
                                  AND line."TransactionDebit" = 0 AND line."TransactionCredit" = allocated
                                  AND line."FunctionalDebit" = 0 AND line."FunctionalCredit" = allocated))
                           OR (unapplied > 0 AND NOT EXISTS (SELECT 1 FROM public."JournalEntryLines" line
                                WHERE line."BusinessUnitId" = payment."BusinessUnitId" AND line."JournalEntryId" = payment."JournalEntryId"
                                  AND line."Sequence" = unapplied_sequence
                                  AND line."LedgerAccountId" = unapplied_ledger
                                  AND line."SourceReference" = 'PAY:' || payment."Id"::text || ':UNAPPLIED'
                                  AND line."TransactionCurrencyId" = payment."CurrencyId" AND line."ExchangeRate" = 1
                                  AND line."TransactionDebit" = 0 AND line."TransactionCredit" = unapplied
                                  AND line."FunctionalDebit" = 0 AND line."FunctionalCredit" = unapplied)) THEN
                            RAISE EXCEPTION 'customer payment journal provenance is invalid' USING ERRCODE = '23514';
                        END IF;
                        IF payment."Status" = 'Reversed' AND (payment."ReversalJournalEntryId" IS NULL OR NOT EXISTS
                            (SELECT 1 FROM public."JournalEntries" reversal WHERE reversal."BusinessUnitId" = payment."BusinessUnitId"
                              AND reversal."Id" = payment."ReversalJournalEntryId" AND reversal."ReversesJournalEntryId" = payment."JournalEntryId"
                              AND reversal."Status" = 'Posted' AND reversal."FunctionalCurrencyId" = payment."CurrencyId")) THEN
                            RAISE EXCEPTION 'reversed customer payment requires an exact posted journal reversal' USING ERRCODE = '23514';
                        END IF;
                    ELSE
                        SELECT * INTO refund FROM public."CustomerRefunds" WHERE "BusinessUnitId" = NEW."BusinessUnitId" AND "Id" = NEW."Id";
                        IF NOT FOUND OR refund."PostingStatus" <> 'Settled' THEN RETURN NULL; END IF;
                        IF refund."BankAccountId" IS NULL OR refund."JournalEntryId" IS NULL THEN
                            RAISE EXCEPTION 'settled refunds require durable bank and journal evidence' USING ERRCODE = '23514';
                        END IF;
                        SELECT bank."LedgerAccountId", bank."CurrencyId", ledger."CurrencyId"
                            INTO STRICT bank_ledger, bank_currency, bank_ledger_currency
                            FROM public."BankAccounts" bank JOIN public."LedgerAccounts" ledger
                              ON ledger."BusinessUnitId" = bank."BusinessUnitId" AND ledger."Id" = bank."LedgerAccountId"
                            WHERE bank."BusinessUnitId" = refund."BusinessUnitId" AND bank."Id" = refund."BankAccountId";
                        SELECT "UnappliedCashAccountId", "FunctionalCurrencyId" INTO STRICT unapplied_ledger, book_currency FROM public."LedgerBooks"
                            WHERE "BusinessUnitId" = refund."BusinessUnitId";
                        SELECT count(*) INTO line_count FROM public."JournalEntryLines" line
                            WHERE line."BusinessUnitId" = refund."BusinessUnitId" AND line."JournalEntryId" = refund."JournalEntryId";
                        IF refund."CurrencyId" <> bank_currency OR refund."CurrencyId" <> bank_ledger_currency
                           OR refund."CurrencyId" <> book_currency
                           OR NOT EXISTS (SELECT 1 FROM public."JournalEntries" journal WHERE journal."BusinessUnitId" = refund."BusinessUnitId"
                            AND journal."Id" = refund."JournalEntryId" AND journal."SourceType" = 'CustomerRefund'
                            AND journal."SourceReference" = refund."Id"::text AND journal."Status" = 'Posted'
                            AND journal."SourceVersion" = refund."Version" AND journal."FunctionalCurrencyId" = refund."CurrencyId"
                            AND journal."TotalDebit" = refund."Amount" AND journal."TotalCredit" = refund."Amount")
                           OR line_count <> 2
                           OR NOT EXISTS (SELECT 1 FROM public."JournalEntryLines" line
                            WHERE line."BusinessUnitId" = refund."BusinessUnitId" AND line."JournalEntryId" = refund."JournalEntryId"
                              AND line."Sequence" = 1 AND line."LedgerAccountId" = unapplied_ledger
                              AND line."SourceReference" = 'REF:' || refund."Id"::text || ':UNAPPLIED'
                              AND line."TransactionCurrencyId" = refund."CurrencyId" AND line."ExchangeRate" = 1
                              AND line."TransactionDebit" = refund."Amount" AND line."TransactionCredit" = 0
                              AND line."FunctionalDebit" = refund."Amount" AND line."FunctionalCredit" = 0)
                           OR NOT EXISTS (SELECT 1 FROM public."JournalEntryLines" line
                            WHERE line."BusinessUnitId" = refund."BusinessUnitId" AND line."JournalEntryId" = refund."JournalEntryId"
                              AND line."Sequence" = 2 AND line."LedgerAccountId" = bank_ledger
                              AND line."SourceReference" = 'REF:' || refund."Id"::text || ':BANK'
                              AND line."TransactionCurrencyId" = refund."CurrencyId" AND line."ExchangeRate" = 1
                              AND line."TransactionCredit" = refund."Amount" AND line."TransactionDebit" = 0
                              AND line."FunctionalCredit" = refund."Amount" AND line."FunctionalDebit" = 0) THEN
                            RAISE EXCEPTION 'customer refund journal provenance is invalid' USING ERRCODE = '23514';
                        END IF;
                    END IF;
                    RETURN NULL;
                END; $function$;

                CREATE OR REPLACE FUNCTION public.nexora_payment_posted_immutable()
                RETURNS trigger LANGUAGE plpgsql SECURITY DEFINER
                SET search_path = pg_catalog, public AS $function$
                BEGIN
                    IF TG_OP = 'INSERT' THEN
                        IF NEW."AccountingBridgeRequired" IS NOT TRUE THEN
                            RAISE EXCEPTION 'new customer payments must use the governed accounting bridge' USING ERRCODE = '23514';
                        END IF;
                        RETURN NEW;
                    END IF;
                    IF TG_OP = 'DELETE' THEN
                        RAISE EXCEPTION 'posted customer payments cannot be deleted' USING ERRCODE = '55000';
                    END IF;
                    IF OLD."AccountingBridgeRequired" IS TRUE AND NEW."AccountingBridgeRequired" IS NOT TRUE THEN
                        RAISE EXCEPTION 'the governed accounting bridge marker cannot be disabled' USING ERRCODE = '55000';
                    END IF;
                    IF OLD."Status" = 'Posted' AND NEW."Status" = 'Posted'
                       AND OLD."JournalEntryId" IS NULL AND NEW."JournalEntryId" IS NOT NULL
                       AND NEW."ReversalJournalEntryId" IS NULL
                       AND NEW."Version" = OLD."Version"
                       AND (NEW."BusinessUnitId", NEW."CustomerId", NEW."CommercialCaseId", NEW."CurrencyId",
                            NEW."ReceiptNumber", NEW."PaymentDate", NEW."Amount", NEW."Method", NEW."BankReference",
                            NEW."BankAccountId", NEW."IdempotencyKey", NEW."RequestHash", NEW."CreatedBy", NEW."CreatedOn",
                            NEW."ReversedBy", NEW."ReversedOn", NEW."ReversalReason")
                           IS NOT DISTINCT FROM
                           (OLD."BusinessUnitId", OLD."CustomerId", OLD."CommercialCaseId", OLD."CurrencyId",
                            OLD."ReceiptNumber", OLD."PaymentDate", OLD."Amount", OLD."Method", OLD."BankReference",
                            OLD."BankAccountId", OLD."IdempotencyKey", OLD."RequestHash", OLD."CreatedBy", OLD."CreatedOn",
                            OLD."ReversedBy", OLD."ReversedOn", OLD."ReversalReason") THEN
                        RETURN NEW;
                    END IF;
                    IF OLD."Status" = 'Posted' AND NEW."Status" = 'Reversed'
                       AND ((OLD."JournalEntryId" IS NOT NULL AND NEW."JournalEntryId" = OLD."JournalEntryId"
                              AND OLD."ReversalJournalEntryId" IS NULL AND NEW."ReversalJournalEntryId" IS NOT NULL)
                            OR (OLD."JournalEntryId" IS NULL AND NEW."JournalEntryId" IS NULL
                              AND OLD."ReversalJournalEntryId" IS NULL AND NEW."ReversalJournalEntryId" IS NULL))
                       AND NEW."ReversedBy" IS NOT NULL
                       AND lower(trim(NEW."ReversedBy")) <> lower(trim(OLD."CreatedBy"))
                       AND NEW."ReversedOn" IS NOT NULL AND length(trim(NEW."ReversalReason")) > 0
                       AND NEW."Version" = OLD."Version" + 1
                       AND NOT EXISTS (
                           SELECT 1 FROM public."CustomerRefunds" refund
                           WHERE refund."BusinessUnitId" = OLD."BusinessUnitId"
                             AND refund."SourcePaymentId" = OLD."Id"
                             AND refund."Status" IN ('Approved', 'Released'))
                       AND (NEW."BusinessUnitId", NEW."CustomerId", NEW."CommercialCaseId", NEW."CurrencyId",
                            NEW."ReceiptNumber", NEW."PaymentDate", NEW."Amount", NEW."Method", NEW."BankReference",
                            NEW."BankAccountId", NEW."IdempotencyKey", NEW."RequestHash", NEW."CreatedBy", NEW."CreatedOn")
                           IS NOT DISTINCT FROM
                           (OLD."BusinessUnitId", OLD."CustomerId", OLD."CommercialCaseId", OLD."CurrencyId",
                            OLD."ReceiptNumber", OLD."PaymentDate", OLD."Amount", OLD."Method", OLD."BankReference",
                            OLD."BankAccountId", OLD."IdempotencyKey", OLD."RequestHash", OLD."CreatedBy", OLD."CreatedOn") THEN
                        RETURN NEW;
                    END IF;
                    RAISE EXCEPTION 'posted customer payments are immutable or reserved by an active refund' USING ERRCODE = '55000';
                END $function$;

                DROP TRIGGER IF EXISTS trg_payment_posted_immutable ON public."CustomerPayments";
                CREATE TRIGGER trg_payment_posted_immutable
                    BEFORE INSERT OR UPDATE OR DELETE ON public."CustomerPayments"
                    FOR EACH ROW EXECUTE FUNCTION public.nexora_payment_posted_immutable();

                CREATE OR REPLACE FUNCTION public.nexora_payment_outbox_event()
                RETURNS trigger LANGUAGE plpgsql SECURITY DEFINER
                SET search_path = pg_catalog, public AS $function$
                DECLARE event_type text; DECLARE event_time timestamp without time zone; DECLARE event_actor text;
                DECLARE evidence jsonb;
                BEGIN
                    IF TG_OP = 'INSERT' AND NEW."Status" = 'Posted' THEN
                        event_type := 'finance.payment.posted';
                        event_time := coalesce(NEW."CreatedOn", (CURRENT_TIMESTAMP AT TIME ZONE 'UTC'));
                        event_actor := NEW."CreatedBy";
                    ELSIF TG_OP = 'UPDATE' AND OLD."Status" = 'Posted' AND NEW."Status" = 'Reversed' THEN
                        event_type := 'finance.payment.reversed';
                        event_time := coalesce(NEW."ReversedOn", (CURRENT_TIMESTAMP AT TIME ZONE 'UTC'));
                        event_actor := coalesce(to_jsonb(NEW)->>'ReversedBy', NEW."CreatedBy");
                    ELSE
                        RETURN NEW;
                    END IF;
                    evidence := jsonb_build_object(
                        'receiptNumber', NEW."ReceiptNumber", 'amount', NEW."Amount", 'version', NEW."Version",
                        'BankAccountId', to_jsonb(NEW)->'BankAccountId',
                        'JournalEntryId', to_jsonb(NEW)->'JournalEntryId',
                        'ReversalJournalEntryId', to_jsonb(NEW)->'ReversalJournalEntryId');
                    PERFORM public.nexora_write_finance_audit(NEW."BusinessUnitId", 'CustomerPayment',
                        NEW."Id", CASE WHEN event_type = 'finance.payment.posted' THEN 'Posted' ELSE 'Reversed' END,
                        event_actor, evidence, event_time);
                    PERFORM public.nexora_write_finance_outbox(NEW."BusinessUnitId", 'CustomerPayment',
                        NEW."Id", NEW."Version", event_type, evidence || jsonb_build_object(
                            'Id', NEW."Id", 'Status', NEW."Status", 'CustomerId', NEW."CustomerId",
                            'CommercialCaseId', NEW."CommercialCaseId", 'CurrencyId', NEW."CurrencyId",
                            'Actor', event_actor), event_time);
                    RETURN NEW;
                END
                $function$;

                CREATE OR REPLACE FUNCTION public.nexora_bank_evidence_event()
                RETURNS trigger LANGUAGE plpgsql SECURITY DEFINER
                SET search_path = pg_catalog, public AS $function$
                DECLARE aggregate_type text; DECLARE aggregate_version bigint; DECLARE action_name text;
                DECLARE event_name text; DECLARE actor_id text; DECLARE occurred_at timestamp without time zone;
                DECLARE payload jsonb; DECLARE event_id uuid; DECLARE seed text;
                BEGIN
                    aggregate_type := CASE TG_TABLE_NAME
                        WHEN 'BankAccounts' THEN 'BankAccount'
                        WHEN 'BankStatementImports' THEN 'BankStatementImport'
                        WHEN 'ReconciliationRuns' THEN 'ReconciliationRun'
                        WHEN 'ReconciliationMatches' THEN 'ReconciliationMatch'
                        WHEN 'BankMatchingRules' THEN 'BankMatchingRule'
                        WHEN 'BankAdjustments' THEN 'BankAdjustment'
                        ELSE TG_TABLE_NAME END;
                    aggregate_version := COALESCE((to_jsonb(NEW)->>'Version')::bigint,
                        (to_jsonb(NEW)->>'RecordVersion')::bigint, 1);
                    action_name := CASE WHEN TG_OP = 'INSERT' THEN 'Created'
                        ELSE COALESCE(to_jsonb(NEW)->>'Status', 'Updated') END;
                    actor_id := CASE to_jsonb(NEW)->>'Status'
                        WHEN 'Reversed' THEN to_jsonb(NEW)->>'ReversedBy'
                        WHEN 'Retired' THEN to_jsonb(NEW)->>'RetiredBy'
                        WHEN 'Active' THEN to_jsonb(NEW)->>'ActivatedBy'
                        WHEN 'Rejected' THEN to_jsonb(NEW)->>'RejectedBy'
                        WHEN 'Cancelled' THEN to_jsonb(NEW)->>'CancelledBy'
                        WHEN 'Reopened' THEN to_jsonb(NEW)->>'ReopenedBy'
                        WHEN 'Confirmed' THEN to_jsonb(NEW)->>'ConfirmedBy'
                        WHEN 'Voided' THEN to_jsonb(NEW)->>'VoidedBy'
                        WHEN 'InReview' THEN to_jsonb(NEW)->>'SubmittedBy'
                        WHEN 'Posted' THEN to_jsonb(NEW)->>'ApprovedBy'
                        WHEN 'Approved' THEN to_jsonb(NEW)->>'ApprovedBy'
                        ELSE NULL END;
                    actor_id := COALESCE(actor_id, to_jsonb(NEW)->>'StatusChangedBy',
                        to_jsonb(NEW)->>'ImportedBy', to_jsonb(NEW)->>'CreatedBy',
                        to_jsonb(NEW)->>'PreparedBy', 'system:treasury');
                    occurred_at := clock_timestamp() AT TIME ZONE 'UTC'; payload := to_jsonb(NEW) - 'RawPayload';
                    IF TG_TABLE_NAME = 'BankAdjustments' THEN
                        payload := payload || jsonb_build_object('Distributions', COALESCE((SELECT jsonb_agg(to_jsonb(distribution)
                            ORDER BY distribution."Sequence") FROM public."BankAdjustmentDistributions" distribution
                            WHERE distribution."BusinessUnitId" = NEW."BusinessUnitId"
                              AND distribution."BankAdjustmentId" = NEW."Id"), '[]'::jsonb));
                    END IF;
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
                END $function$;

                CREATE TRIGGER trg_bankadjustments_guard BEFORE INSERT OR UPDATE OR DELETE ON public."BankAdjustments"
                    FOR EACH ROW EXECUTE FUNCTION public.nexora_treasury_guard_adjustment();
                CREATE TRIGGER trg_bankadjustmentdistributions_guard BEFORE INSERT OR UPDATE OR DELETE ON public."BankAdjustmentDistributions"
                    FOR EACH ROW EXECUTE FUNCTION public.nexora_treasury_guard_distribution();
                CREATE CONSTRAINT TRIGGER trg_bankadjustments_validate AFTER INSERT OR UPDATE ON public."BankAdjustments"
                    DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION public.nexora_treasury_validate_adjustment();
                CREATE CONSTRAINT TRIGGER trg_customerpayments_cash_bridge AFTER INSERT OR UPDATE ON public."CustomerPayments"
                    DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION public.nexora_treasury_validate_cash_bridge();
                CREATE CONSTRAINT TRIGGER trg_customerrefunds_cash_bridge AFTER INSERT OR UPDATE ON public."CustomerRefunds"
                    DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION public.nexora_treasury_validate_cash_bridge();
                CREATE CONSTRAINT TRIGGER trg_bankmatchingrules_evidence AFTER INSERT OR UPDATE ON public."BankMatchingRules"
                    DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION public.nexora_bank_evidence_event();
                CREATE CONSTRAINT TRIGGER trg_bankadjustments_evidence AFTER INSERT OR UPDATE ON public."BankAdjustments"
                    DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION public.nexora_bank_evidence_event();

                DO $block$
                DECLARE table_name text;
                BEGIN
                    FOREACH table_name IN ARRAY ARRAY['BankMatchingRules','ReconciliationRunRules','BankAdjustments','BankAdjustmentDistributions'] LOOP
                        EXECUTE format('ALTER TABLE public.%I ENABLE ROW LEVEL SECURITY', table_name);
                        EXECUTE format('ALTER TABLE public.%I FORCE ROW LEVEL SECURITY', table_name);
                        EXECUTE format('CREATE POLICY nexora_tenant_isolation ON public.%I TO nexora_tenant_app USING ("BusinessUnitId" = NULLIF(current_setting(''nexora.business_unit_id'', true), '''')::bigint) WITH CHECK ("BusinessUnitId" = NULLIF(current_setting(''nexora.business_unit_id'', true), '''')::bigint)', table_name);
                        EXECUTE format('CREATE TRIGGER %I BEFORE TRUNCATE ON public.%I FOR EACH STATEMENT EXECUTE FUNCTION public.nexora_finance_reject_truncate()', lower(table_name) || '_reject_truncate', table_name);
                        EXECUTE format('GRANT SELECT, INSERT, UPDATE ON public.%I TO nexora_tenant_app', table_name);
                        EXECUTE format('REVOKE DELETE, TRUNCATE ON public.%I FROM nexora_tenant_app', table_name);
                    END LOOP;
                END $block$;
                REVOKE UPDATE ON public."ReconciliationRunRules", public."BankAdjustmentDistributions" FROM nexora_tenant_app;
                GRANT UPDATE ON public."LedgerBooks" TO nexora_tenant_app;
                GRANT USAGE ON SEQUENCE public."BankMatchingRules_Id_seq", public."ReconciliationRunRules_Id_seq",
                    public."BankAdjustments_Id_seq", public."BankAdjustmentDistributions_Id_seq" TO nexora_tenant_app;

                INSERT INTO public."Module" ("ModuleName","Description","IsActive","CreatedBy","CreatedOn") VALUES
                    ('Bank Matching Rule Administration','Immutable tenant matching-rule versions',true,'migration:treasury-governance:v1',now()),
                    ('Bank Matching Rule Approval','Independent matching-rule approval and activation',true,'migration:treasury-governance:v1',now()),
                    ('Bank Adjustments','Governed bank fee, interest, and adjustment preparation',true,'migration:treasury-governance:v1',now()),
                    ('Bank Adjustment Approval','Independent bank adjustment posting and reversal',true,'migration:treasury-governance:v1',now())
                ON CONFLICT ("ModuleName") DO NOTHING;
                INSERT INTO public."RolePermissions"
                    ("RoleID","ModuleID","BusinessUnitID","CanCreate","CanEdit","CanDelete","CreatedBy","CreatedOn")
                SELECT role."SetupID", module."ID", role."BusinessUnitID", true, true, false,
                    'migration:treasury-governance:v1', now()
                FROM public."Setup_Master" role CROSS JOIN public."Module" module
                WHERE lower(replace(role."SetupType",' ','')) = 'role'
                  AND module."ModuleName" IN ('Bank Matching Rule Administration','Bank Matching Rule Approval','Bank Adjustments','Bank Adjustment Approval')
                  AND ((module."ModuleName" IN ('Bank Matching Rule Approval','Bank Adjustment Approval')
                        AND (upper(coalesce(role."SetupCode",'')) ~ '(CONTROLLER|ADMIN)' OR upper(coalesce(role."SetupValue",'')) ~ '(CONTROLLER|ADMIN)'))
                    OR (module."ModuleName" NOT IN ('Bank Matching Rule Approval','Bank Adjustment Approval')
                        AND (upper(coalesce(role."SetupCode",'')) ~ '(TREASUR|FINANCE|ACCOUNT|ADMIN)' OR upper(coalesce(role."SetupValue",'')) ~ '(TREASUR|FINANCE|ACCOUNT|ADMIN)')))
                  AND NOT EXISTS (SELECT 1 FROM public."RolePermissions" existing WHERE existing."RoleID" = role."SetupID"
                    AND existing."BusinessUnitID" = role."BusinessUnitID" AND existing."ModuleID" = module."ID");
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DO $downgrade$
                BEGIN
                    IF EXISTS (SELECT 1 FROM public."BankAdjustments")
                       OR EXISTS (SELECT 1 FROM public."ReconciliationRuns"
                            WHERE "RuleSetHash" IS NOT NULL OR "RuleSetSnapshotOn" IS NOT NULL)
                       OR EXISTS (SELECT 1 FROM public."ReconciliationMatches"
                            WHERE "BankMatchingRuleId" IS NOT NULL OR "RuleDefinitionHash" IS NOT NULL)
                       OR EXISTS (SELECT 1 FROM public."JournalEntries"
                            WHERE "SourceType" IN ('BankAdjustment','CustomerPayment','CustomerRefund'))
                       OR EXISTS (SELECT 1 FROM public."LedgerBooks"
                            WHERE "ReceivablesControlAccountId" IS NOT NULL OR "UnappliedCashAccountId" IS NOT NULL)
                       OR EXISTS (SELECT 1 FROM public."CustomerPayments"
                            WHERE "JournalEntryId" IS NOT NULL OR "ReversalJournalEntryId" IS NOT NULL)
                       OR EXISTS (SELECT 1 FROM public."CustomerRefunds" WHERE "JournalEntryId" IS NOT NULL) THEN
                        RAISE EXCEPTION 'cannot downgrade treasury governance while governed accounting evidence exists; retain this migration or archive the governed tenant data'
                            USING ERRCODE = '55000';
                    END IF;
                END
                $downgrade$;

                REVOKE UPDATE ON TABLE public."LedgerBooks" FROM nexora_tenant_app;

                CREATE OR REPLACE FUNCTION public.nexora_gl_guard_book()
                RETURNS trigger LANGUAGE plpgsql SECURITY DEFINER
                SET search_path = pg_catalog, public AS $function$
                DECLARE actor_id text;
                BEGIN
                    IF TG_OP <> 'INSERT' THEN
                        RAISE EXCEPTION 'the accounting book is immutable and cannot be changed or deleted' USING ERRCODE = '55000';
                    END IF;
                    IF current_setting('role', true) = 'nexora_tenant_app' THEN
                        actor_id := public.nexora_gl_authenticated_actor(NEW."BusinessUnitId");
                    END IF;
                    IF NEW."Version" <> 1 OR (actor_id IS NOT NULL AND NEW."CreatedBy" <> actor_id)
                       OR NOT EXISTS (SELECT 1 FROM public."Currency" c WHERE c."BusinessUnitID" = NEW."BusinessUnitId"
                            AND c."ID" = NEW."FunctionalCurrencyId" AND c."IsActive" IS TRUE AND c."IsBaseCurrency" IS TRUE) THEN
                        RAISE EXCEPTION 'the accounting book requires the tenant active base currency and authenticated creator' USING ERRCODE = '23514';
                    END IF;
                    RETURN NEW;
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

                DROP TRIGGER IF EXISTS trg_payment_posted_immutable ON public."CustomerPayments";
                CREATE TRIGGER trg_payment_posted_immutable
                    BEFORE UPDATE OR DELETE ON public."CustomerPayments"
                    FOR EACH ROW EXECUTE FUNCTION public.nexora_payment_posted_immutable();

                DELETE FROM public."RolePermissions" permissions USING public."Module" module
                WHERE permissions."ModuleID" = module."ID" AND module."ModuleName" IN
                    ('Bank Matching Rule Administration','Bank Matching Rule Approval','Bank Adjustments','Bank Adjustment Approval')
                  AND permissions."CreatedBy" = 'migration:treasury-governance:v1';
                DELETE FROM public."Module" WHERE "ModuleName" IN
                    ('Bank Matching Rule Administration','Bank Matching Rule Approval','Bank Adjustments','Bank Adjustment Approval')
                  AND "CreatedBy" = 'migration:treasury-governance:v1';
                DROP FUNCTION IF EXISTS public.nexora_treasury_guard_rule() CASCADE;
                DROP FUNCTION IF EXISTS public.nexora_treasury_guard_snapshot() CASCADE;
                DROP FUNCTION IF EXISTS public.nexora_treasury_validate_run_rules() CASCADE;
                DROP FUNCTION IF EXISTS public.nexora_treasury_validate_match_rule() CASCADE;
                DROP FUNCTION IF EXISTS public.nexora_treasury_guard_adjustment() CASCADE;
                DROP FUNCTION IF EXISTS public.nexora_treasury_guard_distribution() CASCADE;
                DROP FUNCTION IF EXISTS public.nexora_treasury_validate_adjustment() CASCADE;
                DROP FUNCTION IF EXISTS public.nexora_treasury_validate_cash_bridge() CASCADE;
                DROP INDEX IF EXISTS public."UX_BankMatchingRules_BU_ActiveTenant";
                ALTER TABLE public."JournalEntries" DROP CONSTRAINT IF EXISTS "CK_JournalEntries_State";
                ALTER TABLE public."JournalEntries" ADD CONSTRAINT "CK_JournalEntries_State" CHECK (
                    "Status" IN ('Draft','Posted','Cancelled','Reversed') AND "SourceType" IN ('Manual','JournalReversal')
                    AND (("Status" = 'Draft' AND "EntryNumber" IS NULL AND "PostedBy" IS NULL AND "PostedOn" IS NULL
                          AND "CancelledBy" IS NULL AND "CancelledOn" IS NULL AND "ReversedBy" IS NULL AND "ReversedOn" IS NULL)
                      OR ("Status" = 'Posted' AND "EntryNumber" IS NOT NULL AND "PostedBy" IS NOT NULL AND "PostedOn" IS NOT NULL
                          AND "CancelledBy" IS NULL AND "CancelledOn" IS NULL AND "ReversedBy" IS NULL AND "ReversedOn" IS NULL)
                      OR ("Status" = 'Cancelled' AND "EntryNumber" IS NULL AND "PostedBy" IS NULL AND "PostedOn" IS NULL
                          AND "CancelledBy" IS NOT NULL AND "CancelledOn" IS NOT NULL AND length(trim("CancellationReason")) >= 20)
                      OR ("Status" = 'Reversed' AND "EntryNumber" IS NOT NULL AND "PostedBy" IS NOT NULL AND "PostedOn" IS NOT NULL
                          AND "ReversedBy" IS NOT NULL AND "ReversedOn" IS NOT NULL
                          AND length(trim("ReversalReason")) >= 20 AND length(trim("ReversalEvidenceReference")) >= 8)));
                ALTER TABLE public."ReconciliationMatches" DROP CONSTRAINT IF EXISTS "CK_ReconciliationMatches_ManualEvidence";
                ALTER TABLE public."ReconciliationMatches" ADD CONSTRAINT "CK_ReconciliationMatches_ManualEvidence" CHECK (
                    ("MatchType" = 'Manual' AND length(trim("MatchReason")) >= 20 AND length(trim("EvidenceReference")) >= 8
                        AND "RuleCode" = 'MANUAL_REVIEWED_V1' AND "RuleVersion" = 1 AND "Confidence" = 1)
                    OR ("MatchType" = 'DeterministicExact' AND "MatchReason" IS NULL AND "EvidenceReference" IS NULL
                        AND "RuleCode" = 'EXACT_AMOUNT_DIRECTION_V1' AND "RuleVersion" = 1 AND "Confidence" = 1));
                """);
            migrationBuilder.DropForeignKey(
                name: "FK_CustomerPayments_BankAccounts_BusinessUnitId_BankAccountId",
                table: "CustomerPayments");

            migrationBuilder.DropForeignKey(
                name: "FK_CustomerPayments_JournalEntries_BusinessUnitId_JournalEntry~",
                table: "CustomerPayments");

            migrationBuilder.DropForeignKey(
                name: "FK_CustomerPayments_JournalEntries_BusinessUnitId_ReversalJour~",
                table: "CustomerPayments");

            migrationBuilder.DropForeignKey(
                name: "FK_CustomerRefunds_BankAccounts_BusinessUnitId_BankAccountId",
                table: "CustomerRefunds");

            migrationBuilder.DropForeignKey(
                name: "FK_CustomerRefunds_JournalEntries_BusinessUnitId_JournalEntryId",
                table: "CustomerRefunds");

            migrationBuilder.DropForeignKey(
                name: "FK_LedgerBooks_LedgerAccounts_BusinessUnitId_ReceivablesContro~",
                table: "LedgerBooks");

            migrationBuilder.DropForeignKey(
                name: "FK_LedgerBooks_LedgerAccounts_BusinessUnitId_UnappliedCashAcco~",
                table: "LedgerBooks");

            migrationBuilder.DropForeignKey(
                name: "FK_ReconciliationMatches_BankMatchingRules_BusinessUnitId_Bank~",
                table: "ReconciliationMatches");

            migrationBuilder.DropTable(
                name: "BankAdjustmentDistributions");

            migrationBuilder.DropTable(
                name: "ReconciliationRunRules");

            migrationBuilder.DropTable(
                name: "BankAdjustments");

            migrationBuilder.DropTable(
                name: "BankMatchingRules");

            migrationBuilder.DropIndex(
                name: "IX_ReconciliationMatches_BusinessUnitId_BankMatchingRuleId",
                table: "ReconciliationMatches");

            migrationBuilder.DropIndex(
                name: "IX_LedgerBooks_BusinessUnitId_ReceivablesControlAccountId",
                table: "LedgerBooks");

            migrationBuilder.DropIndex(
                name: "IX_LedgerBooks_BusinessUnitId_UnappliedCashAccountId",
                table: "LedgerBooks");

            migrationBuilder.DropCheckConstraint(
                name: "CK_LedgerBooks_State",
                table: "LedgerBooks");

            migrationBuilder.DropIndex(
                name: "IX_CustomerRefunds_BusinessUnitId_BankAccountId",
                table: "CustomerRefunds");

            migrationBuilder.DropIndex(
                name: "UX_CustomerRefunds_BU_Journal",
                table: "CustomerRefunds");

            migrationBuilder.DropIndex(
                name: "IX_CustomerPayments_BusinessUnitId_BankAccountId",
                table: "CustomerPayments");

            migrationBuilder.DropIndex(
                name: "IX_CustomerPayments_BusinessUnitId_ReversalJournalEntryId",
                table: "CustomerPayments");

            migrationBuilder.DropIndex(
                name: "UX_CustomerPayments_BU_Journal",
                table: "CustomerPayments");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_BankStatementLines_BusinessUnitId_Id_BankAccountId",
                table: "BankStatementLines");

            migrationBuilder.DropColumn(
                name: "RuleSetHash",
                table: "ReconciliationRuns");

            migrationBuilder.DropColumn(
                name: "RuleSetSnapshotOn",
                table: "ReconciliationRuns");

            migrationBuilder.DropColumn(
                name: "BankMatchingRuleId",
                table: "ReconciliationMatches");

            migrationBuilder.DropColumn(
                name: "RuleDefinitionHash",
                table: "ReconciliationMatches");

            migrationBuilder.DropColumn(
                name: "ReceivablesControlAccountId",
                table: "LedgerBooks");

            migrationBuilder.DropColumn(
                name: "UnappliedCashAccountId",
                table: "LedgerBooks");

            migrationBuilder.DropColumn(
                name: "BankAccountId",
                table: "CustomerRefunds");

            migrationBuilder.DropColumn(
                name: "JournalEntryId",
                table: "CustomerRefunds");

            migrationBuilder.DropColumn(
                name: "BankAccountId",
                table: "CustomerPayments");

            migrationBuilder.DropColumn(
                name: "AccountingBridgeRequired",
                table: "CustomerPayments");

            migrationBuilder.DropColumn(
                name: "JournalEntryId",
                table: "CustomerPayments");

            migrationBuilder.DropColumn(
                name: "ReversalJournalEntryId",
                table: "CustomerPayments");

            migrationBuilder.DropColumn(
                name: "ReversedBy",
                table: "CustomerPayments");

            migrationBuilder.AddCheckConstraint(
                name: "CK_LedgerBooks_State",
                table: "LedgerBooks",
                sql: "\"FiscalYearStartMonth\" BETWEEN 1 AND 12 AND \"Version\" = 1");
        }
    }
}
