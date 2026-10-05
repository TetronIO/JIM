// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Logic.Scoping;

/// <summary>
/// How one scoping criterion evaluated against an object (#348). Only <see cref="Met"/> counts towards its group;
/// <see cref="Invalid"/> is the one outcome synchronisation does not tolerate (it fails the evaluation loudly).
/// </summary>
public enum ScopingCriterionOutcome
{
    /// <summary>The object's value satisfies the criterion.</summary>
    Met = 0,

    /// <summary>The object has a value and it does not satisfy the criterion.</summary>
    NotMet = 1,

    /// <summary>
    /// The object has no value for the attribute, which fails every comparison except an Equals criterion with an
    /// empty expected value (that case is <see cref="Met"/>).
    /// </summary>
    NoValue = 2,

    /// <summary>The criterion references no attribute, for example after the attribute was deleted; never met.</summary>
    AttributeMissing = 3,

    /// <summary>
    /// The comparison is not valid for the attribute's data type. Synchronisation throws when it reaches such a
    /// criterion; an explanation records it instead, so the rest of the tree can still be shown.
    /// </summary>
    Invalid = 4
}

/// <summary>
/// Whether an object is in scope of a Synchronisation Rule, as synchronisation would decide it (#348).
/// </summary>
public enum ScopingRuleOutcome
{
    /// <summary>A top-level criteria group is met, or the rule has no scoping criteria.</summary>
    InScope = 0,

    /// <summary>No top-level criteria group is met.</summary>
    OutOfScope = 1,

    /// <summary>
    /// Synchronisation would fail evaluating this rule: it reaches an <see cref="ScopingCriterionOutcome.Invalid"/>
    /// criterion before finding a met top-level group.
    /// </summary>
    Undetermined = 2
}

/// <summary>
/// What one piece of an explanation's text is (#348), so a surface can style values and attribute names while the
/// plain text stays identical everywhere.
/// </summary>
public enum ExplanationSegmentKind
{
    /// <summary>Connecting words.</summary>
    Text = 0,

    /// <summary>An attribute's name.</summary>
    Attribute = 1,

    /// <summary>The value a criterion requires.</summary>
    ExpectedValue = 2,

    /// <summary>The value the object holds.</summary>
    CurrentValue = 3,

    /// <summary>The object holds no value.</summary>
    NoValue = 4,

    /// <summary>A value withheld because the attribute may hold a credential.</summary>
    Hidden = 5
}
