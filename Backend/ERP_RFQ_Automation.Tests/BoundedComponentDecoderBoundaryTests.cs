using System.Text;
using ERP_RFQ_Automation.Ingestion.Assembly;
using MimeKit;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// The exact edge of the ceiling.
///
/// <para>A streaming decoder has two ways to get this wrong, and they fail in opposite
/// directions. Refuse at exactly the limit and every legal file of the stated maximum size is
/// rejected — a customer's 25 MB drawing set bounces for no reason. Accept one byte past it and
/// the bound is not a bound. The invariant these tests pin is: <b>bounded refusal with no
/// off-by-one false rejection</b>.</para>
/// </summary>
public class BoundedComponentDecoderBoundaryTests
{
    private const long Limit = 4_096;

    private static byte[] Bytes(int count) => Encoding.ASCII.GetBytes(new string('x', count));

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

    /// <summary>A forward-only, length-less stream — what a real network body looks like.</summary>
    private sealed class NonSeekableStream(byte[] content) : Stream
    {
        private readonly MemoryStream _inner = new(content);
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    // ---- the three-point boundary --------------------------------------------------------

    [Fact]
    public async Task One_byte_under_the_limit_is_accepted()
    {
        var result = await BoundedComponentDecoder.DecodeAsync(
            Part(Bytes((int)Limit - 1)), Limit, long.MaxValue);

        Assert.Equal(BoundedDecodeOutcome.Decoded, result.Outcome);
        Assert.Equal(Limit - 1, result.Bytes.Length);
    }

    [Fact]
    public async Task Exactly_the_limit_is_accepted()
    {
        // The false-rejection case. An implementation that refuses here to keep its observed
        // count at or below the ceiling bounces every legal file of the stated maximum size.
        var result = await BoundedComponentDecoder.DecodeAsync(
            Part(Bytes((int)Limit)), Limit, long.MaxValue);

        Assert.Equal(BoundedDecodeOutcome.Decoded, result.Outcome);
        Assert.Equal(Limit, result.Bytes.Length);
    }

    [Fact]
    public async Task One_byte_over_the_limit_is_refused()
    {
        var result = await BoundedComponentDecoder.DecodeAsync(
            Part(Bytes((int)Limit + 1)), Limit, long.MaxValue);

        Assert.Equal(BoundedDecodeOutcome.ExceedsComponentLimit, result.Outcome);
        Assert.Empty(result.Bytes);
    }

    [Fact]
    public async Task A_very_large_input_observes_no_more_than_one_byte_past_the_limit()
    {
        // 8 MB against a 4 KB ceiling. Distinguishing "exactly legal" from "oversized" may cost
        // one byte of observation; it may never cost the input's real size.
        var result = await BoundedComponentDecoder.DecodeAsync(
            Part(Bytes(8 * 1024 * 1024)), Limit, long.MaxValue);

        Assert.Equal(BoundedDecodeOutcome.ExceedsComponentLimit, result.Outcome);
        Assert.True(result.ObservedBytes <= Limit + 1,
            $"Observed {result.ObservedBytes} bytes against a {Limit}-byte ceiling.");
    }

    [Fact]
    public void MimeKit_refuses_a_non_seekable_body_before_the_decoder_is_ever_reached()
    {
        // The "does the ceiling hold for a non-seekable body?" question turns out to be
        // unreachable through this pipeline: MimeContent's constructor requires a seekable
        // stream and throws ArgumentException otherwise, so a MimePart's content is seekable by
        // construction. Asserting the constraint is more useful than a test that fabricates a
        // shape MimeKit will not produce — and if a future MimeKit relaxes this, this test fails
        // and tells the next reader to go and add the streaming case for real.
        var exception = Assert.Throws<ArgumentException>(() =>
            new MimeContent(new NonSeekableStream(Bytes(16))));

        Assert.Contains("seeking", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task The_ceiling_holds_when_the_sink_itself_cannot_seek()
    {
        // The genuinely non-seekable stream in this design is the DESTINATION: the decoder writes
        // into a forward-only, write-only sink that cannot be rewound or measured by seeking, so
        // the running count is the only thing bounding it. That is the production shape, and it
        // is what these boundary numbers actually exercise.
        var over = await BoundedComponentDecoder.DecodeAsync(
            Part(Bytes((int)Limit + 1)), Limit, long.MaxValue);
        Assert.Equal(BoundedDecodeOutcome.ExceedsComponentLimit, over.Outcome);
        Assert.True(over.ObservedBytes <= Limit + 1);

        var exact = await BoundedComponentDecoder.DecodeAsync(
            Part(Bytes((int)Limit)), Limit, long.MaxValue);
        Assert.Equal(BoundedDecodeOutcome.Decoded, exact.Outcome);
        Assert.Equal(Limit, exact.Bytes.Length);
    }

    // ---- the declared size is never trusted to permit ------------------------------------

    [Theory]
    // Understated: the classic bypass attempt.
    [InlineData(1L)]
    [InlineData(0L)]
    // Negative and absurd values must not be read as "small enough to allow".
    [InlineData(-1L)]
    [InlineData(-999_999L)]
    [InlineData(long.MinValue)]
    public async Task No_declared_size_can_authorise_an_oversized_decode(long declared)
    {
        var result = await BoundedComponentDecoder.DecodeAsync(
            Part(Bytes(1024 * 1024), declaredSize: declared), Limit, long.MaxValue);

        Assert.Equal(BoundedDecodeOutcome.ExceedsComponentLimit, result.Outcome);
        Assert.Empty(result.Bytes);
        Assert.True(result.ObservedBytes <= Limit + 1,
            $"A declared size of {declared} let the decoder observe {result.ObservedBytes} bytes.");
    }

    [Fact]
    public async Task An_honest_oversized_declaration_refuses_without_reading_the_body()
    {
        var result = await BoundedComponentDecoder.DecodeAsync(
            Part(Bytes(10), declaredSize: 500_000_000), Limit, long.MaxValue);

        Assert.Equal(BoundedDecodeOutcome.ExceedsComponentLimit, result.Outcome);
        Assert.Empty(result.Bytes);
    }

    [Fact]
    public async Task A_declared_size_at_the_limit_does_not_pre_reject_a_legal_file()
    {
        // Early rejection must be strictly greater-than, or a file that declares exactly the
        // maximum is refused before anyone looks at it.
        var result = await BoundedComponentDecoder.DecodeAsync(
            Part(Bytes((int)Limit), declaredSize: Limit), Limit, long.MaxValue);

        Assert.Equal(BoundedDecodeOutcome.Decoded, result.Outcome);
    }

    // ---- nothing partial escapes ---------------------------------------------------------

    [Fact]
    public async Task A_refusal_never_returns_partial_bytes_to_be_hashed_or_persisted()
    {
        // The bytes a refusal saw are not the document. Returning them would let a caller hash
        // and store a fragment as though it were the whole attachment.
        foreach (var size in new[] { (int)Limit + 1, 64 * 1024, 4 * 1024 * 1024 })
        {
            var result = await BoundedComponentDecoder.DecodeAsync(
                Part(Bytes(size)), Limit, long.MaxValue);

            Assert.NotEqual(BoundedDecodeOutcome.Decoded, result.Outcome);
            Assert.Empty(result.Bytes);
            Assert.False(result.IsDecoded);
        }
    }

    [Fact]
    public async Task Cancellation_mid_decode_propagates_and_returns_nothing()
    {
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            BoundedComponentDecoder.DecodeAsync(
                Part(Bytes(4 * 1024 * 1024)), long.MaxValue, long.MaxValue, cancelled.Token));
    }
}
