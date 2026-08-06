using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ERP_RFQ_Automation.DTOs.LeadDTOs;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Security;
using ERP_RFQ_Automation.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// Email_Configurations.Password holds a CUSTOMER MAILBOX credential — a corporate
/// Exchange/O365 password that the platform replays to the customer's IMAP/SMTP server. It
/// used to sit in the database in cleartext, readable by every role that bypasses RLS and by
/// anyone holding a backup. These tests pin the envelope encryption that closed that, and the
/// two properties that make it trustworthy: it fails closed on tampering, and the credential
/// never leaves through an API response.
/// </summary>
public class MailboxCredentialProtectionTests
{
    private static AesGcmSecretProtector Protector() =>
        new(TestAssemblyInitialization.TestSecretProtectionKey);

    // ---------- envelope properties ----------

    [Fact]
    public void Protect_RoundTripsBackToTheOriginalPlaintext()
    {
        var protector = Protector();
        const string password = "Corp0rate!Mailbox#Päss wörd";

        var envelope = protector.Protect(password);

        Assert.NotEqual(password, envelope);
        Assert.DoesNotContain(password, envelope, StringComparison.Ordinal);
        Assert.StartsWith("v1:", envelope, StringComparison.Ordinal);
        Assert.Equal(password, protector.Unprotect(envelope));
    }

    [Fact]
    public void Protect_ProducesDifferentCiphertextEachCallForTheSamePlaintext()
    {
        var protector = Protector();
        const string password = "shared-across-tenants";

        var first = protector.Protect(password);
        var second = protector.Protect(password);

        // A fresh nonce per value. Without this, two tenants using the same password would
        // store identical ciphertext and a read-only analyst could correlate credentials.
        Assert.NotEqual(first, second);
        Assert.Equal(password, protector.Unprotect(first));
        Assert.Equal(password, protector.Unprotect(second));
    }

    [Fact]
    public void Unprotect_TamperedCiphertextThrowsRatherThanReturningGarbage()
    {
        var protector = Protector();
        var envelope = protector.Protect("original-password");

        // Flip one bit in the ciphertext/tag segment.
        var parts = envelope.Split(':');
        var payload = Convert.FromBase64String(parts[2]);
        payload[0] ^= 0x01;
        var tampered = $"{parts[0]}:{parts[1]}:{Convert.ToBase64String(payload)}";

        Assert.Throws<AuthenticationTagMismatchException>(() => protector.Unprotect(tampered));
    }

    [Fact]
    public void Unprotect_TamperedNonceThrows()
    {
        var protector = Protector();
        var envelope = protector.Protect("original-password");

        var parts = envelope.Split(':');
        var nonce = Convert.FromBase64String(parts[1]);
        nonce[0] ^= 0x01;
        var tampered = $"{parts[0]}:{Convert.ToBase64String(nonce)}:{parts[2]}";

        Assert.Throws<AuthenticationTagMismatchException>(() => protector.Unprotect(tampered));
    }

    [Fact]
    public void Unprotect_WithADifferentKeyThrows()
    {
        var envelope = Protector().Protect("original-password");
        var otherKey = new AesGcmSecretProtector(Enumerable.Repeat((byte)9, 32).ToArray());

        Assert.Throws<AuthenticationTagMismatchException>(() => otherKey.Unprotect(envelope));
    }

    [Theory]
    [InlineData("v1:not-base64!:also-not-base64!")]
    [InlineData("v1:onlyonesegment")]
    [InlineData("v1:")]
    public void Unprotect_MalformedEnvelopeThrows(string malformed)
        => Assert.Throws<CryptographicException>(() => Protector().Unprotect(malformed));

    [Fact]
    public void Protect_IsIdempotentAndNeverDoubleEncrypts()
    {
        var protector = Protector();
        const string password = "mailbox-password";

        var once = protector.Protect(password);
        var twice = protector.Protect(once);

        // Re-running the backfill, or re-saving an unchanged entity, must not wrap the
        // envelope in a second envelope — that would silently corrupt the credential.
        Assert.Equal(once, twice);
        Assert.Equal(password, protector.Unprotect(twice));
    }

    [Fact]
    public void Unprotect_LegacyCleartextPassesThroughUnchanged()
    {
        // A row the backfill has not reached yet during a rolling deploy. Polling must keep
        // working rather than fail the mailbox.
        Assert.Equal("legacy-cleartext", Protector().Unprotect("legacy-cleartext"));
        Assert.False(Protector().IsProtected("legacy-cleartext"));
    }

    [Theory]
    [InlineData("v1:hello")]
    [InlineData("v1:my:password")]
    [InlineData("v1:not-base64!:also-not-base64!")]
    public void Protect_StillEncryptsAPlaintextThatMerelyLooksLikeAnEnvelope(string password)
    {
        var protector = Protector();

        // A prefix-only idempotence check would mistake these for ciphertext and store the
        // credential in cleartext — the exact defect this class exists to close.
        Assert.False(protector.IsProtected(password));

        var envelope = protector.Protect(password);

        Assert.NotEqual(password, envelope);
        Assert.True(protector.IsProtected(envelope));
        Assert.Equal(password, protector.Unprotect(envelope));
    }

