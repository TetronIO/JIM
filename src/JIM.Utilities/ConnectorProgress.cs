// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Models.Interfaces;
using Serilog;

namespace JIM.Utilities;

/// <summary>
/// JIM's side of <see cref="IConnectorProgress"/>: what the server and worker hand to a Connector
/// so it can narrate its internal work, and move between the phases it declared.
/// </summary>
/// <remarks>
/// The guarantees the interface makes to Connector authors are kept here:
/// <list type="bullet">
/// <item>emits are serialised, because a Connector may report from parallel internal work and the
/// delegates typically write through a shared DbContext;</item>
/// <item>a reporting failure is logged and swallowed rather than failing the synchronisation
/// operation, because narration is cosmetic;</item>
/// <item>blank messages are ignored, so a Connector cannot accidentally clear the Activity message;</item>
/// <item>object counts are reported on the same terms, because a lost figure is a cosmetic loss too.</item>
/// </list>
/// Cancellation is deliberately not swallowed: a cancelled run must keep unwinding.
/// </remarks>
public sealed class ConnectorProgress : IConnectorProgress, IDisposable
{
    private readonly Func<string, Task>? _report;
    private readonly Func<string, string?, Task>? _enterPhase;
    private readonly Func<int, Task>? _reportExpectedObjectCount;
    private readonly Func<int, Task>? _reportObjectsProduced;
    private readonly SemaphoreSlim? _gate;
    private readonly bool _ownsGate;
    private readonly ILogger _logger;
    private bool _disposed;

    /// <param name="report">Records a narration message against the Activity, or null when the caller does not want progress reported.</param>
    /// <param name="enterPhase">
    /// Moves the Activity to the Connector's declared phase (key, then optional message), or null
    /// when the caller does not track phases. When null, a phase change is still narrated through
    /// <paramref name="report"/> if a message was supplied, so nothing is lost.
    /// </param>
    /// <param name="logger">Optional logger for reporting failures; defaults to the global Serilog logger.</param>
    /// <param name="sharedGate">
    /// Optional semaphore owned by the caller. Pass the caller's own progress gate when the caller
    /// also reports progress concurrently, so that both kinds of emit serialise against each other.
    /// When supplied, the gate is not disposed with this instance.
    /// </param>
    public ConnectorProgress(
        Func<string, Task>? report,
        Func<string, string?, Task>? enterPhase = null,
        ILogger? logger = null,
        SemaphoreSlim? sharedGate = null,
        Func<int, Task>? reportExpectedObjectCount = null,
        Func<int, Task>? reportObjectsProduced = null)
    {
        _report = report;
        _enterPhase = enterPhase;
        _reportExpectedObjectCount = reportExpectedObjectCount;
        _reportObjectsProduced = reportObjectsProduced;
        _logger = logger ?? Log.ForContext<ConnectorProgress>();

        if (report == null && enterPhase == null && reportExpectedObjectCount == null && reportObjectsProduced == null)
            return;

        _gate = sharedGate ?? new SemaphoreSlim(1, 1);
        _ownsGate = sharedGate == null;
    }

    /// <summary>
    /// A progress reporter that records nothing. For callers with no Activity to report against
    /// (tooling, tests), so that a Connector never has to check whether anybody is listening.
    /// </summary>
    public static IConnectorProgress None { get; } = new ConnectorProgress(report: null);

    /// <inheritdoc />
    public Task EnterPhaseAsync(string phaseKey, string? message = null)
    {
        if (string.IsNullOrWhiteSpace(phaseKey))
            return Task.CompletedTask;

        if (_enterPhase != null)
            return GuardAsync(() => _enterPhase(phaseKey, message));

        // No phase tracking on this path: the narration is still worth showing.
        return message == null ? Task.CompletedTask : ReportAsync(message);
    }

    /// <inheritdoc />
    public Task ReportAsync(string message)
    {
        if (_report == null || string.IsNullOrWhiteSpace(message))
            return Task.CompletedTask;

        return GuardAsync(() => _report(message));
    }

    /// <inheritdoc />
    public Task ReportExpectedObjectCountAsync(int objectCount) =>
        _reportExpectedObjectCount == null ? Task.CompletedTask : GuardAsync(() => _reportExpectedObjectCount(objectCount));

    /// <inheritdoc />
    public Task ReportObjectsProducedAsync(int objectCount) =>
        _reportObjectsProduced == null ? Task.CompletedTask : GuardAsync(() => _reportObjectsProduced(objectCount));

    private async Task GuardAsync(Func<Task> emit)
    {
        if (_gate == null)
            return;

        // No cancellation token: a progress emit is not a cancellation point, and the delegates
        // themselves surface cancellation if the run is aborting.
        await _gate.WaitAsync();
        try
        {
            await emit();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.Warning(ex, "ConnectorProgress: Failed to report Connector progress. The operation continues");
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        if (_ownsGate)
            _gate?.Dispose();
    }
}
