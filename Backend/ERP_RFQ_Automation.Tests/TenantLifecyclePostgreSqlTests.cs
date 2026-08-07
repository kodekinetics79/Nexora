using System.Text;
using System.Text.Json;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Platform.Lifecycle;
using ERP_RFQ_Automation.Platform.Models;
using ERP_RFQ_Automation.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// Offboarding against a real database, where the append-only triggers, the execution roles and
/// row-level security are real.
///
/// <para>Everything here is a property SQLite structurally cannot demonstrate: that a purge
/// suspends ~30 append-only guards and puts them back, that it touches exactly one tenant, that
/// the audit trail outlives the tenant it describes, and that an export executed under
/// <c>nexora_tenant_app</c> cannot see another tenant's rows even if its own predicate were wrong.</para>
/// </summary>
[Collection(PostgreSqlIntegrationCollection.Name)]
public sealed class TenantLifecyclePostgreSqlTests
{
    private readonly PostgreSqlTestDatabase _database;

    public TenantLifecyclePostgreSqlTests(PostgreSqlTestDatabase database)
    {
        _database = database;
        // The migration that creates the offboarding tables is owned by the schema owner. Until it
        // lands, they are created from the same ApplyTenantLifecycleModel configuration it will be
        // generated from.
        TenantLifecycleSchema.EnsureAsync(database.ConnectionString).GetAwaiter().GetResult();
    }

    private const string OffboardingReason =
        "Contract terminated on 2026-07-31; customer confirmed offboarding in writing.";
    private const string ErasureReason =
        "Article 17 erasure request received from the data subject on 2026-08-01.";
    private const string MailboxPassword = "super-secret-mailbox-password";

    private ErpRfqAutomationContext Context() =>
        TenantLifecycleHarness.PostgresContext(_database.ConnectionString);

    private TenantOffboardingService Service(ErpRfqAutomationContext context) =>
        TenantLifecycleHarness.Service(context, _database.ConnectionString);

    // ---------------------------------------------------------------------------------- seed

    /// <summary>
    /// A tenant with a workspace, a person, a mailbox and a commercial document with lines —
    /// enough for a purge to have something to destroy and an export to have something to carry.
    /// </summary>
    private async Task<Tenant> SeedTenantAsync(long businessUnitId, string slug)
    {
        await using var db = Context();

        var existing = await db.Set<Tenant>().IgnoreQueryFilters()
            .FirstOrDefaultAsync(t => t.Slug == slug);
        if (existing is not null) return existing;

        var unit = await db.BusinessUnits.FirstOrDefaultAsync(b => b.Id == businessUnitId)
                   ?? Seed.BusinessUnit(db, businessUnitId);
        unit.BusinessUnitName = $"Offboarding {slug}";

        var tenant = new Tenant
        {
            Name = $"Offboarding {slug}",
            Slug = slug,
            Status = TenantStatus.Archived,
            PrimaryBusinessUnitId = businessUnitId,
            ContactEmail = $"info-{slug}@customer.test",
            Phone = "+966 11 000 0000",
            BillingContactName = "Dana Rowe",
            BillingContactEmail = $"ap-{slug}@customer.test",
            AccountOwnerEmail = "csm@nexora.test",
            CreatedBy = "tests",
            CreatedOn = DateTime.UtcNow
        };
        db.Set<Tenant>().Add(tenant);

        db.Users.Add(new User
        {
            FirstName = "Dana", LastName = "Rowe", Email = $"dana-{slug}@customer.test",
            PasswordHash = "hashed", ImageUrl = string.Empty, Buid = businessUnitId,
            IsActive = true, Timezone = "Asia/Riyadh", CreatedBy = "tests", CreatedOn = DateTime.UtcNow
        });

        db.EmailConfigurations.Add(new EmailConfiguration
        {
            BusinessUnitId = businessUnitId, ConfigurationName = $"Inbox {slug}",
            EmailAddress = $"in-{slug}@customer.test", Protocol = "IMAP", Host = "imap.example.com",
            Port = 993, Username = $"in-{slug}@customer.test", Password = MailboxPassword,
            UseSsl = true, PollingInterval = 5, IsActive = true, CreatedOn = DateTime.UtcNow
        });

        var lead = new Lead
        {
            Rfqno = $"OFFBOARD-{slug}", BuyersName = "Buyer", RecDate = DateTime.UtcNow,
            BidClosingDate = DateTime.UtcNow.AddDays(7), LeadSource = "tests",
            CreatedBy = "tests", CreatedDate = DateTime.UtcNow,
            BusinessUnitId = businessUnitId, NoOfLineItems = 1
        };
        lead.LeadItems.Add(new LeadItem
        {
            LineItemNo = "1", ProductShortDescription = $"Widget for {slug}",
            Quantity = 3, UnitOfMeasure = "EA", Currency = "SAR"
        });
        db.Leads.Add(lead);

        await db.SaveChangesAsync();
        return tenant;
    }

