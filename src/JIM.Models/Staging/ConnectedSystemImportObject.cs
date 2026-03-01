using JIM.Models.Enums;
namespace JIM.Models.Staging;

/// <summary>
/// Represents an object imported from a connected system. Connectors populate these lightweight
/// objects during import, and JIM handles matching them to connector space objects, processing
/// pending exports, and synchronising attribute values.
/// </summary>
public class ConnectedSystemImportObject
{
    /// <summary>
    /// The object type in the connected system, e.g. user, group.
    /// </summary>
    public string? ObjectType { get; set; }

    /// <summary>
    /// The attributes imported for this object.
    /// </summary>
    public List<ConnectedSystemImportObjectAttribute> Attributes { get; set; } = new();

    /// <summary>
    /// Indicates the type of change this object represents (add, update, delete, etc.).
    /// </summary>
    public ObjectChangeType ChangeType { get; set; } = ObjectChangeType.NotSet;

    /// <summary>
    /// The error type, if an error occurred during import of this object.
    /// </summary>
    public ConnectedSystemImportObjectError? ErrorType { get; set; }

    /// <summary>
    /// A descriptive error message, if an error occurred during import of this object.
    /// </summary>
    public string? ErrorMessage { get; set; }
}