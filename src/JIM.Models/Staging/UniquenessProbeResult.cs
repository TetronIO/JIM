// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Models.Staging;

/// <summary>
/// What a live uniqueness probe batch found (Unique Value Generation, #242, release 3): one
/// <see cref="UniquenessProbeOutcome"/> per candidate, and why when the probe could not tell.
/// </summary>
public sealed class UniquenessProbeResult
{
    private UniquenessProbeResult(IReadOnlyList<UniquenessProbeOutcome> outcomes, string? reason, bool isFailure)
    {
        Outcomes = outcomes;
        Reason = reason;
        IsFailure = isFailure;
    }

    /// <summary>
    /// One outcome per candidate, in the request's order.
    /// </summary>
    public IReadOnlyList<UniquenessProbeOutcome> Outcomes { get; }

    /// <summary>
    /// Why the outcomes are <see cref="UniquenessProbeOutcome.CouldNotDetermine"/>, as a sentence an
    /// administrator can act on (no trailing full stop); null when the probe could tell.
    /// </summary>
    public string? Reason { get; }

    /// <summary>
    /// True when the search itself failed (refused, errored, or timed out), as opposed to completing without
    /// returning its control value. A failure says the Connected System cannot be probed by this connection at
    /// all, so JIM stops asking it for the rest of the run.
    /// </summary>
    public bool IsFailure { get; }

    /// <summary>
    /// Builds the result from the values a completed search returned for the attribute. When the control value
    /// is among them, each candidate is <see cref="UniquenessProbeOutcome.Found"/> or
    /// <see cref="UniquenessProbeOutcome.NotFound"/>; otherwise every candidate is
    /// <see cref="UniquenessProbeOutcome.CouldNotDetermine"/>, because a search that cannot see a value JIM knows
    /// is there proves nothing by returning nothing. With no control value, a candidate the search returned is
    /// <see cref="UniquenessProbeOutcome.Found"/> and the rest are <see cref="UniquenessProbeOutcome.NotFound"/>
    /// (unconfirmed; see <see cref="UniquenessProbeRequest.ControlValue"/>). Comparison is case-insensitive, as
    /// uniqueness is throughout Unique Value Generation (plan decision 13).
    /// </summary>
    public static UniquenessProbeResult FromValuesFound(UniquenessProbeRequest request, IEnumerable<string> valuesFound)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(valuesFound);

        var found = new HashSet<string>(valuesFound, StringComparer.OrdinalIgnoreCase);
        if (request.ControlValue != null && !found.Contains(request.ControlValue))
        {
            return new UniquenessProbeResult(
                Enumerable.Repeat(UniquenessProbeOutcome.CouldNotDetermine, request.Candidates.Count).ToList(),
                $"The probe did not return a {request.AttributeName} value JIM knows is there, so the account JIM connects with may not be able to see the values already in use",
                isFailure: false);
        }

        return new UniquenessProbeResult(
            request.Candidates.Select(c => found.Contains(c) ? UniquenessProbeOutcome.Found : UniquenessProbeOutcome.NotFound).ToList(),
            reason: null,
            isFailure: false);
    }

    /// <summary>
    /// The search completed but cannot answer for this attribute (for example, so many entries hold the values
    /// that the answer was cut short): every candidate is <see cref="UniquenessProbeOutcome.CouldNotDetermine"/>,
    /// and <paramref name="reason"/> says why. Unlike <see cref="Failed"/>, this says nothing about the Connected
    /// System's other attributes.
    /// </summary>
    public static UniquenessProbeResult Undetermined(int candidateCount, string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);

        return new UniquenessProbeResult(
            Enumerable.Repeat(UniquenessProbeOutcome.CouldNotDetermine, candidateCount).ToList(),
            reason,
            isFailure: false);
    }

    /// <summary>
    /// The search could not be completed: every candidate is <see cref="UniquenessProbeOutcome.CouldNotDetermine"/>
    /// and <paramref name="reason"/> says why.
    /// </summary>
    public static UniquenessProbeResult Failed(int candidateCount, string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);

        return new UniquenessProbeResult(
            Enumerable.Repeat(UniquenessProbeOutcome.CouldNotDetermine, candidateCount).ToList(),
            reason,
            isFailure: true);
    }
}
