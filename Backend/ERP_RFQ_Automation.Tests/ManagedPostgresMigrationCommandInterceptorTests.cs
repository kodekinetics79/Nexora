using ERP_RFQ_Automation.Infrastructure;

namespace ERP_RFQ_Automation.Tests;

public sealed class ManagedPostgresMigrationCommandInterceptorTests
{
    [Fact]
    public void RewritesOnlyLegacyManagedOwnerMutation()
    {
        const string command = """
            DO $role$
            BEGIN
                EXECUTE format('ALTER ROLE %I NOINHERIT', current_user);
            END
            $role$;
            """;

        var rewritten = ManagedPostgresMigrationCommandInterceptor
            .RewriteLegacyManagedOwnerMutation(command);

        Assert.DoesNotContain("ALTER ROLE", rewritten, StringComparison.Ordinal);
        Assert.Contains("managed migration owner", rewritten, StringComparison.Ordinal);
    }

    [Fact]
    public void LeavesUnrelatedMigrationSqlUnchanged()
    {
        const string command = "ALTER TABLE public.\"Leads\" ENABLE ROW LEVEL SECURITY;";

        Assert.Equal(command, ManagedPostgresMigrationCommandInterceptor
            .RewriteLegacyManagedOwnerMutation(command));
    }
}
