// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Scheduling;

namespace JIM.Web.Models.Api;

/// <summary>
/// Works out the failure setting a step request asks for (#1787): the one place the REST API turns a step's
/// <c>onFailure</c> and <c>continueOnFailure</c> into the stored <see cref="ScheduleStep.OnFailure"/>.
/// </summary>
/// <remarks>
/// <para>
/// <c>onFailure</c> is the step's own setting, and when it is supplied it wins; <c>continueOnFailure</c> is then
/// ignored, whatever it says.
/// </para>
/// <para>
/// <c>continueOnFailure</c> is kept for existing clients, and it reads as the step's EFFECTIVE behaviour (its own
/// setting, or its Schedule's). Mapping it literally on write would silently pin every step of a continuing Schedule
/// that a get-modify-put client merely sent back, so when <c>onFailure</c> is absent:
/// </para>
/// <list type="bullet">
/// <item>A new step: <c>true</c> means Continue; <c>false</c> or absent means Follow the Schedule, because
/// <c>false</c> has always meant "the default".</item>
/// <item>An existing step: absent, or equal to what the step does now, leaves its setting unchanged. A value that
/// differs asks for the opposite of what it does now, so <c>true</c> means Continue and <c>false</c> means Stop.</item>
/// </list>
/// <para>
/// "What the step does now" is judged against the Schedule's setting as stored before the update, so a request that
/// changes the Schedule's setting and echoes the steps it read beforehand does not pin them.
/// </para>
/// </remarks>
public static class ScheduleStepFailureWriteRule
{
    /// <summary>
    /// Resolves the failure setting a step request asks for; see the class remarks for the rule.
    /// </summary>
    /// <param name="onFailure">The request's <c>onFailure</c>, or null when absent.</param>
    /// <param name="continueOnFailure">The request's <c>continueOnFailure</c>, or null when absent.</param>
    /// <param name="existingStep">The stored step the request updates, or null for a new step.</param>
    /// <param name="storedSchedule">The step's Schedule as stored before this update. Only read for an existing step.</param>
    public static ScheduleStepFailureBehaviour Resolve(
        ScheduleStepFailureBehaviour? onFailure,
        bool? continueOnFailure,
        ScheduleStep? existingStep,
        Schedule? storedSchedule)
    {
        if (onFailure.HasValue)
            return onFailure.Value;

        if (existingStep == null)
            return continueOnFailure == true ? ScheduleStepFailureBehaviour.Continue : ScheduleStepFailureBehaviour.FollowSchedule;

        if (!continueOnFailure.HasValue || continueOnFailure.Value == ScheduleFailureHandling.ContinuesOnFailure(existingStep, storedSchedule))
            return existingStep.OnFailure;

        return continueOnFailure.Value ? ScheduleStepFailureBehaviour.Continue : ScheduleStepFailureBehaviour.Stop;
    }

    /// <summary>
    /// Resolves the failure setting a step request asks for, reading both fields off the request.
    /// </summary>
    /// <param name="request">The step request.</param>
    /// <param name="existingStep">The stored step the request updates, or null for a new step.</param>
    /// <param name="storedSchedule">The step's Schedule as stored before this update. Only read for an existing step.</param>
    public static ScheduleStepFailureBehaviour Resolve(ScheduleStepRequest request, ScheduleStep? existingStep, Schedule? storedSchedule)
    {
        ArgumentNullException.ThrowIfNull(request);
        return Resolve(request.OnFailure, request.ContinueOnFailure, existingStep, storedSchedule);
    }
}
