using ERP_RFQ_Automation.Models;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace ERP_RFQ_Automation.Infrastructure;

/// <summary>
/// Opens a real connection through the <b>exact DI registration the HTTP request path uses</b>,
/// before the host reports itself ready.
///
/// <para><b>Why this exists.</b> Migrations and the startup seeders open their own connections
/// and can both succeed while the request-path context cannot authenticate at all — the process
/// still starts, <c>/health</c> still answers, and the failure only surfaces as a 500 on the
/// first real login (<c>28P01</c>). A backend that cannot serve a single authenticated request is
/// not "started"; validating the same registration the controllers resolve is the only check that
/// proves it.</para>
///
/// <para><b>Fail-closed and off by default.</b> Gated on
/// <c>Database:ValidateRequestPathOnStartup</c> so production behaviour is unchanged unless a
/// deployment explicitly opts in. The E2E runner turns it on so a misconfiguration stops the
/// stack at startup instead of masquerading as a browser-test failure twenty minutes later.</para>
///
/// <para><b>No secrets.</b> Host, port, database and username are reported; the password is never
/// read, logged, or included in any message.</para>
/// </summary>
public static class RequestPathDatabaseValidator
{
    public static async Task ValidateAsync(IServiceProvider services, IConfiguration configuration)
    {
        if (!configuration.GetValue("Database:ValidateRequestPathOnStartup", false)) return;

        using var scope = services.CreateScope();
        var logger = scope.ServiceProvider.GetRequiredService<ILoggerFactory>()
            .CreateLogger("RequestPathDatabaseValidator");

        // Resolved from the container exactly as a controller would, so any interceptor,
        // connection string or data source the request path actually uses is what gets tested.
        var db = scope.ServiceProvider.GetRequiredService<ErpRfqAutomationContext>();
        var descriptor = Describe(db.Database.GetConnectionString());

        try
        {
            await db.Database.OpenConnectionAsync();
            try
            {
                await db.Database.ExecuteSqlRawAsync("SELECT 1");
            }
            finally
            {
                await db.Database.CloseConnectionAsync();
            }
        }
        catch (Exception ex)
        {
            var sqlState = ex is PostgresException pg ? pg.SqlState
                : (ex.InnerException as PostgresException)?.SqlState;
            throw new InvalidOperationException(
                "The request-path database context cannot open a connection, so no authenticated "
                + $"request could ever succeed. Context=ErpRfqAutomationContext {descriptor} "
                + $"ConfigurationKey=ConnectionStrings:DefaultConnection SQLSTATE={sqlState ?? "n/a"}. "
                + "Check that the running process was started with the intended connection — a stale "
                + "process left over from a previous run is the usual cause.", ex);
        }

        logger.LogInformation(
            "Request-path database validated. Context=ErpRfqAutomationContext {Descriptor}.", descriptor);
    }

    /// <summary>Host/port/database/username only. The password is never touched.</summary>
    private static string Describe(string? connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString)) return "Host=? Port=? Database=? Username=?";
        try
        {
            var b = new NpgsqlConnectionStringBuilder(connectionString);
            return $"Host={b.Host} Port={b.Port} Database={b.Database} Username={b.Username}";
        }
        catch
        {
            // Never echo an unparsable connection string back — it may contain the password.
            return "Host=<unparsable> Port=? Database=? Username=?";
        }
    }
}
