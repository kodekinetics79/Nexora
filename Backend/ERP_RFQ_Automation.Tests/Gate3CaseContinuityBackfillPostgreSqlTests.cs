using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Procurement;
using ERP_RFQ_Automation.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// Certifies the case-continuity backfill against the production dialect. Populating the two
/// documents at creation only fixes the future; every supplier purchase order and shipment already
/// in the database was written before the columns existed, and would otherwise sit permanently
/// blank while the chain that could answer for them was still intact.
///
/// The rehearsal seeds a fully migrated database, migrates the case-continuity migration back down
/// so the columns disappear, then migrates up again — so the assertion is on the migration's own
/// SQL running against real rows, not on a hand-run script that resembles it.
/// </summary>
[Collection(PostgreSqlIntegrationCollection.Name)]
public sealed class Gate3CaseContinuityBackfillPostgreSqlTests(PostgreSqlTestDatabase database)
{
    private const string PreviousMigration = "20260809172925_Gate3SpineAndMatchingPolicy";
    private const string CurrentMigration = "20260809175352_Gate3CaseContinuityAndLeadOutcome";

    private const long Tenant = 96_401;
    private const long LeadId = 96_402;
    private const long RfqId = 96_403;
    private const long CustomerId = 96_404;
    private const long CurrencyId = 96_405;
    private const long SupplierId = 96_406;
    private const long OrderStatusId = 96_407;
    private const long ShipmentStatusId = 96_408;
    private const long OrderId = 96_409;
    private const long ShipmentId = 96_410;
    private const long CustomerDemandPoId = 96_411;
    private const long StockPoId = 96_412;
    private const long OrphanOrderId = 96_413;
    private const long OrphanShipmentId = 96_414;

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task Upgrade_backfills_shipments_from_their_order_and_supplier_orders_from_their_RFQ()
    {
        var databaseName = $"nexora_case_continuity_{Guid.NewGuid():N}";
        var target = new NpgsqlConnectionStringBuilder(database.ConnectionString) { Database = databaseName };
        await ExecuteAdminAsync($"CREATE DATABASE \"{databaseName}\"");

        try
        {
            long caseId;
            string serial;
            await using (var context = database.ContextForConnectionString(target.ConnectionString, null))
            {
                await context.Database.MigrateAsync();
                (caseId, serial) = await SeedAsync(context);
            }

            await using (var context = database.ContextForConnectionString(target.ConnectionString, null))
            {
                var migrator = context.GetService<IMigrator>();
                await migrator.MigrateAsync(PreviousMigration);
                await migrator.MigrateAsync(CurrentMigration);
            }

            await using var connection = new NpgsqlConnection(target.ConnectionString);
            await connection.OpenAsync();

            Assert.Equal(caseId, await ScalarAsync<long?>(connection,
                $"SELECT \"CommercialCaseId\" FROM public.\"Shipments\" WHERE \"ID\" = {ShipmentId}"));
            Assert.Equal(serial, await ScalarAsync<string>(connection,
                $"SELECT \"NexoraSerial\" FROM public.\"Shipments\" WHERE \"ID\" = {ShipmentId}"));

            Assert.Equal(caseId, await ScalarAsync<long?>(connection,
                $"SELECT \"CommercialCaseId\" FROM public.supplier_purchase_orders WHERE \"Id\" = {CustomerDemandPoId}"));
            Assert.Equal(serial, await ScalarAsync<string>(connection,
                $"SELECT \"NexoraSerial\" FROM public.supplier_purchase_orders WHERE \"Id\" = {CustomerDemandPoId}"));

            // A STOCK order shares the same RFQ, and is still left null: replenishment has no
            // case, so a reference here would be an invention rather than a recovery.
            Assert.True(await ScalarAsync<bool?>(connection,
                $"""
                 SELECT "CommercialCaseId" IS NULL AND "NexoraSerial" IS NULL
                 FROM public.supplier_purchase_orders WHERE "Id" = {StockPoId}
                 """));

            // A shipment whose order never came from a lead cannot resolve, and stays an honest gap.
            Assert.True(await ScalarAsync<bool?>(connection,
                $"""
                 SELECT "CommercialCaseId" IS NULL AND "NexoraSerial" IS NULL
                 FROM public."Shipments" WHERE "ID" = {OrphanShipmentId}
                 """));
        }
        finally
        {
            NpgsqlConnection.ClearAllPools();
            await ExecuteAdminAsync($"DROP DATABASE IF EXISTS \"{databaseName}\" WITH (FORCE)");
        }
    }

