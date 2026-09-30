using ERP_RFQ_Automation.DocumentIntelligence.Persistence;
using ERP_RFQ_Automation.Extraction;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Services.DocumentIntelligence;
using ERP_RFQ_Automation.Services.Interfaces;
using ERP_RFQ_Automation.Tests.Support;
using Microsoft.EntityFrameworkCore;
using CanonicalDtos = ERP_RFQ_Automation.DTOs.DocumentIntelligence;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// PERF-04 and XS-09 (pilot audit 2026-09-28).
///
/// <para>A 1,500-line DOCX took 237 s to become a lead, 177 s of it saving its 1,500 canonical
/// lines one SaveChanges at a time, each save re-scanning every entity the persist transaction
/// tracks. The lines are now saved in batches; the rows written are the same.</para>
///
/// <para>Under load a 23-line SEC document outlived the one lease renewal the persist got, fenced
/// its own completion and re-ran the whole job three times. The persist now renews the lease at
/// its own checkpoints once a third of it has passed.</para>
/// </summary>
public sealed class ExtractionPersistBatchingTests
{
    private const long Tenant = 7_501;

    [Fact]
    public async Task Ledger_saves_do_not_grow_with_the_line_count()
    {
        var small = await PersistLedgerAsync(lines: 12);
        var large = await PersistLedgerAsync(lines: 1_200);

        // Before: one save per line — 12 + k against 1,200 + k.
        Assert.True(large.Saves - small.Saves <= 1_200 / StructuredEvidenceLedgerPersister.LineBatchSize,
            $"12 lines took {small.Saves} saves and 1,200 lines took {large.Saves}.");
    }

    [Fact]
    public async Task Every_line_is_written_bound_to_its_own_lead_line_with_its_evidence()
    {
        var result = await PersistLedgerAsync(lines: 1_200);

        Assert.Equal(1_200, result.Lines.Count);
        Assert.Equal(Enumerable.Range(1, 1_200), result.Lines.Select(x => x.LineNumber));
        // Each canonical line is bound to the lead line with the same customer line number.
        Assert.All(result.Lines, line => Assert.Equal(line.LineNumber.ToString(), result.LeadItemNumbers[line.LeadItemId!.Value]));
        Assert.Equal(1_200, result.Lines.Select(x => x.LeadItemId).Distinct().Count());
        // Four cells per line, one region and one evidence row each.
        Assert.Equal(4_800, result.Regions);
        Assert.Equal(4_800, result.Evidence);
        // Ids still follow the document's line order.
        Assert.Equal(result.Lines.Select(x => x.LineNumber), result.Lines.OrderBy(x => x.Id).Select(x => x.LineNumber));
    }

    [Fact]
    public async Task The_ledger_calls_the_lease_keep_alive_after_its_saves()
    {
        var calls = 0;
        await PersistLedgerAsync(lines: 1_200, keepAlive: _ => { calls++; return Task.CompletedTask; });
        Assert.True(calls >= 5, $"keep-alive called {calls} times");
    }

    [Fact]
    public async Task A_persist_slower_than_a_third_of_its_lease_keeps_renewing_it()
    {
        // Every clock read is ten minutes later: a starved machine, compressed.
        var queue = new RecordingQueue();
        var leadId = await PersistAndCompleteAsync(queue, new SteppingClock(TimeSpan.FromMinutes(10)));

        Assert.NotNull(leadId);
        Assert.True(queue.RenewedFor.Count > 1, $"renewed {queue.RenewedFor.Count} time(s)");
        // Every renewal is for the document-sized lease, never the base five minutes.
        Assert.All(queue.RenewedFor, lease => Assert.Equal(TimeSpan.FromMinutes(5), lease));
        Assert.True(queue.Completed);
    }

    [Fact]
    public async Task A_lease_found_expired_mid_persist_stops_before_completion()
    {
        var queue = new RecordingQueue { RenewalsAllowed = 1 };

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            PersistAndCompleteAsync(queue, new SteppingClock(TimeSpan.FromMinutes(10))));

