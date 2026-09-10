using ERP_RFQ_Automation.DTOs.DocumentIntelligence;
using ERP_RFQ_Automation.Extraction;
using ERP_RFQ_Automation.ProductIntelligence.ManufacturerKnowledge;
using ERP_RFQ_Automation.Services.DocumentIntelligence;
using ERP_RFQ_Automation.Services.Interfaces;
using ERP_RFQ_Automation.Tests.Support;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// The structured path through <see cref="ChunkedExtractionService.ExtractStructuredAsync"/>
/// with a fake knowledge service: a line carrying only a part number the tenant has learned
/// comes out with its maker as a DERIVED value, reason and evidence attached, and a tenant
/// with no knowledge — or no knowledge service at all — sees exactly what it sees today.
/// </summary>
public sealed class ManufacturerInferenceChunkedExtractionTests
{
    private const long Tenant = 7;

    private static RfqSpreadsheetRow Row(int rowNumber, string product, string part, string? maker = null) => new()
    {
        RowNumber = rowNumber,
        SourceDocumentName = "bidlist.xlsx",
        WorksheetName = "Sheet1",
        RfqNo = "RFQ-9",
        BuyerName = "Omega Oil",
        ReceivedDate = "2026-05-26",
        ProductName = product,
        Quantity = "4",
        ManufacturerPartNumber = part,
        ManufacturerName = maker
    };

