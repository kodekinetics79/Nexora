using System.Net;
using System.Net.Http.Headers;

namespace ERP_RFQ_Automation.Tests.HttpIntegration;

/// <summary>
/// Token revocation on the tenant plane, proved against the real Program.cs pipeline
/// (docs/design/token-revocation.md). Before this change a deactivated user kept full access for
/// the remaining life of the token — up to 60 minutes — because nothing between the signature
/// check and the controller ever looked at the account again.
/// </summary>
[Collection(Release01BHttpCollection.Name)]
[Trait("Category", "PostgreSQL")]
public sealed class TenantSessionRevocationHttpTests(Release01BHttpApplication app)
{
    private const string Probe = "/api/operations/readiness";

    [Fact]
    public async Task A_token_whose_stamp_no_longer_matches_the_account_is_refused()
    {
        var (userId, stamp) = await app.CreateUserAsync(
            Release01BHttpApplication.TenantA, Release01BHttpApplication.AllowedRole,
            $"stale-stamp-{Guid.NewGuid():N}@nexora.invalid");
        try
        {
            using var current = Client(userId, stamp);
            Assert.Equal(HttpStatusCode.OK, (await current.GetAsync(Probe)).StatusCode);

            // Same signature, same claims, a stamp the account never had: the token is
            // cryptographically valid and must still be refused.
            using var stale = Client(userId, "0123456789abcdef0123456789abcdef");
            Assert.Equal(HttpStatusCode.Unauthorized, (await stale.GetAsync(Probe)).StatusCode);
        }
        finally
        {
            await app.RemoveUserAsync(userId);
        }
    }

    [Fact]
    public async Task A_token_issued_before_deactivation_is_refused_within_the_cache_window()
    {
        var (userId, stamp) = await app.CreateUserAsync(
            Release01BHttpApplication.TenantA, Release01BHttpApplication.AllowedRole,
            $"deactivated-{Guid.NewGuid():N}@nexora.invalid");
        try
        {
            using var client = Client(userId, stamp);
            // Warm the verdict cache with the "still valid" answer.
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(Probe)).StatusCode);

            // Deactivate the way another instance would — row only, no eviction here. The cached
            // verdict is allowed to stand for at most TenantSessionValidator.CacheTtl (30 s).
            await app.DeactivateUserDirectlyAsync(userId);
            Assert.Equal(
                ERP_RFQ_Automation.Security.ReadOnlyImpersonationMiddleware.CacheTtl,
                ERP_RFQ_Automation.Security.TenantSessionValidator.CacheTtl);

            // A same-process rotation site evicts, which is what bounds the window to "next
            // request" for an administrator's own deactivate. The bound is exercised exactly.
            app.EvictTenantSession(userId);
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(Probe)).StatusCode);
        }
        finally
        {
            await app.RemoveUserAsync(userId);
        }
    }

    [Fact]
    public async Task A_token_minted_before_the_stamp_existed_is_still_accepted_by_default()
    {
        // Every other HTTP test in this collection mints tokens with no sst claim. This is the
        // documented compatibility window: until Auth:RequireSecurityStamp is turned on, a token
        // this build did not issue is honoured to its expiry rather than logging everyone out on
        // the deploy. The assertion exists so the decision is visible, not so it is permanent.
        using var legacy = Client(Release01BHttpApplication.GrowthRepUser, securityStamp: null);
        Assert.Equal(HttpStatusCode.OK, (await legacy.GetAsync(Probe)).StatusCode);
    }

    [Fact]
    public async Task A_stamped_token_for_an_account_that_does_not_exist_is_refused()
    {
        using var ghost = Client(999_999_001, "0123456789abcdef0123456789abcdef");
        Assert.Equal(HttpStatusCode.Unauthorized, (await ghost.GetAsync(Probe)).StatusCode);
    }

    private HttpClient Client(long userId, string? securityStamp)
    {
        var client = app.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
            app.Token(Release01BHttpApplication.AllowedRole, Release01BHttpApplication.TenantA, userId, securityStamp));
        return client;
    }
}
