// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Logic;

public enum SyncRuleDirection
{
    NotSet = 0,
    Import = 1,
    Export = 2
}

/// <summary>
/// The object-creating action a Synchronisation Rule performs, derived from its direction and its
/// projection/provisioning settings. Used to filter and describe rules in list views; the three
/// values are mutually exclusive and exhaustive.
/// </summary>
public enum SyncRuleActionType
{
    /// <summary>
    /// The Synchronisation Rule creates no objects; it only flows attribute values.
    /// </summary>
    FlowOnly = 0,

    /// <summary>
    /// An Import rule that projects new Metaverse Objects.
    /// </summary>
    Projects = 1,

    /// <summary>
    /// An Export rule that provisions new Connected System Objects.
    /// </summary>
    Provisions = 2
}

/// <summary>
/// Whether a Synchronisation Rule is enabled, expressed as an enumeration so that it can be used as
/// a filter facet alongside the other Synchronisation Rule facets.
/// </summary>
public enum SyncRuleStatus
{
    /// <summary>
    /// The Synchronisation Rule is disabled and is skipped by the synchronisation engine.
    /// </summary>
    Disabled = 0,

    /// <summary>
    /// The Synchronisation Rule is enabled and is evaluated by the synchronisation engine.
    /// </summary>
    Enabled = 1
}


/// <summary>
/// Used to provide some context to the user on what type of sources configuration has been used in a Synchronisation Rule mapping.
/// </summary>
public enum SyncRuleMappingSourcesType
{
    NotSet = 0,
    AttributeMapping = 1,
    ExpressionMapping = 2,
    AdvancedMapping = 3
}

/// <summary>
/// Inbound value-processing transforms applied to a text attribute value as it flows from a
/// Connected System Object to a Metaverse Object, configured per import sync rule mapping.
/// A bitwise combination of the enabled transforms; the engine applies them in a fixed canonical
/// order (trim, then collapse internal whitespace, then case normalisation, then the
/// whitespace-as-no-value decision), independent of the bit order declared here.
/// Applies to text attributes only; other attribute types are unaffected.
/// </summary>
[Flags]
public enum InboundValueProcessing
{
    /// <summary>
    /// No value processing; whitespace-only and empty values flow through as literal values.
    /// </summary>
    None = 0,

    /// <summary>
    /// Treat a whitespace-only or empty value as no value: it does not flow, and clears any existing
    /// Metaverse value. JIM's default. Disable to preserve whitespace as a literal value.
    /// </summary>
    TreatWhitespaceAsNoValue = 1 << 0,

    /// <summary>
    /// Remove leading and trailing whitespace from the value (for example, " John " becomes "John").
    /// </summary>
    TrimWhitespace = 1 << 1,

    /// <summary>
    /// Collapse runs of internal whitespace down to a single space (for example, "John   Smith"
    /// becomes "John Smith").
    /// </summary>
    CollapseInternalWhitespace = 1 << 2
}

/// <summary>
/// Case normalisation applied to an inbound text attribute value, configured per import sync rule
/// mapping. Mutually exclusive options, applied after whitespace trimming and collapsing and before
/// the whitespace-as-no-value decision. Applies to text attributes only.
/// </summary>
public enum InboundCaseNormalisation
{
    /// <summary>
    /// No case normalisation; the value's case is preserved.
    /// </summary>
    None = 0,

    /// <summary>
    /// Convert the value to upper case.
    /// </summary>
    Upper = 1,

    /// <summary>
    /// Convert the value to lower case.
    /// </summary>
    Lower = 2,

    /// <summary>
    /// Convert the value to title case: the first letter of each word capitalised.
    /// </summary>
    Title = 3
}