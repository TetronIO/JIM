// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

namespace JIM.Utilities;

/// <summary>
/// The heartbeat file the Worker's and Scheduler's container health checks read: a check fails once the file is
/// older than its service's threshold, so each service touches it from every loop that proves it is alive, including
/// while it waits at start-up.
/// </summary>
public static class HealthcheckFile
{
    /// <summary>Where the container health checks in <c>docker-compose.yml</c> look.</summary>
    public const string DefaultPath = "/tmp/healthcheck";

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
}
