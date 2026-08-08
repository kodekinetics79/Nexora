using ERP_RFQ_Automation.Infrastructure;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;

namespace ERP_RFQ_Automation.Tests;

public sealed class BuildIdentityTests
{
    [Fact]
    public void Build_identity_accepts_only_a_bounded_git_revision()
    {
        Assert.Equal("a1b2c3d4", BuildIdentity.Revision(name =>
            name == "NEXORA_BUILD_REVISION" ? "A1B2C3D4" : null));
        Assert.Equal("unknown", BuildIdentity.Revision(name =>
            name == "NEXORA_BUILD_REVISION" ? "secret=value" : null));
    }

    [Fact]
    public void Build_identity_contains_no_configuration_or_migration_detail()
    {
        var response = BuildIdentity.Current(new TestEnvironment());

        Assert.NotEmpty(response.Version);
        Assert.Equal("Certification", response.Environment);
        Assert.Equal(3, typeof(BuildIdentityResponse).GetProperties().Length);
    }

    private sealed class TestEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Certification";
        public string ApplicationName { get; set; } = "Nexora";
        public string ContentRootPath { get; set; } = "/tmp";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
