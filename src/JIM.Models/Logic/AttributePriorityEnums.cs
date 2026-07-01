// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Logic;

/// <summary>
/// The tri-state contribution a single import Synchronisation Rule makes to a Metaverse Object attribute during
/// attribute priority resolution (#91). The distinction between <see cref="RuleNotApplicable"/> and
/// <see cref="ConnectedNoValue"/> is what makes "Null is a value" coherent: a rule that has no opinion is skipped
/// regardless of its null handling, whereas a connected, in-scope rule that yields nothing can assert null.
/// </summary>
public enum ContributionState
{
    /// <summary>
    /// The rule does not apply to this Metaverse Object: it is disabled, no Connected System Object from the
    /// rule's Connected System is joined to the Metaverse Object, or the joined Connected System Object is out of
    /// the rule's scope. Always skipped, regardless of the mapping's "Null is a value" flag.
    /// </summary>
    RuleNotApplicable = 0,

    /// <summary>
    /// The rule is enabled and a joined, in-scope Connected System Object exists, but the mapping yields no value
    /// (for a multivalued attribute, the empty set). If the mapping's "Null is a value" flag is set this asserts
    /// null and stops resolution; otherwise resolution falls through to the next priority.
    /// </summary>
    ConnectedNoValue = 1,

    /// <summary>
    /// The rule is enabled, a joined, in-scope Connected System Object exists, and the mapping yields a value
    /// (for a multivalued attribute, the entire value set). This contribution wins resolution.
    /// </summary>
    ConnectedWithValue = 2
}

/// <summary>
/// The outcome of resolving a Metaverse Object attribute across its contributing import Synchronisation Rules (#91).
/// </summary>
public enum AttributeResolutionOutcome
{
    /// <summary>
    /// A connected, in-scope contribution provided a value; that value wins. The winning contribution carries the
    /// provenance to stamp onto the resulting Metaverse Object attribute value.
    /// </summary>
    Value = 0,

    /// <summary>
    /// A connected, in-scope contribution with "Null is a value" set asserted no value. Persisted as a
    /// <c>NullValue</c> marker row stamped with the winning contribution's provenance.
    /// </summary>
    AssertedNull = 1,

    /// <summary>
    /// No contribution had an opinion (every contributor was not applicable, or fell through without asserting
    /// null). No row is persisted; any existing contributed value is removed.
    /// </summary>
    NoContributor = 2
}
