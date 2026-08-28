using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ERP_RFQ_Automation.Authorization;
using ERP_RFQ_Automation.CommercialIntelligence.Sales;
using ERP_RFQ_Automation.CommercialRouting;
using ERP_RFQ_Automation.CommercialCases.Participation;
using ERP_RFQ_Automation.CommercialCases.Promotion;
using ERP_RFQ_Automation.DocumentIntelligence.Persistence;
using ERP_RFQ_Automation.Agent.Models;
using ERP_RFQ_Automation.Extraction;
using ERP_RFQ_Automation.Inventory;
using ERP_RFQ_Automation.Inventory.Commercial;
using ERP_RFQ_Automation.LeadIdentity;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.OrderToCash;
using ERP_RFQ_Automation.Procurement;
using ERP_RFQ_Automation.ProductIntelligence;
using ERP_RFQ_Automation.Platform.Entitlements;
using ERP_RFQ_Automation.Security;
using ERP_RFQ_Automation.SupplierQuotes;
using Microsoft.EntityFrameworkCore;
using Models = ERP_RFQ_Automation.Models;
using PlatformModels = ERP_RFQ_Automation.Platform.Models;

AppContext.SetSwitch("Npgsql.EnableLegacyTimestampBehavior", true);

const long tenantId = 80101;
const long otherTenantId = 80102;
const string fixtureActor = "acceptance-fixture";
string? Argument(string name)
{
    var index = Array.FindIndex(args, value => value.Equals($"--{name}", StringComparison.OrdinalIgnoreCase));
    return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
}

var connection = Argument("connection")
    ?? Environment.GetEnvironmentVariable("NEXORA_ACCEPTANCE_CONNECTION")
    ?? throw new InvalidOperationException("NEXORA_ACCEPTANCE_CONNECTION is required.");
var password = Argument("password")
    ?? Environment.GetEnvironmentVariable("NEXORA_ACCEPTANCE_PASSWORD")
    ?? throw new InvalidOperationException("NEXORA_ACCEPTANCE_PASSWORD is required.");
var secretProtectionKey = Argument("secret-protection-key")
    ?? Environment.GetEnvironmentVariable("NEXORA_ACCEPTANCE_SECRET_PROTECTION_KEY")
    ?? throw new InvalidOperationException("NEXORA_ACCEPTANCE_SECRET_PROTECTION_KEY is required.");
SecretProtection.Use(new AesGcmSecretProtector(Convert.FromBase64String(secretProtectionKey)));
var options = new DbContextOptionsBuilder<ErpRfqAutomationContext>().UseNpgsql(connection).Options;
await using var db = new ErpRfqAutomationContext(options);
var now = DateTime.UtcNow;

await EnsureTenantAsync(tenantId, "R01C1", "Release 01C1 Acceptance");
await EnsureTenantAsync(otherTenantId, "R01C1-X", "Release 01C1 Other Tenant");
var platformPlan = await EnsurePlatformPlanAsync("enterprise", "Enterprise", 4, 8, 10000, 100);
await EnsurePlatformPlanAsync("pro", "Pro", 2, 4, 5000, 25);
await EnsurePlatformPlanAsync("free", "Free", 1, 2, 1000, 5);
var platformTenant = await EnsurePlatformTenantAsync(
    "release-01c1-acceptance", "Release 01C1 Acceptance", tenantId, platformPlan.Id);
await EnsurePlatformTenantAsync(
    "release-01c1-other", "Release 01C1 Other Tenant", otherTenantId, platformPlan.Id);
await EnsurePlatformUserAsync("owner@acceptance.local", "Acceptance Platform Owner");
await EnsureUomAsync("EA", "Each");
await EnsureUomAsync("JOB", "Job");
var ownerRole = await EnsureRoleAsync(tenantId, "R01C1_OWNER", "Acceptance Tenant Owner", RoleRanks.Owner);
var managerRole = await EnsureRoleAsync(tenantId, "R01C1_MANAGER", "Acceptance Manager", RoleRanks.Manager);
var editorRole = await EnsureRoleAsync(tenantId, "R01C1_EDITOR", "Acceptance Sales Editor", RoleRanks.Member);
var deniedRole = await EnsureRoleAsync(tenantId, "R01C1_DENIED", "Acceptance Denied", RoleRanks.Member);
var otherRole = await EnsureRoleAsync(otherTenantId, "R01C1_OTHER", "Acceptance Other Tenant", RoleRanks.Member);
await EnsureUserAsync(tenantId, ownerRole.SetupId, "owner@release01c1.local", "Olivia", "Owner");
var manager = await EnsureUserAsync(tenantId, managerRole.SetupId, "manager@release01c1.local", "Morgan", "Manager");
await EnsureUserAsync(tenantId, editorRole.SetupId, "editor@release01c1.local", "Elliot", "Editor");
await EnsureUserAsync(tenantId, deniedRole.SetupId, "denied@release01c1.local", "Dana", "Denied");
await EnsureUserAsync(otherTenantId, otherRole.SetupId, "other@release01c1.local", "Taylor", "Other Tenant");

var permissionModules = new[]
{
    "Leads", "Dashboard", "Users", "Customers", "Products", "Product Categories",
    "Suppliers", "Shipments", "Supplier History", "Supplier Negotiation", "RFQ Management",
    "Quotations", "Orders", "Customer Awards"
};
foreach (var moduleName in permissionModules)
{
    var module = await EnsureModuleAsync(moduleName);
    foreach (var role in new[] { managerRole, editorRole })
        await EnsurePermissionAsync(tenantId, role.SetupId, module.Id, create: true, edit: true);
}
var otherLeadsModule = await EnsureModuleAsync("Leads");
await EnsurePermissionAsync(otherTenantId, otherRole.SetupId, otherLeadsModule.Id, create: true, edit: true);
var otherOrdersModule = await EnsureModuleAsync("Orders");
await EnsurePermissionAsync(otherTenantId, otherRole.SetupId, otherOrdersModule.Id,
    create: false, edit: false, view: false);
await db.SaveChangesAsync();

var northstar = await EnsureCustomerAsync("Northstar Process Controls", "buyer@northstar.local");
var intakeConfig = await EnsureEmailConfigurationAsync();
var intake = await EnsureEmailIngestAsync(intakeConfig.Id);
var originalLead = await EnsureIdentityLeadAsync(
    "NORTHSTAR-440", "Northstar Buyer", "buyer@northstar.local", manager.Id,
    Guid.Parse("01c10000-0000-0000-0000-000000000000"), "fixture-original", "02-duplicate.csv",
    ("2", "VALVE-A", "Control valve", 14),
    ("3", "ACTUATOR-ADDED", "Electric actuator", 2));
EnsureCommercialIdentity(originalLead, northstar.Id, null, "MATCHED");
await db.SaveChangesAsync();

var salesRole = await EnsureRoleAsync(tenantId, "CORE_SALES_REP", "Core Sales Representative", RoleRanks.Member);
var sarah = await EnsureUserAsync(tenantId, salesRole.SetupId, "sarah.malik@acceptance.local", "Sarah", "Malik");
var ahmed = await EnsureUserAsync(tenantId, salesRole.SetupId, "ahmed.khan@acceptance.local", "Ahmed", "Khan");
var priya = await EnsureUserAsync(tenantId, salesRole.SetupId, "priya.nair@acceptance.local", "Priya", "Nair");
var daniel = await EnsureUserAsync(tenantId, salesRole.SetupId, "daniel.ross@acceptance.local", "Daniel", "Ross");
var lena = await EnsureUserAsync(tenantId, salesRole.SetupId, "lena.ortiz@acceptance.local", "Lena", "Ortiz");
foreach (var moduleName in permissionModules)
{
    var module = await EnsureModuleAsync(moduleName);
    await EnsurePermissionAsync(tenantId, salesRole.SetupId, module.Id, create: true, edit: true);
}
await db.SaveChangesAsync();

var team = await EnsureTeamAsync("Core Commercial Intelligence", manager.Id);
await EnsureSalesRepAsync(sarah.Id, 100, 1.25m, ["NORTH", "KEY-ACCOUNT"], ["VALVES", "CONTROLS"]);
await EnsureSalesRepAsync(ahmed.Id, 85, 1.10m, ["NORTH", "BACKUP"], ["VALVES", "MRO"]);
await EnsureSalesRepAsync(priya.Id, 70, 1.40m, ["WEST"], ["ELECTRICAL", "AUTOMATION"]);
await EnsureSalesRepAsync(daniel.Id, 55, 0.90m, ["SOUTH"], ["MECHANICAL", "PUMPS"]);
await EnsureSalesRepAsync(lena.Id, 90, 1.15m, ["EAST"], ["SERVICES", "INSTRUMENTATION"]);
foreach (var rep in new[] { sarah, ahmed, priya, daniel, lena })
    await EnsureMembershipAsync(rep.Id, team.Id, rep.Id == sarah.Id);

var abc = await EnsureCustomerAsync("ABC Engineering", "procurement@abc-engineering.local");
var abcContact = await EnsureContactAsync(abc.Id, "Amira", "Cole", "amira.cole@abc-engineering.local");
await EnsureCustomerIdentifierAsync(abc.Id, CustomerIdentifierType.Email,
    "procurement@abc-engineering.local", "procurement@abc-engineering.local");
await EnsureCustomerIdentifierAsync(abc.Id, CustomerIdentifierType.Domain,
    "abc-engineering.local", "abc-engineering.local");
var ownership = await EnsureOwnershipAsync(abc.Id, sarah.Id, ahmed.Id);

var sixLineLead = await EnsureIdentityLeadAsync(
    "ABC-ENG-ATP-006", "Amira Cole", "procurement@abc-engineering.local", sarah.Id,
    Guid.Parse("c0e00000-0000-0000-0000-000000000006"), "core-six-lines", "abc-engineering-six-lines.csv",
    ("1", "CORE-ATP-100", "Known valve actuator with sufficient ATP", 10),
    ("2", "CORE-PARTIAL-200", "Known regulator with partial ATP", 20),
    ("3", "CORE-OOS-300", "Known transmitter with supplier history and zero ATP", 12),
    ("4", "CORE-INCOMING-400", "Known controller with confirmed incoming inventory", 15),
    ("5", "X-UNKNOWN-900", "Unknown industrial component requiring related-resource search", 5),
    ("6", "FIELD-SERVICE", "On-site commissioning service, non-inventory", 1));
EnsureCommercialIdentity(sixLineLead, abc.Id, abcContact.Id, "MATCHED");
sixLineLead.AssignTo = sarah.Id;
sixLineLead.AssignOn ??= now.AddMinutes(-30);
sixLineLead.AssignComment = "Confirmed ABC Engineering account owner available within capacity.";

var backupLead = await EnsureIdentityLeadAsync(
    "ABC-ENG-BACKUP-001", "Amira Cole", "procurement@abc-engineering.local", ahmed.Id,
    Guid.Parse("c0e00000-0000-0000-0000-000000000007"), "core-backup-case", "abc-engineering-owner-leave.csv",
    ("1", "CORE-ATP-100", "Urgent replacement required while account owner is on leave", 2));
EnsureCommercialIdentity(backupLead, abc.Id, abcContact.Id, "MATCHED");
backupLead.AssignTo = ahmed.Id;
backupLead.AssignOn ??= now.AddMinutes(-20);
backupLead.AssignComment = "Sarah Malik remains Account Owner; Ahmed Khan selected as backup while Sarah is on leave.";
var partialAwardLead = await EnsureIdentityLeadAsync(
    "ABC-ENG-CLIENT-PO-PARTIAL-V2", "Amira Cole", "procurement@abc-engineering.local", sarah.Id,
    Guid.Parse("c0e00000-0000-0000-0000-000000000010"), "core-client-po-partial-v2",
    "abc-engineering-client-po-partial-v2.csv",
    ("1", "CORE-ATP-100", "Valve actuator for partial Client PO acceptance", 4));
EnsureCommercialIdentity(partialAwardLead, abc.Id, abcContact.Id, "MATCHED");
partialAwardLead.AssignTo = sarah.Id;
partialAwardLead.AssignOn ??= now.AddMinutes(-10);
partialAwardLead.AssignComment = "Confirmed ABC Engineering account ownership for Client PO acceptance.";
var exactAwardLead = await EnsureIdentityLeadAsync(
    "ABC-ENG-CLIENT-PO-EXACT", "Amira Cole", "procurement@abc-engineering.local", sarah.Id,
    Guid.Parse("c0e00000-0000-0000-0000-000000000009"), "core-client-po-exact",
    "abc-engineering-client-po-exact.csv",
    ("1", "CORE-ATP-100", "Valve actuator for exact Client PO acceptance", 2));
EnsureCommercialIdentity(exactAwardLead, abc.Id, abcContact.Id, "MATCHED");
exactAwardLead.AssignTo = sarah.Id;
exactAwardLead.AssignOn ??= now.AddMinutes(-9);
exactAwardLead.AssignComment = "Confirmed ABC Engineering account ownership for exact Client PO acceptance.";
await db.SaveChangesAsync();

await EnsureAssignmentAsync(sixLineLead.Id, abc.Id, ownership.Id, sarah.Id,
    RoutingOutcome.AssignedPrimary, "CONFIRMED_ACCOUNT_OWNER_AVAILABLE",
    "Sarah Malik is the confirmed ABC Engineering Account Owner and is active within capacity.", "core-routing-primary");
await EnsureAssignmentAsync(backupLead.Id, abc.Id, ownership.Id, ahmed.Id,
    RoutingOutcome.AssignedBackup, "PRIMARY_OWNER_ON_LEAVE_BACKUP_SELECTED",
    "Sarah Malik remains Account Owner but is unavailable for this opportunity; Ahmed Khan is the configured backup.", "core-routing-backup");

await EnsureWorkloadLeadAsync("CORE-WORKLOAD-SARAH-01", sarah.Id, 3, now.AddDays(2));
await EnsureWorkloadLeadAsync("CORE-WORKLOAD-AHMED-01", ahmed.Id, 6, now.AddHours(18));
await EnsureWorkloadLeadAsync("CORE-WORKLOAD-PRIYA-01", priya.Id, 2, now.AddDays(5));
await EnsureWorkloadLeadAsync("CORE-WORKLOAD-DANIEL-01", daniel.Id, 10, now.AddHours(-4));
await EnsureWorkloadLeadAsync("CORE-WORKLOAD-LENA-01", lena.Id, 4, now.AddDays(1));
await EnsureFollowUpAsync(ahmed.Id, backupLead.Id, abc.Id, now.AddHours(4), 90, "OWNER_LEAVE_COVERAGE");
await EnsureFollowUpAsync(daniel.Id, sixLineLead.Id, abc.Id, now.AddHours(-2), 60, "WORKLOAD_EVIDENCE");

