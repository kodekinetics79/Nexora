using System.Diagnostics;
using System.Text;
using System.Text.Json;
using ERP_RFQ_Automation.Extraction;
using ERP_RFQ_Automation.Infrastructure.Storage;
using ERP_RFQ_Automation.Ingestion.Assembly;
using ERP_RFQ_Automation.LeadIdentity;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.MultiTenancy;
using ERP_RFQ_Automation.Services;
using ERP_RFQ_Automation.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using MimeKit;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// Opt-in, production-dialect load certification for the real IMAP -> durable occurrence ->
/// evidence -> extraction -> canonical Lead pipeline. It is intentionally excluded from the
/// ordinary commit suite: a 1,020-message lane belongs in release certification, not in every
/// developer edit. Run with NEXORA_RUN_EMAIL_LOAD=1 and optionally set
/// NEXORA_EMAIL_LOAD_REPORT_PATH to retain the JSON evidence report.
/// </summary>
public sealed partial class AcceptanceJourneyTests
{
    private const int LoadBaseInquiryCount = 200;
    private const int LoadRepeatCount = 200;
    private const int LoadAmendmentCount = 200;
    private const int LoadMultiAttachmentCount = 200;
    private const int LoadNoiseCount = 100;
    private const int LoadUnsupportedCount = 50;
    private const int LoadMalformedCount = 30;
    private const int LoadTransportDuplicateCount = 40;
    private const int LoadTotal = LoadBaseInquiryCount + LoadRepeatCount + LoadAmendmentCount
        + LoadMultiAttachmentCount + LoadNoiseCount + LoadUnsupportedCount + LoadMalformedCount
        + LoadTransportDuplicateCount;
    private const int LoadUniqueMessageIds = LoadTotal - LoadTransportDuplicateCount;

