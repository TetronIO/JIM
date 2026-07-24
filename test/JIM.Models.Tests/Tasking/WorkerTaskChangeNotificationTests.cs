// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System;
using JIM.Models.Tasking;
using NUnit.Framework;

namespace JIM.Models.Tests.Tasking;

[TestFixture]
public class WorkerTaskChangeNotificationTests
{
    [Test]
    public void TryParse_ValidInsertPayload_ReturnsNotification()
    {
        var taskId = Guid.NewGuid();
        var scheduleExecutionId = Guid.NewGuid();
        var json = $"{{\"op\":\"INSERT\",\"taskId\":\"{taskId}\",\"scheduleExecutionId\":\"{scheduleExecutionId}\",\"status\":0}}";

        var result = WorkerTaskChangeNotification.TryParse(json, out var notification);

        Assert.That(result, Is.True);
        Assert.That(notification, Is.Not.Null);
        Assert.That(notification!.Operation, Is.EqualTo(WorkerTaskChangeOperation.Insert));
        Assert.That(notification!.TaskId, Is.EqualTo(taskId));
        Assert.That(notification!.ScheduleExecutionId, Is.EqualTo(scheduleExecutionId));
        Assert.That(notification!.Status, Is.EqualTo(WorkerTaskStatus.Queued));
    }

    [Test]
    public void TryParse_ValidDeletePayloadWithNullScheduleExecutionId_ReturnsNotification()
    {
        var taskId = Guid.NewGuid();
        var json = $"{{\"op\":\"DELETE\",\"taskId\":\"{taskId}\",\"scheduleExecutionId\":null,\"status\":1}}";

        var result = WorkerTaskChangeNotification.TryParse(json, out var notification);

        Assert.That(result, Is.True);
        Assert.That(notification, Is.Not.Null);
        Assert.That(notification!.Operation, Is.EqualTo(WorkerTaskChangeOperation.Delete));
        Assert.That(notification!.TaskId, Is.EqualTo(taskId));
        Assert.That(notification!.ScheduleExecutionId, Is.Null);
        Assert.That(notification!.Status, Is.EqualTo(WorkerTaskStatus.Processing));
    }

    [Test]
    public void TryParse_ValidUpdatePayload_ReturnsNotification()
    {
        var taskId = Guid.NewGuid();
        var json = $"{{\"op\":\"UPDATE\",\"taskId\":\"{taskId}\",\"scheduleExecutionId\":null,\"status\":2}}";

        var result = WorkerTaskChangeNotification.TryParse(json, out var notification);

        Assert.That(result, Is.True);
        Assert.That(notification, Is.Not.Null);
        Assert.That(notification!.Operation, Is.EqualTo(WorkerTaskChangeOperation.Update));
        Assert.That(notification!.Status, Is.EqualTo(WorkerTaskStatus.CancellationRequested));
    }

    [Test]
    public void TryParse_UnknownOperation_ReturnsFalse()
    {
        var json = $"{{\"op\":\"TRUNCATE\",\"taskId\":\"{Guid.NewGuid()}\",\"scheduleExecutionId\":null,\"status\":0}}";

        var result = WorkerTaskChangeNotification.TryParse(json, out var notification);

        Assert.That(result, Is.False);
        Assert.That(notification, Is.Null);
    }

    [Test]
    public void TryParse_MissingTaskId_ReturnsFalse()
    {
        var json = "{\"op\":\"INSERT\",\"scheduleExecutionId\":null,\"status\":0}";

        var result = WorkerTaskChangeNotification.TryParse(json, out var notification);

        Assert.That(result, Is.False);
        Assert.That(notification, Is.Null);
    }

    [Test]
    public void TryParse_MalformedJson_ReturnsFalse()
    {
        var result = WorkerTaskChangeNotification.TryParse("not json at all", out var notification);

        Assert.That(result, Is.False);
        Assert.That(notification, Is.Null);
    }

    [Test]
    public void TryParse_EmptyString_ReturnsFalse()
    {
        var result = WorkerTaskChangeNotification.TryParse(string.Empty, out var notification);

        Assert.That(result, Is.False);
        Assert.That(notification, Is.Null);
    }

    [Test]
    public void TryParse_MissingStatus_ReturnsNotificationWithNullStatus()
    {
        var taskId = Guid.NewGuid();
        var json = $"{{\"op\":\"DELETE\",\"taskId\":\"{taskId}\",\"scheduleExecutionId\":null}}";

        var result = WorkerTaskChangeNotification.TryParse(json, out var notification);

        Assert.That(result, Is.True);
        Assert.That(notification, Is.Not.Null);
        Assert.That(notification!.Status, Is.Null);
    }
}
