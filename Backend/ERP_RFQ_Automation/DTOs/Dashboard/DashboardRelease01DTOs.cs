namespace ERP_RFQ_Automation.DTOs.Dashboard;

public static class DashboardRelease01Contract
{
    public const string DefinitionVersion = "release-01";
    public const string Available = "available";
    public const string InsufficientData = "insufficient_data";
}

public sealed class DashboardRelease01DTO
{
    public string DefinitionVersion { get; set; } = DashboardRelease01Contract.DefinitionVersion;
    public DateTime GeneratedAt { get; set; }
    public DashboardRelease01FilterDTO Filter { get; set; } = new();
    public DashboardRelease01RoleScopeDTO RoleScope { get; set; } = new();
    public List<DashboardRelease01KpiDTO> Kpis { get; set; } = new();
}

public sealed class DashboardRelease01FilterDTO
{
    public DateTime From { get; set; }
    public DateTime To { get; set; }
    public string Boundary { get; set; } = "[from,to)";
}

public sealed class DashboardRelease01RoleScopeDTO
{
    public string Scope { get; set; } = string.Empty;
    public long? OwnerUserId { get; set; }
}

public sealed class DashboardRelease01KpiDTO
{
    public string DefinitionVersion { get; set; } = DashboardRelease01Contract.DefinitionVersion;
    public string Key { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public string State { get; set; } = DashboardRelease01Contract.InsufficientData;
    public decimal? Value { get; set; }
    public string Unit { get; set; } = "count";
    public int? Numerator { get; set; }
    public int? Denominator { get; set; }
    public string Definition { get; set; } = string.Empty;
    public string? InsufficientDataReason { get; set; }
    public List<DashboardRelease01DrillDownIdentifierDTO> DrillDownIdentifiers { get; set; } = new();
}

public sealed class DashboardRelease01DrillDownIdentifierDTO
{
    public string RecordType { get; set; } = string.Empty;
    public long RecordId { get; set; }
    public long CommercialCaseId { get; set; }
    public string NexoraSerial { get; set; } = string.Empty;
    public string? Classification { get; set; }
    public DateTime? OccurredAt { get; set; }
    public decimal? DurationHours { get; set; }
}
