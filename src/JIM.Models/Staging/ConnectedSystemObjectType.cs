// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Logic;
namespace JIM.Models.Staging;

public class ConnectedSystemObjectType
{
    public int Id { get; set; }

    public string Name { get; set; } = null!;

    public DateTime Created { get; set; } = DateTime.UtcNow;

    public ConnectedSystem ConnectedSystem { get; set; } = null!;
    public int ConnectedSystemId { get; set; }

    public List<ConnectedSystemObjectTypeAttribute> Attributes { get; set; } = new();

    /// <summary>
    /// Connector-supplied classification for this object type, i.e. whether it is a structural or auxiliary class.
    /// Populated during schema import and replaced on refresh. An empty collection means unclassified, which
    /// consumers must treat as "show it, do not group it". See <see cref="ObjectTypeTags"/>.
    /// </summary>
    public List<ConnectedSystemObjectTypeTag> Tags { get; set; } = new();

    /// <summary>
    /// The Object Types an administrator has chosen to extend this one with, i.e. the auxiliary classes whose
    /// attributes should be merged into this structural class. Empty on a Connected System with no such concept.
    /// </summary>
    public List<ConnectedSystemObjectTypeExtension> Extensions { get; set; } = new();

    /// <summary>
    /// The structural Object Type JIM should use as the carrier when creating an object of this type, where this
    /// type cannot stand alone.
    /// </summary>
    /// <remarks>
    /// An RFC 4512 entry must have exactly one structural class, so an object population identified by an auxiliary
    /// class (the case traditional ILM solutions forced a bespoke connector for) still needs a structural class in
    /// order to exist at all. Naming the carrier is what lets JIM provision that population. Null on a structural
    /// type, which is its own carrier, and on any Connected System without the distinction.
    /// </remarks>
    public ConnectedSystemObjectType? StructuralCarrierObjectType { get; set; }
    public int? StructuralCarrierObjectTypeId { get; set; }

    /// <summary>
    /// Whether an administrator has selected this object type to be managed by JIM.
    /// </summary>
    public bool Selected { get; set; }

    /// <summary>
    /// Controls whether Metaverse Object attribute values contributed by a Connected System Object of this type
    /// should be removed when the CSO is obsoleted. When true, attributes contributed by the CSO
    /// will be added to PendingAttributeValueRemovals. When false, attributes remain on the MVO.
    /// </summary>
    public bool RemoveContributedAttributesOnObsoletion { get; set; } = true;

    /// <summary>
    /// Object Matching Rules for this object type. Used when the Connected System's ObjectMatchingRuleMode
    /// is set to ConnectedSystem (the default). These rules are shared across all Synchronisation Rules for this object type.
    /// </summary>
    public List<ObjectMatchingRule> ObjectMatchingRules { get; set; } = new();

    public override string ToString()
    {
        return Name;
    }
}