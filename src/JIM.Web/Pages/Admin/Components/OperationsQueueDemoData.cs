// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Activities;
using JIM.Models.Scheduling;
using JIM.Models.Staging;
using JIM.Models.Tasking;
using JIM.Models.Tasking.DTOs;

namespace JIM.Web.Pages.Admin.Components;

/// <summary>
/// The demo queue the Activity tab shows in development under <c>?d=true</c>: a Schedule Execution running a parallel
/// step, its later steps waiting, a second Schedule queued behind it, a manually queued run, and a task that is not a
/// Run Profile execution, so every shape the Queued and In progress sections draw can be seen without a worker.
/// </summary>
internal static class OperationsQueueDemoData
{
    private const int HrFullImport = 9001;
    private const int AdFullImport = 9002;
    private const int HrDeltaSync = 9003;
    private const int AdDeltaSync = 9004;
    private const int AdExport = 9005;
    private const int AzureExport = 9006;
    private const int LdapFullImport = 9007;

    public static List<WorkerTaskHeader> Build(Guid currentUserId, string currentUserName)
    {
        var nightly = Guid.NewGuid();
        var cloud = Guid.NewGuid();
        var now = DateTime.UtcNow;

        return
        [
            // "Nightly Full Sync": step 2 of 5 is a parallel pair, running now.
            ScheduleTask(nightly, "Nightly Full Sync", 5, 1, 1, "HR System", "Full Import", HrFullImport, WorkerTaskStatus.Processing, now.AddMinutes(-5),
                WorkerTaskExecutionMode.Parallel, started: now.AddMinutes(-4), toProcess: 12500, processed: 8320, steps: DemoImportSteps(RunPhaseKeys.ImportSave)),
            ScheduleTask(nightly, "Nightly Full Sync", 5, 1, 1, "AD", "Full Import", AdFullImport, WorkerTaskStatus.Processing, now.AddMinutes(-5),
                WorkerTaskExecutionMode.Parallel, started: now.AddMinutes(-4), toProcess: 45000, processed: 31200, steps: DemoImportSteps(RunPhaseKeys.ImportFetch)),
            ScheduleTask(nightly, "Nightly Full Sync", 5, 1, 2, "HR System", "Delta Sync", HrDeltaSync, WorkerTaskStatus.WaitingForPreviousStep, now.AddMinutes(-5), WorkerTaskExecutionMode.Parallel),
            ScheduleTask(nightly, "Nightly Full Sync", 5, 1, 2, "AD", "Delta Sync", AdDeltaSync, WorkerTaskStatus.WaitingForPreviousStep, now.AddMinutes(-5), WorkerTaskExecutionMode.Parallel),
            ScheduleTask(nightly, "Nightly Full Sync", 5, 1, 3, "AD", "Export", AdExport, WorkerTaskStatus.WaitingForPreviousStep, now.AddMinutes(-5)),

            // "Cloud Provisioning": its first step queued behind the running Schedule.
            ScheduleTask(cloud, "Cloud Provisioning", 3, 0, 0, "Azure AD", "Export", AzureExport, WorkerTaskStatus.Queued, now.AddMinutes(-1)),

            // A run queued by hand.
            new()
            {
                Id = Guid.NewGuid(), Name = "LDAP Directory - Full Import", Type = "Synchronisation",
                Status = WorkerTaskStatus.Queued, Timestamp = now.AddSeconds(-30),
                ConnectedSystemRunProfileId = LdapFullImport, ConnectedSystemName = "LDAP Directory", RunProfileName = "Full Import",
                InitiatedByType = ActivityInitiatorType.User, InitiatedById = currentUserId, InitiatedByName = currentUserName
            },

            // Not a Run Profile execution, so it has no steps and no typical duration: the estimates after it go blank.
            new()
            {
                Id = Guid.NewGuid(), Name = "HR System", Type = "Clear Connected System Objects",
                Status = WorkerTaskStatus.Queued, Timestamp = now.AddSeconds(-20),
                InitiatedByType = ActivityInitiatorType.User, InitiatedById = currentUserId, InitiatedByName = currentUserName
            }
        ];
    }

