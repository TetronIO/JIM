// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Enums;
namespace JIM.Models.Staging;

/// <summary>
/// Represents an object imported from a Connected System. Connectors populate these lightweight
/// objects during import, and JIM handles matching them to connector space objects, processing
/// Pending Exports, and synchronising attribute values.
/// </summary>
public class ConnectedSystemImportObject
{
    /// <summary>
    /// The object type in the Connected System, e.g. user, group. A deletion may leave it empty when the Connector can
    /// say which object went (its External ID) but not what it was; where the Connector's External IDs name one object
    /// across every Object Type, the import gives the deletion the type of the object it names (#1066).
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
    /// <remarks>
    /// Where the object carries <see cref="AttributeValueErrors"/>, JIM writes the Activity's message from those instead.
    /// </remarks>
    public string? ErrorMessage { get; set; }

    /// <summary>
    /// Every value the Connector found on this object but could not read as its attribute's type (#2066); null when
    /// there were none. Add to it with <see cref="AddAttributeValueError"/>.
    /// </summary>
    /// <remarks>
    /// An attribute named here is left as JIM already held it: JIM neither removes its stored values nor adds any of
    /// the attribute's values that were read, because the attribute's true state in the Connected System is unknown.
    /// A Connector that reports an unreadable value by setting <see cref="ErrorType"/> and <see cref="ErrorMessage"/>
    /// alone still works, but JIM cannot tell which attribute to leave alone, so it treats the attribute as holding
    /// whatever values the object does carry for it.
    /// </remarks>
    public List<ConnectedSystemImportObjectAttributeValueError>? AttributeValueErrors { get; set; }

    /// <summary>
    /// Where in the Connected System the object was read from, in words an administrator can follow to find it, such as
    /// <c>hr-users.csv, row 4,182</c> (#2066). Optional; JIM shows it beside any error the object carries, so it is
    /// worth setting at least on an object you flag. Leave it null where the External ID already says all there is.
    /// </summary>
    public string? SourceLocation { get; set; }

    /// <summary>
    /// Records a value the Connector could not read, and marks the object as carrying an attribute value error.
    /// Call it once for every unreadable value, not once per attribute, and carry on reading the object's other values.
    /// </summary>
    /// <param name="attributeName">The attribute the value was found on.</param>
    /// <param name="value">The value as received, before conversion.</param>
    /// <param name="reason">Why it could not be read.</param>
    /// <param name="source">Who found it could not be imported. A Connector leaves this as it is; JIM sets it when it is
    /// JIM that cannot hold a value the Connector read (#2153).</param>
    public void AddAttributeValueError(string attributeName, string? value, string reason,
        ImportAttributeValueErrorSource source = ImportAttributeValueErrorSource.Connector)
    {
        AttributeValueErrors ??= [];
        AttributeValueErrors.Add(new ConnectedSystemImportObjectAttributeValueError
        {
            AttributeName = attributeName,
            Value = value,
            Reason = reason,
            Source = source
        });
        ErrorType = ConnectedSystemImportObjectError.AttributeValueError;
    }
}