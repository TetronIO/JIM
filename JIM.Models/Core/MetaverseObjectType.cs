using JIM.Models.DataGeneration;
using JIM.Models.Search;
using Microsoft.EntityFrameworkCore;
namespace JIM.Models.Core;

[Index(nameof(Name))]
public class MetaverseObjectType
{
    public int Id { get; set; }
    public string Name { get; set; } = null!;
    public DateTime Created { get; set; } = DateTime.UtcNow;
    public List<MetaverseAttribute> Attributes { get; set; } = new();
    public bool BuiltIn { get; set; }
    public List<DataGenerationTemplateAttribute> DataGenerationTemplateAttributes { get; set; } = null!;
    public List<PredefinedSearch> PredefinedSearches { get; set; } = null!;

    /// <summary>
    /// Determines when Metaverse Objects of this type should be automatically deleted.
    /// Default is Manual, meaning objects are never automatically deleted.
    /// </summary>
    public MetaverseObjectDeletionRule DeletionRule { get; set; } = MetaverseObjectDeletionRule.Manual;

    /// <summary>
    /// Optional grace period in days before a scheduled deletion is executed.
    /// When set, deletion is delayed by this number of days after the deletion condition is met.
    /// If null or 0, deletion occurs immediately when the condition is met.
    /// </summary>
    public int? DeletionGracePeriodDays { get; set; }
}