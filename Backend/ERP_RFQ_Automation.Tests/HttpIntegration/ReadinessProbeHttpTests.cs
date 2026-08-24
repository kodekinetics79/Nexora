using System.Text.Json;

namespace ERP_RFQ_Automation.Tests.HttpIntegration;

/// <summary>
/// The wiring half of the readiness-payload fix. <see cref="ReadinessProbeReportTests"/> asserts
/// what the writer produces; this asserts that <c>Program.cs</c> actually hands <c>/ready</c> to
/// it — the one line whose absence would leave the production probe emitting the single word
/// "Unhealthy" again with every unit test still green.
/// </summary>
[Collection(Release01BHttpCollection.Name)]
[Trait("Category", "PostgreSQL")]
public sealed class ReadinessProbeHttpTests(Release01BHttpApplication app)
{
    [Fact]
    public async Task Ready_reports_every_check_by_name_against_the_real_program()
    {
        using var response = await app.CreateClient().GetAsync("/ready");
        var body = await response.Content.ReadAsStringAsync();

        // The defect: this used to be the ENTIRE body, whatever the outcome.
        Assert.NotEqual("Healthy", body);
        Assert.NotEqual("Unhealthy", body);
        Assert.StartsWith("application/json", response.Content.Headers.ContentType!.ToString());

        using var json = JsonDocument.Parse(body);
        Assert.False(string.IsNullOrWhiteSpace(json.RootElement.GetProperty("status").GetString()));

        var checks = json.RootElement.GetProperty("checks").EnumerateArray().ToList();
        var names = checks.Select(c => c.GetProperty("name").GetString()).ToList();
        // The two checks whose names had to be recovered from the Render log stream.
        Assert.Contains("background-workers", names);
        Assert.Contains("email-poll-channel", names);
        Assert.Contains("database", names);
        foreach (var check in checks)
        {
            Assert.False(string.IsNullOrWhiteSpace(check.GetProperty("status").GetString()));
            Assert.True(check.TryGetProperty("description", out _));
        }

        // Every check reported not-Healthy is named up front, so "which one" needs no scanning.
        var failing = json.RootElement.GetProperty("failing")
            .EnumerateArray().Select(e => e.GetString()).ToList();
        Assert.Equal(
            checks.Where(c => c.GetProperty("status").GetString() != "Healthy")
                .Select(c => c.GetProperty("name").GetString())
                .OrderBy(name => name, StringComparer.Ordinal)
                .ToList(),
            failing);

        // Never on an anonymous probe, whatever a check chose to attach to its result.
        Assert.DoesNotContain("\"exception\"", body);
        Assert.DoesNotContain("\"data\"", body);
    }
}
