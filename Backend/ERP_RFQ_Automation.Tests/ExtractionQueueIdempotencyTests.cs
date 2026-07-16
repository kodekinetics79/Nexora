using System.Security.Cryptography;
using ERP_RFQ_Automation.Extraction;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Tests.Support;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// Content-hash idempotency of the extraction outbox (ADR-0003). Re-enqueuing identical
/// bytes for the same tenant must short-circuit to Duplicate (same job), the hash is
/// normalised to lower-case, and the (BusinessUnitId, ContentHash) pair is the composite
/// idempotency key so the same bytes for a different tenant is a distinct job. Exercised
/// against the real EF SaveChanges path on relational SQLite.
///
/// NOTE: the atomic ClaimAsync uses PostgreSQL-only "FOR UPDATE ... SKIP LOCKED" raw SQL
/// and PostgresException handling; it is not unit-testable on SQLite (see TESTING.md).
/// </summary>
public class ExtractionQueueIdempotencyTests
{
    private static ExtractionQueue NewQueue(ErpRfqAutomationContext ctx)
        => new(ctx, new NoopLogger<ExtractionQueue>());

    private static EnqueueExtractionRequest Request(long bu, byte[]? content = null, string? hash = null, string path = "blob://f")
        => new()
        {
            BusinessUnitId = bu,
            SourceType = ExtractionSourceType.ManualUpload,
            StoragePath = path,
            FileName = "rfq.pdf",
            FileType = "pdf",
            Content = content,
            ContentHash = hash
        };

    private static string Sha256Hex(byte[] bytes)
        => Convert.ToHexString(SHA256.HashData(bytes)); // upper-case hex, as a client might send

    [Fact]
    public async Task FirstEnqueue_CreatesPendingJob_AndLowercasesHash()
    {
        using var db = new TestDb();
        using var ctx = db.ContextFor(1);
        var queue = NewQueue(ctx);

        var result = await queue.EnqueueAsync(Request(1, hash: "ABCDEF0123"));

        Assert.Equal(EnqueueOutcome.Enqueued, result.Outcome);
        Assert.True(result.JobId > 0);
        Assert.Equal("abcdef0123", result.ContentHash);

        using var verify = db.ContextFor(1);
        var job = verify.Set<ExtractionJob>().Single();
        Assert.Equal(ExtractionStatus.Pending, job.Status);
        Assert.Equal("abcdef0123", job.ContentHash);
        Assert.Equal(1, job.BusinessUnitId);
    }

    [Fact]
    public async Task DuplicateContent_SameTenant_ShortCircuitsToDuplicate()
    {
        var bytes = new byte[] { 1, 2, 3, 4, 5 };
        using var db = new TestDb();
        using var ctx = db.ContextFor(1);
        var queue = NewQueue(ctx);

        var first = await queue.EnqueueAsync(Request(1, content: bytes));
        var second = await queue.EnqueueAsync(Request(1, content: bytes));

        Assert.Equal(EnqueueOutcome.Enqueued, first.Outcome);
        Assert.Equal(EnqueueOutcome.Duplicate, second.Outcome);
        Assert.Equal(first.JobId, second.JobId);

        using var verify = db.ContextFor(1);
        Assert.Equal(1, verify.Set<ExtractionJob>().Count()); // no second row
    }

    [Fact]
    public async Task SameContent_DifferentTenant_ProducesDistinctJobs()
    {
        var bytes = new byte[] { 9, 9, 9 };
        using var db = new TestDb();
        using var ctx = db.ContextFor(null);
        var queue = NewQueue(ctx);

        var bu1 = await queue.EnqueueAsync(Request(1, content: bytes));
        var bu2 = await queue.EnqueueAsync(Request(2, content: bytes));

        Assert.Equal(EnqueueOutcome.Enqueued, bu1.Outcome);
        Assert.Equal(EnqueueOutcome.Enqueued, bu2.Outcome);
        Assert.NotEqual(bu1.JobId, bu2.JobId);
        Assert.Equal(bu1.ContentHash, bu2.ContentHash); // same bytes -> same hash, different key

        using var verify = db.ContextFor(null);
        Assert.Equal(2, verify.Set<ExtractionJob>().Count());
    }

    [Fact]
    public async Task ContentHashing_IsConsistent_BetweenComputedAndPrecomputedHash()
    {
        var bytes = new byte[] { 42, 7, 100, 255, 0 };
        using var db = new TestDb();
        using var ctx = db.ContextFor(1);
        var queue = NewQueue(ctx);

        // Enqueue by raw bytes (service computes SHA-256)...
        var computed = await queue.EnqueueAsync(Request(1, content: bytes));
        // ...then by the equivalent precomputed upper-case hash: must dedup to the same job.
        var precomputed = await queue.EnqueueAsync(Request(1, hash: Sha256Hex(bytes)));

        Assert.Equal(EnqueueOutcome.Enqueued, computed.Outcome);
        Assert.Equal(EnqueueOutcome.Duplicate, precomputed.Outcome);
        Assert.Equal(computed.JobId, precomputed.JobId);
        Assert.Equal(Sha256Hex(bytes).ToLowerInvariant(), computed.ContentHash);
    }

    [Fact]
    public async Task MissingContentAndHash_Throws()
    {
        using var db = new TestDb();
        using var ctx = db.ContextFor(1);
        var queue = NewQueue(ctx);

        await Assert.ThrowsAsync<ArgumentException>(() => queue.EnqueueAsync(Request(1)));
    }

    [Fact]
    public async Task MissingStoragePath_Throws()
    {
        using var db = new TestDb();
        using var ctx = db.ContextFor(1);
        var queue = NewQueue(ctx);

        await Assert.ThrowsAsync<ArgumentException>(
            () => queue.EnqueueAsync(Request(1, hash: "abc", path: "   ")));
    }
}
