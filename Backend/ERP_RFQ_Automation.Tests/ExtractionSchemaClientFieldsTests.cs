using System.Reflection;
using ERP_RFQ_Automation.AI;
using ERP_RFQ_Automation.Extraction;
using ERP_RFQ_Automation.Services;
using ERP_RFQ_Automation.Services.Interfaces;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// The extraction schema, the result record and the output budget are ONE artefact split
/// across three files. A field added to the prompt but missing from the record is silently
/// discarded; a field added to both but not paid for in the budget silently truncates the
/// chunk. These tests hold the three in step.
/// </summary>
public sealed class ExtractionSchemaClientFieldsTests
{
    private static readonly string[] ClientHeaderKeys =
    [
        "CustomerCompanyName", "CustomerCompanyNameConfidence", "CustomerCompanyEvidence",
        "CustomerCompanyRegistrationId", "CustomerCompanyRegistrationIdConfidence",
        "CustomerBuyerEmail", "CustomerBuyerEmailConfidence",
        "CustomerPortalName", "CustomerPortalNameConfidence",
        "SupplierNameOnDocument", "SupplierNameOnDocumentConfidence",
        "SupplierAccountRefOnDocument", "SupplierAccountRefOnDocumentConfidence"
    ];

    /// <summary>The 24 per-item VALUE fields the item schema requests, in schema order.</summary>
    private static readonly string[] ItemValueFields =
    [
        "CompanyRef", "CustomerAccountPortalId", "CustomerRfqno", "ItemMaterialCode",
        "CommodityProduct", "BuyerName", "LineItemNo", "ProductShortName", "Alternative",
        "ProductShortDescription", "Currency", "UnitOfMeasure", "UnitPrice", "Quantity",
        "StorageLocation", "ManufacturerName", "ManufacturerPartNumber",
        "AlternateProductName", "AlternatePartNumber", "ItemText", "MaterialPotext",
        "LeadTime", "ReceivedDate", "BidClosingDateLine"
    ];

