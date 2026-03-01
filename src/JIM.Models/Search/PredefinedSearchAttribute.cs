using JIM.Models.Core;
namespace JIM.Models.Search;

/// <summary>
/// Defines a Metaverse attribute to include as a column in the results of a predefined search.
/// </summary>
public class PredefinedSearchAttribute
{
    public int Id { get; set; }

    /// <summary>
    /// The predefined search this attribute belongs to.
    /// </summary>
    public PredefinedSearch PredefinedSearch { get; set; } = null!;

    /// <summary>
    /// The Metaverse attribute to display as a column in the search results.
    /// </summary>
    public MetaverseAttribute MetaverseAttribute { get; set; } = null!;

    /// <summary>
    /// Predefined search attributes are shown to the user in a left-right order as determined by this value. 0 is the first attribute to be shown.
    /// </summary>
    public int Position { get; set; } = 0;
}