using System.Text;
using ERP_RFQ_Automation.Ingestion.Assembly;
using MimeKit;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// Boilerplate must not gate a lead — and a bill of quantities must never be mistaken for
/// boilerplate.
///
/// <para><b>The defect.</b> Only a deterministic classifier may mark a part
/// <see cref="EmailInquiryComponentStatus.Ignored"/>. Everything else that could not be read
/// became <see cref="EmailInquiryComponentStatus.Skipped"/>, and one Skipped part sends the whole
/// message to review — so an unreadable "Terms &amp; Conditions.pdf" downgraded an RFQ whose real
/// content had extracted perfectly. The buyer attaches that file to every mail they send.</para>
///
/// <para><b>The asymmetry these tests pin.</b> Wrongly ignoring a part produces a lead priced
/// against content nobody saw; wrongly reviewing one costs a few seconds of attention. So the
/// negative cases below matter more than the positive ones, and there are deliberately more of
/// them: a spreadsheet is never ignorable, and neither is any name carrying commercial
/// vocabulary, however much legal wording surrounds it.</para>
/// </summary>
public class NonCommercialAttachmentTests
{
    private const string MessageKey = "boilerplate-1@customer.example";

    // =====================================================================================
    // The classifier itself
    // =====================================================================================

    [Theory]
    // The exact names the brief names, and the spellings senders actually use.
    [InlineData("Terms & Conditions.pdf")]
    [InlineData("Terms and Conditions.PDF")]
    [InlineData("terms_and_conditions.pdf")]
    [InlineData("Terms-and-Conditions (2).pdf")]
    [InlineData("TERMS AND CONDITIONS.pdf")]
    [InlineData("General Conditions of Contract.pdf")]
    [InlineData("Standard Conditions of Contract.docx")]
    [InlineData("NDA.pdf")]
    [InlineData("Mutual NDA - signed.pdf")]
    [InlineData("Non-Disclosure Agreement.docx")]
    [InlineData("Confidentiality Agreement.pdf")]
    [InlineData("Privacy Notice.pdf")]
    [InlineData("Privacy Policy.pdf")]
    [InlineData("Email Disclaimer.txt")]
    [InlineData("Company Profile.pdf")]
    [InlineData("Corporate Brochure.pdf")]
    [InlineData("Commercial Registration.pdf")]
    [InlineData("VAT Certificate.pdf")]
    [InlineData("Zakat Certificate.pdf")]
    [InlineData("ISO 9001 certificate.pdf")]
    [InlineData("Supplier Code of Conduct.pdf")]
    // Arabic, as a Saudi/GCC sender writes it.
    [InlineData("الشروط والأحكام.pdf")]
    [InlineData("الشروط والاحكام.pdf")]
    [InlineData("سياسة الخصوصية.pdf")]
    [InlineData("السجل التجاري.pdf")]
    [InlineData("الملف التعريفي.pdf")]
    public void Recognisable_boilerplate_is_ignorable(string fileName)
    {
        Assert.True(
            NonCommercialAttachmentClassifier.IsNonCommercialBoilerplate(
                fileName, "application/pdf", out var pattern),
            $"'{fileName}' was not recognised as boilerplate.");
        Assert.False(string.IsNullOrWhiteSpace(pattern),
            "A boilerplate verdict must name the pattern that produced it, or it is not auditable.");
    }

    [Theory]
    // THE COMMERCIAL VOCABULARY RULE. Every one of these contains boilerplate wording and is
    // still the enquiry.
    [InlineData("RFQ Terms and Pricing Schedule.xlsx")]
    [InlineData("RFQ Terms and Conditions.pdf")]
    [InlineData("Tender - General Conditions and BOQ.pdf")]
    [InlineData("Quotation Terms and Conditions.pdf")]
    [InlineData("Company Profile and Price List.pdf")]
    [InlineData("NDA and Scope of Work.pdf")]
    [InlineData("Terms of Purchase Order 4500123.pdf")]
    // THE SPREADSHEET RULE. A bill of quantities is nearly always one of these.
    [InlineData("BOQ.xlsx")]
    [InlineData("Terms and Conditions.xlsx")]
    [InlineData("Company Profile.xls")]
    [InlineData("NDA.csv")]
    [InlineData("privacy policy.ods")]
    // Ordinary commercial documents that share no wording with the list at all.
    [InlineData("Requirements.pdf")]
    [InlineData("valves.csv")]
    [InlineData("Drawing A-101.pdf")]
    [InlineData("Material Test Certificate.pdf")]
    [InlineData("agenda.pdf")]
    [InlineData("calendar.pdf")]
    public void Anything_that_might_carry_commercial_content_is_never_ignorable(string fileName)
    {
        Assert.False(
            NonCommercialAttachmentClassifier.IsNonCommercialBoilerplate(fileName, null),
            $"'{fileName}' was ignored. A part that might carry priced content must reach a human.");
    }

