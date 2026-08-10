using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ERP_RFQ_Automation.Migrations
{
    /// <summary>
    /// `Email_Configurations.PollingInterval` now means MINUTES, which is what every human-facing
    /// surface already promised it meant. The column default was `300` — five minutes expressed in
    /// seconds — and under the corrected unit that same number reads as a **five-hour** poll. That
    /// is wiring-contract failure #10: a stored default that silently inverts its own meaning.
    ///
    /// <para><b>No row is touched, and that is the whole decision.</b> The obvious repair —
    /// `UPDATE ... SET "PollingInterval" = 5 WHERE "PollingInterval" = 300` — is wrong, because 300
    /// is a perfectly legal operator setting that means five hours both before and after the unit
    /// change. A tenant who deliberately chose a slow poll is indistinguishable from a row that
    /// merely inherited the default, so a blanket update would rewrite a real customer setting on a
    /// guess. There is no column recording which rows were defaulted, so the information needed to
    /// tell them apart does not exist.</para>
    ///
    /// <para>Leaving the rows alone is safe in the one direction that matters. Every stored value
    /// keeps its number and changes meaning from seconds to minutes, so every existing mailbox
    /// polls **sixty times more slowly**, never faster — the transition cannot create the hazard it
    /// removes, it can only make a mailbox lazy, which is visible on the mailbox health screen and
    /// correctable by an operator in one edit. The reverse migration would have been the dangerous
    /// one.</para>
    ///
    /// <para>The default itself is unreachable through the product — both the create and the update
    /// path always write `PollingInterval` explicitly — so this corrects a value that only a raw
    /// INSERT could ever inherit. It is still worth correcting: an unreachable default is exactly
    /// the kind of thing that becomes reachable the day someone writes an importer.</para>
    /// </summary>
    /// <inheritdoc />
    public partial class MailboxPollingIntervalMinutesDefault : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<int>(
                name: "PollingInterval",
                table: "Email_Configurations",
                type: "integer",
                nullable: false,
                defaultValue: 5,
                oldClrType: typeof(int),
                oldType: "integer",
                oldDefaultValue: 300);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<int>(
                name: "PollingInterval",
                table: "Email_Configurations",
                type: "integer",
                nullable: false,
                defaultValue: 300,
                oldClrType: typeof(int),
                oldType: "integer",
                oldDefaultValue: 5);
        }
    }
}
