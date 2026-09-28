using ERP_RFQ_Automation.LeadIdentity;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Tests.Support;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// XS-16 (P0): the .doc print of Aramco RFP 6000000003 was read with no makers and no part
/// numbers, hashed differently from the .docx reading of the same RFP, became revision 2 "as if
/// the buyer had changed the RFP", and the lead's current lines fell from 872 makers / 949 part
/// numbers to 1 / 0 — silently. A poorer reading of the same RFQ must never replace a better one
/// without a person seeing it.
/// </summary>
public sealed class PoorerCopyRevisionTests
{
    private const string Approved = "{\"Approved manufacturers\":\"ABB BV (NL): P/N GHG9601; ABB SERVICE (SA): P/N GHG9601\",\"Hazardous Indicator\":\"No\"}";

    private static Lead Copy(long bu, long ingestId, bool rich, int lines = 3, DateTime? closing = null)
    {
        var lead = new Lead
        {
            Rfqno = "6000000003", BuyersName = "Buyer", RecDate = DateTime.UtcNow, LeadSource = "ManualUpload",
            CreatedBy = "test", CreatedDate = DateTime.UtcNow, BusinessUnitId = bu, EmailIngestsId = ingestId,
            Clientemail = "tenders@aramco.test", RequiresCommercialReview = true,
            BidClosingDate = closing ?? new DateTime(2026, 9, 6, 16, 0, 0, DateTimeKind.Utc),
        };
        for (var n = 1; n <= lines; n++)
            lead.LeadItems.Add(new LeadItem
            {
                LineItemNo = $"{n}", ItemMaterialCode = $"00000000200000{n:0000}", ProductShortDescription = $"MOTOR PART {n}",
                Quantity = n, UnitOfMeasure = "each",
                ManufacturerName = rich && n == 1 ? "BENTLY-NEVADA LLC" : null,
                ManufacturerPartNumber = rich ? $"GHG960{n}" : null,
                ExtraFields = rich && n > 1 ? Approved : "{\"Hazardous Indicator\":\"No\"}",
            });
        return lead;
    }

    private static LeadIntakeDescriptor Intake(string key, string hash) => new(
        Guid.NewGuid(), "ManualUpload", key, null, null, "test", "tenders@aramco.test", "RFP 6000000003", $"{key}.doc",
        "application/msword", 100, hash.PadRight(64, '0')[..64], null, null, null, DateTimeOffset.UtcNow,
        LeadProcessingPath.Deterministic, false, 0, "User", "tester", $"test:{key}");

    private static async Task<(TestDb Db, ErpRfqAutomationContext Context, LeadIdentityApplicationService Service, LeadReconciliationResult Created)> RichLeadAsync(long bu)
    {
        var db = new TestDb();
        var context = db.ContextFor(bu);
        Seed.BusinessUnit(context, bu); Seed.EmailConfig(context, bu * 100 + 1, bu); Seed.EmailIngest(context, bu * 100 + 2, bu * 100 + 1, "NeedsReview");
        await context.SaveChangesAsync();
        var service = new LeadIdentityApplicationService(context);
        var created = await service.ReconcileAsync(Copy(bu, bu * 100 + 2, rich: true), Intake("docx", "docx-bytes"));
        Assert.Equal(LeadOccurrenceClassification.New, created.Classification);
        context.ChangeTracker.Clear();
        return (db, context, service, created);
    }

    private static async Task<List<LeadItem>> CurrentLinesAsync(ErpRfqAutomationContext context, long leadId)
        => (await context.Leads.Include(x => x.LeadItems).SingleAsync(x => x.Id == leadId))
            .LeadItems.Where(x => x.IsCurrentRevisionProjection).OrderBy(x => x.LineItemNo).ToList();

