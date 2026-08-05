using ERP_RFQ_Automation.SupplierQuotes;

namespace ERP_RFQ_Automation.Tests;

public sealed class Release02SupplierQuoteInboxServiceTests
{
    [Fact]
    public async Task Manual_capture_is_ready_and_generates_critical_field_evidence()
    {
        var store = new FakeStore(7);
        var result = await new SupplierQuoteInboxService(store).CaptureAsync(Command());

        Assert.Equal(SupplierQuoteInboxStatuses.ReadyForComparison, result.InboxStatus);
        Assert.Equal(0, result.ReviewRequiredCount);
        var revision = Assert.Single(store.Quotes.Single().Revisions);
        Assert.Contains(revision.Evidence, x => x.FieldName == "CurrencyId" && x.Confidence == 1m);
        Assert.Contains(revision.Evidence, x => x.FieldName == "UnitPrice" && x.Method == "MANUAL_ENTRY");
        Assert.All(revision.Evidence, x => Assert.False(x.ReviewRequired));
    }

    [Fact]
    public async Task Extracted_critical_low_confidence_value_enters_review()
    {
        var store = new FakeStore(7);
        var command = Command() with
        {
            CaptureChannel = SupplierQuoteCaptureChannels.Upload,
            Evidence = [Evidence("CurrencyId", .99m), Evidence("ValidUntil", .99m)],
            Lines = [Line([
                Evidence("UnitPrice", .62m), Evidence("Quantity", .99m),
                Evidence("AvailableQuantity", .99m), Evidence("LeadTimeDays", .99m)])]
        };

        var result = await new SupplierQuoteInboxService(store).CaptureAsync(command);

        Assert.Equal(SupplierQuoteInboxStatuses.ReviewRequired, result.InboxStatus);
        Assert.Equal(1, result.ReviewRequiredCount);
        Assert.Contains(store.Quotes.Single().Revisions.Single().Evidence,
            x => x.FieldName == "UnitPrice" && x.Critical && x.ReviewRequired);
    }

    [Fact]
    public async Task Extracted_capture_with_missing_critical_evidence_fails_into_review()
    {
        var store = new FakeStore(7);
        var result = await new SupplierQuoteInboxService(store).CaptureAsync(Command() with
        {
            CaptureChannel = SupplierQuoteCaptureChannels.Email,
            Evidence = [],
            Lines = [Line()]
        });

        Assert.Equal(SupplierQuoteInboxStatuses.ReviewRequired, result.InboxStatus);
        Assert.Contains(store.Quotes.Single().Revisions.Single().Evidence,
            x => x.FieldName == "CurrencyId" && x.Method == "MISSING_EVIDENCE" && x.ReviewRequired);
    }

