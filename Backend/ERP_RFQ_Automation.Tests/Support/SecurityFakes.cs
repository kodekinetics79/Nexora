using ERP_RFQ_Automation.Authorization;
using ERP_RFQ_Automation.Security.DocumentInspection;

namespace ERP_RFQ_Automation.Tests.Support;

/// <summary>Inspection double that clears every file. Records what it was asked to inspect.</summary>
public sealed class ClearingFileInspection : IFileInspectionService
{
    public List<string> InspectedFileNames { get; } = new();

    public Task<FileInspectionResult> InspectAsync(
        FileInspectionRequest request, CancellationToken cancellationToken = default)
    {
        InspectedFileNames.Add(request.FileName);
        return Task.FromResult(new FileInspectionResult(
            FileInspectionStatus.Cleared, "application/pdf", request.DeclaredLength ?? 0,
            "Cleared", "test", null));
    }
}

/// <summary>Inspection double that rejects every file.</summary>
public sealed class RejectingFileInspection : IFileInspectionService
{
    public bool WasCalled { get; private set; }
    public List<string> InspectedFileNames { get; } = new();

    public Task<FileInspectionResult> InspectAsync(
        FileInspectionRequest request, CancellationToken cancellationToken = default)
    {
        WasCalled = true;
        InspectedFileNames.Add(request.FileName);
        return Task.FromResult(new FileInspectionResult(
            FileInspectionStatus.Rejected, null, request.DeclaredLength ?? 0,
            "Signature mismatch.", "test", null));
    }
}

/// <summary>
/// Deterministic <see cref="IRoleGate"/> double: role ids are classified by membership in
/// the supplied sets rather than by hitting SetupMasters.
/// </summary>
public sealed class StubRoleGate : IRoleGate
{
    public HashSet<long> SuperAdminRoleIds { get; init; } = new();
    public HashSet<long> ManagerRoleIds { get; init; } = new();

    /// <summary>Result returned by <see cref="CanManageRoleAsync"/> when neither party is special.</summary>
    public bool DefaultCanManageRole { get; init; }

    public Task<bool> IsSuperAdminAsync(long roleId, long businessUnitId) =>
        Task.FromResult(SuperAdminRoleIds.Contains(roleId));

    public Task<short> GetRoleRankAsync(long roleId, long businessUnitId) =>
        Task.FromResult(
            SuperAdminRoleIds.Contains(roleId) ? RoleRanks.Owner :
            ManagerRoleIds.Contains(roleId) ? RoleRanks.Manager : RoleRanks.Member);

    public Task<bool> IsManagerOrAdminAsync(long roleId, long businessUnitId) =>
        Task.FromResult(SuperAdminRoleIds.Contains(roleId) || ManagerRoleIds.Contains(roleId));

    public Task<bool> CanManageRoleAsync(long callerRoleId, long? targetRoleId, long businessUnitId)
    {
        if (!targetRoleId.HasValue) return Task.FromResult(true);
        if (SuperAdminRoleIds.Contains(callerRoleId)) return Task.FromResult(true);
        if (SuperAdminRoleIds.Contains(targetRoleId.Value)) return Task.FromResult(false);
        if (ManagerRoleIds.Contains(targetRoleId.Value) && !ManagerRoleIds.Contains(callerRoleId))
            return Task.FromResult(false);
        return Task.FromResult(DefaultCanManageRole);
    }
}