var currency = await EnsureCurrencyAsync("USD", "US Dollar", "$");
var supplier = await EnsureSupplierAsync("Precision Controls Supply", "quotes@precision-controls.local");
var supplierTwo = await EnsureSupplierAsync("Atlas Automation Partners", "quotes@atlas-automation.local");
var supplierThree = await EnsureSupplierAsync("Meridian Process Equipment", "rfq@meridian-process.local");
var category = await EnsureProductCategoryAsync("Core Acceptance Components");
var sufficient = await EnsureProductAsync("CORE-ATP-100", "Acceptance Valve Actuator", category.Id, 5, supplier.Id);
var partial = await EnsureProductAsync("CORE-PARTIAL-200", "Acceptance Pressure Regulator", category.Id, 14, supplier.Id);
var outOfStock = await EnsureProductAsync("CORE-OOS-300", "Acceptance Pressure Transmitter", category.Id, 21, supplier.Id);
var incomingProduct = await EnsureProductAsync("CORE-INCOMING-400", "Acceptance Logic Controller", category.Id, 18, supplier.Id);
await EnsureAliasAsync(sufficient.Id, ProductAliasKind.CustomerPartNumber, "ABC-ACT-100", abc.Id);
await EnsureAliasAsync(outOfStock.Id, ProductAliasKind.SupplierPartNumber, "PCS-TX-300", null);

var primaryWarehouse = await EnsureWarehouseAsync("CORE-PRIMARY", "Core Primary Warehouse", "North Distribution Hub");
var overflowWarehouse = await EnsureWarehouseAsync("CORE-OVERFLOW", "Core Overflow Warehouse", "North Overflow Hub");
var transitWarehouse = await EnsureWarehouseAsync("CORE-TRANSIT", "Core Transit Warehouse", "Incoming Inspection Hub");
var sufficientPrimary = await EnsureInventoryAsync(sufficient, primaryWarehouse, 24, 5, 2);
var sufficientOverflow = await EnsureInventoryAsync(sufficient, overflowWarehouse, 16, 3, 1);
var partialStock = await EnsureInventoryAsync(partial, primaryWarehouse, 9, 12, 2);
var zeroStock = await EnsureInventoryAsync(outOfStock, primaryWarehouse, 0, 8, 0);
var incomingStock = await EnsureInventoryAsync(incomingProduct, transitWarehouse, 0, 10, 0);

await EnsureMovementAsync(sufficient, sufficientPrimary, primaryWarehouse, InventoryMovementType.Receipt, 24, "CORE-RECEIPT-ATP-PRIMARY");
await EnsureMovementAsync(sufficient, sufficientOverflow, overflowWarehouse, InventoryMovementType.Receipt, 16, "CORE-RECEIPT-ATP-OVERFLOW");
await EnsureMovementAsync(partial, partialStock, primaryWarehouse, InventoryMovementType.Receipt, 9, "CORE-RECEIPT-PARTIAL");
await EnsureMovementAsync(outOfStock, zeroStock, primaryWarehouse, InventoryMovementType.AdjustmentDecrease, 0, "CORE-ZERO-OOS");
await EnsureMovementAsync(incomingProduct, incomingStock, transitWarehouse, InventoryMovementType.AdjustmentIncrease, 0, "CORE-ZERO-INCOMING");
await EnsureReservationAsync(sufficientPrimary.Id, 4, "core-reservation-atp");
await EnsureReservationAsync(partialStock.Id, 1, "core-reservation-partial");
await EnsureIncomingAsync(incomingProduct.Id, incomingStock.Id, transitWarehouse.Id, 30, 5, 3,
    DateOnly.FromDateTime(now.AddDays(7)), "CORE-PO-INCOMING-400");
await EnsurePurchaseHistoryAsync(outOfStock.Id, supplier.Id, 25, 418.50m, "USD", "CPOOOS300");
await EnsurePurchaseHistoryAsync(outOfStock.Id, supplierTwo.Id, 18, 431.75m, "USD", "CPOOOS301");
await EnsurePurchaseHistoryAsync(outOfStock.Id, supplierThree.Id, 30, 425.25m, "USD", "CPOOOS302");
await EnsureSupplierQuoteAsync(supplier.Id, "CORE-OOS-300", 12, 452m, "CORE-SQ-OOS-300");
await EnsureSupplierQuoteAsync(supplierTwo.Id, "CORE-OOS-300", 12, 446m, "CORE-SQ-OOS-300-ATLAS");
await EnsureSupplierQuoteAsync(supplierThree.Id, "CORE-OOS-300", 12, 449m, "CORE-SQ-OOS-300-MERIDIAN");
await EnsureSupplierQuoteAsync(supplier.Id, "X-UNKNOWN-900", 5, 87.25m, "CORE-SQ-UNKNOWN-900");

var revisedSixLineLead = await EnsureReconciledOccurrenceAsync(
    sixLineLead, Guid.Parse("c0e00000-0000-0000-0000-000000000106"), "core-six-lines-revision-2",
    "abc-engineering-six-lines-revision-2.csv",
    ("1", "CORE-ATP-100", "Known valve actuator with sufficient ATP", 10),
    ("2", "CORE-PARTIAL-200", "Known regulator with partial ATP", 22),
    ("3", "CORE-OOS-300", "Known transmitter with supplier history and zero ATP", 12),
    ("4", "CORE-INCOMING-400", "Known controller with confirmed incoming inventory", 15),
    ("5", "X-UNKNOWN-900", "Unknown industrial component requiring related-resource search", 5),
    ("6", "FIELD-SERVICE", "On-site commissioning service, non-inventory", 1));
EnsureCommercialIdentity(revisedSixLineLead, abc.Id, abcContact.Id, "MATCHED");
revisedSixLineLead.AssignTo = sarah.Id;
revisedSixLineLead.AssignComment = "Confirmed account ownership and deterministic product expertise match.";
await db.SaveChangesAsync();

var duplicateBatchId = Guid.Parse("c0e00000-0000-0000-0000-000000000206");
await EnsureReconciledOccurrenceAsync(
    sixLineLead, duplicateBatchId, "core-six-lines-exact-duplicate", "abc-engineering-six-lines-resend.csv",
    ("1", "CORE-ATP-100", "Known valve actuator with sufficient ATP", 10),
    ("2", "CORE-PARTIAL-200", "Known regulator with partial ATP", 22),
    ("3", "CORE-OOS-300", "Known transmitter with supplier history and zero ATP", 12),
    ("4", "CORE-INCOMING-400", "Known controller with confirmed incoming inventory", 15),
    ("5", "X-UNKNOWN-900", "Unknown industrial component requiring related-resource search", 5),
    ("6", "FIELD-SERVICE", "On-site commissioning service, non-inventory", 1));

var ambiguousLead = await EnsureIdentityLeadAsync(
    "CORE-AMBIGUOUS-001", "ABC Procurement", "shared-buying@acceptance.local", sarah.Id,
    Guid.Parse("c0e00000-0000-0000-0000-000000000301"), "core-ambiguous-customer",
    "ambiguous-customer.csv", ("1", "CORE-ATP-100", "Customer identity requires review", 1));
ambiguousLead.SuggestCommercialIdentity(
    "MULTIPLE_LOCAL_CUSTOMER_SIGNALS", 0.55m,
    "ABC Engineering is a candidate, but the evidence is not strong enough to link automatically.",
    ambiguous: true, now);
ambiguousLead.RequiresCommercialReview = true;
ambiguousLead.AssignComment = "Customer Resolution Required: multiple local identity signals require manager review.";

var unresolvedLead = await EnsureIdentityLeadAsync(
    "CORE-UNRESOLVED-001", "", "unresolved-source@acceptance.local", manager.Id,
    Guid.Parse("c0e00000-0000-0000-0000-000000000302"), "core-unresolved-upload",
    "unresolved-upload.csv", ("1", "X-UNKNOWN-901", "No customer identity supplied", 1));
unresolvedLead.AssignTo = null;
unresolvedLead.AssignOn = null;
unresolvedLead.AssignComment = null;

var weightedLead = await EnsureIdentityLeadAsync(
    "CORE-WEIGHTED-001", "New Account Buyer", "new-account@acceptance.local", priya.Id,
    Guid.Parse("c0e00000-0000-0000-0000-000000000303"), "core-weighted-routing",
    "weighted-new-customer.csv", ("1", "CORE-ATP-100", "Automation component for western territory", 3));
weightedLead.AssignTo = priya.Id;
weightedLead.AssignOn ??= now.AddMinutes(-15);
weightedLead.AssignComment = "Selected by weighted workload, automation expertise, territory fit, and fair distribution.";

var reassignedLead = await EnsureIdentityLeadAsync(
    "CORE-REASSIGNED-001", "Reassignment Buyer", "reassignment@abc-engineering.local", ahmed.Id,
    Guid.Parse("c0e00000-0000-0000-0000-000000000304"), "core-reassigned",
    "reassigned-opportunity.csv", ("1", "CORE-PARTIAL-200", "Opportunity reassigned with history", 2));
EnsureCommercialIdentity(reassignedLead, abc.Id, abcContact.Id, "MATCHED");
reassignedLead.AssignTo = ahmed.Id;
reassignedLead.AssignComment = "Reassigned from Sarah Malik to Ahmed Khan for temporary backup coverage.";

var confirmationCustomer = await EnsureCustomerAsync("Delta Fabrication", "buying@delta-fabrication.local");
await EnsureOwnershipAsync(confirmationCustomer.Id, ahmed.Id, sarah.Id);
await db.SaveChangesAsync();
await EnsureReassignmentHistoryAsync(reassignedLead.Id, abc.Id, ownership.Id, sarah.Id, ahmed.Id);

var quoteDraftLead = await EnsureSixLineCopyAsync(
    "CORE-QUOTE-DRAFT-006", "core-quote-draft-six", Guid.Parse("c0e00000-0000-0000-0000-000000000401"));
var rfqCreationLead = await EnsureSixLineCopyAsync(
    "CORE-RFQ-CREATE-006", "core-rfq-create-six", Guid.Parse("c0e00000-0000-0000-0000-000000000402"));
rfqCreationLead.CommercialFactsVerified = true;
var partialBidLead = await EnsureSixLineCopyAsync(
    "CORE-PARTIAL-BID-006", "core-partial-bid-six", Guid.Parse("c0e00000-0000-0000-0000-000000000404"));
partialBidLead.CommercialFactsVerified = true;
var noBidLead = await EnsureSixLineCopyAsync(
    "CORE-NO-BID-006", "core-no-bid-six", Guid.Parse("c0e00000-0000-0000-0000-000000000405"));
noBidLead.CommercialFactsVerified = true;
var inventoryFailureLead = await EnsureIdentityLeadAsync(
    "CORE-INVENTORY-CHECK-FAIL", "Inventory Failure Buyer", "procurement@abc-engineering.local", sarah.Id,
    Guid.Parse("c0e00000-0000-0000-0000-000000000403"), "core-inventory-failure",
    "inventory-check-unavailable.csv", ("1", "CHECK-UNAVAILABLE-001", "Inventory check unavailable acceptance case", 1));
EnsureCommercialIdentity(inventoryFailureLead, abc.Id, abcContact.Id, "MATCHED");
await db.SaveChangesAsync();
await EnsureSetupAsync("RFQStatus", "DRAFT", "Draft");
await EnsureSetupAsync("LeadStatus", "CONVERTED_TO_RFQ", "Converted to RFQ");
await EnsureSetupAsync("LeadStatus", "DISQUALIFIED", "Disqualified");
await EnsureLeadQualifiedAsync(rfqCreationLead.Id);
await EnsureLeadQualifiedAsync(partialBidLead.Id);
await EnsureLeadQualifiedAsync(noBidLead.Id);
await EnsurePromotionEvidenceAsync(rfqCreationLead);
await EnsurePromotionEvidenceAsync(partialBidLead);
await EnsurePromotionEvidenceAsync(noBidLead);

var mainRfq = await EnsureRfqAsync(sixLineLead, "CORE-RFQ-006");
var quoteDraftRfq = await EnsureRfqAsync(quoteDraftLead, "CORE-RFQ-QUOTE-DRAFT-006");
var resolutionPersistenceAvailable = await db.Database.SqlQueryRaw<bool>(
    "SELECT to_regclass('public.\"RFQ\"') IS NOT NULL AS \"Value\"").SingleAsync();
if (resolutionPersistenceAvailable)
{
    await EnsureLineResolutionsAsync(sixLineLead, mainRfq, sufficient, partial, outOfStock, incomingProduct);
    await EnsureLineResolutionsAsync(quoteDraftLead, quoteDraftRfq, sufficient, partial, outOfStock, incomingProduct);
}
var negotiationSupplierQuoteId = await EnsureNegotiationSupplierQuoteAsync(
    mainRfq, outOfStock, supplier, currency);

var draftStatus = await EnsureSetupAsync("QuoteStatus", "DRAFT", "Draft");
var sentStatus = await EnsureSetupAsync("QuoteStatus", "SENT", "Sent");
var mainQuote = await EnsureQuoteAsync(mainRfq, draftStatus.SetupId, "CORE-QUOTE-006", sarah.Email!);
await EnsureRevisionImpactAsync(sixLineLead, mainQuote);

var sendRfq = await EnsureRfqAsync(backupLead, "CORE-RFQ-SEND-001");
var sendQuote = await EnsureQuoteAsync(sendRfq, draftStatus.SetupId, "CORE-QUOTE-SEND-001", ahmed.Email!);
var exactAwardRfq = await EnsureRfqAsync(exactAwardLead, "CORE-RFQ-CLIENT-PO-EXACT-V2");
var exactAwardQuote = await EnsureQuoteAsync(exactAwardRfq, sentStatus.SetupId,
    "CORE-QUOTE-CLIENT-PO-EXACT-V2", sarah.Email!);
var partialAwardRfq = await EnsureRfqAsync(partialAwardLead, "CORE-RFQ-CLIENT-PO-PARTIAL-V2");
var partialAwardQuote = await EnsureQuoteAsync(partialAwardRfq, sentStatus.SetupId,
    "CORE-QUOTE-CLIENT-PO-PARTIAL-V2", sarah.Email!);
PrepareClientPoQuote(exactAwardQuote, currency.Id, 525m);
PrepareClientPoQuote(partialAwardQuote, currency.Id, 575m);
await db.SaveChangesAsync();
await EnsureSetupAsync("OrderStatus", "DRAFT", "Draft");
var orderStatus = await EnsureSetupAsync("OrderStatus", "CONFIRMED", "Confirmed");
var allocationOrder = await EnsureOrderAsync(mainQuote, mainRfq, sixLineLead, orderStatus.SetupId,
    sufficient, primaryWarehouse);
var sourcedCustomerOrderLineId = await EnsureSourcedCustomerOrderAsync(mainQuote, mainRfq, outOfStock,
    currency.Id);

var openFollowUp = await EnsureFollowUpRecordAsync(sarah.Id, mainQuote.Id, abc.Id, now.AddHours(8),
    80, "CORE_E2E_OPEN", "core-e2e-open-followup");
var completedFollowUp = await EnsureCompletedFollowUpAsync(sarah.Id, mainQuote.Id, abc.Id);

await db.SaveChangesAsync();
await PrintFixtureAsync();

async Task<BusinessUnit> EnsureTenantAsync(long id, string code, string name)
{
    var existing = await db.BusinessUnits.SingleOrDefaultAsync(x => x.Id == id);
    if (existing is not null) return existing;
    var value = new BusinessUnit { Id = id, BusinessUnitCode = code, BusinessUnitName = name, IsActive = true, CreatedBy = fixtureActor, CreatedOn = now };
    db.Add(value); await db.SaveChangesAsync(); return value;
}

