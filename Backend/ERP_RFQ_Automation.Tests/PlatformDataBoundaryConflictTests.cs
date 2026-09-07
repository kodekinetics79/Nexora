using System.Security.Claims;
using System.Text.Json;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Platform.Auth;
using ERP_RFQ_Automation.Platform.DataAssets;
using ERP_RFQ_Automation.Platform.Models;
using ERP_RFQ_Automation.Platform.Services;
using ERP_RFQ_Automation.Tests.Support;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// The dead end at the top of the activation tab, and the exact width of the way out.
///
/// <para><b>What was there.</b> Recording the deployment's database is refused while any tenant is
/// still registered against a different one — correctly. The registered assets are the evidence;
/// this row is the claim about them, and rewriting the claim under assets that still carry the old
/// value is precisely how a residency control gets satisfied by editing a string.</para>
///
/// <para><b>Why that was still a defect.</b> The refusal said "re-register or move them first" and
/// named no control, anywhere in the product, that does either. One tenant left behind by a test
/// run therefore blocked <c>data.residency-isolation</c> for every other tenant on the deployment,
/// permanently, with an operator reading a red toast that described a task they could not perform.
/// A gate nobody can pass is not a control; it is an outage with a compliance rationale.</para>
///
/// <para><b>The width of the fix.</b> The move is honoured ONLY when the values were read from the
/// live connection. Correcting a registration to what the database itself reports is fixing
/// evidence. Dragging registrations along behind a value somebody TYPED is the attack the check
/// exists to stop, and stays refused. Every moved tenant is named in the audit entry.</para>
/// </summary>
public sealed class PlatformDataBoundaryConflictTests
{
    private const string ObservedProvider = "neon-ep-super-sea-admna6dt";
    private const string ObservedRegion = "us-east-1";
    private const string ObservedHost = "ep-super-sea-admna6dt-pooler.c-2.us-east-1.aws.neon.tech";

    [Fact]
    public async Task The_refusal_names_the_tenants_and_says_the_move_is_available()
    {
        using var database = new TestDb();
        await using var context = database.ContextFor(null);
        await SeedAsync(context,
            Stale(4210, "neon-ep-old-project", "eu-west-1"),
            Stale(4211, "neon-ep-old-project", "eu-west-1"));

        var result = await Controller(context).Record(Confirm(), default);

        // Ids, not just a sentence: without them the console can print the refusal and nothing else.
        var body = Payload(result);
        Assert.Equal(new long[] { 4210, 4211 }, Ids(body, "conflictingTenantIds"));
        Assert.True(body.GetProperty("canReregister").GetBoolean());
        Assert.Empty(await context.Set<PlatformDataBoundarySettings>().ToListAsync());
    }

    [Fact]
    public async Task Asking_for_the_move_records_the_row_corrects_the_assets_and_names_every_one_moved()
    {
        using var database = new TestDb();
        await using var context = database.ContextFor(null);
        await SeedAsync(context,
            Stale(4210, "neon-ep-old-project", "eu-west-1"),
            Stale(4211, ObservedProvider, ObservedRegion));

        var result = await Controller(context).Record(Confirm(reregister: true), default);

        Assert.IsType<OkObjectResult>(result.Result);
        var settings = await context.Set<PlatformDataBoundarySettings>().SingleAsync();
        Assert.Equal(ObservedProvider, settings.OpaqueProviderReference);
        Assert.Equal(ProvenanceBases.ObservedAndConfirmed, settings.Basis);

        // The stale one moved; the one already correct was left alone and is NOT claimed as moved.
        var assets = await context.Set<TenantDataAsset>().OrderBy(a => a.TenantId).ToListAsync();
        Assert.All(assets, a => Assert.Equal(ObservedProvider, a.OpaqueProviderReference));
        Assert.All(assets, a => Assert.Equal(ObservedRegion, a.Region));

        var audit = await context.Set<PlatformAuditLog>()
            .SingleAsync(x => x.Action == PlatformDataBoundariesController.RecordAction);
        var metadata = JsonDocument.Parse(audit.Metadata!).RootElement;
        Assert.Equal(new long[] { 4210 }, Ids(metadata, "reregisteredTenantIds"));
    }

