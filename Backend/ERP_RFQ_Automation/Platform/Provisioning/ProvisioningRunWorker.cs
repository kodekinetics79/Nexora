using Microsoft.Extensions.Options;

namespace ERP_RFQ_Automation.Platform.Provisioning;

/// <summary>
/// The process that actually provisions tenants.
///
/// <para><b>Why the work left the request thread.</b> Provisioning used to run inside the HTTP
/// call that asked for it, which tied a customer's tenant to one connection, one process and one
/// proxy timeout: a restart mid-provision left no record of what had been done, and a timeout
/// left the operator unable to tell a slow success from a silent failure. Moving it here makes
/// the queue a table — an execution outlives the request, the process and the node, and any
/// instance can pick up work another one abandoned.</para>
///
/// <para><b>No tenant scope, deliberately — the same reasoning as <c>BillingRunWorker</c>.</b>
/// <c>SlaSweepWorker</c> pushes a tenant scope per business unit because it reads tenant tables
/// under RLS. This worker CREATES the business unit, so there is no scope to push for most of its
/// life; pushing one would route its commands to <c>nexora_tenant_app</c>, which is
/// RLS-constrained and has no privilege to create a tenant that does not exist yet. With no scope
/// the command interceptor supplies <c>nexora_pipeline_app</c>, which is what a control-plane
/// write needs and is exactly what the synchronous endpoint already ran as.</para>
///
/// <para>Error containment follows the ExtractionWorker / SlaSweepWorker / BillingRunWorker
/// discipline: one execution's failure never blocks the rest, and the loop never dies.</para>
/// </summary>
public sealed class ProvisioningRunWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ProvisioningRunSignal _signal;
    private readonly IOptionsMonitor<ProvisioningOptions> _options;
    private readonly ILogger<ProvisioningRunWorker> _log;
    private readonly string _workerId;

    public ProvisioningRunWorker(
        IServiceScopeFactory scopeFactory,
        ProvisioningRunSignal signal,
        IOptionsMonitor<ProvisioningOptions> options,
        ILogger<ProvisioningRunWorker> log)
    {
        _scopeFactory = scopeFactory;
        _signal = signal;
        _options = options;
        _log = log;
        _workerId = $"{Environment.MachineName}:{Environment.ProcessId}";
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.CurrentValue.Enabled)
        {
            // Loud on purpose: with this off, an accepted provisioning request is accepted and
            // then never acted on. The operator sees a queued execution that stays queued, which
            // is a worse failure than the synchronous behaviour this replaced.
            _log.LogWarning(
                "ProvisioningRunWorker is DISABLED by configuration ({Section}:Enabled=false). Submitted " +
                "provisioning executions will be accepted and will never run.", ProvisioningOptions.SectionName);
            return;
        }

        _log.LogInformation(
            "ProvisioningRunWorker {WorkerId} starting; poll {Interval}, lease {Lease}, batch {Batch}.",
            _workerId, _options.CurrentValue.PollInterval, _options.CurrentValue.LeaseDuration,
            _options.CurrentValue.BatchSize);

        try { await Task.Delay(_options.CurrentValue.InitialDelay, stoppingToken); }
        catch (OperationCanceledException) { return; }

        if (!await IsWiredAsync())
            return;

        while (!stoppingToken.IsCancellationRequested)
        {
            var options = _options.CurrentValue;
            try
            {
                var ran = await RunOnceAsync(options.BatchSize, stoppingToken);
                if (ran > 0)
                    _log.LogInformation("Provisioning sweep drove {Count} execution(s).", ran);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                // The loop must never die on an unexpected error. A dead provisioning worker is
                // indistinguishable from an idle one until an operator submits and nothing moves.
                _log.LogError(exception, "Provisioning sweep failed; will retry next interval.");
            }

            // Woken immediately by a submit or a retry, so the console's first poll already shows
            // a step in flight. Falls back to the interval, which is what recovers work left
            // behind by a process that died holding a lease.
            await _signal.WaitAsync(options.PollInterval, stoppingToken);
        }

        _log.LogInformation("ProvisioningRunWorker {WorkerId} stopped.", _workerId);
    }

    /// <summary>
    /// Refuses to start when the entities are not in the EF model, with the exact line that is
    /// missing.
    ///
    /// <para>The alternative is worse than it sounds. Without the model splice every sweep throws
    /// "cannot be used ... not part of the model", the loop catches it and retries, and the
    /// deployment produces the same stack trace every five seconds forever — a wall of noise
    /// whose single actionable sentence is buried in it. One message, once, naming the fix.</para>
    /// </summary>
    private async Task<bool> IsWiredAsync()
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider
            .GetRequiredService<ERP_RFQ_Automation.Models.ErpRfqAutomationContext>();
        if (db.Model.FindEntityType(typeof(ProvisioningExecution)) is not null)
            return true;

        _log.LogError(
            "ProvisioningRunWorker is stopping: platform.ProvisioningExecutions is not in the EF model, so "
            + "no submitted provisioning work can be read or advanced. Add "
            + "'modelBuilder.ApplyTenantProvisioningModel();' to OnModelCreatingPartial in "
            + "Models/ErpRfqAutomationContext.Tenancy.cs (next to ApplyTenantOnboardingModel), and add the "
            + "matching migration. Until then the synchronous POST /api/platform/tenants remains the only "
            + "working provisioning path.");
        return false;
    }

    /// <summary>
    /// One sweep. Exposed so a test can drive a single deterministic pass and assert on it —
    /// the same seam <c>BillingRunWorker.SweepOnceAsync</c> provides.
    /// </summary>
    internal async Task<int> RunOnceAsync(int batchSize, CancellationToken ct)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var runner = scope.ServiceProvider.GetRequiredService<ITenantProvisioningRunner>();
        return await runner.RunAvailableAsync(batchSize, ct);
    }
}