    private static string Instructions()
    {
        var method = typeof(OllamaLlmService).GetMethod(
            "BuildExtractionInstructions", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("BuildExtractionInstructions was renamed or removed.");
        return (string)method.Invoke(null, null)!;
    }

    [Fact]
    public void The_prompt_asks_for_every_client_organisation_header_field()
    {
        var instructions = Instructions();
        foreach (var key in ClientHeaderKeys)
            Assert.Contains($"\"{key}\"", instructions, StringComparison.Ordinal);
    }

    [Fact]
    public void Every_client_field_is_a_HEADER_field_and_none_is_per_item()
    {
        // A per-item field costs ~225 output tokens PER LINE; a header field is paid once.
        // The item schema block must not contain any of them.
        var instructions = Instructions();
        var itemsBlock = instructions[instructions.IndexOf("\"Items\"", StringComparison.Ordinal)..];
        foreach (var key in ClientHeaderKeys)
            Assert.DoesNotContain($"\"{key}\"", itemsBlock, StringComparison.Ordinal);
    }

    [Fact]
    public void The_item_schema_asks_for_ONE_confidence_per_item_and_none_per_field()
    {
        // rfq-extraction-v2. The item schema used to carry a "<Field>Confidence" number
        // beside each of the 24 value fields — 52 keys per item, ~439 output tokens — and
        // every one of those numbers was discarded at persistence: LeadItem has a single
        // Aiconfidence column, fed from ItemConfidence. Asking for them again would halve
        // the items that survive a chunk and walk straight back into the truncation outage.
        var instructions = Instructions();
        var itemsBlock = instructions[instructions.IndexOf("\"Items\"", StringComparison.Ordinal)..];

        foreach (var field in ItemValueFields)
            Assert.DoesNotContain($"\"{field}Confidence\"", itemsBlock, StringComparison.Ordinal);

        // The two that ARE asked for, and are read: the per-item summary and the
        // multi-inquiry grouping certainty.
        Assert.Contains("\"ItemConfidence\"", itemsBlock, StringComparison.Ordinal);
        Assert.Contains("\"InquiryGroupConfidence\"", itemsBlock, StringComparison.Ordinal);

        // Every value field itself still has to be requested — this is a budget change,
        // not a schema amputation.
        foreach (var field in ItemValueFields)
            Assert.Contains($"\"{field}\"", itemsBlock, StringComparison.Ordinal);
    }

    [Fact]
    public void OverallConfidence_is_still_requested_because_ingestion_gates_read_it()
    {
        // ManualUploadService hard-rejects below 0.60, EmailService drops below 0.25/0.30,
        // ChunkedExtractionService gates review status and the multi-inquiry split on it.
        Assert.Contains("\"OverallConfidence\"", Instructions(), StringComparison.Ordinal);
    }

    [Fact]
    public void The_item_schema_types_Quantity_as_number_or_null_for_fractional_demand()
    {
        // rfq-extraction-v5. Quantity is decimal(20,6), so the schema must permit the
        // fractional measured amounts customers actually request.
        var instructions = Instructions();
        var itemsBlock = instructions[instructions.IndexOf("\"Items\"", StringComparison.Ordinal)..];
        Assert.Contains("\"Quantity\": number | null", itemsBlock, StringComparison.Ordinal);
        Assert.DoesNotContain("\"Quantity\": integer", itemsBlock, StringComparison.Ordinal);
    }

    [Fact]
    public void The_prompt_spells_out_the_quantity_digit_rules()
    {
        // Exact decimals, or null when the document states none. Grouping separators remain
        // ambiguous and an invented quantity still corrupts a quote.
        var instructions = Instructions();
        Assert.Contains("at most 6 decimal places", instructions, StringComparison.Ordinal);
        Assert.Contains("Do not round or truncate", instructions, StringComparison.Ordinal);
        Assert.Contains("no thousands separators", instructions, StringComparison.Ordinal);
        Assert.Contains("never invent one", instructions, StringComparison.Ordinal);
    }

    [Fact]
    public void The_prompt_version_was_bumped_for_the_quantity_format_rules()
    {
        // The ledger attributes every call to the prompt that produced it. Changing the
        // instructions without moving the label would file v4 answers under v3.
        Assert.Equal("rfq-extraction-v5", AiPromptVersions.StructuredRfqExtraction);
    }

    [Fact]
    public void The_prompt_states_the_direction_of_trade_rule_and_the_evidence_rule()
    {
        var instructions = Instructions();
        Assert.Contains("DIRECTION OF TRADE", instructions, StringComparison.Ordinal);
        Assert.Contains("Vendname", instructions, StringComparison.Ordinal);
        Assert.Contains("NEVER in ", instructions, StringComparison.Ordinal);
        Assert.Contains("CUSTOMER EVIDENCE", instructions, StringComparison.Ordinal);
        Assert.Contains("verbatim", instructions, StringComparison.Ordinal);
    }

    [Fact]
    public void The_result_record_carries_every_field_the_prompt_asks_for()
    {
        var properties = typeof(LeadExtractionResult).GetProperties().Select(p => p.Name).ToHashSet(StringComparer.Ordinal);
        foreach (var key in ClientHeaderKeys)
            Assert.Contains(key, properties);
    }

    [Fact]
    public void Every_new_field_is_optional_so_existing_positional_call_sites_still_compile()
    {
        var constructor = typeof(LeadExtractionResult).GetConstructors().Single();
        foreach (var parameter in constructor.GetParameters().Where(p => ClientHeaderKeys.Contains(p.Name)))
            Assert.True(parameter.IsOptional, $"{parameter.Name} must be a trailing optional parameter.");
    }

    [Fact]
    public void The_header_budget_pays_for_the_keys_the_schema_actually_requests()
    {
        // 13 keys, ~806 characters of JSON at ~3.5 characters/token ≈ 230 tokens on top of
        // the original 300. If someone adds keys without moving this constant, the last
        // fields of every chunked document silently truncate.
        Assert.Equal(13, ClientHeaderKeys.Length);
        Assert.Equal(550, ExtractionOutputBudget.EstimatedHeaderOutputTokens);

        // The documented consequence, asserted rather than assumed. 23/10 (was 11/5) since
        // rfq-extraction-v2 stopped paying for 24 discarded per-field confidences per item.
        Assert.Equal(24, ItemValueFields.Length);
        Assert.Equal(225, ExtractionOutputBudget.EstimatedOutputTokensPerItem);
        Assert.Equal(23, ExtractionOutputBudget.MaxItemsPerChunk(8192));
        Assert.Equal(10, ExtractionOutputBudget.MaxItemsPerChunk(4096));
        Assert.True(ExtractionOutputBudget.FitsBudget(23, 8192));
        Assert.False(ExtractionOutputBudget.FitsBudget(24, 8192));
    }

    /// <summary>
    /// GUARD-PIN on a prompt, and honestly labelled as one: it proves the instruction is present,
    /// not that a model obeys it.
    ///
    /// <para>Why it is worth pinning. "ItemMaterialCode" was requested by the schema with ZERO
    /// guidance anywhere in the numbered rules, while rule 7 explicitly tells the model to divert
    /// any labelled per-item value it does not recognise into "ExtraFields" — which never reaches
    /// LeadItem. A buyer's "Material" column therefore had every reason to land in ExtraFields and
    /// no reason to land in the one field the conversion matcher scores at 1.00. A field the
    /// schema declares and the instructions never explain is a field the pipeline never
    /// populates.</para>
    /// </summary>
    [Fact]
    public void The_prompt_tells_the_model_what_the_buyer_material_number_field_is_for()
    {
        var instructions = Instructions();

        // it is explained at all, and named as the BUYER's own number
        Assert.Contains("ItemMaterialCode", instructions, StringComparison.Ordinal);
        Assert.Contains("BUYER'S OWN MATERIAL NUMBER", instructions, StringComparison.Ordinal);

        // and it explicitly outranks the ExtraFields catch-all, which is what it has to compete with
        Assert.Contains("ExtraFields", instructions, StringComparison.Ordinal);
        var rule = instructions[instructions.IndexOf("BUYER'S OWN MATERIAL NUMBER", StringComparison.Ordinal)..];
        Assert.Contains("PRECEDENCE over rule 7", rule, StringComparison.Ordinal);
        Assert.Contains("ManufacturerPartNumber", rule, StringComparison.Ordinal);
    }
}
