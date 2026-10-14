// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Core;
using JIM.Models.Logic;

namespace JIM.Web.Models.Api;

/// <summary>
/// A test of an Object Matching Rule: the object to test, as sample values, a real Connected System Object or both,
/// and whether to look the values up in the Metaverse.
/// </summary>
public class TestMatchingRuleRequest
{
    /// <summary>
    /// Sample values for the attributes the rule reads, keyed by attribute name. Each value is converted to its
    /// attribute's type as the object would hold it: text, a number, a Boolean, null for no value, or an array of these
    /// for a multi-valued attribute. An attribute not given holds no value.
    /// </summary>
    public Dictionary<string, object?>? Attributes { get; set; }

    /// <summary>
    /// A Connected System Object of the rule's Object Type whose values to test with. A sample in <c>Attributes</c>
    /// replaces the object's value for that attribute.
    /// </summary>
    public Guid? ConnectedSystemObjectId { get; set; }

    /// <summary>
    /// Whether to look the values up in the Metaverse, as an import join would, and report what a synchronisation would
    /// do: join, no match, or an ambiguous match. Nothing is written.
    /// </summary>
    public bool LookUp { get; set; }
}

/// <summary>
/// What an Object Matching Rule would compare for an object holding the tested values, worked out as synchronisation
/// works it out, and what those values find in the Metaverse when the lookup was asked for.
/// </summary>
public class TestMatchingRuleResponse
{
    /// <summary>
    /// False when the values could not be used; <c>InputErrors</c> says why, and nothing was evaluated.
    /// </summary>
    public bool IsValid { get; set; }

    /// <summary>
    /// What the rule produced: values to match on, no value (a synchronisation would try the next rule), or a failure
    /// (a synchronisation would stop the object). Null when <c>IsValid</c> is false.
    /// </summary>
    public MatchValueResolutionKind? Outcome { get; set; }

    /// <summary>
    /// The values the rule would compare, one line each, in the Metaverse attribute's type.
    /// </summary>
    public List<string> ValueLines { get; set; } = [];

    /// <summary>
    /// The type the values are compared as: the target Metaverse attribute's.
    /// </summary>
    public AttributeDataType? ValueType { get; set; }

    /// <summary>
    /// Why the rule failed, when <c>Outcome</c> is <c>Failed</c>.
    /// </summary>
    public string? FailureMessage { get; set; }

    /// <summary>
    /// The attributes the rule reads, with the values tested for each.
    /// </summary>
    public List<AttributeSampleDto> Inputs { get; set; } = [];

    /// <summary>
    /// What the values find in the Metaverse. Null when the lookup was not asked for, or the rule produced no values to
    /// look up.
    /// </summary>
    public TestMatchingRuleLookupDto? Lookup { get; set; }

    /// <summary>
    /// Why the values could not be used, one message per problem; null when they could.
    /// </summary>
    public IReadOnlyList<string>? InputErrors { get; set; }
}

/// <summary>
/// What an Object Matching Rule's values found in the Metaverse, as an import join would decide it.
/// </summary>
public class TestMatchingRuleLookupDto
{
    /// <summary>
    /// What the lookup found.
    /// </summary>
    public ObjectMatchingLookupOutcome Outcome { get; set; }

    /// <summary>
    /// What a synchronisation would do, in one or two sentences.
    /// </summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>
    /// The Metaverse Object the values identify; null for no match and for an ambiguous match.
    /// </summary>
    public Guid? MetaverseObjectId { get; set; }

    /// <summary>
    /// The identified Metaverse Object's display name; null when it has none.
    /// </summary>
    public string? MetaverseObjectName { get; set; }

    /// <summary>
    /// For an ambiguous match, the Metaverse Objects the values identify, at most ten.
    /// </summary>
    public List<TestMatchingRuleCandidateDto> Candidates { get; set; } = [];

    /// <summary>
    /// For an ambiguous match, how many Metaverse Objects the values identify in all.
    /// </summary>
    public int? CandidateTotal { get; set; }
}

/// <summary>
/// A Metaverse Object an ambiguous match identifies.
/// </summary>
public class TestMatchingRuleCandidateDto
{
    /// <summary>
    /// The Metaverse Object's ID.
    /// </summary>
    public Guid Id { get; set; }

    /// <summary>
    /// Its display name; null when it has none.
    /// </summary>
    public string? Name { get; set; }

    /// <summary>
    /// Its Metaverse Object Type's name.
    /// </summary>
    public string? TypeName { get; set; }

    /// <summary>
    /// The value it matched on.
    /// </summary>
    public string? MatchedValue { get; set; }
}
