using System.Text;
using ERP_RFQ_Automation.Ingestion.Assembly;
using MimeKit;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// The ceiling has to bind DURING the copy, not after it.
///
/// <para>The planner previously decoded a part in full and compared the length afterwards, so a
/// hostile 800 MB attachment was materialised twice before being refused. These tests pin the
/// property that makes the stated limits real: bytes written never exceed the ceiling, whatever
/// the sender claims or sends.</para>
/// </summary>
public class BoundedComponentDecoderTests
{
    private static MimePart Part(byte[] content, long? declaredSize = null)
    {
        var part = new MimePart("application", "pdf")
        {
            FileName = "thing.pdf",
            Content = new MimeContent(new MemoryStream(content)),
            ContentDisposition = new ContentDisposition(ContentDisposition.Attachment)
        };
        if (declaredSize.HasValue) part.ContentDisposition.Size = declaredSize;
        return part;
    }

    private static byte[] Bytes(int count) => Encoding.ASCII.GetBytes(new string('x', count));

    [Fact]
    public async Task A_part_within_both_budgets_decodes_intact()
    {
        var result = await BoundedComponentDecoder.DecodeAsync(Part(Bytes(500)), 1_000, 10_000);

        Assert.Equal(BoundedDecodeOutcome.Decoded, result.Outcome);
        Assert.Equal(500, result.Bytes.Length);
    }

    [Fact]
    public async Task An_oversized_part_is_refused_without_materialising_beyond_the_ceiling()
    {
        // THE property. 2 MB of content against a 4 KB ceiling: the refusal must observe at most
        // the ceiling, not the part's real size. If this regresses, the observed count jumps to
        // the full length and the OOM vector is back.
        const long ceiling = 4_096;
        var result = await BoundedComponentDecoder.DecodeAsync(
            Part(Bytes(2 * 1024 * 1024)), ceiling, long.MaxValue);

        Assert.Equal(BoundedDecodeOutcome.ExceedsComponentLimit, result.Outcome);
        Assert.Empty(result.Bytes);
        Assert.True(result.ObservedBytes <= ceiling,
            $"Observed {result.ObservedBytes} bytes against a {ceiling}-byte ceiling — the decode "
            + "was not bounded during the copy.");
    }

    [Fact]
    public async Task A_part_that_would_exhaust_the_shared_message_budget_is_refused_as_such()
    {
        // Distinct outcome from the per-component case: "this file is too big" and "this message
        // as a whole is too big" call for different operator actions.
        var result = await BoundedComponentDecoder.DecodeAsync(
            Part(Bytes(5_000)), componentLimit: 1_000_000, remainingMessageBudget: 1_000);

        Assert.Equal(BoundedDecodeOutcome.ExceedsMessageBudget, result.Outcome);
        Assert.True(result.ObservedBytes <= 1_000);
    }

    [Fact]
    public async Task An_exhausted_message_budget_refuses_before_reading_anything()
    {
        var result = await BoundedComponentDecoder.DecodeAsync(
            Part(Bytes(10)), componentLimit: 1_000_000, remainingMessageBudget: 0);

        Assert.NotEqual(BoundedDecodeOutcome.Decoded, result.Outcome);
        Assert.Equal(0, result.ObservedBytes);
    }

    [Fact]
    public async Task A_dishonestly_small_declared_size_cannot_authorise_an_oversized_decode()
    {
        // The sender controls Content-Disposition: size. If a small declared size were trusted to
        // permit the decode, understating it would be the obvious way past the limit. Declared
        // size may only ever REJECT early; the bytes actually written are the verdict.
        const long ceiling = 4_096;
        var result = await BoundedComponentDecoder.DecodeAsync(
            Part(Bytes(1024 * 1024), declaredSize: 10), ceiling, long.MaxValue);

        Assert.Equal(BoundedDecodeOutcome.ExceedsComponentLimit, result.Outcome);
        Assert.True(result.ObservedBytes <= ceiling);
    }

    [Fact]
    public async Task An_honestly_oversized_declared_size_is_refused_without_touching_the_body()
    {
        var result = await BoundedComponentDecoder.DecodeAsync(
            Part(Bytes(100), declaredSize: 50_000_000), componentLimit: 4_096,
            remainingMessageBudget: long.MaxValue);

        Assert.Equal(BoundedDecodeOutcome.ExceedsComponentLimit, result.Outcome);
        // Nothing was written: the declared size alone refused it.
        Assert.Equal(50_000_000, result.ObservedBytes);
    }

    [Fact]
    public async Task A_part_exactly_at_the_ceiling_is_accepted()
    {
        // Off-by-one guard: the limit is inclusive, so a file of exactly the stated size is legal.
        var result = await BoundedComponentDecoder.DecodeAsync(Part(Bytes(1_000)), 1_000, 10_000);

        Assert.Equal(BoundedDecodeOutcome.Decoded, result.Outcome);
        Assert.Equal(1_000, result.Bytes.Length);
    }

    [Fact]
    public async Task An_embedded_message_is_serialized_under_the_same_ceilings()
    {
        var inner = new MimeMessage { Subject = "Forwarded enquiry" };
        inner.From.Add(new MailboxAddress("Buyer", "buyer@customer.example"));
        inner.To.Add(new MailboxAddress("Sales", "sales@nexora.example"));
        inner.Body = new TextPart("plain") { Text = new string('y', 50_000) };

        var refused = await BoundedComponentDecoder.SerializeAsync(inner, 1_024, long.MaxValue);
        Assert.Equal(BoundedDecodeOutcome.ExceedsComponentLimit, refused.Outcome);
        Assert.True(refused.ObservedBytes <= 1_024);

        var accepted = await BoundedComponentDecoder.SerializeAsync(inner, 1_000_000, long.MaxValue);
        Assert.Equal(BoundedDecodeOutcome.Decoded, accepted.Outcome);
        Assert.NotEmpty(accepted.Bytes);
    }

    [Fact]
    public async Task Cancellation_propagates_rather_than_being_swallowed_as_unreadable()
    {
        // A caller hanging up is not a malformed attachment, and reporting it as one would put a
        // false "could not be read" reason on a perfectly good file.
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            BoundedComponentDecoder.DecodeAsync(
                Part(Bytes(10_000)), 1_000_000, long.MaxValue, cancelled.Token));
    }
}
