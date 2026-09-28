using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// PERF-12 (pilot audit 2026-09-28): every SQL statement was logged at Information — 30 MB of
/// log in 32 minutes locally, ~1,300 lines per Decide load, and the same appsettings.json ships
/// to Render. EF's command log now starts at Warning; anyone who wants the statements back sets
/// Logging__LogLevel__Microsoft.EntityFrameworkCore.Database.Command=Information.
/// </summary>
public sealed class SqlCommandLoggingDefaultTests
{
    private const string SqlCommands = "Microsoft.EntityFrameworkCore.Database.Command";

    [Fact]
    public void Sql_statements_are_not_logged_at_information_by_default()
    {
        var factory = Factory(new Dictionary<string, string?>());

        Assert.False(factory.CreateLogger(SqlCommands).IsEnabled(LogLevel.Information));
        Assert.True(factory.CreateLogger(SqlCommands).IsEnabled(LogLevel.Warning));
        // The application's own information logs are untouched.
        Assert.True(factory.CreateLogger("ERP_RFQ_Automation.Extraction.ExtractionWorker").IsEnabled(LogLevel.Information));
    }

    [Fact]
    public void An_explicit_setting_brings_the_statements_back()
    {
        var factory = Factory(new Dictionary<string, string?>
        {
            [$"Logging:LogLevel:{SqlCommands}"] = "Information",
        });

        Assert.True(factory.CreateLogger(SqlCommands).IsEnabled(LogLevel.Information));
    }

    private static ILoggerFactory Factory(Dictionary<string, string?> overrides)
    {
        var configuration = new ConfigurationBuilder()
            .AddJsonFile(Path.Combine(RepositoryRoot(), "Backend/ERP_RFQ_Automation/appsettings.json"))
            .AddInMemoryCollection(overrides)
            .Build();
        return new ServiceCollection()
            .AddLogging(logging =>
            {
                logging.AddConfiguration(configuration.GetSection("Logging"));
                logging.AddProvider(new EverythingProvider());
            })
            .BuildServiceProvider()
            .GetRequiredService<ILoggerFactory>();
    }

    /// <summary>A sink whose loggers accept every level, so only the configured filter decides.</summary>
    private sealed class EverythingProvider : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) => new EverythingLogger();
        public void Dispose() { }

        private sealed class EverythingLogger : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter) { }
        }
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, "Backend", "ERP_RFQ_Automation")))
                return directory.FullName;
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException("Could not locate the Nexora repository root.");
    }
}
