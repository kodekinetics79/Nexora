using ERP_RFQ_Automation.Tests.Support;
using Npgsql;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// Gate 3 mailbox identity: a provider message id is unique WITHIN a mailbox and may legitimately
/// repeat ACROSS mailboxes, because two tenants polling the same distribution list receive the same
/// provider id and neither intake may swallow the other's.
///
/// SQUASH NOTE — what this file used to do, and why it no longer does it.
///   It used to migrate an isolated database to 20260727042452_V1Gate02CommercialIntelligenceIntegrity,
///   seed a mailbox and an ingest, clone the database as a "verified pre-upgrade backup", then walk
///   up to 20260727171327_V1Gate03IntegrationOperationalVisibility, back down, and up again,
///   asserting at each step that the row survived, that the composite index came and went, and that
///   the DOWN path REFUSED once a second mailbox held the same provider id.
///
///   20260811033109_SquashedSchemaBaseline collapsed all 134 migrations into one, so there is no
///   longer a previous migration to walk to and no separate down path to refuse — the walk is not
///   weakened, it is arithmetically impossible. The down-refusal guard was a property of a
///   migration that no longer exists and cannot be reached by any database: a new database starts
///   at the baseline, and every existing database is stamped past it.
///
///   What was NEVER about migration identity is the rule itself, and that is what is asserted here,
///   now against the live catalogue and against real INSERTs rather than against an index name
///   appearing and disappearing: the unique index exists over exactly (EmailConfigurationID,
///   MessageID), a repeat within one mailbox is rejected by the database, and the same provider id
///   in a second mailbox is accepted. A regression that dropped or widened that index failed the
///   old test and fails this one.
/// </summary>
[Collection(PostgreSqlIntegrationCollection.Name)]
public sealed class V1Gate03MigrationPostgreSqlTests(PostgreSqlTestDatabase database)
{
    private const long Tenant = 9_927_301;
    private const long FirstMailbox = 9_927_301;
    private const long SecondMailbox = 9_927_302;
    private const string ProviderMessageId = "<gate3@example.test>";

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task Mailbox_identity_is_unique_within_a_mailbox_and_repeatable_across_mailboxes()
    {
        await using var connection = await database.OpenConnectionAsync();

        // The index is asserted by DEFINITION, not by name alone: a later change that kept the name
        // but dropped UNIQUE, or that added a third column, would leave the name check green while
        // letting a duplicate intake through.
        await using (var index = connection.CreateCommand())
        {
            index.CommandText = """
                SELECT indexdef FROM pg_indexes
                WHERE schemaname = 'public' AND tablename = 'EmailIngests'
                  AND indexname = 'UQ_EmailIngests_EmailConfigurationID_MessageID';
                """;
            var definition = Assert.IsType<string>(await index.ExecuteScalarAsync());
            Assert.StartsWith("CREATE UNIQUE INDEX", definition, StringComparison.Ordinal);
            Assert.Contains("(\"EmailConfigurationID\", \"MessageID\")", definition, StringComparison.Ordinal);
        }

        await using var transaction = await connection.BeginTransactionAsync();
        await using (var seed = connection.CreateCommand())
        {
            seed.Transaction = transaction;
            seed.CommandText = $"""
                INSERT INTO public."BusinessUnits"
                    ("ID", "BusinessUnitCode", "BusinessUnitName", "CreatedBy", "CreatedOn")
                VALUES ({Tenant}, 'G3-UP', 'Gate 3 mailbox identity', 'tests', now());
                INSERT INTO public."Email_Configurations"
                    ("ID", "BusinessUnitID", "ConfigurationName", "EmailAddress", "Protocol", "Host",
                     "Port", "Username", "Password", "UseSSL", "PollingInterval", "IsActive", "CreatedOn")
                VALUES ({FirstMailbox}, {Tenant}, 'Gate 3 inbox', 'gate3@example.test', 'IMAP', 'localhost',
                        993, 'gate3', 'test-only', true, 60, true, now()),
                       ({SecondMailbox}, {Tenant}, 'Gate 3 second inbox', 'gate3-second@example.test', 'IMAP',
                        'localhost', 993, 'gate3-second', 'test-only', true, 60, true, now());
                INSERT INTO public."EmailIngests"
                    ("ID", "MessageID", "EmailSubject", "FromEmail", "ToEmail", "EmailConfigurationID",
                     "ParseStatus", "CreatedOn")
                VALUES ({FirstMailbox}, '{ProviderMessageId}', 'Persisted intake', 'sender@example.test',
                        'gate3@example.test', {FirstMailbox}, 'PROCESSED', now());
                """;
            await seed.ExecuteNonQueryAsync();
        }

        // Same provider id, DIFFERENT mailbox: accepted. This is the case the composite key exists
        // for — a single-column unique index on "MessageID" would silently drop this intake.
        await using (var otherMailbox = connection.CreateCommand())
        {
            otherMailbox.Transaction = transaction;
            otherMailbox.CommandText = $"""
                INSERT INTO public."EmailIngests"
                    ("ID", "MessageID", "EmailSubject", "FromEmail", "ToEmail", "EmailConfigurationID",
                     "ParseStatus", "CreatedOn")
                VALUES ({SecondMailbox}, '{ProviderMessageId}', 'Same provider ID in another mailbox',
                        'sender@example.test', 'gate3-second@example.test', {SecondMailbox}, 'PROCESSED', now());
                """;
            Assert.Equal(1, await otherMailbox.ExecuteNonQueryAsync());
        }

        // Same provider id, SAME mailbox: rejected by the database, not by application code.
        await using (var duplicate = connection.CreateCommand())
        {
            duplicate.Transaction = transaction;
            duplicate.CommandText = $"""
                INSERT INTO public."EmailIngests"
                    ("ID", "MessageID", "EmailSubject", "FromEmail", "ToEmail", "EmailConfigurationID",
                     "ParseStatus", "CreatedOn")
                VALUES ({FirstMailbox + 10}, '{ProviderMessageId}', 'Replayed intake', 'sender@example.test',
                        'gate3@example.test', {FirstMailbox}, 'PROCESSED', now());
                """;
            var error = await Assert.ThrowsAsync<PostgresException>(() => duplicate.ExecuteNonQueryAsync());
            Assert.Equal(PostgresErrorCodes.UniqueViolation, error.SqlState);
        }

        await transaction.RollbackAsync();
    }
}
