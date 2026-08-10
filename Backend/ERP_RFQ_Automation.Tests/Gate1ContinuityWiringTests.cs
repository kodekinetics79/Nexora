using ERP_RFQ_Automation.DTOs.Lead;
using ERP_RFQ_Automation.Extraction;
using ERP_RFQ_Automation.ListViews;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Repositories;
using ERP_RFQ_Automation.Services;
using ERP_RFQ_Automation.Tests.Support;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// The Gate 1 continuity audit found four defects that every existing test passed over, because
/// each of them is an INCOMPLETENESS rather than a fault: a value written and never read, a number
/// carried without its unit, a threshold that reports success on noise. See
/// <c>docs/WIRING_CONTRACT.md</c>.
///
/// <para>Every test here asserts a DEPENDENCE, not a round trip. Delete the wiring and the test
/// fails; store the value and go no further and the test still fails. That distinction is the
/// whole reason these defects survived a green suite.</para>
/// </summary>
public sealed class Gate1ContinuityWiringTests
{
    // ── 1 · The buyer's required delivery date reaches a reader ──────────────────────────────

    [Fact]
    public async Task Lead_list_and_detail_surface_the_buyers_required_delivery_date()
    {
        var required = new DateTime(2026, 11, 30, 0, 0, 0, DateTimeKind.Utc);
        var closing = new DateTime(2026, 9, 15, 0, 0, 0, DateTimeKind.Utc);

        using var db = new TestDb();
        await using var context = db.ContextFor(null);
        var lead = Seed.Lead(context, 8100, 810);
        lead.BidClosingDate = closing;
        lead.RequiredDeliveryDate = required;
        lead.BidClosingDateHijri = "1448-03-12";
        lead.AgreementReference = "FRAME-2026-118";
        await context.SaveChangesAsync();

        var repo = new LeadRepository(context);

        var (rows, _) = await repo.GetLeadListAsync(1, 10, null, null, null, null, 810);
        var listRow = Assert.Single(rows);
        Assert.Equal(required, listRow.RequiredDeliveryDate);
        Assert.Equal("1448-03-12", listRow.BidClosingDateHijri);
        Assert.Equal("FRAME-2026-118", listRow.AgreementReference);

        var detail = await repo.GetLeadByIdAsync(8100, 810);
        Assert.NotNull(detail);
        Assert.Equal(required, detail!.RequiredDeliveryDate);
        Assert.Equal("1448-03-12", detail.BidClosingDateHijri);
        Assert.Equal("FRAME-2026-118", detail.AgreementReference);

        // The point of the field: it is NOT the bid deadline, and nothing may collapse the two.
        Assert.NotEqual(detail.BidClosingDate, detail.RequiredDeliveryDate);
    }

    [Fact]
    public async Task A_lead_with_no_stated_required_delivery_date_reports_absence_not_the_deadline()
    {
        // Failure #3 in the wiring contract: a reader that substitutes a neighbouring value hides
        // exactly the rows a gap report exists to surface. "Not stated" must survive to the client.
        using var db = new TestDb();
        await using var context = db.ContextFor(null);
        var lead = Seed.Lead(context, 8101, 811);
        lead.BidClosingDate = new DateTime(2026, 9, 15, 0, 0, 0, DateTimeKind.Utc);
        lead.RequiredDeliveryDate = null;
        await context.SaveChangesAsync();

        var detail = await new LeadRepository(context).GetLeadByIdAsync(8101, 811);

        Assert.NotNull(detail);
        Assert.Null(detail!.RequiredDeliveryDate);
        Assert.NotNull(detail.BidClosingDate);
    }

    [Fact]
    public void The_leads_grid_offers_the_required_delivery_date_beside_the_deadline()
    {
        var leads = ListViewCatalog.Find("leads.list");
        Assert.NotNull(leads);
        var keys = leads!.Columns.Select(column => column.Key).ToList();

        var deadline = keys.IndexOf("bidClosingDate");
        var requiredDelivery = keys.IndexOf("requiredDeliveryDate");
        Assert.True(deadline >= 0, "leads.list lost its deadline column.");
        Assert.True(requiredDelivery >= 0, "leads.list must offer the buyer's required delivery date.");
        Assert.Equal(deadline + 1, requiredDelivery);

        // Default-visible: it was captured and shown to nobody, so hiding it by default would
        // leave the defect in place for every user who never opens the column picker.
        Assert.True(leads.Columns.Single(column => column.Key == "requiredDeliveryDate").DefaultVisible);

        // The other two writers-with-no-readers are selectable rather than default-visible: they
        // are cross-checks a rep reaches for, and no existing grid rearranges itself on deploy.
        Assert.Contains(keys, key => key == "bidClosingDateHijri");
        Assert.Contains(keys, key => key == "agreementReference");
        Assert.False(leads.Columns.Single(column => column.Key == "bidClosingDateHijri").DefaultVisible);
        Assert.False(leads.Columns.Single(column => column.Key == "agreementReference").DefaultVisible);
    }