    private async Task<Tenant> SchedulePurgeableAsync(long businessUnitId, string slug)
    {
        var tenant = await SeedTenantAsync(businessUnitId, slug);
        await using var db = Context();
        await Service(db).ScheduleDeletionAsync(tenant.Id,
            new ScheduleTenantDeletionRequest { Reason = OffboardingReason },
            TenantLifecycleHarness.Operator(), null, CancellationToken.None);
        await TenantLifecycleHarness.ElapseRetentionWindowAsync(db, tenant.Id);
        return tenant;
    }

    // ------------------------------------------------------------------------------- purge

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task A_purge_destroys_its_tenant_and_leaves_the_neighbour_byte_identical()
    {
        // The catastrophic-and-silent failure mode of any tenant-scoped delete on a shared
        // database. Every table carrying a tenant column is fingerprinted for the NEIGHBOUR before
        // and after, so a predicate that widened by one row anywhere fails this test.
        const long doomed = 88_101, neighbour = 88_102;
        var tenant = await SchedulePurgeableAsync(doomed, "purge-doomed");
        await SeedTenantAsync(neighbour, "purge-neighbour");

        var before = await FingerprintAsync(neighbour);
        Assert.NotEmpty(before);

        await using var db = Context();
        var result = await Service(db).PurgeAsync(tenant.Id,
            new ConfirmTenantDestructionRequest { Reason = OffboardingReason, Confirmation = tenant.Name },
            TenantLifecycleHarness.Operator(), null, CancellationToken.None);

        Assert.True(result.RowsDeleted > 0);

        // Gone: the document, its lines, the person, the mailbox and the workspace itself.
        Assert.Equal(0, await TenantRowsAsync("Leads", doomed));
        Assert.Equal(0, await TenantRowsAsync("Users", doomed));
        Assert.Equal(0, await TenantRowsAsync("Email_Configurations", doomed));
        Assert.Equal(0, await TenantRowsAsync("BusinessUnits", doomed));

        // Untouched: every byte of the neighbour, everywhere.
        Assert.Equal(before, await FingerprintAsync(neighbour));
    }

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task The_append_only_guards_are_restored_after_a_purge_commits()
    {
        // session_replication_role is set LOCAL, so it lapses with the transaction. If it ever
        // leaked past the purge, every immutability guarantee in the system would be silently off
        // for the rest of that connection's life — worse than a failed purge.
        var tenant = await SchedulePurgeableAsync(88_103, "purge-guards");
        await using var db = Context();
        await Service(db).PurgeAsync(tenant.Id,
            new ConfirmTenantDestructionRequest { Reason = OffboardingReason, Confirmation = tenant.Name },
            TenantLifecycleHarness.Operator(), null, CancellationToken.None);

        await using var probe = Context();
        var setting = (await probe.Database
            .SqlQueryRaw<string>("""SELECT current_setting('session_replication_role') AS "Value" """)
            .ToListAsync()).Single();
        Assert.Equal("origin", setting);
    }

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task The_audit_trail_and_the_lifecycle_history_outlive_the_tenant()
    {
        var tenant = await SchedulePurgeableAsync(88_104, "purge-evidence");

        await using var db = Context();
        var result = await Service(db).PurgeAsync(tenant.Id,
            new ConfirmTenantDestructionRequest { Reason = OffboardingReason, Confirmation = tenant.Name },
            TenantLifecycleHarness.Operator("compliance@example.test", 11), null, CancellationToken.None);

        Assert.True(result.LifecycleEventsRetained > 0);
        Assert.True(result.PlatformAuditRecordsRetained > 0);

        await using var verify = Context();

        // The offboarding is fully reconstructable from rows that were inside the purge's own
        // blast radius by tenant id, and are alive because the preserved list keeps them there.
        var history = await verify.Set<TenantLifecycleEvent>().AsNoTracking()
            .Where(e => e.TenantId == tenant.Id).OrderBy(e => e.Id).ToListAsync();
        Assert.Equal(
            [TenantLifecycleActions.ScheduleDeletion, TenantLifecycleActions.PurgeStarted,
             TenantLifecycleActions.Purged],
            history.Select(e => e.Action).ToArray());

        // Readable WITHOUT the tenant: identity is on every row, which is the property that lets
        // the history describe a customer that no longer exists anywhere else.
        Assert.All(history, e =>
        {
            Assert.Equal(tenant.Slug, e.TenantSlug);
            Assert.Equal(tenant.Name, e.TenantName);
            Assert.Contains(OffboardingReason, e.Reason);
            Assert.False(string.IsNullOrWhiteSpace(e.ActorEmail));
        });

        // Who destroyed the tenant is attributable to the operator who did it, not to whoever
        // scheduled the deletion weeks earlier.
        Assert.All(
            history.Where(e => e.Action != TenantLifecycleActions.ScheduleDeletion),
            e => Assert.Equal("compliance@example.test", e.ActorEmail));

        var audit = await verify.Set<PlatformAuditLog>().AsNoTracking()
            .Where(a => a.ActAsTenantId == tenant.Id).Select(a => a.Action).ToListAsync();
        Assert.Contains(TenantLifecycleActions.Purged, audit);

        // The tombstone: the tenant row and its offboarding record survive so nothing above points
        // at nothing, and the stage is terminal.
        var record = await verify.Set<TenantOffboarding>().AsNoTracking()
            .SingleAsync(r => r.TenantId == tenant.Id);
        Assert.Equal(TenantOffboardingStage.Purged, record.Stage);
        Assert.NotNull(record.PurgedOn);
        Assert.Equal(result.RowsDeleted, record.PurgedRowCount);
    }

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task A_purged_tenant_cannot_be_purged_again()
    {
        var tenant = await SchedulePurgeableAsync(88_105, "purge-twice");
        await using var db = Context();
        var service = Service(db);
        var actor = TenantLifecycleHarness.Operator();

        await service.PurgeAsync(tenant.Id,
            new ConfirmTenantDestructionRequest { Reason = OffboardingReason, Confirmation = tenant.Name },
            actor, null, CancellationToken.None);

        var refusal = await Assert.ThrowsAsync<TenantOffboardingRefusedException>(() =>
            service.PurgeAsync(tenant.Id,
                new ConfirmTenantDestructionRequest { Reason = OffboardingReason, Confirmation = tenant.Name },
                actor, null, CancellationToken.None));

        Assert.Equal(409, refusal.SuggestedStatusCode);
        Assert.Contains("already purged", refusal.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task The_purge_preview_names_the_blast_radius_without_changing_anything()
    {
        const long unit = 88_106;
        var tenant = await SeedTenantAsync(unit, "purge-preview");
        await using var db = Context();

        var preview = await TenantLifecycleHarness.PurgeExecutor(_database.ConnectionString)
            .PreviewAsync(tenant.Id, unit, CancellationToken.None);

        Assert.True(preview.TotalRows > 0);
        Assert.Contains(preview.Tables, t => t.Table.Contains("Leads", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(preview.Preserved, p => p == "platform.PlatformAuditLogs");
        Assert.Contains(preview.Preserved, p => p == "platform.TenantLifecycleEvents");

        // A preview is what an operator runs to decide, so it must change nothing.
        Assert.Equal(1, await TenantRowsAsync("Leads", unit));
    }

    // ------------------------------------------------------------------------------ export

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task An_export_carries_only_the_exporting_tenants_rows()
    {
        const long mine = 88_201, theirs = 88_202;
        var tenant = await SeedTenantAsync(mine, "export-mine");
        var other = await SeedTenantAsync(theirs, "export-theirs");

        await using var db = Context();
        var (document, receipt) = await Service(db).ExportAsync(
            tenant.Id, "Offboarding data hand-over requested by the customer.", tenant.Name,
            TenantLifecycleHarness.Operator(), null, CancellationToken.None);

        var json = Encoding.UTF8.GetString(document.Content);

        // The positive half: their own document is in there, with its lines.
        Assert.Contains($"OFFBOARD-{tenant.Slug}", json, StringComparison.Ordinal);
        Assert.Contains($"Widget for {tenant.Slug}", json, StringComparison.Ordinal);

        // The half that matters: no trace of the neighbour, anywhere in the bytes.
        Assert.DoesNotContain($"OFFBOARD-{other.Slug}", json, StringComparison.Ordinal);
        Assert.DoesNotContain($"Widget for {other.Slug}", json, StringComparison.Ordinal);
        Assert.DoesNotContain($"dana-{other.Slug}@customer.test", json, StringComparison.Ordinal);

        using var parsed = JsonDocument.Parse(document.Content);
        var manifest = parsed.RootElement.GetProperty("manifest");
        Assert.Equal(TenantDataExportService.FormatIdentifier, manifest.GetProperty("format").GetString());
        Assert.Equal(tenant.Id, manifest.GetProperty("tenantId").GetInt64());

        // The database itself was enforcing the scope, not only the predicate.
        Assert.True(manifest.GetProperty("rowLevelSecurityEnforced").GetBoolean());
        Assert.True(document.RowLevelSecurityEnforced);

        // Every declared section resolved. A refusal here is a section that cannot be scoped, and
        // an unnoticed one is a customer handed an incomplete file.
        Assert.Empty(document.Refused);

        var data = parsed.RootElement.GetProperty("data");
        Assert.Equal(1, data.GetProperty(nameof(Lead)).GetArrayLength());
        Assert.Equal(1, data.GetProperty(nameof(LeadItem)).GetArrayLength());
        Assert.Equal(1, data.GetProperty(nameof(User)).GetArrayLength());

        Assert.Equal(document.ContentSha256, receipt.ContentSha256);
        Assert.Equal(document.TotalRows, receipt.TotalRows);
    }

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task An_export_never_carries_a_credential()
    {
        // An export is a copy of the customer's records, not a copy of our credential store, and
        // this file gets emailed, forwarded and left in a downloads folder.
        const long unit = 88_203;
        var tenant = await SeedTenantAsync(unit, "export-secrets");

        await using var db = Context();
        var (document, _) = await Service(db).ExportAsync(
            tenant.Id, "Offboarding data hand-over for a departing customer.", tenant.Name,
            TenantLifecycleHarness.Operator(), null, CancellationToken.None);

        var json = Encoding.UTF8.GetString(document.Content);
        Assert.DoesNotContain(MailboxPassword, json, StringComparison.Ordinal);
        Assert.Contains("[redacted: credential]", json, StringComparison.Ordinal);

        var mailbox = document.Sections.Single(s => s.Section == nameof(EmailConfiguration));
        Assert.Contains(mailbox.RedactedColumns, c => c.Equals("Password", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(1, mailbox.Rows);
    }

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task An_export_leaves_a_receipt_that_fingerprints_what_was_handed_over()
    {
        const long unit = 88_204;
        var tenant = await SeedTenantAsync(unit, "export-receipt");

        await using var db = Context();
        var (document, receipt) = await Service(db).ExportAsync(
            tenant.Id, "Offboarding data hand-over for a departing customer.", tenant.Name,
            TenantLifecycleHarness.Operator("compliance@example.test", 11), null, CancellationToken.None);

        var expected = Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(document.Content)).ToLowerInvariant();
        Assert.Equal(expected, receipt.ContentSha256);

        await using var verify = Context();
        var persisted = await verify.Set<TenantExportReceipt>().AsNoTracking()
            .SingleAsync(r => r.Id == receipt.Id);
        Assert.Equal(tenant.Id, persisted.TenantId);
        Assert.Equal(tenant.Slug, persisted.TenantSlug);
        Assert.Equal("compliance@example.test", persisted.RequestedBy);
        Assert.Equal(document.Content.LongLength, persisted.SizeBytes);
        Assert.Contains(nameof(Lead), persisted.Sections, StringComparison.Ordinal);

        var status = await Service(verify).GetStatusAsync(tenant.Id, CancellationToken.None);
        Assert.Equal(receipt.Id, Assert.Single(status.Exports).Id);
        Assert.NotNull(status.LastExportedOn);

        var audit = await verify.Set<PlatformAuditLog>().AsNoTracking()
            .Where(a => a.ActAsTenantId == tenant.Id && a.Action == TenantLifecycleActions.Export)
            .SingleAsync();
        Assert.Contains(receipt.ContentSha256, audit.Metadata!, StringComparison.Ordinal);
    }

    // ----------------------------------------------------------------------------- erasure

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task Erasure_and_deletion_are_independently_achievable()
    {
        // Two tenants, two different obligations honoured, neither depending on the other: one is
        // erased and keeps every commercial record; the other is destroyed having never been
        // erased. This is the whole reason they are separate operations.
        const long erasedUnit = 88_301, purgedUnit = 88_302;
        var erasedTenant = await SeedTenantAsync(erasedUnit, "erasure-only");
        var purgedTenant = await SchedulePurgeableAsync(purgedUnit, "deletion-only");

        await using var db = Context();
        var service = Service(db);
        var actor = TenantLifecycleHarness.Operator();

        var erasure = await service.ErasePersonalDataAsync(erasedTenant.Id,
            new ConfirmTenantDestructionRequest { Reason = ErasureReason, Confirmation = erasedTenant.Name },
            actor, null, CancellationToken.None);
        var purged = await service.PurgeAsync(purgedTenant.Id,
            new ConfirmTenantDestructionRequest { Reason = OffboardingReason, Confirmation = purgedTenant.Name },
            actor, null, CancellationToken.None);

        Assert.True(erasure.IdentitiesErased > 0);
        Assert.True(purged.RowsDeleted > 0);

        await using var verify = Context();

        // Erased: the person is gone, the commercial record is not — which is what makes tax
        // retention and an Article 17 request satisfiable at the same time.
        var user = await verify.Users.IgnoreQueryFilters().AsNoTracking()
            .SingleAsync(u => u.Buid == erasedUnit);
        Assert.EndsWith("@erased.invalid", user.Email, StringComparison.Ordinal);
        Assert.False(user.IsActive);
        Assert.Equal(1, await TenantRowsAsync("Leads", erasedUnit));

        var erasedTenantRow = await verify.Set<Tenant>().IgnoreQueryFilters().AsNoTracking()
            .SingleAsync(t => t.Id == erasedTenant.Id);
        Assert.Null(erasedTenantRow.ContactEmail);
        Assert.Null(erasedTenantRow.BillingContactEmail);
        // The operator's own account manager is not the customer's personal data.
        Assert.Equal("csm@nexora.test", erasedTenantRow.AccountOwnerEmail);
        // Erasure is not a move along the deletion path.
        Assert.Equal(TenantOffboardingStage.NotScheduled,
            (await verify.Set<TenantOffboarding>().AsNoTracking()
                .SingleAsync(r => r.TenantId == erasedTenant.Id)).Stage);

        // Purged without ever being erased: the records are gone and the erasure columns are still
        // null, so "deleted" was never silently reported as "erased".
        Assert.Equal(0, await TenantRowsAsync("Leads", purgedUnit));
        var purgedRecord = await verify.Set<TenantOffboarding>().AsNoTracking()
            .SingleAsync(r => r.TenantId == purgedTenant.Id);
        Assert.Equal(TenantOffboardingStage.Purged, purgedRecord.Stage);
        Assert.Null(purgedRecord.PersonalDataErasedOn);
    }

    // ----------------------------------------------------------------------------- helpers

    /// <summary>
    /// An md5 per table over every one of a tenant's rows, rendered as text in a stable order.
    /// The comparison this feeds is the strongest available statement of "nothing of theirs
    /// changed" without dumping the database.
    /// </summary>
    private async Task<Dictionary<string, string>> FingerprintAsync(long businessUnitId)
    {
        await using var connection = new NpgsqlConnection(_database.ConnectionString);
        await connection.OpenAsync();

        var targets = new List<(string Schema, string Table, string Column)>();
        await using (var discover = new NpgsqlCommand(
            """
            SELECT c.table_schema, c.table_name, c.column_name
            FROM information_schema.columns c
            JOIN information_schema.tables t
              ON t.table_schema = c.table_schema AND t.table_name = c.table_name
             AND t.table_type = 'BASE TABLE'
            WHERE c.table_schema NOT IN ('pg_catalog', 'information_schema')
              AND lower(c.column_name) IN ('businessunitid', 'buid')
            ORDER BY c.table_schema, c.table_name;
            """, connection))
        await using (var reader = await discover.ExecuteReaderAsync())
            while (await reader.ReadAsync())
                targets.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2)));

        var fingerprint = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (schema, table, column) in targets)
        {
            await using var command = new NpgsqlCommand(
                $"""
                 SELECT coalesce(md5(string_agg(row_text, '|' ORDER BY row_text)), '') AS hash
                 FROM (SELECT t::text AS row_text FROM "{schema}"."{table}" t
                       WHERE t."{column}" = @tenant) rows;
                 """, connection);
            command.Parameters.AddWithValue("tenant", businessUnitId);
            var hash = (string)(await command.ExecuteScalarAsync())!;
            if (hash.Length > 0) fingerprint[$"{schema}.{table}"] = hash;
        }

        return fingerprint;
    }

    /// <summary>
    /// Counts a tenant's rows, resolving the tenant column from the live catalogue. Hardcoding it
    /// re-creates in the test the exact naming assumption the production code refuses to make:
    /// legacy tables spell it "BusinessUnitID", newer ones "BusinessUnitId", Users uses "BUID",
    /// and BusinessUnits is keyed by its own id.
    /// </summary>
    private async Task<int> TenantRowsAsync(string table, long businessUnitId)
    {
        await using var connection = new NpgsqlConnection(_database.ConnectionString);
        await connection.OpenAsync();

        await using var resolve = new NpgsqlCommand(
            """
            SELECT column_name FROM information_schema.columns
            WHERE table_schema = 'public' AND table_name = @table
              AND lower(column_name) IN ('businessunitid', 'buid', 'id')
            ORDER BY CASE lower(column_name)
                WHEN 'businessunitid' THEN 0 WHEN 'buid' THEN 1 ELSE 2 END
            LIMIT 1;
            """, connection);
        resolve.Parameters.AddWithValue("table", table);
        var column = (string?)await resolve.ExecuteScalarAsync()
            ?? throw new InvalidOperationException($"No tenant column found on {table}.");

        await using var count = new NpgsqlCommand(
            $"""SELECT count(*)::int FROM public."{table}" WHERE "{column}" = @tenant;""", connection);
        count.Parameters.AddWithValue("tenant", businessUnitId);
        return (int)(await count.ExecuteScalarAsync())!;
    }
}