async Task<PlatformModels.Plan> EnsurePlatformPlanAsync(
    string code, string name, int weight, int concurrency, int documents, int seats)
{
    var existing = await db.Set<PlatformModels.Plan>().SingleOrDefaultAsync(x => x.Code == code);
    if (existing is null)
    {
        existing = new PlatformModels.Plan { Code = code, CreatedOn = now };
        db.Add(existing);
    }
    existing.Name = name;
    existing.Weight = weight;
    existing.MaxConcurrentExtractionJobs = concurrency;
    existing.MaxDocsPerMonth = documents;
    existing.MaxSeats = seats;
    existing.Features = FullyLicensedEntitlements();
    existing.IsActive = true;
    await db.SaveChangesAsync();
    return existing;
}

async Task<PlatformModels.Tenant> EnsurePlatformTenantAsync(
    string slug, string name, long businessUnitId, long planId)
{
    var existing = await db.Set<PlatformModels.Tenant>().SingleOrDefaultAsync(x => x.Slug == slug);
    if (existing is null)
    {
        existing = new PlatformModels.Tenant { Slug = slug, CreatedBy = fixtureActor, CreatedOn = now };
        db.Add(existing);
    }
    existing.Name = name;
    existing.Status = PlatformModels.TenantStatus.Active;
    existing.PlanId = planId;
    existing.PrimaryBusinessUnitId = businessUnitId;
    existing.Entitlements = FullyLicensedEntitlements();
    await db.SaveChangesAsync();
    return existing;
}

string FullyLicensedEntitlements() => JsonSerializer.Serialize(
    TypedEntitlementCatalog.Keys.Order().ToDictionary(key => key, _ => true, StringComparer.Ordinal));

async Task<PlatformModels.PlatformUser> EnsurePlatformUserAsync(string email, string displayName)
{
    var existing = await db.Set<PlatformModels.PlatformUser>().SingleOrDefaultAsync(x => x.Email == email);
    if (existing is null)
    {
        existing = new PlatformModels.PlatformUser { Email = email, CreatedBy = fixtureActor, CreatedOn = now };
        db.Add(existing);
    }
    existing.DisplayName = displayName;
    existing.PlatformRole = PlatformModels.PlatformRole.Owner;
    existing.IsActive = true;
    if (string.IsNullOrWhiteSpace(existing.PasswordHash) ||
        !BCrypt.Net.BCrypt.Verify(password, existing.PasswordHash))
        existing.PasswordHash = BCrypt.Net.BCrypt.HashPassword(password);
    await db.SaveChangesAsync();
    return existing;
}

async Task<SetupMaster> EnsureRoleAsync(long bu, string code, string name, short rank)
{
    var existing = await db.SetupMasters.SingleOrDefaultAsync(x => x.BusinessUnitId == bu && x.SetupType == "Role" && x.SetupCode == code);
    if (existing is not null)
    {
        existing.SetupValue = name;
        existing.RoleRank = rank;
        existing.IsActive = true;
        await db.SaveChangesAsync();
        return existing;
    }
    var value = new SetupMaster { BusinessUnitId = bu, SetupType = "Role", SetupCode = code, SetupValue = name,
        RoleRank = rank, IsActive = true, CreatedBy = fixtureActor, CreatedOn = now };
    db.Add(value); await db.SaveChangesAsync(); return value;
}

async Task<User> EnsureUserAsync(long bu, long role, string email, string first, string last)
{
    var existing = await db.Users.SingleOrDefaultAsync(x => x.Buid == bu && x.Email == email);
    if (existing is not null)
    {
        existing.RoleId = role;
        existing.FirstName = first;
        existing.LastName = last;
        existing.IsActive = true;
        if (!BCrypt.Net.BCrypt.Verify(password, existing.PasswordHash))
            existing.PasswordHash = BCrypt.Net.BCrypt.HashPassword(password);
        await db.SaveChangesAsync();
        return existing;
    }
    var value = new User { Buid = bu, RoleId = role, Email = email, FirstName = first, LastName = last,
        PasswordHash = BCrypt.Net.BCrypt.HashPassword(password), ImageUrl = string.Empty, IsActive = true,
        CreatedBy = fixtureActor, CreatedOn = now, Timezone = "UTC", Region = "Acceptance" };
    db.Add(value); await db.SaveChangesAsync(); return value;
}

async Task<Module> EnsureModuleAsync(string name)
{
    var existing = await db.Modules.SingleOrDefaultAsync(x => x.ModuleName == name);
    if (existing is not null) return existing;
    var value = new Module { ModuleName = name, IsActive = true, CreatedBy = fixtureActor, CreatedOn = now };
    db.Add(value); await db.SaveChangesAsync(); return value;
}

async Task EnsurePermissionAsync(long bu, long role, long module, bool create, bool edit, bool view = true)
{
    var existing = await db.RolePermissions.SingleOrDefaultAsync(x => x.BusinessUnitId == bu && x.RoleId == role && x.ModuleId == module);
    if (existing is null)
    {
        db.Add(new RolePermission { BusinessUnitId = bu, RoleId = role, ModuleId = module, CanView = view, CanCreate = create,
            CanEdit = edit, CanDelete = false, CreatedBy = fixtureActor, CreatedOn = now });
        return;
    }
    existing.CanView = view;
    existing.CanCreate = existing.CanCreate == true || create;
    existing.CanEdit = existing.CanEdit == true || edit;
}

async Task<Customer> EnsureCustomerAsync(string name, string email)
{
    var existing = await db.Customers.SingleOrDefaultAsync(x => x.Buid == tenantId && x.Name == name);
    if (existing is not null) return existing;
    var value = new Customer { Name = name, ContactEmail = email, ImageUrl = string.Empty, Buid = tenantId,
        IsActive = true, CreatedBy = fixtureActor, CreatedOn = now };
    db.Add(value); await db.SaveChangesAsync(); return value;
}

async Task<Contact> EnsureContactAsync(long customerId, string first, string last, string email)
{
    var existing = await db.Contacts.SingleOrDefaultAsync(x => x.BusinessUnitId == tenantId && x.CustomerId == customerId && x.Email == email);
    if (existing is not null) return existing;
    var value = new Contact { BusinessUnitId = tenantId, CustomerId = customerId, FirstName = first, LastName = last,
        Email = email, Position = "Procurement Manager", IsPrimary = true, IsActive = true, CreatedBy = fixtureActor, CreatedOn = now };
    db.Add(value); await db.SaveChangesAsync(); return value;
}

async Task<EmailConfiguration> EnsureEmailConfigurationAsync()
{
    var existing = await db.EmailConfigurations.SingleOrDefaultAsync(x => x.BusinessUnitId == tenantId && x.ConfigurationName == "Release 01C1 fixture");
    if (existing is not null) return existing;
    var value = new EmailConfiguration { BusinessUnitId = tenantId, ConfigurationName = "Release 01C1 fixture",
        EmailAddress = "intake@release01c1.local", Protocol = "IMAP", Host = "localhost", Port = 993,
        Username = "fixture", Password = "fixture-not-used", UseSsl = true, PollingInterval = 300,
        IsActive = true, CreatedOn = now };
    db.Add(value); await db.SaveChangesAsync(); return value;
}

async Task<EmailIngest> EnsureEmailIngestAsync(long configurationId)
{
    var existing = await db.EmailIngests.SingleOrDefaultAsync(x => x.EmailConfigurationId == configurationId && x.MessageId == "release-01c1-fixture");
    if (existing is not null) return existing;
    var value = new EmailIngest { MessageId = "release-01c1-fixture", EmailSubject = "Controlled reconciliation batch",
        FromEmail = "buyer@northstar.local", EmailConfigurationId = configurationId, ParseStatus = "Success",
        ParsedAt = now, CreatedOn = now };
    db.Add(value); await db.SaveChangesAsync(); return value;
}

async Task<Lead> EnsureIdentityLeadAsync(string rfq, string buyer, string email, long owner, Guid batch,
    string key, string file, params (string line, string part, string description, int qty)[] lines)
{
    var existing = await db.Leads.SingleOrDefaultAsync(x => x.BusinessUnitId == tenantId && x.Rfqno == rfq);
    if (existing is not null) return existing;
    var corpus = DocumentCorpus.Create(tenantId, batch, CorpusSourceType.ManualUpload);
    db.Add(corpus); await db.SaveChangesAsync();
    var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{batch:N}:{key}:{file}"))).ToLowerInvariant();
    var source = SourceDocument.Create(tenantId, corpus.Id, hash, file, "text/csv", "acceptance",
        $"core-acceptance/{file}", "v1", 256);
    source.MarkSecurityStatus(DocumentSecurityStatus.Cleared);
    db.Add(source); await db.SaveChangesAsync();
    var occurrence = SourceDocumentOccurrence.Create(tenantId, source.Id, corpus.Id, key,
        "{\"fixture\":\"core-commercial-intelligence\"}");
    db.Add(occurrence); await db.SaveChangesAsync();
    var candidate = new Lead { Rfqno = rfq, BuyersName = buyer, RecDate = now, BidClosingDate = now.AddDays(5),
        LeadSource = "ManualUpload", CreatedBy = fixtureActor, CreatedDate = now, BusinessUnitId = tenantId,
        EmailIngestsId = intake.Id, Clientemail = email, RequiresCommercialReview = true, AssignTo = owner, AssignOn = now };
    foreach (var line in lines)
        candidate.LeadItems.Add(new LeadItem { LineItemNo = line.line, ManufacturerPartNumber = line.part,
            ProductShortDescription = line.description, ItemText = line.description, Quantity = line.qty,
            UnitOfMeasure = line.part == "FIELD-SERVICE" ? "JOB" : "EA",
            CommodityProduct = line.part == "FIELD-SERVICE" ? "SERVICE_OR_NON_INVENTORY" : "PRODUCT" });
    var descriptor = new LeadIntakeDescriptor(batch, "ManualUpload", key, null, null, fixtureActor, email,
        $"Acceptance RFQ {rfq}", file, source.DetectedMimeType, source.ByteSize, source.ContentHash, source.Id,
        null, occurrence.ReceivedOn, DateTimeOffset.UtcNow, LeadProcessingPath.Deterministic, false, 0m,
        "User", fixtureActor, $"core-acceptance:{key}") { SourceDocumentOccurrenceId = occurrence.Id };
    var result = await new LeadIdentityApplicationService(db).ReconcileAsync(candidate, descriptor);
    return await db.Leads.SingleAsync(x => x.Id == result.LeadId);
}

async Task<Team> EnsureTeamAsync(string name, long managerId)
{
    var existing = await db.Teams.SingleOrDefaultAsync(x => x.BusinessUnitId == tenantId && x.TeamName == name);
    if (existing is not null)
    {
        existing.ManagerId = managerId;
        await db.SaveChangesAsync();
        return existing;
    }
    var value = new Team { BusinessUnitId = tenantId, TeamName = name, ManagerId = managerId, CreatedBy = fixtureActor, CreatedOn = now };
    db.Add(value); await db.SaveChangesAsync(); return value;
}

async Task EnsureSalesRepAsync(long userId, int capacity, decimal weight, string[] territories, string[] categories)
{
    var existing = await db.SalesRepProfiles.SingleOrDefaultAsync(x => x.BusinessUnitId == tenantId && x.UserId == userId);
    if (existing is not null) return;
    db.Add(new SalesRepProfile { BusinessUnitId = tenantId, UserId = userId, IsRoutingEligible = true,
        CapacityPercent = capacity, DistributionWeight = weight, TerritoryKeys = territories,
        ProductCategoryKeys = categories, EffectiveFromUtc = now.AddDays(-30), UpdatedAtUtc = now,
        UpdatedBy = fixtureActor, LastMutationIdempotencyKey = $"core-profile-{userId}" });
    await db.SaveChangesAsync();
}

async Task EnsureMembershipAsync(long userId, long teamId, bool primary)
{
    var user = await db.Users.SingleAsync(x => x.Buid == tenantId && x.Id == userId);
    user.TeamId = teamId;
    if (!await db.SalesTeamMemberships.AnyAsync(x => x.BusinessUnitId == tenantId && x.UserId == userId && x.TeamId == teamId && x.EffectiveToUtc == null))
        db.Add(new SalesTeamMembership { BusinessUnitId = tenantId, UserId = userId, TeamId = teamId,
            IsPrimary = primary, EffectiveFromUtc = now.AddDays(-30) });
    await db.SaveChangesAsync();
}

async Task EnsureCustomerIdentifierAsync(long customerId, CustomerIdentifierType type, string normalized, string display)
{
    if (await db.Set<CustomerIdentifier>().AnyAsync(x => x.BusinessUnitId == tenantId && x.CustomerId == customerId && x.IdentifierType == type && x.NormalizedValue == normalized && x.EffectiveTo == null)) return;
    db.Add(new CustomerIdentifier { BusinessUnitId = tenantId, CustomerId = customerId, IdentifierType = type,
        NormalizedValue = normalized, DisplayValue = display, IsVerified = true, Confidence = 1m,
        Source = fixtureActor, EffectiveFrom = now.AddDays(-30) });
    await db.SaveChangesAsync();
}

async Task<CustomerOwnership> EnsureOwnershipAsync(long customerId, long primaryId, long backupId)
{
    var existing = await db.Set<CustomerOwnership>().SingleOrDefaultAsync(x => x.BusinessUnitId == tenantId && x.CustomerId == customerId && x.Scope == OwnershipScope.GeneralCustomer && x.IsActive && x.EffectiveTo == null);
    if (existing is not null) return existing;
    var value = new CustomerOwnership { BusinessUnitId = tenantId, CustomerId = customerId, PrimaryUserId = primaryId,
        BackupUserId = backupId, Scope = OwnershipScope.GeneralCustomer, Priority = 100, EffectiveFrom = now.AddDays(-30),
        IsActive = true, Source = fixtureActor, Reason = "Confirmed synthetic account ownership", Version = 1 };
    db.Add(value); await db.SaveChangesAsync(); return value;
}

async Task EnsureAssignmentAsync(long leadId, long customerId, long ownershipId, long selectedUserId,
    RoutingOutcome outcome, string decisionCode, string explanation, string key)
{
    if (await db.Set<LeadAssignment>().AnyAsync(x => x.BusinessUnitId == tenantId && x.LeadId == leadId && x.EffectiveTo == null)) return;
    var decision = new LeadRoutingDecision { BusinessUnitId = tenantId, LeadId = leadId, CustomerId = customerId,
        OwnershipId = ownershipId, SuggestedUserId = selectedUserId, SelectedUserId = selectedUserId,
        MatchStatus = CustomerMatchStatus.Matched, Outcome = outcome, MatchConfidence = 1m,
        DecisionCode = decisionCode, Explanation = System.Text.Json.JsonSerializer.Serialize(new { summary = explanation,
            accountOwner = "Sarah Malik", opportunityOwner = selectedUserId == sarah.Id ? "Sarah Malik" : "Ahmed Khan" }),
        PolicyVersion = "core-acceptance-v1", CorrelationId = key, IdempotencyKey = key, CreatedOn = now };
    db.Add(decision); await db.SaveChangesAsync();
    db.Add(new LeadAssignment { BusinessUnitId = tenantId, LeadId = leadId, ToUserId = selectedUserId,
        AssignmentScope = selectedUserId == sarah.Id ? AssignmentScope.CustomerPermanent : AssignmentScope.SharedBackup,
        OwnershipId = ownershipId, RoutingDecisionId = decision.Id, ReasonCode = decisionCode, Comment = explanation,
        EffectiveFrom = now, AssignedByUserId = manager.Id, CorrelationId = key, IdempotencyKey = $"{key}-assignment" });
    await db.SaveChangesAsync();
}