    [Fact]
    public async Task A_reviewer_can_correct_the_required_delivery_date_without_a_developer()
    {
        // Extraction is the only thing that has ever written this field. A value nobody can
        // correct through the product is a value nobody can trust.
        using var db = new TestDb();
        await using var context = db.ContextFor(null);
        var lead = Seed.Lead(context, 8102, 812, parseStatus: "NeedsReview");
        lead.RequiredDeliveryDate = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);
        await context.SaveChangesAsync();

        var corrected = new DateTime(2026, 12, 20, 0, 0, 0, DateTimeKind.Utc);
        var result = await new LeadRepository(context).SubmitLeadReviewAsync(
            8102, 812,
            new LeadReviewSubmitDTO
            {
                Action = "save",
                ExpectedVersion = Math.Max(1, lead.ReviewVersion),
                Header = new LeadReviewHeaderDTO { RequiredDeliveryDate = corrected }
            },
            "reviewer@nexora.test");

        Assert.NotNull(result);
        Assert.Equal(corrected, result!.RequiredDeliveryDate);
        Assert.Equal(corrected, (await context.Leads.FindAsync(8102L))!.RequiredDeliveryDate);
    }

    // ── 2 · The mailbox poll interval carries its unit ───────────────────────────────────────

    [Fact]
    public async Task A_stored_poll_interval_of_five_means_five_MINUTES_not_five_seconds()
    {
        // The defect: every human-facing surface said minutes — the DTOs, the mailbox screen and
        // the health text — while the poller read the same column with TimeSpan.FromSeconds. An
        // operator entering 5 got a five-second IMAP poll, sixty times the intended rate.
        using var db = new TestDb();
        await using var context = db.ContextFor(null);
        Seed.BusinessUnit(context, 820);
        var mailbox = Seed.EmailConfig(context, 8200, 820);
        mailbox.PollingInterval = 5;
        await context.SaveChangesAsync();

        var interval = await Service().ResolvePollIntervalAsync(context, CancellationToken.None);

        Assert.Equal(TimeSpan.FromMinutes(5), interval);
        Assert.NotEqual(TimeSpan.FromSeconds(5), interval);
    }

    [Fact]
    public async Task The_poll_interval_is_clamped_to_the_range_the_operator_is_offered()
    {
        // The fastest an operator may ask for is one minute and the slowest is 1440, on both the
        // API ([Range(1, 1440)]) and the screen. A value outside that range can only have arrived
        // by direct SQL, and it must not be allowed to poll faster than the product permits.
        using var db = new TestDb();
        await using var context = db.ContextFor(null);
        Seed.BusinessUnit(context, 821);
        var mailbox = Seed.EmailConfig(context, 8201, 821);

        mailbox.PollingInterval = EmailBackgroundService.MinimumPollIntervalMinutes;
        await context.SaveChangesAsync();
        Assert.Equal(
            TimeSpan.FromMinutes(1),
            await Service().ResolvePollIntervalAsync(context, CancellationToken.None));

        mailbox.PollingInterval = 100_000;
        await context.SaveChangesAsync();
        Assert.Equal(
            TimeSpan.FromMinutes(EmailBackgroundService.MaximumPollIntervalMinutes),
            await Service().ResolvePollIntervalAsync(context, CancellationToken.None));

        // Negative is the only way to express "faster than instant" and it must not be honoured.
        mailbox.PollingInterval = -7;
        await context.SaveChangesAsync();
        Assert.Equal(
            TimeSpan.FromMinutes(EmailBackgroundService.MinimumPollIntervalMinutes),
            await Service().ResolvePollIntervalAsync(context, CancellationToken.None));
    }

    [Fact]
    public async Task An_SMTP_mailbox_does_not_set_the_inbound_poll_rate()
    {
        // The DTOs document the field as "Ignored for SMTP" and the screen hides the input for
        // SMTP — but the read took the minimum across EVERY active mailbox, so an outbound-only
        // row was setting the inbound rate for the whole tenant. A field documented as ignored
        // must actually be ignored.
        using var db = new TestDb();
        await using var context = db.ContextFor(null);
        Seed.BusinessUnit(context, 822);
        var imap = Seed.EmailConfig(context, 8202, 822);
        imap.PollingInterval = 30;
        var smtp = Seed.EmailConfig(context, 8203, 822);
        smtp.Protocol = "SMTP";
        smtp.PollingInterval = 1;
        await context.SaveChangesAsync();

        var interval = await Service().ResolvePollIntervalAsync(context, CancellationToken.None);

        Assert.Equal(TimeSpan.FromMinutes(30), interval);
    }

    [Fact]
    public void The_operator_settable_range_and_the_pollers_clamp_are_the_same_range()
    {
        // Failure #12 is a number compared without its unit; this is the same failure's twin —
        // two components agreeing on a range by coincidence. Pin them together so a change to
        // one is a compile-time-visible change to the other.
        var createRange = RangeOf(nameof(ERP_RFQ_Automation.Mailbox.MailboxCreateRequestDTO.PollingInterval),
            typeof(ERP_RFQ_Automation.Mailbox.MailboxCreateRequestDTO));
        var updateRange = RangeOf(nameof(ERP_RFQ_Automation.Mailbox.MailboxUpdateRequestDTO.PollingInterval),
            typeof(ERP_RFQ_Automation.Mailbox.MailboxUpdateRequestDTO));

        foreach (var range in new[] { createRange, updateRange })
        {
            Assert.Equal(EmailBackgroundService.MinimumPollIntervalMinutes, range.Minimum);
            Assert.Equal(EmailBackgroundService.MaximumPollIntervalMinutes, range.Maximum);
        }
    }

    [Fact]
    public void A_worker_is_never_called_dead_faster_than_its_own_configured_period()
    {
        // Consequence of the unit correction, and a defect in its own right if left alone: the
        // poller's documented maximum of 1440 stopped meaning 24 minutes and started meaning
        // 24 hours, four times the flat six-hour staleness cap. A once-a-day mailbox is a legal
        // configuration, and it must not report the poller dead for eighteen hours out of every
        // twenty-four. The bound must never sit below the interval it is judging.
        var heartbeats = new ERP_RFQ_Automation.HealthChecks.BackgroundWorkerHeartbeats();
        var slowest = TimeSpan.FromMinutes(EmailBackgroundService.MaximumPollIntervalMinutes);
        heartbeats.Register("slow-poller", slowest);
        heartbeats.Beat("slow-poller", slowest);

        var status = heartbeats.Snapshot().Single(x => x.Worker == "slow-poller");
        Assert.True(status.DeadlineUtc - status.LastBeatUtc!.Value >= slowest,
            "A worker beating exactly on its own period would be reported dead.");
    }

    // ── 4 · An unreadable scan is reported as unreadable ─────────────────────────────────────

    [Fact]
    public void Twenty_characters_off_a_scanned_page_is_not_a_completed_read()
    {
        // The old bar was a flat 20 non-whitespace characters — roughly three words — applied to
        // the whole document however many pages it had. Only eng.traineddata is installed and
        // every engine is constructed with "eng", so a scanned ARABIC tender comes back as
        // transliterated noise; twenty characters of it reported Completed, which asserts that
        // Nexora read the file. Arabic recognition is Gate 9; until then the honest verdict for
        // an unreadable scan is "unreadable".
        Assert.Equal(
            ProductionDocumentReader.MinimumOcrCharactersPerPage,
            ProductionDocumentReader.OcrTextThreshold(1));
        Assert.True(ProductionDocumentReader.OcrTextThreshold(1) > 20);
    }

    [Fact]
    public void The_OCR_bar_is_a_density_so_a_long_scan_does_not_get_easier_to_pass()
    {
        // A whole-document total gets easier to clear the longer the scan is, which is exactly
        // backwards: more pages should mean more recovered text, not a lower bar per page.
        Assert.Equal(
            ProductionDocumentReader.MinimumOcrCharactersPerPage * 12,
            ProductionDocumentReader.OcrTextThreshold(12));
        Assert.True(
            ProductionDocumentReader.OcrTextThreshold(12) >
            ProductionDocumentReader.OcrTextThreshold(1));
    }

    [Fact]
    public void The_OCR_bar_matches_the_reasoning_already_ratified_for_PDF_text_layers()
    {
        // PDFs were tightened to 100 characters per page and images and TIFFs were left behind.
        // The two thresholds are the same question about the same populations, so they are the
        // same number, and a change to one that is not a change to the other fails here.
        Assert.Equal(
            ProductionDocumentReader.MinimumNativeCharactersPerPage,
            ProductionDocumentReader.MinimumOcrCharactersPerPage);
    }

    [Fact]
    public void A_page_OCR_could_not_read_at_all_is_not_also_charged_a_character_quota()
    {
        // Frames that threw are already counted as failures. Charging them a per-page quota as
        // well would report a mostly-successful read as an outright failure — a control tightened
        // into a false negative, which is its own defect.
        Assert.Equal(
            ProductionDocumentReader.MinimumOcrCharactersPerPage * 2,
            ProductionDocumentReader.OcrTextThreshold(2));

        // Every frame failed: there is no page that should have carried text, so only the
        // absolute "this is nothing at all" floor is left to apply.
        Assert.True(ProductionDocumentReader.OcrTextThreshold(0) <= 20);
    }

    // ── helpers ─────────────────────────────────────────────────────────────────────────────

    private static EmailBackgroundService Service() => new(
        new ServiceCollection().BuildServiceProvider(),
        NullLogger<EmailBackgroundService>.Instance);

    private static System.ComponentModel.DataAnnotations.RangeAttribute RangeOf(
        string propertyName, Type declaringType)
    {
        var property = declaringType.GetProperty(propertyName)
            ?? throw new InvalidOperationException($"{declaringType.Name}.{propertyName} is gone.");
        return property
            .GetCustomAttributes(typeof(System.ComponentModel.DataAnnotations.RangeAttribute), true)
            .Cast<System.ComponentModel.DataAnnotations.RangeAttribute>()
            .SingleOrDefault()
            ?? throw new InvalidOperationException(
                $"{declaringType.Name}.{propertyName} lost its [Range]; the interval is no longer bounded.");
    }
}
