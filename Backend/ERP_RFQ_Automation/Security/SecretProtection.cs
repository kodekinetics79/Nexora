using System.Security.Cryptography;

namespace ERP_RFQ_Automation.Security;

/// <summary>
/// Process-wide access point for the active <see cref="ISecretProtector"/>, plus the
/// fail-closed startup validation of its key material.
///
/// Why a process-wide holder rather than plain DI: the protection is applied by an EF Core
/// <see cref="ProtectedSecretConverter"/>, and EF builds and CACHES the model once per
/// context type — value converters therefore cannot receive scoped services. The converter
/// resolves <see cref="Current"/> at each conversion instead of capturing an instance at
/// model-build time, which also keeps design-time model building (migrations) working with
/// no key present at all. There is exactly one key per process, so a single holder is an
/// honest representation rather than hidden global state.
/// </summary>
public static class SecretProtection
{
    /// <summary>Configuration path for the base64-encoded 32-byte AES-256 key.</summary>
    public const string KeyConfigurationPath = "Security:SecretProtectionKey";

    private static ISecretProtector? _current;

    /// <summary>True when the active protector was generated on the fly because no key was
    /// configured. Only ever possible in Development; surfaced so startup can log it loudly.</summary>
    public static bool UsingEphemeralDevelopmentKey { get; private set; }

    /// <summary>The active protector. Throws rather than silently persisting cleartext if
    /// nothing has been installed — a missing protector is a configuration bug, and writing
    /// the credential unprotected is the exact defect this exists to close.</summary>
    public static ISecretProtector Current
        => _current ?? throw new InvalidOperationException(
            $"No ISecretProtector has been installed. Configure '{KeyConfigurationPath}' and call " +
            $"{nameof(SecretProtection)}.{nameof(Use)} during startup before touching protected columns.");

    /// <summary>True when a protector is installed; lets diagnostics ask without throwing.</summary>
    public static bool IsConfigured => Volatile.Read(ref _current) is not null;

    /// <summary>Installs the protector for this process. Called once from startup, and from
    /// tests that need a deterministic key.</summary>
    public static void Use(ISecretProtector protector, bool ephemeral = false)
    {
        ArgumentNullException.ThrowIfNull(protector);
        Volatile.Write(ref _current, protector);
        UsingEphemeralDevelopmentKey = ephemeral;
    }

    /// <summary>
    /// Builds the protector from configuration, mirroring how <c>Jwt:Key</c> and the
    /// CommercialFinance HMAC secrets are validated at startup: outside Development a
    /// missing, placeholder, or undersized key throws and the deploy stops, rather than
    /// booting an API that writes mailbox credentials in cleartext.
    ///
    /// In Development only, an ephemeral random key is generated so the demo runs with no
    /// setup. That key dies with the process — anything protected under it is unreadable
    /// after a restart — which is exactly why it is refused everywhere else.
    /// </summary>
    /// <param name="isDevelopment">Pass <c>builder.Environment.IsDevelopment()</c>.</param>
    public static ISecretProtector CreateFromConfiguration(
        IConfiguration configuration, bool isDevelopment, out bool ephemeral)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var configuredKey = configuration[KeyConfigurationPath];
        ephemeral = false;

        var missing = string.IsNullOrWhiteSpace(configuredKey) ||
                      configuredKey.Contains("__SECRET_PROTECTION_KEY__", StringComparison.Ordinal);

        if (missing)
        {
            if (!isDevelopment)
                throw new InvalidOperationException(
                    $"{KeyConfigurationPath} is missing or still contains a placeholder. It must be a " +
                    $"base64-encoded {AesGcmSecretProtector.KeySizeBytes}-byte key so stored mailbox " +
                    "credentials can be encrypted at rest. Generate one with: " +
                    "openssl rand -base64 32");

            ephemeral = true;
            return AesGcmSecretProtector.CreateEphemeral();
        }

        byte[] key;
        try
        {
            key = Convert.FromBase64String(configuredKey!.Trim());
        }
        catch (FormatException exception)
        {
            throw new InvalidOperationException(
                $"{KeyConfigurationPath} is not valid base64. Generate one with: openssl rand -base64 32",
                exception);
        }

        if (key.Length != AesGcmSecretProtector.KeySizeBytes)
            throw new InvalidOperationException(
                $"{KeyConfigurationPath} decodes to {key.Length} bytes; exactly " +
                $"{AesGcmSecretProtector.KeySizeBytes} bytes (AES-256) are required. " +
                "Generate one with: openssl rand -base64 32");

        // Reject an all-zero key: it is the shape a badly templated deploy produces
        // (base64 of a zero-filled buffer) and it must not pass for a real secret.
        if (CryptographicOperations.FixedTimeEquals(key, new byte[AesGcmSecretProtector.KeySizeBytes]))
            throw new InvalidOperationException(
                $"{KeyConfigurationPath} is an all-zero key and is not usable as key material.");

        return new AesGcmSecretProtector(key);
    }
}
