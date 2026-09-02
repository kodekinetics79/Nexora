using ERP_RFQ_Automation.Models;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.Platform.Services;

/// <summary>What one reconciliation pass did, for the boot log and for tests.</summary>
public sealed record TenantReferenceListReconciliation(
    int BusinessUnitsSwept, int BusinessUnitsCompleted, int RowsCreated, int Failures);

/// <summary>
/// Gives every EXISTING business unit the reference lists a new one is provisioned with.
///
/// <para><b>Why a reconciler and not only a seeder.</b> <see cref="TenantBaselineSeeder"/> runs
/// once, at provisioning, and <c>ProvisioningStepReconciler</c> deliberately carries no probe for
/// the baseline step. Every list added to <see cref="TenantBaselineCatalog.ReferenceLists"/>
/// after a tenant was provisioned is therefore absent from that tenant forever unless something
/// re-runs the check — which is exactly the state the live database was in: business units 7 and 8
/// had no ShipmentStatus, PaymentMethod, LeadRejectedReason or RFQType rows, and 8 had no
/// QuoteOutcomeReason either, because the catalogue grew after they were created.</para>
///
/// <para><b>What it will never do.</b> It calls
/// <see cref="ITenantBaselineSeeder.ReconcileReferenceListsAsync"/>, which writes a list only when
/// the tenant has no row of that type at all and never edits, reactivates or deletes anything. A
/// second run is a no-op, and a tenant that has shaped a list keeps its shape.</para>
///
/// <para><b>Concurrency.</b> Two instances booting together would both find the same list absent
/// and both insert it; Setup_Master carries no unique index that would stop the second. On
/// PostgreSQL the pass therefore runs under a transaction-scoped advisory lock, so the second
/// instance waits, re-reads, and finds nothing to do. On any other provider (the SQLite test lane)
/// the lock is skipped; nothing there runs two hosts against one file.</para>
/// </summary>
public static class TenantReferenceListReconciler
{
    /// <summary>Stamped into CreatedBy so a support engineer can tell a reconciled row from a
    /// provisioned or customer-added one.</summary>
    public const string Actor = "startup:tenant-reference-lists:v1";

    /// <summary>A fixed key for <c>pg_advisory_xact_lock</c>; chosen once, shared by every node.</summary>
    private const long AdvisoryLockKey = 0x4E58_5245_464C_5354; // "NXREFLST"

    public static async Task<TenantReferenceListReconciliation> RunAsync(
        ErpRfqAutomationContext db, ITenantBaselineSeeder seeder, ILogger logger, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(seeder);
        ArgumentNullException.ThrowIfNull(logger);

        var strategy = db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            if (db.Database.IsNpgsql())
                await db.Database.ExecuteSqlRawAsync(
                    $"SELECT pg_advisory_xact_lock({AdvisoryLockKey});", ct);

            var businessUnitIds = await db.BusinessUnits.IgnoreQueryFilters().AsNoTracking()
                .OrderBy(unit => unit.Id)
                .Select(unit => unit.Id)
                .ToListAsync(ct);

            int completed = 0, rows = 0, failures = 0;
            foreach (var businessUnitId in businessUnitIds)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    var created = await seeder.ReconcileReferenceListsAsync(businessUnitId, Actor, ct);
                    if (created > 0)
                    {
                        completed++;
                        rows += created;
                        logger.LogInformation(
                            "Reference lists reconciled for business unit {BusinessUnitId}: {Rows} row(s) added.",
                            businessUnitId, created);
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // One tenant's failure must not cost the others their lists, and the change
                    // tracker must not carry that tenant's half-built rows into the next tenant's
                    // save.
                    failures++;
                    db.ChangeTracker.Clear();
                    logger.LogError(ex,
                        "Reference list reconciliation failed for business unit {BusinessUnitId}; continuing.",
                        businessUnitId);
                }
            }

            await transaction.CommitAsync(ct);
            return new TenantReferenceListReconciliation(businessUnitIds.Count, completed, rows, failures);
        });
    }
}

/// <summary>
/// Runs <see cref="TenantReferenceListReconciler"/> once at host start, after migrations.
///
/// <para>Guarded by <c>TenantBaseline:ReconcileReferenceListsOnStartup</c>, which defaults to ON:
/// the whole point is that a tenant provisioned before a list existed gets it without anybody
/// remembering to run a repair. Turning it off is a configured decision for an operator who has
/// a reason. Like <c>ModuleCatalogStartupService</c>, a failure here is logged and swallowed —
/// reference data is not worth refusing to serve traffic over.</para>
/// </summary>
public sealed class TenantReferenceListStartupReconciler(
    IServiceScopeFactory scopeFactory,
    IConfiguration configuration,
    ILogger<TenantReferenceListStartupReconciler> logger) : IHostedService
{
    public const string EnabledConfigurationKey = "TenantBaseline:ReconcileReferenceListsOnStartup";

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!configuration.GetValue(EnabledConfigurationKey, true))
        {
            logger.LogInformation(
                "Tenant reference-list reconciliation is disabled ({Key}=false); tenants provisioned "
                + "before a list existed will not receive it.", EnabledConfigurationKey);
            return;
        }

        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<ErpRfqAutomationContext>();
            var seeder = scope.ServiceProvider.GetRequiredService<ITenantBaselineSeeder>();
            var result = await TenantReferenceListReconciler.RunAsync(db, seeder, logger, cancellationToken);
            logger.LogInformation(
                "Tenant reference lists reconciled: {Swept} business unit(s) swept, {Completed} completed with "
                + "{Rows} row(s) added, {Failures} failure(s).",
                result.BusinessUnitsSwept, result.BusinessUnitsCompleted, result.RowsCreated, result.Failures);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex,
                "Tenant reference-list reconciliation failed at startup; tenants provisioned before a list "
                + "existed may still lack it. The API continues to serve.");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
