using System.Net;
using System.Text.Json;
using ERP_RFQ_Automation.HealthChecks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// <c>/ready</c> MUST SAY WHAT FAILED.
///
/// <para>Production, 2026-08-24: <c>/ready</c> had been returning 503 for the entire life of the
/// deployment, and its whole body was the word <c>Unhealthy</c>. The two failing check names —
/// <c>email-poll-channel</c> and <c>background-workers</c> — had to be recovered by grepping the
/// Render log stream, which is the second system a probe exists to make unnecessary.</para>
///
/// <para>These tests pin both halves of the contract: the report NAMES the failing checks and
/// carries their reasons, and it does not leak the things a probe reachable without
/// authentication must never carry.</para>
/// </summary>
public sealed class ReadinessProbeReportTests
{
    [Fact]
    public async Task The_readiness_body_names_every_failing_check_and_says_why()
    {
        using var host = await ProbeHostAsync(
            ("database", HealthCheckResult.Healthy("Database reachable")),
            ("background-workers", HealthCheckResult.Healthy("2 background worker(s) beating.")),
            ("email-poll-channel", HealthCheckResult.Unhealthy(
                "Inbound mail: 1 of 2 mailbox(es) failing. FAILING - mailbox 5 "
                + "ops@intelliflowsystem.example: authentication failed.")));

        using var response = await host.GetTestClient().GetAsync("/ready");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        // The defect: this used to be the ENTIRE body.
        Assert.NotEqual("Unhealthy", body);

        using var json = JsonDocument.Parse(body);
        Assert.Equal("Unhealthy", json.RootElement.GetProperty("status").GetString());
        var failing = json.RootElement.GetProperty("failing")
            .EnumerateArray().Select(e => e.GetString()).ToList();
        Assert.Equal(new[] { "email-poll-channel" }, failing);

        var check = json.RootElement.GetProperty("checks").EnumerateArray()
            .Single(e => e.GetProperty("name").GetString() == "email-poll-channel");
        Assert.Equal("Unhealthy", check.GetProperty("status").GetString());
        Assert.Contains("mailbox 5", check.GetProperty("description").GetString());
        Assert.Contains("1 of 2 mailbox(es) failing", check.GetProperty("description").GetString());
        // Healthy checks are listed too, so "which of the twelve ran" is answerable.
        Assert.Equal(3, json.RootElement.GetProperty("checks").GetArrayLength());
    }

    [Fact]
    public async Task A_healthy_deployment_still_reports_which_checks_ran()
    {
        using var host = await ProbeHostAsync(
            ("database", HealthCheckResult.Healthy("Database reachable")),
            ("evidence-storage", HealthCheckResult.Degraded("Object storage is slow.")));

        using var response = await host.GetTestClient().GetAsync("/ready");
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        // Degraded is deliberately 200: it must show in the payload without draining traffic.
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Degraded", json.RootElement.GetProperty("status").GetString());
        Assert.Equal(
            new[] { "evidence-storage" },
            json.RootElement.GetProperty("failing").EnumerateArray().Select(e => e.GetString()).ToArray());
    }

    [Fact]
    public async Task The_readiness_body_never_carries_the_exception_or_the_data_bag()
    {
        // DatabaseHealthCheck passes the exception straight through, and Npgsql's messages carry
        // host and database. The probe is AllowAnonymous; the exception stays in the logs.
        using var host = await ProbeHostAsync(
            ("database", HealthCheckResult.Unhealthy(
                "Database check failed",
                new InvalidOperationException(
                    "Host=db.internal;Database=nexora;Username=pipeline;Password=hunter2"),
                new Dictionary<string, object> { ["connectionString"] = "Password=hunter2" })));

        using var response = await host.GetTestClient().GetAsync("/ready");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Contains("Database check failed", body);
        Assert.DoesNotContain("hunter2", body);
        Assert.DoesNotContain("db.internal", body);
        Assert.DoesNotContain("connectionString", body);
    }

    // ------------------------------------------------------------------------- redaction unit

    [Theory]
    // The local part identifies a person; the domain is what makes the message diagnosable.
    [InlineData("mailbox 5 info@intelliflowsystem.com: refused",
                "mailbox 5 ***@intelliflowsystem.com: refused")]
    [InlineData("Host=db;Password=hunter2;Pooling=true", "Host=db;Password=***;Pooling=true")]
    [InlineData("postgres://nexora:hunter2@db.internal:5432/app",
                "postgres://***@db.internal:5432/app")]
    [InlineData("api_key: sk-live-1234", "api_key=***")]
    // ...and the parts an operator needs are untouched.
    [InlineData("Background worker(s) stopped beating: email-poller.",
                "Background worker(s) stopped beating: email-poller.")]
    [InlineData("Last successful read 2026-07-30T04:15:00.0000000+00:00.",
                "Last successful read 2026-07-30T04:15:00.0000000+00:00.")]
    public void Redaction_removes_identities_and_secrets_and_leaves_the_diagnosis(
        string input, string expected)
        => Assert.Equal(expected, HealthReportResponseWriter.Redact(input));

    [Fact]
    public void A_pathological_description_cannot_turn_the_probe_into_a_log_sink()
    {
        var redacted = HealthReportResponseWriter.Redact(new string('x', 50_000))!;
        Assert.True(redacted.Length <= HealthReportResponseWriter.MaxDescriptionLength + 16);
        Assert.EndsWith("(truncated)", redacted);
    }

    // ------------------------------------------------------------------------- test plumbing

    /// <summary>
    /// A real ASP.NET pipeline with the real health-check middleware and the real writer, so the
    /// status code mapping, content type and JSON all come from the framework rather than a
    /// direct call to the writer.
    /// </summary>
    private static async Task<IHost> ProbeHostAsync(params (string Name, HealthCheckResult Result)[] checks)
    {
        var builder = new HostBuilder().ConfigureWebHost(web =>
        {
            web.UseTestServer();
            web.ConfigureServices(services =>
            {
                services.AddRouting();
                var health = services.AddHealthChecks();
                foreach (var (name, result) in checks)
                    health.AddCheck(name, new StaticCheck(result), tags: new[] { "ready" });
            });
            web.Configure(app => app.UseRouting().UseEndpoints(endpoints =>
                endpoints.MapHealthChecks("/ready",
                    new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions
                    {
                        Predicate = registration => registration.Tags.Contains("ready"),
                        ResponseWriter = HealthReportResponseWriter.WriteAsync
                    })));
        });

        var host = await builder.StartAsync();
        return host;
    }

    private sealed class StaticCheck(HealthCheckResult result) : IHealthCheck
    {
        public Task<HealthCheckResult> CheckHealthAsync(
            HealthCheckContext context, CancellationToken cancellationToken = default)
            => Task.FromResult(result);
    }
}
