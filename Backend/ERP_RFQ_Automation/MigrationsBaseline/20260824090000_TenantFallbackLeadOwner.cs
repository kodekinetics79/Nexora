using ERP_RFQ_Automation.Models;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ERP_RFQ_Automation.Migrations;

/// <summary>
/// The tenant's ONE customer-set answer to "when Nexora can't work out who owns an inquiry, give it
/// to ___" (BusinessUnit.RoutingDefaults.cs), plus who set it and when.
///
/// <para>Hand-authored with its [DbContext]/[Migration] attributes rather than scaffolded: EF
/// produces a ~1,100-operation garbage diff against the excluded pre-squash Migrations\ folder, so
/// the maintained snapshot under MigrationsBaseline\ is edited by hand alongside this file.</para>
///
/// <para>All three columns are nullable with no default and no backfill. Null means "no fallback
/// owner", which is exactly the behaviour every existing tenant has today: an inquiry the routing
/// engine cannot place is held on the unassigned queue. No FORCE ROW LEVEL SECURITY dance is
/// needed because nothing is updated — BusinessUnits gains three empty columns.</para>
///
/// <para>No foreign key to Users on purpose. A Restrict edge would make whoever a tenant nominates
/// undeletable, and a stale id is already the safe case: DeterministicRoutingEngine only uses the
/// value after it clears the same availability test as any other candidate, so an id that no
/// longer resolves parks the inquiry on the queue exactly as an unset one does.</para>
/// </summary>
[DbContext(typeof(ErpRfqAutomationContext))]
[Migration("20260824090000_TenantFallbackLeadOwner")]
public sealed class TenantFallbackLeadOwner : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<long>(
            name: "DefaultLeadOwnerUserId", table: "BusinessUnits", type: "bigint", nullable: true);
        migrationBuilder.AddColumn<long>(
            name: "DefaultLeadOwnerSetByUserId", table: "BusinessUnits", type: "bigint", nullable: true);
        migrationBuilder.AddColumn<DateTime>(
            name: "DefaultLeadOwnerSetOn", table: "BusinessUnits",
            type: "timestamp without time zone", nullable: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(name: "DefaultLeadOwnerSetOn", table: "BusinessUnits");
        migrationBuilder.DropColumn(name: "DefaultLeadOwnerSetByUserId", table: "BusinessUnits");
        migrationBuilder.DropColumn(name: "DefaultLeadOwnerUserId", table: "BusinessUnits");
    }
}
