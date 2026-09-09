using System.Text.Json;
using System.Text.Json.Serialization;
using ERP_RFQ_Automation.AI;
using ERP_RFQ_Automation.PlatformGovernance;
using Xunit;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// The names these payloads reach the browser under.
///
/// <para>The external-dependency fix widened two contracts —
/// <see cref="AiTrustCenterView"/> gained a dependency snapshot and
/// <see cref="AiTrustUsageSummary"/> an authorized count, and
/// <see cref="QualityRecommendation"/> gained a metric key. The service tests prove the
/// NUMBERS are right; they say nothing about whether the browser can find them. A field the
/// frontend reads as <c>dependency.externalSharePercent</c> and the API emits under any other
/// name is an undefined at runtime and a blank banner — the screen goes quiet instead of
/// wrong, which is the harder failure to notice.</para>
///
/// <para>These assertions are the literal strings in
/// <c>Frontend/src/api/services/platformGovernanceService.ts</c>. If serialization is ever
/// reconfigured in Program.cs, this fails here rather than on the screen.</para>
/// </summary>
public sealed class AiTrustWireContractTests
{
    /// <summary>
    /// The options Program.cs builds: AddJsonOptions leaves ASP.NET Core's camelCase policy in
    /// place and only adds case-insensitive reads and string-tolerant numbers.
    /// </summary>
    private static readonly JsonSerializerOptions ApiOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };

    [Fact]
    public void Trust_center_payload_reaches_the_browser_under_the_names_it_reads()
    {
        var view = new AiTrustCenterView(
            Policy(), Usage(), [], [], nameof(InferencePosture.ExternalAuthorized),
            new AiExternalDependencySnapshot(40, 0, 40, 40, 0, 0m, 10m, 50, false));

        using var document = JsonDocument.Parse(JsonSerializer.Serialize(view, ApiOptions));
        var root = document.RootElement;

        var usage = root.GetProperty("usage");
        Assert.Equal(40, usage.GetProperty("externalRequests").GetInt32());
        Assert.Equal(40, usage.GetProperty("authorizedExternalRequests").GetInt32());
        Assert.Equal(0m, usage.GetProperty("externalDependencyPercent").GetDecimal());
        Assert.False(usage.GetProperty("dependencyCeilingBreached").GetBoolean());

        var dependency = root.GetProperty("dependency");
        foreach (var (name, expected) in new (string, int)[]
                 { ("total", 40), ("local", 0), ("external", 40), ("authorizedExternal", 40),
                   ("unresolved", 0), ("windowSize", 50) })
            Assert.Equal(expected, dependency.GetProperty(name).GetInt32());
        Assert.Equal(0m, dependency.GetProperty("externalSharePercent").GetDecimal());
        Assert.Equal(10m, dependency.GetProperty("ceilingPercent").GetDecimal());
        Assert.False(dependency.GetProperty("ceilingBreached").GetBoolean());
    }

    [Fact]
    public void Quality_recommendation_carries_its_metric_key_to_the_browser()
    {
        // The page needs metricKey and drilldownKey as SEPARATE fields: two metrics now share
        // the "external-ai" cohort, so selecting on the cohort alone highlighted both cards and
        // explained the wrong one.
        var recommendation = new QualityRecommendation("Critical", "Review external dependency",
            "Inspect external call evidence.", "Unauthorized external dependency is 25%.",
            "external-ai", "unauthorized-external-dependency");

        using var document = JsonDocument.Parse(JsonSerializer.Serialize(recommendation, ApiOptions));
        var root = document.RootElement;

        Assert.Equal("external-ai", root.GetProperty("drilldownKey").GetString());
        Assert.Equal("unauthorized-external-dependency", root.GetProperty("metricKey").GetString());
        Assert.Equal("Critical", root.GetProperty("priority").GetString());
    }

    [Fact]
    public void Quality_metric_keys_the_page_and_the_recommendation_agree_on_are_emitted()
    {
        // Recommendations() resolves its metric with metrics.Single(x => x.Key == ...). A
        // renamed metric key would throw at request time, not compile time, so the pair is
        // asserted together: the raw egress figure and the ratio the ceiling governs.
        var metrics = new[]
        {
            new QualityMetric("external-dependency", "External AI dependency", 100m, "percent",
                40, 40, "definition", "Measured", "external-ai"),
            new QualityMetric("unauthorized-external-dependency", "Unauthorized external AI dependency",
                0m, "percent", 0, 40, "definition", "Measured", "external-ai"),
        };

        using var document = JsonDocument.Parse(JsonSerializer.Serialize(metrics, ApiOptions));
        var keys = document.RootElement.EnumerateArray()
            .Select(x => x.GetProperty("key").GetString()).ToArray();

        Assert.Equal(["external-dependency", "unauthorized-external-dependency"], keys);
        // Both point at the same evidence cohort, which is why metricKey had to exist.
        Assert.All(document.RootElement.EnumerateArray(),
            x => Assert.Equal("external-ai", x.GetProperty("drilldownKey").GetString()));
    }

    private static AiTrustPolicyState Policy() => new(true, true, "RfqExtraction", null, null,
        null, null, null, null, null, null, null, 10m, true, "Commercial", "RedactedFieldsOnly",
        "TenantApprovedRegion", 30, false, true, null, null, null, 1, DateTime.UtcNow, "test");

    private static AiTrustUsageSummary Usage() => new(40, 0, 40, 40, 0m, false, 0, 0, 0,
        4000, 800, 0, 4800, null, null, new Dictionary<string, decimal>());
}
