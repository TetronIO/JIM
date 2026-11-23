// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Activities;
using JIM.Models.Localisation;

namespace JIM.Web.Localisation;

/// <summary>How the portal shows what an Activity was about (#3030).</summary>
public static class ActivityDisplay
{
    /// <summary>
    /// The Activity's target name for display: in the viewer's language where JIM wrote the name itself ("Factory
    /// reset", "Rule for User"), and as stored where it is a name someone gave or the Activity predates stored target
    /// names. Logic that matches on the name (a run type, a link built from it, a search) reads <see cref="Activity.TargetName"/>.
    /// </summary>
    public static string? TargetNameForDisplay(this Activity activity) =>
        activity.TargetNameDetail?.ToCurrentLanguage() ?? activity.TargetName;
}
