using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace ERP_RFQ_Automation.Security;

/// <summary>
/// EF Core value converter that protects a secret column at the PERSISTENCE boundary.
/// Applying it in <c>OnModelCreating</c> means every read and write goes through it — the
/// existing IMAP/SMTP call sites keep reading <c>configuration.Password</c> and get
/// plaintext, while the database only ever holds the AES-256-GCM envelope. Nothing is
/// scattered through the services, so there is no code path that can forget to encrypt.
///
/// The protector is resolved from <see cref="SecretProtection.Current"/> at each conversion
/// rather than captured in the constructor. That matters twice over: EF caches the model
/// per context type (so a captured instance would outlive its scope), and design-time model
/// building for <c>dotnet ef migrations</c> must succeed with no key configured at all —
/// which it does, because building the expression tree never invokes it.
/// </summary>
public sealed class ProtectedSecretConverter : ValueConverter<string, string>
{
    public ProtectedSecretConverter()
        : base(
            plaintext => SecretProtection.Current.Protect(plaintext),
            stored => SecretProtection.Current.Unprotect(stored))
    {
    }

    /// <summary>Store width for a protected column. The envelope is
    /// <c>v1:</c> + base64(12-byte nonce) + <c>:</c> + base64(ciphertext + 16-byte tag),
    /// roughly 1.4x the plaintext byte length plus 36 characters of framing. 2048 leaves
    /// generous headroom over the 255-character plaintext the column previously allowed,
    /// even for multi-byte UTF-8 passwords.</summary>
    public const int ProtectedColumnMaxLength = 2048;
}
