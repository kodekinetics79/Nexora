using ERP_RFQ_Automation.Models;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ERP_RFQ_Automation.Migrations;

/// <summary>
/// Completes the customer-order lifecycle for every existing tenant. Outbound shipment already
/// resolves OrderStatus/SHIPPED and commercial finance already requires a confirmed, shipped,
/// delivered or completed order, but the provisioning catalogue previously created none of those
/// states. A full despatch could therefore commit while its order remained Draft and the invoice
/// gate then refused the delivered goods.
/// </summary>
[DbContext(typeof(ErpRfqAutomationContext))]
[Migration("20260829213000_BackfillOrderFulfilmentStatuses")]
public partial class BackfillOrderFulfilmentStatuses : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        if (migrationBuilder.ActiveProvider?.Contains("Npgsql", StringComparison.OrdinalIgnoreCase) != true)
            return;

        migrationBuilder.Sql("""
            INSERT INTO public."Setup_Master"
                ("SetupType", "SetupCode", "SetupValue", "Description", "BusinessUnitID",
                 "RoleRank", "IsActive", "CreatedBy", "CreatedOn")
            SELECT 'OrderStatus', status.code, status.label,
                   'Server-governed customer order fulfilment state.', business_unit."ID",
                   0, true, 'migration:order-fulfilment-statuses:v1', now()
            FROM public."BusinessUnits" business_unit
            CROSS JOIN (VALUES
                ('CONFIRMED', 'Confirmed'),
                ('SHIPPED', 'Shipped'),
                ('DELIVERED', 'Delivered'),
                ('COMPLETED', 'Completed')
            ) AS status(code, label)
            WHERE NOT EXISTS (
                SELECT 1
                FROM public."Setup_Master" existing
                WHERE existing."BusinessUnitID" = business_unit."ID"
                  AND lower(replace(existing."SetupType", ' ', '')) = 'orderstatus'
                  AND COALESCE(existing."IsActive", true)
                  AND upper(trim(COALESCE(NULLIF(existing."SetupCode", ''), existing."SetupValue", ''))) = status.code
            );
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        if (migrationBuilder.ActiveProvider?.Contains("Npgsql", StringComparison.OrdinalIgnoreCase) != true)
            return;

        // Intentionally irreversible. These rows become foreign-key targets of live Orders as soon
        // as fulfilment runs. Deleting them on rollback would either fail mid-deployment or erase
        // tenant lifecycle reference data. A code rollback can safely leave additive statuses in
        // place; a later governed data migration may retire only rows proven unreferenced.
    }
}
