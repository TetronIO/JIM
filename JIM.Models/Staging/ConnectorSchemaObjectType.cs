﻿namespace JIM.Models.Staging;

public class ConnectorSchemaObjectType
{
    /// <summary>
    /// The name of the Object Type in the connected system.
    /// Recommend using standard conventions, i.e. "User" or "Group" to simplify mappings between the metaverse and connected system object types and enable JIM to auto-map them.
    /// </summary>
    public string Name { get; set; }

    public List<ConnectorSchemaAttribute> Attributes { get; set; }

    /// <summary>
    /// Which attribute for this object type, is recommended to uniquely identify the object in the connected system?
    /// The recommended attribute should be immutable for the lifetime of the object so that JIM can always identify it and not see connected system objects as being deleted or created when unique ids change in the connected system.
    /// Generally, it's best to use a system-generated attribute for this where appropriate, rather than using business-generated attribute as system-generated attributes are less likely to change value over time.
    /// </summary>
    public ConnectorSchemaAttribute RecommendedExternalIdAttribute { get; set; } = null!;

    /// <summary>
    /// If the connector supports secondary external identifiers, then a value must be present here.
    /// i.e. for an LDAP system, this would be the DN attribute, which is used for resolving references between objects, but is not an ideal candidate for a unique identifier as it's mutable.
    /// </summary>
    public ConnectorSchemaAttribute? RecommendedSecondaryExternalIdAttribute { get; set; }

    public ConnectorSchemaObjectType(string name)
    {
        Name = name;
        Attributes = new List<ConnectorSchemaAttribute>();
    }
}