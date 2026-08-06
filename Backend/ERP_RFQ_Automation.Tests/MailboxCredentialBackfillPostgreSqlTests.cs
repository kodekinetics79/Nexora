using ERP_RFQ_Automation.Security;
using ERP_RFQ_Automation.Tests.Support;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// The startup backfill that carries EXISTING cleartext mailbox credentials into the
/// AES-256-GCM envelope. It runs against the real migrated PostgreSQL schema because the
/// column width, the <c>v1:</c> prefix guard and the per-row failure handling are all
/// properties of the actual database, not of an in-memory approximation.
/// </summary>
[Collection(PostgreSqlIntegrationCollection.Name)]
public sealed class MailboxCredentialBackfillPostgreSqlTests(PostgreSqlTestDatabase database)
{
    private static readonly AesGcmSecretProtector Protector =
        new(TestAssemblyInitialization.TestSecretProtectionKey);

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task Backfill_encrypts_legacy_cleartext_and_is_idempotent_on_a_second_run()
    {
        await using var connection = await database.OpenConnectionAsync();
        var buId = await SeedBusinessUnitAsync(connection);

        const string legacyOne = "legacy-mailbox-password-1";
        const string legacyTwo = "legacy-mailbox-password-2";
        var alreadyProtected = Protector.Protect("already-protected-password");

        var idOne = await InsertConfigurationAsync(connection, buId, legacyOne);
        var idTwo = await InsertConfigurationAsync(connection, buId, legacyTwo);
        var idThree = await InsertConfigurationAsync(connection, buId, alreadyProtected);

        try
        {
            // The backfill is estate-wide by design, and this database is shared with the rest
            // of PostgreSqlIntegrationCollection, so a fixed tally would really be asserting how
            // many mailboxes OTHER tests happened to leave behind. Survey first and assert the
            // exact delta this pass must produce instead.
            var (totalBefore, cleartextBefore) = await SurveyAsync(connection);
            Assert.True(cleartextBefore >= 2, "The two rows seeded above must still be cleartext.");

            var first = await MailboxCredentialProtectionBackfill.RunAsync(
                connection, Protector, NullLogger.Instance);

            // Every cleartext row is converted and every already-enveloped row — including the
            // pre-protected row seeded above — is recognised and left exactly as it was.
            Assert.Equal(cleartextBefore, first.Protected);
            Assert.Equal(totalBefore - cleartextBefore, first.AlreadyProtected);
            Assert.Equal(0, first.Failed);

            var storedOne = await ReadRawPasswordAsync(connection, idOne);
            var storedTwo = await ReadRawPasswordAsync(connection, idTwo);
            var storedThree = await ReadRawPasswordAsync(connection, idThree);

            Assert.StartsWith("v1:", storedOne, StringComparison.Ordinal);
            Assert.StartsWith("v1:", storedTwo, StringComparison.Ordinal);
            Assert.DoesNotContain(legacyOne, storedOne, StringComparison.Ordinal);
            Assert.DoesNotContain(legacyTwo, storedTwo, StringComparison.Ordinal);
            Assert.Equal(alreadyProtected, storedThree);

            // The credential still decrypts to what the mail server expects.
            Assert.Equal(legacyOne, Protector.Unprotect(storedOne));
            Assert.Equal(legacyTwo, Protector.Unprotect(storedTwo));

            // A second pass — every boot re-runs this — must be a complete no-op, never a
            // double encryption.
            var second = await MailboxCredentialProtectionBackfill.RunAsync(
                connection, Protector, NullLogger.Instance);

            Assert.Equal(0, second.Protected);
            Assert.Equal(totalBefore, second.AlreadyProtected);
            Assert.Equal(0, second.Failed);
            Assert.Equal(storedOne, await ReadRawPasswordAsync(connection, idOne));
            Assert.Equal(legacyOne, Protector.Unprotect(await ReadRawPasswordAsync(connection, idOne)));
        }
        finally
        {
            await CleanupAsync(connection, buId);
        }
    }

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task Backfill_reports_a_failed_row_and_still_protects_the_others()
    {
        await using var connection = await database.OpenConnectionAsync();
        var buId = await SeedBusinessUnitAsync(connection);

        var goodId = await InsertConfigurationAsync(connection, buId, "good-row-password");
        var otherGoodId = await InsertConfigurationAsync(connection, buId, "another-good-password");
        var poisonId = await InsertConfigurationAsync(connection, buId, "poison-row-password");

        try
        {
            // A protector that throws for exactly one value models the "one bad row" case.
            // The pass must log it and keep going rather than abort and leave the rest of the
            // estate in cleartext.
            var flaky = new ThrowingProtector(Protector, failFor: "poison-row-password");

            // Shared fixture database (see the sibling test): the pass also sees whatever other
            // tests in this collection left behind, so the assertion is the delta — exactly one
            // failure, the poison row, and every other cleartext row converted regardless.
            var (totalBefore, cleartextBefore) = await SurveyAsync(connection);

            var result = await MailboxCredentialProtectionBackfill.RunAsync(
                connection, flaky, NullLogger.Instance);

            Assert.Equal(1, result.Failed);
            Assert.Equal(cleartextBefore - 1, result.Protected);
            Assert.Equal(totalBefore, result.Examined);

            Assert.StartsWith("v1:", await ReadRawPasswordAsync(connection, goodId), StringComparison.Ordinal);
            Assert.StartsWith("v1:", await ReadRawPasswordAsync(connection, otherGoodId), StringComparison.Ordinal);
            // The unconvertible row is left untouched rather than corrupted or blanked.
            Assert.Equal("poison-row-password", await ReadRawPasswordAsync(connection, poisonId));
        }
        finally
        {
            await CleanupAsync(connection, buId);
        }
    }

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task Migrated_column_accepts_a_protected_value_longer_than_the_old_255_limit()
    {
        await using var connection = await database.OpenConnectionAsync();
        var buId = await SeedBusinessUnitAsync(connection);

        // A 255-character plaintext — the maximum the column used to allow — becomes a much
        // longer envelope. This is the assertion the widening migration exists for.
        var longPassword = new string('p', 255);
        var id = await InsertConfigurationAsync(connection, buId, longPassword);

        try
        {
            var (_, cleartextBefore) = await SurveyAsync(connection);

            var result = await MailboxCredentialProtectionBackfill.RunAsync(
                connection, Protector, NullLogger.Instance);

            Assert.Equal(cleartextBefore, result.Protected);
            Assert.Equal(0, result.Failed);

            var stored = await ReadRawPasswordAsync(connection, id);
            Assert.True(stored.Length > 255, $"Envelope was {stored.Length} chars; expected > 255.");
            Assert.Equal(longPassword, Protector.Unprotect(stored));
        }
        finally
        {
            await CleanupAsync(connection, buId);
        }
    }

