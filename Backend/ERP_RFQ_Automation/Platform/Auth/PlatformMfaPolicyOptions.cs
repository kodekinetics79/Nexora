using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace ERP_RFQ_Automation.Platform.Auth;

/// <summary>
/// The BACKEND's answer to "what kind of deployment is this, and what is it therefore allowed to do
/// about MFA".
///
/// <para><b>Nothing here can be influenced by a request.</b> The environment class comes from
/// <see cref="IHostEnvironment.EnvironmentName"/> and one explicit configuration key; there is
/// deliberately no key that names the class directly, because such a key is a production bypass
/// with extra steps — a deployment that could declare itself "LocalOrTest" would be able to turn
/// MFA off for real customers by editing one string.</para>
///
/// <para><b>Configuration keys introduced (all under <c>Platform:Mfa</c>):</b></para>
/// <list type="bullet">
/// <item><description><c>Platform:Mfa:IsolatedTestInfrastructure</c> (bool, default <c>false</c>) —
/// the explicit classification that a Staging/UAT deployment is isolated test infrastructure with
/// no customer data. It is the ONLY thing that lets Staging reach
/// <see cref="PlatformMfaMode.DISABLED_TEST_ONLY"/>. Setting it in Production is a startup
/// failure, not a warning: a deployment that lies about being a test rig would otherwise get the
/// test rig's privileges.</description></item>
/// <item><description><c>Platform:Mfa:MaxBypassHours</c> (int, default <c>24</c>, range 1–24) —
/// the ceiling on how long any non-REQUIRED policy may run before it expires on its own.</description></item>
/// <item><description><c>Platform:Mfa:BrowserTrustHours</c> (int, default <c>12</c>, range 8–12) —
/// how long "remember this browser" suppresses a repeat challenge.</description></item>
/// <item><description><c>Platform:Mfa:PasswordReauthWindowMinutes</c> (int, default <c>5</c>,
/// range 1–15) — how long a password re-authentication satisfies a high-risk operation.</description></item>
/// </list>
/// </summary>
public sealed class PlatformMfaPolicyOptions
{
    public const string IsolatedTestInfrastructureKey = "Platform:Mfa:IsolatedTestInfrastructure";
    public const string MaxBypassHoursKey = "Platform:Mfa:MaxBypassHours";
    public const string BrowserTrustHoursKey = "Platform:Mfa:BrowserTrustHours";
    public const string PasswordReauthWindowMinutesKey = "Platform:Mfa:PasswordReauthWindowMinutes";

    public const int DefaultMaxBypassHours = 24;
    public const int MinMaxBypassHours = 1;

    /// <summary>Bounds from the control's own definition: a trust shorter than a working day makes
    /// operators re-challenge mid-afternoon and teaches them to click through prompts; one longer
    /// than a day is a second factor that outlives the person's memory of granting it.</summary>
    public const int MinBrowserTrustHours = 8;
    public const int MaxBrowserTrustHours = 12;
    public const int DefaultBrowserTrustHours = 12;

    public const int MinPasswordReauthWindowMinutes = 1;
    public const int MaxPasswordReauthWindowMinutes = 15;
    public const int DefaultPasswordReauthWindowMinutes = 5;

    /// <summary>A bypass reason short enough to be a keystroke is not a reason. Long enough to
    /// force a sentence, short enough not to be theatre.</summary>
    public const int MinimumReasonLength = 20;

    private PlatformMfaPolicyOptions(
        PlatformMfaEnvironmentClass environmentClass,
        string environmentName,
        bool isolatedTestInfrastructure,
        int maxBypassHours,
        int browserTrustHours,
        int passwordReauthWindowMinutes)
    {
        EnvironmentClass = environmentClass;
        EnvironmentName = environmentName;
        IsolatedTestInfrastructure = isolatedTestInfrastructure;
        MaxBypassHours = maxBypassHours;
        BrowserTrustHours = browserTrustHours;
        PasswordReauthWindowMinutes = passwordReauthWindowMinutes;
    }

