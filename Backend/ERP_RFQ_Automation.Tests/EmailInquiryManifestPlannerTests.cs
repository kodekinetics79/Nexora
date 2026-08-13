using System.Text;
using ERP_RFQ_Automation.Ingestion.Assembly;
using MimeKit;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// What a message is expected to produce, decided once, up front.
///
/// <para>The manifest is the thing the barrier counts, so these tests are really about one
/// property: a part that will not be processed still gets a row. If a skipped part were simply
/// absent, "we dropped it" and "it was never there" would be the same observation, and the
/// barrier could be satisfied by failing to record something.</para>
/// </summary>
public class EmailInquiryManifestPlannerTests
{
    private const string MessageKey = "msg-1@customer.example";

    private static MimeMessage Message(string subject, params MimeEntity[] attachments)
    {
        var message = new MimeMessage();
        message.From.Add(new MailboxAddress("Buyer", "buyer@customer.example"));
        message.To.Add(new MailboxAddress("Sales", "sales@nexora.example"));
        message.Subject = subject;
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

    private static Task<EmailInquiryManifest> Plan(
        MimeMessage message, string? body = "Please quote 10 valves.", EmailInquiryLimits? limits = null)
        => EmailInquiryManifestPlanner.PlanAsync(message, MessageKey, body, limits);

    // ---- the ordinary shapes ---------------------------------------------------------------

    [Fact]
    public async Task Body_only_message_plans_one_processable_component()
    {
        var manifest = await Plan(Message("RFQ"));

        Assert.Equal(1, manifest.ExpectedComponentCount);
        var body = Assert.Single(manifest.Components);
        Assert.Equal(EmailInquiryComponentKind.Body, body.Kind);
        Assert.Equal(EmailInquiryComponentDisposition.Process, body.Disposition);
        Assert.False(manifest.QuotedOnlyBody);
    }

    [Fact]
    public async Task Body_plus_one_attachment_plans_two_and_both_are_processable()
    {
        var manifest = await Plan(Message("RFQ", File("boq.pdf")));

        Assert.Equal(2, manifest.ExpectedComponentCount);
        Assert.Equal(2, manifest.Processable.Count());
    }

    [Fact]
    public async Task Body_plus_multiple_attachments_plans_all_of_them()
    {
        var manifest = await Plan(Message("RFQ",
            File("a.pdf"), File("b.xlsx", mime: "application/vnd.ms-excel"), File("c.csv", mime: "text/csv")));

        Assert.Equal(4, manifest.ExpectedComponentCount);
        Assert.Equal(4, manifest.Processable.Count());
    }

    [Fact]
    public async Task Attachment_only_message_plans_no_body_component()
    {
        // A quoted-only reply carrying the priced attachment. The attachment is what matters
        // and it must not be held back by the absence of fresh prose.
        var manifest = await Plan(Message("Re: RFQ", File("boq.pdf")), body: "   ");

        Assert.True(manifest.QuotedOnlyBody);
        var only = Assert.Single(manifest.Components);
        Assert.Equal(EmailInquiryComponentKind.Attachment, only.Kind);
        Assert.Equal(EmailInquiryComponentDisposition.Process, only.Disposition);
    }

    [Fact]
    public async Task A_bare_quoted_reply_with_nothing_attached_plans_nothing()
    {
        // Expected count zero, which the barrier reads as NoInquiry — not as a finished
        // message, and not as a permanent hold.
        var manifest = await Plan(Message("Re: RFQ"), body: null);

        Assert.Equal(0, manifest.ExpectedComponentCount);
        Assert.True(manifest.QuotedOnlyBody);
    }

    // ---- skipped parts are recorded, never dropped -----------------------------------------

    [Fact]
    public async Task An_unsupported_attachment_is_recorded_with_its_reason_not_omitted()
    {
        var manifest = await Plan(Message("RFQ", File("drawing.dwg", mime: "application/acad")));

        Assert.Equal(2, manifest.ExpectedComponentCount);
        var skipped = Assert.Single(manifest.Components,
            c => c.Disposition == EmailInquiryComponentDisposition.Skip);
        Assert.Equal(EmailInquirySkipReasons.UnsupportedFileType, skipped.ReasonCode);
        Assert.Contains(".dwg", skipped.ReasonDetail);
    }

    [Fact]
    public async Task An_empty_attachment_is_recorded_as_empty()
    {
        var manifest = await Plan(Message("RFQ", File("empty.pdf", Array.Empty<byte>())));

        var skipped = Assert.Single(manifest.Components,
            c => c.Disposition == EmailInquiryComponentDisposition.Skip);
        Assert.Equal(EmailInquirySkipReasons.AttachmentEmpty, skipped.ReasonCode);
    }

    [Fact]
    public async Task An_oversized_attachment_is_recorded_as_oversize()
    {
        var limits = new EmailInquiryLimits { MaxComponentBytes = 16 };
        var manifest = await Plan(
            Message("RFQ", File("big.pdf", Encoding.UTF8.GetBytes(new string('x', 64)))), limits: limits);

        var skipped = Assert.Single(manifest.Components,
            c => c.Disposition == EmailInquiryComponentDisposition.Skip);
        Assert.Equal(EmailInquirySkipReasons.AttachmentOversize, skipped.ReasonCode);
    }

    [Fact]
    public async Task An_unnamed_attachment_is_recorded_rather_than_silently_ignored()
    {
        var unnamed = new MimePart("application", "pdf")
        {
            Content = new MimeContent(new MemoryStream(Encoding.UTF8.GetBytes("x"))),
            ContentDisposition = new ContentDisposition(ContentDisposition.Attachment)
        };
        var manifest = await Plan(Message("RFQ", unnamed));

        var skipped = Assert.Single(manifest.Components,
            c => c.Disposition == EmailInquiryComponentDisposition.Skip);
        Assert.Equal(EmailInquirySkipReasons.AttachmentUnnamed, skipped.ReasonCode);
    }

    [Fact]
    public async Task A_skip_reason_names_no_infrastructure_detail()
    {
        var manifest = await Plan(Message("RFQ", File("x.dwg", mime: "application/acad")));

        foreach (var component in manifest.Components.Where(c => c.ReasonDetail is not null))
        {
            Assert.DoesNotContain("Exception", component.ReasonDetail!, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("bucket", component.ReasonDetail!, StringComparison.OrdinalIgnoreCase);
        }
    }

    // ---- the inline-asset exception ---------------------------------------------------------

    private static MimePart InlineImage(
        string name = "logo.png", string? contentId = "logo@sig", long? size = 4096,
        string disposition = ContentDisposition.Inline, string mime = "image/png")
    {
        var slash = mime.IndexOf('/');
        var part = new MimePart(mime[..slash], mime[(slash + 1)..])
        {
            FileName = name,
            Content = new MimeContent(new MemoryStream(Encoding.UTF8.GetBytes("img"))),
            ContentDisposition = new ContentDisposition(disposition)
        };
        if (size.HasValue) part.ContentDisposition.Size = size;
        if (contentId is not null) part.ContentId = contentId;
        return part;
    }

    /// <summary>A message whose HTML body genuinely references the inline image by cid.</summary>
    private static MimeMessage MessageWithCidBody(string subject, string contentId, params MimeEntity[] parts)
    {
        var message = new MimeMessage();
        message.From.Add(new MailboxAddress("Buyer", "buyer@customer.example"));
        message.To.Add(new MailboxAddress("Sales", "sales@nexora.example"));
        message.Subject = subject;
        var related = new Multipart("related")
        {
            new TextPart("html") { Text = $"<p>Please quote.</p><img src=\"cid:{contentId}\">" }
        };
        foreach (var part in parts) related.Add(part);
        message.Body = related;
        return message;
    }

    [Fact]
    public async Task A_small_cid_referenced_inline_image_is_ignored_without_review()
    {
        // The reference must be REAL. A Content-Id alone proves nothing — a document attachment
        // can carry one — so the body has to point at it.
        var manifest = await Plan(MessageWithCidBody("RFQ", "logo@sig", InlineImage()));

        var asset = Assert.Single(manifest.Components,
            c => c.Disposition == EmailInquiryComponentDisposition.IgnoreInlineAsset);
        Assert.Equal(EmailInquirySkipReasons.NonCommercialInlineAsset, asset.ReasonCode);
    }

    [Theory]
    // No Content-Id: a genuine document attachment has no reason to carry one, so its absence
    // means this is not an embedded body image.
    [InlineData(null, 4096L, ContentDisposition.Inline, "image/png")]
    // Marked as an attachment: the sender is telling us it is content.
    [InlineData("logo@sig", 4096L, ContentDisposition.Attachment, "image/png")]
    // Not an image.
    [InlineData("doc@sig", 4096L, ContentDisposition.Inline, "application/pdf")]
    // Above the ceiling — a pasted screenshot of a priced table is structurally identical to a
    // logo and distinguishable ONLY by being big.
    [InlineData("shot@sig", 900_000L, ContentDisposition.Inline, "image/png")]
    // Undeclared size: unknown never means ignorable.
    [InlineData("logo@sig", null, ContentDisposition.Inline, "image/png")]
    public async Task Anything_short_of_a_provable_inline_asset_is_handled_normally(
        string? contentId, long? size, string disposition, string mime)
    {
        var manifest = await Plan(Message("RFQ",
            InlineImage("thing.png", contentId, size, disposition, mime)));

        Assert.DoesNotContain(manifest.Components,
            c => c.Disposition == EmailInquiryComponentDisposition.IgnoreInlineAsset);
    }

    [Fact]
    public async Task An_oversized_inline_image_is_extracted_rather_than_ignored()
    {
        // It falls through to normal handling, where images are a supported type. The
        // classifier removes decorations from consideration; it never removes something that
        // could plausibly carry a requirement.
        var manifest = await Plan(Message("RFQ",
            InlineImage("requirements.png", "shot@sig", 900_000)));

        Assert.Contains(manifest.Components,
            c => c.FileName == "requirements.png"
                 && c.Disposition == EmailInquiryComponentDisposition.Process);
    }

    // ---- embedded messages -----------------------------------------------------------------

    [Fact]
    public async Task An_embedded_message_becomes_a_structural_component_not_a_processable_one()
    {
        // It used to be recorded as "embedded email message is not ingested" and dropped, so a
        // forwarded enquiry was invisible to the pipeline. It is now REPRESENTED — but as a
        // structural container, not a processable one: its children are planned separately and
        // extracting the container as well would put the same body and attachments through
        // extraction twice, because EmailContainerReader unwraps an .eml internally.
        var inner = new MimeMessage();
        inner.From.Add(new MailboxAddress("Original", "original@customer.example"));
        inner.To.Add(new MailboxAddress("Buyer", "buyer@customer.example"));
        inner.Subject = "Original enquiry";
        inner.Body = new TextPart("plain") { Text = "Please quote 40 flanges." };

        var manifest = await Plan(Message("Fwd: enquiry", new MessagePart { Message = inner }));

        var embedded = Assert.Single(manifest.Components,
            c => c.Kind == EmailInquiryComponentKind.EmbeddedMessage);
        Assert.Equal(EmailInquiryComponentDisposition.StructuralContainer, embedded.Disposition);
        Assert.EndsWith(".eml", embedded.FileName);
        Assert.Equal("message/rfc822", embedded.MimeType);
        Assert.Equal(1, embedded.NestingDepth);
        // Identity is still recorded — presence, raw hash and size — so the forward is visible
        // and traceable even though it carries no content into extraction.
        Assert.True(embedded.ByteSize > 0);
        Assert.NotEmpty(embedded.ContentHash);
        Assert.True(embedded.Content.IsEmpty);

        // The forwarded message's OWN words are what carry the commercial content.
        Assert.Contains(manifest.Processable,
            c => c.Kind == EmailInquiryComponentKind.Body && c.NestingDepth == 1);
    }

    [Fact]
    public async Task Nesting_beyond_the_declared_depth_is_refused_at_the_boundary()
    {
        var limits = new EmailInquiryLimits { MaxNestingDepth = 0 };
        var inner = new MimeMessage { Subject = "Deep" };
        inner.From.Add(new MailboxAddress("A", "a@example.com"));
        inner.Body = new TextPart("plain") { Text = "x" };

        var manifest = await Plan(
            Message("Fwd", new MessagePart { Message = inner }), limits: limits);

        var refused = Assert.Single(manifest.Components,
            c => c.Kind == EmailInquiryComponentKind.EmbeddedMessage);
        Assert.Equal(EmailInquirySkipReasons.NestingLimitExceeded, refused.ReasonCode);
        Assert.Equal(EmailInquiryComponentDisposition.Skip, refused.Disposition);
    }

    // ---- hostile shapes terminate at a stated limit -----------------------------------------

    [Fact]
    public async Task The_component_ceiling_records_one_overflow_row_not_one_per_excess_part()
    {
        // Answering a pathological message with a row per part would make the refusal the
        // denial of service it is defending against.
        var attachments = Enumerable.Range(1, 40).Select(i => File($"f{i}.pdf")).ToArray();
        var limits = new EmailInquiryLimits { MaxComponents = 5 };

        var manifest = await Plan(Message("RFQ", attachments), limits: limits);

        Assert.Equal(6, manifest.ExpectedComponentCount);
        var overflow = Assert.Single(manifest.Components,
            c => c.ReasonCode == EmailInquirySkipReasons.ComponentLimitExceeded);
        Assert.Contains("were not processed", overflow.ReasonDetail);
    }

    [Fact]
    public async Task The_total_size_ceiling_refuses_the_part_that_crosses_it()
    {
        // No body, so the budget is spent only by the attachments: the first fits, the second
        // crosses. Each part is individually legal — the message as a whole is not.
        var limits = new EmailInquiryLimits { MaxComponentBytes = 1024, MaxTotalBytes = 200 };
        var manifest = await Plan(Message("RFQ",
            File("a.pdf", Encoding.UTF8.GetBytes(new string('a', 150))),
            File("b.pdf", Encoding.UTF8.GetBytes(new string('b', 150)))), body: null, limits: limits);

        Assert.Equal(2, manifest.ExpectedComponentCount);
        Assert.Single(manifest.Components, c => c.Disposition == EmailInquiryComponentDisposition.Process);
        Assert.Single(manifest.Components,
            c => c.ReasonCode == EmailInquirySkipReasons.TotalSizeLimitExceeded);
    }

    // ---- idempotency identity ---------------------------------------------------------------

    [Fact]
    public async Task Component_keys_are_stable_across_a_replanned_message()
    {
        // The restart/replay guarantee: the same message re-walked yields the same identities,
        // so a second pass updates the same rows instead of appending a parallel set.
        var first = await Plan(Message("RFQ", File("a.pdf"), File("b.pdf")));
        var second = await Plan(Message("RFQ", File("a.pdf"), File("b.pdf")));

        Assert.Equal(
            first.Components.Select(c => c.ComponentKey),
            second.Components.Select(c => c.ComponentKey));
    }

    [Fact]
    public async Task Component_keys_are_unique_within_a_message()
    {
        var manifest = await Plan(Message("RFQ",
            File("same.pdf"), File("same.pdf"), File("same.pdf")));

        Assert.Equal(
            manifest.Components.Count,
            manifest.Components.Select(c => c.ComponentKey).Distinct().Count());
    }

    [Fact]
    public async Task Component_keys_match_the_extraction_source_occurrence_convention()
    {
        // Byte-identical to the SourceOccurrenceId the queue already uses, so a returning job
        // finds its component without a second lookup table.
        var manifest = await Plan(Message("RFQ", File("a.pdf")));

        // Hierarchical PATH, not a flat counter. Once traversal recurses, a counter alone
        // collides: the third top-level attachment and the third attachment inside a forward
        // would both be "attachment:3".
        Assert.Contains($"email:{MessageKey}:body", manifest.Components.Select(c => c.ComponentKey));
        Assert.Contains(manifest.Components, c => c.ComponentKey.StartsWith($"email:{MessageKey}:part:"));
    }

    [Fact]
    public async Task Ordinals_are_dense_and_start_at_zero()
    {
        var manifest = await Plan(Message("RFQ", File("a.pdf"), File("b.dwg", mime: "application/acad")));

        Assert.Equal(
            Enumerable.Range(0, manifest.ExpectedComponentCount),
            manifest.Components.Select(c => c.Ordinal).OrderBy(o => o));
    }
}