    [EmailLoadFact]
    [Trait("Category", "EmailLoad")]
    public async Task One_thousand_mixed_messages_converge_without_loss_duplicate_leads_or_direct_rfqs()
    {
        Assert.Equal(1_020, LoadTotal);
        await using var imap = new FakeImapServer(CorpusGenerator.IntakeAddress);
        var messages = BuildLoadCorpus();
        Assert.Equal(LoadTotal, messages.Count);
        foreach (var bytes in messages) imap.AddMessage(bytes);
        await SeedMailboxAsync(imap);

        var totalClock = Stopwatch.StartNew();
        var pollClock = Stopwatch.StartNew();
        var poll = await PollLoadAsync();
        pollClock.Stop();

        Assert.True(poll.AllSucceeded, poll.FailureSummary);
        Assert.Equal(LoadTotal, poll.MessagesFound);
        Assert.Equal(LoadTotal, poll.MessagesDownloaded);
        Assert.Equal(0, poll.MessagesNotAcknowledged);
        Assert.Equal(0, poll.MessagesDeferred);

        var workerClock = Stopwatch.StartNew();
        var worker = StartLoadWorker();
        try
        {
            await WaitForLoadSettlementAsync(TimeSpan.FromMinutes(25));
        }
        finally
        {
            await StopWorkerAsync(worker);
        }
        workerClock.Stop();
        totalClock.Stop();

        await using var ctx = ContextFor(null);
        var ingests = await ctx.EmailIngests.AsNoTracking().ToListAsync();
        var assemblies = await ctx.Set<EmailInquiryAssembly>().AsNoTracking().ToListAsync();
        var jobs = await ctx.Set<ExtractionJob>().AsNoTracking().ToListAsync();
        var leads = await ctx.Leads.AsNoTracking().Where(x => x.BusinessUnitId == Tenant).ToListAsync();
        var revisions = await ctx.Set<LeadRevision>().AsNoTracking()
            .Where(x => x.BusinessUnitId == Tenant).ToListAsync();

        var activeJobCount = jobs.Count(x => x.Status is ExtractionStatus.Pending
            or ExtractionStatus.Leased or ExtractionStatus.Extracting or ExtractionStatus.Persisting);
        var unsettledAssemblyCount = assemblies.Count(x => x.Status is EmailInquiryAssemblyStatus.Captured
            or EmailInquiryAssemblyStatus.Inspecting or EmailInquiryAssemblyStatus.Extracting
            or EmailInquiryAssemblyStatus.ReadyForAssembly or EmailInquiryAssemblyStatus.FailedRecoverable);
        var missingRawEvidence = ingests.Count(x => string.IsNullOrWhiteSpace(x.RawEmailPath)
            || !File.Exists(x.RawEmailPath));
        var invalidAssembled = assemblies.Count(x => x.Status == EmailInquiryAssemblyStatus.Assembled
            && x.AssembledLeadId is not > 0);

        var report = new
        {
            generatedAtUtc = DateTime.UtcNow,
            seed = 20260828,
            scenario = new
            {
                total = LoadTotal,
                baseInquiries = LoadBaseInquiryCount,
                logicalRepeats = LoadRepeatCount,
                quantityAmendments = LoadAmendmentCount,
                multiAttachmentInquiries = LoadMultiAttachmentCount,
                noise = LoadNoiseCount,
                unsupportedAttachments = LoadUnsupportedCount,
                malformedCsv = LoadMalformedCount,
                byteIdenticalTransportDuplicates = LoadTransportDuplicateCount
            },
            timing = new
            {
                pollSeconds = pollClock.Elapsed.TotalSeconds,
                workerSeconds = workerClock.Elapsed.TotalSeconds,
                totalSeconds = totalClock.Elapsed.TotalSeconds,
                messagesPerSecond = LoadTotal / Math.Max(totalClock.Elapsed.TotalSeconds, 0.001)
            },
            poll = new
            {
                poll.MessagesFound,
                poll.MessagesDownloaded,
                poll.MessagesCaptured,
                poll.MessagesAlreadyIngested,
                poll.ComponentsScheduled,
                poll.MessagesHeldForReview,
                poll.MessagesRejected,
                poll.MessagesNotAcknowledged,
                poll.MessagesDeferred
            },
            durable = new
            {
                emailIngests = ingests.Count,
                assemblies = assemblies.Count,
                extractionJobs = jobs.Count,
                extractionJobStates = jobs.GroupBy(x => x.Status.ToString())
                    .ToDictionary(x => x.Key, x => x.Count()),
                assemblyStates = assemblies.GroupBy(x => x.Status.ToString())
                    .ToDictionary(x => x.Key, x => x.Count()),
                leads = leads.Count,
                revisions = revisions.Count,
                rfqs = await ctx.Rfqs.AsNoTracking().CountAsync(x => x.BusinessUnitId == Tenant),
                activeJobCount,
                unsettledAssemblyCount,
                missingRawEvidence,
                invalidAssembled,
                evidenceBytes = Directory.Exists(_root)
                    ? Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories)
                        .Sum(path => new FileInfo(path).Length)
                    : 0
            }
        };

