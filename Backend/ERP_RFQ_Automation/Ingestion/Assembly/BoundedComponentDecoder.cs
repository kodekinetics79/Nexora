using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using MimeKit;

namespace ERP_RFQ_Automation.Ingestion.Assembly;

/// <summary>Why a bounded decode stopped.</summary>
public enum BoundedDecodeOutcome
{
    /// <summary>Fully decoded within both budgets.</summary>
    Decoded,

    /// <summary>Larger than this one component is allowed to be.</summary>
    ExceedsComponentLimit,

    /// <summary>Would push the whole message past its shared byte budget.</summary>
    ExceedsMessageBudget,

    /// <summary>The part could not be decoded at all — malformed encoding, truncated body.</summary>
    Unreadable
}

/// <param name="Bytes">Decoded content, empty unless <paramref name="Outcome"/> is Decoded.</param>
/// <param name="ObservedBytes">
/// How many bytes were actually written before the decode stopped. On a refusal this is the cap
/// plus at most one buffer — never the part's full size, because the full size is never
/// materialized.
/// </param>
public readonly record struct BoundedDecodeResult(
    BoundedDecodeOutcome Outcome, byte[] Bytes, long ObservedBytes)
{
    public bool IsDecoded => Outcome == BoundedDecodeOutcome.Decoded;
}

/// <summary>
/// Decodes one MIME part under a hard ceiling, abandoning the copy the moment the ceiling is
/// crossed.
///
/// <para><b>The defect this replaces.</b> The planner used to do
/// <c>DecodeToAsync(buffer)</c> then <c>buffer.ToArray()</c> and only THEN compare the length
/// against the limit. A single 800 MB base64 part was therefore fully decoded into a
/// <see cref="MemoryStream"/> and copied again (≈1.6 GB transient) before being refused as
/// oversize, and fifty 20 MB parts retained ≈1 GB of arrays for the lifetime of one manifest. The
/// class comment claimed the limits made a hostile message "terminate at a stated limit rather
/// than by exhausting memory"; it did the opposite, and one large message could take the poller
/// down and every in-flight message with it.</para>
///
/// <para><b>Declared sizes are a hint, never the verdict.</b> A sender controls
/// <c>Content-Disposition: size</c> and the transfer encoding, so a small declared size on a huge
/// body is the obvious way to walk past a limit that trusts it. Declared size may only ever
/// REJECT early — it can never authorise a decode — and the authoritative number is the count of
/// bytes actually written.</para>
/// </summary>
public static class BoundedComponentDecoder
{
    /// <summary>
    /// Decodes <paramref name="part"/> while enforcing both the per-component ceiling and the
    /// message's remaining shared budget, whichever binds first.
    /// </summary>
    /// <param name="remainingMessageBudget">
    /// Bytes still available to the WHOLE message tree, nested content included. Passing the
    /// per-message total here rather than a fresh allowance per branch is what stops a nested
    /// message from being handed a new budget of its own.
    /// </param>
    public static async Task<BoundedDecodeResult> DecodeAsync(
        MimePart part, long componentLimit, long remainingMessageBudget,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(part);

        // Declared size is consulted ONLY to refuse without touching the body. It is never
        // allowed to shorten or skip the real count below.
        var declared = part.ContentDisposition?.Size;
        if (declared is { } size && size > componentLimit)
            return new BoundedDecodeResult(BoundedDecodeOutcome.ExceedsComponentLimit, [], size);

        var ceiling = Math.Min(componentLimit, Math.Max(remainingMessageBudget, 0));
        await using var sink = new CeilingStream(ceiling);
        try
        {
            await part.Content.DecodeToAsync(sink, ct);
        }
        catch (CeilingExceededException)
        {
            // Which budget bound first decides what the operator is told: "this file is too big"
            // and "this message as a whole is too big" call for different actions.
            return componentLimit <= remainingMessageBudget
                ? new BoundedDecodeResult(BoundedDecodeOutcome.ExceedsComponentLimit, [], sink.Written)
                : new BoundedDecodeResult(BoundedDecodeOutcome.ExceedsMessageBudget, [], sink.Written);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return new BoundedDecodeResult(BoundedDecodeOutcome.Unreadable, [], sink.Written);
        }

        return new BoundedDecodeResult(BoundedDecodeOutcome.Decoded, sink.ToArray(), sink.Written);
    }

    /// <summary>
    /// Serializes an embedded message under the same ceilings. Kept beside the part decoder so
    /// nested content cannot quietly acquire a different limit.
    /// </summary>
    public static async Task<BoundedDecodeResult> SerializeAsync(
        MimeMessage message, long componentLimit, long remainingMessageBudget,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(message);

        var ceiling = Math.Min(componentLimit, Math.Max(remainingMessageBudget, 0));
        await using var sink = new CeilingStream(ceiling);
        try
        {
            await message.WriteToAsync(sink, ct);
        }
        catch (CeilingExceededException)
        {
            return componentLimit <= remainingMessageBudget
                ? new BoundedDecodeResult(BoundedDecodeOutcome.ExceedsComponentLimit, [], sink.Written)
                : new BoundedDecodeResult(BoundedDecodeOutcome.ExceedsMessageBudget, [], sink.Written);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return new BoundedDecodeResult(BoundedDecodeOutcome.Unreadable, [], sink.Written);
        }

        return new BoundedDecodeResult(BoundedDecodeOutcome.Decoded, sink.ToArray(), sink.Written);
    }

    private sealed class CeilingExceededException : Exception;

    /// <summary>
    /// A write-only sink that accepts at most <c>ceiling</c> bytes and then refuses.
    ///
    /// <para>Throwing rather than truncating is deliberate: a silently truncated attachment would
    /// be extracted as if it were the whole document, and half a bill of quantities priced as a
    /// complete one is worse than a refusal an operator can see.</para>
    /// </summary>
    private sealed class CeilingStream(long ceiling) : Stream
    {
        private readonly MemoryStream _inner = new();

        public long Written => _inner.Length;

        public byte[] ToArray() => _inner.ToArray();

        public override void Write(byte[] buffer, int offset, int count)
        {
            if (_inner.Length + count > ceiling) throw new CeilingExceededException();
            _inner.Write(buffer, offset, count);
        }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            if (_inner.Length + buffer.Length > ceiling) throw new CeilingExceededException();
            _inner.Write(buffer);
        }

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken ct)
        {
            Write(buffer, offset, count);
            return Task.CompletedTask;
        }

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
        {
            Write(buffer.Span);
            return ValueTask.CompletedTask;
        }

        public override void WriteByte(byte value)
        {
            if (_inner.Length + 1 > ceiling) throw new CeilingExceededException();
            _inner.WriteByte(value);
        }

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => _inner.Length;
        public override long Position { get => _inner.Position; set => throw new NotSupportedException(); }
        public override void Flush() => _inner.Flush();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing) _inner.Dispose();
            base.Dispose(disposing);
        }
    }
}