    public PlatformMfaEnvironmentClass EnvironmentClass { get; }

    /// <summary>The raw <c>ASPNETCORE_ENVIRONMENT</c> value, carried so the screen and the audit
    /// row can say "Staging", not merely "StagingOrUat".</summary>
    public string EnvironmentName { get; }

    public bool IsolatedTestInfrastructure { get; }

    public int MaxBypassHours { get; }

    public int BrowserTrustHours { get; }

    public int PasswordReauthWindowMinutes { get; }

    public TimeSpan MaxBypassDuration => TimeSpan.FromHours(MaxBypassHours);

    public TimeSpan BrowserTrustDuration => TimeSpan.FromHours(BrowserTrustHours);

    public TimeSpan PasswordReauthWindow => TimeSpan.FromMinutes(PasswordReauthWindowMinutes);

    /// <summary>
    /// Reads and VALIDATES the configuration, throwing on anything a production deployment must not
    /// boot with. Same fail-closed contract as <c>SecretProtection.CreateFromConfiguration</c> and
    /// <c>UnifiedDocumentIngestionGuard.Enforce</c>: a misconfiguration stops the deploy instead of
    /// producing a running API with a control quietly switched off.
    /// </summary>
    public static PlatformMfaPolicyOptions CreateFromConfiguration(
        IConfiguration configuration, IHostEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(environment);

        var environmentClass = ClassifyEnvironment(environment.EnvironmentName);
        var isolated = configuration.GetValue(IsolatedTestInfrastructureKey, false);

        // The single most dangerous configuration in this file. If it were merely ignored in
        // production, a staging appsettings copied onto a production host would boot silently and
        // the only difference would be that DISABLED_TEST_ONLY had become reachable there.
        if (isolated && environmentClass == PlatformMfaEnvironmentClass.Production)
            throw new InvalidOperationException(
                $"{IsolatedTestInfrastructureKey} is set to true in environment " +
                $"'{environment.EnvironmentName}', which is classified as Production. Isolated test " +
                "infrastructure classification exists so a Staging/UAT rig with no customer data can " +
                "disable MFA enforcement; a production deployment must never claim it. Remove the key " +
                "or correct ASPNETCORE_ENVIRONMENT.");

        var maxBypassHours = ReadBoundedInt(
            configuration, MaxBypassHoursKey, DefaultMaxBypassHours, MinMaxBypassHours, DefaultMaxBypassHours);
        var browserTrustHours = ReadBoundedInt(
            configuration, BrowserTrustHoursKey, DefaultBrowserTrustHours, MinBrowserTrustHours, MaxBrowserTrustHours);
        var reauthWindow = ReadBoundedInt(
            configuration, PasswordReauthWindowMinutesKey, DefaultPasswordReauthWindowMinutes,
            MinPasswordReauthWindowMinutes, MaxPasswordReauthWindowMinutes);

        return new PlatformMfaPolicyOptions(
            environmentClass, environment.EnvironmentName, isolated,
            maxBypassHours, browserTrustHours, reauthWindow);
    }

