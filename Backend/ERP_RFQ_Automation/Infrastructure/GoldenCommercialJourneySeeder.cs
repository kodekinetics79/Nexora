using System.Text.Json;
using ERP_RFQ_Automation.AI;
using ERP_RFQ_Automation.CommercialCases.Lifecycle;
using ERP_RFQ_Automation.CommercialIntelligence.Sales;
using ERP_RFQ_Automation.CommercialRouting;
using ERP_RFQ_Automation.Models;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.Infrastructure;

/// <summary>
/// Seeds the STARTING CONDITIONS for the Phase 1 base browser journey
/// (Lead → RFQ → Customer Quote Draft) and nothing else.
///
/// <para><b>What it deliberately does NOT create.</b> No warning acknowledgements, no line
/// exclusions, no RFQ, no participation decisions and no Quote Draft. Every one of those is a
/// human decision the browser test must make for itself — seeding any of them would mean the
/// test asserts against state the seeder wrote rather than behaviour the product produced, which
/// is indistinguishable from not testing it at all.</para>
///
/// <para><b>Fail-closed.</b> Off unless <c>GoldenJourneySeed:Enabled</c> is explicitly true, and
/// it refuses outright under Production — it provisions logins and writes commercial records with
/// tenant isolation disabled (startup runs outside any HttpContext, so the EF global query filters
/// are no-ops and the RLS interceptor resolves the BYPASSRLS pipeline role).</para>
///
/// <para><b>Deterministic and idempotent.</b> Every record is looked up by a stable natural key
/// before being created, so re-running against an existing database converges instead of
/// duplicating. Ids are written to a JSON file for the E2E script to consume, because a browser
/// test must never be asked to guess a database id.</para>
/// </summary>
public static class GoldenCommercialJourneySeeder
{
    // Lead.CreatedBy is varchar(20) — this string must stay within it (17 chars).
    private const string Actor = "system:golden-e2e";