async Task EnsureWorkloadLeadAsync(string rfq, long owner, int lineCount, DateTime deadline)
{
    if (await db.Leads.AnyAsync(x => x.BusinessUnitId == tenantId && x.Rfqno == rfq)) return;
    var batchBytes = SHA256.HashData(Encoding.UTF8.GetBytes($"core-workload:{rfq}"))[..16];
    var lines = Enumerable.Range(1, lineCount)
        .Select(i => (i.ToString(), $"WORK-{owner}-{i}", "Synthetic weighted-routing workload", 1))
        .ToArray();
    var lead = await EnsureIdentityLeadAsync(rfq, "Synthetic Workload", $"workload-{owner}@acceptance.local",
        owner, new Guid(batchBytes), $"core-workload-{owner}", $"{rfq.ToLowerInvariant()}.csv", lines);
    lead.BidClosingDate = deadline;
    await db.SaveChangesAsync();
}

async Task EnsureFollowUpAsync(long userId, long aggregateId, long customerId, DateTime due, int priority, string purpose)
{
    var key = $"core-followup-{purpose.ToLowerInvariant()}";
    if (await db.FollowUpTasks.AnyAsync(x => x.BusinessUnitId == tenantId && x.CreationIdempotencyKey == key)) return;
    db.Add(new FollowUpTask { BusinessUnitId = tenantId, AssignedToUserId = userId, AggregateType = "Lead",
        AggregateId = aggregateId, CustomerId = customerId, DueAtUtc = due, Status = FollowUpStatus.Open,
        Priority = priority, PurposeCode = purpose, CreatedAtUtc = now.AddDays(-1), UpdatedAtUtc = now,
        CreatedBy = fixtureActor, CorrelationId = key, CreationIdempotencyKey = key });
    await db.SaveChangesAsync();
}

async Task<Supplier> EnsureSupplierAsync(string name, string email)
{
    var existing = await db.Suppliers.SingleOrDefaultAsync(x => x.Buid == tenantId && x.Name == name);
    if (existing is not null)
    {
        existing.GovernanceStatus = SupplierGovernanceStatuses.Approved;
        existing.VerificationStatus = SupplierVerificationStatuses.Verified;
        existing.ComplianceStatus = SupplierComplianceStatuses.Cleared;
        existing.RiskStatus = SupplierRiskStatuses.Low;
        existing.ReadinessStatus = SupplierReadinessStatuses.Ready;
        await db.SaveChangesAsync();
        return existing;
    }
    var value = new Supplier { Buid = tenantId, Name = name, ContactEmail = email, ImageUrl = string.Empty,
        PaymentTerms = "Net 30", SuccessRate = 94, AvgResponseTime = 8, IsActive = true, CreatedBy = fixtureActor, CreatedOn = now };
    value.GovernanceStatus = SupplierGovernanceStatuses.Approved;
    value.VerificationStatus = SupplierVerificationStatuses.Verified;
    value.ComplianceStatus = SupplierComplianceStatuses.Cleared;
    value.RiskStatus = SupplierRiskStatuses.Low;
    value.ReadinessStatus = SupplierReadinessStatuses.Ready;
    db.Add(value); await db.SaveChangesAsync(); return value;
}

async Task<Currency> EnsureCurrencyAsync(string code, string name, string symbol)
{
    var existing = await db.Currencies.SingleOrDefaultAsync(x =>
        x.BusinessUnitId == tenantId && x.Code == code);
    if (existing is not null) return existing;
    var value = new Currency
    {
        BusinessUnitId = tenantId,
        Code = code,
        CurrencyName = name,
        Symbol = symbol,
        ExchangeRate = 1,
        IsBaseCurrency = true,
        IsActive = true,
        CreatedBy = fixtureActor,
        CreatedOn = now
    };
    db.Add(value);
    await db.SaveChangesAsync();
    return value;
}

async Task<ProductCategory> EnsureProductCategoryAsync(string name)
{
    var existing = await db.ProductCategories.SingleOrDefaultAsync(x => x.BusinessUnitId == tenantId && x.CategoryName == name);
    if (existing is not null) return existing;
    var value = new ProductCategory { BusinessUnitId = tenantId, CategoryName = name, Description = "Synthetic acceptance catalog",
        IsActive = true, CreatedBy = fixtureActor, CreatedOn = now };
    db.Add(value); await db.SaveChangesAsync(); return value;
}

async Task<Product> EnsureProductAsync(string part, string name, long categoryId, int leadTime, long supplierId)
{
    var existing = await db.Products.SingleOrDefaultAsync(x => x.Buid == tenantId && x.PartNo == part);
    if (existing is not null) return existing;
    var value = new Product { Buid = tenantId, PartNo = part, ProductName = name, Description = name,
        CategoryId = categoryId, QtyOnHand = 0, ReorderPoint = 8, LeadTime = leadTime, PreferredSupplierId = supplierId,
        IsActive = true, IsCatalogItem = true, UnitCost = 100, SellingPrice = 135, CreatedBy = fixtureActor, CreatedOn = now };
    db.Add(value); await db.SaveChangesAsync(); return value;
}

async Task EnsureAliasAsync(long productId, ProductAliasKind kind, string value, long? accountId)
{
    var normalized = ProductIdentityNormalizer.NormalizePartNumber(value)!;
    if (await db.ProductAliases.AnyAsync(x => x.BusinessUnitId == tenantId && x.Kind == kind && x.NormalizedValue == normalized && x.AccountId == accountId)) return;
    db.Add(new ProductAlias { BusinessUnitId = tenantId, ProductId = productId, Kind = kind, Value = value,
        NormalizedValue = normalized, AccountId = accountId, IsActive = true, CreatedOn = now, CreatedBy = fixtureActor });
    await db.SaveChangesAsync();
}

async Task<Warehouse> EnsureWarehouseAsync(string code, string name, string location)
{
    var existing = await db.Warehouses.SingleOrDefaultAsync(x => x.BusinessUnitId == tenantId && x.WarehouseCode == code);
    if (existing is not null) return existing;
    var value = new Warehouse { BusinessUnitId = tenantId, WarehouseCode = code, WarehouseName = name,
        Location = location, Country = "Test", IsActive = true, CreatedBy = fixtureActor, CreatedOn = now };
    db.Add(value); await db.SaveChangesAsync(); return value;
}

async Task<Models.Inventory> EnsureInventoryAsync(Product product, Warehouse warehouse, decimal onHand, decimal reorder, decimal safety)
{
    var existing = await db.Set<Models.Inventory>().SingleOrDefaultAsync(x => x.Buid == tenantId && x.ProductId == product.Id && x.WarehouseId == warehouse.Id);
    if (existing is not null) return existing;
    var value = new Models.Inventory { Buid = tenantId, ProductId = product.Id, WarehouseId = warehouse.Id,
        PartNo = product.PartNo, ProductName = product.ProductName, Description = product.Description,
        QtyOnHand = onHand, ReorderPoint = reorder, SafetyStockQuantity = safety, UnitCost = product.UnitCost,
        SellingPrice = product.SellingPrice, LeadTime = product.LeadTime, PreferredSupplierId = product.PreferredSupplierId,
        CreatedBy = fixtureActor, CreatedOn = now };
    db.Add(value); await db.SaveChangesAsync(); return value;
}

async Task EnsureMovementAsync(Product product, Models.Inventory inventory, Warehouse warehouse,
    InventoryMovementType type, decimal quantity, string key)
{
    if (await db.InventoryMovements.AnyAsync(x => x.BusinessUnitId == tenantId && x.IdempotencyKey == key)) return;
    db.Add(new InventoryMovement { BusinessUnitId = tenantId, ProductId = product.Id, InventoryId = inventory.Id,
        WarehouseId = warehouse.Id, Type = type, Quantity = quantity, OccurredOn = now.AddDays(-3), IdempotencyKey = key,
        SourceType = "AcceptanceFixture", SourceId = key, Reason = "Authorized synthetic acceptance balance",
        CreatedBy = fixtureActor, CreatedOn = now });
    await db.SaveChangesAsync();
}

async Task EnsureReservationAsync(long inventoryId, decimal quantity, string key)
{
    if (await db.StockReservations.AnyAsync(x => x.BusinessUnitId == tenantId && x.IdempotencyKey == key)) return;
    db.Add(new StockReservation { BusinessUnitId = tenantId, InventoryId = inventoryId, Quantity = quantity,
        Status = StockReservationStatus.Active, IdempotencyKey = key, CreatedBy = fixtureActor, CreatedOn = now.AddHours(-6) });
    await db.SaveChangesAsync();
}

async Task EnsureIncomingAsync(long productId, long inventoryId, long warehouseId, decimal ordered,
    decimal received, decimal allocated, DateOnly expected, string sourceId)
{
    if (await db.IncomingInventory.AnyAsync(x => x.BusinessUnitId == tenantId && x.SourceType == "PurchaseOrder" && x.SourceId == sourceId && x.ProductId == productId && x.WarehouseId == warehouseId)) return;
    db.Add(new IncomingInventory { BusinessUnitId = tenantId, ProductId = productId, InventoryId = inventoryId,
        WarehouseId = warehouseId, OrderedQuantity = ordered, ReceivedQuantity = received, AllocatedQuantity = allocated,
        ExpectedOn = expected, Status = IncomingInventoryStatus.Confirmed, SourceType = "PurchaseOrder", SourceId = sourceId });
    await db.SaveChangesAsync();
}

async Task EnsurePurchaseHistoryAsync(long productId, long supplierId, decimal quantity, decimal price, string currency, string reference)
{
    if (await db.SupplierPurchaseHistories.AnyAsync(x => x.ProductId == productId && x.SupplierId == supplierId && x.PoDocId == reference)) return;
    db.Add(new SupplierPurchaseHistory { ProductId = productId, SupplierId = supplierId, PurchaseDate = now.AddDays(-45),
        Quantity = quantity, UnitPrice = price, Currency = currency, PoDocId = reference, CreatedBy = fixtureActor, CreatedOn = now });
    await db.SaveChangesAsync();
}

async Task EnsureSupplierQuoteAsync(long supplierId, string itemName, decimal quantity, decimal price, string reference)
{
    if (await db.SupplierQuotedItems.AnyAsync(x => x.BusinessUnitId == tenantId && x.QuoteReference == reference)) return;
    db.Add(new SupplierQuotedItem { BusinessUnitId = tenantId, SupplierId = supplierId, ItemName = itemName,
        Description = $"Synthetic sourcing evidence for {itemName}", Quantity = quantity, UnitPrice = price,
        QuoteReference = reference, QuoteDate = now.AddDays(-15), ValidUntil = now.AddDays(30), IsActive = true,
        CreatedBy = fixtureActor, CreatedDate = now.AddDays(-15) });
    await db.SaveChangesAsync();
}

