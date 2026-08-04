using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Repositories;
using ERP_RFQ_Automation.Security;
using ERP_RFQ_Automation.Tests.Support;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;

namespace ERP_RFQ_Automation.Tests;

/// <summary>Regression cover for H5 (unrestricted file write) and H6 (no brute-force protection).</summary>
public sealed class UploadAndLoginHardeningTests : IDisposable
{
    private readonly string _webRoot =
        Path.Combine(Path.GetTempPath(), "erp-upload-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_webRoot)) Directory.Delete(_webRoot, recursive: true);
    }

    // ------------------------------------------------------- H5: product attachment write

    [Fact]
    public async Task Product_attachment_that_fails_inspection_writes_nothing_and_saves_nothing()
    {
        using var database = new TestDb();
        await using (var seed = database.ContextFor(null))
        {
            Seed.EnsureBusinessUnit(seed, 1);
            await seed.SaveChangesAsync();
        }

        await using var context = database.ContextFor(1);
        var inspection = new RejectingFileInspection();
        var repository = new ProductRepository(context, Environment(), inspection);

        // A classic disguised payload: traversal in the name, executable content.
        var payload = new FormFile(
            new MemoryStream("<?php system($_GET[0]); ?>"u8.ToArray()), 0, 27, "files", "../../../shell.php")
        {
            Headers = new HeaderDictionary(), ContentType = "application/x-php"
        };

        await Assert.ThrowsAsync<ArgumentException>(() => repository.AddAsync(
            new Product
            {
                Buid = 1, PartNo = "PN-H5", ProductName = "Hardening probe",
                CreatedBy = "test", CreatedOn = DateTime.UtcNow
            },
            [payload]));

        Assert.True(inspection.WasCalled);
        // The client path component must never reach the inspector either.
        Assert.Equal(["shell.php"], inspection.InspectedFileNames);
        // Inspection runs before the insert, so no half-written product is left behind.
        Assert.Empty(context.Products.Where(p => p.PartNo == "PN-H5").ToList());
        Assert.False(Directory.Exists(Path.Combine(_webRoot, "ProductAttachments")));
    }

    [Fact]
    public async Task Cleared_product_attachment_is_stored_under_a_generated_name()
    {
        using var database = new TestDb();
        await using (var seed = database.ContextFor(null))
        {
            Seed.EnsureBusinessUnit(seed, 1);
            await seed.SaveChangesAsync();
        }

        await using var context = database.ContextFor(1);
        var repository = new ProductRepository(context, Environment(), new ClearingFileInspection());

        var file = new FormFile(new MemoryStream("%PDF-1.4 hello"u8.ToArray()), 0, 14, "files", "datasheet.pdf")
        {
            Headers = new HeaderDictionary(), ContentType = "application/pdf"
        };

        await repository.AddAsync(
            new Product
            {
                Buid = 1, PartNo = "PN-OK", ProductName = "Good product",
                CreatedBy = "test", CreatedOn = DateTime.UtcNow
            },
            [file]);

        var stored = Assert.Single(context.ProductAttachments.ToList());
        var storedName = Path.GetFileName(stored.Locations!);

        // The client filename is metadata only; the on-disk name is a GUID + inspected extension.
        Assert.Equal("datasheet.pdf", stored.FileName);
        Assert.EndsWith(".pdf", storedName, StringComparison.Ordinal);
        Assert.DoesNotContain("datasheet", storedName, StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(Path.Combine(_webRoot, "ProductAttachments", storedName)));
    }

    // ------------------------------------------------------------ H6: login lockout

    [Fact]
    public async Task Repeated_failures_lock_the_account_out_progressively()
    {
        using var database = new TestDb();
        var clock = new MutableClock(new DateTimeOffset(2026, 8, 4, 12, 0, 0, TimeSpan.Zero));
        var options = new LoginThrottleOptions
        {
            FailureThreshold = 3,
            FailureWindow = TimeSpan.FromMinutes(15),
            BaseLockout = TimeSpan.FromMinutes(1),
            MaximumLockout = TimeSpan.FromMinutes(60)
        };

        await using var context = database.ContextFor(null);
        var throttle = Throttle(context, options, clock);

        Assert.False((await throttle.CheckAsync(LoginPlane.Tenant, "victim@example.com")).IsLockedOut);

        await throttle.RegisterFailureAsync(LoginPlane.Tenant, "victim@example.com");
        await throttle.RegisterFailureAsync(LoginPlane.Tenant, "victim@example.com");
        Assert.False((await throttle.CheckAsync(LoginPlane.Tenant, "victim@example.com")).IsLockedOut);

        await throttle.RegisterFailureAsync(LoginPlane.Tenant, "victim@example.com");
        var first = await throttle.CheckAsync(LoginPlane.Tenant, "victim@example.com");
        Assert.True(first.IsLockedOut);
        Assert.Equal(TimeSpan.FromMinutes(1), first.RetryAfter);

        // The lockout window lengthens with each further failure.
        clock.Advance(TimeSpan.FromMinutes(2));
        await throttle.RegisterFailureAsync(LoginPlane.Tenant, "victim@example.com");
        Assert.Equal(TimeSpan.FromMinutes(2),
            (await throttle.CheckAsync(LoginPlane.Tenant, "victim@example.com")).RetryAfter);

        // ... and it expires on its own.
        clock.Advance(TimeSpan.FromMinutes(3));
        Assert.False((await throttle.CheckAsync(LoginPlane.Tenant, "victim@example.com")).IsLockedOut);
    }

