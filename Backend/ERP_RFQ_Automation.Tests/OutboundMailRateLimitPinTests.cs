using System.Reflection;
using ERP_RFQ_Automation.Controllers;
using ERP_RFQ_Automation.Platform.Hardening;
using Microsoft.AspNetCore.RateLimiting;
using Xunit;

/// <summary>
/// Security audit 2026-09-04, lane 4. The two tenant-plane actions added since 09-02 that put a
/// message on the wire per call must carry the smtp rate-limit policy (the same bound the
/// anonymous password-reset request has). Removing either attribute turns this red.
/// </summary>
public sealed class OutboundMailRateLimitPinTests
{
    [Theory]
    [InlineData(typeof(MailboxController), nameof(MailboxController.SendTest))]
    [InlineData(typeof(UserController), nameof(UserController.ResendInvitation))]
    public void Mail_sending_actions_are_bounded_by_the_smtp_policy(Type controller, string action)
    {
        var limiter = controller.GetMethod(action)!.GetCustomAttribute<EnableRateLimitingAttribute>(inherit: true);
        Assert.NotNull(limiter);
        Assert.Equal(RateLimitingExtensions.SmtpPolicy, limiter!.PolicyName);
    }
}
