using System.Reflection;
using System.Runtime.CompilerServices;
using ERP_RFQ_Automation.Platform.Onboarding;
using ERP_RFQ_Automation.Platform.Provisioning;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// RED TEAM. Which types can print a live credential if somebody ever interpolates one.
///
/// <para>A C# <c>record</c> gets a compiler-generated <c>ToString()</c> that prints EVERY member,
/// so a record holding a raw token or a plaintext password turns a single
/// <c>_log.LogInformation("... {Result}", result)</c> — or an exception message built from it —
/// into a credential in the log aggregator. <see cref="IssuedTenantAdminInvitation"/> already knows
/// this and overrides <c>ToString()</c> to redact; the test below is that knowledge, applied to
/// every record in the two namespaces rather than to the one where somebody remembered.</para>
/// </summary>
public sealed class RedTeamSecretSurfaceTests
{
    /// <summary>
    /// Property-name fragments that mean "this value is a credential". Deliberately excludes
    /// "Hash" on its own — a BCrypt hash is not a login credential, and the schema is full of
    /// legitimately-named hash columns — but includes the specific ones this control plane holds.
    /// </summary>
    private static readonly string[] SecretNameFragments =
        ["Password", "Token", "ActivationUrl", "Secret", "Credential", "ApiKey"];

    /// <summary>Not secrets, despite matching a fragment above.</summary>
    private static readonly string[] Allowed =
    [
        "PasswordFailures",       // policy messages, not the password
        "MinimumPasswordLength",  // an int
        "TokenHash",              // the stored digest; identifies nobody and unlocks nothing
        "LeaseToken",             // a runner's lease guid, not an authentication credential
        "PasswordGenerated",      // a boolean
        "AdminPasswordHash",      // the stored BCrypt hash
        "ExternalTokens",         // an inference-token METER
        "MonthlySoftTokenLimit",
        "MonthlyHardTokenLimit",
        "TokenState"              // an enum describing WHY a token was refused, never the token
    ];

    /// <summary>
    /// FINDING R10 (latent). <see cref="ProvisioningSubmitResult"/> is a POSITIONAL record carrying
    /// the plaintext founding-administrator password in <c>GeneratedPassword</c> and does not
    /// override <c>ToString()</c>. Nothing interpolates it today — the one log line near it prints
    /// <c>result.Outcome</c> — so this is a loaded gun rather than a discharged one, and it is the
    /// exact hazard its sibling <see cref="IssuedTenantAdminInvitation"/> overrides
    /// <c>ToString()</c> to avoid.
    ///
    /// <para>SKIPPED because it FAILS: it proves the defect. Remove the Skip to see it.</para>
    /// </summary>
    [Fact]
    public void No_record_carrying_a_credential_relies_on_the_compiler_generated_ToString()
    {
        var offenders = new List<string>();

        foreach (var type in ControlPlaneTypes().Where(IsRecord))
        {
            var secrets = SecretProperties(type).ToArray();
            if (secrets.Length == 0) continue;

            // EVERY record declares a ToString() — the question is whether the COMPILER wrote it.
            // The synthesised one carries [CompilerGenerated]; a hand-written override does not.
            var toString = type.GetMethod(nameof(ToString), BindingFlags.Public | BindingFlags.Instance,
                Type.EmptyTypes);
            var handWritten = toString?.DeclaringType == type
                              && toString!.GetCustomAttribute<CompilerGeneratedAttribute>() is null;
            if (handWritten) continue; // redacts on purpose

            offenders.Add($"{type.FullName} carries [{string.Join(", ", secrets)}]");
        }

        Assert.True(offenders.Count == 0, $"""
            {offenders.Count} record type(s) hold a credential and print it from the compiler's
            ToString(). One interpolation into a log line or an exception message is enough.

            Fix as IssuedTenantAdminInvitation does — override ToString() and redact — or move the
            value onto a plain class, whose ToString() prints only the type name.

            Offenders:
              {string.Join("\n              ", offenders)}
            """);
    }

    /// <summary>The half that is right, pinned so it stays right.</summary>
    [Fact]
    public void The_issued_invitation_redacts_its_token_and_url_when_printed()
    {
        var issued = new IssuedTenantAdminInvitation
        {
            InvitationId = 1,
            TenantId = 2,
            UserId = 3,
            Email = "founder@customer.test",
            RecipientName = "Founder",
            TenantName = "Acme",
            Token = "a-live-256-bit-activation-token",
            ActivationUrl = "https://app.nexora.test/activate/a-live-256-bit-activation-token",
            ExpiresAtUtc = DateTime.UtcNow.AddDays(7)
        };

        var printed = issued.ToString();

        Assert.DoesNotContain("a-live-256-bit-activation-token", printed, StringComparison.Ordinal);
        Assert.Contains("[redacted]", printed, StringComparison.Ordinal);
    }

    /// <summary>
    /// A plain class prints only its type name, which is why <c>ProvisionTenantRequest</c> holding
    /// <c>AdminPassword</c> is safe today — and why converting it to a record would silently make
    /// <see cref="ProvisioningStepContext"/> print a plaintext password, since that record holds
    /// the request as a member.
    /// </summary>
    [Fact]
    public void The_provisioning_request_is_a_class_and_therefore_prints_nothing()
    {
        Assert.False(IsRecord(typeof(ERP_RFQ_Automation.Platform.Models.ProvisionTenantRequest)));

        var request = new ERP_RFQ_Automation.Platform.Models.ProvisionTenantRequest
        {
            Name = "Acme", Slug = "acme", AdminEmail = "a@b.test",
            AdminFirstName = "A", AdminLastName = "B",
            AdminPassword = "a-plaintext-password-nobody-should-log"
        };

        Assert.DoesNotContain("a-plaintext-password-nobody-should-log", request.ToString() ?? string.Empty,
            StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------------------------- helpers

    private static IEnumerable<Type> ControlPlaneTypes() =>
        typeof(ProvisioningSubmitResult).Assembly.GetTypes()
            .Where(t => t is { IsClass: true, IsPublic: true })
            .Where(t => t.Namespace is "ERP_RFQ_Automation.Platform.Provisioning"
                                    or "ERP_RFQ_Automation.Platform.Onboarding");

    /// <summary>A record is a class with the compiler-synthesised <c>&lt;Clone&gt;$</c> method.</summary>
    private static bool IsRecord(Type type) =>
        type.GetMethod("<Clone>$", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            is not null;

    private static IEnumerable<string> SecretProperties(Type type) =>
        type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(p => p.Name)
            .Where(name => !Allowed.Contains(name, StringComparer.Ordinal))
            .Where(name => SecretNameFragments.Any(f => name.Contains(f, StringComparison.Ordinal)));
}
