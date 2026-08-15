using System.Text;
using ERP_RFQ_Automation.Ingestion.Assembly;
using MimeKit;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// Forwarded messages: the shape most real RFQs arrive in, and the one where double extraction
/// is easiest to introduce and hardest to notice.
///
/// <para><b>The duplication gate.</b> An embedded <c>message/rfc822</c> is represented as a
/// component so the forward is visible, and its children are planned as their own components so
/// a refused spreadsheet inside it cannot hide. Doing both naively sends the same content
/// through extraction twice — <c>EmailContainerReader</c> unwraps an <c>.eml</c> internally — so
/// the container is <b>structural only</b>: recorded, identified, counted by the barrier, never
/// extracted. These tests exist to keep that contract, because the duplicate lines it prevents
/// would appear on a real quotation.</para>
/// </summary>
public class EmailInquiryNestedMessageTests
{
    private const string MessageKey = "fwd-1@customer.example";

    private static MimePart File(string name, int size = 32, string mime = "application/pdf")
    {
        var slash = mime.IndexOf('/');
        return new MimePart(mime[..slash], mime[(slash + 1)..])
        {
            FileName = name,
            Content = new MimeContent(new MemoryStream(Encoding.ASCII.GetBytes(new string('x', size)))),
            ContentDisposition = new ContentDisposition(ContentDisposition.Attachment)
        };
    }

    private static MimeMessage Inner(string subject, params MimeEntity[] parts)
    {
        var message = new MimeMessage { Subject = subject };
        message.From.Add(new MailboxAddress("Original", "original@customer.example"));
        message.To.Add(new MailboxAddress("Buyer", "buyer@customer.example"));
        var multipart = new Multipart("mixed") { new TextPart("plain") { Text = "Please quote 40 flanges." } };
        foreach (var part in parts) multipart.Add(part);
        message.Body = multipart;
        return message;
    }

    private static MimeMessage Outer(params MimeEntity[] parts)
    {
        var message = new MimeMessage { Subject = "Fwd: enquiry" };
        message.From.Add(new MailboxAddress("Buyer", "buyer@customer.example"));
        message.To.Add(new MailboxAddress("Sales", "sales@nexora.example"));
        var multipart = new Multipart("mixed") { new TextPart("plain") { Text = "See below." } };
        foreach (var part in parts) multipart.Add(part);
        message.Body = multipart;
        return message;
    }

    private static Task<EmailInquiryManifest> Plan(MimeMessage message, EmailInquiryLimits? limits = null)
        => EmailInquiryManifestPlanner.PlanAsync(message, MessageKey, "See below.", limits);

    // ---- the duplication gate --------------------------------------------------------------

    [Fact]
    public async Task A_forwarded_message_container_is_structural_and_never_extracted()
    {
        var manifest = await Plan(Outer(new MessagePart { Message = Inner("Original enquiry") }));

        var container = Assert.Single(manifest.Components,
            c => c.Kind == EmailInquiryComponentKind.EmbeddedMessage);

        // Recorded and identified...
        Assert.Equal(EmailInquiryComponentDisposition.StructuralContainer, container.Disposition);
        Assert.NotEmpty(container.ContentHash);
        Assert.True(container.ByteSize > 0);

        // ...but carrying no content into extraction. If this becomes Process, every line inside
        // the forward is extracted twice and appears twice on the resulting inquiry.
        Assert.DoesNotContain(manifest.Processable, c => c.ComponentKey == container.ComponentKey);
        Assert.True(container.Content.IsEmpty);
    }

    [Fact]
    public async Task A_forwarded_body_only_RFQ_yields_exactly_one_commercial_body()
    {
        var manifest = await Plan(Outer(new MessagePart { Message = Inner("Original enquiry") }));

        // The outer body and the FORWARDED message's own body are both commercial; the container
        // between them is not. Two bodies, and no re-extraction of the container.
        var processable = manifest.Processable.ToList();
        Assert.Equal(2, processable.Count(c => c.Kind == EmailInquiryComponentKind.Body));
        Assert.DoesNotContain(processable, c => c.Kind == EmailInquiryComponentKind.EmbeddedMessage);
    }