    private static ManufacturerKnowledgeSnapshot BmwKnowledge() => new(
        Tenant,
        [
            new ManufacturerPartPattern
            {
                Id = 1, BusinessUnitId = Tenant, Pattern = "X7M5", Manufacturer = "BMW",
                NormalizedManufacturer = "BMW", ObservationCount = 2
            },
            new ManufacturerPartPattern
            {
                Id = 2, BusinessUnitId = Tenant, Pattern = "3RT", Manufacturer = "Siemens",
                NormalizedManufacturer = "SIEMENS", ObservationCount = 2
            }
        ],
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "BMW", "Siemens" });

    private static ChunkedExtractionService Service(IManufacturerKnowledge? knowledge, ILLMService? llm = null)
        => new(llm ?? new StubLlm(), new CanonicalRfqNormalizer(), new NoopLogger<ChunkedExtractionService>(),
            manufacturerKnowledge: knowledge);

    [Fact]
    public async Task A_line_with_only_a_learned_part_number_gets_its_maker_as_a_derived_value()
    {
        var knowledge = new FakeKnowledge(BmwKnowledge());
        var rows = new List<RfqSpreadsheetRow>
        {
            Row(2, "Bracket", "X7-M5"),
            Row(3, "Contactor", "X7-M9", maker: "Toyota"),
            Row(4, "Siemens contactor 7.5kW", "ZZ-99")
        };

        var outcome = await Service(knowledge).ExtractStructuredAsync(rows, Tenant, "bidlist.xlsx");

        Assert.NotNull(outcome.CanonicalImport);
        var lines = Assert.Single(outcome.CanonicalImport!.Documents).LineItems;
        Assert.Equal(3, lines.Count);

        var inferred = lines[0].ManufacturerName;
        Assert.Equal("BMW", inferred.Value);
        Assert.Equal(CanonicalValueKind.Derived, inferred.Kind);
        Assert.Equal(0.80m, inferred.Confidence);
        Assert.Equal(ValidationStatus.Valid, inferred.ValidationStatus);
        Assert.False(inferred.StatedInDocument);
        Assert.Contains("inferred_from_part_number_pattern:X7M5", inferred.Transformations);
        // The normaliser leaves the empty manufacturer cell's own evidence in place; the
        // inference APPENDS a pointer at the part-number cell carrying the reason.
        var evidence = inferred.Evidence.Last();
        Assert.Equal("inferred_from_part_number_pattern:X7M5", evidence.RawValue);
        Assert.Equal(lines[0].ManufacturerPartNumber.Evidence.Single().Location, evidence.Location);

        // The stated maker is untouched, however strong the pattern.
        Assert.Equal("Toyota", lines[1].ManufacturerName.Value);
        Assert.Equal(CanonicalValueKind.Extracted, lines[1].ManufacturerName.Kind);

        // A maker written in the description is read from there.
        Assert.Equal("Siemens", lines[2].ManufacturerName.Value);
        Assert.Equal(0.85m, lines[2].ManufacturerName.Confidence);
        Assert.Contains("inferred_from_description:Siemens", lines[2].ManufacturerName.Transformations);

        // And it reaches the item the lead is built from.
        Assert.NotNull(outcome.Result);
        Assert.Equal("BMW", outcome.Result!.Items![0].ManufacturerName);
        Assert.Equal(0.80, outcome.Result.Items[0].ManufacturerNameConfidence);
        Assert.Equal("Toyota", outcome.Result.Items[1].ManufacturerName);

        Assert.Contains("Manufacturer inferred on 2 line(s) from the tenant's own history.", outcome.Diagnostics);
        Assert.Equal(1, knowledge.Loads);
    }

    [Fact]
    public async Task A_tenant_with_no_knowledge_is_unchanged()
    {
        var knowledge = new FakeKnowledge(ManufacturerKnowledgeSnapshot.Empty(Tenant));
        var rows = new List<RfqSpreadsheetRow> { Row(2, "Bracket", "X7-M5") };

        var outcome = await Service(knowledge).ExtractStructuredAsync(rows, Tenant, "bidlist.xlsx");

        var line = Assert.Single(Assert.Single(outcome.CanonicalImport!.Documents).LineItems);
        Assert.Null(line.ManufacturerName.Value);
        Assert.Equal(CanonicalValueKind.Missing, line.ManufacturerName.Kind);
        Assert.Empty(line.ManufacturerName.Transformations.Where(t => t.StartsWith("inferred_from", StringComparison.Ordinal)));
        Assert.DoesNotContain(line.ManufacturerName.Evidence, e => e.RawValue?.StartsWith("inferred_from", StringComparison.Ordinal) == true);
        Assert.Null(outcome.Result!.Items![0].ManufacturerName);
        Assert.DoesNotContain(outcome.Diagnostics, d => d.Contains("Manufacturer inferred", StringComparison.Ordinal));
    }

    [Fact]
    public async Task No_knowledge_service_at_all_is_the_same_no_op()
    {
        var rows = new List<RfqSpreadsheetRow> { Row(2, "Bracket", "X7-M5") };

        var outcome = await Service(null).ExtractStructuredAsync(rows, Tenant, "bidlist.xlsx");

        var line = Assert.Single(Assert.Single(outcome.CanonicalImport!.Documents).LineItems);
        Assert.Equal(CanonicalValueKind.Missing, line.ManufacturerName.Kind);
        Assert.DoesNotContain(outcome.Diagnostics, d => d.Contains("Manufacturer inferred", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_unstructured_path_fills_blank_makers_on_the_merged_items()
    {
        var knowledge = new FakeKnowledge(BmwKnowledge());
        var items = new List<LeadItemData>
        {
            Ext.Item(0.9, "Bracket") with { ManufacturerPartNumber = "X7-M5" },
            Ext.Item(0.9, "Siemens contactor") with { ManufacturerName = "Toyota" },
            Ext.Item(0.9, "Siemens contactor")
        };
        var llm = new StubLlm(Ext.Result(items, 0.9));
        var input = new DocumentExtractionInput
        {
            BusinessUnitId = Tenant, LineItemRegions = ["row 1", "row 2", "row 3"], HeaderText = "buyer: Omega"
        };

        var outcome = await Service(knowledge, llm).ExtractUnstructuredAsync(input);

        Assert.Equal(1, llm.CallCount);
        Assert.NotNull(outcome.Result);
        var produced = outcome.Result!.Items!;
        Assert.Equal("BMW", produced[0].ManufacturerName);
        Assert.Equal(0.80, produced[0].ManufacturerNameConfidence);
        Assert.Equal("Toyota", produced[1].ManufacturerName);
        Assert.Equal("Siemens", produced[2].ManufacturerName);
        Assert.Contains("Manufacturer inferred on 2 line(s) from the tenant's own history.", outcome.Diagnostics);
    }

    private sealed class FakeKnowledge(ManufacturerKnowledgeSnapshot snapshot) : IManufacturerKnowledge
    {
        public int Loads { get; private set; }

        public Task<ManufacturerKnowledgeSnapshot> ForBusinessUnitAsync(long businessUnitId, CancellationToken ct = default)
        {
            Loads++;
            Assert.Equal(snapshot.BusinessUnitId, businessUnitId);
            return Task.FromResult(snapshot);
        }
    }
}
