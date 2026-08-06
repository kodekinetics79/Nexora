using System.Security.Claims;
using ERP_RFQ_Automation.Authorization;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Tests.Support;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// Creating a user and granting a permission must actually commit when the DbContext is
/// configured the way production configures it.
///
/// <para><b>The outage this pins.</b> <c>UserController.Create</c>,
/// <c>RolerPermissionController.Create</c> and <c>BulkApply</c> opened an explicit transaction so
/// the mutation and its IAM audit row would commit together. Production registers the context
/// with <c>EnableRetryOnFailure</c>, and EF refuses to execute inside a user-initiated transaction
/// under a retrying strategy. It throws at <b>SaveChanges</b>, not at <c>BeginTransaction</c> — so
/// the try/catch that guarded the transaction open never saw it, and all three actions returned a
/// 500 <b>one hundred percent of the time</b>. A tenant could neither add a user nor grant a
/// permission. No test caught it because the SQLite lane has no retrying strategy, so the whole
/// class of failure was invisible there. Hence PostgreSQL, and hence
/// <c>EnableRetryOnFailure</c> spelled out explicitly below rather than inherited from a helper:
/// the strategy IS the thing under test.</para>
///
/// <para><b>Retry discipline these tests assume.</b> The delegate passed to
/// <see cref="IIamAuditWriter.ExecuteAtomicAsync"/> may run more than once — a rolled-back attempt
/// leaves every entity it touched still tracked, carrying state from that attempt. Entities are
/// therefore constructed INSIDE the delegate, so each attempt starts from an untracked instance,
/// while non-transactional work (password hashing, the profile-image write) stays outside so it
/// happens exactly once. See the remarks on <c>ExecuteAtomicAsync</c>.</para>
/// </summary>
[Collection(PostgreSqlIntegrationCollection.Name)]
public sealed class IamAtomicWritePostgreSqlTests(PostgreSqlTestDatabase database)
{
    private const long Tenant = 60_981;

    /// <summary>Mirrors the production registration: Npgsql + a retrying execution strategy.</summary>
    private ErpRfqAutomationContext ProductionShapedContext()
    {
        var options = new DbContextOptionsBuilder<ErpRfqAutomationContext>()
            .UseNpgsql(database.ConnectionString, npgsql => npgsql.EnableRetryOnFailure())
            .Options;
        return new ErpRfqAutomationContext(options, new StubTenant(Tenant));
    }

    private async Task EnsureTenantAsync()
    {
        await using var seed = database.ContextFor(null);
        Seed.EnsureBusinessUnit(seed, Tenant);
        await seed.SaveChangesAsync();
    }

    /// <summary>
    /// The audit writer refuses to attribute an event without a tenant claim, so the caller has to
    /// be a real principal. Passing null here silently turned the rollback test into a test of that
    /// guard instead of a test of rollback.
    /// </summary>
    private static ClaimsPrincipal Caller() => new(new ClaimsIdentity(
        [new Claim("businessUnitId", Tenant.ToString()), new Claim(ClaimTypes.NameIdentifier, "9001")],
        "test"));

    private static User NewUser(string email) => new()
    {
        FirstName = "Atomic", LastName = "Write", Email = email,
        PasswordHash = "not-a-real-hash", ImageUrl = string.Empty,
        Buid = Tenant, IsActive = true,
        CreatedBy = "tests", CreatedOn = DateTime.UtcNow
    };

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task A_user_and_its_audit_row_commit_together_under_the_production_retry_strategy()
    {
        await EnsureTenantAsync();
        const string email = "atomic-commit@tests.local";

        await using var context = ProductionShapedContext();
        var audit = new IamAuditWriter(context);

        // Before the fix this threw "The configured execution strategy
        // 'NpgsqlRetryingExecutionStrategy' does not support user-initiated transactions".
        await audit.ExecuteAtomicAsync(async () =>
        {
            var user = NewUser(email);
            context.Users.Add(user);
            await context.SaveChangesAsync();
            await audit.WriteAsync(Caller(), new IamAuditEntry(
                IamAuditActions.UserCreated, IamAuditTargets.User, user.Id, user.Email));
        });

        await using var verify = database.ContextFor(null);
        var persisted = await verify.Users.AsNoTracking().SingleAsync(u => u.Email == email);
        Assert.True(persisted.Id > 0);

        // The audit row is the point of the transaction — "created but unaudited" must not be a
        // reachable state, so it is asserted alongside the user rather than assumed.
        Assert.True(await verify.IamAuditEvents.AsNoTracking()
            .AnyAsync(e => e.TargetId == persisted.Id && e.Action == IamAuditActions.UserCreated));
    }

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task A_failure_inside_the_delegate_rolls_back_the_user_and_the_audit_row_together()
    {
        await EnsureTenantAsync();
        const string email = "atomic-rollback@tests.local";

        await using (var context = ProductionShapedContext())
        {
            var audit = new IamAuditWriter(context);
            var failure = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                audit.ExecuteAtomicAsync(async () =>
                {
                    var user = NewUser(email);
                    context.Users.Add(user);
                    await context.SaveChangesAsync();
                    await audit.WriteAsync(Caller(), new IamAuditEntry(
                        IamAuditActions.UserCreated, IamAuditTargets.User, user.Id, user.Email));
                    throw new InvalidOperationException("seat limit exceeded after write");
                }));
            // Pinned to the seeded failure. Asserting the bare exception type let this test pass
            // for the wrong reason once already: the writer's missing-tenant-claim guard threw
            // first, so nothing about rollback was actually exercised.
            Assert.Equal("seat limit exceeded after write", failure.Message);
        }

        // Both rows are gone. A half-applied create — user without audit, or audit without user —
        // is what the explicit transaction exists to prevent, and is the reason the retrying
        // strategy has to own the transaction rather than the fix being "drop the transaction".
        await using var verify = database.ContextFor(null);
        Assert.False(await verify.Users.AsNoTracking().AnyAsync(u => u.Email == email));
        Assert.False(await verify.IamAuditEvents.AsNoTracking()
            .AnyAsync(e => e.TargetLabel == email));
    }

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task An_ambient_transaction_is_joined_rather_than_nested()
    {
        // BulkApply and the seeding paths can already be inside a transaction. Opening a second
        // one there would either throw or silently commit the inner work early, so the writer
        // joins the ambient transaction instead — and must NOT commit it on the caller's behalf.
        await EnsureTenantAsync();
        const string email = "atomic-ambient@tests.local";

        await using var context = ProductionShapedContext();
        var audit = new IamAuditWriter(context);
        var strategy = context.Database.CreateExecutionStrategy();

        await strategy.ExecuteAsync(async () =>
        {
            await using var outer = await context.Database.BeginTransactionAsync();
            await audit.ExecuteAtomicAsync(async () =>
            {
                context.Users.Add(NewUser(email));
                await context.SaveChangesAsync();
            });

            // Still open and still the caller's to decide. If ExecuteAtomicAsync had committed
            // it, the rollback below could not undo the write.
            Assert.NotNull(context.Database.CurrentTransaction);
            await outer.RollbackAsync();
        });

        await using var verify = database.ContextFor(null);
        Assert.False(await verify.Users.AsNoTracking().AnyAsync(u => u.Email == email));
    }
}
