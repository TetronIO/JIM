// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Core;
namespace JIM.Models.Staging;

public class ConnectedSystemObjectTypeAttribute
{
    public int Id { get; set; }
        
    public DateTime Created { set; get; } = DateTime.UtcNow;

    public string Name { get; set; } = null!;

    public string? Description { get; set; }

    /// <summary>
    /// Some types of Connected Systems have a concept of hierarchy where an attribute is inherited from a class that the object type inherits, i.e. an LDAP object class.
    /// Storing this information in JIM and presenting it to the user when configuring a Connected System can help them with understanding what might or might not need managing, attribute wise.
    /// </summary>
    public string? ClassName { get; set; }

    public AttributeDataType Type { get; set; }

    /// <summary>
    /// Whether an administrator chose <see cref="Type"/>, rather than schema discovery inferring it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A schema refresh overwrites what the Connector discovered and preserves what the administrator
    /// decided. Until an attribute's data type could be overridden it was purely discovered, so it sat
    /// on the refreshed side; this flag is what moves an overridden one across.
    /// </para>
    /// <para>
    /// Without it the refresh would silently undo an override, and silently is the danger: the mapping
    /// validator runs when a mapping is created, not continuously, so a Synchronisation Rule validated
    /// against the chosen type would keep running against the reverted one, and the Attribute Flow, which
    /// switches on the source type, would write the value into the wrong column of the Metaverse Object.
    /// It would also be a way around the rule that an override is refused once an attribute holds values.
    /// </para>
    /// <para>
    /// It pins the type alone. Writability, plurality and the description remain the Connector's to state,
    /// so an override cannot freeze an attribute in the past.
    /// </para>
    /// </remarks>
    public bool TypeSetByAdministrator { get; set; }

    public AttributePlurality AttributePlurality { get; set; } = AttributePlurality.SingleValued;

    /// <summary>
    /// The Connected System Object Type this attribute belongs to.
    /// </summary>
    public ConnectedSystemObjectType ConnectedSystemObjectType { get; set; } = null!;

    /// <summary>
    /// Whether an administrator has selected this attribute to be managed by JIM.
    /// </summary>
    public bool Selected { get; set; }

    /// <summary>
    /// Indicates if this attribute is a unique identifier for the object type in a Connected System.
    /// </summary>
    public bool IsExternalId { get; set; }

    /// <summary>
    /// Indicates if this attribute is used as a secondary identifier by the Connected System, i.e. how a DN is used as such in an LDAP system.
    /// </summary>
    public bool IsSecondaryExternalId { get; set; }

    /// <summary>
    /// Indicates if this attribute's selection state is locked and cannot be changed by administrators.
    /// This is automatically set to true for External ID and Secondary External ID attributes to ensure
    /// the system always has the required anchor attributes available for sync operations.
    /// </summary>
    public bool SelectionLocked { get; set; }

    /// <summary>
    /// Indicates whether this attribute can be written to in the Connected System.
    /// Read-only attributes (system-managed, constructed, back-links) can still be imported but cannot be targeted by export Attribute Flows.
    /// <see cref="AttributeWritability.WritableOnCreate"/> attributes can be targeted, but only ever flow on a Create Pending Export.
    /// </summary>
    public AttributeWritability Writability { get; set; }

    /// <summary>
    /// For a <see cref="AttributeDataType.Reference"/> attribute, the Object Type this reference points at,
    /// when the Connected System's schema declares one (the SQL Connector's <c>referencesObjectType</c>).
    /// Null when the schema does not say; import reference resolution then searches every Object Type and
    /// requires the value to be unambiguous (#1285). Connector-stated: a schema refresh restates or clears it,
    /// like <see cref="Writability"/>; administrators cannot set it.
    /// </summary>
    public int? ReferencedObjectTypeId { get; set; }

    /// <inheritdoc cref="ReferencedObjectTypeId"/>
    public ConnectedSystemObjectType? ReferencedObjectType { get; set; }

    public override string ToString()
    {
        return Name;
    }
}