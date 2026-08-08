using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace ERP_RFQ_Automation.Platform.Auth;

internal static class PlatformTotp
{
    internal const int SecretBytes = 20;
    internal const long StepSeconds = 30;

    internal static string GenerateSecret() => Base32Encode(RandomNumberGenerator.GetBytes(SecretBytes));

    internal static bool TryVerify(string secret, string? code, DateTime utcNow, long? lastAcceptedStep,
        out long acceptedStep)
    {
        acceptedStep = 0;
        if (string.IsNullOrWhiteSpace(code) || code.Length != 6 || !code.All(char.IsAsciiDigit))
            return false;

        var supplied = Encoding.ASCII.GetBytes(code);
        var currentStep = new DateTimeOffset(utcNow).ToUnixTimeSeconds() / StepSeconds;
        for (var offset = -1; offset <= 1; offset++)
        {
            var step = currentStep + offset;
            if (lastAcceptedStep is long previous && step <= previous) continue;
            var expected = Encoding.ASCII.GetBytes(CodeAt(secret, step));
            if (CryptographicOperations.FixedTimeEquals(supplied, expected))
            {
                acceptedStep = step;
                return true;
            }
        }
        return false;
    }

    internal static string CodeAt(string secret, long step)
    {
        Span<byte> counter = stackalloc byte[8];
        BinaryPrimitives.WriteInt64BigEndian(counter, step);
        using var hmac = new HMACSHA1(Base32Decode(secret));
        var hash = hmac.ComputeHash(counter.ToArray());
        var offset = hash[^1] & 0x0f;
        var binary = ((hash[offset] & 0x7f) << 24)
                     | (hash[offset + 1] << 16)
                     | (hash[offset + 2] << 8)
                     | hash[offset + 3];
        return (binary % 1_000_000).ToString("D6");
    }

    internal static string Base32Encode(ReadOnlySpan<byte> bytes)
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        var output = new StringBuilder((bytes.Length * 8 + 4) / 5);
        var buffer = 0;
        var bits = 0;
        foreach (var value in bytes)
        {
            buffer = (buffer << 8) | value;
            bits += 8;
            while (bits >= 5)
            {
                bits -= 5;
                output.Append(alphabet[(buffer >> bits) & 31]);
            }
        }
        if (bits > 0) output.Append(alphabet[(buffer << (5 - bits)) & 31]);
        return output.ToString();
    }

    private static byte[] Base32Decode(string value)
    {
        var output = new List<byte>(value.Length * 5 / 8);
        var buffer = 0;
        var bits = 0;
        foreach (var character in value.Trim().ToUpperInvariant())
        {
            var index = character switch
            {
                >= 'A' and <= 'Z' => character - 'A',
                >= '2' and <= '7' => character - '2' + 26,
                _ => throw new FormatException("The TOTP secret is invalid.")
            };
            buffer = (buffer << 5) | index;
            bits += 5;
            if (bits < 8) continue;
            bits -= 8;
            output.Add((byte)((buffer >> bits) & 255));
        }
        return output.ToArray();
    }
}
