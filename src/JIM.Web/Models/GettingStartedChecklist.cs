// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Web.Models;

/// <summary>
/// The state of the home page's Getting Started checklist: the four steps an administrator completes to get a new
/// deployment synchronising identities, and whether the whole checklist is done (at which point it is hidden).
/// </summary>
/// <param name="ConnectedSystemCreated">At least one Connected System exists.</param>
/// <param name="SynchronisationRulesCreated">At least one Synchronisation Rule exists.</param>
/// <param name="ScheduleCreated">At least one Schedule exists.</param>
/// <param name="FirstSynchronisationRun">A synchronisation has been run, by hand or by a Schedule.</param>
public sealed record GettingStartedChecklist(
    bool ConnectedSystemCreated,
    bool SynchronisationRulesCreated,
    bool ScheduleCreated,
    bool FirstSynchronisationRun)
{
    /// <summary>
    /// Whether every step is complete, and so the checklist has nothing left to say.
    /// </summary>
    public bool AllComplete =>
        ConnectedSystemCreated && SynchronisationRulesCreated && ScheduleCreated && FirstSynchronisationRun;

    /// <summary>
    /// Evaluates the checklist from the counts and run history the home page has loaded.
    /// </summary>
    /// <param name="connectedSystemCount">How many Connected Systems exist.</param>
    /// <param name="syncRuleCount">How many Synchronisation Rules exist.</param>
    /// <param name="scheduleCount">How many Schedules exist.</param>
    /// <param name="anyScheduleHasRun">Whether any Schedule has ever fired.</param>
    /// <param name="anyRunProfileExecuted">Whether any Run Profile has ever been executed.</param>
    public static GettingStartedChecklist Evaluate(
        int connectedSystemCount,
        int syncRuleCount,
        int scheduleCount,
        bool anyScheduleHasRun,
        bool anyRunProfileExecuted)
    {
        // Either route counts as a first synchronisation. Running a Run Profile by hand from Operations is how an
        // administrator normally runs their first one, and a Schedule may be disabled (or not yet due), so keying
        // the step on Schedules alone leaves the checklist permanently unfinished on a system that is demonstrably
        // synchronising.
        return new GettingStartedChecklist(
            ConnectedSystemCreated: connectedSystemCount > 0,
            SynchronisationRulesCreated: syncRuleCount > 0,
            ScheduleCreated: scheduleCount > 0,
            FirstSynchronisationRun: anyRunProfileExecuted || anyScheduleHasRun);
    }
}
