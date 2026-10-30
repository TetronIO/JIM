// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Staging;

namespace JIM.Web;

/// <summary>
/// Which Run Profile the "Run now" action on a Connected System's changes awaiting synchronisation queues (#2615).
/// </summary>
/// <remarks>
/// The Connected System's next synchronisation, Delta or Full, applies the changes, so either run type answers them. The
/// Delta Synchronisation is offered first: it processes the awaiting Object Types in full and everything else as it
/// ordinarily would, where a Full Synchronisation processes every object of every type for no further benefit. A system
/// with no Delta Synchronisation Run Profile falls back to its Full Synchronisation. The notice's caption and the dialog
/// it opens both choose through this class, so what the caption promises is what the dialog preselects.
/// </remarks>
public static class AwaitingSynchronisationRunProfile
{
    /// <summary>
    /// The Run Profile to preselect: the Connected System's Delta Synchronisation, or failing that its Full
    /// Synchronisation. Null when it has neither, and so nothing that applies the changes can be queued from here. Where
    /// there are several of one type, the earliest created is chosen, so the choice does not change as others are added.
    /// </summary>
    public static ConnectedSystemRunProfile? Preferred(IEnumerable<ConnectedSystemRunProfile>? runProfiles)
    {
        if (runProfiles == null)
            return null;

        var candidates = runProfiles.OrderBy(runProfile => runProfile.Id).ToList();
        return candidates.FirstOrDefault(runProfile => runProfile.RunType == ConnectedSystemRunType.DeltaSynchronisation)
               ?? candidates.FirstOrDefault(runProfile => runProfile.RunType == ConnectedSystemRunType.FullSynchronisation);
    }

    /// <summary>
    /// The run type's name as the notice's caption says it: "Delta Synchronisation" or "Full Synchronisation".
    /// </summary>
    public static string RunTypeName(ConnectedSystemRunType runType) => runType switch
    {
        ConnectedSystemRunType.DeltaSynchronisation => "Delta Synchronisation",
        ConnectedSystemRunType.FullSynchronisation => "Full Synchronisation",
        _ => runType.ToString()
    };
}
