using ERP_RFQ_Automation.Ingestion.Triage;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// The inbound-mail gate. The rule it encodes is a business decision, not a heuristic
/// preference: STOP ONLY ON POSITIVE, MACHINE-VERIFIABLE EVIDENCE OF NON-BUSINESS MAIL.
/// Absence of RFQ vocabulary is never a reason to stop — that was the previous gate's
/// behaviour and it is how a bare prose enquiry gets lost while every supplier order
/// confirmation sails through on the word "quote".
/// </summary>
public class EmailTriageTests
{
    private static EmailTriageSignals Signals(
        string subject = "", string body = "", string? senderPartyType = null,
        string? from = "buyer@gulfmep.ae", bool hasAttachments = false)
        => new()
        {
            Subject = subject,
            FreshBody = body,
            FromAddress = from,
            FromDomain = SenderPartyResolver.ExtractDomain(from),
            SenderPartyType = senderPartyType,
            HasAttachments = hasAttachments
        };

    // ------------------------------------------------------------ the owner's sentence

    [Fact]
    public void ProseEnquiryWithQuantitiesIsAnInquiry()
    {
        // THE case this whole work item exists for: no RFQ number, no attachment, no table,
        // and none of the old gate's "strong" keywords in the subject.
        var decision = DeterministicEmailTriage.Evaluate(Signals(
            subject: "Cable tray requirement",
            body: "Hi, please quote 40 nos cable tray 300mm and 12 nos junction box IP65, "
                + "delivery to Jebel Ali by 20th"));

        Assert.Equal(EmailTriageOutcome.Inquiry, decision.Outcome);
        Assert.Contains(EmailTriageReasonCodes.QtyUomPattern, decision.ReasonCodes);
        Assert.Contains(EmailTriageReasonCodes.RequestVerb, decision.ReasonCodes);
    }

    [Fact]
    public void QuantityPatternAloneCarriesAnEnquiryWithNoRequestVerbAndNoKeywords()
    {
        var decision = DeterministicEmailTriage.Evaluate(Signals(
            subject: "Jebel Ali site",
            body: "We require 250 mtrs of 4 core 16 sqmm cable for the villa project."));

        Assert.Equal(EmailTriageOutcome.Inquiry, decision.Outcome);
        Assert.Equal(new[] { EmailTriageReasonCodes.QtyUomPattern }, decision.ReasonCodes);
    }

    // ------------------------------------------------------------------- one per reason code

    [Fact]
    public void AutoSubmittedHeaderIsNoise()
    {
        // An out-of-office autoreply: RFC 3834 says so in a header, which is a fact rather
        // than a guess — and it consumes NO AI tokens because no job is ever enqueued.
        var decision = DeterministicEmailTriage.Evaluate(Signals(
            subject: "Automatic reply: RFQ 4711 cable tray",
            body: "I am out of the office until 20 August with no access to email.")
            with
        { AutoSubmitted = "auto-replied" });

        Assert.Equal(EmailTriageOutcome.Noise, decision.Outcome);
        Assert.Contains(EmailTriageReasonCodes.AutoSubmittedHeader, decision.ReasonCodes);
    }

    [Fact]
    public void AutoSubmittedNoIsNotNoise()
    {
        // RFC 3834: "no" is the value ordinary human mail carries. Treating it as automated
        // would stop real correspondence.
        var decision = DeterministicEmailTriage.Evaluate(
            Signals(subject: "Enquiry", body: "Please quote.") with { AutoSubmitted = "no" });

        Assert.NotEqual(EmailTriageOutcome.Noise, decision.Outcome);
    }

    [Fact]
    public void XAutoreplyHeaderIsNoise()
    {
        var decision = DeterministicEmailTriage.Evaluate(
            Signals(body: "On leave.") with { XAutoreply = "yes" });

        Assert.Equal(EmailTriageOutcome.Noise, decision.Outcome);
        Assert.Contains(EmailTriageReasonCodes.AutoSubmittedHeader, decision.ReasonCodes);
    }

    [Theory]
    [InlineData("bulk")]
    [InlineData("list")]
    [InlineData("junk")]
    public void BulkPrecedenceIsNoise(string precedence)
    {
        var decision = DeterministicEmailTriage.Evaluate(
            Signals(subject: "August newsletter: 40 nos of new products")
            with
            { Precedence = precedence });

        Assert.Equal(EmailTriageOutcome.Noise, decision.Outcome);
        Assert.Contains(EmailTriageReasonCodes.BulkListHeader, decision.ReasonCodes);
    }

    [Fact]
    public void ListHeadersAreNoise()
    {
        Assert.Equal(EmailTriageOutcome.Noise, DeterministicEmailTriage.Evaluate(
            Signals() with { ListId = "<news.vendor.example>" }).Outcome);
        Assert.Equal(EmailTriageOutcome.Noise, DeterministicEmailTriage.Evaluate(
            Signals() with { ListUnsubscribe = "<https://vendor.example/u>" }).Outcome);
    }

