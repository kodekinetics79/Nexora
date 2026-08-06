using System.Text.Json;
using ERP_RFQ_Automation.AI;
using ERP_RFQ_Automation.CommercialCases.Lifecycle;
using ERP_RFQ_Automation.CommercialIntelligence.Sales;
using ERP_RFQ_Automation.CommercialRouting;
using ERP_RFQ_Automation.LeadIdentity;
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
        var identity = scope.ServiceProvider.GetRequiredService<ILeadIdentityApplicationService>();
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

        var leadA = await EnsureGoldenLeadAsync(db, identity, tenantA.Id, customerA.Id, now);
        var leadB = await EnsureGoldenLeadAsync(db, identity, tenantB.Id, customerB.Id, now, reference: "E2E-GOLDEN-B-001");

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

        await AssertStartingStateAsync(db, tenantA.Id, leadA, logger);

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

    /// <summary>
    /// Establishes the golden Lead through <see cref="ILeadIdentityApplicationService.ReconcileAsync"/>
    /// — the SAME call the extraction worker makes (ExtractionWorker.cs:944).
    ///
    /// <para><b>Why not construct the Lead directly.</b> The seeder used to, and the resulting row
    /// had no <c>CurrentRevisionId</c>: real ingestion creates the canonical Lead, revision 1, the
    /// occurrence lineage and the commercial identity together, inside the identity pipeline.
    /// Conversion legitimately refuses a Lead with no immutable current revision, so a
    /// directly-built Lead could never convert — the seeder was manufacturing a state the product
    /// never produces. Reconcile owns all of it; nothing here duplicates that logic.</para>
    ///
    /// <para>The extracted document facts are supplied deterministically because this scenario
    /// certifies Lead review and conversion, not OCR accuracy. Everything downstream of the facts
    /// — identity, revision, fingerprint, occurrence, serial — is the product's own work.</para>
    ///
    /// <para>Idempotent by <c>IdempotencyKey</c>: replaying returns the same Lead and creates no
    /// second revision, which is the identity service's own guarantee rather than a seeder trick.</para>
    /// </summary>
    private static async Task<long> EnsureGoldenLeadAsync(
        ErpRfqAutomationContext db, ILeadIdentityApplicationService identity,
        long businessUnitId, long customerId, DateTime now, string reference = "E2E-GOLDEN-A-001")
    {
        var candidate = new Lead
        {
            Rfqno = reference,
            BuyersName = "SEC Bid Desk",
            RecDate = now,
            BidClosingDate = now.Date.AddDays(21),
            LeadSource = "GoldenJourneySeed",
            CreatedBy = Actor,
            CreatedDate = now,
            BusinessUnitId = businessUnitId,
            NoOfLineItems = 6,
            HeaderRemarks = "Golden journey: six deterministic lines."
        };

        // Line 1 carries quantity 0 — the HARD blocker the operator must correct in the browser.
        candidate.LeadItems.Add(Line("00010", GoldenLine1Part, "Ball valve 2IN class 300", 0, "EA"));
        // Line 2 has no catalog row, so the resolver raises "No catalog match found" — SOFT.
        candidate.LeadItems.Add(Line("00020", GoldenLine2Part, "Gasket spiral wound 4IN", 12, "EA"));
        candidate.LeadItems.Add(Line("00030", GoldenLine3Part, "Hex bolt M12 x 60 A4-80", 200, "EA"));
        candidate.LeadItems.Add(Line("00040", GoldenLine4Part, "Centrifugal pump seal kit", 4, "EA"));
        candidate.LeadItems.Add(Line("00050", GoldenLine5Part, "Alstom obsolete relay card", 2, "EA"));
        candidate.LeadItems.Add(Line("00060", GoldenLine6Part, "Cable tray 300mm hot-dip", 30, "EA"));

        var key = $"golden-journey:{businessUnitId}:{reference}";
        var result = await identity.ReconcileAsync(candidate, new LeadIntakeDescriptor(
            BatchId: DeterministicBatchId(businessUnitId),
            SourceChannel: "ManualUpload",
            IdempotencyKey: key,
            ExternalSourceId: key,
            EmailThreadId: null,
            SourceSystem: "GoldenJourneySeed",
            Sender: "bids@sec.com.sa",
            Subject: $"RFQ {reference} — six line golden scenario",
            OriginalFileName: $"{reference}.xlsx",
            MimeType: "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            FileSize: 20480,
            // Deterministic content hash: the same document identity on every replay, which is
            // what makes duplicate/revision behaviour reproducible.
            ContentHash: DeterministicHash(key),
            SourceDocumentId: null,
            ExtractionJobId: null,
            SourceReceivedAtUtc: now,
            IngestedAtUtc: now,
            ProcessingPath: LeadProcessingPath.Deterministic,
            ExternalAiUsed: false,
            ExternalCost: null,
            ActorType: "Service",
            ActorId: Actor,
            CorrelationId: key), CancellationToken.None);

        var lead = await db.Leads.IgnoreQueryFilters()
            .Include(l => l.LeadItems)
            .SingleAsync(l => l.Id == result.LeadId);

        // Starting state only, through the domain: a confirmed customer and a qualified lead.
        // No acknowledgement, exclusion, RFQ, participation or quote — those are the browser's job.
        if (!lead.CustomerId.HasValue)
            lead.ResolveCommercialIdentity(customerId, null, "CUSTOMER_CONFIRMED");
        lead.CommercialFactsVerified = true;
        await db.SaveChangesAsync();

        // Lead status is domain-protected: "Lead status must be changed through the governed
        // lifecycle command." Setting LeadStatusId directly is refused, and rightly — the
        // transition writes a lifecycle event and bumps the version. Qualify through the same
        // service the product uses, inside its own transaction.
        // Walk the REAL lead lifecycle rather than jumping to QUALIFIED. The policy graph
        // (LifecyclePolicy.LeadTransitions) allows RECEIVED -> PENDING_IDENTIFICATION ->
        // ASSIGNED -> UNDER_REVIEW -> QUALIFIED, and refuses any shortcut. A seeded lead that
        // teleported into QUALIFIED would carry a lifecycle history no real lead can have, and
        // the browser test would then be certifying a state the product cannot produce.
        // TransitionLeadAsync owns its own retriable transaction; opening one here fails against
        // NpgsqlRetryingExecutionStrategy.
        var lifecycle = new LifecycleApplicationService(db);
        foreach (var target in new[] { "PENDING_IDENTIFICATION", "ASSIGNED", "UNDER_REVIEW", "QUALIFIED" })
        {
            var current = await db.Leads.IgnoreQueryFilters()
                .Include(l => l.LeadStatus)
                .SingleAsync(l => l.Id == lead.Id);
            if (LifecyclePolicy.Canonicalize("Lead", current.LeadStatus?.SetupCode, current.LeadStatus?.SetupValue) == "QUALIFIED")
                break;
            await lifecycle.TransitionLeadAsync(
                businessUnitId, current.Id, new LifecycleActor(Actor, "GoldenJourneySeed"),
                new LifecycleTransitionCommand(target, current.LifecycleVersion, null, null,
                    "Seed", $"golden-journey-{current.Id}", $"lead-{current.Id}",
                    $"golden-journey-{target.ToLowerInvariant()}:{businessUnitId}:{current.Id}"),
                false, CancellationToken.None);
            db.ChangeTracker.Clear();
        }
        return lead.Id;
    }

    /// <summary>Stable per-tenant batch id — a random Guid would break replay idempotency.</summary>
    private static Guid DeterministicBatchId(long businessUnitId)
    {
        var bytes = new byte[16];
        BitConverter.GetBytes(businessUnitId).CopyTo(bytes, 0);
        "GOLDEN"u8.ToArray().CopyTo(bytes, 8);
        return new Guid(bytes);
    }

    private static string DeterministicHash(string seed) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(seed))).ToLowerInvariant();

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
            // These roles all carried SetupCode "SUPER_ADMIN", which the old name rule read as
            // super admin. Rank is explicit now; Owner keeps the golden journey behaving exactly
            // as it did before the column existed.
            RoleRank = ERP_RFQ_Automation.Authorization.RoleRanks.Owner,
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


    /// <summary>
    /// Refuses to hand a wrong starting state to the browser test.
    ///
    /// <para>A browser suite that begins from the wrong state fails somewhere in the middle with
    /// a misleading message, and the hours go into the wrong place — as they did when a Lead with
    /// no current revision surfaced only as a 409 four steps into the journey. These assertions
    /// state the contract the test depends on, and fail here where the cause is obvious.</para>
    /// </summary>
    private static async Task AssertStartingStateAsync(
        ErpRfqAutomationContext db, long businessUnitId, long leadId, ILogger logger)
    {
        var problems = new List<string>();

        var leads = await db.Leads.IgnoreQueryFilters()
            .Where(l => l.BusinessUnitId == businessUnitId && l.Rfqno == "E2E-GOLDEN-A-001")
            .ToListAsync();
        if (leads.Count != 1) problems.Add($"expected exactly 1 canonical Lead, found {leads.Count}");

        var lead = leads.FirstOrDefault(l => l.Id == leadId);
        if (lead is null) problems.Add($"lead {leadId} is not the canonical golden lead");
        else
        {
            if (!lead.CurrentRevisionId.HasValue)
                problems.Add("lead has no immutable current revision — conversion would refuse it");
            if (!lead.CustomerId.HasValue) problems.Add("lead has no confirmed customer");
            if (lead.CommercialCaseId <= 0) problems.Add("lead has no CommercialCaseId");
            if (string.IsNullOrWhiteSpace(lead.CommercialCaseReference))
                problems.Add("lead has no NexoraSerial");
        }

        var revisions = await db.Set<LeadRevision>().IgnoreQueryFilters()
            .Include(r => r.Items)
            .Where(r => r.BusinessUnitId == businessUnitId && r.LeadId == leadId)
            .ToListAsync();
        if (revisions.Count != 1) problems.Add($"expected exactly 1 revision, found {revisions.Count}");
        var revision = revisions.FirstOrDefault();
        if (revision is not null)
        {
            if (revision.RevisionNumber != 1) problems.Add($"expected revision 1, found {revision.RevisionNumber}");
            if (lead?.CurrentRevisionId != revision.Id) problems.Add("lead does not point at its only revision");
            if (revision.EstablishedByOccurrenceId <= 0) problems.Add("revision is not linked to a source occurrence");
            if (revision.Items.Count != 6) problems.Add($"expected 6 revision lines, found {revision.Items.Count}");
        }

        var lineCount = await db.LeadItems.IgnoreQueryFilters().CountAsync(i => i.LeadId == leadId);
        if (lineCount != 6) problems.Add($"expected 6 lead lines, found {lineCount}");

        // Nothing the browser test is supposed to CREATE may already exist, or its assertions
        // prove nothing.
        var rfqs = await db.Rfqs.IgnoreQueryFilters().CountAsync(r => r.LeadId == leadId);
        if (rfqs != 0) problems.Add($"expected no RFQ before the browser converts, found {rfqs}");
        var quotes = await db.Quotes.IgnoreQueryFilters()
            .CountAsync(q => q.Rfq != null && q.Rfq.LeadId == leadId);
        if (quotes != 0) problems.Add($"expected no Quote before the browser prepares one, found {quotes}");
        var decided = await db.Rfqitems.IgnoreQueryFilters()
            .CountAsync(i => i.Rfq.LeadId == leadId && i.ParticipationDecision != Rfqitem.ParticipationPending);
        if (decided != 0) problems.Add($"expected no participation decisions, found {decided}");

        if (problems.Count > 0)
            throw new InvalidOperationException(
                "Golden journey starting state is invalid, so the browser test would assert against the "
                + $"wrong scenario: {string.Join("; ", problems)}.");

        logger.LogInformation(
            "Golden starting state verified: 1 lead, revision 1 with 6 lines linked to its occurrence, "
            + "customer confirmed, no RFQ, no Quote, no participation decisions.");
    }
}