async Task<long> EnsureNegotiationSupplierQuoteAsync(
    Rfq rfq, Product product, Supplier quoteSupplier, Currency quoteCurrency)
{
    const string quoteReference = "CORE-SQ-NEGOTIATION-V24";
    var rfqItem = rfq.Rfqitems.Single(x => x.ProductId == product.Id);
    var expectedIdentityKey = $"rfq:{rfq.Id}:line:{rfqItem.Id}";
    var demandLine = await db.CommercialDemandLines.SingleOrDefaultAsync(x =>
        x.BusinessUnitId == tenantId && x.RfqItemId == rfqItem.Id);
    if (demandLine is null)
    {
        demandLine = new CommercialDemandLine
        {
            BusinessUnitId = tenantId,
            RfqId = rfq.Id,
            RfqItemId = rfqItem.Id,
            NexoraSerial = rfq.NexoraSerial!,
            IdentityKey = $"rfq:{rfq.Id}:line:{rfqItem.Id}",
            CreatedOn = now,
            CreatedBy = fixtureActor
        };
        db.CommercialDemandLines.Add(demandLine);
        await db.SaveChangesAsync();
    }
    else if (demandLine.RfqId != rfq.Id || demandLine.NexoraSerial != rfq.NexoraSerial ||
        demandLine.IdentityKey != expectedIdentityKey)
    {
        throw new InvalidOperationException(
            $"Acceptance fixture Demand Line {demandLine.Id} does not match RFQ {rfq.Id} and its Nexora Serial.");
    }

    var sourcingCase = await db.SourcingCases.SingleOrDefaultAsync(x =>
        x.BusinessUnitId == tenantId && x.CommercialDemandLineId == demandLine.Id);
    if (sourcingCase is null)
    {
        sourcingCase = new SourcingCase
        {
            BusinessUnitId = tenantId,
            CommercialDemandLineId = demandLine.Id,
            RfqId = rfq.Id,
            RfqItemId = rfqItem.Id,
            LeadId = rfq.LeadId,
            CustomerId = rfq.CustomerId,
            ProductId = product.Id,
            NexoraSerial = rfq.NexoraSerial!,
            RequestedPartNumber = product.PartNo,
            Description = rfqItem.ProductShortDescription ?? product.ProductName ?? product.PartNo ?? "Acceptance product",
            UnitOfMeasure = rfqItem.UnitOfMeasure ?? "EA",
            RequestedQuantity = rfqItem.Quantity
                ?? throw new InvalidOperationException("Acceptance RFQ item quantity is required."),
            StockQuantity = 0,
            UnfulfilledQuantity = rfqItem.Quantity
                ?? throw new InvalidOperationException("Acceptance RFQ item quantity is required."),
            SearchLimit = 10,
            Status = SourcingCaseStatuses.ComparisonReady,
            NextAction = "Review supplier bid quality and prepare negotiation",
            ShortageDecisionKey = "core-v24-shortage-decision",
            IdempotencyKey = "core-v24-sourcing-case",
            RequestHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("core-v24-sourcing-case"))),
            CreatedOn = now,
            CreatedBy = fixtureActor,
            UpdatedOn = now,
            UpdatedBy = fixtureActor
        };
        db.SourcingCases.Add(sourcingCase);
        await db.SaveChangesAsync();
    }
    else if (sourcingCase.RfqId != rfq.Id || sourcingCase.RfqItemId != rfqItem.Id ||
        sourcingCase.NexoraSerial != rfq.NexoraSerial ||
        sourcingCase.CommercialDemandLineId != demandLine.Id)
    {
        throw new InvalidOperationException(
            $"Acceptance fixture Sourcing Case {sourcingCase.Id} does not match the canonical Demand Line.");
    }

    var solicitation = await db.Set<SupplierSolicitation>().SingleOrDefaultAsync(x =>
        x.BusinessUnitId == tenantId && x.SourcingCaseId == sourcingCase.Id &&
        x.SupplierId == quoteSupplier.Id);
    if (solicitation is null)
    {
        solicitation = new SupplierSolicitation
        {
            BusinessUnitId = tenantId,
            RfqId = rfq.Id,
            SupplierId = quoteSupplier.Id,
            SourcingCaseId = sourcingCase.Id,
            CommercialDemandLineId = demandLine.Id,
            NexoraSerial = rfq.NexoraSerial!,
            SupplierRfqNumber = "CORE-SRFQ-NEGOTIATION-V24",
            IdempotencyKey = "core-v24-supplier-solicitation",
            RequestHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("core-v24-supplier-solicitation"))),
            RequestedRfqItemIdsJson = $"[{rfqItem.Id}]",
            Status = SolicitationStatus.Responded,
            SentOn = now.AddDays(-3),
            RespondedOn = now.AddDays(-2),
            CreatedOn = now.AddDays(-3),
            UpdatedOn = now.AddDays(-2)
        };
        db.Add(solicitation);
        await db.SaveChangesAsync();
    }
    else if (solicitation.RfqId != rfq.Id || solicitation.SupplierId != quoteSupplier.Id ||
        solicitation.CommercialDemandLineId != demandLine.Id ||
        solicitation.NexoraSerial != rfq.NexoraSerial)
    {
        throw new InvalidOperationException(
            $"Acceptance fixture Supplier RFQ {solicitation.Id} does not match the canonical commercial journey.");
    }

    var existingQuote = await db.SupplierQuotes.AsNoTracking()
        .Include(x => x.Revisions).ThenInclude(x => x.Lines)
        .SingleOrDefaultAsync(x => x.BusinessUnitId == tenantId &&
            x.SupplierQuoteReference == quoteReference);
    if (existingQuote is not null)
    {
        var currentRevision = existingQuote.Revisions.SingleOrDefault(x =>
            x.RevisionNumber == existingQuote.CurrentRevisionNumber);
        if (existingQuote.SupplierId != quoteSupplier.Id || existingQuote.RfqId != rfq.Id ||
            existingQuote.SupplierSolicitationId != solicitation.Id ||
            existingQuote.SourcingCaseId != sourcingCase.Id ||
            existingQuote.NexoraSerial != rfq.NexoraSerial || currentRevision is null ||
            currentRevision.Lines.Any(x => x.RfqItemId != rfqItem.Id ||
                x.CommercialDemandLineId != demandLine.Id))
        {
            throw new InvalidOperationException(
                $"Acceptance fixture Supplier Quote {existingQuote.Id} has stale or conflicting lineage.");
        }
        return existingQuote.Id;
    }

    var sourceIdentity = "acceptance-fixture:core-sq-negotiation-v24";
    var sourceHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sourceIdentity)));
    var result = await new SupplierQuoteInboxService(db).CaptureAsync(new ERP_RFQ_Automation.SupplierQuotes.CaptureSupplierQuoteCommand(
        tenantId, quoteSupplier.Id, solicitation.Id, sourcingCase.Id, rfq.NexoraSerial!,
        quoteReference, 1, SupplierQuoteCaptureChannels.Manual, null, sourceIdentity, sourceHash,
        // Duty stays zero against an FCA term on purpose: the fixture is what the cost-completeness
        // warning fires on, so acceptance walks a buyer past the very case the panel named.
        quoteCurrency.Id, now.AddDays(30), "FCA", 30m, 0m, 0m, 0m, 0m, "Net 30",
        "Authorized synthetic quote for authenticated V2.4 browser acceptance.",
        new[]
        {
            new ERP_RFQ_Automation.SupplierQuotes.CaptureSupplierQuoteLine(1, rfqItem.Id, demandLine.Id, product.PartNo, null,
                "PCS-TX-300", product.ProductName ?? product.PartNo ?? "Acceptance product",
                rfqItem.Quantity
                    ?? throw new InvalidOperationException("Acceptance RFQ item quantity is required."),
                rfqItem.Quantity,
                rfqItem.UnitOfMeasure ?? "EA", 452m, 4m, 14, "IN_STOCK", "US", "12 months",
                false, null, Array.Empty<CaptureSupplierQuoteEvidence>())
        },
        Array.Empty<CaptureSupplierQuoteEvidence>(), "core-v24-supplier-quote-revision-1",
        fixtureActor, "core-v24-browser-acceptance"));
    return result.SupplierQuoteId;
}

async Task<Lead> EnsureReconciledOccurrenceAsync(Lead canonicalLead, Guid batchId, string key, string file,
    params (string line, string part, string description, int qty)[] lines)
{
    var existing = await db.Set<LeadIngestionOccurrence>().AsNoTracking()
        .SingleOrDefaultAsync(x => x.BusinessUnitId == tenantId && x.IdempotencyKey == key);
    if (existing?.LeadId is long existingLeadId)
        return await db.Leads.SingleAsync(x => x.Id == existingLeadId);

    var corpus = DocumentCorpus.Create(tenantId, batchId, CorpusSourceType.ManualUpload);
    db.Add(corpus); await db.SaveChangesAsync();
    var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{batchId:N}:{key}:{file}"))).ToLowerInvariant();
    var source = SourceDocument.Create(tenantId, corpus.Id, hash, file, "text/csv", "acceptance",
        $"core-acceptance/{file}", "v1", 512);
    source.MarkSecurityStatus(DocumentSecurityStatus.Cleared);
    db.Add(source); await db.SaveChangesAsync();
    var documentOccurrence = SourceDocumentOccurrence.Create(tenantId, source.Id, corpus.Id, key,
        "{\"fixture\":\"core-commercial-e2e\"}");
    db.Add(documentOccurrence); await db.SaveChangesAsync();

    var candidate = new Lead { Rfqno = canonicalLead.Rfqno, BuyersName = canonicalLead.BuyersName,
        RecDate = canonicalLead.RecDate, BidClosingDate = canonicalLead.BidClosingDate,
        LeadSource = "ManualUpload", CreatedBy = fixtureActor, CreatedDate = now,
        BusinessUnitId = tenantId, EmailIngestsId = intake.Id, Clientemail = canonicalLead.Clientemail,
        RequiresCommercialReview = true, AssignTo = canonicalLead.AssignTo, AssignOn = canonicalLead.AssignOn };
    foreach (var line in lines)
        candidate.LeadItems.Add(new LeadItem { LineItemNo = line.line, ManufacturerPartNumber = line.part,
            ProductShortDescription = line.description, ItemText = line.description, Quantity = line.qty,
            UnitOfMeasure = line.part == "FIELD-SERVICE" ? "JOB" : "EA",
            CommodityProduct = line.part == "FIELD-SERVICE" ? "SERVICE_OR_NON_INVENTORY" : "PRODUCT" });
    var descriptor = new LeadIntakeDescriptor(batchId, "ManualUpload", key, null, null, fixtureActor,
        canonicalLead.Clientemail, $"Acceptance revision {canonicalLead.Rfqno}", file,
        source.DetectedMimeType, source.ByteSize, source.ContentHash, source.Id, null,
        documentOccurrence.ReceivedOn, DateTimeOffset.UtcNow, LeadProcessingPath.Deterministic,
        false, 0m, "User", fixtureActor, $"core-acceptance:{key}")
        { SourceDocumentOccurrenceId = documentOccurrence.Id };
    var result = await new LeadIdentityApplicationService(db).ReconcileAsync(candidate, descriptor);
    return await db.Leads.SingleAsync(x => x.Id == result.LeadId);
}

async Task<Lead> EnsureSixLineCopyAsync(string rfqNumber, string key, Guid batchId)
{
    var lead = await EnsureIdentityLeadAsync(rfqNumber, "Amira Cole", "procurement@abc-engineering.local",
        sarah.Id, batchId, key, $"{key}.csv",
        ("1", "CORE-ATP-100", "Known valve actuator with sufficient ATP", 10),
        ("2", "CORE-PARTIAL-200", "Known regulator with partial ATP", 22),
        ("3", "CORE-OOS-300", "Known transmitter with supplier history and zero ATP", 12),
        ("4", "CORE-INCOMING-400", "Known controller with confirmed incoming inventory", 15),
        ("5", "X-UNKNOWN-900", "Unknown industrial component requiring related-resource search", 5),
        ("6", "FIELD-SERVICE", "On-site commissioning service, non-inventory", 1));
    await db.Entry(lead).Collection(x => x.LeadItems).LoadAsync();
    foreach (var item in lead.LeadItems)
        item.Currency ??= "USD";
    EnsureCommercialIdentity(lead, abc.Id, abcContact.Id, "MATCHED");
    lead.AssignTo = sarah.Id;
    lead.AssignOn ??= now;
    lead.AssignComment = "Confirmed ABC Engineering account owner available within capacity.";
    await db.SaveChangesAsync();
    return lead;
}

async Task EnsurePromotionEvidenceAsync(Lead lead)
{
    await db.Entry(lead).Collection(x => x.LeadItems).LoadAsync();
    var revision = await db.Set<LeadRevision>().AsNoTracking()
        .SingleAsync(x => x.BusinessUnitId == tenantId && x.Id == lead.CurrentRevisionId!.Value);
    var occurrence = await db.Set<LeadIngestionOccurrence>().AsNoTracking()
        .SingleAsync(x => x.BusinessUnitId == tenantId && x.Id == revision.EstablishedByOccurrenceId);
    var originalDocument = await db.Set<SourceDocument>().AsNoTracking()
        .SingleAsync(x => x.BusinessUnitId == tenantId && x.Id == occurrence.SourceDocumentId!.Value);

    var csv = new StringBuilder("Line,Part,Description,Quantity,UOM,Currency\n");
    foreach (var item in lead.LeadItems.GroupBy(x => x.Id).Select(x => x.First()).OrderBy(x => x.LineItemNo))
        csv.AppendLine($"{item.LineItemNo},{item.ManufacturerPartNumber},{item.ProductShortDescription},{item.Quantity},{item.UnitOfMeasure},{item.Currency ?? "USD"}");
    var bytes = Encoding.UTF8.GetBytes(csv.ToString());
    var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    var relativePath = $"acceptance/{tenantId}/{hash}.csv";
    var storageRoot = Argument("storage-root")
        ?? Environment.GetEnvironmentVariable("NEXORA_ACCEPTANCE_STORAGE_ROOT")
        ?? Path.Combine(Path.GetTempPath(), "nexora-acceptance-storage");
    var physicalPath = Path.Combine(storageRoot, relativePath);
    Directory.CreateDirectory(Path.GetDirectoryName(physicalPath)!);
    await File.WriteAllBytesAsync(physicalPath, bytes);

    var job = await db.Set<ExtractionJob>().SingleOrDefaultAsync(x =>
        x.BusinessUnitId == tenantId && x.ContentHash == hash && x.ResultLeadId == lead.Id);
    if (job is null)
    {
        job = new ExtractionJob
        {
            BatchId = occurrence.BatchId,
            BusinessUnitId = tenantId,
            SourceType = ExtractionSourceType.ManualUpload,
            ContentHash = hash,
            StoragePath = relativePath,
            FileName = $"{lead.Rfqno}-evidence.csv",
            FileType = "csv",
            Status = ExtractionStatus.Succeeded,
            Priority = 0,
            SchedulerTag = 0,
            Attempts = 1,
            MaxAttempts = 5,
            NextAttemptAt = now,
            ResultLeadId = lead.Id,
            CreatedOn = now,
            UpdatedOn = now
        };
        db.Add(job);
        await db.SaveChangesAsync();
    }

    var document = await db.Set<SourceDocument>().SingleOrDefaultAsync(x =>
        x.BusinessUnitId == tenantId && x.ContentHash == hash);
    if (document is null)
    {
        document = SourceDocument.Create(tenantId, originalDocument.CorpusId, hash,
            $"{lead.Rfqno}-evidence.csv", "text/csv", "local", relativePath, hash, bytes.Length);
        document.ReleaseFromQuarantine("local", relativePath, hash);
        document.BindExtractionJob(job.Id);
        db.Add(document);
        await db.SaveChangesAsync();
    }
    else if (!document.ExtractionJobId.HasValue)
    {
        document.BindExtractionJob(job.Id);
        await db.SaveChangesAsync();
    }

    if (!await db.Set<LeadOccurrenceDocument>().AnyAsync(x => x.BusinessUnitId == tenantId
            && x.OccurrenceId == occurrence.Id && x.SourceDocumentId == document.Id))
    {
        db.Add(new LeadOccurrenceDocument
        {
            BusinessUnitId = tenantId,
            OccurrenceId = occurrence.Id,
            SourceDocumentId = document.Id,
            Role = "Primary",
            Ordinal = 99,
            LinkedAtUtc = DateTimeOffset.UtcNow
        });
    }

    if (await db.Set<FieldEvidence>().AnyAsync(x => x.BusinessUnitId == tenantId
            && x.LineItem != null && x.LineItem.LeadItemId != null
            && lead.LeadItems.Select(item => item.Id).Contains(x.LineItem.LeadItemId.Value)))
    {
        await db.SaveChangesAsync();
        return;
    }

    var run = await db.Set<ExtractionRun>().SingleOrDefaultAsync(x =>
        x.BusinessUnitId == tenantId && x.SourceDocumentId == document.Id && x.AttemptNumber == 1);
    var runId = run?.RunId ?? Guid.NewGuid();
    if (run is null)
    {
        run = ExtractionRun.Create(tenantId, document.Id, runId, job.Id, 1,
            "acceptance-csv/v1", "lead-evidence/v1");
        db.Add(run);
    }
    var page = await db.Set<DocumentPage>().SingleOrDefaultAsync(x =>
        x.BusinessUnitId == tenantId && x.DocumentId == document.Id && x.PageNumber == 1);
    if (page is null)
    {
        page = DocumentPage.Create(tenantId, document.Id, 1, 100, 100);
        db.Add(page);
    }
    var inquiry = await db.Set<CanonicalInquiry>().SingleOrDefaultAsync(x =>
        x.BusinessUnitId == tenantId && x.CorpusId == originalDocument.CorpusId && x.InquiryNumber == 1);
    if (inquiry is null)
    {
        inquiry = CanonicalInquiry.Create(tenantId, originalDocument.CorpusId, 1);
        inquiry.PopulateHeader(lead.Rfqno, lead.BuyersName, lead.RecDate, lead.BidClosingDate);
        inquiry.BindLead(lead.Id);
        db.Add(inquiry);
    }
    await db.SaveChangesAsync();

    var region = await db.Set<DocumentRegion>().FirstOrDefaultAsync(x =>
        x.BusinessUnitId == tenantId && x.PageId == page.Id && x.RegionType == DocumentRegionType.Table);
    if (region is null)
    {
        region = DocumentRegion.Create(tenantId, page.Id, DocumentRegionType.Table,
            0, 0, 100, 100, csv.ToString(), 1m);
        db.Add(region);
        await db.SaveChangesAsync();
    }

    foreach (var item in lead.LeadItems.GroupBy(x => x.Id).Select(x => x.First()).OrderBy(x => x.LineItemNo))
    {
        var lineNumber = int.TryParse(item.LineItemNo, out var parsedLineNumber) ? parsedLineNumber : 1;
        var canonical = await db.Set<CanonicalLineItem>().SingleOrDefaultAsync(x =>
            x.BusinessUnitId == tenantId && x.InquiryId == inquiry.Id && x.LineNumber == lineNumber);
        if (canonical is null)
        {
            canonical = CanonicalLineItem.Create(tenantId, inquiry.Id, lineNumber,
                item.ProductShortDescription ?? item.ItemText ?? "Requested line",
                item.Quantity, item.UnitOfMeasure);
            canonical.Enrich(null, item.ManufacturerPartNumber, item.Currency ?? "USD", null, null, "{}",
                CanonicalValidationStatus.Valid);
            canonical.BindLeadItem(item.Id);
            db.Add(canonical);
            await db.SaveChangesAsync();
        }
        if (!await db.Set<FieldEvidence>().AnyAsync(x => x.BusinessUnitId == tenantId
                && x.LineItemId == canonical.Id))
            db.Add(FieldEvidence.ForLineItem(tenantId, region.Id, canonical.Id, "requestedLine",
                item.ProductShortDescription, item.ManufacturerPartNumber, 1m,
                "acceptance-fixture", runId, validationStatus: FieldValidationStatus.Valid));
    }
    await db.SaveChangesAsync();
}