    [Fact]
    public async Task A_forward_carrying_a_BOQ_assembles_each_evidence_item_exactly_once()
    {
        var manifest = await Plan(Outer(
            new MessagePart { Message = Inner("Original enquiry", File("BOQ.pdf")) }));

        // One PDF, once. Extracting the container as well would produce a second copy of these
        // very bytes under a different component identity.
        var pdfs = manifest.Processable.Where(c => c.FileName == "BOQ.pdf").ToList();
        Assert.Single(pdfs);

        var hashes = manifest.Processable.Select(c => c.ContentHash).ToList();
        Assert.Equal(hashes.Count, hashes.Distinct().Count());
    }

    // ---- identity across the tree ----------------------------------------------------------

    [Fact]
    public async Task Identically_named_files_in_two_forwards_get_distinct_keys()
    {
        var manifest = await Plan(Outer(
            new MessagePart { Message = Inner("First", File("quote.pdf")) },
            new MessagePart { Message = Inner("Second", File("quote.pdf")) }));

        var quotes = manifest.Components.Where(c => c.FileName == "quote.pdf").ToList();
        Assert.Equal(2, quotes.Count);
        Assert.Equal(2, quotes.Select(c => c.ComponentKey).Distinct().Count());
    }

    [Fact]
    public async Task No_key_collides_between_outer_attachment_container_and_nested_attachment()
    {
        var manifest = await Plan(Outer(
            File("outer.pdf"),
            new MessagePart { Message = Inner("Original", File("nested.pdf")) }));

        var keys = manifest.Components.Select(c => c.ComponentKey).ToList();
        Assert.Equal(keys.Count, keys.Distinct().Count());
        // Hierarchical: the nested part is addressed beneath its container.
        Assert.Contains(keys, k => k.Contains(":part:") && k.Split(':').Last().Contains('.'));
    }

    [Fact]
    public async Task Keys_and_ordinals_are_stable_across_a_re_plan()
    {
        static MimeMessage Build() => Outer(
            File("outer.pdf"),
            new MessagePart { Message = Inner("Original", File("nested.pdf")) });

        var first = await Plan(Build());
        var second = await Plan(Build());

        Assert.Equal(
            first.Components.Select(c => (c.ComponentKey, c.Ordinal)),
            second.Components.Select(c => (c.ComponentKey, c.Ordinal)));
    }

    [Fact]
    public async Task Ordinals_are_dense_across_the_whole_tree()
    {
        var manifest = await Plan(Outer(
            File("outer.pdf"),
            new MessagePart { Message = Inner("Original", File("a.pdf"), File("b.pdf")) }));

        Assert.Equal(
            Enumerable.Range(0, manifest.ExpectedComponentCount),
            manifest.Components.Select(c => c.Ordinal).OrderBy(o => o));
    }

    // ---- the shared budget ------------------------------------------------------------------

    [Fact]
    public async Task Depth_zero_refuses_to_open_a_forward_at_all()
    {
        var manifest = await Plan(
            Outer(new MessagePart { Message = Inner("Original", File("nested.pdf")) }),
            new EmailInquiryLimits { MaxNestingDepth = 0 });

        var container = Assert.Single(manifest.Components,
            c => c.Kind == EmailInquiryComponentKind.EmbeddedMessage);
        Assert.Equal(EmailInquirySkipReasons.NestingLimitExceeded, container.ReasonCode);
        // The nested file is not planned, because the forward was never opened.
        Assert.DoesNotContain(manifest.Components, c => c.FileName == "nested.pdf");
    }

    [Fact]
    public async Task One_level_of_nesting_is_followed_when_permitted()
    {
        var manifest = await Plan(
            Outer(new MessagePart { Message = Inner("Original", File("nested.pdf")) }),
            new EmailInquiryLimits { MaxNestingDepth = 1 });

        Assert.Contains(manifest.Processable, c => c.FileName == "nested.pdf");
    }

