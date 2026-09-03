using ERP_RFQ_Automation.Extraction;
using ERP_RFQ_Automation.Ingestion.Assembly;
using ERP_RFQ_Automation.Ingestion.Triage;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Tests.Support;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// A queue nobody can see is the same as a silent loss.
///
/// <para><b>What this filter is for.</b> Every other filter on the inbound-mail surface selects a
/// triage OUTCOME — what the arrival gate decided. A message stops long after triage, so no
/// combination of those tabs can answer "what is waiting on somebody". Measured on mailbox 9 /
/// business unit 7 on 2026-09-03: 80 of 332 ingested messages had produced no Lead and had
/// nothing left that would move them, scattered across the Inquiry, Uncertain, Noise and legacy
/// tabs. The screen's default landing tab was "Rejected as noise" — the one population that needs
/// nobody at all.</para>
///
/// <para><b>The five shapes below are the live census</b>, and the filter has to separate them by
/// what is TRUE of the message rather than by which subsystem stopped it:</para>
/// <code>
/// stopped, held for a person   57  assembly NeedsReview, AssembledLeadId null
///                                  (assembly_no_requestable_content 37, lead_not_produced 8, ...)
/// stopped, dead-lettered alone 20  no assembly row, ParseStatus 'Failed - extraction dead-lettered'
/// ended, correctly rejected    83  TriageOutcome Noise
/// finished, has a Lead        ...  Leads.EmailIngestsID, or assembly AssembledLeadId (amendments
///                                  and exact resends attach to the EXISTING lead — counting only
///                                  the first link overstates loss badly)
/// </code>
/// </summary>
public sealed class EmailTriageStoppedMessagesAreVisibleTests : IDisposable
{
    private readonly TestDb _db = new();
    public void Dispose() => _db.Dispose();

    private const long Bu = 5301;
    private const long ConfigId = 5401;

    [Fact]
    public async Task The_stopped_filter_gathers_only_what_is_waiting_on_a_person()
    {
        await SeedCensusAsync();
        await using var context = _db.ContextFor(Bu);
        var service = NewService(context);

        var stopped = await service.ListAsync(Bu, outcome: null, page: 1, pageSize: 50,
            ct: default, state: EmailTriageStates.Stopped);

        Assert.Equal(3, stopped.TotalCount);
        Assert.Equal(
            new[] { 8103L, 8104L, 8106L }.OrderBy(x => x),
            stopped.Items.Select(x => x.Id).OrderBy(x => x));

        // Unfiltered still shows everything: the filter narrows, it does not become the surface.
        var everything = await service.ListAsync(Bu, outcome: null, page: 1, pageSize: 50);
        Assert.Equal(6, everything.TotalCount);
    }

    [Fact]
    public async Task A_message_that_reached_a_lead_only_through_the_assembly_is_not_stopped()
    {
        // THE MISCOUNT THIS GUARDS. An amendment, an exact resend and a confident duplicate all
        // attach to the EXISTING Lead rather than minting a second one, so the message carries
        // AssembledLeadId and no row in Leads.EmailIngestsID of its own. Reading only the first
        // link reports every one of them as lost work and buries the genuinely stuck messages
        // under a queue of things that are finished.
        await SeedCensusAsync();
        await using var context = _db.ContextFor(Bu);
        var service = NewService(context);

        var stopped = await service.ListAsync(Bu, outcome: null, page: 1, pageSize: 50,
            ct: default, state: EmailTriageStates.Stopped);

        Assert.DoesNotContain(8105L, stopped.Items.Select(x => x.Id));
    }

    [Fact]
    public async Task Only_a_message_with_no_aggregate_that_gave_up_is_flagged_stopped_in_processing()
    {
        // The flag exists so the screen can route the twenty dead-lettered messages that have NO
        // assembly to the exceptions surface. `describeAssemblyState(null).needsHuman` is false
        // for them — honestly, nothing was reported — so they fell out of every needs-a-person
        // branch and their row offered no way out at all.
        //
        // It must NOT fire where an assembly exists. The per-ingest checkpoint and the assembly
        // disagree on live data and the checkpoint reads BACKWARDS, so a message whose assembly
        // has spoken must never be described by its ParseStatus.
        await SeedCensusAsync();
        await using var context = _db.ContextFor(Bu);
        var service = NewService(context);

        var page = await service.ListAsync(Bu, outcome: null, page: 1, pageSize: 50);
        var byId = page.Items.ToDictionary(x => x.Id);

        Assert.True(byId[8104].StoppedInProcessing);            // dead-lettered, no assembly
        Assert.False(byId[8103].StoppedInProcessing);           // held, assembly speaks for it
        Assert.False(byId[8101].StoppedInProcessing);           // rejected as noise: a decision
        Assert.False(byId[8102].StoppedInProcessing);           // finished
        Assert.False(byId[8106].StoppedInProcessing);           // held with a lead-not-produced hold
    }

    [Fact]
    public async Task An_unrecognised_state_shows_everything_rather_than_nothing()
    {
        // Fail OPEN. A typo in a query string must not render as "nothing is stuck", which is the
        // one wrong answer this screen can give.
        await SeedCensusAsync();
        await using var context = _db.ContextFor(Bu);
        var service = NewService(context);

        var page = await service.ListAsync(Bu, outcome: null, page: 1, pageSize: 50,
            ct: default, state: "stoped");
        Assert.Equal(6, page.TotalCount);
    }