void EnsureCommercialIdentity(Lead lead, long customerId, long? contactId, string matchStatus)
{
    if (lead.CustomerId is null)
    {
        lead.ResolveCommercialIdentity(customerId, contactId, matchStatus);
        return;
    }

    if (lead.CustomerId != customerId || (contactId.HasValue && lead.ContactId != contactId))
    {
        throw new InvalidOperationException(
            $"Acceptance fixture identity conflict for Lead {lead.Id}: expected customer {customerId}" +
            $" and contact {contactId?.ToString() ?? "(preserved)"}, found customer {lead.CustomerId}" +
            $" and contact {lead.ContactId?.ToString() ?? "(none)"}.");
    }
}

async Task EnsureReassignmentHistoryAsync(long leadId, long customerId, long ownershipId,
    long previousUserId, long currentUserId)
{
    if (await db.Set<LeadAssignment>().AnyAsync(x => x.BusinessUnitId == tenantId && x.LeadId == leadId)) return;
    var correlation = $"core-reassignment-{leadId}";
    var previousDecision = new LeadRoutingDecision { BusinessUnitId = tenantId, LeadId = leadId,
        CustomerId = customerId, OwnershipId = ownershipId, SuggestedUserId = previousUserId,
        SelectedUserId = previousUserId, MatchStatus = CustomerMatchStatus.Matched,
        Outcome = RoutingOutcome.AssignedPrimary, MatchConfidence = 1m,
        DecisionCode = "ACCOUNT_OWNER_ASSIGNED", Explanation = "{\"reason\":\"Initial account owner assignment\"}",
        PolicyVersion = "core-acceptance-v1", CorrelationId = correlation,
        IdempotencyKey = $"{correlation}-initial", CreatedOn = now.AddDays(-2) };
    db.Add(previousDecision); await db.SaveChangesAsync();
    db.Add(new LeadAssignment { BusinessUnitId = tenantId, LeadId = leadId, ToUserId = previousUserId,
        AssignmentScope = AssignmentScope.CustomerPermanent, OwnershipId = ownershipId,
        RoutingDecisionId = previousDecision.Id, ReasonCode = "ACCOUNT_OWNER_ASSIGNED",
        Comment = "Initial assignment to Sarah Malik", EffectiveFrom = now.AddDays(-2),
        EffectiveTo = now.AddDays(-1), AssignedByUserId = manager.Id, CorrelationId = correlation,
        IdempotencyKey = $"{correlation}-assignment-initial" });
    await db.SaveChangesAsync();

    var currentDecision = new LeadRoutingDecision { BusinessUnitId = tenantId, LeadId = leadId,
        CustomerId = customerId, OwnershipId = ownershipId, SuggestedUserId = currentUserId,
        SelectedUserId = currentUserId, MatchStatus = CustomerMatchStatus.Matched,
        Outcome = RoutingOutcome.AssignedBackup, MatchConfidence = 1m,
        DecisionCode = "TEMPORARY_BACKUP_REASSIGNMENT",
        Explanation = "{\"reason\":\"Sarah Malik unavailable; Ahmed Khan accepted temporary coverage\"}",
        PolicyVersion = "core-acceptance-v1", CorrelationId = correlation,
        IdempotencyKey = $"{correlation}-current", CreatedOn = now.AddDays(-1) };
    db.Add(currentDecision); await db.SaveChangesAsync();
    db.Add(new LeadAssignment { BusinessUnitId = tenantId, LeadId = leadId, FromUserId = previousUserId,
        ToUserId = currentUserId, AssignmentScope = AssignmentScope.SharedBackup, OwnershipId = ownershipId,
        RoutingDecisionId = currentDecision.Id, ReasonCode = "TEMPORARY_BACKUP_REASSIGNMENT",
        Comment = "Reassigned to Ahmed Khan while retaining Sarah Malik as Account Owner",
        EffectiveFrom = now.AddDays(-1), AssignedByUserId = manager.Id, CorrelationId = correlation,
        IdempotencyKey = $"{correlation}-assignment-current" });
    await db.SaveChangesAsync();
}

async Task<SetupMaster> EnsureSetupAsync(string type, string code, string value)
{
    var existing = await db.SetupMasters.SingleOrDefaultAsync(x => x.BusinessUnitId == tenantId &&
        x.SetupType == type && x.SetupCode == code);
    if (existing is not null) return existing;
    var setup = new SetupMaster { BusinessUnitId = tenantId, SetupType = type, SetupCode = code,
        SetupValue = value, IsActive = true, CreatedBy = fixtureActor, CreatedOn = now };
    db.Add(setup); await db.SaveChangesAsync(); return setup;
}

async Task EnsureLeadQualifiedAsync(long leadId)
{
    var path = new[] { "PENDING_IDENTIFICATION", "ASSIGNED", "UNDER_REVIEW", "QUALIFIED" };
    foreach (var code in path)
        await EnsureSetupAsync("LeadStatus", code, code.Replace('_', ' '));

    var lifecycle = new ERP_RFQ_Automation.CommercialCases.Lifecycle.LifecycleApplicationService(db);
    while (true)
    {
        var lead = await db.Leads.AsNoTracking().Include(x => x.LeadStatus)
            .SingleAsync(x => x.BusinessUnitId == tenantId && x.Id == leadId);
        var current = ERP_RFQ_Automation.CommercialCases.Lifecycle.LifecyclePolicy.Canonicalize(
            "Lead", lead.LeadStatus?.SetupCode, lead.LeadStatus?.SetupValue);
        if (current is "QUALIFIED" or "CONVERTED_TO_RFQ" or "DISQUALIFIED" or "CLOSED") return;
        var targetIndex = current == "RECEIVED" ? 0 : Array.IndexOf(path, current) + 1;
        if (targetIndex < 0 || targetIndex >= path.Length)
            throw new InvalidOperationException($"Cannot qualify acceptance lead {leadId} from lifecycle state {current}.");
        var target = path[targetIndex];
        await lifecycle.TransitionLeadAsync(tenantId, leadId,
            new ERP_RFQ_Automation.CommercialCases.Lifecycle.LifecycleActor(fixtureActor, "AcceptanceFixture"),
            new ERP_RFQ_Automation.CommercialCases.Lifecycle.LifecycleTransitionCommand(
                target, lead.LifecycleVersion, null, "Acceptance qualification path", "Fixture",
                $"core-qualify-{leadId}-{target}", $"acceptance-lead-{leadId}",
                $"core-qualify:{tenantId}:{leadId}:{target}"),
            false, CancellationToken.None);
    }
}

async Task<Rfq> EnsureRfqAsync(Lead lead, string rfqNumber)
{
    var existing = await db.Rfqs.Include(x => x.Rfqitems).Include(x => x.Lead)
        .SingleOrDefaultAsync(x => x.BusinessUnitId == tenantId
            && (x.Rfqno == rfqNumber || x.LeadId == lead.Id));
    if (existing is not null) return existing;
    var authorization = await EnsurePromotionAuthorizationAsync(lead);
    var revisionProjection = await LoadCurrentRevisionProjectionAsync(lead, authorization.RevisionId);
    var products = await db.Products.Where(x => x.Buid == tenantId).ToDictionaryAsync(x => x.PartNo, x => x.Id);
    var draftStatus = await EnsureSetupAsync("RFQStatus", "DRAFT", "Draft");
    var rfq = new Rfq { Rfqno = rfqNumber, BuyersName = lead.BuyersName, RecDate = now,
        BidClosingDate = lead.BidClosingDate, LeadId = lead.Id, CustomerId = lead.CustomerId,
        BusinessUnitId = tenantId, RfqstatusId = draftStatus.SetupId, CreatedBy = fixtureActor,
        CreatedDate = now, NoOfLineItems = revisionProjection.Count,
        PromotionId = authorization.PromotionId,
        SourceLeadRevisionId = authorization.RevisionId,
        ParticipationDecisionId = authorization.DecisionId };
    rfq.InheritCommercialIdentity(lead);
    foreach (var (revisionLine, item) in revisionProjection)
    {
        products.TryGetValue(item.ManufacturerPartNumber ?? string.Empty, out var productId);
        rfq.Rfqitems.Add(new Rfqitem { LineItemNo = item.LineItemNo,
            ProductId = productId == 0 ? null : productId, ProductShortDescription = item.ProductShortDescription,
            ItemMaterialCode = item.ItemMaterialCode, ManufacturerPartNumber = item.ManufacturerPartNumber,
            CommodityProduct = item.CommodityProduct, ItemText = item.ItemText,
            // The fixture builds every line with a stated quantity; RFQItems.Quantity is NOT NULL
            // and CHECK > 0, so an unquantified lead line is a fixture bug, not a runtime case.
            Quantity = item.Quantity ?? throw new InvalidOperationException(
                $"Fixture lead line {item.LineItemNo} has no quantity."),
            UnitOfMeasure = item.UnitOfMeasure ?? "EA", CreatedBy = fixtureActor, CreatedDate = now,
            SourceBusinessUnitId = tenantId, SourceLeadId = lead.Id,
            SourceLeadRevisionId = authorization.RevisionId,
            SourceLeadItemRevisionId = revisionLine.Id });
    }
    db.Add(rfq); await db.SaveChangesAsync(); return rfq;
}

async Task<(long RevisionId, long DecisionId, long PromotionId)> EnsurePromotionAuthorizationAsync(Lead lead)
{
    if (!lead.CurrentRevisionId.HasValue)
        throw new InvalidOperationException($"Fixture lead {lead.Id} has no immutable current revision.");
    var revisionId = lead.CurrentRevisionId.Value;
    var fitKey = $"acceptance-fit:{tenantId}:{revisionId}";
    var fit = await db.Set<LeadFitAssessment>().SingleOrDefaultAsync(x =>
        x.BusinessUnitId == tenantId && x.IdempotencyKey == fitKey);
    if (fit is null)
    {
        fit = new LeadFitAssessment
        {
            BusinessUnitId = tenantId, LeadId = lead.Id, LeadRevisionId = revisionId,
            Sequence = 1, PolicyVersion = "acceptance/v1", Recommendation = "BID",
            IsActionable = true,
            AssessmentJson = "{\"fixture\":true,\"decision\":\"BID\"}",
            IdempotencyKey = fitKey,
            RequestHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(fitKey))).ToLowerInvariant(),
            AssessedBy = fixtureActor, AssessedAtUtc = DateTimeOffset.UtcNow
        };
        db.Add(fit);
        await db.SaveChangesAsync();
    }

    var decisionKey = $"acceptance-participation:{tenantId}:{revisionId}";
    var decision = await db.Set<LeadParticipationDecision>().Include(x => x.Lines)
        .SingleOrDefaultAsync(x => x.BusinessUnitId == tenantId && x.IdempotencyKey == decisionKey);
    if (decision is null)
    {
        var uom = await EnsureUomAsync("EA", "Each");
        var currency = await EnsureCurrencyAsync("USD", "US Dollar", "$");
        var revisionProjection = await LoadCurrentRevisionProjectionAsync(lead, revisionId);
        decision = new LeadParticipationDecision
        {
            BusinessUnitId = tenantId, LeadId = lead.Id, LeadRevisionId = revisionId,
            FitAssessmentId = fit.Id, Sequence = 1, IsCommitted = true,
            Outcome = LeadParticipationOutcome.FullBid,
            Notes = "Authorized acceptance-fixture participation decision.",
            IdempotencyKey = decisionKey,
            RequestHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(decisionKey))).ToLowerInvariant(),
            DecidedBy = fixtureActor, DecidedAtUtc = DateTimeOffset.UtcNow
        };
        foreach (var (line, current) in revisionProjection)
        {
            var productId = await db.Products.Where(x => x.Buid == tenantId
                    && x.PartNo == current.ManufacturerPartNumber)
                .Select(x => (long?)x.Id).SingleOrDefaultAsync();
            decision.Lines.Add(new LeadLineParticipationDecision
            {
                BusinessUnitId = tenantId, LeadId = lead.Id, LeadRevisionId = revisionId,
                LeadItemRevisionId = line.Id, Choice = LeadLineParticipationChoice.Bid,
                ProductId = productId, Quantity = current.Quantity,
                UnitOfMeasure = current.UnitOfMeasure ?? "EA", UomId = uom.UomId,
                Currency = current.Currency ?? "USD", CurrencyId = currency.Id,
                CatalogPolicyVersion = "acceptance/v1", WarningSnapshotJson = "{}"
            });
        }
        db.Add(decision);
        await db.SaveChangesAsync();
    }

    var promotionKey = $"acceptance-promotion:{tenantId}:{revisionId}";
    var promotion = await db.Set<RfqPromotion>().SingleOrDefaultAsync(x =>
        x.BusinessUnitId == tenantId && x.IdempotencyKey == promotionKey);
    if (promotion is null)
    {
        promotion = new RfqPromotion
        {
            BusinessUnitId = tenantId, LeadId = lead.Id, LeadRevisionId = revisionId,
            ParticipationDecisionId = decision.Id, IdempotencyKey = promotionKey,
            RequestHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(promotionKey))).ToLowerInvariant(),
            PromotedBy = fixtureActor, PromotedAtUtc = DateTimeOffset.UtcNow
        };
        db.Add(promotion);
        await db.SaveChangesAsync();
    }
    return (revisionId, decision.Id, promotion.Id);
}

