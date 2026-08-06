// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Staging;

/// <summary>
/// An administrator's decision that one Connected System Object Type extends another: on an RFC 4512 directory,
/// that a structural class such as inetOrgPerson should also carry the attributes of an auxiliary class such as
/// posixAccount.
/// </summary>
/// <remarks>
/// This is the source of truth for auxiliary class merging, and it is deliberately the administrator's statement
/// rather than the directory's. An RFC 4512 subschema cannot answer the question: auxiliary classes attach to
/// individual entries rather than to the schema, so nothing in the schema says which of them a given population
/// actually uses. JIM offers evidence for the decision (DIT Content Rules, and counts from reading entries) as
/// suggestions, and never acts on that evidence by itself.
/// <para>
/// Both ends cascade. Removing the base type takes its extensions with it, and an auxiliary type that disappears
/// from the Connected System's schema on a refresh takes with it every selection that pointed at it, matching the
/// documented data-loss semantics of a schema refresh.
/// </para>
/// </remarks>
public class ConnectedSystemObjectTypeExtension
{
    public int Id { get; set; }

    /// <summary>
    /// The Object Type being extended, i.e. the structural class an administrator manages.
    /// </summary>
    public ConnectedSystemObjectType BaseObjectType { get; set; } = null!;
    public int BaseObjectTypeId { get; set; }

    /// <summary>
    /// The Object Type contributing the additional attributes, i.e. the auxiliary class.
    /// </summary>
    public ConnectedSystemObjectType ExtensionObjectType { get; set; } = null!;
    public int ExtensionObjectTypeId { get; set; }

    public DateTime Created { get; set; } = DateTime.UtcNow;

    public override string ToString()
    {
        return $"{BaseObjectType?.Name ?? BaseObjectTypeId.ToString()} + {ExtensionObjectType?.Name ?? ExtensionObjectTypeId.ToString()}";
    }
}
