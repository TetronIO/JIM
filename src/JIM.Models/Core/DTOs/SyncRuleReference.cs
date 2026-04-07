namespace JIM.Models.Core.DTOs;

/// <summary>
/// Lightweight reference to a sync rule, used in validation error responses
/// to identify which sync rules reference a given metaverse attribute.
/// </summary>
public class SyncRuleReference
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
}