    // ------------------------------------------------------------------ fixture

    /// <summary>Six messages, one per shape the live tenant actually contains.</summary>
    private async Task SeedCensusAsync()
    {
        await using var context = _db.ContextFor(null);
        Seed.EnsureBusinessUnit(context, Bu);
        Seed.EmailConfig(context, ConfigId, Bu);

        // 8101 — ended: correctly rejected as noise. Not stopped; it needs nobody.
        Ingest(context, 8101, "Noise", "Rejected", "Automatic reply: out of office");

        // 8102 — finished: became a Lead of its own.
        var withLead = Ingest(context, 8102, "Inquiry", "Success", "RFQ 4711 cable tray");

        // 8103 — STOPPED: held for a person, the largest live bucket.
        Ingest(context, 8103, "Uncertain", "NeedsReview", "Fwd: pricing?");

        // 8104 — STOPPED: dead-lettered with no message aggregate at all. Twenty of these on the
        // live tenant, every one from 2026-08-13/14, none with an assembly row.
        Ingest(context, 8104, null, "Failed - extraction dead-lettered", "Fwd: RFQ against PR# 111");

        // 8105 — finished through the assembly only: an exact resend that attached to 8102's Lead.
        Ingest(context, 8105, "Inquiry", "Success", "Exact attachment replay | CSV");

        // 8106 — STOPPED: the sharpest shape. Success, Inquiry, every component terminal, held
        // with assembly_lead_not_produced and no lead by either link.
        Ingest(context, 8106, "Inquiry", "Success", "Unnumbered inquiry B quantity changed");
        await context.SaveChangesAsync();

        var lead = new Lead
        {
            Rfqno = "RFQ-4711", RecDate = DateTime.UtcNow, LeadSource = "Email",
            CreatedBy = "test", CreatedDate = DateTime.UtcNow, BusinessUnitId = Bu,
            EmailIngestsId = withLead.Id
        };
        context.Add(lead);
        await context.SaveChangesAsync();

        // 8102 also carries its assembly, Assembled against that lead.
        Assembly(context, 8102, EmailInquiryAssemblyStatus.Assembled, null, lead.Id);
        Assembly(context, 8103, EmailInquiryAssemblyStatus.NeedsReview,
            $"{EmailInquiryHoldReasons.NoRequestableContent}: {EmailInquiryHoldReasons.NoRequestableContentDetail}",
            null);
        Assembly(context, 8105, EmailInquiryAssemblyStatus.Assembled, null, lead.Id);
        Assembly(context, 8106, EmailInquiryAssemblyStatus.NeedsReview,
            $"{EmailInquiryHoldReasons.LeadNotProduced}: {EmailInquiryHoldReasons.LeadNotProducedDetail}",
            null);
        // 8104 deliberately gets NO assembly row — that is what the dead-lettered population
        // looks like in production, and a fixture that gave it one would exercise a shape the
        // product never emits.
        await context.SaveChangesAsync();
    }

    private static EmailIngest Ingest(
        ErpRfqAutomationContext context, long id, string? outcome, string parseStatus, string subject)
    {
        var ingest = Seed.EmailIngest(context, id, ConfigId, parseStatus);
        ingest.EmailSubject = subject;
        ingest.TriageOutcome = outcome;
        ingest.TriageDecidedOn = new DateTime(2026, 8, 29, 2, 0, 0, DateTimeKind.Utc);
        return ingest;
    }

    private static void Assembly(
        ErpRfqAutomationContext context, long ingestId,
        EmailInquiryAssemblyStatus status, string? reason, long? assembledLeadId)
        => context.Add(new EmailInquiryAssembly
        {
            BusinessUnitId = Bu,
            EmailIngestId = ingestId,
            EmailConfigurationId = ConfigId,
            MessageKey = $"msg-{ingestId}",
            ManifestContractVersion = EmailInquiryManifestPlanner.ContractVersion,
            ExpectedComponentCount = 1,
            CompletedComponentCount = 1,
            Status = status,
            StatusReason = reason,
            AssembledLeadId = assembledLeadId,
            CreatedAtUtc = DateTimeOffset.UtcNow,
            UpdatedAtUtc = DateTimeOffset.UtcNow
        });

    private static EmailTriageService NewService(ErpRfqAutomationContext context)
        => new(context, new NoIntake(), new NoRawEmail(), new NoopLogger<EmailTriageService>());

    private sealed class NoIntake : IEmailInquiryIntakeService
    {
        public Task<EmailInquiryIntakeResult> CaptureAndScheduleAsync(
            MimeKit.MimeMessage message, EmailIngest ingest, EmailConfiguration configuration,
            string? freshBodyText, EmailTriageDecision triage, string? clientEmail,
            CancellationToken ct = default)
            => throw new NotSupportedException("The list surface must never capture or schedule.");

        public Task<EmailInquiryResumeResult> ResumeSchedulingAsync(
            long businessUnitId, long assemblyId, CancellationToken ct = default,
            EmailInquirySchedulingGrant? grant = null)
            => throw new NotSupportedException("The list surface must never resume scheduling.");
    }

    private sealed class NoRawEmail : IRawEmailEvidenceReader
    {
        public Task<MimeKit.MimeMessage?> TryLoadAsync(
            long businessUnitId, EmailIngest ingest, CancellationToken ct = default)
            => Task.FromResult<MimeKit.MimeMessage?>(null);
    }
}
