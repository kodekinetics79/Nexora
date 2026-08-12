using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ERP_RFQ_Automation.Billing;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Platform.Activation;
using ERP_RFQ_Automation.Platform.DataAssets;
using ERP_RFQ_Automation.Platform.Entitlements;
using ERP_RFQ_Automation.Platform.Models;
using ERP_RFQ_Automation.Platform.Onboarding;
using ERP_RFQ_Automation.Platform.Provisioning;
using ERP_RFQ_Automation.Platform.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// Self-provisioning data boundaries, and the four properties that make them safe to trust.
///
/// <para><b>The state this replaces.</b> A Nexora-hosted tenant could not be switched on until an
/// operator had hand-typed the platform's own provider reference, region and backup-policy version
/// into a form, and hand-hashed an "evidence document" about a database Nexora runs itself.
/// Deletion certification then demanded the same exercise nine times over, once per boundary type.
/// Two more controls asked for attestations nobody could truthfully make. Five operator
/// submissions per tenant, four of them the platform asking a human to describe the platform.</para>
///
/// <para><b>What must not have been traded away for that.</b> Each test below pins one half of the
/// bargain: the automation works end to end; a probe that DISAGREES fails and leaves the control
/// blocking; a deployment that declares nothing keeps the manual path byte for byte; and the
/// evidence a green tick rests on is a real document whose hash can be recomputed from the audit
/// trail.</para>
/// </summary>
public sealed class PlatformDataBoundaryAutomationTests
{
    private const string Region = "us-east-1";

    /// <summary>
    /// A deployment that has described its own estate: all nine boundary types, each with the
    /// provider reference, region, backup policy and version only the deployment can know.
    /// </summary>
    private static Dictionary<string, string?> Manifest(string region = Region) =>
        TenantDataAssetTypes.All.SelectMany(type => new Dictionary<string, string?>
        {
            [$"Platform:DataBoundaries:{type}:OpaqueProviderReference"] = $"nexora-shared-{type.ToLowerInvariant()}",
            [$"Platform:DataBoundaries:{type}:Region"] = region,
            [$"Platform:DataBoundaries:{type}:BackupPolicyReference"] = $"nexora-backup-policy-{type.ToLowerInvariant()}",
            [$"Platform:DataBoundaries:{type}:BackupPolicyVersion"] = "3"
        }).ToDictionary(x => x.Key, x => x.Value);

    // ---- 1. the whole point -------------------------------------------------------------------

    [Fact]
    public async Task A_provisioned_tenant_reaches_active_with_no_manual_evidence_when_the_manifest_is_configured()
    {
        using var harness = new ProvisioningHarness(Manifest());
        var tenantId = await ReadyTenantAsync(harness, "northwind-auto", "ada@northwind.test");

        var decision = await EvaluateAsync(harness, tenantId);

        // Not a single operator submission stands between provisioning and Active. The three
        // controls that used to require one — the data boundary, the MFA attestation and the
        // integration evidence — are answered by the platform, by a moved gate, and by a fact.
        Assert.Empty(decision.BlockingControls);
        Assert.True(decision.Ready);
        Assert.True(decision.Controls.Single(x => x.Code == "data.residency-isolation").Satisfied);
        Assert.True(decision.Controls.Single(x => x.Code == "integrations.mandatory").Satisfied);
        Assert.Equal(ActivationControlDispositions.CertificationOnly,
            decision.Controls.Single(x => x.Code == "security.privileged-mfa-policy").Disposition);

        // And nothing was quietly certified along the way: the one control that still needs a human
        // attestation still stops the tenant being called production-ready.
        Assert.False(decision.ProductionReadiness.Certifiable);
        Assert.Contains("security.privileged-mfa-policy", decision.ProductionBlockingControls);

        var activated = await ActivateAsync(harness, tenantId);
        Assert.True(activated.Ready);
        await using var db = harness.Context();
        Assert.Equal(TenantStatus.Active,
            (await db.Set<Tenant>().IgnoreQueryFilters().SingleAsync(x => x.Id == tenantId)).Status);
    }

