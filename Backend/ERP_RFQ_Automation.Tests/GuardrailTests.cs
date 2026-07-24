using ERP_RFQ_Automation.Agent;
using ERP_RFQ_Automation.Agent.Guardrails;
using ERP_RFQ_Automation.Tests.Support;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// The autonomy guardrail engine (Agent/Guardrails/AgentGuardrail.cs) is the ONLY thing
/// standing between the copilot and unattended mutations, so its precedence order is
/// pinned here test-by-test:
///   per-tool override > autonomy level (Observe deny / Suggest approval) >
///   category flags > value caps > fail-safe RequireApproval for unknown mutations.
/// Read-only tools bypass the engine entirely.
/// </summary>
public class GuardrailTests
{
    private const long Bu1 = 1;

    private static readonly AgentToolContext Ctx = new() { BusinessUnitId = Bu1, UserId = 42, UserName = "tester" };

    private static readonly System.Text.Json.JsonElement EmptyInput = AgentSeed.Json("{}");

    // ---- 1. Read-only tools are always allowed, even at the most restrictive level ----

    [Fact]
    public async Task ReadOnlyTool_IsAllowed_EvenAtObserve()
    {
        using var db = new TestDb();
        using (var seed = db.ContextFor(null))
        {
            AgentSeed.Policy(seed, Bu1, AgentAutonomyLevel.Observe);
            seed.SaveChanges();
        }

        using var ctx = db.ContextFor(Bu1);
        var guardrail = new AgentGuardrail(ctx);
        var tool = new FakeAgentTool(AgentToolNames.SearchRfqs, isMutation: false);

        var decision = await guardrail.EvaluateAsync(tool, EmptyInput, Ctx, CancellationToken.None);

        Assert.Equal(GuardrailOutcome.Allow, decision.Outcome);
        Assert.Contains("Read-only", decision.Reason);
    }

    // ---- 2. Observe denies every mutation ----

    [Fact]
    public async Task Observe_DeniesMutations()
    {
        using var db = new TestDb();
        using (var seed = db.ContextFor(null))
        {
            AgentSeed.Policy(seed, Bu1, AgentAutonomyLevel.Observe);
            seed.SaveChanges();
        }

        using var ctx = db.ContextFor(Bu1);
        var guardrail = new AgentGuardrail(ctx);
        var tool = new FakeAgentTool(AgentToolNames.SendRfqToSuppliers, isMutation: true);

        var decision = await guardrail.EvaluateAsync(tool, EmptyInput, Ctx, CancellationToken.None);

        Assert.Equal(GuardrailOutcome.Deny, decision.Outcome);
        Assert.Contains("Observe", decision.Reason);
    }

    // ---- 3. No stored policy -> conservative default (Suggest) -> approval required ----

    [Fact]
    public async Task NoPolicyRow_DefaultsToSuggest_MutationRequiresApproval()
    {
        using var db = new TestDb(); // nothing seeded: the default-policy path
        using var ctx = db.ContextFor(Bu1);
        var guardrail = new AgentGuardrail(ctx);

        var policy = await guardrail.GetPolicyAsync(Bu1, CancellationToken.None);
        Assert.Equal(AgentAutonomyLevel.Suggest, policy.AutonomyLevel);
        Assert.Equal(0m, policy.MaxAutoAwardValue);
        Assert.Equal(0m, policy.MaxAutoOrderValue);
        Assert.True(policy.RequireApprovalForAwards);
        Assert.True(policy.RequireApprovalForOrders);
        Assert.True(policy.RequireApprovalForSupplierEmails);

        var tool = new FakeAgentTool(AgentToolNames.AwardRfq, isMutation: true);
        var decision = await guardrail.EvaluateAsync(tool, EmptyInput, Ctx, CancellationToken.None);

        Assert.Equal(GuardrailOutcome.RequireApproval, decision.Outcome);
        Assert.Contains("Suggest", decision.Reason);
    }

    // ---- 4. Act level: supplier-email category flag gates send_rfq_to_suppliers ----

