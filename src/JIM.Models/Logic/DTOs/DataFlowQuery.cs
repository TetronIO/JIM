// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Logic.DTOs;

/// <summary>
/// The filters the Data Flow view applies (#1199). Every filter is optional and they combine with AND, so leaving
/// them all unset returns every attribute data flow in both directions.
///
/// Passed as an object rather than as a parameter list because the filters are numerous, all optional, and mostly of
/// the same type, which makes a positional call site easy to get wrong and impossible to read.
/// </summary>
public class DataFlowQuery
{
    /// <summary>
    /// Import or Export. Unset returns both.
    /// </summary>
    public SyncRuleDirection? Direction { get; set; }

    public int? ConnectedSystemId { get; set; }

    public int? ConnectedSystemObjectTypeId { get; set; }

    public int? MetaverseObjectTypeId { get; set; }

    /// <summary>
    /// Matches a flow that reads or writes this Connected System attribute, whichever side it sits on for the
    /// direction in question. A flow whose Connected System side is an expression cannot match: an expression's
    /// attribute references are not modelled, only its text.
    /// </summary>
    public int? ConnectedSystemAttributeId { get; set; }

    /// <summary>
    /// Matches a flow that reads or writes this Metaverse Attribute, whichever side it sits on for the direction in
    /// question. Subject to the same expression limitation as <see cref="ConnectedSystemAttributeId"/>.
    /// </summary>
    public int? MetaverseAttributeId { get; set; }

    /// <summary>
    /// Restricts to Import flows whose target Metaverse Attribute has more than one contributor, which are the only
    /// flows whose priority order decides anything. Ignored for Export flows, which have no priority.
    /// </summary>
    public bool MultipleContributorsOnly { get; set; }

    /// <summary>
    /// Case-insensitive free text, matched against the Synchronisation Rule, Connected System, both object types,
    /// every attribute named on either side, and any expression text.
    /// </summary>
    public string? Search { get; set; }
}
