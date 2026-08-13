using System.Text;
using ERP_RFQ_Automation.Ingestion.Assembly;
using MimeKit;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// Which images may go unread, and — far more importantly — which may not.
///
/// <para><b>The asymmetry that drives every case here.</b> Wrongly ignoring an image produces a
/// Lead priced against content nobody saw. Wrongly processing a signature logo costs one
/// extraction job that returns no text. Those are not comparable costs, so the classifier must
/// require positive evidence on every axis and resolve every ambiguity toward processing.</para>
///
/// <para>A pasted requirements screenshot is inline, cid-referenced, an image, and often named
/// <c>image001.png</c> by Outlook — structurally identical to a logo in every respect except
/// size. That is why the size probe is the load-bearing signal and why it measures rather than
/// believes.</para>
/// </summary>
public class EmailInquiryInlineAssetTests
{
    private const string MessageKey = "inline-1@customer.example";
    private const int Threshold = 16 * 1024;

    private static MimePart Image(
        string? name, string? contentId, int decodedBytes,
        string disposition = ContentDisposition.Inline, string mime = "image/png",
        long? declaredSize = null)
    {
        var slash = mime.IndexOf('/');
        var part = new MimePart(mime[..slash], mime[(slash + 1)..])
        {
            Content = new MimeContent(new MemoryStream(Encoding.ASCII.GetBytes(new string('p', decodedBytes)))),
            ContentDisposition = new ContentDisposition(disposition)
        };
        if (name is not null) part.FileName = name;
        if (contentId is not null) part.ContentId = contentId;
        if (declaredSize.HasValue) part.ContentDisposition.Size = declaredSize;
        return part;
    }

    private static MimePart Pdf(string name) => new("application", "pdf")
    {
        FileName = name,
        Content = new MimeContent(new MemoryStream(Encoding.ASCII.GetBytes("BOQ lines"))),
        ContentDisposition = new ContentDisposition(ContentDisposition.Attachment)
    };

    /// <summary>A message whose HTML body genuinely references the given cids.</summary>
    private static MimeMessage Message(string[] referencedCids, params MimeEntity[] parts)
    {
        var message = new MimeMessage { Subject = "RFQ for valves" };
        message.From.Add(new MailboxAddress("Buyer", "buyer@customer.example"));
        message.To.Add(new MailboxAddress("Sales", "sales@nexora.example"));
        var images = string.Concat(referencedCids.Select(c => $"<img src=\"cid:{c}\">"));
        var related = new Multipart("related")
        {
            new TextPart("html") { Text = $"<p>Please quote the attached.</p>{images}" }
        };
        foreach (var part in parts) related.Add(part);
        message.Body = related;
        return message;
    }

    private static Task<EmailInquiryManifest> Plan(MimeMessage message)
        => EmailInquiryManifestPlanner.PlanAsync(
            message, MessageKey, "Please quote the attached.",
            new EmailInquiryLimits { InlineAssetMaxBytes = Threshold });

    private static EmailInquiryComponentPlan? Find(EmailInquiryManifest manifest, string fileName)
        => manifest.Components.FirstOrDefault(c => c.FileName == fileName);

    // ---- the logo may be exempted --------------------------------------------------------

    [Fact]
    public async Task An_ordinary_cid_signature_logo_is_decorative()
    {
        var manifest = await Plan(Message(["logo@sig"], Image("logo.png", "logo@sig", 4_000)));

        var logo = Find(manifest, "logo.png");
        Assert.NotNull(logo);
        Assert.Equal(EmailInquiryComponentDisposition.IgnoreInlineAsset, logo!.Disposition);
    }

    [Fact]
    public async Task A_logo_with_no_declared_size_is_still_classified_correctly()
    {
        // Gmail, Apple Mail, Outlook and Exchange all omit Content-Disposition: size. The old
        // classifier required it and therefore almost never fired on real mail.
        var manifest = await Plan(Message(["logo@sig"],
            Image("logo.png", "logo@sig", 4_000, declaredSize: null)));

        Assert.Equal(EmailInquiryComponentDisposition.IgnoreInlineAsset, Find(manifest, "logo.png")!.Disposition);
    }

    [Fact]
    public async Task A_logo_measured_just_under_the_threshold_is_decorative()
    {
        var manifest = await Plan(Message(["logo@sig"],
            Image("logo.png", "logo@sig", Threshold - 1)));

        Assert.Equal(EmailInquiryComponentDisposition.IgnoreInlineAsset, Find(manifest, "logo.png")!.Disposition);
    }

    [Fact]
    public async Task A_logo_does_not_block_the_real_inquiry()
    {
        // THE commercial outcome. The signature must neither become its own inquiry nor drag the
        // message into review while a genuine BOQ sits beside it.
        var manifest = await Plan(Message(["logo@sig"],
            Image("logo.png", "logo@sig", 3_000), Pdf("BOQ.pdf")));

        Assert.Equal(EmailInquiryComponentDisposition.IgnoreInlineAsset, Find(manifest, "logo.png")!.Disposition);
        Assert.Equal(EmailInquiryComponentDisposition.Process, Find(manifest, "BOQ.pdf")!.Disposition);
        Assert.DoesNotContain(manifest.Components, c => c.Disposition == EmailInquiryComponentDisposition.Skip);
    }

