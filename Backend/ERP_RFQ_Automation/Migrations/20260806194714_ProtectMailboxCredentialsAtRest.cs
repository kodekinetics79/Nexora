using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ERP_RFQ_Automation.Migrations
{
    /// <summary>
    /// Widens <c>Email_Configurations.Password</c> so it can hold the AES-256-GCM envelope
    /// (<c>v1:&lt;base64 nonce&gt;:&lt;base64 ciphertext||tag&gt;</c>) that now protects the CUSTOMER
    /// MAILBOX credential at rest. The envelope is roughly 1.4x the plaintext byte length plus
    /// ~36 characters of framing, so the old varchar(255) could not hold a protected value.
    ///
    /// THE DATA MIGRATION IS DELIBERATELY NOT HERE. Converting the existing cleartext rows
    /// requires the encryption key, which lives in application configuration
    /// (<c>Security:SecretProtectionKey</c>) and is deliberately absent from the database —
    /// the threat model is an actor who can read the database. A <c>migrationBuilder.Sql</c>
    /// block runs inside the database and cannot reach that key, and putting the key anywhere
    /// SQL could reach would defeat the entire exercise. The backfill therefore runs at
    /// startup, in <c>MailboxCredentialProtectionBackfill</c>, where the key is present and
    /// has already passed validation. It is idempotent (skips values already carrying the
    /// <c>v1:</c> prefix) and per-row fault tolerant, so it is safe on every boot.
    ///
    /// Up is a widening and therefore non-destructive. Down narrows back to 255 and will fail
    /// on any row still holding an envelope — which is correct: silently truncating a
    /// credential is worse than refusing to roll back.
    /// </summary>
    public partial class ProtectMailboxCredentialsAtRest : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "Password",
                table: "Email_Configurations",
                type: "character varying(2048)",
                maxLength: 2048,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(255)",
                oldMaxLength: 255);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "Password",
                table: "Email_Configurations",
                type: "character varying(255)",
                maxLength: 255,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(2048)",
                oldMaxLength: 2048);
        }
    }
}
