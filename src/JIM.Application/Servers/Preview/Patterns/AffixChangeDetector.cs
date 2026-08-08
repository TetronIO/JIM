// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Preview;

namespace JIM.Application.Servers.Preview.Patterns;

/// <summary>
/// Recognises text added to or removed from one end of a value, the rest surviving intact (#827 Phase 4b).
///
/// The naming convention change: a "svc-" prefix applied across service accounts, a "_disabled" suffix appended on
/// deprovisioning, either of them being unwound. All four are the same test run from both ends, so they live in one
/// detector: it is the mutual exclusion between them that needs stating in one place.
///
/// Where an edit reads as more than one of the four it names none of them. "ab" becoming "abab" is a prefix
/// addition and a suffix addition with equal justification, and picking one would be a coin toss presented as an
/// observation.
/// </summary>
public class AffixChangeDetector : IPreviewPatternDetector
{
    public string? Detect(PreviewPatternCandidate candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);

        var oldValue = candidate.OldValue;
        var newValue = candidate.NewValue;

        // A value being set or cleared is not text being added to or taken off one, and every string starts and
        // ends with the empty string, so leaving these in would label every set and clear in the preview.
        if (string.IsNullOrEmpty(oldValue) || string.IsNullOrEmpty(newValue) ||
            string.Equals(oldValue, newValue, StringComparison.Ordinal))
        {
            return null;
        }

        string? match = null;
        var ambiguous = false;

        // Comparisons are ordinal: an "affix" that also changed the case of what it kept did not leave the original
        // text intact, and is a bigger change than this detector is entitled to describe.
        Consider(newValue.StartsWith(oldValue, StringComparison.Ordinal), PreviewPatternKeys.SuffixAdded, ref match, ref ambiguous);
        Consider(newValue.EndsWith(oldValue, StringComparison.Ordinal), PreviewPatternKeys.PrefixAdded, ref match, ref ambiguous);
        Consider(oldValue.StartsWith(newValue, StringComparison.Ordinal), PreviewPatternKeys.SuffixRemoved, ref match, ref ambiguous);
        Consider(oldValue.EndsWith(newValue, StringComparison.Ordinal), PreviewPatternKeys.PrefixRemoved, ref match, ref ambiguous);

        return ambiguous ? null : match;
    }

    private static void Consider(bool matched, string key, ref string? match, ref bool ambiguous)
    {
        if (!matched)
            return;

        if (match is null)
            match = key;
        else
            ambiguous = true;
    }
}
