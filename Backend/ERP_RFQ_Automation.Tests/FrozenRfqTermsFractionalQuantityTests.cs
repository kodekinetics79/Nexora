using ERP_RFQ_Automation.CommercialCases.Participation;
using ERP_RFQ_Automation.Controllers;
using ERP_RFQ_Automation.LeadIdentity;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using System.Text.Json;

namespace ERP_RFQ_Automation.Tests;

public sealed class FrozenRfqTermsFractionalQuantityTests
{
    [Fact]
    public void V2_revision_snapshot_preserves_customer_terms_fractional_quantity_and_verbatim_columns()
    {
        const string extra = "{\"Plant\":\"Riyadh-02\",\"Incoterms\":\"DAP\",\"Project\":\"Grid 2030\",\"Cost Centre\":\"CC-4711\"}";
        var lead = new Lead
        {
            Rfqno = "BUYER-RFQ/2026-118",
            BuyersName = "National Grid Buyer",
            RecDate = new DateTime(2026, 8, 29, 10, 15, 0, DateTimeKind.Utc),
            RequiredDeliveryDate = new DateTime(2026, 11, 12),
            DeliveryLocation = "Riyadh Central Warehouse, Gate 4",
            AgreementReference = "FRAME-2026-118",
            BidClosingDateHijri = "1448-03-12",
            InquiryType = "product",
            LeadItems =
            {
                new LeadItem
                {
                    IsCurrentRevisionProjection = true,
                    LineItemNo = "00010",
                    ManufacturerPartNumber = "MTR-100",
                    ProductShortDescription = "Motor starter",
                    Quantity = 2.750125m,
                    UnitOfMeasure = "KG",
                    Currency = "SAR",
                    ExtraFields = extra
                }
            }
        };

        var captured = LeadRevisionCommercialSnapshot.Capture(
            lead, value => value?.Trim(), value => value?.Trim(), _ => null);

        Assert.Equal("BUYER-RFQ/2026-118", captured.CustomerRfqReference);
        Assert.Equal(lead.RequiredDeliveryDate, captured.RequiredDeliveryDate);
        Assert.Equal(lead.DeliveryLocation, captured.DeliveryLocation);
        Assert.Equal(lead.AgreementReference, captured.AgreementReference);
        Assert.Equal(lead.BidClosingDateHijri, captured.BidClosingDateHijri);
        Assert.Equal(lead.InquiryType, captured.InquiryType);
        var line = Assert.Single(captured.Items);
        Assert.Equal(2.750125m, line.Quantity);
        Assert.Equal(extra, line.ExtraFields);
    }

    [Fact]
    public void Runtime_model_uses_decimal_20_6_across_lead_participation_and_rfq()
    {
        using var database = new TestDb();
        using var context = database.ContextFor(null);

        foreach (var (type, property) in new[]
                 {
                     (typeof(LeadItem), nameof(LeadItem.Quantity)),
                     (typeof(LeadLineParticipationDecision), nameof(LeadLineParticipationDecision.Quantity)),
                     (typeof(Rfqitem), nameof(Rfqitem.Quantity))
                 })
        {
            var quantity = context.Model.FindEntityType(type)!.FindProperty(property)!;
            Assert.Equal(typeof(decimal?), quantity.ClrType);
            Assert.Equal(20, quantity.GetPrecision());
            Assert.Equal(6, quantity.GetScale());
        }

        var rfq = context.Model.FindEntityType(typeof(Rfq))!;
        foreach (var property in new[]
                 {
                     nameof(Rfq.CustomerRfqReference), nameof(Rfq.RequiredDeliveryDate),
                     nameof(Rfq.DeliveryLocation), nameof(Rfq.AgreementReference),
                     nameof(Rfq.BidClosingDateHijri), nameof(Rfq.InquiryType)
                 })
            Assert.NotNull(rfq.FindProperty(property));

        Assert.Equal("jsonb", context.Model.FindEntityType(typeof(Rfqitem))!
            .FindProperty(nameof(Rfqitem.ExtraFields))!.GetColumnType());
    }

