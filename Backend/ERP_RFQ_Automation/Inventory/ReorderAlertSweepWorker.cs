using ERP_RFQ_Automation.Authorization;
using ERP_RFQ_Automation.HealthChecks;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.MultiTenancy;
using ERP_RFQ_Automation.Sla;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ERP_RFQ_Automation.Inventory;

/// <summary>
/// FR-INV-04. The reorder sweep: reconciles every tenant's stock rows against their configured
/// minimum/maximum/reorder levels, raises the alerts that are newly true, resolves the ones that
/// have recovered, and mails one copy of each NEW alert to the people who can act on it.
///
/// <para>Modelled on <see cref="SlaSweepWorker"/> deliberately — same fresh DI scope per iteration,
/// same never-die loop, same heartbeat discipline, same claim-before-send ledger, and the same
/// three-state settle. That worker is owned by a concurrent workstream and is not edited here; only
/// its public claim primitives are reused, so the two engines cannot drift apart on the one thing
/// that matters, which is whether a message can be sent twice.</para>
///
/// <para><b>TENANT SCOPE IS MANDATORY.</b> A background worker has no HttpContext, so
/// <c>TenantRlsCommandInterceptor.ResolveDatabaseRole</c> hands it the BYPASSRLS pipeline role, and
/// with a null tenant the EF global filters (<c>CurrentTenantId == null || ...</c>) are no-ops —
/// BOTH isolation layers off at once. Exactly one query here runs that way, and it reads nothing
/// but tenant ids. Every other query runs inside a pushed scope, and
/// <see cref="SweepTenantAsync"/> refuses to proceed if the DbContext did not pick that scope up.
/// The explicit BusinessUnitId predicates are kept as defence in depth.</para>
///
/// <para><b>Why the period is 30 minutes and not 5.</b> Reorder is a purchasing decision measured
/// in days of lead time; nobody acts differently because they heard about it 25 minutes sooner. A
/// long period also means the resolve-then-raise cycle cannot thrash on a row hovering on its
/// threshold: a row that dips below and recovers inside one period is never mentioned at all, which
/// is the correct amount to say about it.</para>
/// </summary>
public sealed class ReorderAlertSweepWorker : BackgroundService
{
    /// <summary>Sweep period. Overridable for tests.</summary>
    public static readonly TimeSpan Period = TimeSpan.FromMinutes(30);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ITenantScopeAccessor _tenantScope;
    private readonly IBackgroundWorkerHeartbeats? _heartbeats;
    private readonly ILogger<ReorderAlertSweepWorker> _log;

    public ReorderAlertSweepWorker(
        IServiceScopeFactory scopeFactory,
        ITenantScopeAccessor tenantScope,
        ILogger<ReorderAlertSweepWorker> log,
        IBackgroundWorkerHeartbeats? heartbeats = null)
    {
        _scopeFactory = scopeFactory;
        _tenantScope = tenantScope;
        _log = log;
        _heartbeats = heartbeats;
        _heartbeats?.Register(BackgroundWorkerNames.ReorderAlertSweep, Period);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _log.LogInformation("ReorderAlertSweepWorker starting; period {Period}.", Period);
        _heartbeats?.Beat(BackgroundWorkerNames.ReorderAlertSweep, Period);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await SweepOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Reorder alert sweep iteration failed; will retry next period.");
            }

            _heartbeats?.Beat(BackgroundWorkerNames.ReorderAlertSweep, Period);

            try { await Task.Delay(Period, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }

        _log.LogInformation("ReorderAlertSweepWorker stopped.");
    }

    /// <summary>Resolves the tenant list once, then does all per-tenant work inside a pushed tenant
    /// scope. Returns the number of business units swept.</summary>
    internal async Task<int> SweepOnceAsync(CancellationToken ct)
    {
        var businessUnits = await ResolveActiveBusinessUnitsAsync(ct);

        foreach (var bu in businessUnits)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                await SweepTenantAsync(bu, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                _log.LogError(ex, "Reorder alert sweep failed for BU {Bu}; continuing with next tenant.", bu);
            }
        }

