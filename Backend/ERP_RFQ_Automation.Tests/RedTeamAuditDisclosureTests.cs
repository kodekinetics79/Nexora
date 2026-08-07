using System.Reflection;
using ERP_RFQ_Automation.Billing;
using ERP_RFQ_Automation.Billing.Controllers;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Platform.Auth;
using ERP_RFQ_Automation.Platform.Controllers;
using ERP_RFQ_Automation.Platform.Models;
using ERP_RFQ_Automation.Platform.Services;
using ERP_RFQ_Automation.Platform.Support;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// RED TEAM. What the audit explorer discloses, to whom.
///
/// <para>The finding: the control plane restricted a fact in one place and republished it in
/// another. Every mutation on <c>PlatformBillingController</c> (Owner | BillingAdmin) writes a full
/// before/after payload into <c>platform.PlatformAuditLogs</c>, and
/// <c>PlatformAuditExplorerController</c> — gated only on <c>PlatformScope</c>, which every operator
/// including ReadOnlyOps satisfies — handed the raw metadata back verbatim. The explorer's own class
/// docs argued that admitting the read-only tier "widens nobody's authority" because nothing there
/// mutates; that reasoning covers INTEGRITY and says nothing about CONFIDENTIALITY.</para>
///
/// <para>Closed by <c>PlatformAuditDisclosure</c>: payload disclosure now requires the policy that
/// gates the endpoint which wrote the entry, evaluated against the caller, on every surface that
/// republishes an audit payload. The tests below are the regression, not the reproduction.</para>
/// </summary>
public sealed class RedTeamAuditDisclosureTests
{
    private const string ExemptionReason =
        "Strategic design partner; free until the Q4 renewal is signed.";

