// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Core;

/// <summary>
/// Advisory metadata that documents how a Metaverse Attribute corresponds to an attribute in a
/// wire standard's vocabulary (SCIM 2.0 or LDAP/AD), or to JIM's own canonical vocabulary.
/// Powers Attribute Flow editor hints, connector wizard default-flow suggestions, and generated
/// schema documentation. NEVER consulted by the synchronisation engine; Attribute Flow
/// configuration remains the single source of mapping truth.
/// </summary>
public class MetaverseAttributeStandardMapping
{
    public int Id { get; set; }

    public int MetaverseAttributeId { get; set; }

    public MetaverseAttribute? MetaverseAttribute { get; set; }

    /// <summary>
    /// The vocabulary the counterpart attribute name belongs to.
    /// </summary>
    public AttributeStandard Standard { get; set; }

    /// <summary>
    /// The counterpart attribute name within the standard's vocabulary, e.g. "name.givenName"
    /// (SCIM) or "givenName" (LDAP).
    /// </summary>
    public string CounterpartName { get; set; } = null!;

    /// <summary>
    /// Optional free-text nuance about the correspondence, e.g. type differences or
    /// deployment-specific conventions.
    /// </summary>
    public string? Notes { get; set; }

    public override string ToString()
    {
        return $"{Standard}: {CounterpartName}";
    }
}