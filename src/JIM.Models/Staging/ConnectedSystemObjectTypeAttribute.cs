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
    /// Whether this attribute's values are case-exact: when JIM asks whether two values identify the same thing,
    /// <c>JBloggs</c> and <c>jbloggs</c> are different values. False means they are the same value, which is
    /// what most identifiers are (directory account names, email addresses, distinguished names).
    /// </summary>
    /// <remarks>
    /// Discovered by the Connector where the Connected System's schema says so (an LDAP equality matching rule,
    /// an Active Directory case-sensitive string syntax, SCIM <c>caseExact</c>, a SQL column collation), and
    /// overridable by an administrator. A schema refresh restates it unless
    /// <see cref="CaseExactSetByAdministrator"/> is set. It is only meaningful for text attributes.
    /// </remarks>
    public bool CaseExact { get; set; }

    /// <summary>
    /// Whether an administrator chose <see cref="CaseExact"/>, rather than schema discovery stating it. A schema
    /// refresh leaves an administrator's choice alone, as it does an overridden <see cref="Type"/>.
    /// </summary>
    public bool CaseExactSetByAdministrator { get; set; }

    /// <summary>
    /// Whether the Connected System requires a value for this attribute on an object of the class named by
    /// <see cref="ClassName"/>, i.e. an RFC 4512 MUST.
    /// </summary>
    /// <remarks>
    /// Connector-supplied and replaced on every schema refresh, in the same way as <see cref="Writability"/>. JIM
    /// uses it to refuse an export that would leave an object invalid at the Connected System, naming what is
    /// missing, rather than sending a change the directory rejects with an error an administrator cannot read.
    /// </remarks>
    public bool Required { get; set; }

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
    /// Whether the Connector reported this as an operational attribute: one the Connected System maintains for its own
    /// operation rather than one that describes the object, i.e. an RFC 4512 attribute type whose usage is not
    /// <c>userApplications</c> (<c>createTimestamp</c>, <c>memberOf</c>, OpenLDAP's <c>pwdFailureTime</c>).
    /// </summary>
    /// <remarks>
    /// Connector-owned: a schema refresh restates it, like <see cref="Writability"/>; administrators cannot set it.
    /// Such an attribute belongs to no class, is always read-only, and is discovered unselected; the schema screen
    /// holds unselected ones back until asked for, because a directory publishes many.
    /// </remarks>
    public bool IsOperational { get; set; }

    /// <summary>
    /// For a <see cref="AttributeDataType.Reference"/> attribute, the Object Type this reference points at,
    /// when the Connected System's schema declares one (the SQL Connector's <c>referencesObjectType</c>).
    /// Null when the schema does not say; import reference resolution then searches every Object Type and
    /// requires the value to be unambiguous (#1285). Connector-stated: a schema refresh restates or clears it,
    /// like <see cref="Writability"/>; administrators cannot set it.
    /// </summary>
    public int? ReferencedObjectTypeId { get; set; }

    /// <inheritdoc cref="ReferencedObjectTypeId"/>
    /// <remarks>
    /// Never serialised: this navigation exists for EF wiring (the schema merge assigns it so the foreign
    /// key resolves for Object Types created in the same save), and the API surfaces the target as id and
    /// name on the DTO instead. It also closes a type cycle (attribute to Object Type to attributes), and
    /// OpenAPI schema generation inlines nullable navigations rather than referencing them, so without this
    /// the document generation recurses to death on any endpoint whose response reaches this entity.
    /// </remarks>
    [System.Text.Json.Serialization.JsonIgnore]
    public ConnectedSystemObjectType? ReferencedObjectType { get; set; }

    public override string ToString()
    {
        return Name;
    }
}