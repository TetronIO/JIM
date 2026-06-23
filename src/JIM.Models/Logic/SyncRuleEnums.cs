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