    [Theory]
    [InlineData("no-reply@portal.example")]
    [InlineData("noreply@portal.example")]
    [InlineData("donotreply@portal.example")]
    [InlineData("mailer-daemon@portal.example")]
    [InlineData("postmaster@portal.example")]
    [InlineData("bounces+17@portal.example")]
    public void UnattendedSenderIsNoise(string from)
    {
        var decision = DeterministicEmailTriage.Evaluate(Signals(
            subject: "Order confirmation", body: "Your order has shipped.", from: from));

        Assert.Equal(EmailTriageOutcome.Noise, decision.Outcome);
        Assert.Contains(EmailTriageReasonCodes.NoreplySender, decision.ReasonCodes);
    }

    [Fact]
    public void CalendarMessageIsNoise()
    {
        var decision = DeterministicEmailTriage.Evaluate(
            Signals(subject: "Site meeting")
            with
            { ContentClass = "urn:content-classes:calendarmessage" });

        Assert.Equal(EmailTriageOutcome.Noise, decision.Outcome);
        Assert.Contains(EmailTriageReasonCodes.CalendarInvite, decision.ReasonCodes);
    }

    [Fact]
    public void EmptyAfterQuoteStripWithNoAttachmentsIsNoise()
    {
        var decision = DeterministicEmailTriage.Evaluate(
            Signals(subject: "RE: RFQ 4711") with { BodyEmptyAfterStrip = true });

        Assert.Equal(EmailTriageOutcome.Noise, decision.Outcome);
        Assert.Contains(EmailTriageReasonCodes.EmptyAfterQuoteStrip, decision.ReasonCodes);
    }

    [Fact]
    public void EmptyAfterQuoteStripWithAnAttachmentIsNotNoise()
    {
        // "see attached" with the body written by the mail client is the single most common
        // shape of a real RFQ in this segment. The attachment clause is load bearing.
        var decision = DeterministicEmailTriage.Evaluate(Signals(
            subject: "RFQ 4711", hasAttachments: true) with { BodyEmptyAfterStrip = true });

        Assert.NotEqual(EmailTriageOutcome.Noise, decision.Outcome);
        Assert.Equal(EmailTriageOutcome.Inquiry, decision.Outcome);
        Assert.Contains(EmailTriageReasonCodes.RfqReference, decision.ReasonCodes);
    }

    [Fact]
    public void KnownCustomerContactIsAnInquiryEvenWithNoVocabularyAtAll()
    {
        var decision = DeterministicEmailTriage.Evaluate(Signals(
            subject: "Following up", body: "Any update on the below?", senderPartyType: "customer"));

        Assert.Equal(EmailTriageOutcome.Inquiry, decision.Outcome);
        Assert.Contains(EmailTriageReasonCodes.KnownCustomerContact, decision.ReasonCodes);
    }

    [Theory]
    [InlineData("RFQ 4711 - cable tray")]
    [InlineData("Request for quotation: switchgear")]
    [InlineData("Enquiry for MEP materials")]
    [InlineData("ITB 22/2026")]
    [InlineData("Tender documents")]
    public void SubjectReferenceIsAnInquiry(string subject)
    {
        var decision = DeterministicEmailTriage.Evaluate(Signals(subject: subject, body: "See below."));

        Assert.Equal(EmailTriageOutcome.Inquiry, decision.Outcome);
        Assert.Contains(EmailTriageReasonCodes.RfqReference, decision.ReasonCodes);
    }

    [Theory]
    [InlineData("Pls quote for the below items")]
    [InlineData("please send your best offer")]
    [InlineData("Kindly advise availability")]
    public void RequestVerbIsAnInquiry(string body)
    {
        var decision = DeterministicEmailTriage.Evaluate(Signals(subject: "Materials", body: body));

        Assert.Equal(EmailTriageOutcome.Inquiry, decision.Outcome);
        Assert.Contains(EmailTriageReasonCodes.RequestVerb, decision.ReasonCodes);
    }

    // --------------------------------------------------------- commercial non-inquiry

    [Fact]
    public void SupplierQuotationReplyIsNotTurnedIntoACustomerLead()
    {
        var decision = DeterministicEmailTriage.Evaluate(Signals(
            subject: "Our quotation QTN-8891",
            body: "Please find our supplier quotation. Unit price is USD 42.00 per metre, "
                + "incoterms CIF Jebel Ali. Quote validity 30 days.",
            senderPartyType: "supplier",
            from: "sales@cabletraysupplier.example"));

        Assert.Equal(EmailTriageOutcome.CommercialNonInquiry, decision.Outcome);
        Assert.Equal(EmailTriageDocumentHints.SupplierQuote, decision.CommercialDocumentTypeHint);
        Assert.Contains(EmailTriageReasonCodes.SupplierQuoteTerms, decision.ReasonCodes);
        // The hint is the mechanism: the worker completes such a job WITHOUT creating a Lead.
        Assert.True(ERP_RFQ_Automation.Extraction.ExtractionJobMetadata
            .IsNonLeadCommercialType(decision.CommercialDocumentTypeHint));
    }

