// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Web.Models;

/// <summary>The filters of the Operations page's Activity tab, one per control in its filter row (#2549).</summary>
public enum ActivityFilterKind
{
    ConnectedSystems,
    RunProfiles,
    Schedules,
    Results,
    HasChildActivities,
    ScheduledOnly
}
