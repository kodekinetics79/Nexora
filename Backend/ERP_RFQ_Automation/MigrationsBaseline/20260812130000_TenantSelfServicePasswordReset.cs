using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace ERP_RFQ_Automation.Migrations
{
    /// <summary>
    /// Self-service password recovery for tenant users: one table, four indexes, and the grants
    /// without which the whole flow is a 42501.
    ///
    /// <para><b>The gap this closes.</b> A tenant user who forgot their password had no route back
    /// into the product that did not go through somebody with database access overwriting
    /// <c>public."Users"."Password_Hash"</c> by hand. That is precisely the
    /// operator-holds-a-working-credential defect that 20260807002456 built the activation
    /// invitation to end for the FIRST credential — reappearing at every subsequent one, and worse,
    /// because by then the account is live and in use. The activation page has been telling users
    /// to "use forgot password on the sign-in page" since it shipped; this makes that true.</para>
    ///
    /// <para><b>Why platform schema and no RLS.</b> Identical to
    /// <c>platform."TenantAdminInvitations"</c>, because it is the identical situation: the row is
    /// written on a fully anonymous request (someone typed an address into a public form) and read
    /// on another one (someone opened a link), so there is no <c>nexora.business_unit_id</c> for a
    /// policy to key on and no tenant scope for a query filter to apply. Being outside the public
    /// schema keeps it outside the RLS expectation <c>PostgreSqlProductionDialectTests</c> asserts,
    /// the same exemption <c>LoginAttempts</c> takes for the same pre-authentication reason.</para>
    ///
    /// <para><b>No backfill, and nothing existing changes.</b> An upgrade adds an empty table. No
    /// account's credential, status or lockout is touched, which is the only safe shape for a
    /// migration that touches authentication.</para>
    ///
    /// <para><b>The one grant that differs from activation's, and why it is safe.</b>
    /// <c>nexora_identity_app</c> gets INSERT here. It deliberately does NOT have INSERT on
    /// <c>TenantAdminInvitations</c> — 20260807002456 withheld it because an anonymous caller who
    /// could mint an invitation could invite themselves into any tenant. Minting a RESET is not the
    /// same act and does not carry that power: a reset row names an account that already exists, it
    /// creates nothing, it grants nothing on its own, and the cleartext token that would make it
    /// usable is sent to that account's own mailbox and to nowhere else. The privilege an anonymous
    /// caller gains from this INSERT is exactly "cause an email to be sent to somebody else's
    /// address", which is what the endpoint's rate limiter and durable per-IP counter are for.</para>
    /// </summary>
    public partial class TenantSelfServicePasswordReset : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PasswordResetTokens",
                schema: "platform",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    UserId = table.Column<long>(type: "bigint", nullable: false),
                    TenantId = table.Column<long>(type: "bigint", nullable: true),
                    TokenHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    IssuedAtUtc = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    ExpiresAtUtc = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    RequestedFromIp = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    RedeemedAtUtc = table.Column<DateTime>(type: "timestamp without time zone", nullable: true),
                    RedeemedFromIp = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    RevokedAtUtc = table.Column<DateTime>(type: "timestamp without time zone", nullable: true),
                    RevokedBy = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    RevocationReason = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    LastSentAtUtc = table.Column<DateTime>(type: "timestamp without time zone", nullable: true),
                    SendCount = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PasswordResetTokens", x => x.Id);

                    // No foreign key to public."Users" or platform."Tenants", and the omission is
                    // deliberate rather than forgotten. TenantPurgeExecutor runs under
                    // session_replication_role = 'replica', which suspends foreign-key triggers
                    // along with the ~30 append-only guards — so ON DELETE CASCADE would not fire
                    // during the one operation that most needs it, and would only create the
                    // illusion of cleanup. The real mechanism is the explicit PasswordResetTokens
                    // entry in PlatformTenantDataMap, which makes the deletion reviewable and is
                    // held in place by TenantLifecyclePlatformTableClassificationTests.
                });

            migrationBuilder.CreateIndex(
                name: "IX_PasswordResetTokens_ExpiresAtUtc",
                schema: "platform",
                table: "PasswordResetTokens",
                column: "ExpiresAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_PasswordResetTokens_TenantId",
                schema: "platform",
                table: "PasswordResetTokens",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_PasswordResetTokens_UserId_Live",
                schema: "platform",
                table: "PasswordResetTokens",
                columns: new[] { "UserId", "RedeemedAtUtc", "RevokedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "UX_PasswordResetTokens_TokenHash",
                schema: "platform",
                table: "PasswordResetTokens",
                column: "TokenHash",
                unique: true);

            GrantPasswordResetPrivileges(migrationBuilder);
        }

        /// <summary>
        /// The privileges the reset flow needs, granted explicitly because nothing else will.
        ///
        /// <para><b>Why this is not optional.</b> Every blanket "ALL TABLES IN SCHEMA platform"
        /// grant in this history is point-in-time — it grants nothing on a table created afterwards
        /// — so without this block <c>platform."PasswordResetTokens"</c> is unreadable and
        /// unwritable by every runtime role. The flow would fail at the INSERT with 42501 while the
        /// whole SQLite suite stayed green, because SQLite has neither roles nor privileges. That is
        /// the exact defect class 20260807002456 documented for the activation table and
        /// <c>RedTeamControlPlanePostgreSqlTests</c> exists to catch.</para>
        /// </summary>
        private static void GrantPasswordResetPrivileges(MigrationBuilder migrationBuilder)
        {
            // Guarded exactly like the sibling platform migrations: a bare development database
            // created without the execution roles must still migrate cleanly.
            if (!IsNpgsql(migrationBuilder))
                return;

            migrationBuilder.Sql("""
                DO $password_reset_grants$
                BEGIN
                    IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'nexora_identity_app') THEN
                        -- The whole flow runs as nexora_identity_app:
                        -- TenantRlsCommandInterceptor.ResolveDatabaseRole routes
                        -- "/api/password-reset" to it, hoisted above the tenant check for the same
                        -- reason the two login paths are — the frontend attaches whatever bearer
                        -- token it holds to every request, and arriving with a business unit set
                        -- would downgrade the role out of these privileges.
                        --
                        -- INSERT: mint a token. UPDATE: spend it, and supersede its siblings.
                        -- SELECT: find it again by hash.
                        --
                        -- No DELETE. Nothing in the flow deletes a reset row — a spent token is
                        -- kept as the record that the password WAS changed, and from where, which
                        -- is the only forensic trace an anonymous credential change leaves. Pruning
                        -- expired rows is an operator or purge concern, and both have their own
                        -- roles below.
                        GRANT SELECT, INSERT, UPDATE
                            ON TABLE platform."PasswordResetTokens" TO nexora_identity_app;
                        GRANT USAGE, SELECT
                            ON SEQUENCE platform."PasswordResetTokens_Id_seq" TO nexora_identity_app;

                        -- The column-scoped UPDATE on Users that completion needs is ALREADY held
                        -- by this role: 20260807002456 enumerated "Password_Hash", "IsActive",
                        -- "DeactivatedAtUtc", "ModifiedBy" and "ModifiedOn" for activation, and the
                        -- reset path writes a strict subset of those three — Password_Hash,
                        -- ModifiedBy, ModifiedOn. Nothing is granted here, and nothing needs to be.
                        --
                        -- That the role CAN write "IsActive" and this code declines to is the point:
                        -- a reset may change what an account's credential is, never whether the
                        -- account is allowed to exist. RoleId, Buid and Email remain ungranted, so a
                        -- defect in this path cannot become a privilege-escalation or
                        -- tenant-crossing defect.
                    END IF;

                    -- The offboarding purge. PasswordResetTokens is classified as the CUSTOMER's
                    -- record in PlatformTenantDataMap — it holds the client addresses their reset
                    -- was asked for and completed from — so it is destroyed with the rest of their
                    -- data, and the role that does the destroying needs to be able to see and
                    -- delete it. Without this the purge fails on the row it was told to remove,
                    -- which is the worst possible moment to discover a missing grant.
                    IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'nexora_purge_app') THEN
                        GRANT SELECT, DELETE
                            ON TABLE platform."PasswordResetTokens" TO nexora_purge_app;
                    END IF;

                    -- Never the tenant role. A reset request carries no tenant scope, so a row it
                    -- could reach is a row RLS failed to hide — and nothing on the authenticated
                    -- tenant plane has any business reading who asked to reset whose password.
                    -- Stated as an explicit REVOKE rather than left ungranted, matching how
                    -- 20260807002456 treats the activation table, so a future blanket grant over
                    -- the schema does not silently re-open it.
                    IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'nexora_tenant_app') THEN
                        REVOKE ALL PRIVILEGES
                            ON TABLE platform."PasswordResetTokens" FROM nexora_tenant_app;
                    END IF;

                    -- nexora_pipeline_app is deliberately absent. It is the role for background
                    -- workers and the platform plane, and neither issues, reads nor spends a reset:
                    -- an operator cannot cause one, by design, because an operator who could would
                    -- be back in possession of a route to a customer's credential. If a pruning
                    -- worker is ever written it should arrive with its own grant and its own
                    -- migration saying so.
                END
                $password_reset_grants$;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // DropTable takes the grants, the indexes and the sequence with it, so there is nothing
            // to revoke separately. The Users column grants are NOT touched: they predate this
            // migration and activation still depends on them.
            migrationBuilder.DropTable(
                name: "PasswordResetTokens",
                schema: "platform");
        }

        private static bool IsNpgsql(MigrationBuilder migrationBuilder)
            => migrationBuilder.ActiveProvider?.Contains("Npgsql", StringComparison.OrdinalIgnoreCase) == true;
    }
}
