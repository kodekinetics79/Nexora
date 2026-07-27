using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ERP_RFQ_Automation.Procurement;
using ERP_RFQ_Automation.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace ERP_RFQ_Automation.Tests;

public sealed class ProcurementIntegrationServiceTests
{
    private const string Secret = "gate-3-disposable-integration-secret-at-least-32-bytes";

    [Fact]
    public async Task Signed_callback_is_authoritative_idempotent_and_conflicting_replay_is_rejected()
    {
        using var fixture = new CustomerAwardTestFixture();
        var orderLineId = await ProcurementHandoffServiceTests.SeedSourcedCustomerOrderAsync(fixture);
        var handoffs = new ProcurementHandoffService(fixture.Context);
        var created = await handoffs.CreateAsync(fixture.BusinessUnitId, "integration-handoff",
            "corr-integration-handoff", "tests", new(orderLineId, "DROP_SHIP", null,
                "Authorized test destination", new DateOnly(2026, 8, 15)));
        var service = new ProcurementIntegrationService(fixture.Context,
            Configuration(fixture.BusinessUnitId));
        var command = new ProcurementStatusCallbackCommand(created.Id, "provider-event-1",
            "EXT-PO-9001", "20", "EXT-SO-4001", created.RequiredQuantity,
            created.SelectedUnitCost, new DateOnly(2026, 8, 12),
            ProcurementHandoffStatuses.ExternalPoCreated, DateTime.UtcNow.AddMinutes(-1));
        var raw = JsonSerializer.Serialize(command, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();

        var applied = await service.ApplyCallbackAsync(fixture.BusinessUnitId, timestamp,
            Sign(timestamp, raw), "corr-provider-1", raw, command);
        var replay = await service.ApplyCallbackAsync(fixture.BusinessUnitId, timestamp,
            Sign(timestamp, raw), "corr-provider-replay", raw, command);
        var renamedReplay = await new ProcurementIntegrationService(fixture.Context,
            Configuration(fixture.BusinessUnitId, "Renamed ERP display label")).ApplyCallbackAsync(
                fixture.BusinessUnitId, timestamp, Sign(timestamp, raw), "corr-provider-renamed",
                raw, command);

        var changed = command with { ExternalSalesOrderNumber = "EXT-SO-CHANGED" };
        var changedRaw = JsonSerializer.Serialize(changed, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ApplyCallbackAsync(
            fixture.BusinessUnitId, timestamp, Sign(timestamp, changedRaw), "corr-provider-conflict",
            changedRaw, changed));

        var current = await handoffs.GetAsync(fixture.BusinessUnitId, created.Id);
        Assert.True(applied.Applied);
        Assert.False(applied.Replay);
        Assert.True(replay.Replay);
        Assert.True(renamedReplay.Replay);
        Assert.True(current.IsAuthoritative);
        Assert.Equal("Disposable ERP", current.SourceOfTruth);
        Assert.Equal("EXT-PO-9001", current.ExternalSupplierPoNumber);
        Assert.Equal("EXT-SO-4001", current.ExternalSalesOrderNumber);
        Assert.Equal("provider-event-1", current.LastExternalEventId);
        await Assert.ThrowsAsync<InvalidOperationException>(() => handoffs.SynchronizeAsync(
            fixture.BusinessUnitId, current.Id, "manual-overwrite", "corr-manual-overwrite", "tests",
            new(current.Version, current.ExternalSupplierPoNumber!, current.ExternalSupplierPoLineNumber!,
                current.RequiredQuantity, current.SelectedUnitCost,
                current.ExternalExpectedOn ?? new DateOnly(2026, 8, 12),
                current.Status, DateTime.UtcNow)));
        var receipt = Assert.Single(await fixture.Context.ProcurementCallbackReceipts.ToListAsync());
        Assert.Equal($"procurement:{fixture.BusinessUnitId}", receipt.SourceSystem);
        Assert.Single(await fixture.Context.ProcurementEvents.Where(x =>
            x.EventType == "PROCUREMENT_HANDOFF_PROVIDER_STATUS_APPLIED").ToListAsync());
    }

    [Fact]
    public async Task Commercial_variance_is_persisted_for_review_without_mutating_handoff()
    {
        using var fixture = new CustomerAwardTestFixture();
        var orderLineId = await ProcurementHandoffServiceTests.SeedSourcedCustomerOrderAsync(fixture);
        var handoffs = new ProcurementHandoffService(fixture.Context);
        var created = await handoffs.CreateAsync(fixture.BusinessUnitId, "variance-handoff",
            "corr-variance-handoff", "tests", new(orderLineId, "DROP_SHIP", null, null, null));
        var service = new ProcurementIntegrationService(fixture.Context,
            Configuration(fixture.BusinessUnitId));
        var command = new ProcurementStatusCallbackCommand(created.Id, "provider-event-variance",
            "EXT-PO-VARIANCE", "1", null, created.RequiredQuantity + 1,
            created.SelectedUnitCost, new DateOnly(2026, 8, 12),
            ProcurementHandoffStatuses.ExternalPoCreated, DateTime.UtcNow.AddMinutes(-1));
        var raw = JsonSerializer.Serialize(command, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();

        var rejected = await service.ApplyCallbackAsync(fixture.BusinessUnitId, timestamp,
            Sign(timestamp, raw), "corr-provider-variance", raw, command);

        var current = await handoffs.GetAsync(fixture.BusinessUnitId, created.Id);
        Assert.False(rejected.Applied);
        Assert.Equal("COMMERCIAL_RECONCILIATION_REQUIRED", rejected.RejectionCode);
        Assert.False(current.IsAuthoritative);
        Assert.Null(current.ExternalSupplierPoNumber);
        var receipt = await fixture.Context.ProcurementCallbackReceipts.SingleAsync();
        Assert.Equal(ProcurementCallbackReceiptStatuses.Rejected, receipt.Status);
        Assert.Equal(command.OrderedQuantity, receipt.ObservedQuantity);
        Assert.Equal(command.ApprovedUnitCost, receipt.ObservedUnitCost);
        Assert.Equal(command.Status, receipt.ObservedStatus);
        Assert.Equal(command.ObservedOn, receipt.ObservedOn);
        var status = await service.GetStatusAsync(fixture.BusinessUnitId);
        Assert.Equal(1, status.ReconciliationDifferences);
        Assert.Equal("DEGRADED", status.ConnectorStatus);

        var corrected = command with
        {
            ExternalEventId = "provider-event-variance-corrected",
            OrderedQuantity = created.RequiredQuantity
        };
        var correctedRaw = JsonSerializer.Serialize(corrected, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var correctedResult = await service.ApplyCallbackAsync(fixture.BusinessUnitId, timestamp,
            Sign(timestamp, correctedRaw), "corr-provider-variance-corrected", correctedRaw, corrected);
        var reconciled = await service.GetStatusAsync(fixture.BusinessUnitId);
        Assert.True(correctedResult.Applied);
        Assert.Equal(0, reconciled.ReconciliationDifferences);
        Assert.Equal("SYNCHRONIZED", reconciled.ConnectorStatus);
    }

    [Fact]
    public async Task Older_provider_observation_is_retained_as_rejected_without_rewinding_state()
    {
        using var fixture = new CustomerAwardTestFixture();
        var orderLineId = await ProcurementHandoffServiceTests.SeedSourcedCustomerOrderAsync(fixture);
        var handoffs = new ProcurementHandoffService(fixture.Context);
        var created = await handoffs.CreateAsync(fixture.BusinessUnitId, "chronology-handoff",
            "corr-chronology-handoff", "tests", new(orderLineId, "DROP_SHIP", null, null, null));
        var service = new ProcurementIntegrationService(fixture.Context, Configuration(fixture.BusinessUnitId));
        var observed = DateTime.UtcNow.AddMinutes(-1);
        var first = new ProcurementStatusCallbackCommand(created.Id, "provider-event-current",
            "EXT-PO-CHRONOLOGY", "1", "EXT-SO-CHRONOLOGY", created.RequiredQuantity,
            created.SelectedUnitCost, new DateOnly(2026, 8, 12),
            ProcurementHandoffStatuses.ExternalPoCreated, observed);
        var firstRaw = JsonSerializer.Serialize(first, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
        await service.ApplyCallbackAsync(fixture.BusinessUnitId, timestamp, Sign(timestamp, firstRaw),
            "corr-current", firstRaw, first);

        var stale = first with { ExternalEventId = "provider-event-stale", ObservedOn = observed.AddMinutes(-1) };
        var staleRaw = JsonSerializer.Serialize(stale, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var result = await service.ApplyCallbackAsync(fixture.BusinessUnitId, timestamp,
            Sign(timestamp, staleRaw), "corr-stale", staleRaw, stale);

        Assert.False(result.Applied);
        Assert.Equal("STALE_PROVIDER_EVENT", result.RejectionCode);
        Assert.Equal(observed, (await handoffs.GetAsync(fixture.BusinessUnitId, created.Id)).LastSynchronizedOn);
    }

    [Fact]
    public async Task Invalid_signature_and_other_tenant_cannot_apply_callback()
    {
        using var fixture = new CustomerAwardTestFixture();
        var orderLineId = await ProcurementHandoffServiceTests.SeedSourcedCustomerOrderAsync(fixture);
        var handoffs = new ProcurementHandoffService(fixture.Context);
        var created = await handoffs.CreateAsync(fixture.BusinessUnitId, "tenant-handoff",
            "corr-tenant-handoff", "tests", new(orderLineId, "DROP_SHIP", null, null, null));
        var command = new ProcurementStatusCallbackCommand(created.Id, "provider-event-tenant",
            "EXT-PO-TENANT", "1", null, created.RequiredQuantity, created.SelectedUnitCost,
            new DateOnly(2026, 8, 12), ProcurementHandoffStatuses.ExternalPoCreated,
            DateTime.UtcNow.AddMinutes(-1));
        var raw = JsonSerializer.Serialize(command, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
        var service = new ProcurementIntegrationService(fixture.Context,
            Configuration(fixture.BusinessUnitId));

        await Assert.ThrowsAsync<ProcurementIntegrationAuthenticationException>(() =>
            service.ApplyCallbackAsync(fixture.BusinessUnitId, timestamp, new string('0', 64),
                "corr-invalid-signature", raw, command));

        using var other = fixture.Database.ContextFor(fixture.BusinessUnitId + 1);
        var otherService = new ProcurementIntegrationService(other, Configuration(fixture.BusinessUnitId + 1));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => otherService.ApplyCallbackAsync(
            fixture.BusinessUnitId + 1, timestamp, Sign(timestamp, raw), "corr-other-tenant",
            raw, command));
        Assert.Empty(await fixture.Context.ProcurementCallbackReceipts.ToListAsync());
    }

    [Fact]
    public async Task Initial_observation_cannot_predate_the_handoff()
    {
        using var fixture = new CustomerAwardTestFixture();
        var orderLineId = await ProcurementHandoffServiceTests.SeedSourcedCustomerOrderAsync(fixture);
        var handoffs = new ProcurementHandoffService(fixture.Context);
        var created = await handoffs.CreateAsync(fixture.BusinessUnitId, "old-observation-handoff",
            "corr-old-observation-handoff", "tests", new(orderLineId, "DROP_SHIP", null, null, null));
        var service = new ProcurementIntegrationService(fixture.Context, Configuration(fixture.BusinessUnitId));
        var command = new ProcurementStatusCallbackCommand(created.Id, "provider-event-too-old",
            "EXT-PO-OLD", "1", null, created.RequiredQuantity, created.SelectedUnitCost,
            new DateOnly(2026, 8, 12), ProcurementHandoffStatuses.ExternalPoCreated,
            DateTime.UtcNow.AddYears(-1));
        var raw = JsonSerializer.Serialize(command, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();

        var result = await service.ApplyCallbackAsync(fixture.BusinessUnitId, timestamp,
            Sign(timestamp, raw), "corr-provider-too-old", raw, command);

        Assert.False(result.Applied);
        Assert.Equal("INVALID_OBSERVED_TIME", result.RejectionCode);
        Assert.False((await handoffs.GetAsync(fixture.BusinessUnitId, created.Id)).IsAuthoritative);
    }

    [Fact]
    public async Task Manual_reference_capture_remains_in_authoritative_sync_backlog()
    {
        using var fixture = new CustomerAwardTestFixture();
        var orderLineId = await ProcurementHandoffServiceTests.SeedSourcedCustomerOrderAsync(fixture);
        var handoffs = new ProcurementHandoffService(fixture.Context);
        var created = await handoffs.CreateAsync(fixture.BusinessUnitId, "manual-sync-handoff",
            "corr-manual-sync-handoff", "tests", new(orderLineId, "DROP_SHIP", null, null, null));
        await handoffs.SynchronizeAsync(fixture.BusinessUnitId, created.Id, "manual-sync-reference",
            "corr-manual-sync-reference", "tests", new(created.Version, "EXT-MANUAL", "1",
                created.RequiredQuantity, created.SelectedUnitCost, new DateOnly(2026, 8, 12),
                ProcurementHandoffStatuses.ExternalPoCreated, DateTime.UtcNow));

        var status = await new ProcurementIntegrationService(fixture.Context,
            Configuration(fixture.BusinessUnitId)).GetStatusAsync(fixture.BusinessUnitId);

        Assert.Equal(1, status.AwaitingSynchronization);
        Assert.Equal("AWAITING_SYNCHRONIZATION", status.ConnectorStatus);
    }

    [Fact]
    public async Task Missing_connector_is_reported_truthfully()
    {
        using var fixture = new CustomerAwardTestFixture();
        var status = await new ProcurementIntegrationService(fixture.Context,
            new ConfigurationBuilder().Build()).GetStatusAsync(fixture.BusinessUnitId);

        Assert.False(status.IsConfigured);
        Assert.Equal("NOT_INTEGRATED", status.ConnectorStatus);
        Assert.Equal("Not integrated", status.SourceSystem);
    }

    private static IConfiguration Configuration(long businessUnitId, string sourceSystem = "Disposable ERP") =>
        new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?>
        {
            [$"ProcurementIntegration:Tenants:{businessUnitId}:SourceSystem"] = sourceSystem,
            [$"ProcurementIntegration:Tenants:{businessUnitId}:SharedSecret"] = Secret
        }).Build();

    private static string Sign(string timestamp, string body) => Convert.ToHexString(
        HMACSHA256.HashData(Encoding.UTF8.GetBytes(Secret),
            Encoding.UTF8.GetBytes($"{timestamp}\n{body}"))).ToLowerInvariant();
}
