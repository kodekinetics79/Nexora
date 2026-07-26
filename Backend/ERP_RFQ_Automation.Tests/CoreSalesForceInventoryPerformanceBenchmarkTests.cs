using System.Collections.Concurrent;
using System.Data.Common;
using System.Diagnostics;
using ERP_RFQ_Automation.CommercialIntelligence.Sales;
using ERP_RFQ_Automation.CommercialRouting;
using ERP_RFQ_Automation.Inventory;
using ERP_RFQ_Automation.Inventory.Commercial;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.ProductIntelligence;
using ERP_RFQ_Automation.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;
using Xunit.Abstractions;

namespace ERP_RFQ_Automation.Tests;

[Collection(PostgreSqlIntegrationCollection.Name)]
public sealed class CoreSalesForceInventoryPerformanceBenchmarkTests(
    PostgreSqlTestDatabase database,
    ITestOutputHelper output)
{
    private const long Tenant = 989_000;
    private const long InventoryId = 989_001;
    private static readonly DateTime Now = new(2026, 7, 25, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    [Trait("Category", "PostgreSQL")]
    [Trait("Category", "Benchmark")]
    public async Task Representative_local_release_benchmark_reports_observed_costs_without_thresholds()
    {
        var results = new List<Measurement>();
        var allocatedBefore = GC.GetTotalAllocatedBytes(true);

        results.Add(BenchmarkCustomerAndContactMatching());
        results.Add(BenchmarkWeightedAssignment());
        results.Add(await BenchmarkProductLookupAsync());
        results.Add(BenchmarkMultiWarehouseAtp());
        results.Add(await BenchmarkReadAggregationAsync());
        results.Add(await BenchmarkConcurrentReservationsAsync());
        results.Add(await BenchmarkTenThousandLineClassificationAsync());

        var allocatedAfter = GC.GetTotalAllocatedBytes(true);
        var report = BuildReport(results, Math.Max(0, allocatedAfter - allocatedBefore));
        output.WriteLine(report);

        Assert.All(results, result => Assert.True(result.Operations > 0));
        Assert.Equal(0, results.Sum(result => result.ExternalCalls));
        Assert.Equal(0m, results.Sum(result => result.ExternalCost));
    }

    private static Measurement BenchmarkCustomerAndContactMatching()
    {
        const int operations = 2_000;
        var engine = new DeterministicRoutingEngine();
        var policy = new RoutingPolicy { MatchThreshold = .80m, AmbiguityMargin = .10m };
        var ownership = new CustomerOwnership
        {
            Id = 1, BusinessUnitId = Tenant, CustomerId = 40, PrimaryUserId = 501,
            Scope = OwnershipScope.GeneralCustomer, EffectiveFrom = Now.AddDays(-30), IsActive = true
        };
        var request = new RoutingRequest(Tenant, 42, "benchmark-route", "benchmark-correlation", Now,
        [
            // Verified contact email has precedence over the weaker customer-name candidate.
            new(Tenant, 40, 100, CustomerIdentifierType.Email, .98m, true),
            new(Tenant, 41, 101, CustomerIdentifierType.CustomerName, .99m, true),
        ], [ownership],
        [new RoutingUserAvailability(Tenant, 501, Workload: Workload(20))],
        new Dictionary<OwnershipScope, string?>());

        var samples = Measure(operations, () =>
        {
            var result = engine.Route(request, policy);
            Assert.Equal(40, result.Decision.CustomerId);
            Assert.Equal(CustomerMatchStatus.Matched, result.Decision.MatchStatus);
            Assert.Equal(501, result.Assignment?.ToUserId);
        });
        return Measurement.Local("customer_contact_matching", operations, samples,
            contentCount: 2, note: "Verified email candidate wins by identifier precedence; contact lookup itself is represented by persisted match evidence.");
    }

    private static Measurement BenchmarkWeightedAssignment()
    {
        const int operations = 2_000;
        var engine = new WeightedEligibleRepScoringEngine();
        var candidates = Enumerable.Range(1, 25)
            .Select(index => Candidate(600 + index, index)).ToArray();
        var request = new NewCustomerRoutingRequest(Tenant, null, "US-EAST", "VALVES", 900,
            Now, [], candidates);

        var expected = engine.Score(request).SelectedUserId;
        var samples = Measure(operations, () => Assert.Equal(expected, engine.Score(request).SelectedUserId));
        return Measurement.Local("weighted_assignment_25_reps", operations, samples,
            contentCount: candidates.Length, note: $"Stable selected user {expected}; weighted fit/capacity/workload scoring only.");
    }

    private static async Task<Measurement> BenchmarkProductLookupAsync()
    {
        const int operations = 2_000;
        var catalog = new CountingCatalog(Products(100));
        var references = new CountingReferences([]);
        var resolver = new DeterministicProductItemResolver(catalog, references);
        var samples = new long[operations];
        for (var index = 0; index < operations; index++)
        {
            var requested = index % 2 == 0 ? " pn-0042 " : " internal/0042 ";
            var started = Stopwatch.GetTimestamp();
            var result = await resolver.ResolveAsync(Request(index + 1, requested));
            samples[index] = Stopwatch.GetTimestamp() - started;
            Assert.Equal(ProductResolutionDecisionState.AutoLinked, result.DecisionState);
            Assert.Equal(42, result.ResolvedProductId);
        }
        return Measurement.Local("exact_normalized_product_lookup", operations, samples,
            queryCount: catalog.Calls + references.Calls, contentCount: 100,
            note: $"Catalog calls={catalog.Calls}; approved-reference calls={references.Calls}; exact part and exact internal-code inputs alternate.");
    }

    private static Measurement BenchmarkMultiWarehouseAtp()
    {
        const int operations = 5_000;
        var service = new FulfilmentRouteService();
        var snapshots = new[]
        {
            Snapshot(1, 8m, reserved: 1m),
            Snapshot(2, 7m, safety: 1m),
            Snapshot(3, 4m, allocated: 1m),
        };
        var samples = Measure(operations, () =>
        {
            var route = service.Classify(12m, snapshots);
            Assert.Equal(FulfilmentRouteClassification.MultipleWarehouses, route.Classification);
            Assert.Equal(12m, route.AllocatedQuantity);
            Assert.Equal(2, route.Allocations.Count);
        });
        return Measurement.Local("multi_warehouse_atp_3_warehouses", operations, samples,
            contentCount: snapshots.Length, note: "ATP subtracts reserved, allocated, and safety stock before deterministic warehouse ordering.");
    }

    private static async Task<Measurement> BenchmarkReadAggregationAsync()
    {
        const int operations = 500;
        var persistence = new CountingSalesReadPersistence();
        var service = new SalesApplicationService(persistence);
        var samples = new long[operations];
        for (var index = 0; index < operations; index++)
        {
            var started = Stopwatch.GetTimestamp();
            var result = await service.GetPerformanceAsync(Tenant,
                new SalesPerformanceQuery(null, Now.AddDays(-30), Now.AddDays(1), Now), default);
            samples[index] = Stopwatch.GetTimestamp() - started;
            Assert.Equal(10, result.Count);
        }
        return Measurement.Local("sales_performance_read_aggregation", operations, samples,
            queryCount: persistence.QueryCalls, contentCount: persistence.SourceRows,
            note: "Four persistence reads per aggregation; in-memory fixture isolates aggregation cost from network/database latency.");
    }

    private async Task<Measurement> BenchmarkConcurrentReservationsAsync()
    {
        const int attempts = 20;
        var commandCounter = new CommandCounter();
        await SeedInventoryAsync(commandCounter);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var timings = new ConcurrentBag<long>();
        var outcomes = new ConcurrentBag<string>();

        var tasks = Enumerable.Range(1, attempts).Select(async index =>
        {
            await gate.Task;
            var started = Stopwatch.GetTimestamp();
            try
            {
                await using var context = Context(commandCounter);
                var service = new InventoryAvailabilityService(context);
                await service.ReserveAsync(Tenant, InventoryId, 10m, $"benchmark-reservation-{index}", index, actor: "benchmark");
                outcomes.Add("reserved");
            }
            catch (InsufficientStockException)
            {
                outcomes.Add("insufficient_stock");
            }
            catch (PostgresException exception)
            {
                outcomes.Add($"contention_error:PostgresException:{exception.SqlState}");
            }
            catch (Exception exception)
            {
                outcomes.Add($"contention_error:{exception.GetType().Name}");
            }
            finally
            {
                timings.Add(Stopwatch.GetTimestamp() - started);
            }
        }).ToArray();

        gate.SetResult();
        await Task.WhenAll(tasks);
        await using var verify = Context(commandCounter);
        var availability = await new InventoryAvailabilityService(verify)
            .GetAvailabilityAsync(Tenant, InventoryId);
        var reserved = outcomes.Count(value => value == "reserved");
        var rejected = outcomes.Count(value => value == "insufficient_stock");
        var contentionErrors = outcomes.Count(value => value.StartsWith("contention_error", StringComparison.Ordinal));

        Assert.True(reserved > 0, $"No reservation succeeded. Outcomes: {string.Join(", ", outcomes.Order())}");
        Assert.Equal(10, rejected);
        Assert.Equal(0, contentionErrors);
        Assert.Equal(0m, availability.Available);
        return Measurement.Local("concurrent_postgresql_reservations", attempts, timings.ToArray(),
            queryCount: commandCounter.Commands, contentionCount: rejected + contentionErrors,
            note: $"reserved={reserved}; insufficient_stock={rejected}; unexpected_contention_errors={contentionErrors}; final_ATP={availability.Available}.");
    }

    private static async Task<Measurement> BenchmarkTenThousandLineClassificationAsync()
    {
        const int operations = 10_000;
        var products = Products(100);
        var catalog = new CountingCatalog(products);
        var references = new CountingReferences([]);
        var resolver = new DeterministicProductItemResolver(catalog, references);
        var fulfilment = new FulfilmentRouteService();
        var snapshots = new[] { Snapshot(1, 8m), Snapshot(2, 8m) };
        var samples = new long[operations];
        var signatures = new HashSet<string>(StringComparer.Ordinal);
        var autoLinked = 0;
        var unresolved = 0;
        var externalCalls = 0;

        for (var index = 0; index < operations; index++)
        {
            var product = (index % 100) + 1;
            var requested = index % 10 == 0 ? $"UNKNOWN-{product:0000}" : $" pn-{product:0000} ";
            signatures.Add(requested.Trim().ToUpperInvariant());
            var started = Stopwatch.GetTimestamp();
            var resolution = await resolver.ResolveAsync(Request(index + 1, requested));
            if (resolution.ResolvedProductId.HasValue)
            {
                var route = fulfilment.Classify(12m, snapshots.Select(value => value with
                    { ProductId = resolution.ResolvedProductId.Value }).ToArray());
                Assert.Equal(FulfilmentRouteClassification.MultipleWarehouses, route.Classification);
                autoLinked++;
            }
            else
            {
                unresolved++;
            }
            if (resolution.ExternalProviderUsed) externalCalls++;
            samples[index] = Stopwatch.GetTimestamp() - started;
        }

        Assert.Equal(9_000, autoLinked);
        Assert.Equal(1_000, unresolved);
        Assert.Equal(0, externalCalls);
        var duplicates = operations - signatures.Count;
        return Measurement.Local("local_classification_10000_requested_lines", operations, samples,
            queryCount: catalog.Calls + references.Calls, duplicateCount: duplicates,
            contentCount: signatures.Count,
            note: $"auto_linked={autoLinked}; unresolved={unresolved}; unique_request_signatures={signatures.Count}; requested_duplicate_signatures={duplicates}; classifier_deduplication=0.");
    }

    private async Task SeedInventoryAsync(CommandCounter counter)
    {
        await using var context = Context(counter);
        var existing = await context.Set<ERP_RFQ_Automation.Models.Inventory>()
            .IgnoreQueryFilters().SingleOrDefaultAsync(value => value.Id == InventoryId);
        if (existing is not null)
        {
            context.StockReservations.RemoveRange(context.StockReservations.IgnoreQueryFilters()
                .Where(value => value.BusinessUnitId == Tenant && value.InventoryId == InventoryId));
            context.Set<ERP_RFQ_Automation.Models.Inventory>().Remove(existing);
            await context.SaveChangesAsync();
        }
        Seed.EnsureBusinessUnit(context, Tenant);
        context.Set<ERP_RFQ_Automation.Models.Inventory>().Add(new ERP_RFQ_Automation.Models.Inventory
        {
            Id = InventoryId, Buid = Tenant, PartNo = "BENCH-ATP", ProductName = "Benchmark ATP item",
            QtyOnHand = 100m, ReorderPoint = 0m, CreatedBy = "benchmark", CreatedOn = Now
        });
        await context.SaveChangesAsync();
        counter.Reset();
    }

    private ErpRfqAutomationContext Context(CommandCounter counter)
    {
        var options = new DbContextOptionsBuilder<ErpRfqAutomationContext>()
            .UseNpgsql(database.ConnectionString)
            .AddInterceptors(counter)
            .EnableDetailedErrors()
            .Options;
        return new ErpRfqAutomationContext(options, new StubTenant(Tenant));
    }

    private static ProductResolutionRequest Request(int line, string part) => new(
        Tenant, 1, line, part, "Acme", "Representative requested line",
        [new ProductResolutionEvidence("benchmark", $"line:{line}", part)]);

    private static ProductIdentityCandidate[] Products(int count) => Enumerable.Range(1, count)
        .Select(index => new ProductIdentityCandidate(Tenant, index, $"PN-{index:0000}",
            $"INTERNAL/{index:0000}", "Acme", $"Product {index}", "Representative catalog item"))
        .ToArray();

    private static WeightedRepCandidate Candidate(int userId, int index) => new(
        new User
        {
            Id = userId, Buid = Tenant, IsActive = true, FirstName = "Rep", LastName = index.ToString(),
            Email = $"rep-{index}@nexora.invalid", PasswordHash = "not-used", ImageUrl = "n/a", CreatedBy = "benchmark"
        },
        new SalesRepProfile
        {
            BusinessUnitId = Tenant, UserId = userId, IsRoutingEligible = true,
            CapacityPercent = 60 + index % 41, DistributionWeight = 1 + index % 5,
            TerritoryKeys = index % 3 == 0 ? ["US-EAST"] : ["US-WEST"],
            ProductCategoryKeys = index % 4 == 0 ? ["VALVES"] : ["PUMPS"],
            EffectiveFromUtc = Now.AddDays(-30)
        },
        index % 2 == 0
            ? [new SalesTeamMembership { BusinessUnitId = Tenant, UserId = userId, TeamId = 900, EffectiveFromUtc = Now.AddDays(-30) }]
            : [],
        [], index * 3 % 100, Now.AddMinutes(-index));

    private static RoutingWorkloadSnapshot Workload(int points) =>
        new(1, 10, 0, 0, 0, 1, 1, 0, points);

    private static InventorySnapshot Snapshot(long warehouse, decimal onHand, decimal reserved = 0,
        decimal allocated = 0, decimal safety = 0) => new()
        {
            BusinessUnitId = Tenant, ProductId = 1, InventoryId = warehouse,
            WarehouseId = warehouse, WarehouseCode = $"WH-{warehouse}", OnHand = onHand,
            Reserved = reserved, Allocated = allocated, SafetyStock = safety, AsOf = Now
        };

    private static long[] Measure(int operations, Action action)
    {
        for (var warmup = 0; warmup < Math.Min(100, operations); warmup++) action();
        var samples = new long[operations];
        for (var index = 0; index < operations; index++)
        {
            var started = Stopwatch.GetTimestamp();
            action();
            samples[index] = Stopwatch.GetTimestamp() - started;
        }
        return samples;
    }

    private static string BuildReport(IEnumerable<Measurement> results, long processAllocationBytes)
    {
        var lines = new List<string>
        {
            "NEXORA CORE SALES FORCE + INVENTORY REPRESENTATIVE BENCHMARK",
            $"observed_at_utc={DateTime.UtcNow:O}",
            $"runtime={Environment.Version}; os={Environment.OSVersion}; processors={Environment.ProcessorCount}",
            "thresholds=none (measurements are observations, not release limits)",
            $"process_allocation_delta_bytes={processAllocationBytes} (process-wide approximation; includes PostgreSQL/test infrastructure)",
            "external_provider_calls=0; external_cost=0; network_external_calls=0",
            ""
        };
        foreach (var result in results)
            lines.Add($"{result.Name}: operations={result.Operations}; p50_ms={result.P50Milliseconds:F4}; p95_ms={result.P95Milliseconds:F4}; " +
                $"query_or_catalog_calls={Count(result.QueryCount)}; external_calls={result.ExternalCalls}; external_cost={result.ExternalCost:F2}; " +
                $"duplicate_count={result.DuplicateCount}; content_count={Count(result.ContentCount)}; contention_count={result.ContentionCount}; note={result.Note}");
        lines.Add("");
        lines.Add("Query counts are exact only where instrumented: local catalog/reference invocations, sales persistence reads, and PostgreSQL DbCommand executions.");
        lines.Add("No external AI, supplier, search, or pricing provider was configured or called.");
        return string.Join(Environment.NewLine, lines);

        static string Count(long? value) => value?.ToString() ?? "not_instrumented";
    }

    private sealed record Measurement(string Name, int Operations, double P50Milliseconds,
        double P95Milliseconds, long? QueryCount, int ExternalCalls, decimal ExternalCost,
        int DuplicateCount, long? ContentCount, int ContentionCount, string Note)
    {
        public static Measurement Local(string name, int operations, IReadOnlyCollection<long> samples,
            long? queryCount = null, int duplicateCount = 0, long? contentCount = null,
            int contentionCount = 0, string note = "")
        {
            var ordered = samples.Order().ToArray();
            return new(name, operations, Milliseconds(Percentile(ordered, .50)),
                Milliseconds(Percentile(ordered, .95)), queryCount, 0, 0m,
                duplicateCount, contentCount, contentionCount, note);
        }

        private static long Percentile(IReadOnlyList<long> ordered, double percentile) =>
            ordered[Math.Clamp((int)Math.Ceiling(ordered.Count * percentile) - 1, 0, ordered.Count - 1)];
        private static double Milliseconds(long ticks) => ticks * 1_000d / Stopwatch.Frequency;
    }

    private sealed class CountingCatalog(IReadOnlyList<ProductIdentityCandidate> products) : IProductResolutionCatalog
    {
        private long _calls;
        public long Calls => Interlocked.Read(ref _calls);
        public Task<IReadOnlyList<ProductIdentityCandidate>> GetActiveProductsAsync(long businessUnitId,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _calls);
            return Task.FromResult(products);
        }
    }

    private sealed class CountingReferences(IReadOnlyList<ApprovedProductReference> references)
        : IApprovedProductReferenceSource
    {
        private long _calls;
        public long Calls => Interlocked.Read(ref _calls);
        public Task<IReadOnlyList<ApprovedProductReference>> GetApprovedReferencesAsync(long businessUnitId,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _calls);
            return Task.FromResult(references);
        }
    }

    private sealed class CommandCounter : DbCommandInterceptor
    {
        private long _commands;
        public long Commands => Interlocked.Read(ref _commands);
        public void Reset() => Interlocked.Exchange(ref _commands, 0);
        public override InterceptionResult<DbDataReader> ReaderExecuting(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result)
        { Interlocked.Increment(ref _commands); return result; }
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        { Interlocked.Increment(ref _commands); return ValueTask.FromResult(result); }
        public override InterceptionResult<int> NonQueryExecuting(DbCommand command,
            CommandEventData eventData, InterceptionResult<int> result)
        { Interlocked.Increment(ref _commands); return result; }
        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        { Interlocked.Increment(ref _commands); return ValueTask.FromResult(result); }
        public override InterceptionResult<object> ScalarExecuting(DbCommand command,
            CommandEventData eventData, InterceptionResult<object> result)
        { Interlocked.Increment(ref _commands); return result; }
        public override ValueTask<InterceptionResult<object>> ScalarExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<object> result,
            CancellationToken cancellationToken = default)
        { Interlocked.Increment(ref _commands); return ValueTask.FromResult(result); }
    }

    private sealed class CountingSalesReadPersistence : ISalesPersistence
    {
        private long _calls;
        public long QueryCalls => Interlocked.Read(ref _calls);
        public int SourceRows => Activities.Count + FollowUps.Count + Transitions.Count + Contributions.Count;
        private List<CommercialActivity> Activities { get; } = Enumerable.Range(1, 100).Select(index => new CommercialActivity
        {
            BusinessUnitId = Tenant, SalesRepUserId = 700 + index % 10,
            AggregateType = "Opportunity", AggregateId = index,
            ActivityType = index % 5 == 0 ? CommercialActivityType.Won : CommercialActivityType.QuoteSent,
            OccurredAtUtc = Now.AddDays(-(index % 20))
        }).ToList();
        private List<FollowUpTask> FollowUps { get; } = Enumerable.Range(1, 50).Select(index => new FollowUpTask
        {
            Id = index, BusinessUnitId = Tenant, AssignedToUserId = 700 + index % 10,
            AggregateType = "Quote", AggregateId = index, CreatedAtUtc = Now.AddDays(-(index % 20)),
            DueAtUtc = Now.AddDays(index % 3 - 1), Status = index % 2 == 0 ? FollowUpStatus.Completed : FollowUpStatus.Open
        }).ToList();
        private List<FollowUpTransitionEvent> Transitions { get; } = Enumerable.Range(1, 25).Select(index => new FollowUpTransitionEvent
        {
            BusinessUnitId = Tenant, FollowUpTaskId = index * 2, ToStatus = FollowUpStatus.Completed,
            OccurredAtUtc = Now.AddDays(-(index % 20))
        }).ToList();
        private List<SalesContribution> Contributions { get; } = Enumerable.Range(1, 40).Select(index => new SalesContribution
        {
            BusinessUnitId = Tenant, SalesRepUserId = 700 + index % 10,
            AggregateType = "Order", AggregateId = index, RevenueAmount = 1000 + index,
            ContributionPercent = 100, CurrencyCode = "USD", RecognizedAtUtc = Now.AddDays(-(index % 20))
        }).ToList();

        public Task<IReadOnlyList<CommercialActivity>> QueryActivitiesAsync(long businessUnitId, DateTime fromUtc, DateTime toUtc, long? userId, CancellationToken ct)
        { Interlocked.Increment(ref _calls); return Result(Activities, row => row.OccurredAtUtc, row => row.SalesRepUserId, fromUtc, toUtc, userId); }
        public Task<IReadOnlyList<FollowUpTask>> QueryFollowUpsAsync(long businessUnitId, DateTime fromUtc, DateTime toUtc, long? userId, CancellationToken ct)
        { Interlocked.Increment(ref _calls); return Result(FollowUps, row => row.CreatedAtUtc, row => row.AssignedToUserId, fromUtc, toUtc, userId); }
        public Task<IReadOnlyList<FollowUpTransitionEvent>> QueryFollowUpTransitionsAsync(long businessUnitId, DateTime fromUtc, DateTime toUtc, long? userId, CancellationToken ct)
        { Interlocked.Increment(ref _calls); return Task.FromResult<IReadOnlyList<FollowUpTransitionEvent>>(Transitions.Where(row => row.OccurredAtUtc >= fromUtc && row.OccurredAtUtc < toUtc).ToArray()); }
        public Task<IReadOnlyList<SalesContribution>> QueryContributionsAsync(long businessUnitId, DateTime fromUtc, DateTime toUtc, long? userId, CancellationToken ct)
        { Interlocked.Increment(ref _calls); return Result(Contributions, row => row.RecognizedAtUtc, row => row.SalesRepUserId, fromUtc, toUtc, userId); }
        private static Task<IReadOnlyList<T>> Result<T>(IEnumerable<T> source, Func<T, DateTime> at,
            Func<T, long> user, DateTime from, DateTime to, long? userId) =>
            Task.FromResult<IReadOnlyList<T>>(source.Where(row => at(row) >= from && at(row) < to && (!userId.HasValue || user(row) == userId)).ToArray());

        public Task<bool> UserExistsAsync(long businessUnitId, long userId, CancellationToken ct) => throw new NotSupportedException();
        public Task<bool> CustomerExistsAsync(long businessUnitId, long customerId, CancellationToken ct) => throw new NotSupportedException();
        public Task<bool> LeadAssignmentExistsAsync(long businessUnitId, long assignmentId, CancellationToken ct) => throw new NotSupportedException();
        public Task<bool> AggregateExistsAsync(long businessUnitId, string aggregateType, long aggregateId, CancellationToken ct) => throw new NotSupportedException();
        public Task<SalesRepProfile?> GetProfileAsync(long businessUnitId, long userId, CancellationToken ct) => throw new NotSupportedException();
        public Task<SalesRepProfile?> FindProfileMutationAsync(long businessUnitId, string idempotencyKey, CancellationToken ct) => throw new NotSupportedException();
        public Task<SalesRepProfile> SaveProfileAsync(SalesRepProfile profile, long expectedVersion, string idempotencyKey, CancellationToken ct) => throw new NotSupportedException();
        public Task<CommercialActivity?> FindActivityAsync(long businessUnitId, string idempotencyKey, CancellationToken ct) => throw new NotSupportedException();
        public Task<CommercialActivity> AppendActivityAsync(CommercialActivity activity, CancellationToken ct) => throw new NotSupportedException();
        public Task<FollowUpTask?> FindFollowUpByCreationKeyAsync(long businessUnitId, string idempotencyKey, CancellationToken ct) => throw new NotSupportedException();
        public Task<FollowUpTask> CreateFollowUpAsync(FollowUpTask task, CancellationToken ct) => throw new NotSupportedException();
        public Task<(FollowUpTask Task, FollowUpTransitionEvent? Replay)> GetFollowUpForTransitionAsync(long businessUnitId, long taskId, string idempotencyKey, CancellationToken ct) => throw new NotSupportedException();
        public Task<FollowUpTransitionEvent> TransitionFollowUpAsync(FollowUpTask task, FollowUpTransitionEvent transition, long expectedVersion, CancellationToken ct) => throw new NotSupportedException();
        public Task<SalesContribution?> FindContributionAsync(long businessUnitId, string idempotencyKey, CancellationToken ct) => throw new NotSupportedException();
        public Task<SalesContribution> AppendContributionAsync(SalesContribution contribution, CancellationToken ct) => throw new NotSupportedException();
    }
}
