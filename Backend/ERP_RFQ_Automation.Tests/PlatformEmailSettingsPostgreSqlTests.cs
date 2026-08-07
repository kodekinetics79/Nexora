using ERP_RFQ_Automation.Platform.Notifications;
using ERP_RFQ_Automation.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// Certifies the platform email settings table against real PostgreSQL: the credential is
/// unreadable at rest, and the COLUMN-level grants the runtime roles need are exactly the ones the
/// projected read requires — no more, and not one column less.
///
/// <para><b>Why this cannot be a SQLite test.</b> SQLite has neither roles nor privileges, so a
/// query that materialises columns a role was never granted passes there and fails in production
/// with 42501. Outbound mail is sent from the tenant plane (quote delivery, lead routing) as well
/// as the platform plane, so this table is read under <c>nexora_tenant_app</c> on an ordinary
/// customer request — which is precisely where that defect would first appear.</para>
///
/// <para>The DDL and the GRANT block below are the specification handed to the migration author.
/// They are executed here rather than described, so the grant list is verified against the queries
/// the code actually issues before the migration is written.</para>
/// </summary>
[Collection(PostgreSqlIntegrationCollection.Name)]
public class PlatformEmailSettingsPostgreSqlTests : IAsyncLifetime
{
    private const string SmtpPassword = "pilot-smtp-password-9f3a";
    private const string SendGridKey = "SG.pilot-api-key-7c21";

    private readonly PostgreSqlTestDatabase _database;

    public PlatformEmailSettingsPostgreSqlTests(PostgreSqlTestDatabase database) => _database = database;

    /// <summary>The exact table this module needs. Handed to the migration author verbatim.</summary>
    private const string CreateTableSql = """
        CREATE TABLE platform."PlatformEmailSettings" (
            "Id"                             bigint NOT NULL,
            "Provider"                       character varying(32) NOT NULL DEFAULT 'console',
            "FromAddress"                    character varying(320) NOT NULL,
            "FromName"                       character varying(200) NOT NULL,
            "ReplyToAddress"                 character varying(320) NULL,
            "AppBaseUrl"                     character varying(512) NOT NULL,
            "SmtpHost"                       character varying(255) NULL,
            "SmtpPort"                       integer NOT NULL,
            "SmtpUsername"                   character varying(320) NULL,
            "SmtpPassword"                   character varying(2048) NULL,
            "SmtpEnableSsl"                  boolean NOT NULL,
            "SmtpTimeoutMs"                  integer NOT NULL,
            "SendGridApiKey"                 character varying(2048) NULL,
            "SendGridApiBaseUrl"             character varying(512) NULL,
            "OutboundGuardMode"              character varying(32) NOT NULL DEFAULT 'Live',
            "OutboundGuardRedirectTo"        character varying(320) NULL,
            "OutboundGuardAllowedRecipients" character varying(4000) NULL,
            "OutboundGuardAllowedDomains"    character varying(4000) NULL,
            "OutboundGuardSubjectTag"        character varying(64) NULL,
            "Version"                        bigint NOT NULL,
            "CreatedAtUtc"                   timestamp without time zone NOT NULL,
            "UpdatedAtUtc"                   timestamp without time zone NOT NULL,
            "UpdatedBy"                      character varying(320) NULL,
            "UpdateReason"                   character varying(1000) NULL,
            "LastVerifiedAtUtc"              timestamp without time zone NULL,
            "LastVerifiedBy"                 character varying(320) NULL,
            "LastVerifiedRecipient"          character varying(320) NULL,
            "LastFailureAtUtc"               timestamp without time zone NULL,
            "LastFailureKind"                character varying(48) NULL,
            "LastFailureReason"              character varying(1000) NULL,
            CONSTRAINT "PK_PlatformEmailSettings" PRIMARY KEY ("Id"),
            CONSTRAINT "CK_PlatformEmailSettings_Singleton" CHECK ("Id" = 1)
        );
        """;

