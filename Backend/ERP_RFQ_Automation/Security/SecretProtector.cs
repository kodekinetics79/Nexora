using System.Security.Cryptography;
using System.Text;

namespace ERP_RFQ_Automation.Security;

/// <summary>
/// Reversible protection for secrets that the platform must be able to REPLAY, not merely
/// verify — customer mailbox IMAP/SMTP credentials being the motivating case. A login
/// password is one-way hashed (BCrypt) because we only ever compare it; a mailbox password
/// must be presented verbatim to the customer's Exchange/O365 server on every poll, so it
/// has to be recoverable. Recoverable must not mean readable: before this abstraction the
/// credential sat in cleartext in <c>Email_Configurations.Password</c>, visible to every
/// role that bypasses RLS (<c>nexora_identity_app</c>, <c>nexora_pipeline_app</c>) and to
/// anyone holding a database backup.
///
/// The protected form is self-describing and versioned so the scheme can be rotated later
/// without a flag day: <c>v1:&lt;base64 nonce&gt;:&lt;base64 ciphertext||tag&gt;</c>.
/// </summary>
public interface ISecretProtector
{
    /// <summary>Encrypts <paramref name="plaintext"/> into the versioned envelope. Already
    /// protected input is returned unchanged so repeated passes (a re-run backfill, a
    /// re-saved entity) can never double-encrypt.</summary>
    string Protect(string plaintext);

    /// <summary>Recovers the plaintext. Legacy (unprotected) input is passed through
    /// unchanged so a rolling deploy keeps polling mailboxes whose rows the backfill has
    /// not reached yet. A value that CLAIMS to be protected but fails authentication
    /// throws — it never degrades to garbage.</summary>
    string Unprotect(string protectedValue);

    /// <summary>True when the value already carries a known protection envelope.</summary>
    bool IsProtected(string? value);
}

/// <summary>
/// AES-256-GCM with a fresh 96-bit nonce per value. GCM is authenticated: a flipped bit in
/// the ciphertext, the tag, or the nonce fails the tag check and throws, so a tampered row
/// surfaces as an error rather than as a silently wrong password sent to a customer's mail
/// server. The random per-value nonce also means two mailboxes sharing a password do not
/// share a ciphertext, denying an analyst with read-only SQL access the ability to
/// correlate credentials across tenants.
/// </summary>
public sealed class AesGcmSecretProtector : ISecretProtector
{
    /// <summary>Envelope version marker. Bump alongside a new algorithm; <see cref="Unprotect"/>
    /// keeps understanding older markers so rotation can be gradual.</summary>
    public const string VersionPrefix = "v1:";

    /// <summary>Required key size. AES-256 — matched to the sensitivity of a corporate
    /// mailbox credential.</summary>
    public const int KeySizeBytes = 32;

    private const int NonceSizeBytes = 12;  // 96-bit nonce: the GCM-recommended size.
    private const int TagSizeBytes = 16;    // 128-bit authentication tag: GCM maximum.

    private readonly byte[] _key;

    public AesGcmSecretProtector(byte[] key)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (key.Length != KeySizeBytes)
            throw new ArgumentException(
                $"Secret protection key must be exactly {KeySizeBytes} bytes (AES-256).", nameof(key));
        _key = (byte[])key.Clone();
    }

    /// <summary>Generates a fresh random key. Used by the Development-only ephemeral
    /// fallback and by tests; never a production key source, because a key that dies with
    /// the process makes every stored credential unreadable after a restart.</summary>
    public static AesGcmSecretProtector CreateEphemeral()
        => new(RandomNumberGenerator.GetBytes(KeySizeBytes));

    /// <summary>
    /// True only for a STRUCTURALLY VALID envelope, not merely anything starting with the
    /// version prefix. The distinction matters: a mailbox password could legitimately begin
    /// with the literal text "v1:", and a prefix-only test would make
    /// <see cref="Protect"/> mistake it for an already-encrypted value and store the
    /// credential in cleartext — reintroducing the exact defect this class exists to fix.
    /// </summary>
    public bool IsProtected(string? value)
        => value is not null && TryParseEnvelope(value, out _, out _);

    /// <summary>Parses the envelope. Returns false for anything that is not a well-formed
    /// v1 value, including plain text that merely starts with the prefix.</summary>
    private static bool TryParseEnvelope(string value, out byte[] nonce, out byte[] payload)
    {
        nonce = payload = Array.Empty<byte>();
        if (!value.StartsWith(VersionPrefix, StringComparison.Ordinal)) return false;

        var body = value.AsSpan(VersionPrefix.Length);
        var separator = body.IndexOf(':');
        if (separator <= 0 || separator == body.Length - 1) return false;

        try
        {
            nonce = Convert.FromBase64String(body[..separator].ToString());
            payload = Convert.FromBase64String(body[(separator + 1)..].ToString());
        }
        catch (FormatException)
        {
            return false;
        }

        return nonce.Length == NonceSizeBytes && payload.Length >= TagSizeBytes;
    }

    public string Protect(string plaintext)
    {
        ArgumentNullException.ThrowIfNull(plaintext);
        // Idempotence is a correctness requirement, not a convenience: the startup backfill
        // may run on every boot and EF re-saves unchanged entities. Double-encrypting would
        // silently corrupt the credential.
        if (IsProtected(plaintext)) return plaintext;

        var plaintextBytes = Encoding.UTF8.GetBytes(plaintext);
        var nonce = RandomNumberGenerator.GetBytes(NonceSizeBytes);
        var ciphertext = new byte[plaintextBytes.Length];
        var tag = new byte[TagSizeBytes];

        using (var aes = new AesGcm(_key, TagSizeBytes))
            aes.Encrypt(nonce, plaintextBytes, ciphertext, tag);

        // Tag is appended to the ciphertext so the envelope stays three fields wide
        // regardless of algorithm.
        var payload = new byte[ciphertext.Length + TagSizeBytes];
        ciphertext.CopyTo(payload, 0);
        tag.CopyTo(payload, ciphertext.Length);

        return string.Concat(
            VersionPrefix, Convert.ToBase64String(nonce), ":", Convert.ToBase64String(payload));
    }

    public string Unprotect(string protectedValue)
    {
        ArgumentNullException.ThrowIfNull(protectedValue);

        if (!TryParseEnvelope(protectedValue, out var nonce, out var payload))
        {
            // Claims to be an envelope but is not well formed: fail closed rather than hand
            // back a value that would be replayed to a mail server as a password.
            if (protectedValue.StartsWith(VersionPrefix, StringComparison.Ordinal))
                throw new CryptographicException("Protected secret envelope is malformed.");

            // No prefix => a pre-backfill cleartext row. Returning it keeps a half-migrated
            // database polling; the backfill removes this case permanently.
            return protectedValue;
        }

        var ciphertext = payload.AsSpan(0, payload.Length - TagSizeBytes);
        var tag = payload.AsSpan(payload.Length - TagSizeBytes);
        var plaintextBytes = new byte[ciphertext.Length];

        // AuthenticationTagMismatchException (a CryptographicException) on tamper or wrong
        // key. Deliberately not caught: failing closed beats handing a corrupted credential
        // to a mail server and tripping the customer's account lockout.
        using (var aes = new AesGcm(_key, TagSizeBytes))
            aes.Decrypt(nonce, ciphertext, tag, plaintextBytes);

        return Encoding.UTF8.GetString(plaintextBytes);
    }
}