    /// <summary>
    /// Maps an ASP.NET environment name onto a security class.
    ///
    /// <para><b>Unknown names classify as Production.</b> That is the whole point of doing this in
    /// one place: a deployment named "prod-eu", "live" or anything a future operator invents gets
    /// the strictest ceiling, not the loosest. The alternative — defaulting to permissive and
    /// listing the production names — fails open on exactly the name nobody remembered to add.</para>
    /// </summary>
    public static PlatformMfaEnvironmentClass ClassifyEnvironment(string? environmentName)
    {
        var name = (environmentName ?? string.Empty).Trim();

        if (Matches(name, Environments.Development) || Matches(name, "Testing")
            || Matches(name, "Test") || Matches(name, "Local") || Matches(name, "IntegrationTest"))
            return PlatformMfaEnvironmentClass.LocalOrTest;

        if (Matches(name, Environments.Staging) || Matches(name, "UAT")
            || Matches(name, "PreProduction") || Matches(name, "PreProd") || Matches(name, "QA"))
            return PlatformMfaEnvironmentClass.StagingOrUat;

        return PlatformMfaEnvironmentClass.Production;

        static bool Matches(string value, string candidate) =>
            value.Equals(candidate, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Whether this deployment may run <paramref name="mode"/> at all, before anything about the
    /// request or the operator is considered. This is the environment ceiling and it is checked in
    /// three places on purpose — at startup, on write, and again on every read — because a control
    /// that is only enforced where the change is made is a control that a direct database UPDATE
    /// walks straight past.
    /// </summary>
    public bool Permits(PlatformMfaMode mode) => mode switch
    {
        PlatformMfaMode.REQUIRED => true,
        PlatformMfaMode.OPTIONAL => EnvironmentClass != PlatformMfaEnvironmentClass.Production,
        PlatformMfaMode.DISABLED_TEST_ONLY => EnvironmentClass == PlatformMfaEnvironmentClass.LocalOrTest
            || (EnvironmentClass == PlatformMfaEnvironmentClass.StagingOrUat && IsolatedTestInfrastructure),
        _ => false
    };

    /// <summary>The sentence an operator sees when <see cref="Permits"/> says no. It names the
    /// environment and the key, because "forbidden" without either is a support ticket.</summary>
    public string ExplainRefusal(PlatformMfaMode mode) => mode switch
    {
        PlatformMfaMode.OPTIONAL =>
            $"MFA enforcement cannot be made OPTIONAL in {EnvironmentName}: this deployment is classified " +
            $"as {EnvironmentClass} and production-class deployments accept REQUIRED only.",
        PlatformMfaMode.DISABLED_TEST_ONLY when EnvironmentClass == PlatformMfaEnvironmentClass.Production =>
            $"MFA enforcement cannot be disabled in {EnvironmentName}: this deployment is classified as " +
            "Production. There is no configuration that makes this reachable.",
        PlatformMfaMode.DISABLED_TEST_ONLY =>
            $"MFA enforcement cannot be disabled in {EnvironmentName}: a Staging/UAT deployment must first be " +
            $"classified as isolated test infrastructure by setting {IsolatedTestInfrastructureKey}=true, which " +
            "is a deployment configuration change and not something this screen can do.",
        _ => $"MFA enforcement mode '{mode}' is not permitted in {EnvironmentName}."
    };

    /// <summary>
    /// The phrase the operator must type, per target mode. Not a secret and deliberately shown on
    /// the screen — its job is to make the act deliberate, the same job the tenant-name confirmation
    /// does on a purge. Deriving it from the mode means the phrase can never drift from what it
    /// authorises.
    /// </summary>
    public static string ConfirmationPhraseFor(PlatformMfaMode mode) => mode switch
    {
        PlatformMfaMode.OPTIONAL => "MAKE PLATFORM MFA OPTIONAL",
        PlatformMfaMode.DISABLED_TEST_ONLY => "DISABLE PLATFORM MFA IN TEST",
        _ => "RESTORE PLATFORM MFA"
    };

    /// <summary>
    /// Reads an int key, refusing an out-of-band value instead of clamping it.
    ///
    /// <para>Clamping is the wrong behaviour for a security bound: an operator who wrote
    /// <c>MaxBypassHours: 240</c> believes they have ten days, and a silent clamp to 24 means the
    /// belief and the system disagree with nobody told. Throwing at startup is the only outcome
    /// where the misunderstanding is discovered by the person who caused it.</para>
    /// </summary>
    private static int ReadBoundedInt(
        IConfiguration configuration, string key, int fallback, int minimum, int maximum)
    {
        var raw = configuration[key];
        if (string.IsNullOrWhiteSpace(raw)) return fallback;

        if (!int.TryParse(raw, out var value))
            throw new InvalidOperationException($"{key} must be an integer between {minimum} and {maximum}.");

        if (value < minimum || value > maximum)
            throw new InvalidOperationException(
                $"{key} is {value}, which is outside the permitted range {minimum}–{maximum}.");

        return value;
    }
}