    /// <summary>
    /// The privileges, and the reasoning that decides them.
    ///
    /// <para><c>nexora_pipeline_app</c> is the platform plane and the background workers: it reads
    /// everything and writes the configuration. No DELETE — a single-row configuration that can be
    /// deleted is a way to silently revert the platform to the console provider.</para>
    ///
    /// <para><c>nexora_tenant_app</c> and <c>nexora_identity_app</c> get SELECT on the TRANSPORT
    /// columns only, because a tenant-scoped request that delivers a quote has to resolve the
    /// transport on its own connection. They cannot read who configured it, why, or when it was
    /// last verified — that is operator correspondence about the platform, not tenant data — and
    /// they cannot write anything at all. The credential columns are included: they hold AES-256-GCM
    /// envelopes whose key lives only in application configuration, so a role that can read the row
    /// still cannot read the password.</para>
    /// </summary>
    private const string GrantSql = """
        GRANT SELECT, INSERT, UPDATE ON TABLE platform."PlatformEmailSettings" TO nexora_pipeline_app;
        REVOKE DELETE, TRUNCATE ON TABLE platform."PlatformEmailSettings" FROM nexora_pipeline_app;

        GRANT SELECT (
            "Id", "Version", "UpdatedAtUtc",
            "Provider", "FromAddress", "FromName", "ReplyToAddress", "AppBaseUrl",
            "SmtpHost", "SmtpPort", "SmtpUsername", "SmtpPassword", "SmtpEnableSsl", "SmtpTimeoutMs",
            "SendGridApiKey", "SendGridApiBaseUrl",
            "OutboundGuardMode", "OutboundGuardRedirectTo",
            "OutboundGuardAllowedRecipients", "OutboundGuardAllowedDomains", "OutboundGuardSubjectTag"
        ) ON TABLE platform."PlatformEmailSettings" TO nexora_tenant_app, nexora_identity_app;

        REVOKE INSERT, UPDATE, DELETE, TRUNCATE ON TABLE platform."PlatformEmailSettings"
            FROM nexora_tenant_app, nexora_identity_app;
        """;

    public async Task InitializeAsync()
    {
        await using var connection = await _database.OpenConnectionAsync();
        await Execute(connection, """DROP TABLE IF EXISTS platform."PlatformEmailSettings";""");
        await Execute(connection, CreateTableSql);
        await Execute(connection, GrantSql);

        await using var context = OwnerContext();
        context.Set<PlatformEmailSettings>().Add(new PlatformEmailSettings
        {
            Id = PlatformEmailSettings.SingletonId,
            Provider = "smtp",
            FromAddress = "no-reply@customer.test",
            FromName = "Nexora",
            AppBaseUrl = "https://app.customer.test",
            SmtpHost = "smtp.customer.test",
            SmtpPort = 587,
            SmtpUsername = "apikey",
            SmtpPassword = SmtpPassword,
            SendGridApiKey = SendGridKey,
            Version = 4,
            UpdatedBy = "owner@nexora.test",
            UpdateReason = "Pilot go-live."
        });
        await context.SaveChangesAsync();
    }

    public async Task DisposeAsync()
    {
        await using var connection = await _database.OpenConnectionAsync();
        await Execute(connection, """DROP TABLE IF EXISTS platform."PlatformEmailSettings";""");
    }

    [Fact]
    public async Task Both_credentials_are_encrypted_at_rest()
    {
        await using var connection = await _database.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            """SELECT "SmtpPassword", "SendGridApiKey" FROM platform."PlatformEmailSettings" WHERE "Id" = 1""";

        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        var storedPassword = reader.GetString(0);
        var storedKey = reader.GetString(1);

        // The threat model is an actor who can read the database — a backup, or one of the roles
        // that bypasses RLS. Both of these are the AES-256-GCM envelope, and the key is not here.
        Assert.StartsWith("v1:", storedPassword);
        Assert.StartsWith("v1:", storedKey);
        Assert.DoesNotContain(SmtpPassword, storedPassword, StringComparison.Ordinal);
        Assert.DoesNotContain(SendGridKey, storedKey, StringComparison.Ordinal);

        // Distinct nonces: two secrets in the same row must not share ciphertext structure.
        Assert.NotEqual(storedPassword[..24], storedKey[..24]);
    }

    [Fact]
    public async Task The_projected_transport_read_succeeds_under_the_tenant_role()
    {
        await using var connection = await OpenAsRoleAsync("nexora_tenant_app");
        await using var context = ContextOn(connection);

        var store = new PlatformEmailSettingsStore(context);

        // This is the query a tenant request issues when it delivers a quote. It must work, and it
        // must return a usable (decrypted) credential.
        var snapshot = await store.ReadAsync();

        Assert.NotNull(snapshot);
        Assert.Equal("smtp", snapshot!.NormalizedProvider);
        Assert.Equal("smtp.customer.test", snapshot.SmtpHost);
        Assert.Equal(SmtpPassword, snapshot.SmtpPassword);
        Assert.Equal(4, snapshot.Version);

        Assert.Equal(4, await store.ReadVersionAsync());
    }

