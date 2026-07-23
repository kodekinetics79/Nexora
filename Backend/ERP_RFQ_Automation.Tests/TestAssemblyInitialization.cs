using System.Runtime.CompilerServices;

namespace ERP_RFQ_Automation.Tests;

public static class TestAssemblyInitialization
{
    [ModuleInitializer]
    public static void Initialize()
    {
        // Program.cs applies this before any Npgsql model is built. A module initializer
        // prevents parallel tests from populating EF's model cache with different timestamp
        // semantics before the PostgreSQL fixture starts.
        AppContext.SetSwitch("Npgsql.EnableLegacyTimestampBehavior", true);
    }
}
