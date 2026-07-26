using System.Reflection;
using System.Security.Claims;
using ERP_RFQ_Automation.Controllers;
using ERP_RFQ_Automation.DTOs.LookupDTOs;
using ERP_RFQ_Automation.DTOs.RfqDTOs;
using ERP_RFQ_Automation.Interfaces;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Repositories;
using ERP_RFQ_Automation.Tests.Support;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.Tests;

public sealed class RfqServerAuthorityTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("invalid")]
    [InlineData("0")]
    [InlineData("-1")]
    public async Task Tenant_owned_actions_fail_closed_without_valid_authenticated_tenant(string? claimValue)
    {
        var repository = new RecordingRfqRepository();
        var controller = Controller(repository, claimValue, "operator@example.test");

        Assert.IsType<UnauthorizedResult>((await controller.GetAll()).Result);
        Assert.IsType<UnauthorizedResult>((await controller.GetById(11)).Result);
        Assert.IsType<UnauthorizedResult>(await controller.Update(11, UpdateRequest()));
        Assert.IsType<UnauthorizedResult>(await controller.Delete(11));
        Assert.Equal(0, repository.CallCount);
    }

    [Fact]
    public async Task Read_and_delete_actions_use_only_the_authenticated_tenant()
    {
        var repository = new RecordingRfqRepository();
        var controller = Controller(repository, "41", "operator@example.test");
        controller.HttpContext.Request.QueryString = new QueryString("?businessUnitId=999");

        Assert.IsType<OkObjectResult>((await controller.GetAll()).Result);
        Assert.IsType<OkObjectResult>((await controller.GetById(11)).Result);
        Assert.IsType<NoContentResult>(await controller.Delete(11));

        Assert.Equal([41L, 41L, 41L], repository.TenantIds);
        foreach (var methodName in new[] { nameof(RfqController.GetAll), nameof(RfqController.GetById), nameof(RfqController.Delete) })
        {
            var method = typeof(RfqController).GetMethod(methodName)!;
            Assert.DoesNotContain(method.GetParameters(), p =>
                string.Equals(p.Name, "businessUnitId", StringComparison.OrdinalIgnoreCase));
        }
    }

    [Fact]
    public async Task Update_derives_tenant_and_actor_and_has_no_client_authority_fields()
    {
        var repository = new RecordingRfqRepository();
        var controller = Controller(repository, "41", "operator@example.test");
        var request = UpdateRequest();

        Assert.IsType<NoContentResult>(await controller.Update(11, request));

        var mutation = Assert.IsType<Rfq>(repository.Updated);
        Assert.Equal(41, mutation.BusinessUnitId);
        Assert.Equal("operator@example.test", mutation.ModifiedBy);
        Assert.All(mutation.Rfqitems, item => Assert.Equal("operator@example.test", item.ModifiedBy));
        Assert.Null(mutation.Rfqno);

        var exposedNames = typeof(RfqUpdateRequestDTO).GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .Select(property => property.Name)
            .ToArray();
        Assert.DoesNotContain(nameof(Rfq.Rfqno), exposedNames);
        Assert.DoesNotContain(nameof(Rfq.BusinessUnitId), exposedNames);
        Assert.DoesNotContain(nameof(Rfq.ModifiedBy), exposedNames);
        Assert.Null(typeof(RfqitemUpdateRequestDTO).GetProperty(nameof(Rfqitem.ModifiedBy)));
    }

    [Fact]
    public async Task Update_fails_closed_without_authenticated_actor()
    {
        var repository = new RecordingRfqRepository();
        var controller = Controller(repository, "41", actor: null);

        Assert.IsType<UnauthorizedResult>(await controller.Update(11, UpdateRequest()));
        Assert.Null(repository.Updated);
    }

    [Fact]
    public async Task Repository_update_preserves_the_server_generated_rfq_number()
    {
        using var db = new TestDb();
        await using var context = db.ContextFor(null);
        const string serverNumber = "NXR-RFQ-41-2026-00000017";
        Seed.EnsureBusinessUnit(context, 41);
        context.Rfqs.Add(new Rfq
        {
            Id = 11,
            Rfqno = serverNumber,
            BuyersName = "Original buyer",
            RecDate = DateTime.UtcNow,
            BusinessUnitId = 41,
            CreatedBy = "creator@example.test",
            CreatedDate = DateTime.UtcNow
        });
        await context.SaveChangesAsync();

        await new RfqRepository(context).UpdateAsync(new Rfq
        {
            Id = 11,
            Rfqno = "FORGED-RFQ-NUMBER",
            BuyersName = "Updated buyer",
            RecDate = DateTime.UtcNow,
            BusinessUnitId = 41,
            ModifiedBy = "operator@example.test"
        });

        var persisted = await context.Rfqs.AsNoTracking().SingleAsync(rfq => rfq.Id == 11);
        Assert.Equal(serverNumber, persisted.Rfqno);
        Assert.Equal("Updated buyer", persisted.BuyersName);
        Assert.Equal("operator@example.test", persisted.ModifiedBy);
    }

    [Fact]
    public void Rfq_number_sequence_is_schema_qualified()
    {
        var source = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(), "Backend/ERP_RFQ_Automation/Repositories/RfqRepository.cs"));

        Assert.Contains("nextval('public.nexora_rfq_number_seq')", source, StringComparison.Ordinal);
        Assert.DoesNotContain("nextval('nexora_rfq_number_seq')", source, StringComparison.Ordinal);
    }

    private static RfqController Controller(
        IRfqRepository repository,
        string? businessUnitClaim,
        string? actor)
    {
        var claims = new List<Claim>();
        if (businessUnitClaim is not null)
            claims.Add(new Claim("businessUnitId", businessUnitClaim));
        if (actor is not null)
            claims.Add(new Claim(ClaimTypes.Email, actor));

        return new RfqController(repository, null!, null!, null!)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(claims, "test"))
                }
            }
        };
    }

    private static RfqUpdateRequestDTO UpdateRequest() => new()
    {
        Id = 11,
        BuyersName = "Updated buyer",
        RecDate = DateTime.UtcNow,
        Rfqitems =
        [
            new RfqitemUpdateRequestDTO
            {
                Id = 17,
                Quantity = 2,
                BidClosingDateLine = DateTime.UtcNow
            }
        ]
    };

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "Backend")))
            directory = directory.Parent;
        return directory?.FullName
            ?? throw new DirectoryNotFoundException("Could not locate the repository root.");
    }

    private sealed class RecordingRfqRepository : IRfqRepository
    {
        public List<long> TenantIds { get; } = [];
        public Rfq? Updated { get; private set; }
        public int CallCount { get; private set; }

        public Task<(IEnumerable<RfqResponseDTO>, int TotalItems)> GetAllAsync(
            long businessUnitId, int pageNumber = 1, int pageSize = 10, string? search = null,
            bool? isActive = null, long? assignedToId = null, string? createdBy = null,
            long? rfqStatusId = null, string? rfqStatusCode = null, string? readiness = null)
        {
            Record(businessUnitId);
            return Task.FromResult<(IEnumerable<RfqResponseDTO>, int)>(([], 0));
        }

        public Task<RfqResponseDTO> GetByIdAsync(long id, long businessUnitId)
        {
            Record(businessUnitId);
            return Task.FromResult(new RfqResponseDTO
            {
                Id = id,
                Rfqno = "NXR-RFQ-41-2026-00000001",
                CreatedBy = "seed"
            });
        }

        public Task UpdateAsync(Rfq rfq)
        {
            CallCount++;
            Updated = rfq;
            return Task.CompletedTask;
        }

        public Task DeleteAsync(long id, long businessUnitId)
        {
            Record(businessUnitId);
            return Task.CompletedTask;
        }

        private void Record(long businessUnitId)
        {
            CallCount++;
            TenantIds.Add(businessUnitId);
        }

        public Task AddAsync(Rfq rfq) => throw new NotSupportedException();
        public Task<long> ApproveAsync(long id, string approvedBy, long businessUnitId, long? customerId = null) => throw new NotSupportedException();
        public Task<List<RFQTypeLookupDTO>> GetRFQTypeAsync() => throw new NotSupportedException();
        public Task<RfqStatsDTO> GetRfqStatsAsync(long businessUnitId) => throw new NotSupportedException();
    }
}
