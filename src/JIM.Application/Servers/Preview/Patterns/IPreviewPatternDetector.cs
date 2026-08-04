// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Application.Servers.Preview.Patterns;

/// <summary>
/// Recognises one kind of edit across a preview's deltas (#827 Phase 4b).
///
/// A detector turns "38,900 objects would have Email changed" into "38,900 objects would have their email domain
/// changed", which is the difference between a number and a sentence an administrator can act on.
///
/// The contract is silence by default. A detector returns null unless it is certain, because an unlabelled group
/// still carries its exact count and its old and new values, and loses almost nothing; a group labelled with the
/// wrong pattern is an assertion about what a change means, and an administrator who reads "casing changed" will
/// not look any further. Implementations must be pure, deterministic and free of state: the same candidate must
/// give the same answer on every run, or the same preview re-read looks like a different one.
/// </summary>
public interface IPreviewPatternDetector
{
    /// <summary>
    /// The pattern this detector recognises in <paramref name="candidate"/>, as a key from
    /// <see cref="JIM.Models.Preview.PreviewPatternKeys"/>, or null where it recognises nothing.
    /// </summary>
    string? Detect(PreviewPatternCandidate candidate);
}
