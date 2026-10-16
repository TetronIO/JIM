// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Globalization;

namespace JIM.Application.UniqueValues;

/// <summary>
/// The wording of what a generation's forms did (Generated Value Forms, #2023), shared by the synchronisation that
/// records it and the Sync Preview that predicts it, so the two say the same thing in the same words.
/// </summary>
public static class GeneratedValueFormReporting
{
    /// <summary>
    /// The most candidates passed over for one generated value that are reported one by one; any beyond are counted in
    /// one more line.
    /// </summary>
    public const int MaximumSkippedCandidatesReported = 10;

    /// <summary>
    /// One line per candidate passed over (<see cref="GenerationOutcome.SkippedCandidates"/>), at most
    /// <see cref="MaximumSkippedCandidatesReported"/> of them, then one line counting the rest.
    /// </summary>
    public static IEnumerable<string> DescribeSkippedCandidates(IReadOnlyList<string> skipped)
    {
        ArgumentNullException.ThrowIfNull(skipped);

        foreach (var candidate in skipped.Take(MaximumSkippedCandidatesReported))
            yield return candidate;

        if (skipped.Count > MaximumSkippedCandidatesReported)
            yield return string.Create(CultureInfo.InvariantCulture, $"and {skipped.Count - MaximumSkippedCandidatesReported} more candidates passed over for the same reasons");
    }

    /// <summary>
    /// What a waiting generated value waits for (<see cref="GenerationOutcome.FormWaits"/>): "Region (needed by Email)",
    /// one input after another.
    /// </summary>
    public static string DescribeWaits(IReadOnlyList<GeneratedValueFormWait> waits)
    {
        ArgumentNullException.ThrowIfNull(waits);
        return string.Join("; ", waits.Select(wait => $"{wait.InputName} (needed by {wait.NeededBy})"));
    }
}
