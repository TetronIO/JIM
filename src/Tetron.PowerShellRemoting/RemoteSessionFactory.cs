// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Management.Automation.Runspaces;
using Tetron.PowerShellRemoting.Transport;

namespace Tetron.PowerShellRemoting;

/// <summary>Opens remote PowerShell runspaces over this library's WinRM transport.</summary>
public static class RemoteSessionFactory
{
    /// <summary>
    /// Opens a remote runspace on <see cref="RemoteSessionOptions.ConnectionUri"/>. Run commands with
    /// <c>PowerShell.Create(runspace).AddCommand(...).AddParameter(...)</c>: commands and bound parameters only, which is
    /// also all a restricted endpoint such as Exchange's accepts. Only remote runspaces are ever created; nothing
    /// runs in this process.
    /// </summary>
    /// <exception cref="Exception">The endpoint could not be reached, refused authentication, or failed to open the shell.</exception>
    public static async Task<Runspace> OpenAsync(RemoteSessionOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        cancellationToken.ThrowIfCancellationRequested();

        var runspace = RunspaceFactory.CreateRunspace(new WinRMConnectionInfo(options));
        try
        {
            using var registration = cancellationToken.Register(() => runspace.CloseAsync());
            await Task.Factory.FromAsync(
                (callback, state) => BeginOpen(runspace, callback),
                _ => { },
                null).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (runspace.RunspaceStateInfo.State != RunspaceState.Opened)
                throw runspace.RunspaceStateInfo.Reason ?? new InvalidOperationException($"The remote runspace is {runspace.RunspaceStateInfo.State}.");
            return runspace;
        }
        catch
        {
            runspace.Dispose();
            throw;
        }
    }

    /// <summary>What a runspace this factory opened has cost so far: logins, connections and commands.</summary>
    /// <exception cref="ArgumentException">The runspace was not opened by <see cref="OpenAsync"/>.</exception>
    public static RemoteSessionStatistics GetStatistics(Runspace runspace)
    {
        ArgumentNullException.ThrowIfNull(runspace);
        return (runspace.ConnectionInfo as WinRMConnectionInfo)?.Statistics
            ?? throw new ArgumentException("The runspace was not opened by RemoteSessionFactory.OpenAsync.", nameof(runspace));
    }

    // Runspace.OpenAsync raises StateChanged rather than completing an IAsyncResult, so the open is bridged to a Task.
    private static IAsyncResult BeginOpen(Runspace runspace, AsyncCallback callback)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        runspace.StateChanged += (_, e) =>
        {
            if (e.RunspaceStateInfo.State is RunspaceState.Opened or RunspaceState.Broken or RunspaceState.Closed)
                completion.TrySetResult();
        };
        runspace.OpenAsync();
        completion.Task.ContinueWith(t => callback(t), TaskScheduler.Default);
        return completion.Task;
    }
}
