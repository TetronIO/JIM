// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Core;
namespace JIM.Models.Staging;

public class ConnectorSchemaAttribute
{
    public string Name { get; set; }

    public string? Description { get; set; }

    /// <summary>
    /// Some types of Connected Systems have a concept of hierarchy where an attribute is inherited from a class that the object type inherits, i.e. an LDAP object class.
    /// Storing this information in JIM and presenting it to the user when configuring a Connected System can help them with understanding what might or might not need managing, attribute wise.
    /// </summary>
    public string? ClassName { get; set; }
        
    /// <summary>
    /// What type of data is the attribute representing?
    /// </summary>
    public AttributeDataType Type { get; set; }
        
    /// <summary>
    /// How many values can this attribute hold?
    /// </summary>
    public AttributePlurality AttributePlurality { get; set; }
        
    /// <summary>
    /// Does the external system require this attribute to have a value set for the object type it relates to?
    /// This may or may not be useful, i.e. some attributes in AD are marked as required but if you don't supply a value, the DSA assigns one.
    /// </summary>
    public bool Required { get; set; }

    /// <summary>
    /// Indicates whether this attribute can be written to in the Connected System.
    /// Read-only attributes (system-managed, constructed, back-links) can still be imported but cannot be targeted by export Attribute Flows.
    /// </summary>
    public AttributeWritability Writability { get; set; }

    public ConnectorSchemaAttribute(string name, AttributeDataType type, AttributePlurality attributePlurality, bool required = false, string? className = null, AttributeWritability writability = AttributeWritability.Writable)
    {
        Name = name;
        Type = type;
        Required = required;
        AttributePlurality = attributePlurality;
        ClassName = className;
        Writability = writability;
    }
}