    [Fact]
    public async Task A_repeated_inline_asset_is_exempted_every_time_without_collision()
    {
        var manifest = await Plan(Message(["a@sig", "b@sig"],
            Image("logo.png", "a@sig", 2_000), Image("logo.png", "b@sig", 2_000)));

        var logos = manifest.Components.Where(c => c.FileName == "logo.png").ToList();
        Assert.Equal(2, logos.Count);
        Assert.All(logos, l => Assert.Equal(EmailInquiryComponentDisposition.IgnoreInlineAsset, l.Disposition));
        Assert.Equal(2, logos.Select(l => l.ComponentKey).Distinct().Count());
    }

    // ---- everything ambiguous is processed -------------------------------------------------

    [Fact]
    public async Task A_small_generic_named_requirements_screenshot_is_NOT_silently_discarded()
    {
        // THE case that matters. Outlook names pasted screenshots image001.png, it is inline,
        // cid-referenced, an image, and small. Nothing structural separates it from a logo — so
        // the honest answer is that it is processed, and commercial extraction decides.
        var manifest = await Plan(Message(["shot@body"], Image("image001.png", "shot@body", 30_000)));

        var shot = Find(manifest, "image001.png");
        Assert.NotNull(shot);
        Assert.NotEqual(EmailInquiryComponentDisposition.IgnoreInlineAsset, shot!.Disposition);
        Assert.Equal(EmailInquiryComponentDisposition.Process, shot.Disposition);
    }

    [Fact]
    public async Task A_commercially_named_inline_image_is_processed_despite_perfect_logo_headers()
    {
        // Every header says decoration; the filename says otherwise. The filename wins.
        var manifest = await Plan(Message(["rfq@body"], Image("rfq-lines.png", "rfq@body", 2_000)));

        Assert.Equal(EmailInquiryComponentDisposition.Process, Find(manifest, "rfq-lines.png")!.Disposition);
    }

    [Fact]
    public async Task An_image_explicitly_marked_as_an_attachment_is_processed()
    {
        // A sender who attaches something is telling us it is content, Content-Id or not.
        var manifest = await Plan(Message(["logo@sig"],
            Image("logo.png", "logo@sig", 2_000, disposition: ContentDisposition.Attachment)));

        Assert.Equal(EmailInquiryComponentDisposition.Process, Find(manifest, "logo.png")!.Disposition);
    }

    [Fact]
    public async Task A_cid_image_the_html_never_references_is_processed()
    {
        // A Content-Id proves nothing alone — a document attachment can carry one. Without a
        // body reference there is no evidence it is decoration.
        var manifest = await Plan(Message(["other@sig"], Image("logo.png", "orphan@sig", 2_000)));

        Assert.Equal(EmailInquiryComponentDisposition.Process, Find(manifest, "logo.png")!.Disposition);
    }

    [Fact]
    public async Task An_oversized_inline_image_is_processed_rather_than_exempted()
    {
        var manifest = await Plan(Message(["big@sig"],
            Image("banner.png", "big@sig", Threshold + 5_000)));

        Assert.Equal(EmailInquiryComponentDisposition.Process, Find(manifest, "banner.png")!.Disposition);
    }

    [Fact]
    public async Task A_QR_code_is_processed_because_nothing_proves_it_decorative()
    {
        // A QR code can encode a portal link or a reference number. It is small and inline like a
        // logo, so the classifier must not assume.
        var manifest = await Plan(Message(["qr@body"], Image("qr-code.png", "qr@body", 1_500)));

        Assert.Equal(EmailInquiryComponentDisposition.Process, Find(manifest, "qr-code.png")!.Disposition);
    }

    [Fact]
    public async Task A_dishonest_declared_size_cannot_buy_an_exemption_for_a_large_image()
    {
        // Declared size may never authorise Ignored. A 200 KB screenshot claiming 100 bytes is
        // still measured and still processed.
        var manifest = await Plan(Message(["shot@body"],
            Image("image002.png", "shot@body", Threshold + 100_000, declaredSize: 100)));

        Assert.Equal(EmailInquiryComponentDisposition.Process, Find(manifest, "image002.png")!.Disposition);
    }

    [Fact]
    public async Task An_unnamed_inline_cid_image_within_the_threshold_is_decorative_not_unnamed_skip()
    {
        // Tracking pixels and Exchange-style inline parts often carry no filename at all. Before
        // the classifier fired, these became "attachment has no filename" — a Skip, which sends
        // the whole message to review. Every message with a signature would have been reviewed.
        var manifest = await Plan(Message(["pixel@sig"], Image(null, "pixel@sig", 128)));

        var pixel = Assert.Single(manifest.Components, c => c.NestingDepth == 0 && c.Kind == EmailInquiryComponentKind.Attachment);
        Assert.Equal(EmailInquiryComponentDisposition.IgnoreInlineAsset, pixel.Disposition);
        Assert.NotEqual(EmailInquirySkipReasons.AttachmentUnnamed, pixel.ReasonCode);
    }

    [Fact]
    public async Task An_unnamed_inline_image_ABOVE_the_threshold_is_processed_not_skipped_as_unnamed()
    {
        // The screenshot variant of the case above: too big to be decoration, no filename to
        // judge by. It must reach extraction rather than being refused for lacking a name.
        var manifest = await Plan(Message(["shot@body"], Image(null, "shot@body", Threshold + 20_000)));

        var component = Assert.Single(manifest.Components,
            c => c.NestingDepth == 0 && c.Kind == EmailInquiryComponentKind.Attachment);
        Assert.Equal(EmailInquiryComponentDisposition.Process, component.Disposition);
    }
}