    [Fact]
    public void Focused_postgresql_migration_is_additive_and_guards_lossy_rollback()
    {
        var root = FindRepositoryRoot();
        var migration = File.ReadAllText(Path.Combine(root,
            "Backend/ERP_RFQ_Automation/MigrationsBaseline/20260829173000_PreserveFrozenRfqTermsAndFractionalQuantity.cs"));

        foreach (var table in new[] { "LeadItems", "LeadLineParticipationDecisions", "RFQItems" })
        {
            Assert.Contains($"ALTER TABLE public.\"{table}\"", migration, StringComparison.Ordinal);
            Assert.Contains("TYPE numeric(20,6)", migration, StringComparison.Ordinal);
        }
        foreach (var column in new[]
                 {
                     "CustomerRfqReference", "RequiredDeliveryDate", "DeliveryLocation",
                     "AgreementReference", "BidClosingDateHijri", "InquiryType", "ExtraFields"
                 })
            Assert.Contains($"\"{column}\"", migration, StringComparison.Ordinal);
        Assert.Contains("ADD COLUMN IF NOT EXISTS", migration, StringComparison.Ordinal);
        Assert.Contains("SET LOCAL lock_timeout = '30s'", migration, StringComparison.Ordinal);
        Assert.Contains("Every integer is exactly representable by numeric(20,6)", migration, StringComparison.Ordinal);
        Assert.DoesNotContain("SELECT 1 FROM public.\"LeadLineParticipationDecisions\"", migration, StringComparison.Ordinal);
        Assert.Contains("\"Quantity\" = trunc(\"Quantity\")", migration, StringComparison.Ordinal);
        Assert.Contains("abs(\"Quantity\") <= 2147483647", migration, StringComparison.Ordinal);
        Assert.Contains("ELSE \"Quantity\"::text::integer", migration, StringComparison.Ordinal);
        Assert.Contains("CK_RFQItems_Quantity_Positive", migration, StringComparison.Ordinal);
        Assert.Contains("CHECK (\"Quantity\" IS NULL OR \"Quantity\" > 0) NOT VALID", migration, StringComparison.Ordinal);
        Assert.Contains("VALIDATE CONSTRAINT \"CK_RFQItems_Quantity_Positive\"", migration, StringComparison.Ordinal);
    }

    [Fact]
    public void Promotion_formal_values_are_sourced_from_the_frozen_v2_snapshot()
    {
        var source = File.ReadAllText(Path.Combine(FindRepositoryRoot(),
            "Backend/ERP_RFQ_Automation/CommercialCases/Promotion/RfqPromotionService.cs"));

        Assert.Contains("CustomerRfqReference = frozenHeader.CustomerRfqReference", source, StringComparison.Ordinal);
        Assert.Contains("RequiredDeliveryDate = frozenHeader.RequiredDeliveryDate", source, StringComparison.Ordinal);
        Assert.Contains("DeliveryLocation = frozenHeader.DeliveryLocation", source, StringComparison.Ordinal);
        Assert.Contains("AgreementReference = frozenHeader.AgreementReference", source, StringComparison.Ordinal);
        Assert.Contains("BidClosingDateHijri = frozenHeader.BidClosingDateHijri", source, StringComparison.Ordinal);
        Assert.Contains("InquiryType = frozenHeader.InquiryType", source, StringComparison.Ordinal);
        Assert.Contains("ExtraFields = frozenLine.ExtraFields", source, StringComparison.Ordinal);
        Assert.Contains("no complete v2 commercial snapshot", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("CustomerRfqReference = lead.Rfqno", source, StringComparison.Ordinal);
        Assert.DoesNotContain("ExtraFields = source.ExtraFields", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Participation_api_contract_deserializes_fractional_quantities_without_model_binding_loss()
    {
        const string requestJson = """
            {
              "expectedLeadRevisionId": 71,
              "expectedDecisionVersion": 3,
              "expectedParticipationVersion": 2,
              "commit": true,
              "lines": [{
                "revisionLineId": 901,
                "decision": 1,
                "quantity": 2.750125
              }]
            }
            """;

        var request = JsonSerializer.Deserialize<SaveParticipationRequest>(requestJson,
            new JsonSerializerOptions(JsonSerializerDefaults.Web));

        Assert.Equal(2.750125m, Assert.Single(request!.Lines!).Quantity);
        Assert.Equal(typeof(decimal?), typeof(ParticipationLineRequest).GetProperty("Quantity")!.PropertyType);
    }

    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (Directory.Exists(Path.Combine(current.FullName, "Backend"))
                && Directory.Exists(Path.Combine(current.FullName, "Frontend")))
                return current.FullName;
            current = current.Parent;
        }
        throw new DirectoryNotFoundException("Repository root was not found.");
    }
}
