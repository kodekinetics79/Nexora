using ERP_RFQ_Automation.Ingestion.Triage;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// Real bid opportunities must survive the intake gate.
///
/// <para><b>Observed in production, 2026-08-13.</b> The Inbound Mail screen showed BidNet Direct
/// addenda for the Fulton County Jail Special Purpose Facility and the Clayton County Integrated
/// Justice Information System — genuine government solicitations — every one discarded as
/// "No-reply sender".</para>
///
/// <para>The rule reasoned that "a reply to one of these can never reach a human, so a request
/// never originates from one". That premise is false in this market: public and enterprise
/// procurement reaches a distributor almost entirely through no-reply notification platforms —
/// BidNet, DemandStar, Periscope, SAM.gov, Ariba, Jaggaer. You do not reply to a solicitation;
/// you act on it. An unattended address is evidence about repliability, never about whether the
/// message is business mail.</para>
/// </summary>
public class EmailTriageNoReplyProcurementTests
{
    private static EmailTriageSignals From(string address, string subject, string body = "",
        bool hasAttachments = false) => new()
    {
        FromAddress = address,
        FromDomain = address.Split('@').Last(),
        Subject = subject,
        FreshBody = body,
        HasAttachments = hasAttachments
    };

    // ---- the production failures, as tests -------------------------------------------------

    [Theory]
    [InlineData("Addendum from BidNet Direct for Design Professional Services for the Fulton County Jail Special Purpose Facility solicitation for Fulton County Government")]
    [InlineData("Communication on the \"Design Professional Services for the Fulton County Jail Special Purpose Facility\" solicitation from BidNet Direct")]
    [InlineData("Addendum from BidNet Direct for Integrated Justice Information and Management System Modernization solicitation for Clayton County")]
    public void A_real_BidNet_solicitation_is_not_discarded_as_noise(string subject)
    {
        var decision = DeterministicEmailTriage.Evaluate(From("noreply@bidnet.com", subject));

        Assert.NotEqual(EmailTriageOutcome.Noise, decision.Outcome);
        Assert.DoesNotContain(EmailTriageReasonCodes.NoreplySender, decision.ReasonCodes);
    }

    [Theory]
    [InlineData("noreply@bidnet.com", "Invitation to Bid — municipal water pumps")]
    [InlineData("do-not-reply@demandstar.com", "New solicitation matching your commodity codes")]
    [InlineData("donotreply@sam.gov", "Amendment to Request for Proposal W912")]
    [InlineData("no-reply@periscopeholdings.com", "Bid opportunity: closing date 12 September")]
    [InlineData("noreply@ariba.com", "Sourcing event invitation from Aramco")]
    public void Procurement_platforms_reach_extraction_despite_unattended_addresses(
        string sender, string subject)
    {
        var decision = DeterministicEmailTriage.Evaluate(From(sender, subject));

        Assert.NotEqual(EmailTriageOutcome.Noise, decision.Outcome);
    }

    [Fact]
    public void An_unattended_sender_with_an_attachment_is_read_not_discarded()
    {
        // "See attached" from a portal is the commonest shape of all. The attachment is the
        // opportunity; refusing it because nobody can reply loses the document entirely.
        var decision = DeterministicEmailTriage.Evaluate(
            From("noreply@portal.example", "Documents available", "", hasAttachments: true));

        Assert.NotEqual(EmailTriageOutcome.Noise, decision.Outcome);
    }

    // ---- but genuine automated noise is still stopped ---------------------------------------

    [Fact]
    public void A_service_notice_from_an_unattended_sender_is_still_noise()
    {
        // The GoDaddy row from the same screen: no commercial vocabulary, nothing attached.
        var decision = DeterministicEmailTriage.Evaluate(
            From("donotreply@godaddy.com", "Your Professional Email powered by Titan switch is about to begin."));

        Assert.Equal(EmailTriageOutcome.Noise, decision.Outcome);
        Assert.Contains(EmailTriageReasonCodes.NoreplySender, decision.ReasonCodes);
    }

    [Fact]
    public void Bulk_and_mailing_list_headers_still_stop_a_message_outright()
    {
        // The Instagram rows. This rule was always correct and is untouched — and it stops the
        // message even though the demoted no-reply rule would now let it pass.
        var signals = From("posts-recaps@mail.instagram.com", "catch up on moments you've missed");
        signals = signals with { ListUnsubscribe = "<https://instagram.com/unsubscribe>" };

        var decision = DeterministicEmailTriage.Evaluate(signals);

        Assert.Equal(EmailTriageOutcome.Noise, decision.Outcome);
        Assert.Contains(EmailTriageReasonCodes.BulkListHeader, decision.ReasonCodes);
    }

    [Fact]
    public void A_bulk_marketing_blast_mentioning_a_bid_is_still_stopped_by_its_headers()
    {
        // The override is deliberately scoped: commercial vocabulary overrides the UNATTENDED
        // signal only. It must not rescue a mailing list, or every vendor newsletter that says
        // "bid" becomes an inquiry.
        var signals = From("news@vendor.example", "Win more bids with our new tender toolkit");
        signals = signals with { ListId = "<news.vendor.example>" };

        var decision = DeterministicEmailTriage.Evaluate(signals);

        Assert.Equal(EmailTriageOutcome.Noise, decision.Outcome);
    }

    [Fact]
    public void An_auto_reply_from_an_unattended_sender_is_still_noise()
    {
        var signals = From("noreply@customer.example", "Out of office: RFQ for valves");
        signals = signals with { AutoSubmitted = "auto-replied" };

        var decision = DeterministicEmailTriage.Evaluate(signals);

        Assert.Equal(EmailTriageOutcome.Noise, decision.Outcome);
        Assert.Contains(EmailTriageReasonCodes.AutoSubmittedHeader, decision.ReasonCodes);
    }

    [Fact]
    public void A_normal_customer_RFQ_is_unaffected()
    {
        var decision = DeterministicEmailTriage.Evaluate(
            From("buyer@customer.example", "RFQ for 40 flanges", "Please quote 40 nos flanges."));

        Assert.NotEqual(EmailTriageOutcome.Noise, decision.Outcome);
    }
}
