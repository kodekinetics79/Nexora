using ERP_RFQ_Automation.Ingestion.Assembly;
using ERP_RFQ_Automation.Ingestion.Triage;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Tests.Support;
using Microsoft.EntityFrameworkCore;
using MimeKit;

namespace ERP_RFQ_Automation.Tests;

[Collection(PostgreSqlIntegrationCollection.Name)]
public sealed class EmailTriageGovernancePostgreSqlTests(PostgreSqlTestDatabase database)
{
    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task Concurrent_identical_reprocess_commands_append_one_audit_and_one_reopen()
    {
        // FIXED, not drawn from a 499-wide random range.
        //
        // The tenant id has nothing to do with what this test proves — two identical reprocess
        // commands must append one audit row and perform one reopen — so randomising it buys
        // nothing and costs determinism: a collision with another class in this shared database
        // would show up as a one-in-five-hundred failure that never reproduces, which is the most
        // expensive kind of test there is. 98_801 is exclusive to this test; the neighbouring
        // 99_001 in ManualUploadControllerTrustTests is on SQLite and never reaches this database.
        const long tenant = 98_801L;
        var ingestId = await SeedNoiseAsync(tenant);
        const string key = "concurrent-noise-reopen";

        async Task InvokeAsync()
        {
            await using var db = database.TenantContextWithRls(tenant);
            var service = new EmailTriageService(
                db, new SuccessfulIntake(), new FixedRawReader(Message()),
                new NoopLogger<EmailTriageService>());
            await service.ReprocessAsync(
                tenant, ingestId, "9001", "Buyer confirmed this is an RFQ.", key);
        }

        await Task.WhenAll(InvokeAsync(), InvokeAsync());

        await using var verify = database.TenantContextWithRls(tenant);
        Assert.Single(await verify.IamAuditEvents.AsNoTracking().Where(x =>
            x.BusinessUnitId == tenant && x.Action == "EmailTriageReprocessed"
            && x.CorrelationId == key).ToListAsync());
        var assembly = await verify.EmailInquiryAssemblies.AsNoTracking()
            .Include(x => x.Components).SingleAsync(x => x.EmailIngestId == ingestId);
        Assert.Equal(EmailInquiryAssemblyStatus.Captured, assembly.Status);
        Assert.All(assembly.Components, component =>
            Assert.Equal(EmailInquiryComponentStatus.Pending, component.Status));
        Assert.Equal("Queued", await verify.EmailIngests.Where(x => x.Id == ingestId)
            .Select(x => x.ParseStatus).SingleAsync());

        // This collection shares one migrated database. The fake intake intentionally proves only
        // governance and leaves no real jobs, so return its rows to a terminal fixture state before
        // the platform-wide stranded-work sweep runs in another test.
        await verify.EmailInquiryComponents.Where(x => x.AssemblyId == assembly.Id)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(x => x.Status, EmailInquiryComponentStatus.Ignored)
                .SetProperty(x => x.ReasonCode, "test_fixture_terminal"));
        await verify.EmailInquiryAssemblies.Where(x => x.Id == assembly.Id)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(x => x.Status, EmailInquiryAssemblyStatus.NoInquiry));
    }

    private async Task<long> SeedNoiseAsync(long tenant)
    {
        await using var owner = database.ContextFor(null);
        Seed.EnsureBusinessUnit(owner, tenant);
        var config = Seed.EmailConfig(owner, tenant, tenant);
        var ingest = Seed.EmailIngest(owner, tenant, config.Id, "Rejected");
        ingest.TriageOutcome = EmailTriageOutcome.Noise.ToString();
        await owner.SaveChangesAsync();

        var now = DateTimeOffset.UtcNow;
        var assembly = new EmailInquiryAssembly
        {
            BusinessUnitId = tenant,
            EmailIngestId = ingest.Id,
            EmailConfigurationId = config.Id,
            MessageKey = ingest.MessageId,
            RawEvidenceUri = "evidence://raw/noise.eml",
            RawEvidenceSha256 = new string('a', 64),
            ManifestContractVersion = EmailInquiryManifestPlanner.ContractVersion,
            ExpectedComponentCount = 1,
            Status = EmailInquiryAssemblyStatus.NoInquiry,
            StatusReason = "triage_noise: auto_submitted_header",
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        };
        assembly.Components.Add(new EmailInquiryComponent
        {
            BusinessUnitId = tenant,
            ComponentKey = $"email:{ingest.MessageId}:body",
            Kind = EmailInquiryComponentKind.Body,
            Ordinal = 0,
            FileName = "body.txt",
            MimeType = "text/plain",
            ContentHash = new string('b', 64),
            Status = EmailInquiryComponentStatus.Ignored,
            ReasonCode = "no_inquiry",
            ReasonDetail = "triage_noise: auto_submitted_header",
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        });
        owner.Add(assembly);
        await owner.SaveChangesAsync();
        return ingest.Id;
    }

    private static MimeMessage Message()
    {
        var message = new MimeMessage
        {
            MessageId = $"governed-{Guid.NewGuid():N}@buyer.example",
            Subject = "Automated-looking RFQ",
            Body = new TextPart("plain") { Text = "Please quote 5 pressure gauges." }
        };
        message.From.Add(MailboxAddress.Parse("buyer@customer.example"));
        message.To.Add(MailboxAddress.Parse("sales@nexora.example"));
        return message;
    }

    private sealed class FixedRawReader(MimeMessage message) : IRawEmailEvidenceReader
    {
        public Task<MimeMessage?> TryLoadAsync(
            long businessUnitId, EmailIngest ingest, CancellationToken ct = default)
            => Task.FromResult<MimeMessage?>(message);
    }

    private sealed class SuccessfulIntake : IEmailInquiryIntakeService
    {
        public Task<EmailInquiryIntakeResult> CaptureAndScheduleAsync(
            MimeMessage message, EmailIngest ingest, EmailConfiguration configuration,
            string? freshBodyText, EmailTriageDecision triage, string? clientEmail,
            CancellationToken ct = default)
            => Task.FromResult(new EmailInquiryIntakeResult(
                1, Guid.NewGuid(), Scheduled: 1, AlreadyScheduled: 0, Held: 0,
                ExpectedComponents: 1, AlreadyCaptured: true, SafeToAcknowledge: true,
                FailureReason: null));
        /// <summary>
        /// Not this stub's subject. The resume path is proved against the real intake service on
        /// PostgreSQL; a stand-in here would only assert its own return value.
        /// </summary>
        public Task<ERP_RFQ_Automation.Ingestion.Assembly.EmailInquiryResumeResult> ResumeSchedulingAsync(
            long businessUnitId, long assemblyId, CancellationToken ct = default,
            ERP_RFQ_Automation.Ingestion.Assembly.EmailInquirySchedulingGrant? grant = null)
            => Task.FromResult(new ERP_RFQ_Automation.Ingestion.Assembly.EmailInquiryResumeResult(
                ERP_RFQ_Automation.Ingestion.Assembly.EmailInquiryResumeOutcome.NothingToResume, 0, 0));
    }
}
