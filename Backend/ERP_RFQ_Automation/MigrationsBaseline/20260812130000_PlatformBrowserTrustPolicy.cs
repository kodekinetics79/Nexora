using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ERP_RFQ_Automation.Migrations
{
    /// <summary>
    /// Moves "remember this browser" out of <c>appsettings</c> and onto the singleton MFA policy row,
    /// and widens the permitted window from 12 hours to 30 days.
    ///
    /// <para><b>Why it is a column and not a setting.</b> Same argument that put <c>Mode</c> here: a
    /// security parameter in a deployment file is changed by whoever can edit that file, leaves no
    /// record of who or why, and cannot be changed at all by the Owner it belongs to. As a column it
    /// carries the row's Owner-only, password-re-authenticated, reasoned, versioned, audited change
    /// path for free.</para>
    ///
    /// <para><b>The check constraint is the point of the widening.</b> 8–720 hours, in the database,
    /// so the UPDATE that never came through the service — a hand-edited row, a restore, a future
    /// admin script — cannot install "remember this browser forever". A month-long second factor with
    /// no floor and no ceiling is the defect the old 12-hour cap was standing in for; the cap moved,
    /// the bound did not go away.</para>
    ///
    /// <para><b>No backfill, and one upgrade consequence stated plainly.</b> The columns take store
    /// defaults (<c>true</c>, <c>12</c>) — the shipped behaviour — so a deployment with no policy row
    /// is unaffected, and one WITH a row inherits 12 hours regardless of what
    /// <c>Platform:Mfa:BrowserTrustHours</c> said. A migration cannot read appsettings, and inventing
    /// a value from the process running the migration would make the stored policy depend on which
    /// host happened to deploy. A deployment that had configured a non-default window and already had
    /// a policy row therefore sets it once, on the screen, where it is audited.</para>
    ///
    /// <para>No GRANT block: 20260810233008 granted SELECT/INSERT/UPDATE on the TABLE, and PostgreSQL
    /// table privileges cover columns added later. Adding one here would be noise, and a
    /// column-level grant would be worse — it would silently narrow the table grant.</para>
    /// </summary>
    public partial class PlatformBrowserTrustPolicy : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "BrowserTrustEnabled",
                schema: "platform",
                table: "PlatformMfaPolicies",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<int>(
                name: "BrowserTrustHours",
                schema: "platform",
                table: "PlatformMfaPolicies",
                type: "integer",
                nullable: false,
                defaultValue: 12);

            migrationBuilder.AddCheckConstraint(
                name: "CK_PlatformMfaPolicies_BrowserTrustHours",
                schema: "platform",
                table: "PlatformMfaPolicies",
                sql: "\"BrowserTrustHours\" BETWEEN 8 AND 720");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_PlatformMfaPolicies_BrowserTrustHours",
                schema: "platform",
                table: "PlatformMfaPolicies");

            migrationBuilder.DropColumn(
                name: "BrowserTrustEnabled",
                schema: "platform",
                table: "PlatformMfaPolicies");

            migrationBuilder.DropColumn(
                name: "BrowserTrustHours",
                schema: "platform",
                table: "PlatformMfaPolicies");
        }
    }
}