        Assert.Contains("Fenced completion failed", error.Message, StringComparison.Ordinal);
        Assert.False(queue.Completed);
    }

    [Fact]
    public async Task A_quick_persist_renews_once_as_before()
    {
        var queue = new RecordingQueue();
        await PersistAndCompleteAsync(queue, TimeProvider.System);
        Assert.Equal([TimeSpan.FromMinutes(5)], queue.RenewedFor);
        Assert.True(queue.Completed);
    }

    [Fact]
    public async Task The_keep_alive_renews_only_when_a_third_of_the_lease_has_passed()
    {
        var clock = new ManualClock();
        var renewals = 0;
        var keepAlive = new PersistLeaseKeepAlive(1, TimeSpan.FromMinutes(9), _ => { renewals++; return Task.FromResult(true); }, clock);

        clock.Advance(TimeSpan.FromMinutes(2));
        await keepAlive.RenewIfDueAsync(default);
        Assert.Equal(0, renewals);

        clock.Advance(TimeSpan.FromMinutes(1));
        await keepAlive.RenewIfDueAsync(default);
        Assert.Equal(1, renewals);

        clock.Advance(TimeSpan.FromMinutes(2));
        await keepAlive.RenewIfDueAsync(default);
        Assert.Equal(1, renewals);
        Assert.Equal(1, keepAlive.Renewals);
    }

    // ------------------------------------------------------------------ fixture

    private sealed record LedgerResult(int Saves, List<CanonicalLineItem> Lines,
        Dictionary<long, string?> LeadItemNumbers, int Regions, int Evidence);

    private static async Task<LedgerResult> PersistLedgerAsync(int lines, Func<CancellationToken, Task>? keepAlive = null)
    {
        using var db = new TestDb();
        var job = Job(lines);
        long leadId;
        await using (var seed = db.ContextFor(null))
        {
            Seed.BusinessUnit(seed, Tenant);
            await seed.SaveChangesAsync();
            var corpus = DocumentCorpus.Create(Tenant, job.BatchId, CorpusSourceType.ManualUpload);
            seed.Add(corpus);
            await seed.SaveChangesAsync();
            var source = SourceDocument.Create(Tenant, corpus.Id, job.ContentHash, job.FileName!,
                "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", "test", $"ledger/{lines}", "v1", 1);
            source.MarkSecurityStatus(DocumentSecurityStatus.Cleared);
            seed.Add(source);
            var lead = Seed.Lead(seed, 70_000 + lines, Tenant, items: Enumerable.Range(1, lines).Select(i => new LeadItem
            {
                LineItemNo = i.ToString(), ProductShortDescription = $"Part {i}", Quantity = i, UnitOfMeasure = "EA",
            }));
            await seed.SaveChangesAsync();
            leadId = lead.Id;
        }

        var saves = 0;
        await using (var context = db.ContextFor(null))
        {
            // Tracked, as the worker loads them: the cost of each save grew with this graph.
            var leads = await context.Leads.Include(x => x.LeadItems).Where(x => x.Id == leadId).ToListAsync();
            context.SavedChanges += (_, _) => saves++;
            // The SQLite schema EnsureCreated builds points field_evidence's run key at a table
            // no ledger row is written to ("ExtractionRun"), so every evidence insert fails its
            // foreign key there, before and after this change alike. PostgreSQL's migrated schema
            // is the one that enforces that key (AuthoritativeEvidencePostgreSqlTests); this
            // fixture measures the saves and the rows written.
            await context.Database.ExecuteSqlRawAsync("PRAGMA foreign_keys = OFF");
            await new StructuredEvidenceLedgerPersister(context, keepAlive)
                .PersistAsync(job, Outcome(lines), leads);
        }

        await using var read = db.ContextFor(null);
        return new LedgerResult(saves,
            await read.Set<CanonicalLineItem>().AsNoTracking().OrderBy(x => x.LineNumber).ToListAsync(),
            await read.Set<LeadItem>().IgnoreQueryFilters().AsNoTracking().Where(x => x.LeadId == leadId)
                .ToDictionaryAsync(x => x.Id, x => x.LineItemNo),
            await read.Set<DocumentRegion>().CountAsync(),
            await read.Set<FieldEvidence>().CountAsync());
    }

    private static async Task<long?> PersistAndCompleteAsync(RecordingQueue queue, TimeProvider clock)
    {
        using var db = new TestDb();
        var job = Job(300, businessUnitId: 1);
        await using (var seed = db.ContextFor(null))
        {
            Seed.BusinessUnit(seed, 1);
            Seed.EmailConfig(seed, 100, 1);
            await seed.SaveChangesAsync();
            var corpus = DocumentCorpus.Create(1, job.BatchId, CorpusSourceType.ManualUpload);
            seed.Add(corpus);
            await seed.SaveChangesAsync();
            var source = SourceDocument.Create(1, corpus.Id, job.ContentHash, job.FileName!,
                "application/vnd.openxmlformats-officedocument.wordprocessingml.document", "test", "keep-alive", "v1", 1);
            source.MarkSecurityStatus(DocumentSecurityStatus.Cleared);
            seed.Add(source);
            await seed.SaveChangesAsync();
        }
        var outcome = new ChunkedExtractionOutcome
        {
            Status = ExtractionOutcomeStatus.Ok,
            Result = Ext.Result(Ext.Items(300, 0.9), 0.9) with { Rfqno = "SE-RFP-C001832145" },
            ExtractedItemCount = 300,
            ExpectedItemCount = 300,
        };
        await using var context = db.ContextFor(null);
        var persister = new LeadPersister(context, new NoopLogger<LeadPersister>()) { PersistClock = clock };
        return await persister.PersistAndCompleteAsync(job, outcome, queue, "worker-a", 1, TimeSpan.FromMinutes(5));
    }

    private static ExtractionJob Job(int lines, long businessUnitId = Tenant) => new()
    {
        Id = 7_000 + lines,
        BatchId = Guid.NewGuid(),
        BusinessUnitId = businessUnitId,
        SourceType = ExtractionSourceType.ManualUpload,
        ContentHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes($"ledger-{lines}-{businessUnitId}"))).ToLowerInvariant(),
        StoragePath = "/nonexistent/extraction/bid-list.xlsx",
        FileName = "bid-list.xlsx",
        FileType = "xlsx",
        Attempts = 1,
    };

    private static ChunkedExtractionOutcome Outcome(int lines)
    {
        var document = new CanonicalDtos.CanonicalRfqDocument
        {
            BusinessUnitId = Tenant,
            ValidationStatus = CanonicalDtos.ValidationStatus.Valid,
        };
        for (var i = 1; i <= lines; i++)
        {
            var row = i + 1;
            document.LineItems.Add(new CanonicalDtos.CanonicalRfqLineItem
            {
                LineItemNo = Cell(i.ToString(), $"'Sheet1'!A{row}"),
                ProductName = Cell($"Part {i}", $"'Sheet1'!B{row}"),
                Quantity = Cell((decimal)i, $"'Sheet1'!C{row}"),
                UnitOfMeasure = Cell("EA", $"'Sheet1'!D{row}"),
                ValidationStatus = CanonicalDtos.ValidationStatus.Valid,
            });
        }
        return new ChunkedExtractionOutcome
        {
            Status = ExtractionOutcomeStatus.Ok,
            Result = Ext.Result(Ext.Items(lines, 1.0), 1.0),
            ExtractedItemCount = lines,
            ExpectedItemCount = lines,
            CanonicalImport = new CanonicalRfqImportResult { Documents = [document] },
        };
    }

    private static CanonicalDtos.CanonicalValue<T> Cell<T>(T value, string location) => new()
    {
        Value = value,
        OriginalValue = value?.ToString(),
        Confidence = 1m,
        ValidationStatus = CanonicalDtos.ValidationStatus.Valid,
        Evidence = [new CanonicalDtos.SourceEvidence { Location = location, RawValue = value?.ToString() }],
    };

    private sealed class SteppingClock(TimeSpan step) : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 9, 28, 0, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow()
        {
            var now = _now;
            _now += step;
            return now;
        }
    }

    private sealed class ManualClock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 9, 28, 0, 0, 0, TimeSpan.Zero);
        public void Advance(TimeSpan by) => _now += by;
        public override DateTimeOffset GetUtcNow() => _now;
    }

    private sealed class RecordingQueue : IExtractionQueue
    {
        public List<TimeSpan> RenewedFor { get; } = new();
        public bool Completed { get; private set; }
        public int RenewalsAllowed { get; init; } = int.MaxValue;

        public Task<bool> RenewLeaseAsync(long jobId, string workerId, int leaseAttempt, TimeSpan leaseDuration, CancellationToken ct = default)
        {
            RenewedFor.Add(leaseDuration);
            return Task.FromResult(RenewedFor.Count <= RenewalsAllowed);
        }

        public Task<bool> CompleteAsync(long jobId, string workerId, int leaseAttempt, long? resultLeadId, CancellationToken ct = default)
        {
            Completed = true;
            return Task.FromResult(true);
        }

        public Task<EnqueueResult> EnqueueAsync(EnqueueExtractionRequest request, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<ExtractionJob?> ClaimAsync(string workerId, TimeSpan leaseDuration, int perTenantCap, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<bool> SetStatusAsync(long jobId, string workerId, int leaseAttempt, ExtractionStatus status, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<bool> FailAsync(long jobId, string workerId, int leaseAttempt, string error, CancellationToken ct = default)
            => throw new NotSupportedException();
    }
}
