// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Text.Json.Serialization;
using JIM.Models.Activities;

namespace JIM.Models.Preview;

/// <summary>
/// One of the objects a Configuration Change Preview predicts an ambiguous match would find (#2052), kept with the
/// row that predicts it so the drill-down can list and link every candidate, as the Activity item a real run would
/// record does.
/// </summary>
/// <remarks>
/// A child of the row with a cascading foreign key, so the preview's retention housekeeping (which removes the
/// preview with its Activity) removes these too. The candidate itself is a snapshot with no foreign key; see
/// <see cref="MatchCandidateSnapshot"/>.
/// </remarks>
public class ConfigurationChangePreviewDeltaMatchCandidate : MatchCandidateSnapshot
{
    public Guid Id { get; set; }

    /// <summary>The row predicting the ambiguity. Cascades on delete.</summary>
    public Guid ConfigurationChangePreviewDeltaId { get; set; }

    /// <summary>
    /// Foreign key navigation for <see cref="ConfigurationChangePreviewDeltaId"/>. Never serialised: a candidate is
    /// only ever read as one of its row's.
    /// </summary>
    [JsonIgnore]
    public ConfigurationChangePreviewDelta ConfigurationChangePreviewDelta { get; set; } = null!;

    /// <summary>A copy of <paramref name="candidate"/> for a preview row to keep.</summary>
    public static ConfigurationChangePreviewDeltaMatchCandidate From(MatchCandidateSnapshot candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);

        var copy = new ConfigurationChangePreviewDeltaMatchCandidate();
        candidate.CopySnapshotTo(copy);
        return copy;
    }
}
