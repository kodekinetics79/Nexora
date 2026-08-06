using System.Reflection;
using ERP_RFQ_Automation.Authorization;
using ERP_RFQ_Automation.Controllers;
using ERP_RFQ_Automation.DTOs.RfqDTOs;
using Microsoft.AspNetCore.Mvc;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// Governance of <c>POST api/Rfq/{id}/lines/{lineId}/participation</c> — the write path that
/// turns an 84-line customer bid list into a 12-line Nexora quote.
///
/// <para>The domain rules themselves (a No-Quote needs a meaningful reason, an unknown decision
/// is refused, reversing a decline clears its reason) are asserted in
/// <c>RfqLineParticipationTests</c> against <c>Rfqitem.DecideParticipation</c>. What is pinned
/// here is the surface: who may call it, and what a caller may not smuggle in.</para>
/// </summary>
public sealed class RfqLineParticipationEndpointTests
{
    private static MethodInfo Action => typeof(RfqController)
        .GetMethod(nameof(RfqController.SetLineParticipation))!;

    [Fact]
    public void TheEndpointExists_AndIsScopedToASingleLineOfASingleRfq()
    {
        // The route carries BOTH ids. The handler then requires the line to belong to that RFQ,
        // so a caller cannot name their own RFQ and someone else's line.
        var post = Assert.Single(Action.GetCustomAttributes<HttpPostAttribute>(true));
        Assert.Equal("{id}/lines/{lineId}/participation", post.Template);
    }

    [Fact]
    public void DecidingWhatWeBidOnRequiresEditNotView()
    {
        // Choosing which lines Nexora quotes is a commercial act. Read access to an RFQ must
        // not carry the right to decline half of it.
        var permissions = Action.GetCustomAttributes<RequireModulePermissionAttribute>(true);
        Assert.Contains(permissions, p => p.Policy == "ModulePermission:RFQ Management:Edit");
        Assert.DoesNotContain(permissions, p => p.Policy == "ModulePermission:RFQ Management:View");
    }

    [Fact]
    public void TheBodyCarriesOnlyTheDecisionAndItsReason()
    {
        // Everything identity-bearing — tenant, actor, timestamp, which line — is derived
        // server-side. A body that could set the actor would let one user record a decline
        // against another's name.
        var properties = typeof(RfqLineParticipationRequestDTO).GetProperties().Select(p => p.Name).ToArray();

        Assert.Equal(2, properties.Length);
        Assert.Contains(nameof(RfqLineParticipationRequestDTO.Decision), properties);
        Assert.Contains(nameof(RfqLineParticipationRequestDTO.Reason), properties);
    }

    [Fact]
    public void ParticipationIsNotSettableThroughTheBulkRfqUpdate()
    {
        // The decision carries an actor, a timestamp and a mandatory decline reason. Allowing
        // it to ride along on a bulk header update would let all three be silently omitted.
        var updateProperties = typeof(RfqUpdateRequestDTO).GetProperties().Select(p => p.Name).ToArray();

        Assert.DoesNotContain("ParticipationDecision", updateProperties);
        Assert.DoesNotContain("NoQuoteReason", updateProperties);
    }

    [Fact]
    public void TheReadModelExposesTheDecisionItsReasonAndWhoMadeIt()
    {
        // Without the actor and timestamp on the read side, the queue shows a decline that
        // nobody can be asked about.
        var properties = typeof(RfqitemResponseDTO).GetProperties().Select(p => p.Name).ToArray();

        Assert.Contains(nameof(RfqitemResponseDTO.ParticipationDecision), properties);
        Assert.Contains(nameof(RfqitemResponseDTO.NoQuoteReason), properties);
        Assert.Contains(nameof(RfqitemResponseDTO.ParticipationDecidedBy), properties);
        Assert.Contains(nameof(RfqitemResponseDTO.ParticipationDecidedOn), properties);
    }

    [Fact]
    public void AnUndecidedLineReadsAsPending_NotAsQuote()
    {
        // The load-bearing default, restated on the read model: a line nobody has looked at
        // must never render as an implicit commitment to quote it.
        Assert.Equal(ERP_RFQ_Automation.Models.Rfqitem.ParticipationPending,
            new RfqitemResponseDTO().ParticipationDecision);
    }
}
