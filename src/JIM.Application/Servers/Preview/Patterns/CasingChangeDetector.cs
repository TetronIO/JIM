// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Preview;

namespace JIM.Application.Servers.Preview.Patterns;

/// <summary>
/// Recognises a change that alters nothing but letter case (#827 Phase 4b).
///
/// Worth naming precisely because it looks alarming and usually is not: a group whose old and new values read
/// identically at a glance is otherwise the most confusing thing a preview can show. It runs before the other
/// detectors, so a value whose domain or container differs only in case is described as the narrower change it
/// actually is rather than sending an administrator looking for a cutover that is not happening.
/// </summary>
public class CasingChangeDetector : IPreviewPatternDetector
{
    public string? Detect(PreviewPatternCandidate candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);

        // A value being set or cleared is a different kind of event, and one the transition itself already says.
        if (string.IsNullOrEmpty(candidate.OldValue) || string.IsNullOrEmpty(candidate.NewValue))
            return null;

        return !string.Equals(candidate.OldValue, candidate.NewValue, StringComparison.Ordinal) &&
               string.Equals(candidate.OldValue, candidate.NewValue, StringComparison.OrdinalIgnoreCase)
            ? PreviewPatternKeys.CasingChanged
            : null;
    }
}
