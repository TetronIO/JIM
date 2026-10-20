// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application;
using Serilog;

namespace JIM.Web.Scim;

/// <summary>
/// Flushes <see cref="ScimRequestTallyBuffer"/> onto the SCIM summary Activities every ten seconds, and once more as the
/// web tier stops, so a clean shutdown loses nothing. A crash loses at most the counts gathered since the last flush;
/// the writes they count are journalled transactionally regardless (decided 20 October 2026).
/// </summary>
/// <remarks>
/// Each flush resolves its <see cref="JimApplication"/> from a scope of its own, as
/// <c>FailedAuthenticationRecorder</c> does, so nothing it creates outlives the flush.
/// </remarks>
public class ScimRequestTallyFlusher(ScimRequestTallyBuffer buffer, IServiceScopeFactory scopeFactory) : BackgroundService
{
    /// <summary>
    /// How often counts are written.
    /// </summary>
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(10);

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
                await FlushAsync();
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Stopping: the final flush below catches what the last tick did not.
        }
    }

    /// <inheritdoc />
    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken);
        await FlushAsync();
    }

    /// <summary>
    /// Writes everything counted since the last flush. Never throws: these are counts for an administrator's view, and
    /// a flush that fails is logged rather than allowed to stop the next one.
    /// </summary>
    public async Task FlushAsync()
    {
        var tallies = buffer.Drain();
        if (tallies.Count == 0)
            return;

        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var jim = scope.ServiceProvider.GetRequiredService<JimApplication>();
            await jim.Scim.RecordRequestTalliesAsync(tallies);
            Log.Debug("ScimRequestTallyFlusher: Flushed {Count} SCIM request tallies", tallies.Count);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Error(ex, "ScimRequestTallyFlusher: Could not flush {Count} SCIM request tallies; their counts are lost", tallies.Count);
        }
    }
}
