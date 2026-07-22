using ERP_RFQ_Automation.CommercialCases.Lifecycle;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Services;
using ERP_RFQ_Automation.Tests.Support;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.Tests;

public sealed class LeadReferenceTests
{
    [Fact]
    public void Formatter_supports_tenant_year_branch_source_and_financial_year_tokens()
    {
        var configuration = new LeadReferenceConfiguration
        {
            Prefix = "nx west",
            Format = "{PREFIX}/{BU}/{SOURCE}/{FY}/{SEQUENCE}",
            SequencePadding = 4,
            FinancialYearStartMonth = 4
        };

        var result = LeadReferenceFormatter.Format(
            configuration, "Dubai 01", "E-mail", new DateTime(2026, 2, 10), 42);

        Assert.Equal("NXWEST/DUBAI01/E-MAIL/2025-26/0042", result);
    }

    [Theory]
    [InlineData("{PREFIX}-{YEAR}")]
    [InlineData("{PREFIX}-{UNKNOWN}-{SEQUENCE}")]
    public void Formatter_rejects_unsafe_formats(string format)
    {
        var configuration = new LeadReferenceConfiguration { Format = format };

        Assert.Throws<InvalidOperationException>(() => LeadReferenceFormatter.Validate(configuration));
    }

    [Fact]
    public void SaveChanges_generates_tenant_scoped_monotonic_references_and_creation_history()
    {
        using var db = new TestDb();
        using var context = db.ContextFor(null);
        var first = Seed.Lead(context, 101, 7);
        var second = Seed.Lead(context, 102, 7);

        context.SaveChanges();

        Assert.Equal("NXR-2026-000001", first.CommercialCaseReference);
        Assert.Equal("NXR-2026-000002", second.CommercialCaseReference);
        Assert.Equal(2, context.CommercialCases.Count());
        Assert.Equal(first.CommercialCaseReference, first.CommercialCase.MasterReference);
        Assert.Equal(first.CommercialCaseId, first.CommercialCase.Id);
        Assert.Equal(first.BusinessUnitId, first.CommercialCase.BusinessUnitId);
        Assert.Equal(2, context.LeadStatusHistories.Count(h => h.BusinessUnitId == 7 && h.EventType == "Created"));
        Assert.All(context.LeadStatusHistories.Where(h => h.BusinessUnitId == 7),
            history => Assert.Equal(history.Lead.CommercialCaseReference, history.CommercialCaseReference));
    }

    [Fact]
    public void Aggregate_identity_is_global_while_references_remain_tenant_owned()
    {
        using var db = new TestDb();
        using var context = db.ContextFor(null);
        var tenantOne = Seed.Lead(context, 201, 1);
        var tenantTwo = Seed.Lead(context, 202, 2);

        context.SaveChanges();

        Assert.Equal("NXR-2026-000001", tenantOne.CommercialCaseReference);
        Assert.Equal("NXR-2026-000002", tenantTwo.CommercialCaseReference);
        Assert.NotEqual(tenantOne.CommercialCaseId, tenantTwo.CommercialCaseId);
        Assert.Equal(1, tenantOne.CommercialCase.BusinessUnitId);
        Assert.Equal(2, tenantTwo.CommercialCase.BusinessUnitId);
    }

    [Fact]
    public async Task Reference_is_immutable_and_status_changes_are_historicized()
    {
        using var db = new TestDb();
        using var context = db.ContextFor(null);
        var lead = Seed.Lead(context, 301, 3);
        Seed.LeadStatus(context, 24, 3, "Pending Identification");
        context.SaveChanges();
        var originalReference = lead.CommercialCaseReference;

        await new LifecycleApplicationService(context).TransitionLeadAsync(3, lead.Id,
            new LifecycleActor("reviewer-3", "AuthenticatedUser"),
            new LifecycleTransitionCommand("PENDING_IDENTIFICATION", 1, null, null,
                "Api", "corr-301", "req-301", "lead-reference-301"), false, default);

        var statusChange = Assert.Single(context.LeadStatusHistories.Where(h =>
            h.LeadId == lead.Id && h.EventType == "StatusChanged"));
        Assert.Null(statusChange.PreviousStatusId);
        Assert.Equal(24, statusChange.NewStatusId);
        Assert.Equal(originalReference, statusChange.CommercialCaseReference);
        Assert.Equal(lead.CommercialCaseId, statusChange.CommercialCaseId);
        Assert.Null(statusChange.ChangedBy);
        Assert.Equal("PersistenceFallback", statusChange.ActorSource);
        Assert.Single(context.CommercialLifecycleEvents.Where(e => e.AggregateId == lead.Id));

        context.ChangeTracker.Clear();
        lead = context.Leads.Include(item => item.CommercialCase).Single(item => item.Id == 301);
        context.Entry(lead).Property(e => e.CommercialCaseReference).CurrentValue = "MANUAL-CHANGE";
        var error = Assert.Throws<InvalidOperationException>(() => context.SaveChanges());
        Assert.Contains("cannot be changed", error.Message, StringComparison.OrdinalIgnoreCase);

        context.Entry(lead).Property(e => e.CommercialCaseReference).CurrentValue = originalReference;
        context.Entry(lead).State = EntityState.Unchanged;
        var aggregateError = Assert.Throws<InvalidOperationException>(() =>
            context.Entry(lead.CommercialCase).Property(e => e.MasterReference).CurrentValue = "CASE-CHANGE");
        Assert.Contains("part of a key", aggregateError.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Model_has_a_unique_tenant_reference_index()
    {
        using var db = new TestDb();
        using var context = db.ContextFor(null);

        var index = context.Model.FindEntityType(typeof(Lead))!.GetIndexes().Single(i =>
            i.Properties.Select(p => p.Name).SequenceEqual([nameof(Lead.BusinessUnitId), nameof(Lead.CommercialCaseReference)]));

        Assert.True(index.IsUnique);
    }
}
