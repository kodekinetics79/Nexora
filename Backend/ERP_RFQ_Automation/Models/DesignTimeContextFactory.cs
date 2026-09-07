using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace ERP_RFQ_Automation.Models;

/// <summary>
/// Lets <c>dotnet ef migrations add</c> build the model without booting the host, whose
/// startup validation demands the full production secret set (connection strings, JWT keys,
/// platform keys) that a schema-only operation never uses. Nothing here can reach a real
/// database: the connection string is a placeholder and <c>migrations add</c> never connects.
/// </summary>
public sealed class DesignTimeContextFactory : IDesignTimeDbContextFactory<ErpRfqAutomationContext>
{
    static DesignTimeContextFactory()
    {
        // THIS SWITCH IS NOT OPTIONAL, AND ITS ABSENCE HERE WAS A TRAP.
        //
        // Program.cs sets Npgsql.EnableLegacyTimestampBehavior before any data source is built,
        // so at RUNTIME every DateTime maps to `timestamp without time zone`. Design time never
        // executes Program.cs — `dotnet ef` constructs the context through this factory — so the
        // model EF compared against the snapshot used the MODERN mapping, in which the same
        // DateTime is `timestamp with time zone`.
        //
        // The consequence was not a warning. `dotnet ef migrations add` for a one-column change
        // scaffolded 390 KB of AlterColumn calls rewriting the timestamp type of essentially
        // every dated column in the schema — a full-table rewrite of the production database,
        // hidden at the top of a migration whose name said it was about something else. The
        // repo's one-migration-per-branch rule and a careful reviewer are the only things that
        // stood between that and a deploy.
        //
        // A static constructor rather than a line in CreateDbContext because the switch must be
        // set before Npgsql's type mapper is first touched, and the mapper is a process-wide
        // singleton: setting it later is silently too late.
        AppContext.SetSwitch("Npgsql.EnableLegacyTimestampBehavior", true);
    }

    public ErpRfqAutomationContext CreateDbContext(string[] args)
        => new(new DbContextOptionsBuilder<ErpRfqAutomationContext>()
            .UseNpgsql("Host=localhost;Database=design_time_only;Username=design;Password=design")
            .Options);
}