    [Fact]
    public async Task Lockout_is_persisted_so_it_survives_a_restart()
    {
        using var database = new TestDb();
        var clock = new MutableClock(new DateTimeOffset(2026, 8, 4, 12, 0, 0, TimeSpan.Zero));
        var options = new LoginThrottleOptions { FailureThreshold = 2 };

        await using (var writer = database.ContextFor(null))
        {
            var throttle = Throttle(writer, options, clock);
            await throttle.RegisterFailureAsync(LoginPlane.Tenant, "victim@example.com");
            await throttle.RegisterFailureAsync(LoginPlane.Tenant, "victim@example.com");
        }

        // A brand-new context models a restarted / second instance: the counter is in the
        // database, not in process memory, so the lockout still applies.
        await using var reader = database.ContextFor(null);
        Assert.True((await Throttle(reader, options, clock)
            .CheckAsync(LoginPlane.Tenant, "victim@example.com")).IsLockedOut);
    }

    [Fact]
    public async Task Planes_are_independent_and_case_insensitive()
    {
        using var database = new TestDb();
        var clock = new MutableClock(new DateTimeOffset(2026, 8, 4, 12, 0, 0, TimeSpan.Zero));
        var options = new LoginThrottleOptions { FailureThreshold = 2 };

        await using var context = database.ContextFor(null);
        var throttle = Throttle(context, options, clock);

        // Mixed case must hit the same counter (the identifier is normalised).
        await throttle.RegisterFailureAsync(LoginPlane.Tenant, "Ops@Example.COM");
        await throttle.RegisterFailureAsync(LoginPlane.Tenant, "ops@example.com");

        Assert.True((await throttle.CheckAsync(LoginPlane.Tenant, "ops@example.com")).IsLockedOut);
        // The platform plane keeps its own namespace - the boundary is not blurred.
        Assert.False((await throttle.CheckAsync(LoginPlane.Platform, "ops@example.com")).IsLockedOut);
    }

    [Fact]
    public async Task A_successful_login_clears_the_counter()
    {
        using var database = new TestDb();
        var clock = new MutableClock(new DateTimeOffset(2026, 8, 4, 12, 0, 0, TimeSpan.Zero));
        var options = new LoginThrottleOptions { FailureThreshold = 2 };

        await using var context = database.ContextFor(null);
        var throttle = Throttle(context, options, clock);

        await throttle.RegisterFailureAsync(LoginPlane.Tenant, "ops@example.com");
        await throttle.RegisterSuccessAsync(LoginPlane.Tenant, "ops@example.com");
        await throttle.RegisterFailureAsync(LoginPlane.Tenant, "ops@example.com");

        Assert.False((await throttle.CheckAsync(LoginPlane.Tenant, "ops@example.com")).IsLockedOut);
    }

    // ------------------------------------------------------------------- helpers

    private static LoginAttemptThrottle Throttle(
        ErpRfqAutomationContext context, LoginThrottleOptions options, TimeProvider clock)
        => new(context, options, NullLogger<LoginAttemptThrottle>.Instance, clock);

    private TestWebHostEnvironment Environment() => new(_webRoot);

    private sealed class MutableClock(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan delta) => _now = _now.Add(delta);
    }

    private sealed class TestWebHostEnvironment(string webRoot) : IWebHostEnvironment
    {
        public string ApplicationName { get; set; } = "UploadHardeningTests";
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
        public string WebRootPath { get; set; } = webRoot;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
        public string ContentRootPath { get; set; } = webRoot;
        public string EnvironmentName { get; set; } = "Testing";
    }
}