    public static async Task EnsureAsync(IServiceProvider services, IConfiguration configuration, IHostEnvironment environment)
    {
        if (!configuration.GetValue("GoldenJourneySeed:Enabled", false)) return;

        using var scope = services.CreateScope();
        var logger = scope.ServiceProvider.GetRequiredService<ILoggerFactory>()
            .CreateLogger("GoldenCommercialJourneySeeder");

        if (environment.IsProduction())
        {
            logger.LogError(
                "GoldenCommercialJourneySeeder refused to run: GoldenJourneySeed:Enabled is true under the "
                + "Production environment. This seeder provisions logins and commercial records with tenant "
                + "isolation disabled and is a local E2E facility only.");
            return;
        }

        var password = configuration["GoldenJourneySeed:Password"];
        if (string.IsNullOrWhiteSpace(password))
        {
            logger.LogWarning(
                "GoldenJourneySeed:Enabled is true but GoldenJourneySeed:Password is not set. Skipping — "
                + "no default credential will ever be seeded.");
            return;
        }

        var db = scope.ServiceProvider.GetRequiredService<ErpRfqAutomationContext>();
        var routing = scope.ServiceProvider.GetRequiredService<ICommercialRoutingApplicationService>();
        var sales = scope.ServiceProvider.GetRequiredService<ISalesApplicationService>();
        var now = DateTime.UtcNow;

        // Tenant A is the journey. Tenant B exists ONLY so cross-tenant denial can be proven
        // against a real second tenant rather than a fabricated id.
        var tenantA = await EnsureBusinessUnitAsync(db, "E2E-GOLDEN-A", "E2E Golden Tenant A", now);
        var tenantB = await EnsureBusinessUnitAsync(db, "E2E-GOLDEN-B", "E2E Golden Tenant B", now);

        var adminRoleA = await EnsureRoleAsync(db, tenantA.Id, "SUPER_ADMIN", "Golden Admin", now);
        var salesRoleA = await EnsureRoleAsync(db, tenantA.Id, "SUPER_ADMIN", "Golden Salesperson", now);
        var adminRoleB = await EnsureRoleAsync(db, tenantB.Id, "SUPER_ADMIN", "Golden Admin B", now);

        var admin = await EnsureUserAsync(db, "golden.admin@e2e.local", "Golden", "Admin", adminRoleA.SetupId, tenantA.Id, password!, now);
        var salesperson = await EnsureUserAsync(db, "golden.sales@e2e.local", "Golden", "Salesperson", salesRoleA.SetupId, tenantA.Id, password!, now);
        var outsider = await EnsureUserAsync(db, "golden.outsider@e2e.local", "Golden", "Outsider", adminRoleB.SetupId, tenantB.Id, password!, now);

        foreach (var tenantId in new[] { tenantA.Id, tenantB.Id })
            if (!await db.AiProcessingPolicies.AnyAsync(p => p.BusinessUnitId == tenantId))
                db.AiProcessingPolicies.Add(AiProcessingPolicy.CreateSecureDefault(tenantId, Actor, now));
        await db.SaveChangesAsync();

        foreach (var bu in new[] { tenantA, tenantB })
            if (!await db.SetupMasters.AnyAsync(s => s.BusinessUnitId == bu.Id && s.SetupType == "LeadStatus"))
            {
                db.SetupMasters.AddRange(LifecycleStatusCatalog.CreateFor(bu, Actor));
                await db.SaveChangesAsync();
            }

        var customerA = await EnsureCustomerAsync(db, tenantA.Id, "Saudi Electricity Company (E2E)", now);
        var customerB = await EnsureCustomerAsync(db, tenantB.Id, "Tenant B Customer (E2E)", now);

        // The catalog decides which lines raise a soft warning. Lines 1 and 3-6 match a product;
        // line 2 deliberately does not, so exactly one line carries "No catalog match found".
        foreach (var partNo in new[] { GoldenLine1Part, GoldenLine3Part, GoldenLine4Part, GoldenLine5Part, GoldenLine6Part })
            await EnsureProductAsync(db, tenantA.Id, partNo, now);

        // Routing profile through the REAL application service — the same path the API exposes.
        //
        // Skipped when one already exists. The service records the idempotency key ALONGSIDE the
        // request content, and ExpectedVersion necessarily changes once the profile exists (0 on
        // create, 1 afterwards). Re-sending the same key with a new ExpectedVersion is "same key,
        // different content" and is correctly rejected — so converging here means not re-issuing
        // the command at all, which is what "idempotent seed" has to mean for a versioned
        // aggregate.
        if (!await db.Set<SalesRepProfile>().IgnoreQueryFilters()
                .AnyAsync(p => p.BusinessUnitId == tenantA.Id && p.UserId == salesperson.Id))
        await sales.UpsertProfileAsync(tenantA.Id, new UpsertSalesRepProfileCommand(
            UserId: salesperson.Id, IsRoutingEligible: true, CapacityPercent: 100, DistributionWeight: 1m,
            TerritoryKeys: Array.Empty<string>(), ProductCategoryKeys: Array.Empty<string>(),
            EffectiveFromUtc: now.Date, EffectiveToUtc: null,
            ExpectedVersion: 0,
            ActorId: Actor, IdempotencyKey: "golden-journey-rep-profile-v1"), CancellationToken.None);

        // Ownership through the REAL routing service, so the deterministic engine can resolve a
        // NAMED owner rather than dropping the lead into the unassigned queue.
        if (!await db.Set<CustomerOwnership>().AnyAsync(o => o.BusinessUnitId == tenantA.Id && o.CustomerId == customerA.Id && o.IsActive))
            await routing.CreateOwnershipAsync(tenantA.Id, new CreateCustomerOwnershipCommand(
                CustomerId: customerA.Id, PrimaryUserId: salesperson.Id, BackupUserId: null,
                Scope: OwnershipScope.GeneralCustomer, ScopeKey: null, Priority: 1,
                EffectiveFrom: now.Date, EffectiveTo: null, Source: Actor,
                Reason: "Golden journey seed"), CancellationToken.None);

        var leadA = await EnsureGoldenLeadAsync(db, tenantA.Id, customerA.Id, now);
        var leadB = await EnsureGoldenLeadAsync(db, tenantB.Id, customerB.Id, now, reference: "E2E-GOLDEN-B-001");

        // Real routing/assignment path — not a hand-written LeadAssignment row.
        try
        {
            await routing.RouteLeadAsync(tenantA.Id, new RouteLeadCommand(
                leadA, $"golden-journey-route-{leadA}", $"golden-journey-{leadA}"), CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Golden journey: routing did not assign lead {LeadId}; it will surface in the unassigned queue.", leadA);
        }

        var manifest = new
        {
            tenantA = tenantA.Id,
            tenantB = tenantB.Id,
            adminUserId = admin.Id,
            adminEmail = admin.Email,
            salespersonUserId = salesperson.Id,
            salespersonEmail = salesperson.Email,
            outsiderUserId = outsider.Id,
            outsiderEmail = outsider.Email,
            customerA = customerA.Id,
            leadId = leadA,
            foreignLeadId = leadB,
            lineParts = new
            {
                hardWarning = GoldenLine1Part,
                softWarning = GoldenLine2Part,
                toExclude = GoldenLine3Part,
                toQuote = GoldenLine4Part,
                toNoQuote = GoldenLine5Part,
                toLeavePending = GoldenLine6Part
            }
        };

        var manifestPath = configuration["GoldenJourneySeed:ManifestPath"];
        if (!string.IsNullOrWhiteSpace(manifestPath))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(manifestPath))!);
            await File.WriteAllTextAsync(manifestPath,
                JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));
            logger.LogInformation("Golden journey manifest written to {Path}.", manifestPath);
        }

        logger.LogInformation(
            "Golden journey seeded: tenantA={TenantA} lead={LeadId} salesperson={SalesUser} tenantB={TenantB} foreignLead={ForeignLead}.",
            tenantA.Id, leadA, salesperson.Id, tenantB.Id, leadB);
    }

    // ------------------------------------------------------------------ the six lines

    private const string GoldenLine1Part = "GOLD-HARD-0001";   // quantity 0 -> HARD blocker, must be corrected
    private const string GoldenLine2Part = "GOLD-SOFT-0002";   // no catalog row -> SOFT warning, must be acknowledged
    private const string GoldenLine3Part = "GOLD-EXCL-0003";   // valid, but the operator excludes it with a reason
    private const string GoldenLine4Part = "GOLD-QUOTE-0004";  // valid -> marked Quote
    private const string GoldenLine5Part = "GOLD-NOQT-0005";   // valid -> marked NoQuote with a reason
    private const string GoldenLine6Part = "GOLD-PEND-0006";   // valid -> deliberately left Pending

    private static async Task<long> EnsureGoldenLeadAsync(
        ErpRfqAutomationContext db, long businessUnitId, long customerId, DateTime now, string reference = "E2E-GOLDEN-A-001")
    {
        var existing = await db.Leads.IgnoreQueryFilters()
            .FirstOrDefaultAsync(l => l.BusinessUnitId == businessUnitId && l.Rfqno == reference);
        if (existing is not null) return existing.Id;

        var qualifiedId = await LifecycleStatusCatalog.ResolveIdAsync(db, businessUnitId, "Lead", "QUALIFIED");

        var lead = new Lead
        {
            Rfqno = reference,
            BuyersName = "SEC Bid Desk",
            RecDate = now,
            BidClosingDate = now.Date.AddDays(21),
            LeadSource = "GoldenJourneySeed",
            CreatedBy = Actor,
            CreatedDate = now,
            BusinessUnitId = businessUnitId,
            LeadStatusId = qualifiedId,
            NoOfLineItems = 6
        };

        // Line 1 carries quantity 0 — the HARD blocker the operator must correct in the browser.
        lead.LeadItems.Add(Line("00010", GoldenLine1Part, "Ball valve 2IN class 300", 0, "EA"));
        // Line 2 has no catalog row, so the resolver raises "No catalog match found" — SOFT.
        lead.LeadItems.Add(Line("00020", GoldenLine2Part, "Gasket spiral wound 4IN", 12, "EA"));
        lead.LeadItems.Add(Line("00030", GoldenLine3Part, "Hex bolt M12 x 60 A4-80", 200, "EA"));
        lead.LeadItems.Add(Line("00040", GoldenLine4Part, "Centrifugal pump seal kit", 4, "EA"));
        lead.LeadItems.Add(Line("00050", GoldenLine5Part, "Alstom obsolete relay card", 2, "EA"));
        lead.LeadItems.Add(Line("00060", GoldenLine6Part, "Cable tray 300mm hot-dip", 30, "EA"));

        db.Leads.Add(lead);
        await db.SaveChangesAsync();
        lead.ResolveCommercialIdentity(customerId, null, "CUSTOMER_CONFIRMED");
        await db.SaveChangesAsync();
        return lead.Id;
    }

    private static LeadItem Line(string lineNo, string partNo, string description, int quantity, string uom) => new()
    {
        LineItemNo = lineNo,
        ItemMaterialCode = partNo,
        ManufacturerPartNumber = partNo,
        ManufacturerName = "GOULDS PUMPS",
        ProductShortDescription = description,
        Quantity = quantity,
        UnitOfMeasure = uom,
        Currency = "SAR",
        // LeadItem carries no requested-delivery column (a known BRD gap); the line-level date
        // that DOES exist is the bid closing date, so only that is seeded.
        BidClosingDateLine = DateTime.UtcNow.Date.AddDays(21)
    };

    // ------------------------------------------------------------------ idempotent helpers

    private static async Task<BusinessUnit> EnsureBusinessUnitAsync(ErpRfqAutomationContext db, string code, string name, DateTime now)
    {
        var existing = await db.BusinessUnits.IgnoreQueryFilters().FirstOrDefaultAsync(b => b.BusinessUnitCode == code);
        if (existing is not null) return existing;
        var created = new BusinessUnit
        {
            BusinessUnitCode = code, BusinessUnitName = name,
            Description = "Local E2E golden journey tenant.", IsActive = true,
            CreatedBy = Actor, CreatedOn = now
        };
        db.BusinessUnits.Add(created);
        await db.SaveChangesAsync();
        return created;
    }

    private static async Task<SetupMaster> EnsureRoleAsync(
        ErpRfqAutomationContext db, long businessUnitId, string code, string name, DateTime now)
    {
        var existing = await db.SetupMasters.IgnoreQueryFilters()
            .FirstOrDefaultAsync(s => s.BusinessUnitId == businessUnitId && s.SetupType == "Role" && s.SetupValue == name);
        if (existing is not null) return existing;
        var role = new SetupMaster
        {
            SetupType = "Role", SetupCode = code, SetupValue = name,
            Description = "Local E2E golden journey role.", BusinessUnitId = businessUnitId,
            IsActive = true, CreatedBy = Actor, CreatedOn = now
        };
        db.SetupMasters.Add(role);
        await db.SaveChangesAsync();
        return role;
    }

    private static async Task<User> EnsureUserAsync(
        ErpRfqAutomationContext db, string email, string first, string last,
        long roleId, long businessUnitId, string password, DateTime now)
    {
        var existing = await db.Users.IgnoreQueryFilters().FirstOrDefaultAsync(u => u.Email == email);
        if (existing is not null)
        {
            // Never silently move an account between tenants — that is the defect DemoUserSeeder
            // documents. These addresses are E2E-only, so a mismatch is a hard configuration error.
            if (existing.Buid != businessUnitId)
                throw new InvalidOperationException(
                    $"Golden journey seed refused: {email} already belongs to business unit {existing.Buid}.");
            return existing;
        }
        var user = new User
        {
            FirstName = first, LastName = last, Email = email,
            PasswordHash = BCrypt.Net.BCrypt.HashPassword(password),
            ImageUrl = string.Empty, RoleId = roleId, Buid = businessUnitId,
            Timezone = "UTC", Region = "E2E", IsActive = true,
            CreatedBy = Actor, CreatedOn = now
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user;
    }

    private static async Task<Customer> EnsureCustomerAsync(ErpRfqAutomationContext db, long businessUnitId, string name, DateTime now)
    {
        var existing = await db.Customers.IgnoreQueryFilters()
            .FirstOrDefaultAsync(c => c.Buid == businessUnitId && c.Name == name);
        if (existing is not null) return existing;
        var customer = new Customer
        {
            Name = name, Buid = businessUnitId, IsActive = true,
            ImageUrl = string.Empty, // NOT NULL in the schema; empty is the seeded-no-logo value
            CreatedBy = Actor, CreatedOn = now
        };
        db.Customers.Add(customer);
        await db.SaveChangesAsync();
        return customer;
    }

    private static async Task EnsureProductAsync(ErpRfqAutomationContext db, long businessUnitId, string partNo, DateTime now)
    {
        if (await db.Products.IgnoreQueryFilters().AnyAsync(p => p.Buid == businessUnitId && p.PartNo == partNo)) return;
        db.Products.Add(new Product
        {
            Buid = businessUnitId, PartNo = partNo, ProductName = partNo,
            IsActive = true, CreatedBy = Actor, CreatedOn = now
        });
        await db.SaveChangesAsync();
    }

}
