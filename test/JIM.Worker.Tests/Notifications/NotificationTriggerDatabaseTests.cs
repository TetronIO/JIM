// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Tasking;
using JIM.PostgresData;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;
using NUnit.Framework;

namespace JIM.Worker.Tests.Notifications;

/// <summary>
/// Real-PostgreSQL verification of the real-time notification triggers (issue #307): Worker Task
/// inserts, status updates and deletes must raise NOTIFY on the Worker Task change channel, and Activity
/// progress updates must raise NOTIFY on the Activity progress channel. LISTEN/NOTIFY cannot be exercised
/// by the in-memory provider, and the triggers live in a raw-SQL migration, so only a real database
/// proves them.
/// </summary>
/// <remarks>
/// Opt-in via the same <c>JIM_TEST_RESET_*</c> environment variables as the other <c>RequiresPostgres</c>
/// fixtures; ignored when <c>JIM_TEST_RESET_DB</c> is absent. The target database must be a scratch
/// database, never a live one.
/// </remarks>
[TestFixture]
[Category("RequiresPostgres")]
public class NotificationTriggerDatabaseTests
{
    private static readonly TimeSpan NotificationTimeout = TimeSpan.FromSeconds(10);

    private string _connectionString = null!;

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        var dbName = Environment.GetEnvironmentVariable("JIM_TEST_RESET_DB");
        if (string.IsNullOrEmpty(dbName))
            Assert.Ignore("JIM_TEST_RESET_DB not set; skipping real-PostgreSQL notification trigger tests.");

        var host = Environment.GetEnvironmentVariable("JIM_TEST_RESET_HOST") ?? "localhost";
        var user = Environment.GetEnvironmentVariable("JIM_TEST_RESET_USER") ?? "postgres";
        var pass = Environment.GetEnvironmentVariable("JIM_TEST_RESET_PASSWORD") ?? "postgres";
        var port = Environment.GetEnvironmentVariable("JIM_TEST_RESET_PORT") ?? "5432";
        _connectionString = $"Host={host};Port={port};Database={dbName};Username={user};Password={pass}";

        TestUtilities.SetEnvironmentVariables();

