// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Core;
using JIM.Models.Staging;

namespace JIM.Web.Models;

/// <summary>
/// A target attribute the advisory Standard Mappings say corresponds to the chosen source attribute (#1122).
/// Exactly one of the two attribute properties is set, depending on the Synchronisation Rule's direction.
/// Only a suggestion whose <see cref="Status"/> is <see cref="StandardMappingSuggestionStatus.Available"/> is
/// offered for selection, so accepting one never produces a mapping the editor would have refused; the rest
/// are stated with the reason they cannot be selected rather than being dropped silently.
/// </summary>
public sealed class StandardMappingSuggestion
{
    /// <summary>
    /// Whether this target can receive the chosen source attribute, and if not, why.
    /// </summary>
    public required StandardMappingSuggestionStatus Status { get; init; }

    /// <summary>
    /// The Metaverse Attribute to flow to, on an import Synchronisation Rule.
    /// </summary>
    public MetaverseAttribute? MetaverseAttribute { get; init; }

    /// <summary>
    /// The Connected System attribute to flow to, on an export Synchronisation Rule.
    /// </summary>
    public ConnectedSystemObjectTypeAttribute? ConnectedSystemAttribute { get; init; }

    /// <summary>
    /// The target attribute's name, as shown to the administrator.
    /// </summary>
    public required string TargetName { get; init; }

    /// <summary>
    /// The standard whose vocabulary produced the correspondence, e.g. "LDAP/AD".
    /// </summary>
    public required string StandardLabel { get; init; }

    /// <summary>
    /// The counterpart name that links the two attributes, e.g. "givenName".
    /// </summary>
    public required string CounterpartName { get; init; }

    /// <summary>
    /// Optional nuance recorded against the mapping, such as a type difference that needs a transform.
    /// </summary>
    public string? Notes { get; init; }
}
