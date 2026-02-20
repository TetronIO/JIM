using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Interfaces;
using Microsoft.EntityFrameworkCore;
namespace JIM.Models.Search;

/// <summary>
/// Enables users to find objects easily, and to control what attributes are returned in the search results
/// </summary>
[Index(nameof(Uri))]
public class PredefinedSearch : IAuditable
{
    public int Id { get; set; }

    public DateTime Created { get; set; } = DateTime.UtcNow;
    public ActivityInitiatorType CreatedByType { get; set; }
    public Guid? CreatedById { get; set; }
    public string? CreatedByName { get; set; }

    public DateTime? LastUpdated { get; set; }
    public ActivityInitiatorType LastUpdatedByType { get; set; }
    public Guid? LastUpdatedById { get; set; }
    public string? LastUpdatedByName { get; set; }

    /// <summary>
    /// The type of Metaverse object this search will result results for.
    /// </summary>
    public MetaverseObjectType MetaverseObjectType { get; set; } = null!;

    /// <summary>
    /// If true, this is the default predefined search for the associated metaverse object type.
    /// This means in the web portal, a search parameter does not have to be used on the URL.
    /// </summary>
    public bool IsDefaultForMetaverseObjectType { get; set; }

    /// <summary>
    /// The user-supplied name of the predefined search. This will be displayed to users when selecting from predefined searches.
    /// i.e. "All Permanent Staff"
    /// </summary>
    public string Name { get; set; } = null!;

    /// <summary>
    /// The uri component to use in URLs for the predefined search, i.e. "distribution" would result in: https://iga.tetron.io/t/groups/s/distribution
    /// </summary>
    public string Uri { get; set; } = null!;

    /// <summary>
    /// Is this a PredefinedSearch that comes built-in to JIM, or is it user-created?
    /// </summary>
    public bool BuiltIn { get; set; }

    /// <summary>
    /// The attribute(s) to return in the result of the search. They can be different for each predefined search.
    /// </summary>
    public List<PredefinedSearchAttribute> Attributes { get; set; } = new();

    /// <summary>
    /// The criteria used to filter the results, i.e. the search query.
    /// </summary>
    public List<PredefinedSearchCriteriaGroup> CriteriaGroups { get; } = new();
}