        using var context = NewContext();
        context.Database.Migrate();
    }

    private JimDbContext NewContext()
    {
        var options = new DbContextOptionsBuilder<JimDbContext>()
            .UseNpgsql(_connectionString)
            .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning))
            .Options;
        return new JimDbContext(options);
    }

    private async Task<NpgsqlConnection> OpenListeningConnectionAsync(string channel, List<(string Channel, string Payload)> received)
    {
        var connection = new NpgsqlConnection(_connectionString + ";Pooling=false");
        connection.Notification += (_, args) => received.Add((args.Channel, args.Payload));
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand($"LISTEN \"{channel}\"", connection);
        await command.ExecuteNonQueryAsync();
        return connection;
    }

    private static async Task WaitForNotificationAsync(NpgsqlConnection connection, List<(string Channel, string Payload)> received)
    {
        using var cts = new CancellationTokenSource(NotificationTimeout);
        while (received.Count == 0)
            await connection.WaitAsync(cts.Token);
    }

    private static TemporalScopeReconciliationWorkerTask NewWorkerTask()
    {
        return new TemporalScopeReconciliationWorkerTask
        {
            InitiatedByType = ActivityInitiatorType.System,
            InitiatedByName = "Notification trigger test",
            Activity = new Activity
            {
                InitiatedByType = ActivityInitiatorType.System,
                InitiatedByName = "Notification trigger test",
                TargetType = ActivityTargetType.ConnectedSystemRunProfile
            }
        };
    }

    [Test]
    public async Task InsertWorkerTask_RaisesInsertNotificationAsync()
    {
        var received = new List<(string Channel, string Payload)>();
        await using var listeningConnection = await OpenListeningConnectionAsync(Constants.NotificationChannels.WorkerTaskChange, received);

        await using var context = NewContext();
        var workerTask = NewWorkerTask();
        context.WorkerTasks.Add(workerTask);
        await context.SaveChangesAsync();

        await WaitForNotificationAsync(listeningConnection, received);

        Assert.That(received, Has.Count.EqualTo(1));
        Assert.That(received[0].Channel, Is.EqualTo(Constants.NotificationChannels.WorkerTaskChange));
        Assert.That(WorkerTaskChangeNotification.TryParse(received[0].Payload, out var notification), Is.True);
        Assert.That(notification!.Operation, Is.EqualTo(WorkerTaskChangeOperation.Insert));
        Assert.That(notification!.TaskId, Is.EqualTo(workerTask.Id));
        Assert.That(notification!.ScheduleExecutionId, Is.Null);
        Assert.That(notification!.Status, Is.EqualTo(WorkerTaskStatus.Queued));
    }

    [Test]
    public async Task DeleteWorkerTask_RaisesDeleteNotificationAsync()
    {
        await using var context = NewContext();
        var workerTask = NewWorkerTask();
        context.WorkerTasks.Add(workerTask);
        await context.SaveChangesAsync();

        var received = new List<(string Channel, string Payload)>();
        await using var listeningConnection = await OpenListeningConnectionAsync(Constants.NotificationChannels.WorkerTaskChange, received);

        context.WorkerTasks.Remove(workerTask);
        await context.SaveChangesAsync();

        await WaitForNotificationAsync(listeningConnection, received);

        Assert.That(received, Has.Count.EqualTo(1));
        Assert.That(WorkerTaskChangeNotification.TryParse(received[0].Payload, out var notification), Is.True);
        Assert.That(notification!.Operation, Is.EqualTo(WorkerTaskChangeOperation.Delete));
        Assert.That(notification!.TaskId, Is.EqualTo(workerTask.Id));
    }

    [Test]
    public async Task UpdateWorkerTaskStatus_RaisesUpdateNotificationAsync()
    {
        await using var context = NewContext();
        var workerTask = NewWorkerTask();
        context.WorkerTasks.Add(workerTask);
        await context.SaveChangesAsync();

        var received = new List<(string Channel, string Payload)>();
        await using var listeningConnection = await OpenListeningConnectionAsync(Constants.NotificationChannels.WorkerTaskChange, received);

        workerTask.Status = WorkerTaskStatus.Processing;
        await context.SaveChangesAsync();

        await WaitForNotificationAsync(listeningConnection, received);

        Assert.That(received, Has.Count.EqualTo(1));
        Assert.That(WorkerTaskChangeNotification.TryParse(received[0].Payload, out var notification), Is.True);
        Assert.That(notification!.Operation, Is.EqualTo(WorkerTaskChangeOperation.Update));
        Assert.That(notification!.Status, Is.EqualTo(WorkerTaskStatus.Processing));
    }

    [Test]
    public async Task UpdateWorkerTaskHeartbeatOnly_RaisesNoNotificationAsync()
    {
        await using var context = NewContext();
        var workerTask = NewWorkerTask();
        context.WorkerTasks.Add(workerTask);
        await context.SaveChangesAsync();

        var received = new List<(string Channel, string Payload)>();
        await using var listeningConnection = await OpenListeningConnectionAsync(Constants.NotificationChannels.WorkerTaskChange, received);

        workerTask.LastHeartbeat = DateTime.UtcNow;
        await context.SaveChangesAsync();

        // give any (unexpected) notification time to arrive before asserting silence.
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        try
        {
            await listeningConnection.WaitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            // expected: no notification within the window.
        }

        Assert.That(received, Is.Empty);
    }

    [Test]
    public async Task UpdateActivityProgress_RaisesActivityProgressNotificationAsync()
    {
        await using var context = NewContext();
        var activity = new Activity
        {
            InitiatedByType = ActivityInitiatorType.System,
            InitiatedByName = "Notification trigger test",
            TargetType = ActivityTargetType.ConnectedSystemRunProfile
        };
        context.Activities.Add(activity);
        await context.SaveChangesAsync();

        var received = new List<(string Channel, string Payload)>();
        await using var listeningConnection = await OpenListeningConnectionAsync(Constants.NotificationChannels.ActivityProgress, received);

        activity.ObjectsProcessed = 42;
        activity.Message = "Processing objects";
        await context.SaveChangesAsync();

        await WaitForNotificationAsync(listeningConnection, received);

        Assert.That(received, Has.Count.EqualTo(1));
        Assert.That(received[0].Channel, Is.EqualTo(Constants.NotificationChannels.ActivityProgress));
        Assert.That(Guid.TryParse(received[0].Payload, out var activityId), Is.True);
        Assert.That(activityId, Is.EqualTo(activity.Id));
    }

    [Test]
    public async Task PostgresNotificationListener_ReceivesWorkerTaskInsertEndToEndAsync()
    {
        var received = new List<(string Channel, string Payload)>();
        var listener = new PostgresNotificationListener(_connectionString + ";Pooling=false");
        using var cts = new CancellationTokenSource(NotificationTimeout);
        var connected = new TaskCompletionSource();
        listener.ConnectionStateChanged += isConnected =>
        {
            if (isConnected)
                connected.TrySetResult();
        };

        var listenTask = listener.ListenAsync(
            [Constants.NotificationChannels.WorkerTaskChange],
            (channel, payload, _) =>
            {
                received.Add((channel, payload));
                cts.Cancel();
                return Task.CompletedTask;
            },
            cts.Token);

        await connected.Task.WaitAsync(cts.Token);

        await using var context = NewContext();
        var workerTask = NewWorkerTask();
        context.WorkerTasks.Add(workerTask);
        await context.SaveChangesAsync();

        await listenTask;

        Assert.That(received, Has.Count.EqualTo(1));
        Assert.That(received[0].Channel, Is.EqualTo(Constants.NotificationChannels.WorkerTaskChange));
        Assert.That(WorkerTaskChangeNotification.TryParse(received[0].Payload, out var notification), Is.True);
        Assert.That(notification!.TaskId, Is.EqualTo(workerTask.Id));
    }
}
