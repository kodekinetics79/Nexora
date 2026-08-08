using System.Reflection;
using System.Text.RegularExpressions;

namespace ERP_RFQ_Automation.Infrastructure;

public sealed record BuildIdentityResponse(string Revision, string Version, string Environment);

public static partial class BuildIdentity
{
    private const string Unknown = "unknown";

    public static BuildIdentityResponse Current(IHostEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(environment);

        var assembly = typeof(Program).Assembly;
        var informationalVersion = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion;

        return new BuildIdentityResponse(
            Revision(),
            string.IsNullOrWhiteSpace(informationalVersion)
                ? assembly.GetName().Version?.ToString() ?? Unknown
                : informationalVersion,
            environment.EnvironmentName);
    }

    internal static string Revision(Func<string, string?>? readVariable = null)
    {
        readVariable ??= Environment.GetEnvironmentVariable;
        foreach (var variable in new[] { "RENDER_GIT_COMMIT", "NEXORA_BUILD_REVISION" })
        {
            var candidate = readVariable(variable)?.Trim();
            if (candidate is not null && GitRevision().IsMatch(candidate))
                return candidate.ToLowerInvariant();
        }

        return Unknown;
    }

    [GeneratedRegex("^[0-9a-fA-F]{7,64}$", RegexOptions.CultureInvariant)]
    private static partial Regex GitRevision();
}
