namespace JIM.Models.Staging.DTOs;

/// <summary>
/// Wraps a ConnectedSystemObject with optional per-attribute metadata.
/// When loaded with <see cref="CsoAttributeLoadStrategy.CappedMva"/>, includes
/// total value counts per attribute so consumers know when values were capped.
/// </summary>
public class CsoDetailResult
{
    public ConnectedSystemObject ConnectedSystemObject { get; set; } = null!;

    /// <summary>
    /// Per-attribute total value counts. Only populated when the load strategy
    /// caps MVA values (e.g. <see cref="CsoAttributeLoadStrategy.CappedMva"/>).
    /// Key is the attribute name; value is the total count in the database.
    /// </summary>
    public Dictionary<string, int> AttributeValueTotalCounts { get; set; } = new();
}
