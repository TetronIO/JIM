using JIM.Models.Core;
namespace JIM.Models.Search;

/// <summary>
/// A single search criterion that compares a Metaverse attribute against a value using a specified comparison operator.
/// </summary>
public class PredefinedSearchCriteria
{
    public int Id { get; set; }

    /// <summary>
    /// The comparison operator to apply (e.g. Equals, Contains, StartsWith).
    /// </summary>
    public SearchComparisonType ComparisonType { get; set; }

    /// <summary>
    /// The value to compare the attribute against, stored as a string.
    /// </summary>
    public string StringValue { get; set; } = null!;

    /// <summary>
    /// The Metaverse attribute that this criterion evaluates.
    /// </summary>
    public MetaverseAttribute MetaverseAttribute { get; set; } = null!;
}