using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.MultiTenancy;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.Tests.Support;

/// <summary>
/// A relational, in-memory SQLite database created against the REAL scaffolded
/// <see cref="ErpRfqAutomationContext"/> model (all ~40 entities, the platform schema,
/// the computed column, the async-extraction tables). SQLite-in-memory is used instead
/// of EF Core InMemory because it enforces foreign keys, unique indexes and honours the
/// relational translation of the global query filters — the invariant under test.
///
/// The connection is kept open for the lifetime of the instance; every context created
/// from it shares the same in-memory database, so multiple context instances (a common
/// "seed with one context, assert with another" pattern) see the same data.
/// </summary>
public sealed class TestDb : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<ErpRfqAutomationContext> _options;

    public TestDb()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<ErpRfqAutomationContext>()
            .UseSqlite(_connection)
            .EnableSensitiveDataLogging()
            .Options;

        // Build the full relational schema once.
        using var ctx = ContextFor(null);
        ctx.Database.EnsureCreated();
    }

    /// <summary>A context scoped to <paramref name="businessUnitId"/>; pass null for an
    /// unfiltered "background worker / anonymous" context (query filters become no-ops).</summary>
    public ErpRfqAutomationContext ContextFor(long? businessUnitId)
        => new(_options, new StubTenant(businessUnitId));

    public void Dispose() => _connection.Dispose();
}

/// <summary>Deterministic <see cref="ITenantContext"/> for tests. A null id models the
/// no-tenant path (login / anonymous request / background worker sweep).</summary>
public sealed class StubTenant : ITenantContext
{
    public StubTenant(long? businessUnitId) => BusinessUnitId = businessUnitId;
    public long? BusinessUnitId { get; }
}
