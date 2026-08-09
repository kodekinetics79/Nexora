using System.Security.Claims;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Platform.Auth;
using ERP_RFQ_Automation.Platform.Controllers;
using ERP_RFQ_Automation.Platform.Models;
using ERP_RFQ_Automation.Platform.Operations;
using ERP_RFQ_Automation.Platform.Services;
using ERP_RFQ_Automation.QuoteDelivery;
using ERP_RFQ_Automation.Tests.Support;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace ERP_RFQ_Automation.Tests;

public sealed class PlatformDeadLetterRecoveryTests
{
    [Fact]
    public async Task Quote_delivery_recovery_is_tenant_bound_atomic_audited_and_idempotent()
    {
        using var database = new TestDb();
        await using var db = database.ContextFor(null);
        Seed.EnsureBusinessUnit(db, 910);
        db.Set<Tenant>().Add(new Tenant
        {
            Id = 91, Name = "Recovery tenant", Slug = "recovery-tenant-91",
            Status = TenantStatus.Active, PrimaryBusinessUnitId = 910
        });
        db.Quotes.Add(new Quote
        {
            Id = 911, BusinessUnitId = 910, QuoteNo = "Q-RECOVERY-911",
            QuoteDate = DateTime.UtcNow, ValidUntil = DateTime.UtcNow.AddDays(7),
            TotalAmount = 125, CreatedBy = "tests", CreatedDate = DateTime.UtcNow
        });
        db.QuoteDeliveryRequests.Add(new QuoteDeliveryRequest
        {
            Id = 912, BusinessUnitId = 910, QuoteId = 911,
            IdempotencyKey = "original-delivery", RecipientEmail = "buyer@example.test",
            Subject = "Quote", Body = "Attached", AttachmentFileName = "quote.pdf",
            RequestedOn = DateTime.UtcNow.AddHours(-1), AvailableOn = DateTime.UtcNow.AddHours(-1),
            AttemptCount = 5, DeadLetteredOn = DateTime.UtcNow.AddMinutes(-10),
            LastErrorCode = "provider_rejected", Version = 3
        });
        await db.SaveChangesAsync();
        var service = Service(db);
        var command = new RecoverPlatformDeadLetterCommand(
            PlatformDeadLetterQueues.QuoteDelivery, 912,
            "Owner verified corrected provider configuration and approved retry.", "quote-retry-912");

        var first = await service.RecoverAsync(91, command, Actor(), null, default);
        var replay = await service.RecoverAsync(91, command, Actor(), null, default);

        Assert.Equal("RetryQueued", first.Status);
        Assert.False(first.IdempotentReplay);
        Assert.True(replay.IdempotentReplay);
        var row = await db.QuoteDeliveryRequests.SingleAsync(x => x.Id == 912);
        Assert.Null(row.DeadLetteredOn);
        Assert.Null(row.LastErrorCode);
        Assert.Equal(0, row.AttemptCount);
        Assert.Equal(4, row.Version);
        var occurrence = await db.Set<PlatformAuditLog>().SingleAsync(x =>
            x.Action == PlatformDeadLetterRecoveryService.AuditAction);
        Assert.Equal(91, occurrence.ActAsTenantId);
        Assert.Contains("quote-retry-912", occurrence.Metadata, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Recovery_cannot_cross_the_registered_tenant_boundary()
    {
        using var database = new TestDb();
        await using var db = database.ContextFor(null);
        Seed.EnsureBusinessUnit(db, 920);
        Seed.EnsureBusinessUnit(db, 930);
        db.Set<Tenant>().AddRange(
            new Tenant { Id = 92, Name = "Tenant 92", Slug = "tenant-92", Status = TenantStatus.Active, PrimaryBusinessUnitId = 920 },
            new Tenant { Id = 93, Name = "Tenant 93", Slug = "tenant-93", Status = TenantStatus.Active, PrimaryBusinessUnitId = 930 });
        db.Quotes.Add(new Quote
        {
            Id = 931, BusinessUnitId = 930, QuoteNo = "Q-OTHER-931",
            QuoteDate = DateTime.UtcNow, ValidUntil = DateTime.UtcNow.AddDays(7),
            TotalAmount = 1, CreatedBy = "tests", CreatedDate = DateTime.UtcNow
        });
        db.QuoteDeliveryRequests.Add(new QuoteDeliveryRequest
        {
            Id = 932, BusinessUnitId = 930, QuoteId = 931, IdempotencyKey = "other",
            RecipientEmail = "other@example.test", Subject = "Quote", Body = "Attached",
            AttachmentFileName = "quote.pdf", RequestedOn = DateTime.UtcNow,
            AvailableOn = DateTime.UtcNow, DeadLetteredOn = DateTime.UtcNow, Version = 1
        });
        await db.SaveChangesAsync();

        await Assert.ThrowsAsync<KeyNotFoundException>(() => Service(db).RecoverAsync(92,
            new(PlatformDeadLetterQueues.QuoteDelivery, 932,
                "Attempted cross-boundary recovery must fail.", "cross-tenant-932"),
            Actor(), null, default));
        Assert.NotNull((await db.QuoteDeliveryRequests.SingleAsync(x => x.Id == 932)).DeadLetteredOn);
        Assert.Empty(await db.Set<PlatformAuditLog>().ToListAsync());
    }

    [Fact]
    public void Platform_recovery_endpoint_requires_owner_and_mfa()
    {
        var policies = typeof(PlatformOperationsController)
            .GetMethod(nameof(PlatformOperationsController.RecoverDeadLetter))!
            .GetCustomAttributes(typeof(AuthorizeAttribute), true)
            .Cast<AuthorizeAttribute>().Select(x => x.Policy).ToArray();

        Assert.Contains(PlatformPolicies.Owner, policies);
        Assert.Contains(PlatformPolicies.Mfa, policies);
    }

    private static PlatformDeadLetterRecoveryService Service(ErpRfqAutomationContext db) => new(
        db, new PlatformAuditService(db, NullLogger<PlatformAuditService>.Instance), null!);

    private static ClaimsPrincipal Actor() => new(new ClaimsIdentity(new[]
    {
        new Claim("sub", "7"), new Claim("email", "owner@nexora.test"),
        new Claim(PlatformAuthConstants.PlatformRoleClaim, nameof(PlatformRole.Owner)),
        new Claim(PlatformAuthConstants.AuthenticationMethodClaim, PlatformAuthConstants.MfaAuthenticationMethod)
    }, "test"));
}
