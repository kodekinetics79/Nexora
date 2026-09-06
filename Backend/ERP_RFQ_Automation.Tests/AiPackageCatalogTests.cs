using ERP_RFQ_Automation.AI;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// A plan sells an AI package; a tenant is provisioned with it; the console shows where the two
/// have since diverged. These pin the three rules that make that safe rather than merely tidy.
/// </summary>
public sealed class AiPackageCatalogTests
{
    private static AiProcessingPolicy Policy() =>
        AiProcessingPolicy.CreateSecureDefault(1, "test", DateTime.UtcNow);

    [Fact]
    public void EveryPackagePrintsItsOwnMeaning()
    {
        // A tier whose meaning is not printed beside it is an opaque label with a friendly name,
        // and the operator ends up guessing what they sold.
        Assert.NotEmpty(AiPackages.All);
        foreach (var package in AiPackages.All)
        {
            Assert.False(string.IsNullOrWhiteSpace(package.WhatItTurnsOn));
            Assert.False(string.IsNullOrWhiteSpace(package.SoldAs));
            Assert.True(AiPostures.IsKnown(package.Posture));
        }
    }

    [Fact]
    public void NoPackage_TurnsOnExternalProcessing_BecauseAPlanCannotConsentForACustomer()
    {
        // The load-bearing rule. A plan may say a customer bought cloud extraction; it may not say
        // they agreed to send whole document text off their own infrastructure. A pre-ticked
        // consent is not a decision anybody made.
        foreach (var package in AiPackages.All)
        {
            var policy = Policy();
            AiPackageProvisioning.Apply(policy, package.Key, 2_000_000, false, "test", DateTime.UtcNow);

            Assert.False(policy.ExternalProcessingAllowed);
            Assert.Equal(AiEgressPolicies.RedactedFieldsOnly, policy.EgressPolicy);
        }
    }

    [Fact]
    public void AnUnrecognisedPackage_ReadsAsOff_RatherThanAsWhateverLooksClosest()
    {
        var policy = Policy();
        AiPackageProvisioning.Apply(policy, "Enterprise-Plus", null, false, "test", DateTime.UtcNow);

        Assert.False(policy.IsEnabled);
        Assert.Equal(AiPackages.Off, AiPackages.Resolve("Enterprise-Plus").Key);
    }

    [Fact]
    public void TheAllowanceIsOnlyUnlimitedWhenThePlanSaysSoOutLoud()
    {
        var capped = Policy();
        AiPackageProvisioning.Apply(capped, AiPackages.Private, 2_000_000, false, "test", DateTime.UtcNow);
        Assert.Equal(2_000_000, capped.MonthlyHardTokenLimit);

        var uncapped = Policy();
        AiPackageProvisioning.Apply(uncapped, AiPackages.Private, null, true, "test", DateTime.UtcNow);
        Assert.Null(uncapped.MonthlyHardTokenLimit);

        // Neither stated. The plan endpoint refuses to write this, so it can only be a plan older
        // than the rule — and the safe reading of "nobody decided" is not "no ceiling".
        var undecided = Policy();
        AiPackageProvisioning.Apply(undecided, AiPackages.Private, null, false, "test", DateTime.UtcNow);
        Assert.Null(undecided.MonthlyHardTokenLimit);
    }

    [Fact]
    public void ATenantWithNoPlanAtAll_KeepsTheSecureDefaultRatherThanLosingAI()
    {
        // "No plan" is not "a plan that sells nothing". Reading an absent package as Off silently
        // disabled AI for every plan-less tenant — which is neither what anybody chose, nor what
        // this codebase does elsewhere: an unplanned tenant gets a modest allowance, not a refusal.
        var policy = Policy();
        var before = (policy.IsEnabled, policy.AllowedPurposes, policy.MonthlyHardTokenLimit);

        AiPackageProvisioning.Apply(policy, null, 2_000_000, false, "test", DateTime.UtcNow);

        Assert.Equal(before, (policy.IsEnabled, policy.AllowedPurposes, policy.MonthlyHardTokenLimit));
        Assert.True(policy.IsEnabled);
    }

    [Fact]
    public void BeingTighterThanThePlan_IsNotADeviation()
    {
        // A customer who has not finished setting up, or who deliberately narrowed something.
        // Demanding a signature for tightening trains operators to type "n/a" into the field that
        // is supposed to carry the answer.
        var policy = Policy();
        AiPackageProvisioning.Apply(policy, AiPackages.Off, null, false, "test", DateTime.UtcNow);

        Assert.Empty(AiPackageProvisioning.Deviations(policy, AiPackages.Cloud, 2_000_000, false));
    }

    [Fact]
    public void GoingBeyondThePlan_IsNamedInSentencesAnOperatorCanRead()
    {
        var policy = Policy();
        AiPackageProvisioning.Apply(policy, AiPackages.Private, 2_000_000, false, "test", DateTime.UtcNow);

        // Approved for cloud on a plan that sells private extraction, with the ceiling lifted.
        policy.ExternalProcessingAllowed = true;
        policy.MonthlyHardTokenLimit = 9_000_000;

        var deviations = AiPackageProvisioning.Deviations(policy, AiPackages.Private, 2_000_000, false);

        Assert.Equal(2, deviations.Count);
        Assert.Contains(deviations, x => x.Contains("approved cloud provider", StringComparison.Ordinal));
        Assert.Contains(deviations, x => x.Contains("9,000,000", StringComparison.Ordinal)
                                         && x.Contains("2,000,000", StringComparison.Ordinal));
    }

    [Fact]
    public void NoCeilingAtAll_DeviatesFromAPlanThatSellsOne()
    {
        // The expensive direction, and the one that reaches a finance conversation months later
        // with nobody able to say who agreed to it.
        var policy = Policy();
        AiPackageProvisioning.Apply(policy, AiPackages.Private, 2_000_000, false, "test", DateTime.UtcNow);
        policy.MonthlyHardTokenLimit = null;

        var deviations = AiPackageProvisioning.Deviations(policy, AiPackages.Private, 2_000_000, false);

        Assert.Contains(deviations, x => x.Contains("no monthly AI ceiling at all", StringComparison.Ordinal));
    }

    [Fact]
    public void APurposeThePackageDoesNotInclude_IsADeviation()
    {
        var policy = Policy();
        AiPackageProvisioning.Apply(policy, AiPackages.Private, 2_000_000, false, "test", DateTime.UtcNow);
        policy.AllowedPurposes = $"{AiPurposes.RfqExtraction},{AiPurposes.Agent}";

        var deviations = AiPackageProvisioning.Deviations(policy, AiPackages.Private, 2_000_000, false);

        Assert.Contains(deviations, x => x.Contains(AiPurposes.Agent, StringComparison.Ordinal));
    }
}
