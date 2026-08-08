namespace ERP_RFQ_Automation.Platform.Entitlements;

/// <summary>
/// Structural, reviewable coverage declaration. Only keys with production callers are listed as
/// enforced; absent domain surfaces remain explicit gaps instead of being papered over by a test.
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
            [TypedEntitlementCatalog.Integrations] = ["ProcurementIntegrationController"]
        };

    public static readonly IReadOnlyDictionary<string, string> Missing =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [TypedEntitlementCatalog.Ocr] = "OCR worker boundary requires a worker-owned evaluation before processing.",
            [TypedEntitlementCatalog.Api] = "No separately identifiable customer API surface exists; applying this globally would block login.",
            [TypedEntitlementCatalog.Exports] = "Export runs on the platform plane and needs explicit tenant-to-BU resolution.",
            [TypedEntitlementCatalog.Automation] = "No canonical automation command/worker boundary exists.",
            [TypedEntitlementCatalog.Sso] = "SSO is not implemented.",
            [TypedEntitlementCatalog.Scim] = "SCIM is not implemented.",
            [TypedEntitlementCatalog.DedicatedResources] = "Dedicated-resource provisioning is not implemented."
        };
}