    /// <summary>
    /// Deletion certification demanded nine boundary TYPES per tenant and got them from an
    /// operator's memory. The types the deployment declares now come from the deployment.
    /// </summary>
    [Fact]
    public async Task Every_declared_boundary_type_is_registered_so_deletion_certification_stops_asking()
    {
        using var harness = new ProvisioningHarness(Manifest());
        var tenantId = await ReadyTenantAsync(harness, "northwind-boundaries", "grace@northwind.test");

        await using var db = harness.Context();
        var assets = await db.Set<TenantDataAsset>().AsNoTracking()
            .Where(x => x.TenantId == tenantId).ToListAsync();

        Assert.Equal(
            TenantDataAssetTypes.All.Order(),
            assets.Select(x => x.AssetType).Order());

        // Registered automatically means registered BY the automation, visibly. An operator reading
        // this row has to be able to tell a probe from a person without leaving the page.
        Assert.All(assets, asset =>
        {
            Assert.Equal(PlatformAutomationActors.Provisioning, asset.CreatedBy);
            Assert.Equal(Region, asset.Region);
            Assert.Equal(3, asset.BackupPolicyVersion);
        });

        // Only the boundary the platform can genuinely observe is VERIFIED. Registering a
        // subprocessor is not a claim that anything about it has been checked.
        var verified = assets.Where(x => x.Status == TenantDataAssetStatuses.Verified).ToArray();
        Assert.Equal([TenantDataAssetRegistryService.PostgreSqlLogicalKey],
            verified.Select(x => x.LogicalKey));
        Assert.Equal(PlatformAutomationActors.Provisioning, verified[0].VerifiedBy);

        var decision = await new TenantDataRecoveryService(db, new NoopAudit()).DecisionAsync(tenantId, default);
        Assert.DoesNotContain(decision.Blockers, blocker => blocker.Contains("is not registered"));
    }

    // ---- 2. the evidence is real --------------------------------------------------------------

