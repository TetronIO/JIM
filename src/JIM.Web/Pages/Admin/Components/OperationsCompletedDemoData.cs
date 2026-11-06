// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Activities;
using JIM.Models.Staging;

namespace JIM.Web.Pages.Admin.Components;

/// <summary>
/// The finished work the Activity tab's Completed list shows in demo mode (<c>?d=true</c>, Development only): every
/// outcome the list renders, every run type, every finished status and every kind of initiator, at counts from one
/// to hundreds of thousands, so the list can be judged fully populated without a worker. Held to that by
/// <c>OperationsDemoDataTests</c>.
/// </summary>
public static class OperationsCompletedDemoData
{
    private const string LongSystemName = "Corporate Directory (Northern Europe and Asia Pacific)";

    public static List<Activity> Build(DateTime now)
    {
        var nightly = Guid.NewGuid();
        var minute = 0;
        DateTime Ago() => now.AddMinutes(-(minute += 7));

        return
        [
            // A Schedule's synchronisation, with every outcome a synchronisation reports.
            Run("HR System", "Delta Sync", ConnectedSystemRunType.DeltaSynchronisation, ActivityStatus.CompleteWithError, Ago(), 42, Schedule(nightly),
                a =>
                {
                    a.TotalProjected = 1_204; a.TotalJoined = 312; a.TotalAttributeFlows = 48_211; a.TotalDisconnected = 5_000;
                    a.TotalDisconnectedOutOfScope = 87; a.TotalOutOfScopeRetainJoin = 23; a.TotalDeleted = 5_000;
                    a.TotalDriftCorrections = 9; a.TotalProvisioned = 1_198; a.TotalPendingExports = 10_421; a.TotalErrors = 3;
                }),
            Run("Yellowstone APAC", "Full Sync", ConnectedSystemRunType.FullSynchronisation, ActivityStatus.Complete, Ago(), 1_212, ApiKey(),
                a => { a.TotalProjected = 524_997; a.TotalJoined = 524_997; a.TotalAttributeFlows = 524_997; a.TotalPendingExports = 524_997; }),
            Run(LongSystemName, "Full Import (Scoped)", ConnectedSystemRunType.FullImport, ActivityStatus.Complete, Ago(), 1_486, ApiKey(),
                a => a.TotalAdded = 524_997),
            Run("HR System", "Delta Import", ConnectedSystemRunType.DeltaImport, ActivityStatus.CompleteWithWarning, Ago(), 9, Schedule(nightly),
                a => { a.TotalAdded = 12; a.TotalUpdated = 5_000; a.TotalDeleted = 1; a.WarningMessage = "2 rows were skipped: no employee number."; }),
            Run("Glitterband EMEA", "Export", ConnectedSystemRunType.Export, ActivityStatus.Complete, Ago(), 51, Schedule(nightly),
                a => { a.TotalExported = 5_421; a.TotalDeprovisioned = 5_000; }),
            Run("Glitterband EMEA", "Delta Sync", ConnectedSystemRunType.DeltaSynchronisation, ActivityStatus.Complete, Ago(), 0.3, Person()),
            Run("Azure AD", "Export", ConnectedSystemRunType.Export, ActivityStatus.FailedWithError, Ago(), 4, Schedule(nightly),
                a => { a.TotalExported = 17; a.TotalErrors = 1_208; a.ErrorMessage = "The service refused the connection: the credential has expired."; }),
            Run("LDAP Directory", "Full Import", ConnectedSystemRunType.FullImport, ActivityStatus.Cancelled, Ago(), 63, Person(),
                a => a.TotalAdded = 31_200),
            Run("HR System", "Full Sync", ConnectedSystemRunType.FullSynchronisation, ActivityStatus.Complete, Ago(), 0.1, SystemInitiated()),
            ConnectedSystemOperation(LongSystemName, ActivityTargetOperationType.Clear, ActivityStatus.Complete, Ago(), 2, Person(),
                a => a.TotalDeleted = 10_000),
            ConnectedSystemOperation("Retired Payroll", ActivityTargetOperationType.Delete, ActivityStatus.Complete, Ago(), 6, ApiKey()),
        ];
    }

    private static Activity Run(string system, string runProfile, ConnectedSystemRunType runType, ActivityStatus status, DateTime executed,
        double seconds, Action<Activity> initiator, Action<Activity>? outcomes = null)
    {
        var activity = new Activity
        {
            TargetType = ActivityTargetType.ConnectedSystemRunProfile,
            TargetOperationType = ActivityTargetOperationType.Execute,
            TargetName = runProfile,
            ConnectedSystemRunType = runType
        };
        return Finish(activity, system, status, executed, seconds, initiator, outcomes);
    }

    private static Activity ConnectedSystemOperation(string system, ActivityTargetOperationType operation, ActivityStatus status, DateTime executed,
        double seconds, Action<Activity> initiator, Action<Activity>? outcomes = null)
    {
        var activity = new Activity
        {
            TargetType = ActivityTargetType.ConnectedSystem,
            TargetOperationType = operation,
            TargetName = system
        };
        return Finish(activity, system, status, executed, seconds, initiator, outcomes);
    }

    private static Activity Finish(Activity activity, string system, ActivityStatus status, DateTime executed, double seconds,
        Action<Activity> initiator, Action<Activity>? outcomes)
    {
        activity.Id = Guid.NewGuid();
        activity.TargetContext = system;
        activity.Status = status;
        activity.Created = executed;
        activity.Executed = executed;
        activity.ExecutionTime = TimeSpan.FromSeconds(seconds);
        initiator(activity);
        outcomes?.Invoke(activity);
        return activity;
    }

    private static Action<Activity> ApiKey() => a =>
    {
        a.InitiatedByType = ActivityInitiatorType.ApiKey;
        a.InitiatedById = Guid.NewGuid();
        a.InitiatedByName = "Infrastructure Key";
    };

    private static Action<Activity> Person() => a =>
    {
        a.InitiatedByType = ActivityInitiatorType.User;
        a.InitiatedById = Guid.NewGuid();
        a.InitiatedByName = "Morgan Ellison-Whitfield";
    };

    private static Action<Activity> SystemInitiated() => a => a.InitiatedByType = ActivityInitiatorType.System;

    private static Action<Activity> Schedule(Guid executionId) => a =>
    {
        a.InitiatedByType = ActivityInitiatorType.System;
        a.ScheduledByScheduleName = "Nightly Full Sync";
        a.ScheduleExecutionId = executionId;
    };
}
