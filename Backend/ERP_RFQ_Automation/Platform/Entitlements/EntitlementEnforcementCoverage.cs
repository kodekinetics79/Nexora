namespace ERP_RFQ_Automation.Platform.Entitlements;

/// <summary>
/// Structural, reviewable coverage declaration. Every key has either a real execution gate or a
/// server-owned unavailable boundary that denies even when packaging metadata says true.
/// </summary>
public static class EntitlementEnforcementCoverage
{
    public static readonly IReadOnlyDictionary<string, IReadOnlyList<string>> Enforced =
        new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
        {
            [TypedEntitlementCatalog.Rfq] = ["RfqController", "ExtractionController"],
            [TypedEntitlementCatalog.Quotes] = ["QuoteController"],
            [TypedEntitlementCatalog.Orders] = ["OrderController"],
            [TypedEntitlementCatalog.Procurement] = ["ProcurementController"],
            [TypedEntitlementCatalog.Inventory] = ["InventoryIntelligenceController"],
            [TypedEntitlementCatalog.Ai] = ["AgentController"],
            [TypedEntitlementCatalog.EmailIntake] = ["EmailTriageController", "MailboxController"],
            [TypedEntitlementCatalog.SupplierSearch] = ["ProcurementController.SearchSourcingCandidates"],
            [TypedEntitlementCatalog.Integrations] = ["ProcurementIntegrationController"],
            [TypedEntitlementCatalog.Ocr] = ["ProductionDocumentReader.OCR"],
            [TypedEntitlementCatalog.Exports] =
            [
                "CustomerUploaderController.ExportData",
                "SupplierUploaderController.ExportData",
                "ProductUploaderController.ExportProducts",
                "ProductCategoryUploaderController.ExportCategoryData",
                "ProductCategoryUploaderController.ExportSubCategoryData",
                "BoqController.ExportCsv"
            ],
            [TypedEntitlementCatalog.Api] = ["RuntimeUnavailableBoundary"],
            [TypedEntitlementCatalog.Automation] = ["RuntimeUnavailableBoundary"],
            [TypedEntitlementCatalog.Sso] = ["RuntimeUnavailableBoundary"],
            [TypedEntitlementCatalog.Scim] = ["RuntimeUnavailableBoundary"],
            [TypedEntitlementCatalog.DedicatedResources] = ["RuntimeUnavailableBoundary"]
        };

    public static readonly IReadOnlyDictionary<string, string> Missing =
        new Dictionary<string, string>(StringComparer.Ordinal);
}
