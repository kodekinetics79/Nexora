using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.SupplierQuotes;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.Tests;

public sealed class Release02SupplierOfferPricingContractTests
{
    [Fact]
    public void Projection_and_pricing_lineage_are_tenant_qualified_in_the_model()
    {
        var options = new DbContextOptionsBuilder<ErpRfqAutomationContext>()
            .UseNpgsql("Host=localhost;Database=model_only;Username=model;Password=model")
            .Options;
        using var context = new ErpRfqAutomationContext(options);
        var projection = context.Model.FindEntityType(typeof(SupplierQuotedItem))!;
        Assert.Contains(projection.GetIndexes(), index => index.IsUnique &&
            index.Properties.Select(x => x.Name).SequenceEqual([
                nameof(SupplierQuotedItem.BusinessUnitId), nameof(SupplierQuotedItem.SourceSupplierQuoteLineId)]));

        var decision = context.Model.FindEntityType(typeof(CustomerQuoteSourcingDecision))!;
        Assert.NotNull(decision.GetQueryFilter());
        Assert.Contains(decision.GetForeignKeys(), foreignKey =>
            foreignKey.Properties.Select(x => x.Name).SequenceEqual([
                nameof(CustomerQuoteSourcingDecision.BusinessUnitId), nameof(CustomerQuoteSourcingDecision.SupplierQuoteId)]));
        Assert.Contains(decision.GetForeignKeys(), foreignKey =>
            foreignKey.Properties.Select(x => x.Name).SequenceEqual([
                nameof(CustomerQuoteSourcingDecision.QuoteItemId), nameof(CustomerQuoteSourcingDecision.QuoteId)]));
    }

    [Fact]
    public void Pricing_contract_requires_explicit_margin_rationale_and_lineage_ids()
    {
        var command = new ApplyCustomerQuotePricingCommand(7, 101, 202, 22.5m,
            "Approved margin", "pricing-1", "seller@example.com", "corr-1");
        Assert.Equal(22.5m, command.TargetMarginPercent);
        Assert.Equal(101, command.QuoteItemId);
        Assert.Equal(202, command.SourcingAwardId);
        Assert.False(string.IsNullOrWhiteSpace(command.Rationale));
    }
}