    [Fact]
    public async Task Materialising_the_whole_entity_under_the_tenant_role_is_refused()
    {
        await using var connection = await OpenAsRoleAsync("nexora_tenant_app");
        await using var context = ContextOn(connection);

        // The defect class this guards: an unprojected read compiles, passes every SQLite test, and
        // fails at runtime on the first tenant request that sends an email. Asserting the refusal
        // makes the projection in PlatformEmailSettingsStore load-bearing rather than stylistic.
        var refused = await Assert.ThrowsAsync<PostgresException>(() =>
            context.Set<PlatformEmailSettings>().AsNoTracking().FirstOrDefaultAsync());

        Assert.Equal("42501", refused.SqlState);
    }

    [Fact]
    public async Task The_tenant_role_cannot_read_the_operator_columns()
    {
        await using var connection = await OpenAsRoleAsync("nexora_tenant_app");
        await using var command = connection.CreateCommand();
        command.CommandText =
            """SELECT "UpdatedBy", "UpdateReason" FROM platform."PlatformEmailSettings" WHERE "Id" = 1""";

        var refused = await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteReaderAsync());
        Assert.Equal("42501", refused.SqlState);
    }

    [Fact]
    public async Task The_tenant_role_cannot_change_the_platforms_sending_identity()
    {
        await using var connection = await OpenAsRoleAsync("nexora_tenant_app");
        await using var command = connection.CreateCommand();
        command.CommandText =
            """UPDATE platform."PlatformEmailSettings" SET "FromAddress" = 'attacker@elsewhere.test' WHERE "Id" = 1""";

        var refused = await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync());
        Assert.Equal("42501", refused.SqlState);
    }

    [Fact]
    public async Task The_platform_role_can_read_and_update_but_never_delete()
    {
        await using var connection = await OpenAsRoleAsync("nexora_pipeline_app");

        await using (var context = ContextOn(connection))
        {
            var row = await context.Set<PlatformEmailSettings>().FirstAsync();
            Assert.Equal(SmtpPassword, row.SmtpPassword);
            row.UpdateReason = "Rotated the relay credential.";
            row.Version += 1;
            await context.SaveChangesAsync();
        }

        await using var delete = connection.CreateCommand();
        delete.CommandText = """DELETE FROM platform."PlatformEmailSettings" WHERE "Id" = 1""";
        var refused = await Assert.ThrowsAsync<PostgresException>(() => delete.ExecuteNonQueryAsync());

        // A single-row configuration that can be deleted is a way to silently revert the whole
        // platform to the console provider, which sends nothing and reports nothing.
        Assert.Equal("42501", refused.SqlState);
    }

    [Fact]
    public async Task A_second_row_is_refused_by_the_database()
    {
        await using var connection = await _database.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO platform."PlatformEmailSettings"
                ("Id", "Provider", "FromAddress", "FromName", "AppBaseUrl", "SmtpPort",
                 "SmtpEnableSsl", "SmtpTimeoutMs", "OutboundGuardMode", "Version",
                 "CreatedAtUtc", "UpdatedAtUtc")
            VALUES (2, 'console', 'other@nexora.test', 'Nexora', 'https://x.test', 587,
                    true, 30000, 'Live', 1, now(), now());
            """;

        var refused = await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync());
        Assert.Equal("23514", refused.SqlState); // check_violation
    }

    // ==== harness ====================================================================================

    private PlatformEmailTestContext OwnerContext() =>
        new(new DbContextOptionsBuilder<PlatformEmailTestContext>()
            .UseNpgsql(_database.ConnectionString)
            .Options);

    private static PlatformEmailTestContext ContextOn(NpgsqlConnection connection) =>
        new(new DbContextOptionsBuilder<PlatformEmailTestContext>()
            .UseNpgsql(connection)
            .Options);

    /// <summary>
    /// A session pinned to a runtime role. <c>SET ROLE</c> rather than the command interceptor's
    /// <c>SET LOCAL ROLE</c>, because this test is about privileges on one table and not about the
    /// tenant GUC — and a session-level role survives across the several commands EF issues.
    /// </summary>
    private async Task<NpgsqlConnection> OpenAsRoleAsync(string role)
    {
        // Pooling off: a pooled connection returned with a role still set would leak that role into
        // an unrelated test and produce a failure nobody could reproduce.
        var builder = new NpgsqlConnectionStringBuilder(_database.ConnectionString) { Pooling = false };
        var connection = new NpgsqlConnection(builder.ConnectionString);
        await connection.OpenAsync();
        await Execute(connection, $"SET ROLE {role};");
        return connection;
    }

    private static async Task Execute(NpgsqlConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }
}