    [Fact]
    public async Task A_typed_value_can_never_drag_the_registrations_along_behind_it()
    {
        using var database = new TestDb();
        await using var context = database.ContextFor(null);
        await SeedAsync(context, Stale(4210, "neon-ep-old-project", "eu-west-1"));

        // The flag is set, and set truthfully — this request still asks to overwrite the evidence
        // with a string somebody typed, which is the whole thing the conflict check protects.
        var result = await Controller(context).Record(new RecordPlatformDataBoundaryRequest
        {
            OpaqueProviderReference = "neon-ep-somewhere-else",
            Region = "me-south-1",
            BackupPolicyReference = "neon-pitr-7d",
            BackupPolicyVersion = 3,
            Reason = "Typing a database this process is not connected to.",
            ReregisterConflictingTenants = true
        }, default);

        var body = Payload(result);
        Assert.False(body.GetProperty("canReregister").GetBoolean());
        Assert.Empty(await context.Set<PlatformDataBoundarySettings>().ToListAsync());
        var asset = await context.Set<TenantDataAsset>().SingleAsync();
        Assert.Equal("neon-ep-old-project", asset.OpaqueProviderReference);
        Assert.Equal("eu-west-1", asset.Region);
    }

    [Fact]
    public async Task Nothing_to_move_still_records_and_claims_no_moves()
    {
        using var database = new TestDb();
        await using var context = database.ContextFor(null);
        await SeedAsync(context, Stale(4211, ObservedProvider, ObservedRegion));

        // The flag is inert when there is no conflict: it must not become a reason to touch rows.
        var result = await Controller(context).Record(Confirm(reregister: true), default);

        Assert.IsType<OkObjectResult>(result.Result);
        var audit = await context.Set<PlatformAuditLog>()
            .SingleAsync(x => x.Action == PlatformDataBoundariesController.RecordAction);
        var metadata = JsonDocument.Parse(audit.Metadata!).RootElement;
        Assert.Empty(Ids(metadata, "reregisteredTenantIds"));
    }

    /// <summary>Assets hang off real tenants, so the tenant each one belongs to is created first.</summary>
    private static async Task SeedAsync(ErpRfqAutomationContext context, params TenantDataAsset[] assets)
    {
        foreach (var asset in assets)
        {
            context.Set<Tenant>().Add(new Tenant
            {
                Id = asset.TenantId,
                Name = $"Conflict tenant {asset.TenantId}",
                Slug = $"conflict-tenant-{asset.TenantId}",
                Status = TenantStatus.Provisioning,
                DataRegion = asset.Region
            });
            context.Set<TenantDataAsset>().Add(asset);
        }

        await context.SaveChangesAsync();
    }

    private static RecordPlatformDataBoundaryRequest Confirm(bool reregister = false) => new()
    {
        BackupPolicyReference = "neon-pitr-7d",
        BackupPolicyVersion = 3,
        ReregisterConflictingTenants = reregister
    };

    private static PlatformDataBoundariesController Controller(ErpRfqAutomationContext context)
    {
        var controller = new PlatformDataBoundariesController(
            context,
            new EmptyManifest(),
            new FixedObserver(),
            new PlatformAuditService(context, NullLogger<PlatformAuditService>.Instance),
            NullLogger<PlatformDataBoundariesController>.Instance);
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = Actor() }
        };
        return controller;
    }

    private static JsonElement Payload(ActionResult<PlatformDataBoundaryManifestDto> result)
    {
        var conflict = Assert.IsType<ConflictObjectResult>(result.Result);
        return JsonSerializer.SerializeToElement(conflict.Value);
    }

    private static long[] Ids(JsonElement body, string property) =>
        body.GetProperty(property).EnumerateArray().Select(x => x.GetInt64()).ToArray();

    private static TenantDataAsset Stale(long tenantId, string provider, string region) => new()
    {
        TenantId = tenantId,
        LogicalKey = TenantDataAssetRegistryService.PostgreSqlLogicalKey,
        OpaqueProviderReference = provider,
        Region = region,
        BackupPolicyReference = "neon-pitr-7d",
        BackupPolicyVersion = 3,
        CreatedOn = new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc),
        CreatedBy = "owner@nexora.test"
    };

    private static ClaimsPrincipal Actor() => new(new ClaimsIdentity(new[]
    {
        new Claim("sub", "7"),
        new Claim("email", "owner@nexora.test"),
        new Claim(PlatformAuthConstants.PlatformRoleClaim, nameof(PlatformRole.Owner))
    }, "test"));

    /// <summary>A deployment that has declared nothing — the state the console screen exists for.</summary>
    private sealed class EmptyManifest : IPlatformDataBoundaryManifest
    {
        public bool IsConfigured => false;
        public IReadOnlyList<PlatformDataBoundary> Boundaries => [];
        public IReadOnlyList<PlatformDataBoundaryDefect> Defects => [];
        public PlatformDataBoundary? For(string assetType) => null;
        public string? DefectFor(string assetType) => null;
    }

    private sealed class FixedObserver : IDatabaseSelfObserver
    {
        public DatabaseSelfObservation Observe(ErpRfqAutomationContext db) => new(
            ObservedHost, "Neon", ObservedProvider, ObservedRegion,
            "Read from the database host this process is connected to.");
    }
}