        var reportPath = Environment.GetEnvironmentVariable("NEXORA_EMAIL_LOAD_REPORT_PATH");
        if (!string.IsNullOrWhiteSpace(reportPath))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(reportPath))!);
            await File.WriteAllTextAsync(reportPath,
                JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        }
        Console.WriteLine(JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));

        Assert.Equal(LoadUniqueMessageIds, ingests.Count);
        Assert.Equal(LoadUniqueMessageIds, assemblies.Count);
        Assert.Equal(0, missingRawEvidence);
        Assert.Equal(0, activeJobCount);
        Assert.Equal(0, unsettledAssemblyCount);
        Assert.Equal(0, invalidAssembled);
        Assert.Equal(0, await ctx.Rfqs.AsNoTracking().CountAsync(x => x.BusinessUnitId == Tenant));

        // 200 base inquiries + 200 independent multi-attachment inquiries. The 200 logical
        // repeats are duplicates and the 200 quantity-only amendments become revision 2.
        Assert.Equal(400, leads.Count);
        Assert.Equal(600, revisions.Count);
        Assert.Equal(200, leads.Count(x => x.CurrentRevisionNumber == 2));
        Assert.Equal(200, leads.Count(x => x.CurrentRevisionNumber == 1));

        Assert.Equal(LoadNoiseCount,
            ingests.Count(x => string.Equals(x.ParseStatus, "Rejected", StringComparison.Ordinal)));
        Assert.DoesNotContain(assemblies, x => x.Status == EmailInquiryAssemblyStatus.RejectedSecurity);
    }

    private async Task<MailboxPollReport> PollLoadAsync()
    {
        await using var discovery = ContextFor(null);
        var service = new EmailService(
            context: discovery,
            env: new JourneyEnvironment(_root),
            logger: NullLogger<EmailService>.Instance,
            llmService: _llm,
            scopeFactory: _provider.GetRequiredService<IServiceScopeFactory>(),
            configuration: new ConfigurationBuilder().AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["Ingestion:Email:MaxLookbackDays"] = "36500",
                    ["Ingestion:Email:MinLookbackDays"] = "36500",
                    ["Ingestion:Email:MaxNewMessagesPerMailboxAttempt"] = "2000",
                    ["Ingestion:Email:MailboxAttemptTimeoutSeconds"] = "3600",
                    ["Ingestion:Email:NetworkOperationTimeoutSeconds"] = "300"
                }).Build(),
            storage: _provider.GetRequiredService<IFileStorage>(),
            pollerHealth: null,
            workGate: null,
            tenantScope: _provider.GetRequiredService<ITenantScopeAccessor>());
        return await service.FetchAndSaveLeadsAsync(Tenant);
    }

    private ExtractionWorker StartLoadWorker()
    {
        var worker = new ExtractionWorker(
            _provider.GetRequiredService<IServiceScopeFactory>(),
            new ExtractionWorkerOptions
            {
                WorkerCount = 8,
                MaxConcurrentLlmCalls = 4,
                PerTenantConcurrencyCap = 8,
                LeaseDuration = TimeSpan.FromMinutes(5),
                IdlePollDelay = TimeSpan.FromMilliseconds(50)
            },
            _provider.GetRequiredService<ILogger<ExtractionWorker>>(),
            _provider.GetRequiredService<ITenantScopeAccessor>());
        worker.StartAsync(CancellationToken.None).GetAwaiter().GetResult();
        return worker;
    }

    private async Task WaitForLoadSettlementAsync(TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            await using var ctx = ContextFor(null);
            var activeJobs = await ctx.Set<ExtractionJob>().AsNoTracking().CountAsync(x =>
                x.Status == ExtractionStatus.Pending || x.Status == ExtractionStatus.Leased
                || x.Status == ExtractionStatus.Extracting || x.Status == ExtractionStatus.Persisting);
            var activeAssemblies = await ctx.Set<EmailInquiryAssembly>().AsNoTracking().CountAsync(x =>
                x.Status == EmailInquiryAssemblyStatus.Captured
                || x.Status == EmailInquiryAssemblyStatus.Inspecting
                || x.Status == EmailInquiryAssemblyStatus.Extracting
                || x.Status == EmailInquiryAssemblyStatus.ReadyForAssembly
                || x.Status == EmailInquiryAssemblyStatus.FailedRecoverable);
            if (activeJobs == 0 && activeAssemblies == 0) return;
            await Task.Delay(250);
        }

        await using var failed = ContextFor(null);
        var jobStates = await failed.Set<ExtractionJob>().AsNoTracking()
            .GroupBy(x => x.Status).Select(x => new { x.Key, Count = x.Count() }).ToListAsync();
        var assemblyStates = await failed.Set<EmailInquiryAssembly>().AsNoTracking()
            .GroupBy(x => x.Status).Select(x => new { x.Key, Count = x.Count() }).ToListAsync();
        throw new TimeoutException("The 1,020-message load did not settle. Jobs: "
            + string.Join(", ", jobStates.Select(x => $"{x.Key}={x.Count}"))
            + "; assemblies: "
            + string.Join(", ", assemblyStates.Select(x => $"{x.Key}={x.Count}")));
    }

    private static IReadOnlyList<byte[]> BuildLoadCorpus()
    {
        var result = new List<byte[]>(LoadTotal);
        var bases = new List<byte[]>(LoadBaseInquiryCount);

        for (var i = 0; i < LoadBaseInquiryCount; i++)
        {
            var bytes = Serialize(BuildStructuredLoadMessage(
                $"load-base-{i:D4}@load.nexora.example", $"LOAD-{i:D4}", i, quantityDelta: 0,
                attachmentCount: 2));
            bases.Add(bytes);
            result.Add(bytes);
        }
        for (var i = 0; i < LoadRepeatCount; i++)
            result.Add(Serialize(BuildStructuredLoadMessage(
                $"load-repeat-{i:D4}@load.nexora.example", $"LOAD-{i:D4}", i, quantityDelta: 0,
                attachmentCount: 2)));
        for (var i = 0; i < LoadAmendmentCount; i++)
            result.Add(Serialize(BuildStructuredLoadMessage(
                $"load-amend-{i:D4}@load.nexora.example", $"LOAD-{i:D4}", i, quantityDelta: 7,
                attachmentCount: 2, inReplyTo: $"load-base-{i:D4}@load.nexora.example")));
        for (var i = 0; i < LoadMultiAttachmentCount; i++)
            result.Add(Serialize(BuildStructuredLoadMessage(
                $"load-multi-{i:D4}@load.nexora.example", $"LOAD-MULTI-{i:D4}", 10_000 + i,
                quantityDelta: 0, attachmentCount: 3)));
        for (var i = 0; i < LoadNoiseCount; i++) result.Add(Serialize(BuildNoiseMessage(i)));
        for (var i = 0; i < LoadUnsupportedCount; i++) result.Add(Serialize(BuildUnsupportedMessage(i)));
        for (var i = 0; i < LoadMalformedCount; i++) result.Add(Serialize(BuildMalformedMessage(i)));
        for (var i = 0; i < LoadTransportDuplicateCount; i++) result.Add(bases[i]);

        // Preserve mailbox causality: a reply/amendment cannot arrive before the message its
        // In-Reply-To header names. Workers still process eight independent assemblies at once,
        // and every scenario is present in the same poll, but the transport corpus does not
        // manufacture an impossible chronology and then call the expected review outcome a
        // duplicate defect. Out-of-order recovery is covered separately from this throughput
        // lane because it has a different expected disposition.
        return result;
    }

    private static MimeMessage BuildStructuredLoadMessage(
        string messageId, string reference, int group, int quantityDelta, int attachmentCount,
        string? inReplyTo = null)
    {
        var mixed = new Multipart("mixed");
        for (var attachment = 0; attachment < attachmentCount; attachment++)
        {
            var csv = new StringBuilder("Part Number,Description,Quantity,Unit\n");
            for (var line = 0; line < 3; line++)
            {
                var part = $"P-{group:D5}-{attachment + 1}-{line + 1}";
                var quantity = 10 + group % 17 + attachment * 3 + line + quantityDelta;
                csv.Append(part).Append(',')
                    .Append("Load certified industrial component ").Append(part).Append(',')
                    .Append(quantity).Append(",EA\n");
            }
            mixed.Add(CsvAttachment($"{reference}-schedule-{attachment + 1}.csv", csv.ToString()));
        }

        var message = new MimeMessage
        {
            Subject = $"RFQ {reference} - deterministic load certification",
            Body = mixed,
            MessageId = messageId,
            Date = new DateTimeOffset(2026, 8, 28, 9, group % 60, 0, TimeSpan.Zero)
        };
        message.From.Add(new MailboxAddress($"Load Buyer {group:D4}", $"buyer-{group:D5}@load.customer.example"));
        message.To.Add(new MailboxAddress("Nexora Intake", CorpusGenerator.IntakeAddress));
        if (!string.IsNullOrWhiteSpace(inReplyTo))
        {
            message.InReplyTo = inReplyTo;
            message.References.Add(inReplyTo);
        }
        return message;
    }

    private static MimeMessage BuildNoiseMessage(int index)
    {
        var message = new MimeMessage
        {
            Subject = $"Automatic delivery report {index:D4}",
            Body = new TextPart("plain") { Text = "This is an automated notification. Do not reply." },
            MessageId = $"load-noise-{index:D4}@load.nexora.example",
            Date = new DateTimeOffset(2026, 8, 28, 10, index % 60, 0, TimeSpan.Zero)
        };
        message.From.Add(new MailboxAddress("Automated Sender", $"no-reply-{index:D4}@load.customer.example"));
        message.To.Add(new MailboxAddress("Nexora Intake", CorpusGenerator.IntakeAddress));
        message.Headers.Add("Auto-Submitted", "auto-generated");
        return message;
    }

    private static MimeMessage BuildUnsupportedMessage(int index)
    {
        var mixed = new Multipart("mixed");
        mixed.Add(Attachment($"unsupported-{index:D4}.pptx",
            "application/vnd.openxmlformats-officedocument.presentationml.presentation",
            Encoding.UTF8.GetBytes("not a supported commercial schedule")));
        var message = new MimeMessage
        {
            Subject = $"RFQ LOAD-UNSUPPORTED-{index:D4}",
            Body = mixed,
            MessageId = $"load-unsupported-{index:D4}@load.nexora.example",
            Date = new DateTimeOffset(2026, 8, 28, 11, index % 60, 0, TimeSpan.Zero)
        };
        message.From.Add(new MailboxAddress("Unsupported Buyer", $"unsupported-{index:D4}@load.customer.example"));
        message.To.Add(new MailboxAddress("Nexora Intake", CorpusGenerator.IntakeAddress));
        return message;
    }

    private static MimeMessage BuildMalformedMessage(int index)
    {
        var mixed = new Multipart("mixed");
        mixed.Add(CsvAttachment($"malformed-{index:D4}.csv",
            "Mystery,Columns,Without,Commercial,Meaning\nalpha,beta,gamma,delta,epsilon\n"));
        var message = new MimeMessage
        {
            Subject = $"RFQ LOAD-MALFORMED-{index:D4}",
            Body = mixed,
            MessageId = $"load-malformed-{index:D4}@load.nexora.example",
            Date = new DateTimeOffset(2026, 8, 28, 12, index % 60, 0, TimeSpan.Zero)
        };
        message.From.Add(new MailboxAddress("Malformed Buyer", $"malformed-{index:D4}@load.customer.example"));
        message.To.Add(new MailboxAddress("Nexora Intake", CorpusGenerator.IntakeAddress));
        return message;
    }

    private static MimePart CsvAttachment(string fileName, string content)
        => Attachment(fileName, "text/csv", Encoding.UTF8.GetBytes(content));

    private static MimePart Attachment(string fileName, string mimeType, byte[] content)
    {
        var slash = mimeType.IndexOf('/');
        return new MimePart(mimeType[..slash], mimeType[(slash + 1)..])
        {
            Content = new MimeContent(new MemoryStream(content)),
            ContentDisposition = new ContentDisposition(ContentDisposition.Attachment) { FileName = fileName },
            ContentTransferEncoding = ContentEncoding.Base64,
            FileName = fileName
        };
    }

    private static byte[] Serialize(MimeMessage message)
    {
        using var stream = new MemoryStream();
        message.WriteTo(stream);
        return stream.ToArray();
    }
}

internal sealed class EmailLoadFactAttribute : FactAttribute
{
    public EmailLoadFactAttribute()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("NEXORA_RUN_EMAIL_LOAD"), "1",
                StringComparison.Ordinal))
            Skip = "Set NEXORA_RUN_EMAIL_LOAD=1 to run the 1,020-message release certification lane.";
    }
}