    [Fact]
    public async Task Tenant_context_cannot_be_overridden_by_command()
    {
        var service = new SupplierQuoteInboxService(new FakeStore(8));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.CaptureAsync(Command()));
    }

    [Fact]
    public async Task Nexora_serial_must_match_resolved_sourcing_case()
    {
        var service = new SupplierQuoteInboxService(new FakeStore(7));
        var exception = await Assert.ThrowsAsync<SupplierQuoteValidationException>(() =>
            service.CaptureAsync(Command() with { NexoraSerial = "NX-WRONG" }));
        Assert.Contains("Nexora Serial", exception.Message);
    }

    [Fact]
    public async Task Demand_line_must_match_same_tenant_rfq_item()
    {
        var service = new SupplierQuoteInboxService(new FakeStore(7));
        var exception = await Assert.ThrowsAsync<SupplierQuoteValidationException>(() =>
            service.CaptureAsync(Command() with { Lines = [Line() with { CommercialDemandLineId = 999 }] }));
        Assert.Contains("does not match a demand line", exception.Message);
    }

    [Fact]
    public async Task Oversized_quote_graph_is_rejected_before_revision_processing()
    {
        var lines = Enumerable.Range(1, SupplierQuoteInboxService.MaxLinesPerQuote + 1)
            .Select(index => Line() with { LineNumber = index }).ToArray();

        var exception = await Assert.ThrowsAsync<SupplierQuoteValidationException>(() =>
            new SupplierQuoteInboxService(new FakeStore(7)).CaptureAsync(Command() with { Lines = lines }));

        Assert.Contains(SupplierQuoteInboxService.MaxLinesPerQuote.ToString(), exception.Message);
    }

    [Fact]
    public async Task Same_idempotency_key_replays_but_changed_payload_conflicts()
    {
        var service = new SupplierQuoteInboxService(new FakeStore(7));
        var first = await service.CaptureAsync(Command());
        var replay = await service.CaptureAsync(Command());
        Assert.Equal(first.RevisionId, replay.RevisionId);
        Assert.True(replay.Replayed);

        await Assert.ThrowsAsync<SupplierQuoteConflictException>(() => service.CaptureAsync(
            Command() with { Notes = "Different response with reused key" }));
    }

    [Fact]
    public async Task New_revision_is_appended_and_prior_revision_is_preserved()
    {
        var store = new FakeStore(7);
        var service = new SupplierQuoteInboxService(store);
        await service.CaptureAsync(Command());
        await service.CaptureAsync(Command() with
        {
            RevisionNumber = 2,
            IdempotencyKey = "quote-capture-2",
            SourceIdentity = "manual-entry-2",
            SourceSha256 = new string('B', 64),
            Lines = [Line() with { UnitPrice = 9.75m }]
        });

        var quote = Assert.Single(store.Quotes);
        Assert.Equal(2, quote.CurrentRevisionNumber);
        Assert.Equal([1, 2], quote.Revisions.OrderBy(x => x.RevisionNumber).Select(x => x.RevisionNumber));
        Assert.Equal(12.50m, quote.Revisions.Single(x => x.RevisionNumber == 1).Lines.Single().UnitPrice);
        Assert.Equal(9.75m, quote.Revisions.Single(x => x.RevisionNumber == 2).Lines.Single().UnitPrice);
    }

    [Fact]
    public async Task Revision_number_cannot_skip_or_overwrite_history()
    {
        var store = new FakeStore(7);
        var service = new SupplierQuoteInboxService(store);
        await service.CaptureAsync(Command());

        var exception = await Assert.ThrowsAsync<SupplierQuoteConflictException>(() => service.CaptureAsync(
            Command() with { RevisionNumber = 3, IdempotencyKey = "quote-capture-3" }));
        Assert.Contains("Revision 2 is required", exception.Message);
        Assert.Single(store.Quotes.Single().Revisions);
    }

    private static CaptureSupplierQuoteCommand Command() => new(
        7, 31, 41, 51, "NX-2026-0001", "SUP-Q-100", 1,
        SupplierQuoteCaptureChannels.Manual, null, "manual-entry-1", new string('A', 64),
        61, new DateTime(2026, 8, 15, 0, 0, 0, DateTimeKind.Utc), "FCA", 20, 5, "NET 30", null,
        [Line()], [], "quote-capture-1", "buyer@example.com", "corr-1");

    private static CaptureSupplierQuoteLine Line(
        IReadOnlyCollection<CaptureSupplierQuoteEvidence>? evidence = null) => new(
        1, 71, 81, "PN-100", "Maker", "SP-100", "Industrial component", 10, 8,
        "EA", 12.50m, 5, 4, "PARTIAL", "US", "12 months", false, null, evidence ?? []);

    private static CaptureSupplierQuoteEvidence Evidence(string field, decimal confidence) =>
        new(field, "raw", "normalized", confidence, "LOCAL_RULE", "rules-1", 1, "A1", true);

    private sealed class FakeStore(long? tenantId) : ISupplierQuoteStore
    {
        private long _quoteId = 100;
        private long _revisionId = 200;
        private long _lineId = 300;
        private long _evidenceId = 400;
        public long? ScopedTenantId { get; } = tenantId;
        public List<SupplierQuote> Quotes { get; } = [];

        public Task<SupplierQuoteAnchor?> ResolveAnchorAsync(long businessUnitId, long supplierId,
            long solicitationId, long sourcingCaseId, CancellationToken cancellationToken) =>
            Task.FromResult<SupplierQuoteAnchor?>(businessUnitId == 7 && supplierId == 31 &&
                solicitationId == 41 && sourcingCaseId == 51
                ? new SupplierQuoteAnchor(31, 41, 51, 91, "NX-2026-0001",
                    new Dictionary<long, long> { [71] = 81 })
                : null);

        public Task<SupplierQuoteRevision?> FindRevisionByIdempotencyKeyAsync(long businessUnitId,
            string idempotencyKey, CancellationToken cancellationToken) => Task.FromResult(
            Quotes.Where(x => x.BusinessUnitId == businessUnitId).SelectMany(x => x.Revisions)
                .SingleOrDefault(x => x.IdempotencyKey == idempotencyKey));

        public Task<SupplierQuote?> FindCanonicalAsync(long businessUnitId, long supplierId,
            string supplierQuoteReference, CancellationToken cancellationToken) => Task.FromResult(
            Quotes.SingleOrDefault(x => x.BusinessUnitId == businessUnitId && x.SupplierId == supplierId &&
                x.SupplierQuoteReference == supplierQuoteReference));

        public Task<SupplierQuoteCaptureResult> PersistRevisionAsync(SupplierQuote quote,
            SupplierQuoteRevision revision, bool isNewQuote, CancellationToken cancellationToken)
        {
            if (isNewQuote)
            {
                quote.Id = ++_quoteId;
                Quotes.Add(quote);
            }
            revision.Id = ++_revisionId;
            revision.SupplierQuoteId = quote.Id;
            revision.SupplierQuote = quote;
            foreach (var line in revision.Lines)
            {
                line.Id = ++_lineId;
                line.SupplierQuoteRevisionId = revision.Id;
                foreach (var evidence in line.Evidence)
                {
                    evidence.Id = ++_evidenceId;
                    evidence.SupplierQuoteRevisionId = revision.Id;
                    evidence.SupplierQuoteLineId = line.Id;
                }
            }
            foreach (var evidence in revision.Evidence.Where(x => x.Id == 0))
            {
                evidence.Id = ++_evidenceId;
                evidence.SupplierQuoteRevisionId = revision.Id;
            }
            quote.Revisions.Add(revision);
            return Task.FromResult(new SupplierQuoteCaptureResult(quote.Id, revision.Id,
                revision.RevisionNumber, quote.InboxStatus,
                revision.Evidence.Count(x => x.ReviewRequired), false));
        }

        public Task<IReadOnlyCollection<SupplierQuoteInboxRow>> SearchInboxAsync(long businessUnitId,
            string? status, int limit, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyCollection<SupplierQuoteInboxRow>>([]);
        public Task<SupplierQuoteDetail?> GetDetailAsync(long businessUnitId, long supplierQuoteId,
            CancellationToken cancellationToken) => Task.FromResult<SupplierQuoteDetail?>(null);
        public Task ReviewAsync(ReviewSupplierQuoteFieldCommand command,
            CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
