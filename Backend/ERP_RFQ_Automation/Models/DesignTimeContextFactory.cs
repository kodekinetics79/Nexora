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
    public ErpRfqAutomationContext CreateDbContext(string[] args)
    {
        // The runtime sets this switch first thing in Program.cs, and it decides how every DateTime
        // column is typed: with it, `timestamp without time zone` (what every existing table is);
        // without it, `timestamp with time zone`. The design-time path never runs Program.cs, so
        // without the same switch here `migrations add` reads the whole schema as drifted and
        // emits an AlterColumn for every DateTime column in the database (1,136 of them on
        // 2026-09-16) before it gets to the change that was asked for.
        AppContext.SetSwitch("Npgsql.EnableLegacyTimestampBehavior", true);
        return new(new DbContextOptionsBuilder<ErpRfqAutomationContext>()
            .UseNpgsql("Host=localhost;Database=design_time_only;Username=design;Password=design")
            .Options);
    }
}
