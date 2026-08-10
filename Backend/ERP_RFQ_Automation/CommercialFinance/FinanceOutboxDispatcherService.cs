using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.MultiTenancy;
using ERP_RFQ_Automation.Platform.Lifecycle;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace ERP_RFQ_Automation.CommercialFinance;

/// <summary>
/// Drains the commercial-finance outbox to the operator's configured endpoint.
///
/// <para><b>Tenant scope.</b> This used to claim in ONE unscoped batch.
/// <see cref="FinanceOutboxStore.ClaimAsync"/> reads <c>ScopedTenantId ?? 0</c> and treats 0 as
/// "every tenant", and a background worker has no ambient tenant — so the wildcard was reached by
/// FALLBACK rather than by decision, and one claim leased the oldest rows across every customer
/// under the BYPASSRLS pipeline role. It now follows the <c>QuoteDeliveryDispatcher</c> and
/// <c>SlaSweepWorker</c> shape: resolve the tenant ids once (the only unscoped query, and it reads
/// nothing but business unit ids), then claim inside a pushed tenant scope with a fail-closed
/// guard. The wildcard is no longer on any production path.</para>
///
/// <para>The DISPATCH half was still unscoped after that: <c>Parallel.ForEachAsync</c> forks each
/// message onto its own execution context and <see cref="DispatchAsync"/> opened a fresh DI scope
/// with no tenant, so the completion and failure transitions ran under the BYPASSRLS pipeline role.
/// A push around the loop cannot fix that — it does not survive the fork — so the push is now the
/// FIRST statement inside <see cref="DispatchAsync"/>, before the scope is created, with the same
/// fail-closed guard.</para>
///
/// <para><b>Why the gate filters the CLAIM and not the dispatch.</b> Claiming a message increments
/// <c>AttemptCount</c>. Leasing a suspended tenant's events and then declining to publish them
/// would re-claim them every cycle, inflate the attempt count against a <c>MaxAttempts</c> ceiling
/// nobody had spent, and dead-letter a customer's finance events for the crime of being suspended.
/// Excluding the tenant before the claim defers instead: the rows are untouched and go out in
/// order on reinstatement.</para>
/// </summary>
public sealed class FinanceOutboxDispatcherService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IOptionsMonitor<FinanceOutboxDispatcherOptions> _options;
    private readonly ILogger<FinanceOutboxDispatcherService> _logger;
    private readonly ITenantScopeAccessor? _tenantScope;
    private readonly string _workerId;

    /// <summary>
    /// <paramref name="tenantScope"/> is optional only so existing callers stay source-compatible;
    /// when it is not supplied the accessor is resolved from the container instead. It is never
    /// substituted with a fresh instance: the scope is an AsyncLocal read by the DbContext, so a
    /// second accessor would push into one object and be read from another, and every claim would
    /// silently fall back to the cross-tenant wildcard.
    /// </summary>
    public FinanceOutboxDispatcherService(
        IServiceScopeFactory scopeFactory,
        IOptionsMonitor<FinanceOutboxDispatcherOptions> options,
        ILogger<FinanceOutboxDispatcherService> logger,
        ITenantScopeAccessor? tenantScope = null)
    {
        _scopeFactory = scopeFactory;
        _options = options;
        _logger = logger;
        _tenantScope = tenantScope;
        _workerId = $"{Environment.MachineName}:{Environment.ProcessId}:{Guid.NewGuid():N}";
    }

    /// <summary>The container's accessor, which is a singleton, so any scope yields the same one.</summary>
    private ITenantScopeAccessor TenantScope()
    {
        if (_tenantScope is not null) return _tenantScope;
        using var scope = _scopeFactory.CreateScope();
        return scope.ServiceProvider.GetRequiredService<ITenantScopeAccessor>();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.CurrentValue.Enabled)
        {
            _logger.LogInformation("Commercial finance outbox dispatcher is disabled.");
            return;
        }

        _logger.LogInformation("Commercial finance outbox dispatcher {WorkerId} started.", _workerId);
        var consecutiveErrors = 0;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var options = _options.CurrentValue;
                var messages = await ClaimAsync(options, stoppingToken);
                consecutiveErrors = 0;

                if (messages.Count == 0)
                {
                    await Task.Delay(options.PollInterval, stoppingToken);
                    continue;
                }

                await Parallel.ForEachAsync(
                    messages,
                    new ParallelOptions
                    {
                        CancellationToken = stoppingToken,
                        MaxDegreeOfParallelism = options.MaxConcurrency
                    },
                    (message, cancellationToken) => DispatchAsync(message, options, cancellationToken));
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                consecutiveErrors = Math.Min(consecutiveErrors + 1, 30);
                var delay = CalculateBackoff(
                    TimeSpan.FromSeconds(1),
                    _options.CurrentValue.MaximumDispatcherBackoff,
                    consecutiveErrors);
                _logger.LogError(
                    exception,
                    "Commercial finance outbox dispatcher cycle failed; retrying in {Delay}.",
                    delay);
                await Task.Delay(delay, stoppingToken);
            }
        }

        _logger.LogInformation("Commercial finance outbox dispatcher {WorkerId} stopped.", _workerId);
    }

    /// <summary>Internal so the suspension tests can drive one claim cycle without standing up
    /// the whole hosted service and its publisher.</summary>
    internal async Task<IReadOnlyList<FinanceOutboxEnvelope>> ClaimAsync(
        FinanceOutboxDispatcherOptions options,
        CancellationToken cancellationToken)
    {
        var businessUnits = await ResolvePendingBusinessUnitsAsync(cancellationToken);

        // Null, not empty: the container could not supply the outbox table, so this cycle has no
        // way to enumerate tenants. Refusing to drain would take the operator's own revenue
        // integration down over a container misconfiguration, so it falls back to the legacy
        // single unscoped claim — LOUDLY, because that claim is the cross-tenant wildcard this
        // worker otherwise no longer uses. Unreachable in production, where Program.cs always
        // registers the context.
        if (businessUnits is null)
        {
            _logger.LogWarning(
                "The finance outbox dispatcher could not resolve ErpRfqAutomationContext, so it "
                + "cannot enumerate tenants. Falling back to ONE unscoped claim across every "
                + "tenant; suspension is not enforced on this cycle.");

            await using var fallbackScope = _scopeFactory.CreateAsyncScope();
            return await fallbackScope.ServiceProvider.GetRequiredService<IFinanceOutboxStore>()
                .ClaimAsync(_workerId, options.BatchSize, options.LeaseDuration, cancellationToken);
        }

        if (businessUnits.Count == 0) return [];

        var claimed = new List<FinanceOutboxEnvelope>();
        // BatchSize keeps meaning what it meant — a per-CYCLE budget, not a per-tenant one.
        // Claiming a full batch for each of a hundred tenants would turn one configured batch into
        // a hundred, and the dispatch fan-out below is bounded by MaxConcurrency, not by this.
        var remaining = options.BatchSize;
        var tenantScope = TenantScope();

        foreach (var businessUnitId in businessUnits)
        {
            if (remaining <= 0) break;
            cancellationToken.ThrowIfCancellationRequested();

            using var tenant = tenantScope.Push(businessUnitId);
            await using var scope = _scopeFactory.CreateAsyncScope();
            EnsureScoped(scope.ServiceProvider, businessUnitId);

            var store = scope.ServiceProvider.GetRequiredService<IFinanceOutboxStore>();
            var batch = await store.ClaimAsync(
                _workerId, remaining, options.LeaseDuration, cancellationToken);

            // A claim that came back for another tenant means the scope was not honoured
            // somewhere. Refuse the whole cycle rather than publish one customer's finance events
            // under another customer's claim.
            foreach (var message in batch)
                if (message.BusinessUnitId != businessUnitId)
                    throw new InvalidOperationException(
                        $"Finance outbox message {message.Id} was claimed for BU {businessUnitId} but "
                        + $"belongs to BU {message.BusinessUnitId}; refusing to dispatch cross-tenant work.");

            claimed.AddRange(batch);
            remaining -= batch.Count;
        }

        return claimed;
    }

    /// <summary>
    /// The ONLY query in this worker that runs without a tenant scope (and therefore under the
    /// BYPASSRLS pipeline role): the distinct business units holding a claimable row. It projects
    /// tenant ids and nothing else, and the predicate mirrors the store's own claim eligibility so
    /// a tenant with nothing to send is never scoped up at all.
    ///
    /// <para>Suspended and archived tenants are dropped here, in the one scope with no tenant
    /// pushed — a hard requirement of the gate, whose platform read fails OPEN under the RLS tenant
    /// role (see <c>ITenantWorkGate</c>). Dropping them at the CLAIM rather than at the dispatch is
    /// what makes this deferral instead of destruction; see the type docs.</para>
    ///
    /// <para>Returns null — distinct from an empty list — when the container holds no
    /// <see cref="ErpRfqAutomationContext"/>, so the caller can tell "no tenant has anything to
    /// send" apart from "this cycle cannot tell".</para>
    /// </summary>
    private async Task<IReadOnlyList<long>?> ResolvePendingBusinessUnitsAsync(
        CancellationToken cancellationToken)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetService<ErpRfqAutomationContext>();
        if (db is null) return null;
        var now = DateTime.UtcNow;

        var businessUnits = await db.Set<FinanceOutboxMessage>().AsNoTracking().IgnoreQueryFilters()
            .Where(x => x.ProcessedOn == null && x.DeadLetteredOn == null)
            .Where(x => x.AvailableOn <= now)
            .Where(x => x.LeaseUntil == null || x.LeaseUntil <= now)
            .Select(x => x.BusinessUnitId)
            .Distinct()
            .OrderBy(id => id)
            .ToListAsync(cancellationToken);

        // Resolved from THIS scope rather than injected: the dispatcher is a hosted singleton and
        // the gate is scoped, so a constructor dependency would fail startup scope validation.
        var gate = scope.ServiceProvider.GetService<ITenantWorkGate>();
        if (gate is null || businessUnits.Count == 0) return businessUnits;

        return await gate.FilterServiceableAsync(businessUnits, cancellationToken);
    }

    /// <summary>
    /// Fail closed, on BOTH halves of the cycle. If the DbContext in this scope did not resolve the
    /// pushed tenant, the claim would fall back to the <c>ScopedTenantId ?? 0</c> wildcard and lease
    /// every tenant's rows again, and the dispatch would complete or fail a message under the
    /// BYPASSRLS pipeline role — which is exactly the defect being fixed, so it must be an error
    /// rather than a warning.
    /// </summary>
    private static void EnsureScoped(IServiceProvider provider, long businessUnitId)
    {
        var db = provider.GetRequiredService<ErpRfqAutomationContext>();
        if (db.ScopedTenantId == businessUnitId) return;
        throw new InvalidOperationException(
            $"Finance outbox dispatch refused to run for BU {businessUnitId}: the DbContext resolved "
            + $"tenant {db.ScopedTenantId?.ToString() ?? "<none>"}. Tenant scope is mandatory for this worker.");
    }

    private async ValueTask DispatchAsync(
        FinanceOutboxEnvelope message,
        FinanceOutboxDispatcherOptions options,
        CancellationToken cancellationToken)
    {
        // THE PUSH IS THE FIRST STATEMENT HERE, and it has to be.
        //
        // The scope is an AsyncLocal. Parallel.ForEachAsync forks this method onto its own
        // execution contexts, and an AsyncLocal set in the caller before the fork is COPIED into
        // the fork but a push/dispose pair around the loop cannot bracket what the fork does after
        // the loop moves on — so a scope pushed around the ForEachAsync above would not survive
        // into these bodies in any dependable way. The push has to be inside the fork, and it has
        // to precede CreateAsyncScope: ITenantContext captures the ambient tenant in its
        // CONSTRUCTOR, so the DbContext resolved from this scope is fixed the moment the scope
        // first resolves it.
        //
        // Until now every dispatch ran with a null tenant and therefore under the BYPASSRLS
        // pipeline role. It was contained only because CompleteAsync/FailAsync key on the message
        // id PLUS the lease token this worker itself minted — containment by accident, one query
        // change away from being a cross-tenant write. With the scope pushed, the store's
        // ExecuteUpdate predicates also carry the global query filter, so the transition can only
        // touch a row that belongs to the tenant the message came from.
        var tenantScope = TenantScope();
        using var tenant = tenantScope.Push(message.BusinessUnitId);
        await using var scope = _scopeFactory.CreateAsyncScope();
        EnsureScoped(scope.ServiceProvider, message.BusinessUnitId);
        var publisher = scope.ServiceProvider.GetRequiredService<IFinanceEventPublisher>();
        var store = scope.ServiceProvider.GetRequiredService<IFinanceOutboxStore>();

        try
        {
            await publisher.PublishAsync(message, cancellationToken);
            await store.CompleteAsync(message.Id, _workerId, message.LeaseToken, cancellationToken);
            _logger.LogInformation(
                "Published finance event {EventId} ({EventType}) on attempt {AttemptCount}.",
                message.EventId,
                message.EventType,
                message.AttemptCount);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (FinanceOutboxLeaseConflictException exception)
        {
            _logger.LogWarning(
                exception,
                "Finance event {EventId} lost lease ownership; no state transition was made.",
                message.EventId);
        }
        catch (Exception exception)
        {
            var retryDelay = CalculateBackoff(
                options.InitialRetryDelay,
                options.MaximumRetryDelay,
                message.AttemptCount);

            try
            {
                await store.FailAsync(
                    message.Id,
                    _workerId,
                    message.LeaseToken,
                    DescribeFailure(exception),
                    retryDelay,
                    options.MaxAttempts,
                    cancellationToken);
                _logger.LogWarning(
                    exception,
                    "Finance event {EventId} failed on attempt {AttemptCount}; next retry delay is {RetryDelay}.",
                    message.EventId,
                    message.AttemptCount,
                    retryDelay);
            }
            catch (FinanceOutboxLeaseConflictException leaseException)
            {
                _logger.LogWarning(
                    leaseException,
                    "Finance event {EventId} failed after its lease expired or was fenced.",
                    message.EventId);
            }
        }
    }

    private static TimeSpan CalculateBackoff(TimeSpan initial, TimeSpan maximum, int attempt)
    {
        var exponent = Math.Clamp(attempt - 1, 0, 30);
        var milliseconds = Math.Min(
            maximum.TotalMilliseconds,
            initial.TotalMilliseconds * Math.Pow(2, exponent));
        return TimeSpan.FromMilliseconds(milliseconds);
    }

    private static string DescribeFailure(Exception exception)
    {
        var description = $"{exception.GetType().Name}: {exception.Message}";
        return description.Length <= 2000 ? description : description[..2000];
    }
}
