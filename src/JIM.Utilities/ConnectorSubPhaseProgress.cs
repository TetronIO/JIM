// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using Serilog;

namespace JIM.Utilities;

/// <summary>
/// Wraps the delegate that narrates a connector's internal sub-phases (i.e. "Loading existing export file...",
/// "Querying root DSE...") so that it is safe to hand to a connector:
/// <list type="bullet">
/// <item>emits are serialised, because a connector may report from parallel internal work and the delegate
/// typically writes the Activity message through a shared DbContext;</item>
/// <item>a reporting failure is logged and swallowed rather than failing the synchronisation operation,
/// because progress narration is cosmetic;</item>
/// <item>blank messages are ignored, so a connector cannot accidentally clear the Activity message.</item>
/// </list>
/// Cancellation is deliberately not swallowed: a cancelled run must keep unwinding.
/// </summary>
public sealed class ConnectorSubPhaseProgress : IDisposable
{
    private readonly Func<string, Task>? _report;
    private readonly SemaphoreSlim? _gate;
    private readonly bool _ownsGate;
    private readonly ILogger _logger;
    private bool _disposed;

    /// <param name="report">The caller's progress delegate, or null when the caller does not want progress reported.</param>
    /// <param name="logger">Optional logger for reporting failures; defaults to the global Serilog logger.</param>
    /// <param name="sharedGate">Optional semaphore owned by the caller. Pass the caller's own progress gate when the
    /// caller also reports progress concurrently, so that both kinds of emit serialise against each other. When
    /// supplied, the gate is not disposed with this instance.</param>
    public ConnectorSubPhaseProgress(Func<string, Task>? report, ILogger? logger = null, SemaphoreSlim? sharedGate = null)
    {
        _report = report;
        _logger = logger ?? Log.ForContext<ConnectorSubPhaseProgress>();

        if (report == null)
            return;

        _gate = sharedGate ?? new SemaphoreSlim(1, 1);
        _ownsGate = sharedGate == null;
    }

    /// <summary>
    /// The delegate to hand to the connector, or null when the caller supplied no progress delegate.
    /// Connectors treat null as "progress reporting is not wanted" and skip building their messages.
    /// </summary>
    public Func<string, Task>? Callback => _report == null ? null : ReportAsync;

    private async Task ReportAsync(string subPhase)
    {
        if (_report == null || _gate == null || string.IsNullOrWhiteSpace(subPhase))
            return;

        // No cancellation token: a progress emit is not a cancellation point, and the report delegate
        // itself surfaces cancellation if the run is aborting.
        await _gate.WaitAsync();
        try
        {
            await _report(subPhase);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.Warning(ex, "ConnectorSubPhaseProgress: Failed to report connector sub-phase progress. The operation continues");
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