async Task<IReadOnlyList<(LeadItemRevision Revision, LeadItem Item)>> LoadCurrentRevisionProjectionAsync(
    Lead lead, long revisionId)
{
    if (lead.CurrentRevisionId != revisionId)
        throw new InvalidOperationException(
            $"Fixture lead {lead.Id} current revision is {lead.CurrentRevisionId?.ToString() ?? "missing"}, not {revisionId}.");

    // Never derive promotion from Lead.LeadItems. This fixture intentionally uses one long-lived
    // DbContext and loads that navigation more than once; relationship fix-up can therefore leave
    // repeated references in the in-memory List. It also retains historical projections by design.
    // LeadItemRevision is the immutable authority for exactly which canonical line belongs to this
    // revision, so follow that lineage and independently prove it is the complete current projection.
    var revisionLines = await db.Set<LeadItemRevision>().AsNoTracking()
        .Include(x => x.LeadItem)
        .Where(x => x.BusinessUnitId == tenantId && x.LeadId == lead.Id
            && x.LeadRevisionId == revisionId)
        .OrderBy(x => x.LineNumber)
        .ThenBy(x => x.Id)
        .ToArrayAsync();
    if (revisionLines.Length == 0)
        throw new InvalidOperationException(
            $"Fixture lead {lead.Id} revision {revisionId} has no immutable line projection.");

    var missing = revisionLines.FirstOrDefault(x => !x.LeadItemId.HasValue || x.LeadItem is null);
    if (missing is not null)
        throw new InvalidOperationException(
            $"Fixture revision line {missing.Id} has no canonical Lead item lineage.");

    var revisionItemIds = revisionLines.Select(x => x.LeadItemId!.Value).ToArray();
    var duplicateItemId = revisionItemIds.GroupBy(x => x).FirstOrDefault(x => x.Count() > 1)?.Key;
    if (duplicateItemId.HasValue)
        throw new InvalidOperationException(
            $"Fixture lead {lead.Id} revision {revisionId} links canonical Lead item {duplicateItemId.Value} more than once.");

    var currentItemIds = await db.LeadItems.AsNoTracking()
        .Where(x => x.LeadId == lead.Id && x.Lead.BusinessUnitId == tenantId
            && x.IsCurrentRevisionProjection)
        .OrderBy(x => x.Id)
        .Select(x => x.Id)
        .ToArrayAsync();
    var orderedRevisionItemIds = revisionItemIds.OrderBy(x => x).ToArray();
    if (!currentItemIds.SequenceEqual(orderedRevisionItemIds))
        throw new InvalidOperationException(
            $"Fixture lead {lead.Id} revision {revisionId} lineage does not exactly match its current Lead projection. "
            + $"Revision items: [{string.Join(',', orderedRevisionItemIds)}]; current items: [{string.Join(',', currentItemIds)}].");

    return revisionLines.Select(x => (x, x.LeadItem!)).ToArray();
}

async Task<SetUom> EnsureUomAsync(string code, string name)
{
    var existing = await db.SetUoms.SingleOrDefaultAsync(x =>
        x.BusinessUnitId == tenantId && x.UomCode == code);
    if (existing is not null) return existing;
    var uom = new SetUom
    {
        BusinessUnitId = tenantId, UomCode = code, UomName = name,
        IsActive = true, CreatedBy = fixtureActor, CreatedDate = now
    };
    db.Add(uom);
    await db.SaveChangesAsync();
    return uom;
}

async Task EnsureLineResolutionsAsync(Lead lead, Rfq rfq, Product full, Product partialProduct,
    Product outProduct, Product incomingProductValue)
{
    var revision = await db.Set<LeadRevision>()
        .SingleAsync(x => x.BusinessUnitId == tenantId && x.Id == lead.CurrentRevisionId!.Value);
    var revisionLines = await db.Set<LeadItemRevision>().AsNoTracking()
        .Where(x => x.BusinessUnitId == tenantId && x.LeadRevisionId == revision.Id)
        .OrderBy(x => x.LineNumber).ToArrayAsync();
    var resolvedLineIds = await db.Set<LeadLineCommercialResolution>().AsNoTracking()
        .Where(x => x.BusinessUnitId == tenantId && x.LeadRevisionId == revision.Id)
        .Select(x => x.LeadLineId).Distinct().ToArrayAsync();
    if (resolvedLineIds.Length == revisionLines.Length) return;
    var resolvedLines = resolvedLineIds.ToHashSet();
    var lines = await db.LeadItems.AsNoTracking().Where(x => x.LeadId == lead.Id)
        .OrderBy(x => x.LineItemNo).ToArrayAsync();
    for (var index = 0; index < lines.Length; index++)
    {
        var item = lines[index];
        if (resolvedLines.Contains(revisionLines[index].Id)) continue;
        var itemQuantity = item.Quantity ?? throw new InvalidOperationException(
            $"Fixture lead line {item.LineItemNo} has no quantity.");
        var partNumber = item.ManufacturerPartNumber ?? $"LINE-{index + 1}";
        var (productId, classification, atp, incomingAvailable) = partNumber switch
        {
            "CORE-ATP-100" => ((long?)full.Id, CommercialResolutionClassification.KnownInStock, 33m, 0m),
            "CORE-PARTIAL-200" => ((long?)partialProduct.Id, CommercialResolutionClassification.KnownShortage, 6m, 0m),
            "CORE-OOS-300" => ((long?)outProduct.Id, CommercialResolutionClassification.KnownShortage, 0m, 0m),
            "CORE-INCOMING-400" => ((long?)incomingProductValue.Id, CommercialResolutionClassification.KnownIncoming, 0m, 22m),
            "FIELD-SERVICE" => ((long?)null, CommercialResolutionClassification.NonInventoryService, 0m, 0m),
            _ => ((long?)null, CommercialResolutionClassification.UnknownProduct, 0m, 0m)
        };
        var related = partNumber == "CORE-OOS-300"
            ? "[{\"resourceId\":\"supplier-quote:core\",\"businessUnitId\":80101,\"kind\":\"SupplierQuoteHistory\",\"supplierId\":1,\"displayName\":\"Precision Controls Supply\",\"matchReason\":\"Tenant-local supplier history\",\"score\":0.9,\"evidenceReference\":\"CORE-SQ-OOS-300\"}]"
            : "[]";
        var fulfilment = partNumber == "CORE-ATP-100"
            ? $"{{\"classification\":\"MultipleWarehouses\",\"requestedQuantity\":{itemQuantity},\"allocatedQuantity\":{itemQuantity},\"shortageQuantity\":0,\"allocations\":[{{\"warehouseId\":{primaryWarehouse.Id},\"inventoryId\":{sufficientPrimary.Id},\"warehouseCode\":\"CORE-PRIMARY\",\"quantity\":8,\"availableBeforeAllocation\":18}},{{\"warehouseId\":{overflowWarehouse.Id},\"inventoryId\":{sufficientOverflow.Id},\"warehouseCode\":\"CORE-OVERFLOW\",\"quantity\":2,\"availableBeforeAllocation\":15}}]}}"
            : $"{{\"classification\":\"{(atp > 0 ? "PartialStock" : "NoStock")}\",\"requestedQuantity\":{itemQuantity},\"allocatedQuantity\":{atp},\"shortageQuantity\":{Math.Max(0, itemQuantity - atp)},\"allocations\":[]}}";
        db.Add(new LeadLineCommercialResolution { BusinessUnitId = tenantId, LeadId = lead.Id,
            LeadRevisionId = revision.Id, LeadLineId = revisionLines[index].Id, RfqId = rfq.Id,
            ProductId = productId, RequestedPartNumber = partNumber, RequestedQuantity = itemQuantity,
            Classification = classification, AvailableToPromise = atp, IncomingAvailable = incomingAvailable,
            FulfilmentJson = fulfilment, RelatedResourcesJson = related,
            ProductResolutionJson = productId.HasValue ? $"{{\"decisionState\":\"AutoLinked\",\"resolvedProductId\":{productId.Value}}}" : "{\"decisionState\":\"Unresolved\"}",
            ResolutionMethod = partNumber == "FIELD-SERVICE" ? "SERVICE_NON_INVENTORY_FIXTURE" : "DETERMINISTIC_LOCAL",
            EvidenceReference = $"lead-revision:{revision.Id}:line:{revisionLines[index].Id}",
            InventoryAsOfUtc = now.AddMinutes(-5), ResolvedOn = now.AddMinutes(-5) });
    }
    await db.SaveChangesAsync();
}

async Task<Quote> EnsureQuoteAsync(Rfq rfq, long statusId, string quoteNumber, string actorEmail)
{
    var existing = await db.Quotes.Include(x => x.QuoteItems)
        .SingleOrDefaultAsync(x => x.BusinessUnitId == tenantId
            && (x.QuoteNo == quoteNumber || x.Rfqid == rfq.Id));
    if (existing is not null) return existing;
    var quote = new Quote { QuoteNo = quoteNumber, Rfqid = rfq.Id, CustomerId = rfq.CustomerId,
        BusinessUnitId = tenantId, QuoteDate = now.AddDays(-1), ValidUntil = now.AddDays(30),
        StatusId = statusId, TotalAmount = 0, HeaderRemarks = "Acceptance Quote Draft",
        CreatedBy = actorEmail, CreatedDate = now.AddDays(-1), LifecycleVersion = 1 };
    quote.InheritCommercialIdentity(rfq);
    foreach (var line in rfq.Rfqitems.OrderBy(x => x.Id))
        quote.QuoteItems.Add(new QuoteItem { RfqitemId = line.Id, ProductId = line.ProductId,
            ItemDescription = line.ProductShortDescription ?? line.ItemText,
            Quantity = line.Quantity
                ?? throw new InvalidOperationException("Acceptance RFQ item quantity is required."),
            UnitPrice = 0, TotalAmount = 0, CreatedBy = actorEmail, CreatedDate = now.AddDays(-1) });
    db.Add(quote); await db.SaveChangesAsync(); return quote;
}

void PrepareClientPoQuote(Quote quote, long currencyId, decimal unitPrice)
{
    quote.CurrencyId = currencyId;
    quote.ValidUntil = now.AddDays(30);
    quote.RevisionNo = Math.Max(1, quote.RevisionNo);
    foreach (var line in quote.QuoteItems)
    {
        line.UnitPrice = unitPrice;
        line.TotalAmount = line.Quantity * unitPrice;
    }
    quote.TotalAmount = quote.QuoteItems.Sum(x => x.TotalAmount);
    quote.HeaderRemarks = "Accepted commercial terms available for Client PO matching.";
}

async Task EnsureRevisionImpactAsync(Lead lead, Quote quote)
{
    if (await db.Set<LeadRevisionImpact>().AnyAsync(x => x.BusinessUnitId == tenantId &&
        x.AggregateType == "QUOTE" && x.AggregateId == quote.Id && x.Status == "OPEN")) return;
    db.Add(new LeadRevisionImpact { BusinessUnitId = tenantId, LeadId = lead.Id,
        LeadRevisionId = lead.CurrentRevisionId!.Value, AggregateType = "QUOTE", AggregateId = quote.Id,
        ImpactType = "INVENTORY_REVALIDATION_REQUIRED", Status = "OPEN",
        DetailsJson = "{\"reason\":\"Inventory snapshot predates the latest accepted inquiry revision\",\"automaticMutation\":false}",
        CreatedAtUtc = DateTimeOffset.UtcNow });
    await db.SaveChangesAsync();
}

async Task<Order> EnsureOrderAsync(Quote quote, Rfq rfq, Lead lead, long statusId, Product product,
    Warehouse warehouse)
{
    var existing = await db.Orders.Include(x => x.OrderItems)
        .SingleOrDefaultAsync(x => x.BusinessUnitId == tenantId && x.OrderNo == "CORE-ALLOCATE-001");
    if (existing is not null) return existing;
    var order = new Order { OrderNo = "CORE-ALLOCATE-001", QuoteId = quote.Id, Rfqid = rfq.Id,
        LeadId = lead.Id, CustomerId = abc.Id, BusinessUnitId = tenantId, StatusId = statusId,
        SourceType = OrderSourceTypes.LegacyQuote,
        OrderDate = now, TotalAmount = 270m, SubTotal = 270m, CreatedBy = fixtureActor,
        CreatedOn = now, IsActive = true };
    order.InheritCommercialIdentity(quote);
    order.OrderItems.Add(new OrderItem { ProductId = product.Id, Description = product.ProductName,
        Quantity = 2, UnitPrice = 135m, TotalAmount = 270m, WarehouseId = warehouse.Id,
        CreatedBy = fixtureActor, CreatedDate = now, IsActive = true });
    db.Add(order); await db.SaveChangesAsync(); return order;
}

async Task<long> EnsureSourcedCustomerOrderAsync(Quote quote, Rfq rfq, Product product, long currencyId)
{
    var existing = await db.Orders.Include(x => x.OrderItems).SingleOrDefaultAsync(x =>
        x.BusinessUnitId == tenantId && x.OrderNo == "CORE-SOURCED-CUSTOMER-ORDER");
    if (existing is not null) return existing.OrderItems.Single().Id;
    var rfqLine = rfq.Rfqitems.Single(x => x.ProductId == product.Id);
    var quoteLine = quote.QuoteItems.Single(x => x.RfqitemId == rfqLine.Id);
    var po = await db.CustomerPurchaseOrders.Include(x => x.Lines).SingleOrDefaultAsync(x =>
        x.BusinessUnitId == tenantId && x.CustomerId == abc.Id
            && x.NormalizedExternalPoNumber == "ABC-PO-SOURCED-001");
    if (po is null)
    {
        po = new CustomerPurchaseOrder
        {
            BusinessUnitId = tenantId, CommercialCaseId = quote.CommercialCaseId!.Value,
            CustomerId = abc.Id, CurrencyId = currencyId, InternalNumber = "CPO-CORE-SOURCED-001",
            ExternalPoNumber = "ABC-PO-SOURCED-001", NormalizedExternalPoNumber = "ABC-PO-SOURCED-001",
            PoDate = now.Date, ReceivedOn = now, Status = CustomerPurchaseOrderStatuses.FullyAwarded,
            Version = 2, CreatedOn = now, CreatedBy = fixtureActor
        };
        po.Lines.Add(new CustomerPurchaseOrderLine
        {
            BusinessUnitId = tenantId, ExternalLineReference = "3", ProductId = product.Id,
            Description = quoteLine.ItemDescription ?? "Sourced Customer Order line",
            OrderedQuantity = 12m, UnitPrice = quoteLine.UnitPrice, LineAmount = quoteLine.UnitPrice * 12m,
            Version = 1
        });
        db.CustomerPurchaseOrders.Add(po);
        await db.SaveChangesAsync();
    }
    var poLine = po.Lines.Single();
    var award = await db.CustomerAwards.Include(x => x.LineAllocations).SingleOrDefaultAsync(x =>
        x.BusinessUnitId == tenantId && x.AwardNumber == "AWD-CORE-SOURCED-001");
    if (award is null)
    {
        award = new CustomerAward
        {
            BusinessUnitId = tenantId, AwardNumber = "AWD-CORE-SOURCED-001",
            CustomerPurchaseOrderId = po.Id, QuoteId = quote.Id, CommercialCaseId = quote.CommercialCaseId!.Value,
            CustomerId = abc.Id, CurrencyId = currencyId, Status = CustomerAwardStatuses.Draft,
            Version = 1, CreatedOn = now, CreatedBy = fixtureActor, ModifiedOn = now, ModifiedBy = fixtureActor
        };
        award.LineAllocations.Add(new CustomerAwardLineAllocation
        {
            BusinessUnitId = tenantId, CustomerPurchaseOrderLineId = poLine.Id, QuoteItemId = quoteLine.Id,
            AwardedQuantity = 12m, UnitPriceSnapshot = quoteLine.UnitPrice,
            DiscountSnapshot = quoteLine.Discount ?? 0m, TaxSnapshot = quoteLine.TaxAmount ?? 0m,
            TotalSnapshot = quoteLine.TotalAmount, Version = 1
        });
        db.CustomerAwards.Add(award);
        await db.SaveChangesAsync();
    }
    if (award.Status == CustomerAwardStatuses.Draft)
    {
        award.Status = CustomerAwardStatuses.Confirmed;
        award.ConfirmedOn = now;
        award.ConfirmedBy = fixtureActor;
        award.Version = 2;
        award.ModifiedOn = now;
        award.ModifiedBy = fixtureActor;
        await db.SaveChangesAsync();
    }
    if (award.Status == CustomerAwardStatuses.Confirmed)
    {
        award.Status = CustomerAwardStatuses.Ordered;
        award.Version = 3;
        await db.SaveChangesAsync();
    }
    var allocation = award.LineAllocations.Single();
    var orderStatusId = (await EnsureSetupAsync("OrderStatus", "DRAFT", "Draft")).SetupId;
    var order = new Order
    {
        OrderNo = "CORE-SOURCED-CUSTOMER-ORDER", QuoteId = quote.Id, Rfqid = rfq.Id,
        LeadId = rfq.LeadId, CustomerId = abc.Id, BusinessUnitId = tenantId,
        StatusId = orderStatusId, CurrencyId = currencyId, SourceType = OrderSourceTypes.CustomerAward,
        CustomerAwardId = award.Id, OrderDate = now,
        SubTotal = allocation.AwardedQuantity * allocation.UnitPriceSnapshot,
        DiscountAmount = allocation.DiscountSnapshot, TaxAmount = allocation.TaxSnapshot,
        TotalAmount = allocation.TotalSnapshot, BalanceAmount = allocation.TotalSnapshot,
        CreatedBy = fixtureActor, CreatedOn = now, IsActive = true
    };
    order.InheritCommercialIdentity(quote);
    order.OrderItems.Add(new OrderItem
    {
        ProductId = product.Id, Description = poLine.Description, Quantity = allocation.AwardedQuantity,
        UnitPrice = allocation.UnitPriceSnapshot, Discount = allocation.DiscountSnapshot,
        TaxAmount = allocation.TaxSnapshot, TotalAmount = allocation.TotalSnapshot,
        CustomerAwardLineAllocationId = allocation.Id, CreatedBy = fixtureActor,
        CreatedDate = now, IsActive = true
    });
    db.Orders.Add(order);
    await db.SaveChangesAsync();
    return order.OrderItems.Single().Id;
}

