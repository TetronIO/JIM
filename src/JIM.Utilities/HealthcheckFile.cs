// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Utilities;

/// <summary>
/// The heartbeat file the Worker's and Scheduler's container health checks read: a check fails once the file is
/// older than its service's threshold, so each service touches it from every loop that proves it is alive, including
/// while it waits or works through start-up.
/// </summary>
public static class HealthcheckFile
{
    /// <summary>Where the container health checks in <c>docker-compose.yml</c> and the Podman pod file look.</summary>
    public const string DefaultPath = "/tmp/healthcheck";

    /// <summary>
    /// How often <see cref="KeepFreshWhileAsync(Func{Task})"/> touches the file: well inside the shortest threshold,
    /// the Worker's 60 seconds.
    /// </summary>
    public static readonly TimeSpan StartupTouchInterval = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Writes the current time to the heartbeat file. A file-system failure is returned rather than thrown: a
    /// heartbeat that could not be written is for the health check to report, not a reason to stop the loop that
    /// writes it.
    /// </summary>
    /// <returns>Whether the file was written.</returns>
    public static Task<bool> TouchAsync() => TouchAsync(DefaultPath);

    /// <inheritdoc cref="TouchAsync()"/>
    /// <param name="path">The file to write; tests point it somewhere writable.</param>
    public static async Task<bool> TouchAsync(string path)
    {
        try
        {
            await File.WriteAllTextAsync(path, DateTime.UtcNow.ToString("O"));
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// Runs a long start-up step, such as database migrations or warming a cache, while touching the heartbeat file
    /// every <see cref="StartupTouchInterval"/>, so that a health check does not judge a service that is busy starting
    /// to be hung. Podman restarts a container whose liveness check fails, which would kill the step part-way.
    /// </summary>
    public static Task KeepFreshWhileAsync(Func<Task> work) => KeepFreshWhileAsync(work, StartupTouchInterval, TouchAsync);

    /// <inheritdoc cref="KeepFreshWhileAsync(Func{Task})"/>
    /// <param name="work">The start-up step. Its failure is rethrown once the touching has stopped.</param>
    /// <param name="interval">How often to touch the file.</param>
    /// <param name="touch">Writes the heartbeat; tests count the calls.</param>
    public static async Task KeepFreshWhileAsync(Func<Task> work, TimeSpan interval, Func<Task<bool>> touch)
    {
        using var stop = new CancellationTokenSource();
        var touching = TouchUntilStoppedAsync(interval, touch, stop.Token);
        try
        {
            await work();
        }
        finally
        {
            // Stop before returning, so that from here on only the service's own loop proves it alive.
            await stop.CancelAsync();
            await touching;
        }
    }

    private static async Task TouchUntilStoppedAsync(TimeSpan interval, Func<Task<bool>> touch, CancellationToken stop)
    {
        try
        {
            while (!stop.IsCancellationRequested)
            {
                await touch();
                await Task.Delay(interval, stop);
            }
        }
        catch (OperationCanceledException)
        {
            // Stopped between touches: the step is over.
        }
    }
}