    /// <summary>
    /// The hash is the whole argument. A constant here, or a hash of a placeholder, would make
    /// <c>data.residency-isolation</c> read as verified on every tenant forever — including the one
    /// whose business unit is missing. So this recomputes it from the document the audit trail
    /// kept, and refuses to accept the reference unless the two agree.
    /// </summary>
    [Fact]
    public async Task The_verification_evidence_is_a_recomputable_hash_of_a_recorded_observation()
    {
        using var harness = new ProvisioningHarness(Manifest());
        var tenantId = await ReadyTenantAsync(harness, "northwind-evidence", "hopper@northwind.test");

        await using var db = harness.Context();
        var asset = await db.Set<TenantDataAsset>().AsNoTracking().SingleAsync(x =>
            x.TenantId == tenantId && x.LogicalKey == TenantDataAssetRegistryService.PostgreSqlLogicalKey);
        var probeAudit = await db.Set<PlatformAuditLog>().AsNoTracking()
            .Where(x => x.ActAsTenantId == tenantId && x.Action == PlatformDataBoundaryProvisioner.ProbeAction)
            .OrderByDescending(x => x.Id).FirstAsync();

        var metadata = JsonSerializer.Deserialize<JsonElement>(probeAudit.Metadata!);
        var observation = metadata.GetProperty("observationJson").GetString()!;
        var recomputed = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(observation)))
            .ToLowerInvariant();

        Assert.Equal(recomputed, asset.VerificationEvidenceSha256);
        Assert.EndsWith($"sha256-{recomputed}", asset.VerificationEvidenceReference);

        // The document says what was actually looked at, so a reader can tell what the pass means.
        var document = JsonSerializer.Deserialize<JsonElement>(observation);
        Assert.True(document.GetProperty("Satisfied").GetBoolean());
        Assert.Equal(asset.VerifiedBusinessUnitId, document.GetProperty("ObservedBusinessUnitId").GetInt64());
        Assert.Equal(Region, document.GetProperty("DeclaredRegion").GetString());
        Assert.True(document.GetProperty("IsolationEnforced").GetBoolean());
        // Named honestly per provider. A SQLite observation must never be readable as a PostgreSQL
        // one: this suite has no roles and no row-level security, and the document says so.
        Assert.Equal("ef-global-query-filter", document.GetProperty("IsolationMechanism").GetString());
        Assert.Contains("Sqlite", document.GetProperty("DatabaseProvider").GetString());
    }

    // ---- 3. a disagreeing probe fails ---------------------------------------------------------

    /// <summary>
    /// The property that makes the rest of this trustworthy. The manifest claims one region, the
    /// contract says another, and the automation refuses: the step fails, nothing is registered,
    /// nothing is verified, and the control an operator would have satisfied by hand stays blocking.
    /// </summary>
    [Fact]
    public async Task A_disagreeing_probe_fails_the_step_and_leaves_the_data_control_blocking()
    {
        // The deployment says its estate is in eu-west-1; the tenant's contract says us-east-1.
        using var harness = new ProvisioningHarness(Manifest("eu-west-1"));
        var planId = await ReadyPlanAsync(harness);
        var execution = await harness.ProvisionAsync(ProvisioningHarness.Request(
            "northwind-region", "turing@northwind.test", planId,
            activation: AdminActivationMethods.Password, password: "Correct-Horse-9!",
            dataRegion: Region));

        Assert.Equal(ProvisioningExecutionState.Failed, execution.State);
        Assert.Equal(ProvisioningStepCodes.DataBoundaries, execution.FailedStep);
        Assert.Contains("eu-west-1", execution.FailureReason);
        Assert.Contains(Region, execution.FailureReason);

        await using var db = harness.Context();
        var tenantId = execution.TenantId!.Value;

        // Nothing half-done. A Registered-but-unverified asset would show a green tick beside a red
        // control and be unexplainable from the console, so the step registers nothing at all.
        Assert.Empty(await db.Set<TenantDataAsset>().AsNoTracking()
            .Where(x => x.TenantId == tenantId).ToListAsync());

        var decision = await EvaluateAsync(harness, tenantId);
        Assert.Contains("data.residency-isolation", decision.BlockingControls);
        Assert.False(decision.Ready);
    }

    // ---- 4. absent configuration changes nothing ----------------------------------------------

    /// <summary>
    /// A deployment that has declared nothing must be indistinguishable from the one that existed
    /// before this feature: the step runs, registers nothing, invents nothing, and the operator's
    /// register-then-verify screens are still the only way past the control.
    /// </summary>
    [Fact]
    public async Task An_absent_manifest_preserves_the_manual_path_exactly()
    {
        using var harness = new ProvisioningHarness();
        var tenantId = await ReadyTenantAsync(harness, "northwind-manual", "lovelace@northwind.test");

        await using var db = harness.Context();
        Assert.Empty(await db.Set<TenantDataAsset>().AsNoTracking()
            .Where(x => x.TenantId == tenantId).ToListAsync());
        Assert.Empty(await db.Set<PlatformAuditLog>().AsNoTracking()
            .Where(x => x.ActAsTenantId == tenantId
                        && x.Action == PlatformDataBoundaryProvisioner.ProbeAction).ToListAsync());

        // The step reports the absence rather than passing silently, so an operator wondering why
        // the boundary is still unregistered gets an answer from the provisioning journal.
        var execution = await db.Set<ProvisioningExecution>().AsNoTracking().Include(x => x.Steps)
            .SingleAsync(x => x.TenantId == tenantId);
        var step = execution.Steps.Single(x => x.StepCode == ProvisioningStepCodes.DataBoundaries);
        Assert.Equal(ProvisioningStepStatus.Succeeded, step.Status);
        var detail = JsonSerializer.Deserialize<JsonElement>(step.Detail!);
        Assert.False(detail.GetProperty("configured").GetBoolean());
        Assert.Equal(TenantDataAssetTypes.All.Count,
            detail.GetProperty("undeclaredAssetTypes").GetArrayLength());

        var decision = await EvaluateAsync(harness, tenantId);
        Assert.Contains("data.residency-isolation", decision.BlockingControls);
        Assert.False(decision.Ready);
    }

    /// <summary>
    /// Half a manifest is not a licence to guess the other half. A boundary declared without a
    /// provider reference is refused, recorded, and left on the manual path — while the boundaries
    /// that WERE declared properly still register.
    /// </summary>
    [Fact]
    public async Task A_boundary_declared_without_a_provider_reference_is_refused_rather_than_defaulted()
    {
        var manifest = Manifest();
        manifest[$"Platform:DataBoundaries:{TenantDataAssetTypes.Cache}:OpaqueProviderReference"] = "";
        using var harness = new ProvisioningHarness(manifest);
        var tenantId = await ReadyTenantAsync(harness, "northwind-partial", "babbage@northwind.test");

        await using var db = harness.Context();
        var assets = await db.Set<TenantDataAsset>().AsNoTracking()
            .Where(x => x.TenantId == tenantId).Select(x => x.AssetType).ToListAsync();

        Assert.DoesNotContain(TenantDataAssetTypes.Cache, assets);
        Assert.Contains(TenantDataAssetTypes.PostgreSqlTenantScope, assets);

        // Deletion certification is still blocked on the one nobody described, by name.
        var decision = await new TenantDataRecoveryService(db, new NoopAudit()).DecisionAsync(tenantId, default);
        Assert.Contains(decision.Blockers,
            blocker => blocker.Contains($"'{TenantDataAssetTypes.Cache}' is not registered"));
    }

    // ---- 5. integrations.mandatory is vacuous only when it is true -----------------------------

    [Fact]
    public async Task Integrations_mandatory_blocks_again_once_an_integration_is_configured()
    {
        using var harness = new ProvisioningHarness(Manifest());
        var tenantId = await ReadyTenantAsync(harness, "northwind-erp", "knuth@northwind.test");

        long businessUnitId;
        await using (var db = harness.Context())
            businessUnitId = (await db.Set<Tenant>().IgnoreQueryFilters()
                .SingleAsync(x => x.Id == tenantId)).PrimaryBusinessUnitId!.Value;

        // Nothing configured: "there is nothing to be healthy" is a fact, and the control passes on
        // it rather than on an attestation about an integration that does not exist.
        var withoutIntegration = await EvaluateAsync(harness, tenantId);
        Assert.True(withoutIntegration.Controls.Single(x => x.Code == "integrations.mandatory").Satisfied);
        Assert.True(withoutIntegration.Ready);

        // The same tenant, the same evidence (none), one ERP connector configured — and the control
        // is back to demanding current evidence or an explicit deferral.
        var withConnector = await EvaluateAsync(harness, tenantId, new Dictionary<string, string?>
        {
            [$"ProcurementIntegration:Tenants:{businessUnitId}:SourceSystem"] = "SAP S/4HANA",
            [$"ProcurementIntegration:Tenants:{businessUnitId}:SharedSecret"] = new string('s', 40)
        });

        var control = withConnector.Controls.Single(x => x.Code == "integrations.mandatory");
        Assert.False(control.Satisfied);
        Assert.Contains(ConfiguredMandatoryIntegrationInventory.ProcurementErpConnector, control.Detail);
        Assert.Contains("integrations.mandatory", withConnector.BlockingControls);
        Assert.False(withConnector.Ready);
    }

    // ---- 6. re-running is safe ----------------------------------------------------------------

    /// <summary>
    /// The reconciler's half of the bargain. Registration is idempotent, but VERIFICATION is not —
    /// every call rewrites the evidence with a freshly hashed observation — so a resume that
    /// re-ran a committed step would churn the verification history of a boundary nothing was
    /// wrong with.
    /// </summary>
    [Fact]
    public async Task A_resume_reconciles_the_verified_boundary_instead_of_re_verifying_it()
    {
        using var harness = new ProvisioningHarness(Manifest());
        var tenantId = await ReadyTenantAsync(harness, "northwind-resume", "dijkstra@northwind.test");

        long executionId;
        string? evidenceBefore;
        await using (var db = harness.Context())
        {
            var execution = await db.Set<ProvisioningExecution>().Include(x => x.Steps)
                .SingleAsync(x => x.TenantId == tenantId);
            executionId = execution.Id;
            evidenceBefore = (await db.Set<TenantDataAsset>().AsNoTracking().SingleAsync(x =>
                x.TenantId == tenantId
                && x.LogicalKey == TenantDataAssetRegistryService.PostgreSqlLogicalKey))
                .VerificationEvidenceReference;

            // The lost-acknowledgement shape: the work committed, the verdict says otherwise.
            var step = execution.Steps.Single(x => x.StepCode == ProvisioningStepCodes.DataBoundaries);
            step.Status = ProvisioningStepStatus.Failed;
            step.FailureReason = "Connection reset before the commit was acknowledged.";
            execution.State = ProvisioningExecutionState.Failed;
            execution.FailedStep = ProvisioningStepCodes.DataBoundaries;
            execution.FailureIsTerminal = false;
            execution.CompletedOn = null;
            await db.SaveChangesAsync();
        }

        Assert.NotNull(await harness.Runner().RunAsync(executionId));

        await using var after = harness.Context();
        var asset = await after.Set<TenantDataAsset>().AsNoTracking().SingleAsync(x =>
            x.TenantId == tenantId && x.LogicalKey == TenantDataAssetRegistryService.PostgreSqlLogicalKey);
        Assert.Equal(1, asset.VerificationVersion);
        Assert.Equal(evidenceBefore, asset.VerificationEvidenceReference);

        var reconciled = await after.Set<ProvisioningExecution>().AsNoTracking().Include(x => x.Steps)
            .SingleAsync(x => x.Id == executionId);
        var reconciledStep = reconciled.Steps.Single(x => x.StepCode == ProvisioningStepCodes.DataBoundaries);
        Assert.Equal(ProvisioningStepStatus.Succeeded, reconciledStep.Status);
        Assert.Contains("\"reconciled\":true", reconciledStep.Detail);
    }

    // ---- fixture ------------------------------------------------------------------------------

    private static async Task<long> ReadyPlanAsync(ProvisioningHarness harness)
    {
        await using var db = harness.Context();
        var plan = new Plan
        {
            Code = $"enterprise-{Guid.NewGuid():N}", Name = "Enterprise", IsActive = true,
            MaxSeats = 25, MaxDocsPerMonth = 5000, MaxConcurrentExtractionJobs = 4, Weight = 3,
            MonthlyPriceUsd = 999m,
            Features = JsonSerializer.Serialize(
                TypedEntitlementCatalog.Keys.ToDictionary(key => key, _ => true))
        };
        db.Set<Plan>().Add(plan);
        await db.SaveChangesAsync();
        return plan.Id;
    }

    /// <summary>
    /// A genuinely provisioned tenant with every OPERATOR-supplied fact recorded and nothing else.
    /// Built by running the real provisioning engine, so the controls that read provisioning's own
    /// output — including the new data-boundary step — are answered by provisioning.
    /// </summary>
    private static async Task<long> ReadyTenantAsync(ProvisioningHarness harness, string slug, string email)
    {
        var planId = await ReadyPlanAsync(harness);
        long rateCardId;
        await using (var db = harness.Context())
        {
            var card = new RateCard
            {
                Code = $"standard-{Guid.NewGuid():N}", Currency = "USD", IsActive = true,
                EffectiveFromUtc = DateTime.UtcNow.AddDays(-30), Version = 1
            };
            card.Lines.Add(new RateCardLine
            {
                MeterKey = "documents", IncludedQuantity = 1000, UnitPrice = 0.25m, Unit = "document"
            });
            db.Set<RateCard>().Add(card);
            await db.SaveChangesAsync();
            rateCardId = card.Id;
        }

        // The password path, so the founding administrator is created ACTIVE and
        // admin.first-activated is answered by a real user holding a real Owner-rank role.
        var execution = await harness.ProvisionAsync(ProvisioningHarness.Request(
            slug, email, planId, activation: AdminActivationMethods.Password,
            password: "Correct-Horse-9!", dataRegion: Region));
        Assert.Equal(ProvisioningExecutionState.Succeeded, execution.State);
        var tenantId = execution.TenantId!.Value;

        await using (var db = harness.Context())
        {
            var tenant = await db.Set<Tenant>().IgnoreQueryFilters().SingleAsync(x => x.Id == tenantId);
            tenant.LegalName = "Northwind Trading LLC";
            tenant.RegistrationNumber = "CR-1010101010";
            tenant.TaxNumber = "310000000000003";
            tenant.ContactEmail = "info@northwind.test";
            tenant.BillingContactName = "Accounts Payable";
            tenant.BillingContactEmail = "ap@northwind.test";
            tenant.BillingAddress = "1 Trading Way, Riyadh";
            tenant.PaymentTermsDays = 30;
            tenant.BaseCurrencyCode = "USD";
            tenant.RateCardId = rateCardId;
            tenant.ContractStartOn = DateTime.UtcNow.AddDays(-1);
            tenant.BillingStartsOn = DateTime.UtcNow.AddDays(-1);
            await db.SaveChangesAsync();
        }

        return tenantId;
    }

    private static async Task<TenantActivationDecision> EvaluateAsync(
        ProvisioningHarness harness, long tenantId, IReadOnlyDictionary<string, string?>? integrations = null)
    {
        await using var db = harness.Context();
        var decision = await PolicyFor(harness, db, integrations).EvaluateAsync(tenantId);
        Assert.NotNull(decision);
        return decision!;
    }

    private static async Task<TenantActivationDecision> ActivateAsync(ProvisioningHarness harness, long tenantId)
    {
        await using var db = harness.Context();
        return await PolicyFor(harness, db).ActivateAsync(tenantId,
            new ClaimsPrincipal(new ClaimsIdentity([new Claim("email", "owner@nexora.app")], "test")),
            new DefaultHttpContext());
    }

    /// <summary>
    /// The policy service as Program.cs composes it: with the integration inventory wired, because
    /// an unwired one keeps integrations.mandatory demanding evidence and would hide the very
    /// behaviour these tests exist to pin.
    /// </summary>
    private static TenantActivationPolicyService PolicyFor(
        ProvisioningHarness harness, ErpRfqAutomationContext db,
        IReadOnlyDictionary<string, string?>? integrations = null)
    {
        var configuration = integrations is null
            ? harness.Configuration
            : new ConfigurationBuilder().AddInMemoryCollection(integrations).Build();
        return new TenantActivationPolicyService(db, new NoopAudit(),
            new TenantAccessService(db, new MemoryCache(new MemoryCacheOptions()),
                NullLogger<TenantAccessService>.Instance),
            posture: null,
            integrations: new ConfiguredMandatoryIntegrationInventory(configuration));
    }

    private sealed class NoopAudit : IPlatformAuditService
    {
        public Task WriteAsync(ClaimsPrincipal actor, string action, string? targetType = null,
            string? targetId = null, object? metadata = null, long? actAsTenantId = null,
            HttpContext? httpContext = null, CancellationToken ct = default) => Task.CompletedTask;
    }
}
