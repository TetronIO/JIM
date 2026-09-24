// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using JIM.Data.Repositories;
using JIM.Models.Scheduling;
using Moq;

namespace JIM.Worker.Tests.Servers;

/// <summary>
/// Makes a mocked <see cref="ISchedulingRepository"/> behave like the real conditional Schedule Execution transitions
/// (#1768), treating the execution instance passed in as the stored row: each transition only takes effect while the
/// execution is in the status it requires, and on success updates the instance exactly as the repository does. The
/// real SQL is covered against PostgreSQL by ScheduleExecutionTransitionDatabaseTests; this is what lets server-level
/// tests assert on outcomes rather than on which mock was called.
/// </summary>
/// <remarks>
/// JIM.Web.Api.Tests carries the same helper for the SchedulerServer fixtures; the two test projects share no
/// project that references both Moq and JIM.Data, so each keeps its own copy. Keep them identical.
/// </remarks>
internal static class SchedulingRepositoryMockExtensions
{
    public static void EmulateConditionalTransitions(this Mock<ISchedulingRepository> mock)
    {
        mock.Setup(r => r.TryStartScheduleExecutionAsync(It.IsAny<ScheduleExecution>(), It.IsAny<int>()))
            .Returns((ScheduleExecution execution, int firstStepIndex) =>
            {
                if (execution.Status != ScheduleExecutionStatus.Queued)
                    return Task.FromResult(false);

                execution.Status = ScheduleExecutionStatus.InProgress;
                execution.CurrentStepIndex = firstStepIndex;
                execution.StartedAt = DateTime.UtcNow;
                return Task.FromResult(true);
            });

        mock.Setup(r => r.TryAdvanceScheduleExecutionAsync(It.IsAny<ScheduleExecution>(), It.IsAny<int>()))
            .Returns((ScheduleExecution execution, int nextStepIndex) =>
            {
                if (execution.Status != ScheduleExecutionStatus.InProgress || execution.CurrentStepIndex >= nextStepIndex)
                    return Task.FromResult(false);

                execution.CurrentStepIndex = nextStepIndex;
                return Task.FromResult(true);
            });

        mock.Setup(r => r.TryFinishScheduleExecutionAsync(
                It.IsAny<ScheduleExecution>(),
                It.IsAny<IReadOnlyCollection<ScheduleExecutionStatus>>(),
                It.IsAny<ScheduleExecutionStatus>(),
                It.IsAny<string?>()))
            .Returns((ScheduleExecution execution, IReadOnlyCollection<ScheduleExecutionStatus> fromStatuses,
                ScheduleExecutionStatus finalStatus, string? errorMessage) =>
            {
                if (!fromStatuses.Contains(execution.Status))
                    return Task.FromResult(false);

                execution.Status = finalStatus;
                execution.CompletedAt = DateTime.UtcNow;
                if (errorMessage != null)
                    execution.ErrorMessage = errorMessage;
                return Task.FromResult(true);
            });
    }
}