    [Theory]
    [InlineData(true, GuardrailOutcome.RequireApproval)]
    [InlineData(false, GuardrailOutcome.Allow)]
    public async Task Act_SendRfqToSuppliers_HonoursSupplierEmailFlag(bool requireApprovalForEmails, GuardrailOutcome expected)
    {
        using var db = new TestDb();
        using (var seed = db.ContextFor(null))
        {
            AgentSeed.Policy(seed, Bu1, AgentAutonomyLevel.Act,
                requireApprovalForSupplierEmails: requireApprovalForEmails);
            seed.SaveChanges();
        }

        using var ctx = db.ContextFor(Bu1);
        var guardrail = new AgentGuardrail(ctx);
        var tool = new FakeAgentTool(AgentToolNames.SendRfqToSuppliers, isMutation: true);

        var decision = await guardrail.EvaluateAsync(tool, EmptyInput, Ctx, CancellationToken.None);

        Assert.Equal(expected, decision.Outcome);
    }

    // ---- 5. Act level: award value cap, resolved from the awards[] payload ----
    // ResolveAwardTotal prefers an explicit totalValue/amount hint, else sums
    // unitPrice * quantity (quantity defaulting to 1) over awards[]. Cap is inclusive:
    // amounts strictly greater than MaxAutoAwardValue require approval.

    [Theory]
    // sum(100*5) = 500 <= 1000 -> auto-execute
    [InlineData("{\"awards\":[{\"unitPrice\":100,\"quantity\":5}]}", GuardrailOutcome.Allow)]
    // boundary: sum(200*5) = 1000 == cap -> still allowed (cap is 'at or below')
    [InlineData("{\"awards\":[{\"unitPrice\":200,\"quantity\":5}]}", GuardrailOutcome.Allow)]
    // sum(300*4) = 1200 > 1000 -> approval
    [InlineData("{\"awards\":[{\"unitPrice\":300,\"quantity\":4}]}", GuardrailOutcome.RequireApproval)]
    // missing quantity defaults to 1: 1500*1 = 1500 > 1000 -> approval
    [InlineData("{\"awards\":[{\"unitPrice\":1500}]}", GuardrailOutcome.RequireApproval)]
    // multiple lines accumulate: 600 + 600 = 1200 > 1000 -> approval
    [InlineData("{\"awards\":[{\"unitPrice\":600,\"quantity\":1},{\"unitPrice\":600,\"quantity\":1}]}", GuardrailOutcome.RequireApproval)]
    // explicit totalValue hint beats the awards[] sum (cheap lines, big declared total)
    [InlineData("{\"totalValue\":5000,\"awards\":[{\"unitPrice\":1,\"quantity\":1}]}", GuardrailOutcome.RequireApproval)]
    public async Task Act_AwardRfq_EnforcesMaxAutoAwardValue(string inputJson, GuardrailOutcome expected)
    {
        using var db = new TestDb();
        using (var seed = db.ContextFor(null))
        {
            AgentSeed.Policy(seed, Bu1, AgentAutonomyLevel.Act,
                maxAutoAwardValue: 1000m,
                requireApprovalForAwards: false); // isolate the cap from the category flag
            seed.SaveChanges();
        }

        using var ctx = db.ContextFor(Bu1);
        var guardrail = new AgentGuardrail(ctx);
        var tool = new FakeAgentTool(AgentToolNames.AwardRfq, isMutation: true);

        var decision = await guardrail.EvaluateAsync(tool, AgentSeed.Json(inputJson), Ctx, CancellationToken.None);

        Assert.Equal(expected, decision.Outcome);
    }

    // ---- 6. Per-tool overrides have the highest precedence ----

