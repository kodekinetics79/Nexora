using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace ERP_RFQ_Automation.Migrations
{
    /// <inheritdoc />
    public partial class TenantCompanyIdentityCommercialTermsAndAdminActivation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AccountOwnerEmail",
                schema: "platform",
                table: "Tenants",
                type: "character varying(320)",
                maxLength: 320,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AddressLine1",
                schema: "platform",
                table: "Tenants",
                type: "character varying(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AddressLine2",
                schema: "platform",
                table: "Tenants",
                type: "character varying(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "BaseCurrencyCode",
                schema: "platform",
                table: "Tenants",
                type: "character(3)",
                fixedLength: true,
                maxLength: 3,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "BillingAddress",
                schema: "platform",
                table: "Tenants",
                type: "character varying(1024)",
                maxLength: 1024,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "BillingContactEmail",
                schema: "platform",
                table: "Tenants",
                type: "character varying(320)",
                maxLength: 320,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "BillingContactName",
                schema: "platform",
                table: "Tenants",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "BillingMode",
                schema: "platform",
                table: "Tenants",
                type: "character varying(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "Billable");

            migrationBuilder.AddColumn<string>(
                name: "BillingModeReason",
                schema: "platform",
                table: "Tenants",
                type: "character varying(1000)",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "BillingStartsOn",
                schema: "platform",
                table: "Tenants",
                type: "timestamp without time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "City",
                schema: "platform",
                table: "Tenants",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ContactEmail",
                schema: "platform",
                table: "Tenants",
                type: "character varying(320)",
                maxLength: 320,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ContractEndOn",
                schema: "platform",
                table: "Tenants",
                type: "timestamp without time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ContractStartOn",
                schema: "platform",
                table: "Tenants",
                type: "timestamp without time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CountryCode",
                schema: "platform",
                table: "Tenants",
                type: "character(2)",
                fixedLength: true,
                maxLength: 2,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DataRegion",
                schema: "platform",
                table: "Tenants",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Industry",
                schema: "platform",
                table: "Tenants",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LegalName",
                schema: "platform",
                table: "Tenants",
                type: "character varying(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Locale",
                schema: "platform",
                table: "Tenants",
                type: "character varying(16)",
                maxLength: 16,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LogoUrl",
                schema: "platform",
                table: "Tenants",
                type: "character varying(1024)",
                maxLength: 1024,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "PaymentTermsDays",
                schema: "platform",
                table: "Tenants",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Phone",
                schema: "platform",
                table: "Tenants",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PostalCode",
                schema: "platform",
                table: "Tenants",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PurchaseOrderReference",
                schema: "platform",
                table: "Tenants",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "RateCardId",
                schema: "platform",
                table: "Tenants",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RegistrationNumber",
                schema: "platform",
                table: "Tenants",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "StateProvince",
                schema: "platform",
                table: "Tenants",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TaxNumber",
                schema: "platform",
                table: "Tenants",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TimeZoneId",
                schema: "platform",
                table: "Tenants",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "TrialEndsOn",
                schema: "platform",
                table: "Tenants",
                type: "timestamp without time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Website",
                schema: "platform",
                table: "Tenants",
                type: "character varying(512)",
                maxLength: 512,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "TenantAdminInvitations",
                schema: "platform",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    TenantId = table.Column<long>(type: "bigint", nullable: false),
                    UserId = table.Column<long>(type: "bigint", nullable: false),
                    Email = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    TenantName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    TokenHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    IssuedAtUtc = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    ExpiresAtUtc = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    RedeemedAtUtc = table.Column<DateTime>(type: "timestamp without time zone", nullable: true),
                    RedeemedFromIp = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    RevokedAtUtc = table.Column<DateTime>(type: "timestamp without time zone", nullable: true),
                    RevokedBy = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    RevocationReason = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    IssuedBy = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    LastSentAtUtc = table.Column<DateTime>(type: "timestamp without time zone", nullable: true),
                    SendCount = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TenantAdminInvitations", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Tenants_RateCardId",
                schema: "platform",
                table: "Tenants",
                column: "RateCardId");

            migrationBuilder.CreateIndex(
                name: "IX_Tenants_Status_BillingMode",
                schema: "platform",
                table: "Tenants",
                columns: new[] { "Status", "BillingMode" });

            migrationBuilder.CreateIndex(
                name: "IX_TenantAdminInvitations_ExpiresAtUtc",
                schema: "platform",
                table: "TenantAdminInvitations",
                column: "ExpiresAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_TenantAdminInvitations_TenantId_IssuedAtUtc",
                schema: "platform",
                table: "TenantAdminInvitations",
                columns: new[] { "TenantId", "IssuedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_TenantAdminInvitations_UserId_Live",
                schema: "platform",
                table: "TenantAdminInvitations",
                columns: new[] { "UserId", "RedeemedAtUtc", "RevokedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "UX_TenantAdminInvitations_TokenHash",
                schema: "platform",
                table: "TenantAdminInvitations",
                column: "TokenHash",
                unique: true);

            GrantActivationPrivileges(migrationBuilder);
        }

        /// <summary>
        /// The privileges the activation flow needs, granted explicitly because nothing else will.
        ///
        /// <para><b>Why this is not optional.</b> The blanket "ALL TABLES IN SCHEMA platform"
        /// grant in 20260728134117 is point-in-time — it grants nothing on a table created
        /// afterwards — so without this block <c>platform."TenantAdminInvitations"</c> is
        /// unreadable and unwritable by every runtime role, and provisioning fails at the INSERT
        /// with 42501 while every SQLite test stays green, because SQLite has no roles.</para>
        ///
        /// <para><b>Why nexora_identity_app gains a column-scoped UPDATE on Users.</b> Redeeming
        /// an activation link is the one operation that must set a password for an account whose
        /// tenant nobody has established yet — it is the invitation that establishes it. The role
        /// held SELECT on Users for the login path and nothing more, so redemption would have
        /// failed on the write. The grant is enumerated column by column rather than granted on
        /// the table: activation may set a credential and switch the account live, and it
        /// specifically may NOT touch RoleId, Buid or Email — the three columns that decide what
        /// the account is and whose tenant it belongs to. A defect in the redemption path
        /// therefore cannot become a privilege-escalation or tenant-crossing defect.</para>
        /// </summary>
        private static void GrantActivationPrivileges(MigrationBuilder migrationBuilder)
        {
            // Guarded exactly like the sibling platform migrations: a bare development database
            // created without 20260728134117's roles must still migrate cleanly.
            if (migrationBuilder.ActiveProvider?.Contains("Npgsql", StringComparison.OrdinalIgnoreCase) != true)
                return;

            migrationBuilder.Sql("""
                DO $activation_grants$
                BEGIN
                    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'nexora_identity_app') THEN
                        RETURN;
                    END IF;

                    -- Platform plane: provisioning issues the invitation, and the operator
                    -- resends or revokes it. Runs as nexora_pipeline_app.
                    GRANT SELECT, INSERT, UPDATE, DELETE
                        ON TABLE platform."TenantAdminInvitations" TO nexora_pipeline_app;
                    GRANT USAGE, SELECT, UPDATE
                        ON SEQUENCE platform."TenantAdminInvitations_Id_seq" TO nexora_pipeline_app;

                    -- Activation plane: read the invitation, then spend it. No INSERT — an
                    -- anonymous caller must never be able to mint an invitation, only redeem one
                    -- that a platform operator already issued.
                    GRANT SELECT, UPDATE
                        ON TABLE platform."TenantAdminInvitations" TO nexora_identity_app;

                    -- Set the credential and bring the dormant account live. Enumerated, so this
                    -- role can never reassign an account's role, tenant or address.
                    --
                    -- The column is "Password_Hash", not "PasswordHash": the Users table is
                    -- scaffolded from a pre-existing schema and the CLR property is remapped
                    -- (see the HasColumnName in the model snapshot). A grant written against the
                    -- property name parses as valid SQL, fails with 42703 at migration time, and
                    -- would have been invisible on any provider without column-level grants.
                    GRANT UPDATE ("Password_Hash", "IsActive", "DeactivatedAtUtc",
                                  "ModifiedBy", "ModifiedOn")
                        ON TABLE public."Users" TO nexora_identity_app;

                    -- Two commercial-term columns for the ENFORCEMENT path, and only those two.
                    --
                    -- TenantAccessService is what TenantStatusGuardMiddleware and the login path
                    -- consult to decide whether a tenant is suspended and what its plan permits.
                    -- It runs under these two roles, whose SELECT on platform."Tenants" is
                    -- column-scoped by 20260805105320 — so a query that materialises the whole
                    -- entity is answered with 42501, and because that service fails OPEN by
                    -- contract (a platform-plane outage must not lock every customer out of the
                    -- product), the refusal is swallowed and enforcement silently stops existing.
                    -- The service now projects exactly the granted columns; these two extend that
                    -- projection to cover billing mode and tenant age, which is what the capacity
                    -- floor for commercially unconfigured tenants reads.
                    --
                    -- Same class as the columns already granted here (Status, PlanId): an enum
                    -- label and a timestamp. Nothing describing the customer, their commercial
                    -- terms, or any operator's reasoning is opened up.
                    GRANT SELECT ("BillingMode", "CreatedOn")
                        ON TABLE platform."Tenants" TO nexora_tenant_app, nexora_identity_app;

                    -- BillingModeReason is deliberately NOT granted. It carries internal
                    -- commercial commentary about why a customer is not being charged, and
                    -- 20260805105320 hid exactly that class of text from the tenant plane. The
                    -- cost of withholding it is bounded and visible: a legacy tenant whose
                    -- exemption was never written down keeps unmetered capacity until somebody
                    -- fixes the record, and it is listed on the revenue-risk board and in every
                    -- billing-run sweep until they do. A visible gap with a remediation path is a
                    -- better trade than re-opening internal reasoning to the tenant plane.

                    -- NOTHING further is granted on platform."Tenants" here. The activation
                    -- page greets the recipient by company name, and the obvious way to get it
                    -- would be to give this role back the "Name" column that 20260805105320
                    -- revoked when it narrowed the tenant plane's read surface to the columns
                    -- suspension and entitlement checks actually need. That narrowing is asserted
                    -- column by column by PlatformControlPlaneHardeningPostgreSqlTests, and a
                    -- greeting is not worth eroding it: the name is denormalised onto the
                    -- invitation row at issue time instead (TenantAdminInvitation.TenantName),
                    -- which is also the name the recipient's email actually said.

                    -- The activation endpoint reuses the login throttle to rate-limit token
                    -- guessing; nexora_identity_app already holds LoginAttempts privileges from
                    -- 20260804181701, and nexora_tenant_app deliberately still holds none.

                    -- Never the tenant role: an activation request carries no tenant scope, so a
                    -- row it could reach is a row RLS failed to hide.
                    REVOKE ALL PRIVILEGES
                        ON TABLE platform."TenantAdminInvitations" FROM nexora_tenant_app;
                END
                $activation_grants$;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "TenantAdminInvitations",
                schema: "platform");

            migrationBuilder.DropIndex(
                name: "IX_Tenants_RateCardId",
                schema: "platform",
                table: "Tenants");

            migrationBuilder.DropIndex(
                name: "IX_Tenants_Status_BillingMode",
                schema: "platform",
                table: "Tenants");

            migrationBuilder.DropColumn(
                name: "AccountOwnerEmail",
                schema: "platform",
                table: "Tenants");

            migrationBuilder.DropColumn(
                name: "AddressLine1",
                schema: "platform",
                table: "Tenants");

            migrationBuilder.DropColumn(
                name: "AddressLine2",
                schema: "platform",
                table: "Tenants");

            migrationBuilder.DropColumn(
                name: "BaseCurrencyCode",
                schema: "platform",
                table: "Tenants");

            migrationBuilder.DropColumn(
                name: "BillingAddress",
                schema: "platform",
                table: "Tenants");

            migrationBuilder.DropColumn(
                name: "BillingContactEmail",
                schema: "platform",
                table: "Tenants");

            migrationBuilder.DropColumn(
                name: "BillingContactName",
                schema: "platform",
                table: "Tenants");

            migrationBuilder.DropColumn(
                name: "BillingMode",
                schema: "platform",
                table: "Tenants");

            migrationBuilder.DropColumn(
                name: "BillingModeReason",
                schema: "platform",
                table: "Tenants");

            migrationBuilder.DropColumn(
                name: "BillingStartsOn",
                schema: "platform",
                table: "Tenants");

            migrationBuilder.DropColumn(
                name: "City",
                schema: "platform",
                table: "Tenants");

            migrationBuilder.DropColumn(
                name: "ContactEmail",
                schema: "platform",
                table: "Tenants");

            migrationBuilder.DropColumn(
                name: "ContractEndOn",
                schema: "platform",
                table: "Tenants");

            migrationBuilder.DropColumn(
                name: "ContractStartOn",
                schema: "platform",
                table: "Tenants");

            migrationBuilder.DropColumn(
                name: "CountryCode",
                schema: "platform",
                table: "Tenants");

            migrationBuilder.DropColumn(
                name: "DataRegion",
                schema: "platform",
                table: "Tenants");

            migrationBuilder.DropColumn(
                name: "Industry",
                schema: "platform",
                table: "Tenants");

            migrationBuilder.DropColumn(
                name: "LegalName",
                schema: "platform",
                table: "Tenants");

            migrationBuilder.DropColumn(
                name: "Locale",
                schema: "platform",
                table: "Tenants");

            migrationBuilder.DropColumn(
                name: "LogoUrl",
                schema: "platform",
                table: "Tenants");

            migrationBuilder.DropColumn(
                name: "PaymentTermsDays",
                schema: "platform",
                table: "Tenants");

            migrationBuilder.DropColumn(
                name: "Phone",
                schema: "platform",
                table: "Tenants");

            migrationBuilder.DropColumn(
                name: "PostalCode",
                schema: "platform",
                table: "Tenants");

            migrationBuilder.DropColumn(
                name: "PurchaseOrderReference",
                schema: "platform",
                table: "Tenants");

            migrationBuilder.DropColumn(
                name: "RateCardId",
                schema: "platform",
                table: "Tenants");

            migrationBuilder.DropColumn(
                name: "RegistrationNumber",
                schema: "platform",
                table: "Tenants");

            migrationBuilder.DropColumn(
                name: "StateProvince",
                schema: "platform",
                table: "Tenants");

            migrationBuilder.DropColumn(
                name: "TaxNumber",
                schema: "platform",
                table: "Tenants");

            migrationBuilder.DropColumn(
                name: "TimeZoneId",
                schema: "platform",
                table: "Tenants");

            migrationBuilder.DropColumn(
                name: "TrialEndsOn",
                schema: "platform",
                table: "Tenants");

            migrationBuilder.DropColumn(
                name: "Website",
                schema: "platform",
                table: "Tenants");
        }
    }
}