async Task<FollowUpTask> EnsureFollowUpRecordAsync(long userId, long aggregateId, long customerId,
    DateTime dueAt, int priority, string purpose, string key)
{
    var existing = await db.FollowUpTasks.SingleOrDefaultAsync(x => x.BusinessUnitId == tenantId &&
        x.CreationIdempotencyKey == key);
    if (existing is not null) return existing;
    var task = new FollowUpTask { BusinessUnitId = tenantId, AssignedToUserId = userId,
        AggregateType = "Quote", AggregateId = aggregateId, CustomerId = customerId, DueAtUtc = dueAt,
        Status = FollowUpStatus.Open, Priority = priority, PurposeCode = purpose,
        CreatedAtUtc = now.AddDays(-1), UpdatedAtUtc = now.AddDays(-1), Version = 1,
        CreatedBy = fixtureActor, CorrelationId = key, CreationIdempotencyKey = key };
    db.Add(task); await db.SaveChangesAsync(); return task;
}

async Task<FollowUpTask> EnsureCompletedFollowUpAsync(long userId, long aggregateId, long customerId)
{
    const string key = "core-e2e-completed-followup";
    var existing = await db.FollowUpTasks.SingleOrDefaultAsync(x => x.BusinessUnitId == tenantId &&
        x.CreationIdempotencyKey == key);
    if (existing is not null) return existing;
    var task = new FollowUpTask { BusinessUnitId = tenantId, AssignedToUserId = userId,
        AggregateType = "Quote", AggregateId = aggregateId, CustomerId = customerId,
        DueAtUtc = now.AddHours(-2), Status = FollowUpStatus.Completed, Priority = 70,
        PurposeCode = "CORE_E2E_COMPLETED", CreatedAtUtc = now.AddDays(-2), UpdatedAtUtc = now.AddHours(-3),
        Version = 2, CreatedBy = fixtureActor, CorrelationId = key, CreationIdempotencyKey = key };
    db.Add(task); await db.SaveChangesAsync();
    db.Add(new FollowUpTransitionEvent { BusinessUnitId = tenantId, FollowUpTaskId = task.Id,
        FromStatus = FollowUpStatus.Open, ToStatus = FollowUpStatus.Completed, FromVersion = 1,
        ToVersion = 2, OccurredAtUtc = now.AddHours(-3), ActorId = sarah.Email!,
        Reason = "Completed before due time", CorrelationId = key,
        IdempotencyKey = $"{key}-transition" });
    db.Add(new CommercialActivity { BusinessUnitId = tenantId, SalesRepUserId = userId,
        ActivityType = CommercialActivityType.FollowUpCompleted, AggregateType = "Quote",
        AggregateId = aggregateId, CustomerId = customerId, OccurredAtUtc = now.AddHours(-3),
        OutcomeCode = "COMPLETED_ON_TIME", EvidenceReference = $"follow-up:{task.Id}",
        ActorId = sarah.Email!, CorrelationId = key, IdempotencyKey = $"{key}-activity" });
    await db.SaveChangesAsync(); return task;
}

async Task PrintFixtureAsync()
{
    var original = await db.Leads.AsNoTracking().SingleAsync(x => x.BusinessUnitId == tenantId && x.Rfqno == "NORTHSTAR-440");
    var reservation = await db.StockReservations.AsNoTracking().SingleAsync(x => x.BusinessUnitId == tenantId &&
        x.IdempotencyKey == "core-reservation-atp");
    var revision = await db.Set<LeadRevision>().AsNoTracking().Include(x => x.Differences)
        .SingleAsync(x => x.BusinessUnitId == tenantId && x.Id == sixLineLead.CurrentRevisionId!.Value);
    var persistedChangedLines = revision.Differences.Count(x => x.ChangeType == LeadRevisionChangeType.Modified &&
        string.Equals(x.Scope, "Line", StringComparison.OrdinalIgnoreCase));
    Console.WriteLine($"ORIGINAL_LEAD_ID={original.Id}");
    Console.WriteLine($"NEXORA_SERIAL={original.CommercialCaseReference}");
    Console.WriteLine($"BUSINESS_UNIT_ID={tenantId}");
    Console.WriteLine($"OTHER_BUSINESS_UNIT_ID={otherTenantId}");
    Console.WriteLine("E2E_PLATFORM_EMAIL=owner@acceptance.local");
    Console.WriteLine($"E2E_PLATFORM_TENANT_ID={platformTenant.Id}");
    Console.WriteLine($"ABC_CUSTOMER_ID={abc.Id}");
    Console.WriteLine($"ABC_CONTACT_ID={abcContact.Id}");
    Console.WriteLine($"ABC_SIX_LINE_LEAD_ID={sixLineLead.Id}");
    Console.WriteLine($"ABC_BACKUP_LEAD_ID={backupLead.Id}");
    Console.WriteLine($"SARAH_USER_ID={sarah.Id}");
    Console.WriteLine($"AHMED_USER_ID={ahmed.Id}");
    Console.WriteLine($"SALES_TEAM_ID={team.Id}");
    Console.WriteLine($"CUSTOMER_OWNERSHIP_ID={ownership.Id}");
    Console.WriteLine($"PRODUCT_IDS={sufficient.Id},{partial.Id},{outOfStock.Id},{incomingProduct.Id}");
    Console.WriteLine($"WAREHOUSE_IDS={primaryWarehouse.Id},{overflowWarehouse.Id},{transitWarehouse.Id}");
    Console.WriteLine($"INVENTORY_IDS={sufficientPrimary.Id},{sufficientOverflow.Id},{partialStock.Id},{zeroStock.Id},{incomingStock.Id}");
    Console.WriteLine($"E2E_CORE_CURRENCY_ID={currency.Id}");
    Console.WriteLine($"E2E_CORE_LEAD_ID={sixLineLead.Id}");
    Console.WriteLine($"E2E_CORE_CUSTOMER_ID={abc.Id}");
    Console.WriteLine($"E2E_CORE_CONTACT_ID={abcContact.Id}");
    Console.WriteLine($"E2E_CORE_CONTACT_EMAIL={abc.ContactEmail}");
    Console.WriteLine($"E2E_CORE_CUSTOMER_NAME={abc.Name}");
    Console.WriteLine("E2E_CORE_ACCOUNT_OWNER_NAME=Sarah Malik");
    Console.WriteLine($"E2E_CORE_ACCOUNT_OWNER_USER_ID={sarah.Id}");
    Console.WriteLine("E2E_CORE_OPPORTUNITY_OWNER_NAME=Sarah Malik");
    Console.WriteLine($"E2E_CORE_OPPORTUNITY_OWNER_USER_ID={sarah.Id}");
    Console.WriteLine($"E2E_CORE_OPPORTUNITY_OWNER_EMAIL={sarah.Email}");
    Console.WriteLine("E2E_CORE_BACKUP_OWNER_NAME=Ahmed Khan");
    Console.WriteLine($"E2E_CORE_BACKUP_LEAD_ID={backupLead.Id}");
    Console.WriteLine($"E2E_CORE_WEIGHTED_LEAD_ID={weightedLead.Id}");
    Console.WriteLine("E2E_CORE_WEIGHTED_OWNER_NAME=Priya Nair");
    Console.WriteLine("E2E_CORE_ASSIGNMENT_REASON=Selected by weighted workload, automation expertise, territory fit, and fair distribution.");
    Console.WriteLine($"E2E_CORE_UNRESOLVED_UPLOAD_LEAD_ID={unresolvedLead.Id}");
    Console.WriteLine($"E2E_CORE_AMBIGUOUS_LEAD_ID={ambiguousLead.Id}");
    Console.WriteLine($"E2E_CORE_OWNERSHIP_CONFIRM_CUSTOMER_ID={confirmationCustomer.Id}");
    Console.WriteLine($"E2E_CORE_OWNERSHIP_CONFIRM_USER_ID={sarah.Id}");
    Console.WriteLine($"E2E_CORE_REASSIGNED_LEAD_ID={reassignedLead.Id}");
    Console.WriteLine($"E2E_CORE_FOLLOW_UP_ID={openFollowUp.Id}");
    Console.WriteLine($"E2E_CORE_COMPLETED_FOLLOW_UP_ID={completedFollowUp.Id}");
    Console.WriteLine($"E2E_CORE_FULL_ATP_PART={sufficient.PartNo}");
    Console.WriteLine("E2E_CORE_FULL_ATP_REQUESTED_QTY=10");
    Console.WriteLine($"E2E_CORE_RESERVED_PART={sufficient.PartNo}");
    Console.WriteLine($"E2E_CORE_PARTIAL_ATP_PART={partial.PartNo}");
    Console.WriteLine($"E2E_CORE_OUT_OF_STOCK_PART={outOfStock.PartNo}");
    Console.WriteLine($"E2E_CORE_INCOMING_PART={incomingProduct.PartNo}");
    Console.WriteLine("E2E_CORE_UNKNOWN_PART=X-UNKNOWN-900");
    Console.WriteLine("E2E_CORE_SERVICE_LINE_REFERENCE=FIELD-SERVICE");
    Console.WriteLine($"E2E_CORE_MULTI_WAREHOUSE_PART={sufficient.PartNo}");
    Console.WriteLine($"E2E_CORE_INVENTORY_FAILURE_LEAD_ID={inventoryFailureLead.Id}");
    Console.WriteLine($"E2E_CORE_RESERVATION_ID={reservation.Id}");
    Console.WriteLine($"E2E_CORE_DOUBLE_ALLOCATION_ORDER_ID={allocationOrder.Id}");
    Console.WriteLine($"E2E_V2_SOURCED_CUSTOMER_ORDER_LINE_ID={sourcedCustomerOrderLineId}");
    Console.WriteLine($"E2E_CORE_STALE_QUOTE_ID={mainQuote.Id}");
    Console.WriteLine($"E2E_CORE_RFQ_CREATION_LEAD_ID={rfqCreationLead.Id}");
    Console.WriteLine($"E2E_CORE_PARTIAL_BID_LEAD_ID={partialBidLead.Id}");
    Console.WriteLine($"E2E_CORE_NO_BID_LEAD_ID={noBidLead.Id}");
    Console.WriteLine($"E2E_CORE_RFQ_CREATION_NEXORA_SERIAL={rfqCreationLead.CommercialCaseReference}");
    Console.WriteLine($"E2E_CORE_RFQ_ID={mainRfq.Id}");
    Console.WriteLine($"E2E_V24_SUPPLIER_QUOTE_ID={negotiationSupplierQuoteId}");
    Console.WriteLine($"E2E_CORE_QUOTE_DRAFT_RFQ_ID={quoteDraftRfq.Id}");
    Console.WriteLine($"E2E_CORE_QUOTE_ID={mainQuote.Id}");
    Console.WriteLine($"E2E_CORE_SEND_QUOTE_ID={sendQuote.Id}");
    Console.WriteLine($"E2E_V2_CLIENT_PO_EXACT_QUOTE_ID={exactAwardQuote.Id}");
    Console.WriteLine($"E2E_V2_CLIENT_PO_EXACT_QUOTE_ITEM_ID={exactAwardQuote.QuoteItems.Single().Id}");
    Console.WriteLine($"E2E_V2_CLIENT_PO_EXACT_PRODUCT_ID={exactAwardQuote.QuoteItems.Single().ProductId}");
    Console.WriteLine($"E2E_V2_CLIENT_PO_EXACT_NEXORA_SERIAL={exactAwardQuote.NexoraSerial}");
    Console.WriteLine($"E2E_V2_CLIENT_PO_PARTIAL_QUOTE_ID={partialAwardQuote.Id}");
    Console.WriteLine($"E2E_V2_CLIENT_PO_PARTIAL_QUOTE_ITEM_ID={partialAwardQuote.QuoteItems.Single().Id}");
    Console.WriteLine($"E2E_V2_CLIENT_PO_PARTIAL_PRODUCT_ID={partialAwardQuote.QuoteItems.Single().ProductId}");
    Console.WriteLine($"E2E_V2_CLIENT_PO_PARTIAL_NEXORA_SERIAL={partialAwardQuote.NexoraSerial}");
    Console.WriteLine($"E2E_CORE_NEXORA_SERIAL={sixLineLead.CommercialCaseReference}");
    Console.WriteLine($"E2E_CORE_DUPLICATE_BATCH_ID={duplicateBatchId}");
    Console.WriteLine("E2E_CORE_REVISION_CHANGED_LINE_COUNT=1");
    Console.WriteLine($"E2E_CORE_PERSISTED_MODIFIED_LINE_COUNT={persistedChangedLines}");
    Console.WriteLine("E2E_CORE_DASHBOARD_METRIC_LABEL=Open follow-ups");
    Console.WriteLine($"E2E_CORE_RESOLUTION_PERSISTENCE_AVAILABLE={resolutionPersistenceAvailable.ToString().ToLowerInvariant()}");
}
