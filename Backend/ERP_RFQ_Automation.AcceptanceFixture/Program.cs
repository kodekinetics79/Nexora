using ERP_RFQ_Automation.DocumentIntelligence.Persistence;
using ERP_RFQ_Automation.LeadIdentity;
using ERP_RFQ_Automation.Models;
using Microsoft.EntityFrameworkCore;

const long tenantId = 80101;
const long otherTenantId = 80102;
var connection = Environment.GetEnvironmentVariable("NEXORA_ACCEPTANCE_CONNECTION")
    ?? throw new InvalidOperationException("NEXORA_ACCEPTANCE_CONNECTION is required.");
var password = Environment.GetEnvironmentVariable("NEXORA_ACCEPTANCE_PASSWORD")
    ?? throw new InvalidOperationException("NEXORA_ACCEPTANCE_PASSWORD is required.");
var options = new DbContextOptionsBuilder<ErpRfqAutomationContext>().UseNpgsql(connection).Options;
await using var db = new ErpRfqAutomationContext(options);

if (await db.Leads.AnyAsync(x => x.BusinessUnitId == tenantId && x.Rfqno == "NORTHSTAR-440"))
{
    await PrintFixtureAsync(db);
    return;
}

var now = DateTime.UtcNow;
var tenant = await EnsureTenantAsync(tenantId, "R01C1", "Release 01C1 Acceptance");
await EnsureTenantAsync(otherTenantId, "R01C1-X", "Release 01C1 Other Tenant");
var managerRole = await EnsureRoleAsync(tenantId, "R01C1_MANAGER", "Acceptance Manager");
var editorRole = await EnsureRoleAsync(tenantId, "R01C1_EDITOR", "Acceptance Sales Editor");
var deniedRole = await EnsureRoleAsync(tenantId, "R01C1_DENIED", "Acceptance Denied");
var otherRole = await EnsureRoleAsync(otherTenantId, "R01C1_OTHER", "Acceptance Other Tenant");
var manager = await EnsureUserAsync(tenantId, managerRole.SetupId, "manager@release01c1.local", "Morgan", "Manager");
await EnsureUserAsync(tenantId, editorRole.SetupId, "editor@release01c1.local", "Elliot", "Editor");
await EnsureUserAsync(tenantId, deniedRole.SetupId, "denied@release01c1.local", "Dana", "Denied");
await EnsureUserAsync(otherTenantId, otherRole.SetupId, "other@release01c1.local", "Taylor", "Other Tenant");

var leadsModule = await EnsureModuleAsync("Leads");
var dashboardModule = await EnsureModuleAsync("Dashboard");
foreach (var role in new[] { managerRole, editorRole })
{
    await EnsurePermissionAsync(tenantId, role.SetupId, leadsModule.Id, create: true, edit: true);
    await EnsurePermissionAsync(tenantId, role.SetupId, dashboardModule.Id, create: false, edit: false);
}
await EnsurePermissionAsync(otherTenantId, otherRole.SetupId, leadsModule.Id, create: true, edit: true);
await db.SaveChangesAsync();

var customer = new Customer
{
    Name = "Northstar Process Controls",
    ContactEmail = "buyer@northstar.local",
    ImageUrl = string.Empty,
    Buid = tenantId,
    IsActive = true,
    CreatedBy = "acceptance-fixture",
    CreatedOn = now
};
db.Customers.Add(customer);
var config = new EmailConfiguration
{
    BusinessUnitId = tenantId,
    ConfigurationName = "Release 01C1 fixture",
    EmailAddress = "intake@release01c1.local",
    Protocol = "IMAP",
    Host = "localhost",
    Port = 993,
    Username = "fixture",
    Password = "fixture-not-used",
    UseSsl = true,
    PollingInterval = 300,
    IsActive = true,
    CreatedOn = now
};
db.EmailConfigurations.Add(config);
await db.SaveChangesAsync();
var ingest = new EmailIngest
{
    MessageId = "release-01c1-fixture",
    EmailSubject = "Controlled reconciliation batch",
    FromEmail = "buyer@northstar.local",
    EmailConfigurationId = config.Id,
    ParseStatus = "Success",
    ParsedAt = now,
    CreatedOn = now
};
db.EmailIngests.Add(ingest);
await db.SaveChangesAsync();

var originalBatch = Guid.Parse("01c10000-0000-0000-0000-000000000000");
var originalCorpus = DocumentCorpus.Create(tenantId, originalBatch, CorpusSourceType.ManualUpload);
db.Add(originalCorpus);
await db.SaveChangesAsync();

var originalSource = await AddSourceAsync(originalCorpus.Id, "02-duplicate.csv",
    "7ed41c4e2196e88577ab32693852c975cd6d16e6793db1512ce8217303271a99");
var originalOccurrence = await AddOccurrenceAsync(originalCorpus.Id, originalSource.Id, "fixture-original");

var identity = new LeadIdentityApplicationService(db);
var original = Candidate("NORTHSTAR-440", null, ingest.Id, manager.Id,
    ("2", "VALVE-A", 14), ("3", "ACTUATOR-ADDED", 2));
var canonical = await identity.ReconcileAsync(original, Intake(originalBatch, "fixture-original", originalSource, originalOccurrence));

await PrintFixtureAsync(db, canonical.NexoraSerial);

