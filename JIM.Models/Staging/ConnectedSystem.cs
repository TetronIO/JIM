using JIM.Models.Activities;
using JIM.Models.Transactional;
using System.ComponentModel.DataAnnotations;
namespace JIM.Models.Staging;

public class ConnectedSystem
{
    public int Id { get; set; }

    [Required(ErrorMessage = "Please provide a name for the Connected System")]
    public string Name { get; set; } = null!;

    public string? Description { get; set; }

    public DateTime Created { get; set; } = DateTime.UtcNow;

    public DateTime LastUpdated { get; set; }

    /// <summary>
    /// The operational status of the Connected System.
    /// Used to block operations during deletion.
    /// </summary>
    public ConnectedSystemStatus Status { get; set; } = ConnectedSystemStatus.Active;

    public List<ConnectedSystemRunProfile>? RunProfiles { get; set; } = new();

    public List<ConnectedSystemObject> Objects { get; set; } = new();

    public List<ConnectedSystemObjectType>? ObjectTypes { get; set; } = new();

    public List<PendingExport> PendingExports { get; set; } = null!;

    public ConnectorDefinition ConnectorDefinition { get; set; } = null!;

    public List<ConnectedSystemSettingValue> SettingValues { get; set; } = new();

    /// <summary>
    /// We track whether setting values have been validated by the Connector so that we can prevent the user from navigating to configuration phases that are dependent upon valid setting values.
    /// When a connected system is created, this will be false as there are no values supplied yet.
    /// When any setting values are changed by the user, this will be toggled to false until the settings are validated.
    /// </summary>
    public bool SettingValuesValid { get; set; }

    /// <summary>
    /// If the Connector implements partitions, then at least one partition is required, and containers may reside under those, if supported by the Connector.
    /// Note: Partitions don't have to support containers, but it's common that they do, i.e. with LDAP-based Connectors.
    /// </summary>
    public List<ConnectedSystemPartition>? Partitions { get; set; }

    /// <summary>
    /// Information that connector developers want to have persisted between synchronisation runs can be stored here.
    /// This is to suppose use-cases such as needing to store the last change id for an LDAP sytem, to enable delta imports.
    /// </summary>
    public string? PersistedConnectorData { get; set; }

    /// <summary>
    /// Determines where object matching rules are configured for this Connected System.
    /// ConnectedSystem (default): Rules are defined per object type and shared across sync rules.
    /// SyncRule: Rules are defined per sync rule for advanced scenarios.
    /// </summary>
    public ObjectMatchingRuleMode ObjectMatchingRuleMode { get; set; } = ObjectMatchingRuleMode.ConnectedSystem;

    /// <summary>
    /// Timestamp of when the last delta synchronisation completed successfully.
    /// Used by delta sync to determine which CSOs have been modified since the last run.
    /// Delta sync queries CSOs where LastUpdated > LastDeltaSyncCompletedAt.
    /// </summary>
    public DateTime? LastDeltaSyncCompletedAt { get; set; }

    /// <summary>
    /// EF back-link.
    /// </summary>
    public List<Activity>? Activities { get; set; }

    public override string ToString()
    {
        return $"{nameof(ConnectedSystem)}: {Name} ({Id})";
    }
}