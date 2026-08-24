namespace ERP_RFQ_Automation.Models;

/// <summary>
/// Assignment is an ownership concern, not a lead-lifecycle state.  These fields are additive
/// companions to the legacy AssignTo/AssignOn columns so old readers keep working while new
/// commands get an explicit method, manual-override fence and optimistic-concurrency token.
/// </summary>
public partial class Lead
{
    public string AssignmentMethod { get; set; } = LeadAssignmentMethods.Automatic;
    public long? AssignedByUserId { get; set; }
    public bool ManualAssignmentOverride { get; set; }
    public long AssignmentVersion { get; set; } = 1;
}

public static class LeadAssignmentMethods
{
    public const string Automatic = "AUTOMATIC";
    public const string Manual = "MANUAL";
}