async Task<BusinessUnit> EnsureTenantAsync(long id, string code, string name)
{
    var existing = await db.BusinessUnits.SingleOrDefaultAsync(x => x.Id == id);
    if (existing is not null) return existing;
    var value = new BusinessUnit { Id = id, BusinessUnitCode = code, BusinessUnitName = name, IsActive = true, CreatedBy = "acceptance-fixture", CreatedOn = now };
    db.Add(value); await db.SaveChangesAsync(); return value;
}

async Task<SetupMaster> EnsureRoleAsync(long bu, string code, string name)
{
    var existing = await db.SetupMasters.SingleOrDefaultAsync(x => x.BusinessUnitId == bu && x.SetupType == "Role" && x.SetupCode == code);
    if (existing is not null) return existing;
    var value = new SetupMaster { BusinessUnitId = bu, SetupType = "Role", SetupCode = code, SetupValue = name, IsActive = true, CreatedBy = "acceptance-fixture", CreatedOn = now };
    db.Add(value); await db.SaveChangesAsync(); return value;
}

async Task<User> EnsureUserAsync(long bu, long role, string email, string first, string last)
{
    var existing = await db.Users.SingleOrDefaultAsync(x => x.Buid == bu && x.Email == email);
    if (existing is not null) return existing;
    var value = new User { Buid = bu, RoleId = role, Email = email, FirstName = first, LastName = last,
        PasswordHash = BCrypt.Net.BCrypt.HashPassword(password), ImageUrl = string.Empty, IsActive = true,
        CreatedBy = "acceptance-fixture", CreatedOn = now, Timezone = "UTC", Region = "Acceptance" };
    db.Add(value); await db.SaveChangesAsync(); return value;
}

async Task<Module> EnsureModuleAsync(string name)
{
    var existing = await db.Modules.SingleOrDefaultAsync(x => x.ModuleName == name);
    if (existing is not null) return existing;
    var value = new Module { ModuleName = name, IsActive = true, CreatedBy = "acceptance-fixture", CreatedOn = now };
    db.Add(value); await db.SaveChangesAsync(); return value;
}

async Task EnsurePermissionAsync(long bu, long role, long module, bool create, bool edit)
{
    if (await db.RolePermissions.AnyAsync(x => x.BusinessUnitId == bu && x.RoleId == role && x.ModuleId == module)) return;
    db.Add(new RolePermission { BusinessUnitId = bu, RoleId = role, ModuleId = module, CanCreate = create,
        CanEdit = edit, CanDelete = false, CreatedBy = "acceptance-fixture", CreatedOn = now });
}

async Task<SourceDocument> AddSourceAsync(long corpusId, string file, string contentHash)
{
    var source = SourceDocument.Create(tenantId, corpusId, contentHash, file, "text/csv",
        "acceptance", $"release-01c1/{file}", "v1", 128);
    source.MarkSecurityStatus(DocumentSecurityStatus.Cleared);
    db.Add(source); await db.SaveChangesAsync(); return source;
}

async Task<SourceDocumentOccurrence> AddOccurrenceAsync(long corpusId, long sourceId, string key)
{
    var occurrence = SourceDocumentOccurrence.Create(tenantId, sourceId, corpusId, key, "{\"fixture\":\"release-01c1\"}");
    db.Add(occurrence); await db.SaveChangesAsync(); return occurrence;
}

Lead Candidate(string? rfq, string? email, long ingestId, long? owner, params (string line, string part, int qty)[] lines)
{
    var lead = new Lead { Rfqno = rfq, BuyersName = "Northstar Buyer", RecDate = now,
        LeadSource = "ManualUpload", CreatedBy = "acceptance-fixture", CreatedDate = now, BusinessUnitId = tenantId,
        EmailIngestsId = ingestId, Clientemail = email, RequiresCommercialReview = true, AssignTo = owner, AssignOn = owner.HasValue ? now : null };
    foreach (var line in lines) lead.LeadItems.Add(new LeadItem { LineItemNo = line.line, ManufacturerPartNumber = line.part,
        Quantity = line.qty, UnitOfMeasure = null });
    return lead;
}

LeadIntakeDescriptor Intake(Guid batch, string key, SourceDocument source, SourceDocumentOccurrence occurrence, string? sender = "buyer@northstar.local") => new(
    batch, "ManualUpload", key, null, null, "acceptance-fixture", sender, "Controlled RFQ", source.OriginalFileName,
    source.DetectedMimeType, source.ByteSize, source.ContentHash, source.Id, null, occurrence.ReceivedOn,
    DateTimeOffset.UtcNow, LeadProcessingPath.Deterministic, false, 0m, "User", "acceptance-fixture", $"release-01c1:{key}")
{ SourceDocumentOccurrenceId = occurrence.Id };

async Task PrintFixtureAsync(ErpRfqAutomationContext context, string? serial = null)
{
    var original = await context.Leads.AsNoTracking().SingleAsync(x => x.BusinessUnitId == tenantId && x.Rfqno == "NORTHSTAR-440");
    serial ??= original.CommercialCaseReference;
    Console.WriteLine($"ORIGINAL_LEAD_ID={original.Id}");
    Console.WriteLine($"NEXORA_SERIAL={serial}");
    Console.WriteLine($"BUSINESS_UNIT_ID={tenantId}");
    Console.WriteLine($"OTHER_BUSINESS_UNIT_ID={otherTenantId}");
}