        return businessUnits.Count;
    }

    /// <summary>
    /// The ONLY query in this worker that runs without a tenant scope, and therefore under the
    /// BYPASSRLS pipeline role: the distinct tenants that hold stock. It reads nothing but ids.
    ///
    /// <para>Suspended and archived tenants are dropped HERE, in this scope, for the reason
    /// <see cref="SlaSweepWorker"/> states: consulted under a pushed scope the work gate's platform
    /// read is refused at column level and fails OPEN, admitting everyone silently. Skipping is safe
    /// — every alert is re-derived from scratch each pass, so a suspension is a gap in notification
    /// rather than a lost record.</para>
    /// </summary>
    private async Task<IReadOnlyList<long>> ResolveActiveBusinessUnitsAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ErpRfqAutomationContext>();

        // Only tenants that have CONFIGURED something. A tenant with stock but no minimum, no
        // maximum and no reorder point can produce no alert by construction, so sweeping it is
        // work that can only ever find nothing.
        var businessUnits = await db.Set<Models.Inventory>().AsNoTracking().IgnoreQueryFilters()
            .Where(x => x.Buid != null
                        && (x.MinimumLevel != null || x.MaximumLevel != null || x.ReorderPoint > 0m))
            .Select(x => x.Buid!.Value)
            .Distinct()
            .OrderBy(id => id)
            .ToListAsync(ct);

        var gate = scope.ServiceProvider
            .GetService<ERP_RFQ_Automation.Platform.Lifecycle.ITenantWorkGate>();
        if (gate is null || businessUnits.Count == 0) return businessUnits;

        return await gate.FilterServiceableAsync(businessUnits, ct);
    }

    internal async Task SweepTenantAsync(long bu, CancellationToken ct)
    {
        using var tenant = _tenantScope.Push(bu);
        using var scope = _scopeFactory.CreateScope();

        var db = scope.ServiceProvider.GetRequiredService<ErpRfqAutomationContext>();
        var alerts = scope.ServiceProvider.GetRequiredService<IReorderAlertService>();
        var notifications = scope.ServiceProvider.GetRequiredService<ISlaNotifications>();

        // Fail closed. If the DbContext did not pick the pushed scope up, every query below would
        // silently run cross-tenant under the bypass role — which is what this worker's whole
        // shape exists to prevent, and it would be invisible until a tenant saw another tenant's
        // part numbers in their own alert email.
        if (db.ScopedTenantId != bu)
        {
            throw new InvalidOperationException(
                $"Reorder alert sweep refused to run for BU {bu}: the DbContext resolved tenant "
                + $"{db.ScopedTenantId?.ToString() ?? "<none>"}. Tenant scope is mandatory for this worker.");
        }

        var (summary, raised) = await alerts.ReconcileAsync(bu, ct);
        if (summary.Raised > 0 || summary.Resolved > 0 || summary.Suppressed > 0)
            _log.LogInformation(
                "BU {Bu}: reorder sweep raised {Raised}, resolved {Resolved}, suppressed {Suppressed} "
                + "(already covered by incoming supply).",
                bu, summary.Raised, summary.Resolved, summary.Suppressed);
        if (raised.Count == 0) return;

        var recipients = await GetBuyersAndManagersAsync(db, bu, ct);
        if (recipients.Count == 0)
        {
            // The alerts are still on the ledger and still visible on the screen — the email is one
            // delivery of the alert, not the alert. Say so rather than dropping them silently.
            _log.LogWarning(
                "BU {Bu}: {Count} reorder alert(s) raised but no manager/admin user could be resolved to notify. "
                + "The alerts remain OPEN on the inventory alerts screen.",
                bu, raised.Count);
            return;
        }

        var labels = await ResolveLabelsAsync(db, bu, raised, ct);

        foreach (var alert in raised)
        {
            var label = labels.GetValueOrDefault(alert.InventoryId)
                        ?? $"Stock row {alert.InventoryId}";
            var (headline, detail) = Compose(alert, label);
            var delivered = 0;
            foreach (var recipient in recipients)
            {
                if (await SendOnceAsync(db, bu, alert, recipient, ct,
                        () => notifications.SendDeadlineAlertAsync(recipient.Email, recipient.FirstName,
                            alert.Severity, label, headline, detail, bu, ct)))
                    delivered++;
            }

            if (delivered > 0)
            {
                // Counted on the alert itself so the screen can say "notified 3" rather than
                // leaving a blank that reads like "we never tried".
                await db.Set<InventoryReorderAlert>().Where(x => x.Id == alert.Id)
                    .ExecuteUpdateAsync(set => set.SetProperty(x => x.NotifiedCount, delivered), ct);
            }
        }
    }

    /// <summary>
    /// The message. It states the arithmetic that produced the alert — available, incoming, the
    /// threshold — because an alert that only says "low stock" makes the reader open three screens
    /// to find out whether it is worth acting on, and after the third time they stop opening them.
    /// </summary>
    internal static (string Headline, string Detail) Compose(InventoryReorderAlert alert, string label)
    {
        var position =
            $"Available to promise is {alert.AvailableQuantity:0.####} of {alert.OnHandQuantity:0.####} on hand"
            + (alert.IncomingQuantity > 0m
                ? $", with {alert.IncomingQuantity:0.####} already on order — {alert.ProjectedQuantity:0.####} projected."
                : ", and nothing is on order.");

        return alert.Kind switch
        {
            ReorderAlertKinds.OutOfStock => (
                $"Out of stock — {label}",
                $"{label} can promise nothing. {position} The minimum level set for this item and warehouse is "
                + $"{alert.ThresholdQuantity:0.####}. Raise a purchase order, or move stock in from another warehouse."),

            ReorderAlertKinds.BelowMinimum => (
                $"Below minimum level — {label}",
                $"{label} is {alert.ShortfallQuantity:0.####} unit(s) short of its minimum level of "
                + $"{alert.ThresholdQuantity:0.####}. {position} Buying the shortfall now keeps the item "
                + "sellable; leaving it means the next enquiry is quoted against stock that is not there."),

            ReorderAlertKinds.Overstock => (
                $"Above maximum level — {label}",
                $"{label} holds {alert.OnHandQuantity:0.####} units against a maximum of "
                + $"{alert.ThresholdQuantity:0.####} — {alert.ShortfallQuantity:0.####} more than the level set "
                + "for this warehouse. That is capital and shelf space tied up in stock nobody has asked for. "
                + "Review the buying policy, transfer the excess, or write the level up if it is wrong."),

            _ => (
                $"Reorder point reached — {label}",
                $"{label} has reached its reorder point of {alert.ThresholdQuantity:0.####}. {position} "
                + "This is the level at which an order has to be placed for stock to arrive before the shelf "
                + "empties, so it is a decision to take now rather than a shortage to fix later."),
        };
    }

    private sealed record Recipient(long Id, string Email, string FirstName);

    /// <summary>
    /// Who hears about a reorder alert. Managers and admins by RoleRank, the same selection
    /// <see cref="SlaSweepWorker"/> uses for its escalations — chosen by rank rather than by a
    /// substring of the role name, so a rename cannot silently empty the audience.
    /// </summary>
    private static async Task<List<Recipient>> GetBuyersAndManagersAsync(
        ErpRfqAutomationContext db, long bu, CancellationToken ct)
    {
        var rows = await db.Users.AsNoTracking()
            .Where(u => u.Buid == bu && u.IsActive != false && u.RoleId != null)
            .Join(db.SetupMasters.AsNoTracking().Where(SetupTypes.IsRoleRow),
                u => u.RoleId, s => s.SetupId,
                (u, s) => new { u.Id, u.Email, u.FirstName, s.RoleRank })
            .ToListAsync(ct);

        return rows
            .Where(r => !string.IsNullOrWhiteSpace(r.Email) && r.RoleRank >= RoleRanks.Manager)
            .Select(r => new Recipient(r.Id, r.Email, r.FirstName))
            .ToList();
    }

    private static async Task<Dictionary<long, string>> ResolveLabelsAsync(
        ErpRfqAutomationContext db, long bu, IReadOnlyList<InventoryReorderAlert> alerts, CancellationToken ct)
    {
        var inventoryIds = alerts.Select(x => x.InventoryId).Distinct().ToArray();
        var rows = await (
            from inventory in db.Set<Models.Inventory>().AsNoTracking()
            join product in db.Products.AsNoTracking() on inventory.ProductId equals product.Id
            join warehouse in db.Warehouses.AsNoTracking() on inventory.WarehouseId equals warehouse.Id
            where inventory.Buid == bu && inventoryIds.Contains(inventory.Id)
            select new
            {
                InventoryId = inventory.Id, product.PartNo, product.ProductName, warehouse.WarehouseName,
            }).ToListAsync(ct);

        return rows.ToDictionary(
            x => x.InventoryId,
            x => $"{x.PartNo} ({x.ProductName ?? x.PartNo}) at {x.WarehouseName}");
    }

    /// <summary>
    /// Claims ONE recipient's copy of an alert and sends it, then records what the transport said.
    ///
    /// <para>The three outcomes are not "success and failure", and this is the whole reason the
    /// send does not return a bool. A bool can only say "nothing threw": SMTP that took the message
    /// body and dropped the connection before its 250 reads as failure and the next sweep mails the
    /// buyer the same alert again, while a provider that returned nothing at all reads as success
    /// and the alert is never sent. <b>NotSent</b> releases the claim, because the provider
    /// demonstrably never had the message. <b>Sent</b> keeps it with the acceptance evidence.
    /// <b>Uncertain</b> also keeps it — the provider may already have it, and a duplicate is not
    /// recoverable from somebody's inbox whereas a missed copy is recoverable from this ledger and
    /// from the alerts screen, which still shows the alert as OPEN.</para>
    ///
    /// <para>The claim mechanics are <see cref="SlaSweepWorker"/>'s, called rather than copied.</para>
    /// </summary>
    private async Task<bool> SendOnceAsync(
        ErpRfqAutomationContext db, long bu, InventoryReorderAlert alert, Recipient recipient,
        CancellationToken ct, Func<Task<SlaSendResult>> send)
    {
        if (string.IsNullOrWhiteSpace(recipient.Email)) return false;

        // Keyed on the ALERT id, not the stock row: a row that recovers and falls short again is a
        // new alert and earns a fresh copy, while a re-sweep against the same still-open alert is
        // refused by the unique index. Without the alert id in the key the second shortage of the
        // year would be silent, and that is the one that loses the order.
        var claim = await SlaSweepWorker.TryClaimEventAsync(
            db, bu, ReorderAlertEventType, alert.Id, alert.Severity, null, recipient.Email, ct);
        if (claim is null) return false;

        SlaSendResult result;
        try
        {
            result = await send();
        }
        catch (Exception ex)
        {
            await SlaSweepWorker.SettleClaimAsync(db, claim, SlaEventStatuses.Uncertain,
                $"SEND_THREW:{ex.GetType().Name}", null, null, ct);
            _log.LogError(ex,
                "Reorder alert {AlertId} to {Recipient} for BU {Bu} threw inside the transport; "
                + "delivery is UNCERTAIN and it will NOT be re-sent.", alert.Id, recipient.Email, bu);
            throw;
        }

        switch (result.Outcome)
        {
            case SlaSendOutcome.Sent:
                await SlaSweepWorker.SettleClaimAsync(db, claim, SlaEventStatuses.Sent, null,
                    result.Provider, result.AcceptanceReference, ct);
                return true;

            case SlaSendOutcome.Uncertain:
                await SlaSweepWorker.SettleClaimAsync(db, claim, SlaEventStatuses.Uncertain,
                    result.Reason, null, null, ct);
                _log.LogError(
                    "Reorder alert {AlertId} to {Recipient} for BU {Bu} has an UNCERTAIN outcome ({Reason}); "
                    + "the claim is kept so it is never re-sent. The alert stays OPEN on the alerts screen.",
                    alert.Id, recipient.Email, bu, result.Reason);
                return false;

            default:
                await SlaSweepWorker.SettleClaimAsync(db, claim, SlaEventStatuses.Released,
                    result.Reason, null, null, ct);
                _log.LogWarning(
                    "Reorder alert {AlertId} to {Recipient} for BU {Bu} was definitely not sent ({Reason}); "
                    + "claim released for retry.", alert.Id, recipient.Email, bu, result.Reason);
                return false;
        }
    }

    /// <summary>The <c>SlaEvent.EntityType</c> this engine claims under. Distinct from every SLA
    /// entity type so a reorder alert can never collide with a deadline alert's key.</summary>
    internal const string ReorderAlertEventType = "inventory-reorder";
}
