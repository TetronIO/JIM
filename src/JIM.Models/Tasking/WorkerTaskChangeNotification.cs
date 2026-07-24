// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Text.Json;

namespace JIM.Models.Tasking;

/// <summary>
/// The payload of a real-time Worker Task change notification, published by the database trigger on the
/// WorkerTasks table via PostgreSQL NOTIFY (issue #307). Payloads carry identifiers only; consumers must
/// re-query the database for current state, as notifications are fire-and-forget hints, not the data itself.
/// </summary>
public class WorkerTaskChangeNotification
{
    /// <summary>
    /// The database operation that raised the notification. A Delete indicates the Worker Task reached a
    /// terminal state (completed or cancelled), as JIM removes Worker Tasks on completion; the surviving
    /// Activity record carries the outcome.
    /// </summary>
    public WorkerTaskChangeOperation Operation { get; private init; }

    /// <summary>
    /// The id of the Worker Task the notification relates to.
    /// </summary>
    public Guid TaskId { get; private init; }

    /// <summary>
    /// The Schedule Execution the Worker Task belongs to, when it was queued by a Schedule.
    /// </summary>
    public Guid? ScheduleExecutionId { get; private init; }

    /// <summary>
    /// The status of the Worker Task at the time the notification was raised (the new status for inserts
    /// and updates, the last status for deletes). Null when the payload did not include a status.
    /// </summary>
    public WorkerTaskStatus? Status { get; private init; }

    /// <summary>
    /// Attempts to parse a notification payload produced by the WorkerTasks database trigger.
    /// Returns false for malformed payloads rather than throwing; notification handling must never
    /// take down a listener loop.
    /// </summary>
    public static bool TryParse(string? payload, out WorkerTaskChangeNotification? notification)
    {
        notification = null;
        if (string.IsNullOrWhiteSpace(payload))
            return false;

        try
        {
            using var document = JsonDocument.Parse(payload);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return false;

            if (!root.TryGetProperty("op", out var opElement) || opElement.ValueKind != JsonValueKind.String)
                return false;

            WorkerTaskChangeOperation operation;
            switch (opElement.GetString())
            {
                case "INSERT":
                    operation = WorkerTaskChangeOperation.Insert;
                    break;
                case "UPDATE":
                    operation = WorkerTaskChangeOperation.Update;
                    break;
                case "DELETE":
                    operation = WorkerTaskChangeOperation.Delete;
                    break;
                default:
                    return false;
            }

            if (!root.TryGetProperty("taskId", out var taskIdElement) || !taskIdElement.TryGetGuid(out var taskId))
                return false;

            Guid? scheduleExecutionId = null;
            if (root.TryGetProperty("scheduleExecutionId", out var scheduleExecutionIdElement) &&
                scheduleExecutionIdElement.ValueKind == JsonValueKind.String &&
                scheduleExecutionIdElement.TryGetGuid(out var parsedScheduleExecutionId))
                scheduleExecutionId = parsedScheduleExecutionId;

            WorkerTaskStatus? status = null;
            if (root.TryGetProperty("status", out var statusElement) &&
                statusElement.ValueKind == JsonValueKind.Number &&
                statusElement.TryGetInt32(out var statusValue) &&
                Enum.IsDefined(typeof(WorkerTaskStatus), statusValue))
                status = (WorkerTaskStatus)statusValue;

            notification = new WorkerTaskChangeNotification
            {
                Operation = operation,
                TaskId = taskId,
                ScheduleExecutionId = scheduleExecutionId,
                Status = status
            };
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