    [Fact]
    public async Task Nesting_past_the_maximum_is_refused_at_the_declared_boundary()
    {
        // A forward of a forward, against a one-level limit.
        var innermost = Inner("Innermost", File("deep.pdf"));
        var middle = new MimeMessage { Subject = "Middle" };
        middle.From.Add(new MailboxAddress("A", "a@customer.example"));
        middle.Body = new Multipart("mixed")
        {
            new TextPart("plain") { Text = "fwd" },
            new MessagePart { Message = innermost }
        };

        var manifest = await Plan(
            Outer(new MessagePart { Message = middle }),
            new EmailInquiryLimits { MaxNestingDepth = 1 });

        Assert.Contains(manifest.Components,
            c => c.ReasonCode == EmailInquirySkipReasons.NestingLimitExceeded);
        Assert.DoesNotContain(manifest.Components, c => c.FileName == "deep.pdf");
    }

    [Fact]
    public async Task The_component_budget_is_shared_across_sibling_branches_not_reissued()
    {
        // Two forwards, three files each. A per-branch budget would let both through; one shared
        // budget stops partway and says so.
        var manifest = await Plan(
            Outer(
                new MessagePart { Message = Inner("First", File("a1.pdf"), File("a2.pdf"), File("a3.pdf")) },
                new MessagePart { Message = Inner("Second", File("b1.pdf"), File("b2.pdf"), File("b3.pdf")) }),
            new EmailInquiryLimits { MaxComponents = 6 });

        Assert.True(manifest.ExpectedComponentCount <= 7,
            $"Planned {manifest.ExpectedComponentCount} components against a 6-component budget — "
            + "a recursion level was handed a fresh allowance.");
    }

    [Fact]
    public async Task The_byte_budget_is_shared_across_the_whole_tree()
    {
        var manifest = await Plan(
            Outer(new MessagePart { Message = Inner("Original", File("big1.pdf", 400), File("big2.pdf", 400)) }),
            new EmailInquiryLimits { MaxComponentBytes = 4096, MaxTotalBytes = 500 });

        Assert.Contains(manifest.Components,
            c => c.ReasonCode == EmailInquirySkipReasons.TotalSizeLimitExceeded);
    }

    [Fact]
    public async Task A_per_component_ceiling_still_applies_inside_a_forward()
    {
        var manifest = await Plan(
            Outer(new MessagePart { Message = Inner("Original", File("huge.pdf", 4000)) }),
            new EmailInquiryLimits { MaxComponentBytes = 200, MaxTotalBytes = 10_000_000 });

        Assert.Contains(manifest.Components,
            c => c.FileName == "huge.pdf" && c.ReasonCode == EmailInquirySkipReasons.AttachmentOversize);
    }

    [Fact]
    public async Task A_malformed_embedded_message_is_recorded_rather_than_crashing()
    {
        var manifest = await Plan(Outer(new MessagePart()));

        var container = Assert.Single(manifest.Components,
            c => c.Kind == EmailInquiryComponentKind.EmbeddedMessage);
        Assert.Equal(EmailInquirySkipReasons.EmbeddedMessageUnreadable, container.ReasonCode);
    }

    [Fact]
    public async Task Deep_nesting_terminates_at_the_declared_depth_without_exhausting_the_stack()
    {
        // Fifty levels against a three-level limit. Recursion is bounded before the call is made,
        // so stack depth is a declared constant rather than a property of the message.
        var current = Inner("Innermost", File("deep.pdf"));
        for (var level = 0; level < 50; level++)
        {
            var wrapper = new MimeMessage { Subject = $"Level {level}" };
            wrapper.From.Add(new MailboxAddress("A", "a@customer.example"));
            wrapper.Body = new Multipart("mixed")
            {
                new TextPart("plain") { Text = "fwd" },
                new MessagePart { Message = current }
            };
            current = wrapper;
        }

        var manifest = await Plan(
            Outer(new MessagePart { Message = current }),
            new EmailInquiryLimits { MaxNestingDepth = 3 });

        Assert.Contains(manifest.Components,
            c => c.ReasonCode == EmailInquirySkipReasons.NestingLimitExceeded);
        Assert.DoesNotContain(manifest.Components, c => c.FileName == "deep.pdf");
    }
}
