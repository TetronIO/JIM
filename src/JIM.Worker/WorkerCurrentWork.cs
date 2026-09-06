// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Text;
using JIM.Models.Tasking;

namespace JIM.Worker;

/// <summary>
/// Puts the Worker's in-flight tasks into the words its heartbeat reports as CurrentWork: what an administrator
/// reads on the Operations page while the Worker is busy. Descriptions are captured once per task at dispatch
/// (<see cref="TaskTask.Description"/>) from the Activity the dispatcher already holds, so the heartbeat costs no
/// extra reads.
/// </summary>
internal static class WorkerCurrentWork
{
    /// <summary>
    /// The CurrentWork column's length; a longer description is cut to fit rather than failing the write.
    /// </summary>
    internal const int MaxLength = 500;

    private const string Separator = "; ";
    private const string Ellipsis = "...";

    /// <summary>
    /// One task, in an administrator's words. A synchronisation task reads as its Run Profile and Connected System
    /// ("Full Import: Corporate Directory"); anything else as its kind and target ("Synchronisation Rule deletion:
    /// Users Inbound (Corporate Directory)"), or just its kind when the target adds nothing.
    /// </summary>
    internal static string DescribeTask(WorkerTask task)
    {
        var kind = KindOf(task);
        var targetName = task.Activity?.TargetName;
        var targetContext = task.Activity?.TargetContext;

        if (task is SynchronisationWorkerTask)
        {
            // TaskingServer records the Run Profile name as the target and the Connected System as its context.
            if (string.IsNullOrWhiteSpace(targetName))
                return kind;
            return string.IsNullOrWhiteSpace(targetContext) ? targetName : $"{targetName}: {targetContext}";
        }

        if (string.IsNullOrWhiteSpace(targetName) || string.Equals(targetName, kind, StringComparison.OrdinalIgnoreCase))
            return kind;

        return string.IsNullOrWhiteSpace(targetContext) ? $"{kind}: {targetName}" : $"{kind}: {targetName} ({targetContext})";
    }

    /// <summary>
    /// The in-flight tasks as one CurrentWork string (null when idle) and the earliest dispatch time among them
    /// (null when idle).
    /// </summary>
    internal static (string? CurrentWork, DateTime? StartedAt) Describe(IReadOnlyCollection<TaskTask> tasks)
    {
        if (tasks.Count == 0)
            return (null, null);

        var builder = new StringBuilder();
        foreach (var task in tasks)
        {
            if (builder.Length > 0)
                builder.Append(Separator);
            builder.Append(string.IsNullOrWhiteSpace(task.Description) ? "Worker Task" : task.Description);
        }

        if (builder.Length > MaxLength)
        {
            builder.Length = MaxLength - Ellipsis.Length;
            builder.Append(Ellipsis);
        }

        return (builder.ToString(), tasks.Min(t => t.StartedAt));
    }

    private static string KindOf(WorkerTask task) => task switch
    {
        SynchronisationWorkerTask => "Synchronisation",
        ExampleDataTemplateWorkerTask => "Example data generation",
        DeleteConnectedSystemWorkerTask => "Connected System deletion",
        DeleteSyncRuleWorkerTask => "Synchronisation Rule deletion",
        SchemaRefreshRemovalWorkerTask => "Schema refresh",
        ClearConnectedSystemObjectsWorkerTask => "Connector Space clear",
        PasswordDeliveryWorkerTask => "Password delivery",
        ConfigurationChangePreviewWorkerTask => "Configuration change preview",
        TemporalScopeReconciliationWorkerTask => "Temporal scope reconciliation",
        HistoryRetentionCleanupWorkerTask => "History retention cleanup",
        // A task type added without a line above still reads as words rather than a class name.
        _ => WordsFromTypeName(task.GetType().Name)
    };

    /// <summary>
    /// "AuxiliaryClassDiscoveryWorkerTask" becomes "Auxiliary class discovery".
    /// </summary>
    private static string WordsFromTypeName(string typeName)
    {
        foreach (var suffix in new[] { "WorkerTask", "Task" })
        {
            if (typeName.EndsWith(suffix, StringComparison.Ordinal) && typeName.Length > suffix.Length)
            {
                typeName = typeName[..^suffix.Length];
                break;
            }
        }

        var words = new StringBuilder();
        for (var i = 0; i < typeName.Length; i++)
        {
            var c = typeName[i];
            if (i > 0 && char.IsUpper(c))
            {
                words.Append(' ');
                words.Append(char.ToLowerInvariant(c));
            }
            else
            {
                words.Append(c);
            }
        }

        return words.ToString();
    }
}
