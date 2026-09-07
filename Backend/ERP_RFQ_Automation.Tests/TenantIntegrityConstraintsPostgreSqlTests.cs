using ERP_RFQ_Automation.Platform.Models;
using ERP_RFQ_Automation.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// Certifies the tenant integrity constraints added by 20260907111347 and 20260907111522
/// against a real PostgreSQL, because none of them exist anywhere else.
///
/// <para>WHY THESE TESTS ARE WRITTEN AS RAW SQL. Every rule proved here is a backstop for the
/// case where the application is NOT the writer: a hand-written UPDATE, a restored backup, a
/// future controller that forgets the rule its neighbour remembers. Driving them through EF or
/// through a controller would prove the application's validation instead — which already
/// existed, and which is exactly what these constraints were added because we could not rely
/// on. So each test writes the offending row the way the thing we are defending against would
/// write it, and asserts the DATABASE refuses.</para>
///
/// <para>The headline is <see cref="Two_tenants_cannot_share_one_primary_business_unit"/>.
/// PrimaryBusinessUnitId is what row-level security isolates on and what the purge executor
/// resolves a tenant's data through; until 20260907111347 it was a bare nullable bigint with no
/// foreign key and no unique index, so two tenants pointing at one business unit — a complete
/// cross-tenant data merge — was a state the database would accept.</para>
/// </summary>
[Collection(PostgreSqlIntegrationCollection.Name)]
public sealed class TenantIntegrityConstraintsPostgreSqlTests
{
    private readonly PostgreSqlTestDatabase _database;

    public TenantIntegrityConstraintsPostgreSqlTests(PostgreSqlTestDatabase database)
        => _database = database;