    [Theory]
    [InlineData("Terms and Conditions", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet")]
    [InlineData("Terms and Conditions", "application/vnd.ms-excel")]
    [InlineData("Terms and Conditions", "text/csv")]
    public void A_spreadsheet_is_never_ignorable_even_without_an_extension(string name, string mime)
    {
        // The extension rule is the primary guard; this is the same rule expressed on the declared
        // type, for a sender whose client stripped the extension. Deleting either half would leave
        // a BOQ named "Terms and Conditions" unread and a lead priced without it.
        Assert.False(NonCommercialAttachmentClassifier.IsNonCommercialBoilerplate(name, mime));
    }

    [Fact]
    public void An_image_is_never_this_classifiers_to_judge_however_it_is_named()
    {
        // Signature blocks and logos belong to InlineAssetClassifier, whose bar is measured size
        // plus a real cid reference from the body — evidence a filename cannot supply, and a bar
        // raised on purpose because Outlook gives a signature logo and a pasted screenshot of a
        // requirements table the same name. Weaker evidence must not overturn stronger, so an
        // image is refused here even when its name looks like decoration.
        Assert.False(NonCommercialAttachmentClassifier.IsNonCommercialBoilerplate(
            "logo.png", "image/png"));
        Assert.False(NonCommercialAttachmentClassifier.IsNonCommercialBoilerplate(
            "signature.png", "image/png"));
        Assert.False(NonCommercialAttachmentClassifier.IsNonCommercialBoilerplate(
            "Terms and Conditions.png", "image/png"));

        // The same words on a document are still boilerplate: it is the media type that changes
        // who owns the decision, not the vocabulary.
        Assert.True(NonCommercialAttachmentClassifier.IsNonCommercialBoilerplate(
            "Terms and Conditions.pdf", "application/pdf"));
    }

    [Fact]
    public void An_unnamed_part_is_never_ignorable()
    {
        // An unnamed attachment already has its own disposition. Answering "ignorable" for a name
        // we do not have would be a verdict reached on no evidence at all.
        Assert.False(NonCommercialAttachmentClassifier.IsNonCommercialBoilerplate(null, "application/pdf"));
        Assert.False(NonCommercialAttachmentClassifier.IsNonCommercialBoilerplate("   ", "application/pdf"));
    }

    [Fact]
    public void A_bare_certificate_is_not_boilerplate_because_a_material_test_certificate_is_priced_against()
    {
        // The conservative reading of "certificates". In this market an MTC is a quality document
        // a buyer genuinely prices against, so only the corporate registrations are named.
        Assert.False(NonCommercialAttachmentClassifier.IsNonCommercialBoilerplate(
            "Certificate.pdf", "application/pdf"));
        Assert.True(NonCommercialAttachmentClassifier.IsNonCommercialBoilerplate(
            "Commercial Registration Certificate.pdf", "application/pdf"));
    }

    // =====================================================================================
    // The planner, which is where the decision reaches a message
    // =====================================================================================

    [Fact]
    public async Task Boilerplate_is_planned_as_Ignored_and_carries_an_auditable_reason()
    {
        var manifest = await Plan(Message(
            File("valves.csv", mime: "text/csv"), File("Terms & Conditions.pdf")));

        // Still a ROW. "We ignored it" and "it was never there" must stay different observations.
        Assert.Equal(3, manifest.ExpectedComponentCount);

        var terms = Assert.Single(manifest.Components, c => c.FileName == "Terms & Conditions.pdf");
        Assert.Equal(EmailInquiryComponentDisposition.IgnoreNonCommercial, terms.Disposition);
        Assert.Equal(EmailInquirySkipReasons.NonCommercialBoilerplate, terms.ReasonCode);
        Assert.Contains("terms conditions", terms.ReasonDetail);

        // And it costs nothing: no bytes carried, no extraction job.
        Assert.Equal(0, terms.ByteSize);
        Assert.Empty(terms.Content.ToArray());
        Assert.DoesNotContain(manifest.Processable, c => c.FileName == "Terms & Conditions.pdf");

        // The real content is untouched.
        Assert.Contains(manifest.Processable, c => c.FileName == "valves.csv");
    }

    [Fact]
    public void The_barrier_lets_a_message_carrying_boilerplate_stay_clean()
    {
        // THE POINT OF THE WHOLE FEATURE, at the level that decides it. Ignored is the only
        // status that lets a message stay clean despite a part not being read.
        var clean = EmailInquiryAssemblyStateMachine.Evaluate(
            3,
            [
                EmailInquiryComponentStatus.Completed,
                EmailInquiryComponentStatus.Completed,
                EmailInquiryComponentStatus.Ignored
            ]);
        Assert.Equal(EmailInquiryAssemblyStatus.ReadyForAssembly, clean.Status);

        // The same message with the part Skipped — which is what happened before — goes to a
        // human instead. Both halves are asserted so the contrast is the test.
        var reviewed = EmailInquiryAssemblyStateMachine.Evaluate(
            3,
            [
                EmailInquiryComponentStatus.Completed,
                EmailInquiryComponentStatus.Completed,
                EmailInquiryComponentStatus.Skipped
            ]);
        Assert.Equal(EmailInquiryAssemblyStatus.NeedsReview, reviewed.Status);
    }

    [Fact]
    public async Task A_spreadsheet_named_like_boilerplate_is_still_planned_for_extraction()
    {
        // The single most expensive mistake this feature could make, asserted through the planner
        // and not only through the classifier.
        var manifest = await Plan(Message(
            File("Terms and Conditions.xlsx", mime: "application/vnd.ms-excel")));

        var sheet = Assert.Single(manifest.Components, c => c.FileName == "Terms and Conditions.xlsx");
        Assert.Equal(EmailInquiryComponentDisposition.Process, sheet.Disposition);
    }

    [Fact]
    public async Task An_RFQ_named_with_terms_wording_is_still_planned_for_extraction()
    {
        var manifest = await Plan(Message(File("RFQ Terms and Pricing Schedule.pdf")));

        var rfq = Assert.Single(
            manifest.Components, c => c.FileName == "RFQ Terms and Pricing Schedule.pdf");
        Assert.Equal(EmailInquiryComponentDisposition.Process, rfq.Disposition);
    }

    [Fact]
    public async Task The_manifest_contract_version_records_that_dispositions_changed()
    {
        // A v2-captured message re-planned by this build must report "the contract changed"
        // rather than a pile of per-component disposition mismatches that read like tampering.
        var manifest = await Plan(Message(File("Company Profile.pdf")));
        Assert.Equal(EmailInquiryManifestPlanner.ContractVersion, manifest.ContractVersion);
        Assert.True(manifest.ContractVersion >= 3,
            "Adding a disposition without bumping the contract version makes the recovery guard blind.");
    }

    [Fact]
    public async Task A_re_plan_of_the_same_message_agrees_with_what_was_persisted()
    {
        // The verifier must accept an Ignored boilerplate row against a fresh plan of the same
        // bytes. Without the new disposition mapped, recovery would report a metadata mismatch on
        // every message carrying a terms-and-conditions attachment and hold it for review — the
        // very outcome this feature removes, reintroduced one layer down.
        var message = Message(File("valves.csv", mime: "text/csv"), File("NDA.pdf"));
        var manifest = await Plan(message);

        var persisted = manifest.Components.Select(c => new EmailInquiryComponent
        {
            ComponentKey = c.ComponentKey,
            Kind = c.Kind,
            Ordinal = c.Ordinal,
            FileName = c.FileName,
            MimeType = c.MimeType,
            ByteSize = c.ByteSize,
            ContentHash = string.IsNullOrEmpty(c.ContentHash) ? null : c.ContentHash,
            ReasonCode = c.ReasonCode,
            NestingDepth = c.NestingDepth,
            Status = c.Disposition switch
            {
                EmailInquiryComponentDisposition.Process => EmailInquiryComponentStatus.Completed,
                EmailInquiryComponentDisposition.IgnoreInlineAsset => EmailInquiryComponentStatus.Ignored,
                EmailInquiryComponentDisposition.IgnoreNonCommercial => EmailInquiryComponentStatus.Ignored,
                EmailInquiryComponentDisposition.StructuralContainer => EmailInquiryComponentStatus.StructuralOnly,
                _ => EmailInquiryComponentStatus.Skipped
            }
        }).ToList();

        var verification = EmailComponentManifestVerifier.Verify(
            manifest.ContractVersion, manifest.ExpectedComponentCount, persisted,
            await Plan(message));

        Assert.True(verification.IsCompatible,
            "A message carrying ignored boilerplate no longer verifies against its own bytes: "
            + EmailComponentManifestVerifier.Describe(verification.Mismatches));
    }

    // =====================================================================================

    private static MimeMessage Message(params MimeEntity[] attachments)
    {
        var message = new MimeMessage { Subject = "RFQ 88-2410 Jubail expansion" };
        message.From.Add(new MailboxAddress("Buyer", "buyer@customer.example"));
        message.To.Add(new MailboxAddress("Sales", "sales@nexora.example"));
        var multipart = new Multipart("mixed") { new TextPart("plain") { Text = "body" } };
        foreach (var attachment in attachments) multipart.Add(attachment);
        message.Body = multipart;
        return message;
    }

    private static MimePart File(string name, byte[]? content = null, string mime = "application/pdf")
    {
        var slash = mime.IndexOf('/');
        return new MimePart(mime[..slash], mime[(slash + 1)..])
        {
            FileName = name,
            Content = new MimeContent(new MemoryStream(content ?? Encoding.UTF8.GetBytes("x"))),
            ContentDisposition = new ContentDisposition(ContentDisposition.Attachment)
        };
    }

    private static Task<EmailInquiryManifest> Plan(MimeMessage message)
        => EmailInquiryManifestPlanner.PlanAsync(message, MessageKey, "Please quote the attached.");
}
