using JIM.Models.Enums;
namespace JIM.Models.Staging;

/// <summary>
/// Represents an object that's just been imported from a Connected System. 
/// Trying a simple form for now, to make life easy for Connector developers. They will then be able to pass
/// these simple objects back to JIM, which will handle the more complex task of matching with Connector Space
/// objects and Pending Export objects and process them further, i.e. synchronising values.
/// </summary>
public class ConnectedSystemImportObject
{
    /// <summary>
    /// The type of object this is in the connected system, i.e. user, group, etc.
    /// </summary>
    public string? ObjectType { get; set; }

    public List<ConnectedSystemImportObjectAttribute> Attributes { get; set; } = new();

    public ObjectChangeType ChangeType { get; set; } = ObjectChangeType.NotSet;    
        
    public ConnectedSystemImportObjectError? ErrorType { get; set; }

    public string? ErrorMessage { get; set; }
}