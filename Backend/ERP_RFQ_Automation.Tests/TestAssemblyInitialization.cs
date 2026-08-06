using System.Runtime.CompilerServices;
using ERP_RFQ_Automation.Security;

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

        // Email_Configurations.Password is encrypted at the persistence boundary by
        // ProtectedSecretConverter, which resolves SecretProtection.Current on every read and
        // write. Production installs the protector in Program.cs from configuration; the test
        // assembly must install one too, or every fixture that seeds a mailbox configuration
        // would fail closed. A fixed key (rather than an ephemeral one) keeps ciphertext
        // readable across the multiple DbContext instances a single test uses.
        if (!SecretProtection.IsConfigured)
            SecretProtection.Use(new AesGcmSecretProtector(TestSecretProtectionKey));
    }

    /// <summary>A deterministic, non-secret 32-byte key. Test-only: it protects nothing real.</summary>
    public static byte[] TestSecretProtectionKey { get; } =
        Enumerable.Range(0, 32).Select(i => (byte)(i + 1)).ToArray();
}
