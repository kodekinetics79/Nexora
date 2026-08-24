using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Platform.Testing;
using ERP_RFQ_Automation.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// The testing-only tenant data reset. Its whole value is that the starting state afterwards is
/// KNOWN, so the tests that matter are the ones proving it neither under-deletes (leaving state
/// that makes the next run behave differently) nor over-deletes (leaving the tenant unusable).
/// </summary>
public sealed class TenantDataResetGuardTests
{
    private sealed class Env(string name) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;
        public string ApplicationName { get; set; } = "tests";
        public string ContentRootPath { get; set; } = ".";
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = null!;
    }

    private static TenantDataReset Build(string environment, bool enabled)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            [TenantDataReset.EnabledConfigurationPath] = enabled ? "true" : "false",
            ["ConnectionStrings:MigrationConnection"] = "Host=127.0.0.1;Port=1;Database=x;Username=x;Password=x",
        }).Build();

        return new TenantDataReset(null!, configuration, new Env(environment),
            NullLogger<TenantDataReset>.Instance);
    }

    [Fact]
    public async Task Production_is_refused_even_when_the_configuration_flag_is_on()
    {
        // The environment check comes FIRST on purpose. A mistyped environment variable must not
        // be sufficient to erase a customer's data, so there is deliberately no configuration
        // value that unlocks this in Production.
        var reset = Build("Production", enabled: true);

        Assert.False(reset.IsEnabled);
        var refusal = await Assert.ThrowsAsync<TenantResetRefusedException>(
            () => reset.PreviewAsync(1, CancellationToken.None));
        Assert.Contains("Production", refusal.Message);
    }

    [Fact]
    public async Task It_is_off_unless_explicitly_switched_on()
    {
        var reset = Build("Development", enabled: false);

        Assert.False(reset.IsEnabled);
        var refusal = await Assert.ThrowsAsync<TenantResetRefusedException>(
            () => reset.PreviewAsync(1, CancellationToken.None));
        Assert.Contains(TenantDataReset.EnabledConfigurationPath, refusal.Message);
    }

    [Fact]
    public void Everything_a_tenant_needs_to_stay_usable_is_preserved()
    {
        // Deleting any of these leaves the tenant unable to log in, unable to transition a lead,
        // or needing its mailbox credentials re-entered before every test run — which defeats the
        // purpose of a repeatable reset.
        Assert.Contains(nameof(User), TenantDataReset.PreservedEntities);
        Assert.Contains(nameof(RolePermission), TenantDataReset.PreservedEntities);
        Assert.Contains(nameof(SetupMaster), TenantDataReset.PreservedEntities);
        Assert.Contains(nameof(BusinessUnit), TenantDataReset.PreservedEntities);
        Assert.Contains(nameof(EmailConfiguration), TenantDataReset.PreservedEntities);
    }

    [Fact]
    public void The_things_a_rerun_depends_on_being_gone_are_NOT_preserved()
    {
        // The inverse assertion, and the one that actually protects the feature's purpose.
        // Preserving any of these would make the reset appear to succeed and then silently
        // ingest nothing: EmailIngests dedupes per message, and LeadIngestionOccurrences carries
        // the idempotency key that makes ReconcileAsync converge on the existing lead.
        Assert.DoesNotContain(nameof(Lead), TenantDataReset.PreservedEntities);
        Assert.DoesNotContain(nameof(Rfq), TenantDataReset.PreservedEntities);
        Assert.DoesNotContain(nameof(Quote), TenantDataReset.PreservedEntities);
        Assert.DoesNotContain(nameof(EmailIngest), TenantDataReset.PreservedEntities);
    }
}

/// <summary>
/// The reset against a real database, where the append-only triggers and foreign keys are real.
/// </summary>
[Collection(PostgreSqlIntegrationCollection.Name)]
public sealed class TenantDataResetPostgreSqlTests(PostgreSqlTestDatabase database)
{
    // Each test owns a distinct tenant. They share one database, and a reset deliberately
    // PRESERVES the BusinessUnit row — so a seed guarded on "does this tenant exist" would
    // short-circuit after another test had already reset it, silently leaving no lead to delete.
    // Isolation by tenant id is the property the production code guarantees anyway.
    private const string TenantName = "Reset Rehearsal Unit";

