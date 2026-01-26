using JIM.Models.DataGeneration;
using JIM.Models.Search;
using Microsoft.EntityFrameworkCore;
namespace JIM.Models.Core;

[Index(nameof(Name))]
public class MetaverseObjectType
{
    public int Id { get; set; }

    /// <summary>
    /// The singular name of the object type (e.g., "User", "Group").
    /// </summary>
    public string Name { get; set; } = null!;

    /// <summary>
    /// The plural name of the object type for display in list views (e.g., "Users", "Groups").
    /// </summary>
    public string PluralName { get; set; } = null!;

    public DateTime Created { get; set; } = DateTime.UtcNow;
    public List<MetaverseAttribute> Attributes { get; set; } = new();
    public bool BuiltIn { get; set; }
    public List<DataGenerationTemplateAttribute> DataGenerationTemplateAttributes { get; set; } = null!;
    public List<PredefinedSearch> PredefinedSearches { get; set; } = null!;

    /// <summary>
    /// Determines when Metaverse Objects of this type should be automatically deleted.
    /// Default is WhenLastConnectorDisconnected, meaning objects are deleted when all connectors are removed.
    /// </summary>
    public MetaverseObjectDeletionRule DeletionRule { get; set; } = MetaverseObjectDeletionRule.WhenLastConnectorDisconnected;

    /// <summary>
    /// Optional grace period before a scheduled deletion is executed.
    /// When set, deletion is delayed by this duration after the deletion condition is met.
    /// If null or TimeSpan.Zero, deletion occurs immediately when the condition is met.
    /// </summary>
    public TimeSpan? DeletionGracePeriod { get; set; }

    /// <summary>
    /// Optional list of connected system IDs that trigger MVO deletion when disconnected.
    /// When set: Delete MVO if ANY of these specific systems disconnect.
    /// When empty/null: Delete MVO only when ALL connectors are disconnected.
    /// </summary>
    public List<int> DeletionTriggerConnectedSystemIds { get; set; } = new();
}