    [Fact]
    public void Protect_HandlesEmptyPlaintext()
    {
        var protector = Protector();
        var envelope = protector.Protect(string.Empty);
        Assert.True(protector.IsProtected(envelope));
        Assert.Equal(string.Empty, protector.Unprotect(envelope));
    }

    [Fact]
    public void Constructor_RejectsKeysThatAreNot256Bits()
    {
        Assert.Throws<ArgumentException>(() => new AesGcmSecretProtector(new byte[16]));
        Assert.Throws<ArgumentException>(() => new AesGcmSecretProtector(new byte[64]));
    }

    // ---------- persistence boundary ----------

    [Fact]
    public async Task ValueConverter_StoresCiphertextAndMaterialisesPlaintext()
    {
        using var db = new TestDb();
        const string password = "imap-secret-value";

        await using (var seed = db.ContextFor(null))
        {
            Seed.BusinessUnit(seed, 1);
            var cfg = Seed.EmailConfig(seed, 10, 1);
            cfg.Password = password;
            await seed.SaveChangesAsync();
        }

        // What actually landed in the column, read beneath EF so the converter cannot
        // participate. This is the assertion that matters: the database holds ciphertext.
        await using (var raw = db.ContextFor(null))
        {
            var connection = raw.Database.GetDbConnection();
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT \"Password\" FROM \"Email_Configurations\" WHERE \"ID\" = 10;";
            if (connection.State != System.Data.ConnectionState.Open) await connection.OpenAsync();
            var stored = (string)(await command.ExecuteScalarAsync())!;

            Assert.NotEqual(password, stored);
            Assert.DoesNotContain(password, stored, StringComparison.Ordinal);
            Assert.StartsWith("v1:", stored, StringComparison.Ordinal);
        }

        // A separate context proves materialisation is transparent for the IMAP/SMTP call sites.
        await using (var read = db.ContextFor(null))
        {
            var cfg = await read.EmailConfigurations.SingleAsync(x => x.Id == 10);
            Assert.Equal(password, cfg.Password);
        }
    }

    [Fact]
    public async Task ValueConverter_RoundTripsUnderATenantScopedContextAndItsQueryFilter()
    {
        using var db = new TestDb();
        const string password = "tenant-scoped-secret";

        await using (var seed = db.ContextFor(null))
        {
            Seed.BusinessUnit(seed, 1);
            Seed.BusinessUnit(seed, 2);
            var cfg = Seed.EmailConfig(seed, 20, 1);
            cfg.Password = password;
            await seed.SaveChangesAsync();
        }

        // Protection must compose with the tenant query filter, not bypass or break it.
        await using (var tenant1 = db.ContextFor(1))
            Assert.Equal(password, (await tenant1.EmailConfigurations.SingleAsync(x => x.Id == 20)).Password);

        await using (var tenant2 = db.ContextFor(2))
            Assert.Null(await tenant2.EmailConfigurations.SingleOrDefaultAsync(x => x.Id == 20));
    }

    [Fact]
    public async Task ValueConverter_DoesNotDoubleEncryptOnResave()
    {
        using var db = new TestDb();
        const string password = "resave-me";

        await using (var seed = db.ContextFor(null))
        {
            Seed.BusinessUnit(seed, 1);
            var cfg = Seed.EmailConfig(seed, 30, 1);
            cfg.Password = password;
            await seed.SaveChangesAsync();
        }

        await using (var touch = db.ContextFor(null))
        {
            var cfg = await touch.EmailConfigurations.SingleAsync(x => x.Id == 30);
            cfg.PollingInterval = 120;      // unrelated edit
            cfg.Password = cfg.Password;    // re-assign the materialised plaintext
            await touch.SaveChangesAsync();
        }

        await using (var read = db.ContextFor(null))
            Assert.Equal(password, (await read.EmailConfigurations.SingleAsync(x => x.Id == 30)).Password);
    }

    // ---------- startup fails closed ----------

    private static IConfiguration ConfigWith(string? key)
    {
        var values = new Dictionary<string, string?>();
        if (key is not null) values[SecretProtection.KeyConfigurationPath] = key;
        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }

    [Theory]
    [InlineData(null)]                              // absent
    [InlineData("")]                                // blank
    [InlineData("   ")]                             // whitespace
    [InlineData("__SECRET_PROTECTION_KEY__")]       // untemplated placeholder
    public void Startup_FailsClosedOutsideDevelopmentWhenTheKeyIsMissing(string? key)
    {
        var error = Assert.Throws<InvalidOperationException>(() =>
            SecretProtection.CreateFromConfiguration(ConfigWith(key), isDevelopment: false, out _));

        Assert.Contains(SecretProtection.KeyConfigurationPath, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Startup_FailsClosedOutsideDevelopmentWhenTheKeyIsTooShort()
    {
        var sixteenBytes = Convert.ToBase64String(new byte[16]);

        var error = Assert.Throws<InvalidOperationException>(() =>
            SecretProtection.CreateFromConfiguration(ConfigWith(sixteenBytes), isDevelopment: false, out _));

        Assert.Contains("32 bytes", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Startup_FailsClosedOnANonBase64OrAllZeroKey()
    {
        Assert.Throws<InvalidOperationException>(() =>
            SecretProtection.CreateFromConfiguration(ConfigWith("not base64 at all !!"), false, out _));

        // The shape a badly templated deploy produces; it must not pass for key material.
        Assert.Throws<InvalidOperationException>(() =>
            SecretProtection.CreateFromConfiguration(
                ConfigWith(Convert.ToBase64String(new byte[32])), false, out _));
    }

    [Fact]
    public void Startup_AcceptsAValidKeyAndDoesNotReportItEphemeral()
    {
        var key = Convert.ToBase64String(TestAssemblyInitialization.TestSecretProtectionKey);

        var protector = SecretProtection.CreateFromConfiguration(ConfigWith(key), false, out var ephemeral);

        Assert.False(ephemeral);
        Assert.Equal("round-trip", protector.Unprotect(protector.Protect("round-trip")));
    }

    [Fact]
    public void Startup_DevelopmentFallsBackToAnEphemeralKeyThatIsFlaggedInsecure()
    {
        var protector = SecretProtection.CreateFromConfiguration(ConfigWith(null), isDevelopment: true, out var ephemeral);

        // Allowed ONLY here, and the flag is what makes Program.cs log it as insecure.
        Assert.True(ephemeral);
        Assert.Equal("dev", protector.Unprotect(protector.Protect("dev")));
    }

    // ---------- the credential never leaves ----------

    [Fact]
    public void EmailConfigurationDropdownDto_CarriesNoCredentialField()
    {
        var names = typeof(EmailConfigurationDropdownDTO)
            .GetProperties()
            .Select(p => p.Name)
            .ToArray();

        Assert.DoesNotContain(names, n => n.Contains("password", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(names, n => n.Contains("secret", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(names, n => n.Contains("credential", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void SerialisingTheEntityNeverEmitsThePassword()
    {
        // Defence in depth: no endpoint returns this entity today, but if one ever did — or
        // reached it through EmailIngest.EmailConfiguration / BusinessUnit.EmailConfigurations
        // — the credential still must not reach the wire.
        var configuration = new EmailConfiguration
        {
            Id = 1,
            BusinessUnitId = 1,
            ConfigurationName = "primary",
            EmailAddress = "rfq@customer.example.com",
            Protocol = "IMAP",
            Host = "outlook.office365.com",
            Port = 993,
            Username = "rfq@customer.example.com",
            Password = "TOP-SECRET-MAILBOX-PASSWORD",
        };

        var json = JsonSerializer.Serialize(configuration);

        Assert.DoesNotContain("TOP-SECRET-MAILBOX-PASSWORD", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"Password\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"password\"", json, StringComparison.Ordinal);
        // The non-secret fields still serialise, so the guard is targeted rather than blunt.
        Assert.Contains("outlook.office365.com", json, StringComparison.Ordinal);
    }

    [Fact]
    public void NoControllerActionReturnsTheEmailConfigurationEntity()
    {
        // A structural guard: the leak this closes would reappear the moment somebody wrote
        // `return Ok(configuration);` in a future mailbox-admin endpoint.
        var offenders = typeof(EmailConfiguration).Assembly
            .GetTypes()
            .Where(t => typeof(Microsoft.AspNetCore.Mvc.ControllerBase).IsAssignableFrom(t) && !t.IsAbstract)
            .SelectMany(t => t.GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.DeclaredOnly))
            .Select(m => new { Method = m, Returned = UnwrapReturnType(m.ReturnType) })
            .Where(x => x.Returned == typeof(EmailConfiguration) ||
                        (x.Returned.IsGenericType &&
                         x.Returned.GetGenericArguments().Contains(typeof(EmailConfiguration))))
            .Select(x => $"{x.Method.DeclaringType!.Name}.{x.Method.Name}")
            .ToArray();

        Assert.Empty(offenders);
    }

    private static Type UnwrapReturnType(Type type)
    {
        // Task<T> / ActionResult<T> / Task<ActionResult<T>> → T
        while (type.IsGenericType &&
               (type.GetGenericTypeDefinition() == typeof(Task<>) ||
                type.GetGenericTypeDefinition() == typeof(Microsoft.AspNetCore.Mvc.ActionResult<>)))
            type = type.GetGenericArguments()[0];
        return type;
    }
}
