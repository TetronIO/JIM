// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Staging.DTOs;

namespace JIM.Models.Activities.DTOs;

/// <summary>
/// When a Synchronisation Rule change reaches the objects that already exist, as the save will treat it (#2615). Stated
/// before the save, in the acknowledgement, so the administrator is told what will actually happen rather than a
/// reminder that applies to a different kind of change.
/// <para>
/// It is computed from the same decisions the save acts on (<c>ConfigurationChangeSynchronisationPolicy</c>), never from
/// a copy of them, so what the dialog promises and what the save does cannot disagree. A change is one of: carried by
/// the next synchronisation of its Connected System processing a whole Object Type (an import change), carried by the
/// next synchronisation of any Connected System reviewing the Metaverse Objects of a type against the export rules (an
/// export change that moves scope), or left for a Full Synchronisation. One change can be more than one of these.
/// </para>
/// </summary>
public class ConfigurationChangeEffect
{
    /// <summary>
    /// The Object Types the save records as awaiting synchronisation: the next synchronisation of their Connected System,
    /// Delta or Full, processes every object of each. Empty when the save records nothing.
    /// </summary>
    public IReadOnlyList<AwaitingSynchronisationEstimate> ObjectTypesProcessedInFull { get; init; } = [];

    /// <summary>
    /// The Metaverse Object Type whose objects the save flags for review against every export rule, which the next
    /// synchronisation of any Connected System carries out. Null when the save flags none.
    /// </summary>
    public string? ExportReviewMetaverseObjectTypeName { get; init; }

    /// <summary>
    /// The sections of the change that still reach existing objects only at a Full Synchronisation, by the labels the
    /// change history gives them (for example "Attribute Flows"). Empty when nothing is left for one.
    /// </summary>
    public IReadOnlyList<string> FullSynchronisationSections { get; init; } = [];

    /// <summary>
    /// The Connected System whose Full Synchronisation applies <see cref="FullSynchronisationSections"/>.
    /// </summary>
    public string? FullSynchronisationConnectedSystemName { get; init; }

    /// <summary>
    /// True when the save records Object Types as awaiting synchronisation.
    /// </summary>
    public bool RecordsAwaitingSynchronisation => ObjectTypesProcessedInFull.Count > 0;

    /// <summary>
    /// True when the save flags Metaverse Objects for export review.
    /// </summary>
    public bool FlagsExportReview => ExportReviewMetaverseObjectTypeName != null;

    /// <summary>
    /// True when some of the change reaches existing objects only at a Full Synchronisation.
    /// </summary>
    public bool NeedsFullSynchronisation => FullSynchronisationSections.Count > 0;

    /// <summary>
    /// A change with no effect on existing objects to state.
    /// </summary>
    public static ConfigurationChangeEffect None { get; } = new();
}
