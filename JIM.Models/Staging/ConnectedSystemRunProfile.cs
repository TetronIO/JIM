using JIM.Models.Activities;
namespace JIM.Models.Staging;

public class ConnectedSystemRunProfile
{
    public int Id { get; set; }

    /// <summary>
    /// The user-supplied name for this run profile.
    /// </summary>
    public string Name { get; set; } = null!;

    /// <summary>
    /// Unique identifier for the parent object.
    /// </summary>
    public int ConnectedSystemId { get; set; }
        
    /// <summary>
    /// If the connected system implements partitions, then a run profile needs to target a partition.
    /// </summary>
    public ConnectedSystemPartition? Partition { get; set; }

    public ConnectedSystemRunType RunType { get; set; }

    /// <summary>
    /// How many items to process in one go via the Connector.
    /// </summary>
    public int PageSize { get; set; }

    /// <summary>
    /// If this run profile is for a file-based connector, then a full path to the file to read from/write to is required.
    /// This enables the user to specify different files for different run types, i.e. there might be a full-import file and a delta-import file.
    /// </summary>
    public string? FilePath { get; set; }

    /// <summary>
    /// Back-link to depedent activity objects. 
    /// Optional relationship.
    /// Used by EntityFramework.
    /// </summary>
    public List<Activity>? Activities { get; set; }

    public override string ToString() => Name;
}