    [Fact]
    public async Task PerToolOverride_Deny_BeatsFullyPermissiveActPolicy()
    {
        using var db = new TestDb();
        using (var seed = db.ContextFor(null))
        {
            // Everything below the override says "allow": Act, no flags, huge cap.
            AgentSeed.Policy(seed, Bu1, AgentAutonomyLevel.Act,
                maxAutoAwardValue: 1_000_000m,
                requireApprovalForAwards: false,
                requireApprovalForOrders: false,
                requireApprovalForSupplierEmails: false,
                perToolOverrides: "{\"award_rfq\":\"deny\"}");
            seed.SaveChanges();
        }

        using var ctx = db.ContextFor(Bu1);
        var guardrail = new AgentGuardrail(ctx);
        var tool = new FakeAgentTool(AgentToolNames.AwardRfq, isMutation: true);

        var decision = await guardrail.EvaluateAsync(
            tool, AgentSeed.Json("{\"awards\":[{\"unitPrice\":1,\"quantity\":1}]}"), Ctx, CancellationToken.None);

        Assert.Equal(GuardrailOutcome.Deny, decision.Outcome);
        Assert.Contains("override", decision.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PerToolOverride_Allow_BeatsSuggestApprovalRequirement()
    {
        using var db = new TestDb();
        using (var seed = db.ContextFor(null))
        {
            // Suggest would normally force approval for every mutation.
            AgentSeed.Policy(seed, Bu1, AgentAutonomyLevel.Suggest,
                perToolOverrides: "{\"capture_supplier_quote\":\"allow\"}");
            seed.SaveChanges();
        }

        using var ctx = db.ContextFor(Bu1);
        var guardrail = new AgentGuardrail(ctx);
        var tool = new FakeAgentTool(AgentToolNames.CaptureSupplierQuote, isMutation: true);

        var decision = await guardrail.EvaluateAsync(tool, EmptyInput, Ctx, CancellationToken.None);

        Assert.Equal(GuardrailOutcome.Allow, decision.Outcome);
        Assert.Contains("override", decision.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PerToolOverride_OnlyAffectsTheNamedTool()
    {
        using var db = new TestDb();
        using (var seed = db.ContextFor(null))
        {
            AgentSeed.Policy(seed, Bu1, AgentAutonomyLevel.Suggest,
                perToolOverrides: "{\"award_rfq\":\"allow\"}");
            seed.SaveChanges();
        }

        using var ctx = db.ContextFor(Bu1);
        var guardrail = new AgentGuardrail(ctx);
        var otherTool = new FakeAgentTool(AgentToolNames.CaptureSupplierQuote, isMutation: true);

        var decision = await guardrail.EvaluateAsync(otherTool, EmptyInput, Ctx, CancellationToken.None);

        // The sibling mutation still goes through Suggest-level approval.
        Assert.Equal(GuardrailOutcome.RequireApproval, decision.Outcome);
    }

    // ---- 7. Unknown mutation tools fail safe ----

    [Fact]
    public async Task UnknownMutationTool_FailsSafe_RequiresApproval()
    {
        using var db = new TestDb();
        using (var seed = db.ContextFor(null))
        {
            // Maximally permissive policy: only the fail-safe default can stop this.
            AgentSeed.Policy(seed, Bu1, AgentAutonomyLevel.Act,
                maxAutoAwardValue: 1_000_000_000m,
                maxAutoOrderValue: 1_000_000_000m,
                requireApprovalForAwards: false,
                requireApprovalForOrders: false,
                requireApprovalForSupplierEmails: false);
            seed.SaveChanges();
        }

        using var ctx = db.ContextFor(Bu1);
        var guardrail = new AgentGuardrail(ctx);
        var rogueTool = new FakeAgentTool("nuke_everything", isMutation: true);

        var decision = await guardrail.EvaluateAsync(rogueTool, EmptyInput, Ctx, CancellationToken.None);

        Assert.Equal(GuardrailOutcome.RequireApproval, decision.Outcome);
        Assert.Contains("Unrecognized", decision.Reason);
    }

    // ---- 8. Malformed / unusable PerToolOverrides JSON is ignored, never thrown ----

    [Theory]
    [InlineData("{ this is not json")]                 // syntactically invalid
    [InlineData("[\"allow\"]")]                        // valid JSON but not an object
    [InlineData("{\"award_rfq\":123}")]                // override value is not a string
    [InlineData("{\"award_rfq\":\"frobnicate\"}")]     // unknown override verb
    [InlineData("{\"some_other_tool\":\"allow\"}")]    // no entry for this tool
    public async Task MalformedOrInapplicableOverrides_FallThroughToLevelLogic(string overridesJson)
    {
        using var db = new TestDb();
        using (var seed = db.ContextFor(null))
        {
            AgentSeed.Policy(seed, Bu1, AgentAutonomyLevel.Observe, perToolOverrides: overridesJson);
            seed.SaveChanges();
        }

        using var ctx = db.ContextFor(Bu1);
        var guardrail = new AgentGuardrail(ctx);
        var tool = new FakeAgentTool(AgentToolNames.AwardRfq, isMutation: true);

        // Must not throw; and must land on the Observe deny, proving the override was
        // discarded rather than (say) parsed as "allow".
        var decision = await guardrail.EvaluateAsync(tool, EmptyInput, Ctx, CancellationToken.None);

        Assert.Equal(GuardrailOutcome.Deny, decision.Outcome);
        Assert.Contains("Observe", decision.Reason);
    }
}