    private static async Task<(long CaseId, string Serial)> SeedAsync(ErpRfqAutomationContext context)
    {
        var now = new DateTime(2026, 8, 9, 9, 0, 0, DateTimeKind.Unspecified);
        Seed.EnsureBusinessUnit(context, Tenant);
        Seed.Customer(context, CustomerId, Tenant, "Case continuity customer");
        var lead = Seed.Lead(context, LeadId, Tenant, buyersName: "Continuity buyer");
        context.SetupMasters.AddRange(
            Status(OrderStatusId, "OrderStatus", "OPEN", now),
            Status(ShipmentStatusId, "ShipmentStatus", "READY", now));
        context.Currencies.Add(new Currency
        {
            Id = CurrencyId, BusinessUnitId = Tenant, Code = "CNT", CurrencyName = "Continuity currency",
            ExchangeRate = 1m, IsBaseCurrency = true, IsActive = true, CreatedBy = "qa", CreatedOn = now
        });
        AgentSeed.Supplier(context, SupplierId, Tenant, "Continuity supplier", "supplier@continuity.test");
        await context.SaveChangesAsync();

        // PostgreSQL allocates the commercial case by trigger, so the lead has to be re-read
        // before the rest of the chain can inherit from it.
        await context.Entry(lead).ReloadAsync();
        var caseId = lead.CommercialCaseId;
        var serial = lead.CommercialCaseReference;

        var rfq = AgentSeed.Rfq(context, RfqId, Tenant, "RFQ-CONTINUITY");
        rfq.LeadId = LeadId;
        rfq.InheritCommercialIdentity(lead);
        context.Set<Order>().AddRange(
            Seed.StampCommercialCase(new Order
            {
                Id = OrderId, OrderNo = "SO-CONTINUITY", CustomerId = CustomerId, BusinessUnitId = Tenant,
                StatusId = OrderStatusId, SourceType = OrderSourceTypes.Manual, TotalAmount = 40m,
                PaidAmount = 0m, OrderDate = now, CreatedBy = "qa", CreatedOn = now, IsActive = true
            }, caseId, serial),
            new Order
            {
                Id = OrphanOrderId, OrderNo = "SO-CONTINUITY-ORPHAN", CustomerId = CustomerId,
                BusinessUnitId = Tenant, StatusId = OrderStatusId, SourceType = OrderSourceTypes.Manual,
                TotalAmount = 10m, PaidAmount = 0m, OrderDate = now, CreatedBy = "qa", CreatedOn = now,
                IsActive = true
            });
        await context.SaveChangesAsync();

        // Written the way the schema held them before this migration: the columns exist, and
        // nothing has ever put a value in them.
        context.Shipments.AddRange(
            new Shipment
            {
                Id = ShipmentId, ShipmentNo = "SH-CONTINUITY", OrderId = OrderId, BusinessUnitId = Tenant,
                StatusId = ShipmentStatusId, ShipmentDate = now, CreatedBy = "qa", CreatedOn = now,
                IsActive = true
            },
            new Shipment
            {
                Id = OrphanShipmentId, ShipmentNo = "SH-CONTINUITY-ORPHAN", OrderId = OrphanOrderId,
                BusinessUnitId = Tenant, StatusId = ShipmentStatusId, ShipmentDate = now,
                CreatedBy = "qa", CreatedOn = now, IsActive = true
            });
        context.SupplierPurchaseOrders.AddRange(
            PurchaseOrder(CustomerDemandPoId, SupplierPurchaseOrderDemandSources.CustomerDemand, now),
            PurchaseOrder(StockPoId, SupplierPurchaseOrderDemandSources.Stock, now));
        await context.SaveChangesAsync();

        return (caseId, serial);
    }

    private static SupplierPurchaseOrder PurchaseOrder(long id, string demandSource, DateTime now) => new()
    {
        Id = id, BusinessUnitId = Tenant, RfqId = RfqId, SupplierId = SupplierId, CurrencyId = CurrencyId,
        PurchaseOrderNumber = $"PO-CONTINUITY-{id}", Status = SupplierPurchaseOrderStatuses.Draft,
        DemandSource = demandSource, TotalValue = 24m, IdempotencyKey = $"continuity-{id}",
        RequestHash = new string('c', 64), CreatedOn = now, CreatedBy = "qa"
    };

    private static SetupMaster Status(long setupId, string type, string value, DateTime now) => new()
    {
        SetupId = setupId, SetupType = type, SetupCode = value, SetupValue = value,
        BusinessUnitId = Tenant, IsActive = true, CreatedBy = "qa", CreatedOn = now
    };

    /// <summary>
    /// Reads one value, reporting an unresolved reference as a null rather than a cast failure —
    /// "the backfill left this blank" is the diagnosis, and it should read like one.
    /// </summary>
    private static async Task<T?> ScalarAsync<T>(NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        var value = await command.ExecuteScalarAsync();
        return value is null or DBNull ? default : (T)value;
    }

    private async Task ExecuteAdminAsync(string sql)
    {
        var admin = new NpgsqlConnectionStringBuilder(database.ConnectionString) { Database = "postgres" };
        await using var connection = new NpgsqlConnection(admin.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }
}