    /// <summary>Wraps a real protector but throws for one specific plaintext.</summary>
    private sealed class ThrowingProtector(ISecretProtector inner, string failFor) : ISecretProtector
    {
        public bool IsProtected(string? value) => inner.IsProtected(value);
        public string Unprotect(string protectedValue) => inner.Unprotect(protectedValue);
        public string Protect(string plaintext) => plaintext == failFor
            ? throw new InvalidOperationException("Simulated protection failure for this row.")
            : inner.Protect(plaintext);
    }

    // ---------- fixtures ----------

    private static async Task<long> SeedBusinessUnitAsync(NpgsqlConnection connection)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO public."BusinessUnits"
                ("BusinessUnitCode", "BusinessUnitName", "CreatedBy", "IsActive", "CreatedOn")
            VALUES (@code, @name, 'backfill-tests', true, now())
            RETURNING "ID";
            """;
        var suffix = Guid.NewGuid().ToString("N")[..12];
        command.Parameters.AddWithValue("code", $"BF-{suffix}");
        command.Parameters.AddWithValue("name", $"backfill-{suffix}");
        return (long)(await command.ExecuteScalarAsync())!;
    }

    private static async Task<long> InsertConfigurationAsync(
        NpgsqlConnection connection, long businessUnitId, string password)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO public."Email_Configurations"
                ("BusinessUnitID", "ConfigurationName", "EmailAddress", "Protocol", "Host",
                 "Port", "Username", "Password", "UseSSL", "PollingInterval", "IsActive", "CreatedOn")
            VALUES (@bu, @name, 'rfq@example.com', 'IMAP', 'imap.example.com',
                    993, 'rfq@example.com', @password, true, 300, true, now())
            RETURNING "ID";
            """;
        command.Parameters.AddWithValue("bu", businessUnitId);
        command.Parameters.AddWithValue("name", $"cfg-{Guid.NewGuid():N}");
        command.Parameters.AddWithValue("password", password);
        return (long)(await command.ExecuteScalarAsync())!;
    }

    /// <summary>
    /// Classifies every stored mailbox credential the way the backfill itself does — with
    /// <see cref="ISecretProtector.IsProtected"/>, not a <c>LIKE 'v1:%'</c> approximation — so a
    /// test can state the exact counts one pass must report over a database it does not own.
    /// </summary>
    private static async Task<(int Total, int Cleartext)> SurveyAsync(NpgsqlConnection connection)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            """SELECT "Password" FROM public."Email_Configurations" WHERE "Password" IS NOT NULL;""";
        await using var reader = await command.ExecuteReaderAsync();
        var total = 0;
        var cleartext = 0;
        while (await reader.ReadAsync())
        {
            total++;
            if (!Protector.IsProtected(reader.GetString(0))) cleartext++;
        }
        return (total, cleartext);
    }

    private static async Task<string> ReadRawPasswordAsync(NpgsqlConnection connection, long id)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """SELECT "Password" FROM public."Email_Configurations" WHERE "ID" = @id;""";
        command.Parameters.AddWithValue("id", id);
        return (string)(await command.ExecuteScalarAsync())!;
    }

    private static async Task CleanupAsync(NpgsqlConnection connection, long businessUnitId)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            DELETE FROM public."Email_Configurations" WHERE "BusinessUnitID" = @bu;
            -- The business_units_create_ai_policy trigger (AddAiGovernanceLedger) creates a
            -- default policy row for every business unit, so it must go before the parent.
            DELETE FROM public."AiProcessingPolicies" WHERE "BusinessUnitId" = @bu;
            DELETE FROM public."BusinessUnits" WHERE "ID" = @bu;
            """;
        command.Parameters.AddWithValue("bu", businessUnitId);
        await command.ExecuteNonQueryAsync();
    }
}