    [Fact]
    public void SupplierInvoiceIsRoutedAwayFromLeadCreation()
    {
        var decision = DeterministicEmailTriage.Evaluate(Signals(
            subject: "Tax invoice 55-2026",
            body: "Invoice number 55-2026. Amount due AED 18,400. Remit to our account below.",
            senderPartyType: "supplier"));

        Assert.Equal(EmailTriageOutcome.CommercialNonInquiry, decision.Outcome);
        Assert.Equal(EmailTriageDocumentHints.SupplierInvoice, decision.CommercialDocumentTypeHint);
        Assert.Contains(EmailTriageReasonCodes.InvoiceTerms, decision.ReasonCodes);
    }

    [Fact]
    public void SupplierVocabularyAloneWithoutAVerifiedSupplierSenderStillExtracts()
    {
        // An UNKNOWN sender whose mail mentions "unit price" is not evidence of a supplier
        // quotation — most enquiries mention price. Stopping here would be stopping on the
        // absence of proof, which is the failure mode this gate exists to remove.
        var decision = DeterministicEmailTriage.Evaluate(Signals(
            subject: "Pricing", body: "What is your unit price for cable tray?"));

        Assert.NotEqual(EmailTriageOutcome.CommercialNonInquiry, decision.Outcome);
    }

    [Fact]
    public void SupplierSenderAskingUsToQuoteIsStillExtractedWhenNoSupplierDocumentTermsAppear()
    {
        // In this segment the same counterparty both buys and sells. A supplier who sends US
        // an enquiry must not be silenced by their party type alone.
        var decision = DeterministicEmailTriage.Evaluate(Signals(
            subject: "Requirement",
            body: "Please quote 30 nos of 100mm elbows for our Sharjah store.",
            senderPartyType: "supplier"));

        Assert.Equal(EmailTriageOutcome.Inquiry, decision.Outcome);
        Assert.Contains(EmailTriageReasonCodes.QtyUomPattern, decision.ReasonCodes);
    }

    [Fact]
    public void PurchaseOrderVocabularyIsEvidenceOnlyAndNeverStopsAMessage()
    {
        var decision = DeterministicEmailTriage.Evaluate(Signals(
            subject: "Purchase order 9931", body: "Attached is our purchase order. Ship to Jebel Ali.",
            hasAttachments: true));

        Assert.Equal(EmailTriageOutcome.Uncertain, decision.Outcome);
        Assert.Contains(EmailTriageReasonCodes.PoTerms, decision.ReasonCodes);
    }

    // --------------------------------------------------------------------- uncertain

    [Fact]
    public void AmbiguousMessageIsExtractedAndFlagged()
    {
        var decision = DeterministicEmailTriage.Evaluate(Signals(
            subject: "Hello", body: "Following our meeting yesterday, can we discuss the project?"));

        Assert.Equal(EmailTriageOutcome.Uncertain, decision.Outcome);
        Assert.Equal(new[] { EmailTriageReasonCodes.NoSignal }, decision.ReasonCodes);
    }

    [Fact]
    public void ThreadContinuationIsReportedForRepliesAndForwards()
    {
        var reply = DeterministicEmailTriage.Evaluate(
            Signals(subject: "RE: RFQ 4711", body: "Please quote as discussed.")
            with
            { HasInReplyTo = true });
        var forward = DeterministicEmailTriage.Evaluate(
            Signals(subject: "FW: RFQ 4711", body: "Please quote as discussed.")
            with
            { HasReferences = true });
        var original = DeterministicEmailTriage.Evaluate(
            Signals(subject: "RFQ 4711", body: "Please quote as discussed."));

        Assert.True(reply.ThreadContinuation);
        Assert.True(forward.ThreadContinuation);
        Assert.False(original.ThreadContinuation);
    }

    [Fact]
    public void EveryDecisionCarriesAtLeastOneReasonCode()
    {
        // A decision without a recorded reason is an unexplainable rejection, which is the
        // one thing the audit trail may never be.
        EmailTriageSignals[] cases =
        {
            Signals(body: "nothing here"),
            Signals(subject: "RFQ 4711"),
            Signals(from: "noreply@x.example"),
            Signals(subject: "Tax invoice", body: "Amount due 100", senderPartyType: "supplier")
        };

        foreach (var signals in cases)
            Assert.NotEmpty(DeterministicEmailTriage.Evaluate(signals).ReasonCodes);
    }
}