    // ============================================================ the isolation key

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task Two_tenants_cannot_share_one_primary_business_unit()
    {
        await using var connection = await _database.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();

        var unit = await CreateBusinessUnitAsync(connection, transaction);
        await InsertTenantAsync(connection, transaction, Unique("merge-a"), primaryBusinessUnitId: unit);

        var violation = await Assert.ThrowsAsync<PostgresException>(
            () => InsertTenantAsync(connection, transaction, Unique("merge-b"), primaryBusinessUnitId: unit));

        Assert.Equal(PostgresErrorCodes.UniqueViolation, violation.SqlState);
        Assert.Contains("IX_Tenants_PrimaryBusinessUnitId", violation.Message, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task A_tenant_cannot_point_at_a_business_unit_that_does_not_exist()
    {
        await using var connection = await _database.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();

        var violation = await Assert.ThrowsAsync<PostgresException>(
            () => InsertTenantAsync(connection, transaction, Unique("dangling"),
                primaryBusinessUnitId: 999_999_999));

        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, violation.SqlState);
    }

    /// <summary>
    /// The reason the foreign key is SET NULL and not RESTRICT, pinned so nobody "tightens" it back.
    ///
    /// <para>platform."Tenants" is classified <c>OperatorRecord</c> in PlatformTenantDataMap and
    /// therefore SURVIVES a purge as a tombstone, while the tenant's public."BusinessUnits" row is
    /// destroyed by it. A restricting foreign key would refuse that delete and strand the purge
    /// half-completed, with the customer's data partly gone and the operation unable to finish.</para>
    /// </summary>
    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task Destroying_the_business_unit_nulls_the_pointer_and_leaves_the_tombstone()
    {
        await using var connection = await _database.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();

        var unit = await CreateBusinessUnitAsync(connection, transaction);
        var slug = Unique("purged");
        await InsertTenantAsync(connection, transaction, slug, primaryBusinessUnitId: unit);

        // The purge executor deletes tenant-plane rows deepest-first. Emulate that for the
        // dependents this fixture creates, so the assertion measures OUR constraint alone.
        await ExecuteAsync(connection, transaction,
            """DELETE FROM public."AiProcessingPolicies" WHERE "BusinessUnitId" = @unit;""",
            ("unit", unit));
        await ExecuteAsync(connection, transaction,
            """DELETE FROM public."BusinessUnits" WHERE "ID" = @unit;""", ("unit", unit));

        await using var read = new NpgsqlCommand(
            """SELECT "PrimaryBusinessUnitId" FROM platform."Tenants" WHERE "Slug" = @slug;""",
            connection, transaction);
        read.Parameters.AddWithValue("slug", slug);
        var pointer = await read.ExecuteScalarAsync();

        Assert.Equal(DBNull.Value, pointer);
    }

    // ============================================================ commercial invariants

    /// <summary>
    /// The sequence this closes was reachable through two legitimate API calls:
    /// set billingMode Internal, clear the invoice recipient (permitted for Internal), set
    /// billingMode Billable again — which never re-examines the recipient. The result is a tenant
    /// that invoicing refuses to bill AND that cannot be offboarded, because offboarding
    /// readiness requires a finalized invoice.
    /// </summary>
    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task A_billable_tenant_cannot_exist_without_an_invoice_recipient()
    {
        await using var connection = await _database.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();

        var violation = await Assert.ThrowsAsync<PostgresException>(
            () => InsertTenantAsync(connection, transaction, Unique("unbillable"),
                billingMode: "Billable", billingContactEmail: null));

        Assert.Equal(PostgresErrorCodes.CheckViolation, violation.SqlState);
        Assert.Contains("CK_Tenants_BillableIsInvoiceable", violation.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The counterpart, and the one a first draft of this constraint got wrong: a Billable tenant
    /// with NO PLAN is a real and supported state. UnplannedTenantAllowance gives a newly
    /// provisioned tenant full capacity for fourteen days and a reduced floor afterwards,
    /// expressly so setup is never interrupted. Requiring a plan would have broken provisioning
    /// for every customer whose plan is chosen after the workspace exists.
    /// </summary>
    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task A_billable_tenant_with_no_plan_remains_representable()
    {
        await using var connection = await _database.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();

        var slug = Unique("unplanned");
        await InsertTenantAsync(connection, transaction, slug,
            billingMode: "Billable", billingContactEmail: "ap@unplanned.test");

        Assert.Equal(1, await CountAsync(connection, transaction, slug));
    }

    /// <summary>
    /// The constraint must not break personal-data erasure, which is why it is scoped to the live
    /// statuses rather than applied to every Billable row.
    ///
    /// <para><c>ErasePersonalDataAsync</c> NULLS <c>BillingContactEmail</c> on purpose — a
    /// customer's accounts-payable address is the customer's personal data — and
    /// <c>TenantLifecycleGraph.ErasureAllowedFrom</c> permits erasure only from Suspended or
    /// Archived. An unscoped constraint would have made a compliance operation fail on any tenant
    /// that had ever been Billable: a worse defect than the one being closed, and one that would
    /// have surfaced as a 500 in the middle of a deletion request.</para>
    /// </summary>
    [Theory]
    [Trait("Category", "PostgreSQL")]
    [InlineData("Suspended")]
    [InlineData("Archived")]
    public async Task Erasure_may_clear_the_invoice_recipient_once_the_tenant_is_off_the_product(string status)
    {
        await using var connection = await _database.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();

        var slug = Unique("erased");
        await InsertTenantAsync(connection, transaction, slug,
            billingMode: "Billable", billingContactEmail: "ap@erased.test");

        await ExecuteAsync(connection, transaction,
            """UPDATE platform."Tenants" SET "Status" = @status WHERE "Slug" = @slug;""",
            ("status", status), ("slug", slug));

        // The erasure write itself: the recipient goes, the row stays.
        await ExecuteAsync(connection, transaction,
            """
            UPDATE platform."Tenants" SET "BillingContactEmail" = NULL, "ContactEmail" = NULL
            WHERE "Slug" = @slug;
            """, ("slug", slug));

        Assert.Equal(1, await CountAsync(connection, transaction, slug));
    }

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task A_trial_cannot_exist_without_an_end_date()
    {
        await using var connection = await _database.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();

        var violation = await Assert.ThrowsAsync<PostgresException>(
            () => InsertTenantAsync(connection, transaction, Unique("forever"),
                billingMode: "Trial",
                billingModeReason: "evaluation agreed with the customer"));

        Assert.Equal(PostgresErrorCodes.CheckViolation, violation.SqlState);
        Assert.Contains("CK_Tenants_TrialHasEnd", violation.Message, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task Free_service_cannot_exist_without_a_written_justification()
    {
        await using var connection = await _database.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();

        var violation = await Assert.ThrowsAsync<PostgresException>(
            () => InsertTenantAsync(connection, transaction, Unique("freebie"),
                billingMode: "Internal", billingModeReason: "because"));

        Assert.Equal(PostgresErrorCodes.CheckViolation, violation.SqlState);
        Assert.Contains("CK_Tenants_NonBillableHasReason", violation.Message, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task A_relaxed_deployment_profile_cannot_exist_without_a_recorded_approver()
    {
        await using var connection = await _database.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();

        var violation = await Assert.ThrowsAsync<PostgresException>(
            () => InsertTenantAsync(connection, transaction, Unique("loose"),
                billingMode: "Internal",
                billingModeReason: "internal workspace for the delivery team",
                deploymentProfile: "Demo",
                deploymentProfileReason: "demo estate for the sales team"));

        Assert.Equal(PostgresErrorCodes.CheckViolation, violation.SqlState);
        Assert.Contains("CK_Tenants_NonProdApproved", violation.Message, StringComparison.Ordinal);
    }

    // ============================================================ concurrency token

    /// <summary>
    /// The tenant row carried no concurrency token at all while thirty-six others in the schema
    /// did, so every tenant write was read-modify-write and two operators silently overwrote each
    /// other. The console now reads this as an ETag and echoes it back as If-Match.
    /// </summary>
    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task Every_tenant_row_starts_at_version_one()
    {
        await using var connection = await _database.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();

        var slug = Unique("versioned");
        await InsertTenantAsync(connection, transaction, slug,
            billingMode: "Internal",
            billingModeReason: "internal workspace for the delivery team");

        await using var read = new NpgsqlCommand(
            """SELECT "Version" FROM platform."Tenants" WHERE "Slug" = @slug;""", connection, transaction);
        read.Parameters.AddWithValue("slug", slug);

        Assert.Equal(1L, (long)(await read.ExecuteScalarAsync())!);
    }

    /// <summary>
    /// The token has to be INCREMENTED to be worth anything. Declaring IsConcurrencyToken on a
    /// plain long makes EF put it in the WHERE clause; it does not make anything advance it, and
    /// a token that never advances matches every time. That is not hypothetical — it is what
    /// happened to the email-assembly token, which is why TenantConcurrencyStamp exists.
    /// </summary>
    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task A_stale_writer_loses_instead_of_silently_overwriting()
    {
        var slug = Unique("concurrent");
        await using (var seed = _database.ContextFor(null))
        {
            seed.Set<Tenant>().Add(new Tenant
            {
                Name = "Concurrent", Slug = slug, Status = TenantStatus.Active,
                BillingContactEmail = "ap@concurrent.test",
                CreatedBy = "tests", CreatedOn = DateTime.UtcNow
            });
            await seed.SaveChangesAsync();
        }

        // Two operators read the same row — the two-tabs case the console makes easy.
        await using var first = _database.ContextFor(null);
        await using var second = _database.ContextFor(null);
        var a = await first.Set<Tenant>().IgnoreQueryFilters().SingleAsync(t => t.Slug == slug);
        var b = await second.Set<Tenant>().IgnoreQueryFilters().SingleAsync(t => t.Slug == slug);
        Assert.Equal(a.Version, b.Version);

        a.LegalName = "Written by the first operator";
        await first.SaveChangesAsync();

        b.City = "Written by the second operator";
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => second.SaveChangesAsync());

        await using var verify = _database.ContextFor(null);
        var landed = await verify.Set<Tenant>().IgnoreQueryFilters().AsNoTracking()
            .SingleAsync(t => t.Slug == slug);
        Assert.Equal("Written by the first operator", landed.LegalName);
        Assert.Null(landed.City);                 // the stale write did not land
        Assert.Equal(a.Version, landed.Version);  // and the token advanced
    }

    // ============================================================ helpers

    /// <summary>A slug that fits the column (varchar 64) and cannot collide across runs.</summary>
    private static string Unique(string prefix, int max = 40)
    {
        var candidate = $"{prefix}-{Guid.NewGuid():N}";
        return candidate.Length <= max ? candidate : candidate[..max];
    }

    private static async Task<long> CreateBusinessUnitAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction)
    {
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO public."BusinessUnits"
                ("BusinessUnitCode", "BusinessUnitName", "IsActive", "CreatedBy", "CreatedOn")
            VALUES (@code, 'Integrity fixture', true, 'tests', now())
            RETURNING "ID";
            """, connection, transaction);
        command.Parameters.AddWithValue("code", Unique("INT", 20));
        return (long)(await command.ExecuteScalarAsync())!;
    }

    private static async Task InsertTenantAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, string slug,
        long? primaryBusinessUnitId = null,
        string billingMode = "Internal",
        string? billingModeReason = "internal workspace for the delivery team",
        string? billingContactEmail = "ap@fixture.test",
        string deploymentProfile = "Production",
        string? deploymentProfileReason = null)
    {
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO platform."Tenants"
                ("Name", "Slug", "Status", "BillingMode", "BillingModeReason", "BillingContactEmail",
                 "DeploymentProfile", "DeploymentProfileReason", "PrimaryBusinessUnitId",
                 "CreatedOn", "Entitlements", "Version")
            VALUES (@slug, @slug, 'Provisioning', @mode, @reason, @email,
                    @profile, @profileReason, @unit, now(), '{}', 1);
            """, connection, transaction);
        command.Parameters.AddWithValue("slug", slug);
        command.Parameters.AddWithValue("mode", billingMode);
        command.Parameters.AddWithValue("reason", (object?)billingModeReason ?? DBNull.Value);
        command.Parameters.AddWithValue("email", (object?)billingContactEmail ?? DBNull.Value);
        command.Parameters.AddWithValue("profile", deploymentProfile);
        command.Parameters.AddWithValue("profileReason", (object?)deploymentProfileReason ?? DBNull.Value);
        command.Parameters.AddWithValue("unit", (object?)primaryBusinessUnitId ?? DBNull.Value);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task ExecuteAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, string sql,
        params (string Name, object Value)[] parameters)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<int> CountAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, string slug)
    {
        await using var command = new NpgsqlCommand(
            """SELECT count(*)::int FROM platform."Tenants" WHERE "Slug" = @slug;""",
            connection, transaction);
        command.Parameters.AddWithValue("slug", slug);
        return (int)(await command.ExecuteScalarAsync())!;
    }
}