    /// <summary>
    /// FINDING R6 (confidentiality). <c>Tenant.BillingModeReason</c> is the one commercial column
    /// 20260805105320 and 20260807002456 deliberately withhold from the tenant plane — the
    /// migration says so in as many words, and <see cref="ERP_RFQ_Automation.Platform.Entitlements.TenantAccessService"/>
    /// reduces it to a boolean inside the database rather than read it. It is also gated behind
    /// <c>Platform.Billing</c> on every endpoint that returns it. It reaches every operator anyway,
    /// verbatim, through the audit explorer.
    ///
    /// <para>PASSING reproduction: the value the billing plane hides is read back through a
    /// PlatformScope endpoint. The invariant that fails is the test below it.</para>
    /// </summary>
    [Fact]
    public async Task The_exemption_reason_billing_hides_is_withheld_from_the_read_only_tier()
    {
        using var db = new PlatformSupportTestDb();
        var tenantId = await PlatformSupportFixture.SeedTenantAsync(db, "redteam-disclosure");

        // The real Owner|BillingAdmin endpoint writes the real audit row.
        await using (var context = db.ContextFor(null))
        {
            var result = await Billing(context).SetTenantCommercialTerms(
                tenantId,
                new SetTenantCommercialTermsRequest(
                    BillingMode: nameof(TenantBillingMode.Partner),
                    BillingModeReason: ExemptionReason,
                    TrialEndsOn: null,
                    BillingStartsOn: null),
                CancellationToken.None);
            Assert.IsType<OkObjectResult>(result.Result);
        }

        // A ReadOnlyOps operator — cross-tenant read-only observability, and nothing else — reads
        // it back through the audit explorer. This is the exact route the auditor took.
        await using (var context = db.ContextFor(null))
        {
            var entryId = await FindAuditEntryAsync(context, tenantId, "billing.tenant.commercial-terms");
            var readOnly = PlatformSupportFixture.Explorer(
                context, PlatformSupportFixture.Actor(role: PlatformRole.ReadOnlyOps));

            var detail = Detail(await readOnly.Entry(entryId, CancellationToken.None));

            Assert.False(detail.MetadataDisclosed);
            Assert.Equal(PlatformPolicies.Billing, detail.MetadataPolicy);
            Assert.Null(detail.Metadata);
            Assert.Null(detail.Before);
            Assert.Null(detail.After);
            Assert.DoesNotContain(detail.Changes,
                c => ExemptionReason.Equals(c.Before, StringComparison.Ordinal)
                     || ExemptionReason.Equals(c.After, StringComparison.Ordinal));

            // The journey survives the fix: the tier still learns that the commercial terms moved,
            // WHEN, by WHOM, and WHICH fields changed — everything except what they now say.
            Assert.Equal("billing.tenant.commercial-terms", detail.Action);
            Assert.Equal(tenantId, detail.TenantId);
            Assert.Contains(detail.Changes, c => c.Field.Contains("BillingMode", StringComparison.OrdinalIgnoreCase));
            Assert.All(detail.Changes, c =>
            {
                Assert.Null(c.Before);
                Assert.Null(c.After);
            });
        }

        // And the tier that owns the fact still reads it in full, or the fix would have replaced a
        // confidentiality defect with a blindfolded billing team.
        await using (var context = db.ContextFor(null))
        {
            var entryId = await FindAuditEntryAsync(context, tenantId, "billing.tenant.commercial-terms");
            var billingAdmin = PlatformSupportFixture.Explorer(
                context, PlatformSupportFixture.Actor(role: PlatformRole.BillingAdmin));

            var detail = Detail(await billingAdmin.Entry(entryId, CancellationToken.None));

            Assert.True(detail.MetadataDisclosed);
            Assert.NotNull(detail.Metadata);
            Assert.Contains(ExemptionReason, detail.Metadata!.Value.GetRawText(), StringComparison.Ordinal);
            Assert.NotNull(detail.After);
            Assert.Contains(ExemptionReason, detail.After!.Value.GetRawText(), StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// FINDING R6, as the invariant, now CLOSED.
    ///
    /// <para>The original assertion here pinned one candidate remedy — a class-level
    /// <c>Platform.Billing</c> attribute on the explorer — and the doc comment offered a second
    /// (stop writing the reason into the payload). Neither is what shipped, and both are worse than
    /// what did: a class gate would take the audit log away from the support and read-only tiers
    /// wholesale, and thinning the payload would blind the billing tier to its own history to
    /// protect it from itself. The assertion is therefore rewritten to the INVARIANT the finding was
    /// actually about, which is the sentence in its own title: an audit explorer must never be a more
    /// open door onto a payload than the endpoint that wrote it.</para>
    ///
    /// <para>Expressed structurally, so it holds for verbs that do not exist yet: for every action
    /// the disclosure table names, the policy required to READ its payload must be the policy that
    /// gates the endpoint that WRITES it — checked here against the writing controller's real
    /// <c>[Authorize]</c> attribute, not against a copy of it — and every unnamed action must fail
    /// closed.</para>
    /// </summary>
    [Fact]
    public void The_audit_explorer_is_no_more_open_than_the_endpoints_whose_writes_it_republishes()
    {
        // The writers, read from the source of truth: whatever policy the controller declares today
        // is the policy its verbs' payloads require, so moving a controller's gate moves this test.
        var billingGate = Assert.Single(typeof(PlatformBillingController)
            .GetCustomAttributes<AuthorizeAttribute>(inherit: true).Select(a => a.Policy));
        var platformUserGate = Assert.Single(typeof(PlatformUsersController)
            .GetCustomAttributes<AuthorizeAttribute>(inherit: true).Select(a => a.Policy));
        var changePlanGate = Assert.Single(typeof(TenantsController)
            .GetMethod(nameof(TenantsController.ChangePlan))!
            .GetCustomAttributes<AuthorizeAttribute>().Select(a => a.Policy));

        Assert.Equal(billingGate, PlatformAuditDisclosure.RequiredPolicyFor("billing.tenant.commercial-terms"));
        Assert.Equal(billingGate, PlatformAuditDisclosure.RequiredPolicyFor("billing.tenant.rate-card"));
        Assert.Equal(billingGate, PlatformAuditDisclosure.RequiredPolicyFor("billing.statement.finalize"));
        Assert.Equal(billingGate, PlatformAuditDisclosure.RequiredPolicyFor("billing.ratecard.create"));
        Assert.Equal(billingGate, PlatformAuditDisclosure.RequiredPolicyFor("billing.ratecard.update"));
        Assert.Equal(billingGate, PlatformAuditDisclosure.RequiredPolicyFor("billing.statement.compute"));

        // Sec9: plan assignment is a BILLING operation even though it lives on the tenants
        // controller. Its payload inherits that, not the class gate around it.
        Assert.Equal(changePlanGate, PlatformAuditDisclosure.RequiredPolicyFor("tenant.plan.change"));

        Assert.Equal(platformUserGate, PlatformAuditDisclosure.RequiredPolicyFor("platform-user.role.change"));
        Assert.Equal(platformUserGate, PlatformAuditDisclosure.RequiredPolicyFor("platform-user.password.reset"));

        // The property that makes the fix generalise: a verb nobody registered is restricted, so
        // drift can only ever over-restrict. Owner is the only platform policy no other policy
        // satisfies, which is what makes it the safe default rather than merely a strict one.
        Assert.Equal(PlatformPolicies.Owner, PlatformAuditDisclosure.RequiredPolicyFor("some.module.invented.tomorrow"));
        Assert.Equal(PlatformPolicies.Owner, PlatformAuditDisclosure.FailClosedPolicy);
    }

    private static PlatformAuditEntryDetailDto Detail(ActionResult<PlatformAuditEntryDetailDto> result)
        => Assert.IsType<PlatformAuditEntryDetailDto>(
            Assert.IsType<OkObjectResult>(result.Result).Value);

    /// <summary>
    /// The half that is right, pinned: the ticket BODY — the field most likely to hold a customer's
    /// pasted personal data — is deliberately excluded from the desk's search predicate. Asserted
    /// so a later "make search better" change has to argue with a test.
    /// </summary>
    [Fact]
    public async Task Ticket_search_never_matches_on_the_body()
    {
        using var db = new PlatformSupportTestDb();
        var tenantId = await PlatformSupportFixture.SeedTenantAsync(db, "redteam-search");
        await PlatformSupportFixture.RaiseTicketAsync(db, tenantId, subject: "Cannot log in");

        await using var context = db.ContextFor(null);
        var tickets = PlatformSupportFixture.Tickets(context);

        var bySubject = await tickets.List(
            tenantId, null, null, null, null, false, "cannot log", null, null, 1, 50, CancellationToken.None);
        Assert.Equal(1, Page(bySubject).TotalCount);

        // A distinctive phrase that exists ONLY in the body.
        var byBody = await tickets.List(
            tenantId, null, null, null, null, false, "known-good password", null, null, 1, 50,
            CancellationToken.None);
        Assert.Equal(0, Page(byBody).TotalCount);
    }

    // ---------------------------------------------------------------------------------- helpers

    private static PagedResultDto<SupportTicketSummaryDto> Page(
        ActionResult<PagedResultDto<SupportTicketSummaryDto>> result)
        => Assert.IsType<PagedResultDto<SupportTicketSummaryDto>>(
            Assert.IsType<OkObjectResult>(result.Result).Value);

    private static PlatformBillingController Billing(ErpRfqAutomationContext context)
        => new(context,
            new BillingStatementService(context, NullLogger<BillingStatementService>.Instance),
            new PlatformAuditService(context, NullLogger<PlatformAuditService>.Instance),
            NullLogger<PlatformBillingController>.Instance)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = PlatformSupportFixture.Actor(role: PlatformRole.BillingAdmin)
                }
            }
        };

    private static async Task<long> FindAuditEntryAsync(
        ErpRfqAutomationContext context, long tenantId, string action)
    {
        var row = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions
            .FirstOrDefaultAsync(
                Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions
                    .AsNoTracking(context.Set<PlatformAuditLog>())
                    .Where(a => a.ActAsTenantId == tenantId && a.Action == action));
        Assert.NotNull(row);
        return row!.Id;
    }
}
