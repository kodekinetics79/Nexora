using Npgsql;

namespace ERP_RFQ_Automation.Security;

/// <summary>Counts from one backfill pass. No credential material, protected or otherwise,
/// is ever carried here — only tallies, so the result is safe to log.</summary>
public sealed record MailboxCredentialProtectionResult(int Protected, int AlreadyProtected, int Failed)
{
    public int Examined => Protected + AlreadyProtected + Failed;
}

/// <summary>
/// One-way migration of legacy cleartext values in <c>Email_Configurations.Password</c> into
/// the AES-256-GCM envelope.
///
/// WHY THIS IS NOT AN EF DATA MIGRATION: the encryption key is supplied through application
/// configuration (<c>Security:SecretProtectionKey</c>) and is deliberately absent from the
/// database. A <c>migrationBuilder.Sql</c> block runs inside the database and has no access
/// to it, and putting the key where SQL could reach it would defeat the entire exercise —
/// the threat model is precisely an actor who can read the database. So the schema change
/// (widening the column for ciphertext) is an EF migration, and the DATA change is this
/// guarded startup pass, which runs where the key is known to exist and to have passed
/// validation.
///
/// It is idempotent: rows already carrying the version prefix are counted and skipped, so
/// every boot re-running it is a no-op after the first. It is also per-row fault tolerant —
/// one unconvertible row must not abort the pass and leave the rest of the estate in
/// cleartext.
/// </summary>
public static class MailboxCredentialProtectionBackfill
{
    /// <summary>Runs one pass. Never throws for per-row problems; a connection-level failure
    /// is logged and swallowed so a transient database hiccup cannot block startup, leaving
    /// the next boot to retry.</summary>
    public static async Task<MailboxCredentialProtectionResult> RunAsync(
        string connectionString,
        ISecretProtector protector,
        ILogger logger,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(protector);
        ArgumentNullException.ThrowIfNull(logger);

        try
        {
            await using var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync(cancellationToken);
            return await RunAsync(connection, protector, logger, cancellationToken);
        }
        catch (Exception exception)
        {
            // Message only — an exception from Npgsql can echo parameter values, and the
            // parameters here are credentials.
            logger.LogError(
                "Mailbox credential protection backfill could not run: {Reason}. Stored credentials may " +
                "remain in cleartext; the next start will retry.", exception.Message);
            return new MailboxCredentialProtectionResult(0, 0, 0);
        }
    }

    /// <summary>Overload against an already-open connection, so tests can drive it inside a
    /// disposable PostgreSQL container.</summary>
    public static async Task<MailboxCredentialProtectionResult> RunAsync(
        NpgsqlConnection connection,
        ISecretProtector protector,
        ILogger logger,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(protector);
        ArgumentNullException.ThrowIfNull(logger);

        // Materialise first: the reader and the UPDATEs share one connection, and Npgsql
        // will not allow an update while a reader is open.
        var rows = new List<(long Id, string Password)>();
        await using (var select = connection.CreateCommand())
        {
            select.CommandText =
                """SELECT "ID", "Password" FROM public."Email_Configurations" WHERE "Password" IS NOT NULL;""";
            await using var reader = await select.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                rows.Add((reader.GetInt64(0), reader.GetString(1)));
        }

        var protectedCount = 0;
        var alreadyProtected = 0;
        var failed = 0;

        foreach (var (id, password) in rows)
        {
            if (protector.IsProtected(password))
            {
                alreadyProtected++;
                continue;
            }

            try
            {
                var envelope = protector.Protect(password);
                await using var update = connection.CreateCommand();
                // Compare-and-swap on the exact value we read. This makes a concurrent second
                // instance a no-op rather than a double-encryption, and — unlike a coarse
                // NOT LIKE 'v1:%' guard — it still converts a cleartext password that happens
                // to start with the literal text "v1:".
                update.CommandText =
                    """
                    UPDATE public."Email_Configurations"
                    SET "Password" = @protected
                    WHERE "ID" = @id AND "Password" = @expected;
                    """;
                update.Parameters.AddWithValue("protected", envelope);
                update.Parameters.AddWithValue("expected", password);
                update.Parameters.AddWithValue("id", id);
                var affected = await update.ExecuteNonQueryAsync(cancellationToken);
                if (affected > 0) protectedCount++; else alreadyProtected++;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // Log and continue: a single bad row (e.g. a column not yet widened by the
                // schema migration) must not leave every other tenant's credential in
                // cleartext. The ID is safe to log; the value is not.
                failed++;
                logger.LogError(
                    "Could not protect the stored credential for Email_Configurations.ID={ConfigurationId}: " +
                    "{Reason}. Continuing with the remaining rows.", id, exception.Message);
            }
        }

        var result = new MailboxCredentialProtectionResult(protectedCount, alreadyProtected, failed);
        if (result.Examined > 0)
            logger.LogInformation(
                "Mailbox credential protection backfill examined {Examined} configuration(s): " +
                "{Protected} newly encrypted, {AlreadyProtected} already protected, {Failed} failed.",
                result.Examined, result.Protected, result.AlreadyProtected, result.Failed);

        return result;
    }
}