    [Fact]
    public async Task A_copy_of_the_same_RFQ_with_fewer_makers_does_not_replace_the_current_revision()
    {
        var (db, context, service, created) = await RichLeadAsync(301);
        using var _ = db; await using var __ = context;

        var result = await service.ReconcileAsync(Copy(301, 30102, rich: false), Intake("doc", "doc-bytes"));

        Assert.Equal(LeadOccurrenceClassification.ExactDuplicate, result.Classification);
        Assert.Equal(created.LeadId, result.LeadId);
        Assert.Equal(1, result.RevisionNumber);
        Assert.Contains("fewer makers or part numbers", Assert.Single(result.Reasons), StringComparison.Ordinal);
        Assert.Equal(1, await context.Set<LeadRevision>().CountAsync(x => x.LeadId == created.LeadId));
        Assert.True(await context.Set<LeadIdentityAuditEvent>()
            .AnyAsync(x => x.LeadId == created.LeadId && x.EventType == "INGESTION_POORER_COPY_KEPT_CURRENT"));

        var lines = await CurrentLinesAsync(context, created.LeadId);
        Assert.Equal(3, lines.Count);
        Assert.Equal(["GHG9601", "GHG9602", "GHG9603"], lines.Select(x => x.ManufacturerPartNumber));
        Assert.Equal("BENTLY-NEVADA LLC", lines[0].ManufacturerName);
        Assert.Contains("Approved manufacturers", lines[1].ExtraFields);
    }

    [Fact]
    public async Task A_copy_missing_lines_and_adding_nothing_waits_for_a_person()
    {
        var (db, context, service, created) = await RichLeadAsync(302);
        using var _ = db; await using var __ = context;

        var result = await service.ReconcileAsync(Copy(302, 30202, rich: true, lines: 1), Intake("partial", "partial-bytes"));

        Assert.Equal(LeadOccurrenceClassification.PossibleMatchReviewRequired, result.Classification);
        Assert.Contains("1 of the 3 lines", Assert.Single(result.Reasons), StringComparison.Ordinal);
        var candidate = Assert.Single(await context.Set<LeadMatchCandidate>().ToListAsync());
        Assert.Equal(created.LeadId, candidate.CandidateLeadId);
        Assert.Equal(3, (await CurrentLinesAsync(context, created.LeadId)).Count);
        Assert.Equal(1, await context.Set<LeadRevision>().CountAsync(x => x.LeadId == created.LeadId));
    }

    [Fact]
    public async Task A_real_amendment_that_reads_fewer_makers_keeps_the_makers_it_did_not_state()
    {
        // The buyer moved the closing date (a genuine revision) and this copy was read without
        // its makers. The new date is taken; the makers are kept, and the reason says so.
        var (db, context, service, created) = await RichLeadAsync(303);
        using var _ = db; await using var __ = context;

        var amended = Copy(303, 30302, rich: false, closing: new DateTime(2026, 9, 20, 16, 0, 0, DateTimeKind.Utc));
        var result = await service.ReconcileAsync(amended, Intake("amended", "amended-bytes"));

        Assert.Equal(LeadOccurrenceClassification.Revision, result.Classification);
        Assert.Equal(2, result.RevisionNumber);
        Assert.Contains(result.Reasons, r => r.Contains("Kept makers and part numbers from revision 1 on 3 line(s)", StringComparison.Ordinal));
        var lead = await context.Leads.SingleAsync(x => x.Id == created.LeadId);
        Assert.Equal(new DateTime(2026, 9, 20, 16, 0, 0, DateTimeKind.Utc), lead.BidClosingDate);
        var lines = await CurrentLinesAsync(context, created.LeadId);
        Assert.Equal(["GHG9601", "GHG9602", "GHG9603"], lines.Select(x => x.ManufacturerPartNumber));
        Assert.Equal("BENTLY-NEVADA LLC", lines[0].ManufacturerName);
        Assert.Contains("Approved manufacturers", lines[1].ExtraFields);
        Assert.Contains("Hazardous Indicator", lines[1].ExtraFields);
    }

    [Fact]
    public async Task A_copy_that_names_a_different_maker_is_a_normal_revision()
    {
        // Control: the guard only catches a copy that states LESS. A buyer changing a part is a
        // revision and the new part replaces the old one.
        var (db, context, service, created) = await RichLeadAsync(304);
        using var _ = db; await using var __ = context;

        var changed = Copy(304, 30402, rich: true);
        changed.LeadItems.First().ManufacturerPartNumber = "GHG9999";
        var result = await service.ReconcileAsync(changed, Intake("changed", "changed-bytes"));

        Assert.Equal(LeadOccurrenceClassification.Revision, result.Classification);
        Assert.DoesNotContain(result.Reasons, r => r.Contains("Kept makers", StringComparison.Ordinal));
        Assert.Equal("GHG9999", (await CurrentLinesAsync(context, created.LeadId))[0].ManufacturerPartNumber);
    }
}
