// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Activities;
using JIM.Models.Activities.DTOs;
using JIM.Models.Enums;
using JIM.Models.Staging;
using JIM.Web.Causality;

namespace JIM.Web.Models.Api;

/// <summary>
/// One Run Profile Execution Item in a causal timeline or a Pending Export's Queued by list: what happened, in which run,
/// and its nearest recorded cause.
/// </summary>
/// <remarks>
/// Carries the item's own id as <see cref="Id"/> so a row pipes straight into the execution item endpoints and
/// cmdlets for its full causes and consequences (#2100).
/// </remarks>
public class CausalTimelineRowDto
{
    /// <summary>
    /// The Run Profile Execution Item's id.
    /// </summary>
    public Guid Id { get; set; }

    /// <summary>
    /// The Activity (run) the item belongs to.
    /// </summary>
    public Guid ActivityId { get; set; }

    /// <summary>
    /// When the run executed (UTC).
    /// </summary>
    public DateTime Executed { get; set; }

    /// <summary>
    /// What the run targeted.
    /// </summary>
    public ActivityTargetType TargetType { get; set; }

    /// <summary>
    /// The run's name, typically the Run Profile's.
    /// </summary>
    public string? TargetName { get; set; }

    /// <summary>
    /// Where the run happened, typically its Connected System's name.
    /// </summary>
    public string? TargetContext { get; set; }

    /// <summary>
    /// The Connected System the run was against, where it was against one.
    /// </summary>
    public int? ConnectedSystemId { get; set; }

    /// <summary>
    /// The kind of run: Full Import, Delta Synchronisation, Export and so on.
    /// </summary>
    public ConnectedSystemRunType? RunType { get; set; }

    /// <summary>
    /// What the item did to its object.
    /// </summary>
    public ObjectChangeType ObjectChangeType { get; set; }

    /// <summary>
    /// A short summary of the item's outcome, where one was recorded.
    /// </summary>
    public string? OutcomeSummary { get; set; }

    /// <summary>
    /// The error the item ended in, or null where it succeeded.
    /// </summary>
    public ActivityRunProfileExecutionItemErrorType? ErrorType { get; set; }

    /// <summary>
    /// The name of the object the item was about, as recorded.
    /// </summary>
    public string? DisplayName { get; set; }

    /// <summary>
    /// The object's type, as recorded.
    /// </summary>
    public string? ObjectType { get; set; }

    /// <summary>
    /// The object's external ID, where it is a Connected System Object.
    /// </summary>
    public string? ExternalId { get; set; }

    /// <summary>
    /// The Connected System Object the item was about, where it was about one.
    /// </summary>
    public Guid? ConnectedSystemObjectId { get; set; }

    /// <summary>
    /// The Metaverse Object the item was about, where it was about one.
    /// </summary>
    public Guid? MetaverseObjectId { get; set; }

    /// <summary>
    /// The nearest cause in one plain-language line, exactly as the portal reads it: the first cause and how many more
    /// there are, or why there is none ("No earlier causes recorded", "What caused this is no longer retained").
    /// </summary>
    public string Because { get; set; } = string.Empty;

    /// <summary>
    /// The item's nearest causes, grouped into cohorts; at most three, with <see cref="NearestCauseCount"/> giving the
    /// total. Empty where nothing earlier was recorded or retained. Only this first step is read, so the members' own
    /// causes are empty here; read the causes of the item with this row's Id for the full chain.
    /// </summary>
    public List<CausalChainCohortDto> NearestCauses { get; set; } = [];

    /// <summary>
    /// How many cohorts of nearest causes the item has in all.
    /// </summary>
    public int NearestCauseCount { get; set; }

    /// <summary>
    /// How the item's causes end: Resolved where it has any, NoFurtherCauses where nothing earlier was recorded, or
    /// CauseNotRetained where history retention has since removed what caused it.
    /// </summary>
    public CausalChainResolution Resolution { get; set; }

    /// <summary>
    /// Maps a timeline row to its API shape.
    /// </summary>
    public static CausalTimelineRowDto FromModel(CausalTimelineRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        var entry = row.Entry;
        return new CausalTimelineRowDto
        {
            Id = entry.RunProfileExecutionItemId,
            ActivityId = entry.ActivityId,
            Executed = entry.Executed,
            TargetType = entry.ActivityTargetType,
            TargetName = entry.ActivityTargetName,
            TargetContext = entry.ActivityTargetContext,
            ConnectedSystemId = entry.ConnectedSystemId,
            RunType = entry.RunType,
            ObjectChangeType = entry.ObjectChangeType,
            OutcomeSummary = entry.OutcomeSummary,
            ErrorType = entry.ErrorType,
            DisplayName = entry.DisplayName,
            ObjectType = entry.ObjectType,
            ExternalId = entry.ExternalId,
            ConnectedSystemObjectId = entry.ConnectedSystemObjectId,
            MetaverseObjectId = entry.MetaverseObjectId,
            Because = CausalityCauseWording.NearestCauseText(row.NearestCauses, entry.DisplayName),
            NearestCauses = row.NearestCauses.Cohorts.Select(CausalChainCohortDto.FromModel).ToList(),
            NearestCauseCount = row.NearestCauses.TotalCohortCount,
            Resolution = row.NearestCauses.Resolution
        };
    }
}
