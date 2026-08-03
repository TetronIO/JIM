// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System;
using System.Linq;
using JIM.Models.Scheduling;
using NUnit.Framework;

namespace JIM.Models.Tests.Scheduling;

/// <summary>
/// Tests <see cref="ScheduleExecutionStepStatusExtensions.ToDisplayString"/>, the single source of the step status
/// labels rendered by the portal and returned as ScheduleExecutionStepDto.Status on
/// GET /api/v1/schedule-executions/{id}. Those labels are a published REST contract, so each one is asserted
/// literally here; changing a string in the implementation must fail this test and be a deliberate decision.
/// Note "Completed with Warning" and "Completed with Error" carry a lowercase "with" and so cannot be derived
/// mechanically from the enum member name.
/// </summary>
[TestFixture]
public class ScheduleExecutionStepStatusExtensionsTests
{
    [Test]
    public void ToDisplayString_EveryStatus_ReturnsItsPublishedLabel()
    {
        Assert.Multiple(() =>
        {
            Assert.That(ScheduleExecutionStepStatus.Pending.ToDisplayString(), Is.EqualTo("Pending"));
            Assert.That(ScheduleExecutionStepStatus.Waiting.ToDisplayString(), Is.EqualTo("Waiting"));
            Assert.That(ScheduleExecutionStepStatus.Queued.ToDisplayString(), Is.EqualTo("Queued"));
            Assert.That(ScheduleExecutionStepStatus.Processing.ToDisplayString(), Is.EqualTo("Processing"));
            Assert.That(ScheduleExecutionStepStatus.Cancelling.ToDisplayString(), Is.EqualTo("Cancelling"));
            Assert.That(ScheduleExecutionStepStatus.Completed.ToDisplayString(), Is.EqualTo("Completed"));
            Assert.That(ScheduleExecutionStepStatus.CompletedWithWarning.ToDisplayString(), Is.EqualTo("Completed with Warning"));
            Assert.That(ScheduleExecutionStepStatus.CompletedWithError.ToDisplayString(), Is.EqualTo("Completed with Error"));
            Assert.That(ScheduleExecutionStepStatus.Failed.ToDisplayString(), Is.EqualTo("Failed"));
            Assert.That(ScheduleExecutionStepStatus.Cancelled.ToDisplayString(), Is.EqualTo("Cancelled"));
            Assert.That(ScheduleExecutionStepStatus.Unknown.ToDisplayString(), Is.EqualTo("Unknown"));
        });
    }

    /// <summary>
    /// Guards the contract from the other direction: a status added to the enum without a label lands on the
    /// "Unknown" fallback and would ship a step silently mislabelled in the portal and on the REST API.
    /// </summary>
    [Test]
    public void ToDisplayString_EveryEnumMember_HasItsOwnLabel()
    {
        var statuses = Enum.GetValues<ScheduleExecutionStepStatus>()
            .Where(s => s != ScheduleExecutionStepStatus.Unknown)
            .ToList();

        var unlabelled = statuses.Where(s => s.ToDisplayString() == "Unknown").ToList();

        Assert.That(unlabelled, Is.Empty,
            "every ScheduleExecutionStepStatus needs its own label; these fell through to the Unknown fallback: " +
            string.Join(", ", unlabelled));
    }

    /// <summary>
    /// British English is mandated across all user-facing text, and "Cancelled" is the spelling used by
    /// ScheduleExecutionStatus and the existing REST responses.
    /// </summary>
    [Test]
    public void ToDisplayString_CancelledLabels_UseBritishEnglishSpelling()
    {
        Assert.Multiple(() =>
        {
            Assert.That(ScheduleExecutionStepStatus.Cancelled.ToDisplayString(), Does.Not.Contain("Canceled"));
            Assert.That(ScheduleExecutionStepStatus.Cancelling.ToDisplayString(), Does.Not.Contain("Canceling"));
        });
    }
}
