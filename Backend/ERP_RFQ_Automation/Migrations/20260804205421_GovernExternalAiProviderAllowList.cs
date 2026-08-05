using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace ERP_RFQ_Automation.Migrations
{
    /// <summary>
    /// Per-tenant allow-list of external AI inference endpoints.
    ///
    /// AI extraction of unstructured documents was governed by one global bit —
    /// <c>if (ProviderClass == External) refuse</c> — with the provider class derived
    /// solely from whether the configured base URL was loopback. Production runs against
    /// a paid non-loopback endpoint, so that rule silently disabled every AI extraction
    /// in the product; the only working path was the deterministic spreadsheet parser,
    /// which contains no AI. A control that can only say "never" is not a governance
    /// position, and deleting it would replace it with "whatever host an environment
    /// variable names", which is worse.
    ///
    /// This table is the third answer: a tenant deliberately authorizes ONE normalised
    /// origin (scheme + host + non-default port), for one model or an explicit "any
    /// model", for named AI purposes, with whole-unstructured-document egress as its own
    /// separate switch, with a mandatory written justification, an optional expiry, an
    /// attributed authorizing user and a revocation trail. Anything not matched here is
    /// refused with exactly the previous message and exactly zero bytes of egress.
    ///
    /// Isolation: EF global query filter (Models/ErpRfqAutomationContext.AiProviderTrust.cs)
    /// plus the <c>nexora_tenant_isolation</c> RLS policy below, so one tenant can neither
    /// read nor rely on another tenant's authorizations. Plain ENABLE (not FORCE) matches
    /// the FolderIngestionRetryStates precedent: the table is read by the extraction
    /// worker, and FORCE would also subject the owner role that background paths use.
    /// The tenant role deliberately gets no DELETE — an authorization is revoked, never
    /// erased, so the audit trail survives.
    /// </summary>
    public partial class GovernExternalAiProviderAllowList : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AiProviderAuthorizations",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    BusinessUnitId = table.Column<long>(type: "bigint", nullable: false),
                    Provider = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Endpoint = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    Model = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    AllowedPurposes = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    UnstructuredDocumentsAllowed = table.Column<bool>(type: "boolean", nullable: false),
                    Justification = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    AuthorizedByUserId = table.Column<long>(type: "bigint", nullable: false),
                    AuthorizedBy = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    AuthorizedOn = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    ExpiresOn = table.Column<DateTime>(type: "timestamp without time zone", nullable: true),
                    RevokedOn = table.Column<DateTime>(type: "timestamp without time zone", nullable: true),
                    RevokedByUserId = table.Column<long>(type: "bigint", nullable: true),
                    RevokedBy = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    RevocationReason = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    Version = table.Column<long>(type: "bigint", nullable: false, defaultValue: 1L),
                    UpdatedOn = table.Column<DateTime>(type: "timestamp without time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AiProviderAuthorizations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AiProviderAuthorizations_BusinessUnits_BusinessUnitId",
                        column: x => x.BusinessUnitId,
                        principalTable: "BusinessUnits",
                        principalColumn: "ID",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AiProviderAuthorizations_BU_Endpoint",
                table: "AiProviderAuthorizations",
                columns: new[] { "BusinessUnitId", "Endpoint" });

            migrationBuilder.CreateIndex(
                name: "UX_AiProviderAuthorizations_BU_Provider_Endpoint_Model",
                table: "AiProviderAuthorizations",
                columns: new[] { "BusinessUnitId", "Provider", "Endpoint", "Model" },
                unique: true);

            // The tenant-isolation sweep migrations enable RLS by scanning for a
            // BusinessUnitID-ish column at the time they ran, so a table added later must
            // opt in explicitly or the tenant role would read it across tenants.
            migrationBuilder.Sql("""
                ALTER TABLE public."AiProviderAuthorizations" ENABLE ROW LEVEL SECURITY;
                DROP POLICY IF EXISTS nexora_tenant_isolation ON public."AiProviderAuthorizations";
                CREATE POLICY nexora_tenant_isolation ON public."AiProviderAuthorizations"
                    TO nexora_tenant_app
                    USING ("BusinessUnitId" = NULLIF(current_setting('nexora.business_unit_id', true), '')::bigint)
                    WITH CHECK ("BusinessUnitId" = NULLIF(current_setting('nexora.business_unit_id', true), '')::bigint);

                -- No DELETE for the tenant role: an authorization is revoked (a tracked
                -- state change with an actor, timestamp and reason), never erased.
                GRANT SELECT, INSERT, UPDATE ON TABLE public."AiProviderAuthorizations"
                    TO nexora_tenant_app;
                GRANT SELECT, INSERT, UPDATE, DELETE ON TABLE public."AiProviderAuthorizations"
                    TO nexora_pipeline_app;
                -- USAGE only for the tenant role: nextval() needs USAGE, and the
                -- PostgreSqlProductionDialectTests sequence guard forbids nexora_tenant_app
                -- holding SELECT or UPDATE on ANY sequence (reading currval/last_value across
                -- tenants leaks row-creation volume, and UPDATE would let a tenant reset the
                -- counter). This matches every other tenant sequence grant in Migrations/.
                GRANT USAGE ON SEQUENCE public."AiProviderAuthorizations_Id_seq"
                    TO nexora_tenant_app;
                GRANT USAGE, SELECT ON SEQUENCE public."AiProviderAuthorizations_Id_seq"
                    TO nexora_pipeline_app;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AiProviderAuthorizations");
        }
    }
}
