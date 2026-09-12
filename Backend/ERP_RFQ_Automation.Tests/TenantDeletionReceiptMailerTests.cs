using ERP_RFQ_Automation.Authorization;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Notifications;
using ERP_RFQ_Automation.Retention;
using ERP_RFQ_Automation.Tests.Support;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// The receipt is the "email" half of the owner's second-confirmation ask, and it is a receipt,
/// not a gate: it goes to every owner-rank administrator of the workspace after a real deletion,
/// and a mail failure can never fail the deletion that already happened.
/// </summary>
public sealed class TenantDeletionReceiptMailerTests
{
    private const long Tenant = 4_410;
    private const long OtherTenant = 4_411;
    private const long OwnerRole = 91;
    private const long MemberRole = 92;
    /// <summary>Setup_Master rows are keyed by SetupId alone, so the other tenant's owner role
    /// needs its own id. The gate only honours it for the other tenant, which is the point.</summary>
    private const long OtherTenantOwnerRole = 93;

    private sealed class RankByRoleId(params long[] ownerRoles) : IRoleGate
    {
        // Owner-rank in the tenant under test only; the other tenant's owner role is rank-owner
        // THERE, and the mailer must never ask about it because it never reads that tenant's users.
        public Task<bool> IsSuperAdminAsync(long roleId, long businessUnitId) =>
            Task.FromResult(businessUnitId == Tenant && ownerRoles.Contains(roleId));
        public Task<bool> IsManagerOrAdminAsync(long roleId, long businessUnitId) => IsSuperAdminAsync(roleId, businessUnitId);
        public Task<short> GetRoleRankAsync(long roleId, long businessUnitId) =>
            Task.FromResult(ownerRoles.Contains(roleId) ? RoleRanks.Owner : RoleRanks.Member);
        public Task<bool> CanManageRoleAsync(long callerRoleId, long? targetRoleId, long businessUnitId) => Task.FromResult(true);
    }

    private sealed class RecordingSender : IEmailSender
    {
        public List<EmailMessage> Sent { get; } = [];
        public bool Fail { get; set; }
        public Task<EmailDeliveryReceipt?> SendAsync(EmailMessage message, CancellationToken ct = default)
        {
            if (Fail) throw new InvalidOperationException("SMTP is down.");
            Sent.Add(message);
            return Task.FromResult<EmailDeliveryReceipt?>(new("test", "accepted", DateTimeOffset.UtcNow));
        }
    }

    private static readonly TenantDeletionReceipt Receipt = new(
        "47 stored document file(s) deleted",
        [new("Space freed", "1,932,735,283 bytes"), new("Documents kept back", "2")],
        "Quarterly storage reclaim");

    private static async Task<ErpRfqAutomationContext> SeedAsync(TestDb db)
    {
        var ctx = db.ContextFor(null);
        Seed.EnsureBusinessUnit(ctx, Tenant);
        Seed.EnsureBusinessUnit(ctx, OtherTenant);
        ctx.SetupMasters.AddRange(Role(Tenant, OwnerRole, "Owner"), Role(Tenant, MemberRole, "Sales"),
            Role(OtherTenant, OtherTenantOwnerRole, "Owner"));
        ctx.Users.AddRange(
            User(1, Tenant, OwnerRole, "Zahid", "Khan", "zahid@example.test", active: true),
            User(2, Tenant, OwnerRole, "Second", "Owner", "owner2@example.test", active: true),
            User(3, Tenant, OwnerRole, "Former", "Owner", "gone@example.test", active: false),
            User(4, Tenant, MemberRole, "Sales", "Rep", "rep@example.test", active: true),
            User(5, OtherTenant, OtherTenantOwnerRole, "Other", "Tenant", "other@example.test", active: true));
        await ctx.SaveChangesAsync();
        return ctx;
    }

    private static SetupMaster Role(long tenant, long setupId, string value) => new()
    {
        SetupId = setupId, SetupType = "Role", SetupValue = value, BusinessUnitId = tenant,
        IsActive = true, CreatedBy = "tests", CreatedOn = DateTime.UtcNow
    };

    private static User User(long id, long tenant, long role, string first, string last, string email, bool active) => new()
    {
        Id = id, Buid = tenant, RoleId = role, FirstName = first, LastName = last, Email = email,
        PasswordHash = "hash", ImageUrl = string.Empty, IsActive = active, CreatedBy = "tests", CreatedOn = DateTime.UtcNow
    };

    [Fact]
    public async Task Every_active_owner_of_the_workspace_gets_the_receipt_and_nobody_else()
    {
        using var db = new TestDb();
        await using var ctx = await SeedAsync(db);
        var sender = new RecordingSender();
        var mailer = new TenantDeletionReceiptMailer(ctx, new RankByRoleId(OwnerRole), sender, new NoopLogger<TenantDeletionReceiptMailer>());

        var notified = await mailer.SendAsync(Tenant, actorUserId: 1, Receipt, default);

        Assert.Equal(2, notified);
        var message = Assert.Single(sender.Sent);
        Assert.Equal(["zahid@example.test", "owner2@example.test"], message.To.Select(x => x.Address).ToArray());
        Assert.Contains("47 stored document file(s) deleted", message.Subject);
        Assert.Contains("Zahid Khan", message.TextBody);
        Assert.Contains("Quarterly storage reclaim", message.TextBody);
        Assert.Contains("1,932,735,283 bytes", message.TextBody);
        Assert.Contains("cannot be undone", message.TextBody);
        Assert.Equal(Tenant.ToString(), message.TenantId);
    }

    [Fact]
    public async Task A_mail_failure_reports_nobody_notified_and_never_throws()
    {
        using var db = new TestDb();
        await using var ctx = await SeedAsync(db);
        var sender = new RecordingSender { Fail = true };
        var mailer = new TenantDeletionReceiptMailer(ctx, new RankByRoleId(OwnerRole), sender, new NoopLogger<TenantDeletionReceiptMailer>());

        var notified = await mailer.SendAsync(Tenant, actorUserId: 1, Receipt, default);

        Assert.Equal(0, notified);
        Assert.Empty(sender.Sent);
    }

    [Fact]
    public async Task A_workspace_with_no_owner_rank_user_sends_nothing()
    {
        using var db = new TestDb();
        await using var ctx = await SeedAsync(db);
        var sender = new RecordingSender();
        var mailer = new TenantDeletionReceiptMailer(ctx, new RankByRoleId(), sender, new NoopLogger<TenantDeletionReceiptMailer>());

        Assert.Equal(0, await mailer.SendAsync(Tenant, actorUserId: 1, Receipt, default));
        Assert.Empty(sender.Sent);
    }
}
