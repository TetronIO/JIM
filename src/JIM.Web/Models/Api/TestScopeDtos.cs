// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Core;
using JIM.Models.Logic.Scoping;

namespace JIM.Web.Models.Api;

/// <summary>
/// A test of a Synchronisation Rule's scoping criteria: the object to test, as sample values, a real object or both,
/// and the instant to test as of.
/// </summary>
public class TestScopeRequest
{
    /// <summary>
    /// Sample values for the attributes the scoping criteria compare, keyed by attribute name: on the Connected System
    /// Object for an import rule, the Metaverse Object for an export rule. Each value is converted to its attribute's
    /// type as the object would hold it: text, a number, a Boolean, null for no value, or an array of these for a
    /// multi-valued attribute. An attribute not given holds no value.
    /// </summary>
    public Dictionary<string, object?>? Attributes { get; set; }

    /// <summary>
    /// A Connected System Object of the rule's Connected System whose values to test with, for an import rule. A
    /// sample in <c>Attributes</c> replaces the object's value for that attribute.
    /// </summary>
    public Guid? ConnectedSystemObjectId { get; set; }

    /// <summary>
    /// A Metaverse Object whose values to test with, for an export rule. A sample in <c>Attributes</c> replaces the
    /// object's value for that attribute.
    /// </summary>
    public Guid? MetaverseObjectId { get; set; }

    /// <summary>
    /// The instant to test as of, in UTC, which relative date criteria such as "30 days ago" resolve against. Omit it
    /// for the current time.
    /// </summary>
    public DateTime? AsOf { get; set; }
}

/// <summary>
/// Whether an object holding the tested values is in scope of the Synchronisation Rule, evaluated as synchronisation
/// evaluates it, and when that would change.
/// </summary>
public class TestScopeResponse
{
    /// <summary>
    /// False when the values could not be used; <c>InputErrors</c> says why, and nothing was evaluated.
    /// </summary>
    public bool IsValid { get; set; }

    /// <summary>
    /// Whether the object is in scope, out of scope, or cannot be evaluated because the evaluation reaches an invalid
    /// criterion. Null when <c>IsValid</c> is false.
    /// </summary>
    public ScopingRuleOutcome? Outcome { get; set; }

    /// <summary>
    /// The instant the test was evaluated as of, in UTC.
    /// </summary>
    public DateTime EvaluatedAt { get; set; }

    /// <summary>
    /// The scoping criteria as they evaluated: each group with whether it was met, each criterion with what the object
    /// held. Null when <c>IsValid</c> is false.
    /// </summary>
    public ScopingExplanationDto? Explanation { get; set; }

    /// <summary>
    /// When the outcome next changes as time passes, the values staying as they are. Null when it never does, which is
    /// so whenever no relative date criterion can change it.
    /// </summary>
    public TestScopeChangeDto? NextChange { get; set; }

    /// <summary>
    /// The attributes the scoping criteria compare, with the values tested for each.
    /// </summary>
    public List<TestScopeInputDto> Inputs { get; set; } = [];

    /// <summary>
    /// Why the values could not be used, one message per problem; null when they could.
    /// </summary>
    public IReadOnlyList<string>? InputErrors { get; set; }
}

/// <summary>
/// An attribute a Synchronisation Rule's scoping criteria compare, with the values a test used for it.
/// </summary>
public class TestScopeInputDto
{
    /// <summary>
    /// The attribute's name.
    /// </summary>
    public string Attribute { get; set; } = null!;

    /// <summary>
    /// The attribute's type.
    /// </summary>
    public AttributeDataType Type { get; set; }

    /// <summary>
    /// Whether the attribute holds one value or several.
    /// </summary>
    public AttributePlurality Plurality { get; set; }

    /// <summary>
    /// The values tested, one line each; empty for no value.
    /// </summary>
    public List<string> ValueLines { get; set; } = [];
}

/// <summary>
/// When a tested object's scope next changes as time passes.
/// </summary>
public class TestScopeChangeDto
{
    /// <summary>
    /// The instant the outcome changes, in UTC.
    /// </summary>
    public DateTime At { get; set; }

    /// <summary>
    /// The outcome from that instant.
    /// </summary>
    public ScopingRuleOutcome Outcome { get; set; }
}