    private sealed class Env : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Development";
        public string ApplicationName { get; set; } = "tests";
        public string ContentRootPath { get; set; } = ".";
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = null!;
    }

    private TenantDataReset Build(ErpRfqAutomationContext context)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            [TenantDataReset.EnabledConfigurationPath] = "true",
            ["ConnectionStrings:MigrationConnection"] = database.ConnectionString,
        }).Build();

        return new TenantDataReset(context, configuration, new Env(), NullLogger<TenantDataReset>.Instance);
    }

    private async Task SeedAsync(long tenant)
    {
        await using var db = database.ContextFor(null);
        if (await db.Leads.AnyAsync(l => l.BusinessUnitId == tenant)) return;

        var unit = await db.BusinessUnits.FirstOrDefaultAsync(b => b.Id == tenant)
                   ?? Seed.BusinessUnit(db, tenant);
        unit.BusinessUnitName = TenantName;

        db.Users.Add(new User
        {
            FirstName = "Reset", LastName = "Operator", Email = $"reset-{tenant}@tests.local",
            PasswordHash = "x", ImageUrl = string.Empty, Buid = tenant, IsActive = true,
            CreatedBy = "tests", CreatedOn = DateTime.UtcNow,
        });
        db.EmailConfigurations.Add(new EmailConfiguration
        {
            BusinessUnitId = tenant, ConfigurationName = "Reset inbox", EmailAddress = $"in-{tenant}@tests.local",
            Protocol = "IMAP", Host = "imap.example.com", Port = 993, Username = $"in-{tenant}@tests.local",
            Password = "secret", UseSsl = true, PollingInterval = 5, IsActive = true,
            CreatedOn = DateTime.UtcNow,
            // The state that makes a re-run ingest nothing unless the reset clears it.
            LastSuccessfulPollOn = DateTime.UtcNow, LastPollAttemptOn = DateTime.UtcNow,
            ConsecutivePollFailures = 3, LastPollError = "stale failure",
        });

        var lead = new Lead
        {
            Rfqno = $"RESET-{tenant}", BuyersName = "Buyer", RecDate = DateTime.UtcNow,
            BidClosingDate = DateTime.UtcNow.AddDays(7), LeadSource = "tests",
            CreatedBy = "tests", CreatedDate = DateTime.UtcNow, BusinessUnitId = tenant, NoOfLineItems = 1,
        };
        lead.LeadItems.Add(new LeadItem
        {
            LineItemNo = "1", ProductShortDescription = "Widget", Quantity = 3,
            UnitOfMeasure = "EA", Currency = "SAR",
        });
        db.Leads.Add(lead);

        await db.SaveChangesAsync();
    }

    /// <summary>Mailboxes still carrying poll history. Must be zero after a reset, or the poller
    /// keeps its lookback window and reports "no new mail" forever.</summary>
    private async Task<int> StaleMailboxPollStateAsync(long tenant)
    {
        await using var connection = new NpgsqlConnection(database.ConnectionString);
        await connection.OpenAsync();

        await using var resolve = new NpgsqlCommand(
            """
            SELECT column_name FROM information_schema.columns
            WHERE table_schema = 'public' AND table_name = 'Email_Configurations'
              AND lower(column_name) = 'businessunitid' LIMIT 1;
            """, connection);
        var column = (string)(await resolve.ExecuteScalarAsync())!;

        await using var count = new NpgsqlCommand(
            $"""
             SELECT count(*)::int FROM public."Email_Configurations"
             WHERE "{column}" = @tenant
               AND ("LastSuccessfulPollOn" IS NOT NULL OR "LastPollAttemptOn" IS NOT NULL
                    OR "LastPollError" IS NOT NULL OR "ConsecutivePollFailures" <> 0);
             """, connection);
        count.Parameters.AddWithValue("tenant", tenant);
        return (int)(await count.ExecuteScalarAsync())!;
    }

    private async Task<T> ScalarAsync<T>(string sql)
    {
        await using var db = database.ContextFor(null);
        return (await db.Database.SqlQueryRaw<T>(sql).ToListAsync()).Single();
    }

    /// <summary>
    /// Counts a tenant's rows in a table, resolving the tenant column from the live catalogue.
    ///
    /// <para>Hardcoding it is what these assertions did first, and they failed: legacy tables call
    /// it "BusinessUnitID" while newer ones use "BusinessUnitId", and Users uses "BUID". Spelling
    /// them out here would have re-created in the test the exact naming assumption that the
    /// production code deliberately refuses to make.</para>
    /// </summary>
    private async Task<int> TenantRowsAsync(string table, long tenant)
    {
        await using var connection = new NpgsqlConnection(database.ConnectionString);
        await connection.OpenAsync();

        await using var resolve = new NpgsqlCommand(
            """
            SELECT column_name FROM information_schema.columns
            WHERE table_schema = 'public' AND table_name = @table
              AND lower(column_name) IN ('businessunitid', 'buid', 'id')
            ORDER BY CASE lower(column_name) WHEN 'businessunitid' THEN 0 WHEN 'buid' THEN 1 ELSE 2 END
            LIMIT 1;
            """, connection);
        resolve.Parameters.AddWithValue("table", table);
        var column = (string?)await resolve.ExecuteScalarAsync()
            ?? throw new InvalidOperationException($"No tenant column found on {table}.");

        await using var count = new NpgsqlCommand(
            $"""SELECT count(*)::int FROM public."{table}" WHERE "{column}" = @tenant;""", connection);
        count.Parameters.AddWithValue("tenant", tenant);
        return (int)(await count.ExecuteScalarAsync())!;
    }

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task The_preview_names_what_would_go_and_what_would_stay()
    {
        const long Tenant = 77_311;
        await SeedAsync(Tenant);
        await using var db = database.ContextFor(null);

        var preview = await Build(db).PreviewAsync(Tenant, CancellationToken.None);

        Assert.Equal(TenantName, preview.BusinessUnitName);
        Assert.True(preview.TotalRows > 0);
        Assert.Contains(preview.Tables, t => t.Table.Contains("Leads", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(nameof(User), preview.Preserved);

        // Preview must not change anything — it is what an operator runs to decide.
        Assert.Equal(1, await TenantRowsAsync("Leads", Tenant));
    }

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task A_confirmation_that_does_not_match_the_business_unit_name_is_refused()
    {
        const long Tenant = 77_312;
        await SeedAsync(Tenant);
        await using var db = database.ContextFor(null);

        var refusal = await Assert.ThrowsAsync<TenantResetRefusedException>(
            () => Build(db).ExecuteAsync(Tenant, "yes", CancellationToken.None));

        Assert.Contains(TenantName, refusal.Message);
        Assert.Equal(1, await TenantRowsAsync("Leads", Tenant));
    }

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task Transactional_data_goes_configuration_stays_and_the_poller_is_rewound()
    {
        const long Tenant = 77_313;
        await SeedAsync(Tenant);
        await using var db = database.ContextFor(null);

        var outcome = await Build(db).ExecuteAsync(Tenant, TenantName, CancellationToken.None);

        Assert.True(outcome.RowsDeleted > 0);

        // Gone: the lead and its lines. Deleting the parent alone would leave orphaned lines that
        // a re-ingest would collide with.
        Assert.Equal(0, await TenantRowsAsync("Leads", Tenant));

        // Kept: everything the tenant needs to still be usable.
        Assert.Equal(1, await TenantRowsAsync("Users", Tenant));
        Assert.Equal(1, await TenantRowsAsync("Email_Configurations", Tenant));
        Assert.Equal(1, await TenantRowsAsync("BusinessUnits", Tenant));

        // Rewound: without this the poller reports "no new mail" forever and the reset silently
        // achieves nothing observable.
        Assert.True(outcome.MailboxPollStateCleared);
        Assert.Equal(0, await StaleMailboxPollStateAsync(Tenant));
    }

    /// <summary>
    /// The reset clears the snake_case evidence and extraction tables too.
    ///
    /// <para>The same defect as the tenant purge's, in the machinery this file deliberately
    /// shares with it: targets were discovered with
    /// <c>lower(column_name) IN ('businessunitid', 'buid')</c>, and eleven tables spell it
    /// <c>business_unit_id</c>. A pilot asked for a clean slate, was told it had one, and
    /// re-ingested on top of the previous run's source documents and extraction runs — which is
    /// worse than an untouched database, because the leftovers are invisible until something
    /// collides with them.</para>
    /// </summary>
    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task The_reset_clears_snake_case_business_unit_tables()
    {
        const long Tenant = 77_318;
        await SeedAsync(Tenant);
        await SeedSnakeCaseEvidenceAsync(Tenant);

        Assert.Equal(1, await SnakeCaseRowsAsync("document_corpora", Tenant));
        Assert.Equal(1, await SnakeCaseRowsAsync("source_documents", Tenant));

        await using var db = database.ContextFor(null);
        var outcome = await Build(db).ExecuteAsync(Tenant, TenantName, CancellationToken.None);

        Assert.Equal(0, await SnakeCaseRowsAsync("document_corpora", Tenant));
        Assert.Equal(0, await SnakeCaseRowsAsync("source_documents", Tenant));
        Assert.Contains(outcome.Deleted, d => d.Table.Contains("source_documents"));
    }

    /// <summary>One corpus and one source document, carrying <c>business_unit_id</c>.</summary>
    private async Task SeedSnakeCaseEvidenceAsync(long tenant)
    {
        await using var connection = new NpgsqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand($"""
            SET session_replication_role = replica;

            DELETE FROM public.source_documents WHERE business_unit_id = {tenant};
            DELETE FROM public.document_corpora  WHERE business_unit_id = {tenant};

            INSERT INTO public.document_corpora
                (id, business_unit_id, batch_id, source_type, status, created_on, updated_on)
            VALUES ({tenant}, {tenant}, gen_random_uuid(), 'Upload', 'Complete', now(), now());

            INSERT INTO public.source_documents
                (id, business_unit_id, corpus_id, content_hash, original_file_name,
                 detected_mime_type, object_bucket, object_key, object_version, byte_size,
                 page_count, security_status, processing_status, created_on, updated_on)
            VALUES ({tenant}, {tenant}, {tenant}, repeat('a', 64), 'reset.pdf', 'application/pdf',
                    'NexoraBucket', 'Evidence/tenants/{tenant}/cleared/sha256/aa/a.pdf',
                    'v1', 10, 1, 'Cleared', 'Complete', now(), now());

            SET session_replication_role = origin;
            """, connection);
        await command.ExecuteNonQueryAsync();
    }

    /// <summary>Counted directly rather than through <c>TenantRowsAsync</c>, whose catalogue lookup
    /// deliberately mirrors the OLD spelling rule and so cannot see these tables either.</summary>
    private async Task<int> SnakeCaseRowsAsync(string table, long tenant)
    {
        await using var connection = new NpgsqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        await using var count = new NpgsqlCommand(
            $"SELECT count(*)::int FROM public.{table} WHERE business_unit_id = @tenant;", connection);
        count.Parameters.AddWithValue("tenant", tenant);
        return (int)(await count.ExecuteScalarAsync())!;
    }

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task The_append_only_guards_are_restored_after_the_reset_commits()
    {
        // session_replication_role is set LOCAL, so it lapses with the transaction. If it ever
        // leaked past the reset, every immutability guarantee in the system would be silently off
        // for the rest of that connection's life — a far worse outcome than a failed reset.
        const long Tenant = 77_314;
        await SeedAsync(Tenant);
        await using (var db = database.ContextFor(null))
            await Build(db).ExecuteAsync(Tenant, TenantName, CancellationToken.None);

        Assert.Equal("origin", await ScalarAsync<string>(
            """SELECT current_setting('session_replication_role') AS "Value" """));
    }

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task Resetting_a_tenant_leaves_every_other_tenant_untouched()
    {
        // The reset is scoped by tenant column on every table. A mistake here would be
        // catastrophic and completely silent on a shared database.
        const long other = 77_399;
        const long Tenant = 77_315;
        await SeedAsync(Tenant);
        await using (var db = database.ContextFor(null))
        {
            Seed.EnsureBusinessUnit(db, other);
            db.Leads.Add(new Lead
            {
                Rfqno = "OTHER-001", BuyersName = "Other", RecDate = DateTime.UtcNow,
                BidClosingDate = DateTime.UtcNow.AddDays(7), LeadSource = "tests",
                CreatedBy = "tests", CreatedDate = DateTime.UtcNow, BusinessUnitId = other, NoOfLineItems = 0,
            });
            await db.SaveChangesAsync();
        }

        await using (var db = database.ContextFor(null))
            await Build(db).ExecuteAsync(Tenant, TenantName, CancellationToken.None);

        Assert.Equal(1, await TenantRowsAsync("Leads", other));
    }
}
