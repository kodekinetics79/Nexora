using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace ERP_RFQ_Automation.Migrations
{
    /// <inheritdoc />
    public partial class EmailInquiryAssembly : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "EmailInquiryAssemblies",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    BusinessUnitId = table.Column<long>(type: "bigint", nullable: false),
                    EmailIngestId = table.Column<long>(type: "bigint", nullable: false),
                    EmailConfigurationId = table.Column<long>(type: "bigint", nullable: false),
                    MessageKey = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    RawEvidenceUri = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    RawEvidenceSha256 = table.Column<string>(type: "character(64)", fixedLength: true, maxLength: 64, nullable: true),
                    RawEvidenceVersionId = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    SenderAddress = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    RecipientsJson = table.Column<string>(type: "jsonb", nullable: true),
                    Subject = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    ReceivedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ExpectedComponentCount = table.Column<int>(type: "integer", nullable: false),
                    CompletedComponentCount = table.Column<int>(type: "integer", nullable: false),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    StatusReason = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    SkippedPartsJson = table.Column<string>(type: "jsonb", nullable: true),
                    Version = table.Column<int>(type: "integer", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EmailInquiryAssemblies", x => x.Id);
                    table.UniqueConstraint("AK_EmailInquiryAssemblies_BusinessUnitId_Id", x => new { x.BusinessUnitId, x.Id });
                    table.ForeignKey(
                        name: "FK_EmailInquiryAssemblies_EmailIngests_EmailIngestId",
                        column: x => x.EmailIngestId,
                        principalTable: "EmailIngests",
                        principalColumn: "ID",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "EmailInquiryComponents",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    BusinessUnitId = table.Column<long>(type: "bigint", nullable: false),
                    AssemblyId = table.Column<long>(type: "bigint", nullable: false),
                    ComponentKey = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    Kind = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    Ordinal = table.Column<int>(type: "integer", nullable: false),
                    FileName = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    MimeType = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    ByteSize = table.Column<long>(type: "bigint", nullable: true),
                    ContentHash = table.Column<string>(type: "character(64)", fixedLength: true, maxLength: 64, nullable: true),
                    EvidenceUri = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    ExtractionJobId = table.Column<long>(type: "bigint", nullable: true),
                    SourceDocumentOccurrenceId = table.Column<long>(type: "bigint", nullable: true),
                    Status = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    ReasonCode = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    ReasonDetail = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    NestingDepth = table.Column<int>(type: "integer", nullable: false),
                    Version = table.Column<int>(type: "integer", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EmailInquiryComponents", x => x.Id);
                    table.ForeignKey(
                        name: "FK_EmailInquiryComponents_EmailInquiryAssemblies_BusinessUnitI~",
                        columns: x => new { x.BusinessUnitId, x.AssemblyId },
                        principalTable: "EmailInquiryAssemblies",
                        principalColumns: new[] { "BusinessUnitId", "Id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_EmailInquiryAssemblies_BusinessUnitId_EmailConfigurationId_~",
                table: "EmailInquiryAssemblies",
                columns: new[] { "BusinessUnitId", "EmailConfigurationId", "MessageKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_EmailInquiryAssemblies_BusinessUnitId_Status_UpdatedAtUtc",
                table: "EmailInquiryAssemblies",
                columns: new[] { "BusinessUnitId", "Status", "UpdatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_EmailInquiryAssemblies_EmailIngestId",
                table: "EmailInquiryAssemblies",
                column: "EmailIngestId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_EmailInquiryComponents_BusinessUnitId_AssemblyId_ComponentK~",
                table: "EmailInquiryComponents",
                columns: new[] { "BusinessUnitId", "AssemblyId", "ComponentKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_EmailInquiryComponents_BusinessUnitId_AssemblyId_Ordinal",
                table: "EmailInquiryComponents",
                columns: new[] { "BusinessUnitId", "AssemblyId", "Ordinal" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_EmailInquiryComponents_BusinessUnitId_ExtractionJobId",
                table: "EmailInquiryComponents",
                columns: new[] { "BusinessUnitId", "ExtractionJobId" });

            migrationBuilder.CreateIndex(
                name: "IX_EmailInquiryComponents_BusinessUnitId_Status",
                table: "EmailInquiryComponents",
                columns: new[] { "BusinessUnitId", "Status" });

            // Row-level security for the two new tenant-owned tables. The EF query filter added
            // alongside them is a convenience; this is the boundary, and TenantIsolationTests
            // fails any tenant table that has a filter and no policy.
            //
            // Both tables spell the column "BusinessUnitId", not the legacy "BusinessUnitID"
            // used by Leads and Quotes — copying the wrong precedent here would produce a policy
            // that silently never matches.
            migrationBuilder.Sql("""
                ALTER TABLE public."EmailInquiryAssemblies" ENABLE ROW LEVEL SECURITY;
                ALTER TABLE public."EmailInquiryAssemblies" FORCE ROW LEVEL SECURITY;
                CREATE POLICY nexora_tenant_isolation ON public."EmailInquiryAssemblies" TO nexora_tenant_app
                    USING ("BusinessUnitId" = NULLIF(current_setting('nexora.business_unit_id', true), '')::bigint)
                    WITH CHECK ("BusinessUnitId" = NULLIF(current_setting('nexora.business_unit_id', true), '')::bigint);

                ALTER TABLE public."EmailInquiryComponents" ENABLE ROW LEVEL SECURITY;
                ALTER TABLE public."EmailInquiryComponents" FORCE ROW LEVEL SECURITY;
                CREATE POLICY nexora_tenant_isolation ON public."EmailInquiryComponents" TO nexora_tenant_app
                    USING ("BusinessUnitId" = NULLIF(current_setting('nexora.business_unit_id', true), '')::bigint)
                    WITH CHECK ("BusinessUnitId" = NULLIF(current_setting('nexora.business_unit_id', true), '')::bigint);
                """);

            // Table privileges are NOT implied by anything: CompleteTenantRlsCoverage revoked the
            // schema default privileges, so `public` is deny-by-default and every new tenant table
            // must be granted explicitly. PostgreSQL checks the grant BEFORE any row-level
            // predicate and raises 42501, so a policy without a grant is not a narrower boundary —
            // it is a table nobody can read. Three tables in Gate 4 shipped with exactly that gap.
            migrationBuilder.Sql("""
                GRANT SELECT, INSERT, UPDATE, DELETE ON TABLE
                    public."EmailInquiryAssemblies",
                    public."EmailInquiryComponents"
                TO nexora_tenant_app;
                GRANT USAGE, SELECT ON SEQUENCE
                    public."EmailInquiryAssemblies_Id_seq",
                    public."EmailInquiryComponents_Id_seq"
                TO nexora_tenant_app;
                """);

            // The mailbox poller and the extraction worker write these rows OUTSIDE a pushed
            // tenant scope (the poller is resolving which tenant a message belongs to; the worker
            // drains a shared queue), so they execute as nexora_pipeline_app. Guarded on the role
            // existing because local and CI databases do not always provision it.
            migrationBuilder.Sql("""
                DO $$
                BEGIN
                    IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'nexora_pipeline_app') THEN
                        GRANT SELECT, INSERT, UPDATE ON TABLE
                            public."EmailInquiryAssemblies",
                            public."EmailInquiryComponents"
                        TO nexora_pipeline_app;
                        GRANT USAGE, SELECT ON SEQUENCE
                            public."EmailInquiryAssemblies_Id_seq",
                            public."EmailInquiryComponents_Id_seq"
                        TO nexora_pipeline_app;
                    END IF;
                END
                $$;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Policies and grants disappear with the tables; dropping them explicitly first keeps
            // the Down readable and survives a partial apply where a table already went.
            migrationBuilder.Sql("""
                DROP POLICY IF EXISTS nexora_tenant_isolation ON public."EmailInquiryComponents";
                DROP POLICY IF EXISTS nexora_tenant_isolation ON public."EmailInquiryAssemblies";
                """);

            migrationBuilder.DropTable(
                name: "EmailInquiryComponents");

            migrationBuilder.DropTable(
                name: "EmailInquiryAssemblies");
        }
    }
}
