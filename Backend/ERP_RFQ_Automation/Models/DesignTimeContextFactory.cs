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
        => new(new DbContextOptionsBuilder<ErpRfqAutomationContext>()
            .UseNpgsql("Host=localhost;Database=design_time_only;Username=design;Password=design")
            .Options);
}