    /// <summary>Typical durations for the demo Run Profiles, so the estimates have something to show.</summary>
    public static Dictionary<int, TimeSpan> MedianDurations() => new()
    {
        [HrFullImport] = TimeSpan.FromMinutes(9),
        [AdFullImport] = TimeSpan.FromMinutes(22),
        [HrDeltaSync] = TimeSpan.FromMinutes(3),
        [AdDeltaSync] = TimeSpan.FromMinutes(6),
        [AdExport] = TimeSpan.FromMinutes(14),
        [AzureExport] = TimeSpan.FromMinutes(11),
        [LdapFullImport] = TimeSpan.FromMinutes(17)
    };

    /// <summary>
    /// The demo queue's Schedule Executions, assembled the way the real ones are. The finished and failed steps are
    /// supplied as Activity outcomes because that is where they come from in earnest: a task is deleted the moment it
    /// ends, so a demo built only from queued tasks could not show a completed step.
    /// </summary>
    public static Dictionary<Guid, ScheduleExecutionProgress?> ScheduleProgress(List<WorkerTaskHeader> headers)
    {
        var demoOutcomes = new Dictionary<string, List<ScheduleStepObservation>>
        {
            ["Nightly Full Sync"] =
            [
                new ScheduleStepObservation
                {
                    StepIndex = 0, Name = "Legacy CRM - Full Import", ActivityId = Guid.NewGuid(),
                    ActivityStatus = ActivityStatus.Complete
                }
            ]
        };

        return headers
            .Where(t => t.ScheduleExecutionId.HasValue)
            .GroupBy(t => t.ScheduleExecutionId!.Value)
            .ToDictionary(g => g.Key, g => ScheduleStepReading.FromQueue(
                g.ToList(),
                demoOutcomes.GetValueOrDefault(g.First().ScheduleExecutionName ?? string.Empty, [])));
    }

    private static WorkerTaskHeader ScheduleTask(Guid execution, string scheduleName, int totalSteps, int currentStep, int step,
        string connectedSystem, string runProfile, int runProfileId, WorkerTaskStatus status, DateTime queued,
        WorkerTaskExecutionMode mode = WorkerTaskExecutionMode.Sequential, DateTime? started = null,
        int? toProcess = null, int? processed = null, RunPhaseSummary? steps = null) => new()
    {
        Id = Guid.NewGuid(),
        Name = $"{connectedSystem} - {runProfile}",
        Type = "Synchronisation",
        Status = status,
        Timestamp = queued,
        Started = started,
        ExecutionMode = mode,
        ConnectedSystemRunProfileId = runProfileId,
        ConnectedSystemName = connectedSystem,
        RunProfileName = runProfile,
        ScheduleExecutionId = execution,
        ScheduleExecutionName = scheduleName,
        ScheduleTotalSteps = totalSteps,
        ScheduleCurrentStepIndex = currentStep,
        ScheduleStepIndex = step,
        ObjectsToProcess = toProcess,
        ObjectsProcessed = processed,
        Steps = steps,
        InitiatedByType = ActivityInitiatorType.System
    };

    /// <summary>
    /// A Full Import's steps as the demo shows them, with <paramref name="activeKey"/> running, everything before it
    /// done, and the connection step skipped so the rail's skipped state is visible.
    /// </summary>
    private static RunPhaseSummary DemoImportSteps(string activeKey)
    {
        var declared = RunProfilePhaseCatalogue.GetPhases(ConnectedSystemRunType.FullImport);
        var activeIndex = declared.ToList().FindIndex(p => p.Key == activeKey);

        var phases = declared.Select((phase, index) => new ActivityPhase
        {
            Id = Guid.NewGuid(),
            Key = phase.Key,
            Name = phase.Name,
            ParentKey = phase.ParentKey,
            Order = index,
            Status = phase.Key == RunPhaseKeys.ImportConnect
                ? ActivityPhaseStatus.Skipped
                : index < activeIndex
                    ? ActivityPhaseStatus.Completed
                    : index == activeIndex
                        ? ActivityPhaseStatus.Active
                        : ActivityPhaseStatus.Pending
        }).ToList();

        return RunPhaseSummary.From(phases)!;
    }
}
