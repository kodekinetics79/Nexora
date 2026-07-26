using System.Text.Json;

namespace ERP_RFQ_Automation.CommercialDocuments;

public interface ICommercialDocumentClassifier
{
    CommercialDocumentDecision Classify(CommercialDocumentClassificationSignals signals,
        CommercialDocumentMatchReferences matches);
}

public sealed class DeterministicCommercialDocumentClassifier : ICommercialDocumentClassifier
{
    private const decimal AutoClassificationThreshold = .80m;
    private const decimal AmbiguityMargin = .20m;

    public CommercialDocumentDecision Classify(CommercialDocumentClassificationSignals signals,
        CommercialDocumentMatchReferences matches)
    {
        ArgumentNullException.ThrowIfNull(signals);
        ArgumentNullException.ThrowIfNull(matches);
        ValidateLength(signals.OriginalFileName, 512, nameof(signals.OriginalFileName));
        ValidateLength(signals.Subject, 1_000, nameof(signals.Subject));
        ValidateLength(signals.SenderPartyType, 32, nameof(signals.SenderPartyType));
        ValidateLength(signals.BodyExcerpt, 12_000, nameof(signals.BodyExcerpt));
        ValidateLength(signals.ReferenceKind, 64, nameof(signals.ReferenceKind));

        var text = string.Join(' ', new[] { signals.OriginalFileName, signals.Subject, signals.BodyExcerpt }
            .Where(value => !string.IsNullOrWhiteSpace(value))).ToLowerInvariant();
        var sender = signals.SenderPartyType?.Trim().ToLowerInvariant();
        var reference = signals.ReferenceKind?.Trim().ToLowerInvariant();
        var scores = Enum.GetValues<CommercialDocumentType>()
            .Where(type => type != CommercialDocumentType.Unknown)
            .ToDictionary(type => type, _ => 0m);
        var evidence = new Dictionary<CommercialDocumentType, List<string>>();

        void Add(CommercialDocumentType type, decimal score, string rule)
        {
            scores[type] += score;
            if (!evidence.TryGetValue(type, out var rules)) evidence[type] = rules = [];
            rules.Add(rule);
        }

        if (matches.SupplierRfqId.HasValue) Add(CommercialDocumentType.SupplierQuote, .75m, "matched_supplier_rfq");
        if (matches.SupplierQuoteId.HasValue) Add(CommercialDocumentType.SupplierQuote, .75m, "matched_supplier_quote");
        if (matches.CustomerRfqId.HasValue) Add(CommercialDocumentType.CustomerRfq, .55m, "matched_customer_rfq");
        if (matches.PurchaseOrderId.HasValue) Add(CommercialDocumentType.PurchaseOrder, .75m, "matched_purchase_order");
        if (matches.SupplierInvoiceId.HasValue) Add(CommercialDocumentType.SupplierInvoice, .75m, "matched_supplier_invoice");

        if (sender == "supplier")
        {
            Add(CommercialDocumentType.SupplierQuote, .25m, "verified_supplier_sender");
            Add(CommercialDocumentType.SupplierInvoice, .15m, "verified_supplier_sender");
        }
        else if (sender == "customer")
        {
            Add(CommercialDocumentType.CustomerRfq, .25m, "verified_customer_sender");
            Add(CommercialDocumentType.PurchaseOrder, .20m, "verified_customer_sender");
        }

        if (reference is "supplier-rfq" or "supplier-quote")
            Add(CommercialDocumentType.SupplierQuote, .50m, "supplier_reference_kind");
        if (reference == "customer-rfq") Add(CommercialDocumentType.CustomerRfq, .50m, "customer_rfq_reference_kind");
        if (reference is "customer-order" or "purchase-order")
            Add(CommercialDocumentType.PurchaseOrder, .50m, "purchase_order_reference_kind");
        if (reference == "supplier-invoice") Add(CommercialDocumentType.SupplierInvoice, .50m, "supplier_invoice_reference_kind");
        if (reference == "inventory-file") Add(CommercialDocumentType.InventoryFile, .50m, "inventory_reference_kind");

        AddTerms(text, CommercialDocumentType.SupplierQuote, .60m, "supplier_quote_terms",
            "supplier quotation", "quotation no", "quote validity", "unit price", "incoterms");
        AddTerms(text, CommercialDocumentType.CustomerRfq, .60m, "customer_rfq_terms",
            "request for quotation", "request for quote", "rfq number", "bid closing", "requested quantity");
        AddTerms(text, CommercialDocumentType.PurchaseOrder, .65m, "purchase_order_terms",
            "purchase order", "customer po", "order confirmation requested", "ship to");
        AddTerms(text, CommercialDocumentType.SupplierInvoice, .65m, "supplier_invoice_terms",
            "tax invoice", "invoice number", "amount due", "remit to");
        AddTerms(text, CommercialDocumentType.InventoryFile, .55m, "inventory_terms",
            "inventory upload", "stock list", "quantity on hand", "available to promise", "warehouse stock");
        if (ContainsInventoryTerm(text) && HasTabularExtension(signals.OriginalFileName))
            Add(CommercialDocumentType.InventoryFile, .25m, "inventory_tabular_file");

        var ranked = scores.OrderByDescending(item => item.Value).ThenBy(item => item.Key).ToArray();
        var best = ranked[0];
        var runnerUp = ranked[1];
        var confidence = Math.Min(1m, best.Value);
        var ambiguous = confidence < AutoClassificationThreshold || best.Value - runnerUp.Value < AmbiguityMargin;
        var selected = ambiguous ? CommercialDocumentType.Unknown : best.Key;
        var selectedRules = evidence.TryGetValue(best.Key, out var applied) ? applied : [];
        var evidenceJson = JsonSerializer.Serialize(new
        {
            classifierVersion = "commercial-document/v1",
            candidate = best.Key.ToString(),
            rules = selectedRules.OrderBy(value => value),
            topScore = confidence,
            runnerUp = runnerUp.Key.ToString(),
            runnerUpScore = Math.Min(1m, runnerUp.Value),
            ambiguous
        });

        return new CommercialDocumentDecision(selected, ambiguous ? 0m : confidence,
            CommercialDocumentClassificationMethods.LocalDeterministicV1, evidenceJson, ambiguous);

        void AddTerms(string input, CommercialDocumentType type, decimal score, string rule,
            params string[] terms)
        {
            if (terms.Any(input.Contains)) Add(type, score, rule);
        }
    }

    private static void ValidateLength(string? value, int maximum, string name)
    {
        if (value?.Length > maximum)
            throw new ArgumentException($"Value must not exceed {maximum} characters.", name);
    }

    private static bool ContainsInventoryTerm(string text) => new[]
        { "inventory", "stock list", "quantity on hand", "available to promise", "warehouse stock" }
        .Any(text.Contains);

    private static bool HasTabularExtension(string? fileName) =>
        new[] { ".csv", ".xlsx", ".xls", ".tsv" }.Any(extension =>
            fileName?.EndsWith(extension, StringComparison.OrdinalIgnoreCase) == true);
}
