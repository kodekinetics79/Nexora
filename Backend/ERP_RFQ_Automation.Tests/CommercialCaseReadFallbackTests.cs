using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Repositories;
using ERP_RFQ_Automation.Tests.Support;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// The list and detail readers used to invent a commercial case for a document that carries none:
/// <c>q.CommercialCaseId ?? q.Rfq?.CommercialCaseId ?? q.Rfq?.Lead?.CommercialCaseId</c> in
/// <see cref="QuoteRepository"/>, and the equivalent <c>?? r.Lead.CommercialCaseId</c> in
/// <see cref="RfqRepository"/>. That is the same silent foreign-key substitution removed from the
/// case-timeline reader, still running on the screens a user actually looks at — so a document the
/// case workspace reports as an unlinked traceability gap displayed a perfectly good Nexora Serial
/// everywhere else.
///
/// <para>Every test here seeds the fallback's ingredients deliberately: the parent HAS a case, the
/// child does NOT. Restoring any <c>??</c> chain makes the parent's case reappear and fails these
/// assertions.</para>
/// </summary>
public sealed class CommercialCaseReadFallbackTests
{
    private const long Tenant = 97_601;
    private static readonly DateTime Now = new(2026, 8, 9, 9, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task An_rfq_with_no_case_reports_none_even_though_its_lead_has_one()
    {
        using var db = new TestDb();
        var graph = await SeedAsync(db);

        await using var context = db.ContextFor(Tenant);
        var dto = await new RfqRepository(context).GetByIdAsync(graph.UnlinkedRfqId, Tenant);

        Assert.Null(dto.CommercialCaseId);
        Assert.Null(dto.NexoraSerial);
        Assert.Null(dto.CommercialCaseReference);
        // Proof the fallback's source was present and simply not used: the unlinked RFQ's
        // own lead carries a case of its own.
        Assert.Equal(graph.UnlinkedLeadId, dto.LeadId);
        Assert.True(graph.CaseId > 0);
    }

    [Fact]
    public async Task The_rfq_list_reports_the_rfqs_own_case_not_its_leads()
    {
        using var db = new TestDb();
        var graph = await SeedAsync(db);

        await using var context = db.ContextFor(Tenant);
        var (rows, _) = await new RfqRepository(context).GetAllAsync(Tenant, 1, 50);

        var unlinked = Assert.Single(rows, r => r.Id == graph.UnlinkedRfqId);
        Assert.Null(unlinked.CommercialCaseId);
        Assert.Null(unlinked.NexoraSerial);

        var linked = Assert.Single(rows, r => r.Id == graph.LinkedRfqId);
        Assert.Equal(graph.CaseId, linked.CommercialCaseId);
        Assert.Equal(graph.Serial, linked.NexoraSerial);
    }

    [Fact]
    public async Task A_quote_with_no_case_reports_none_even_though_its_rfq_and_lead_have_one()
    {
        using var db = new TestDb();
        var graph = await SeedAsync(db);

        await using var context = db.ContextFor(Tenant);
        var dto = await new QuoteRepository(context).GetByIdAsync(graph.UnlinkedQuoteId, Tenant);

        Assert.Null(dto.CommercialCaseId);
        Assert.Null(dto.NexoraSerial);
        Assert.Null(dto.ContactId);
        // The quote hangs off the RFQ that DOES carry the case, so both fallback hops were
        // available; the reader simply no longer takes them.
        Assert.Equal(graph.LinkedRfqId, dto.RfqId);
    }

    [Fact]
    public async Task The_quote_list_reports_the_quotes_own_case_not_its_rfqs()
    {
        using var db = new TestDb();
        var graph = await SeedAsync(db);

        await using var context = db.ContextFor(Tenant);
        var (rows, _) = await new QuoteRepository(context).GetAllAsync(Tenant, 1, 50);

        var unlinked = Assert.Single(rows, q => q.Id == graph.UnlinkedQuoteId);
        Assert.Null(unlinked.CommercialCaseId);
        Assert.Null(unlinked.NexoraSerial);

        var linked = Assert.Single(rows, q => q.Id == graph.LinkedQuoteId);
        Assert.Equal(graph.CaseId, linked.CommercialCaseId);
        Assert.Equal(graph.Serial, linked.NexoraSerial);
    }

    // ---- fixture ---------------------------------------------------------------------------

    private sealed record Graph(
        long CaseId, string Serial, long LeadId, long UnlinkedLeadId,
        long LinkedRfqId, long UnlinkedRfqId, long LinkedQuoteId, long UnlinkedQuoteId);

    /// <summary>
    /// Both shapes of every document: one that inherited its parent's case and one that never
    /// did. Each unlinked document is attached to a parent that HAS a case, so the only thing
    /// that can distinguish linked from unlinked in a DTO is the document's own column. The
    /// unlinked RFQ hangs off a SECOND lead (which also owns a case) because the partial unique
    /// index on RFQ."LeadID" — one lead, one RFQ — makes the earlier same-lead shape
    /// unrepresentable, exactly as intended.
    /// </summary>
    private static async Task<Graph> SeedAsync(TestDb db)
    {
        long caseId;
        string serial;

        await using var seed = db.ContextFor(null);
        Seed.EnsureBusinessUnit(seed, Tenant);
        var customer = Seed.Customer(seed, Tenant, Tenant, "Fallback customer");
        var lead = Seed.Lead(seed, 97_611, Tenant, buyersName: "Fallback buyer");
        var unlinkedLead = Seed.Lead(seed, 97_612, Tenant, buyersName: "Fallback buyer");
        await seed.SaveChangesAsync();

        lead.ResolveCommercialIdentity(customer.Id, null, "CONFIRMED");
        unlinkedLead.ResolveCommercialIdentity(customer.Id, null, "CONFIRMED");
        caseId = lead.CommercialCaseId;
        serial = lead.CommercialCaseReference;

        var linkedRfq = NewRfq(97_621, "RFQ-FALLBACK-LINKED", lead.Id);
        linkedRfq.InheritCommercialIdentity(lead);
        var unlinkedRfq = NewRfq(97_622, "RFQ-FALLBACK-UNLINKED", unlinkedLead.Id);
        seed.Rfqs.AddRange(linkedRfq, unlinkedRfq);

        var linkedQuote = NewQuote(97_631, "QT-FALLBACK-LINKED", linkedRfq.Id, customer.Id);
        linkedQuote.InheritCommercialIdentity(linkedRfq);
        var unlinkedQuote = NewQuote(97_632, "QT-FALLBACK-UNLINKED", linkedRfq.Id, customer.Id);
        seed.Quotes.AddRange(linkedQuote, unlinkedQuote);
        await seed.SaveChangesAsync();

        return new Graph(caseId, serial, lead.Id, unlinkedLead.Id,
            linkedRfq.Id, unlinkedRfq.Id, linkedQuote.Id, unlinkedQuote.Id);
    }

    private static Rfq NewRfq(long id, string number, long leadId) => new()
    {
        Id = id, Rfqno = number, RecDate = Now, BusinessUnitId = Tenant, LeadId = leadId,
        CreatedBy = "qa", CreatedDate = Now
    };

    private static Quote NewQuote(long id, string number, long rfqId, long customerId) => new()
    {
        Id = id, QuoteNo = number, Rfqid = rfqId, CustomerId = customerId, BusinessUnitId = Tenant,
        QuoteDate = Now, TotalAmount = 100m, CreatedBy = "qa", CreatedDate = Now
    };
}
