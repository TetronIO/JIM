// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application.Interfaces;
using JIM.Models.Preview;
using System.Collections.Concurrent;
using System.Threading.Channels;

namespace JIM.Web.Services;

/// <summary>
/// Runs small configuration change previews inside JIM.Web (#827), so the common case (a proposal affecting a
/// handful of objects) returns without waiting for JIM.Worker to poll for it.
///
/// Deliberately not a general-purpose job runner. It holds no state a restart could lose that matters: a preview
/// interrupted by a shutdown leaves its Activity in progress and is recovered like any other abandoned Activity,
/// and the administrator can simply ask again. Anything valuable enough to survive a restart is, by definition,
/// large enough to belong in JIM.Worker, which is where the threshold sends it.
/// </summary>
public class ConfigurationChangePreviewBackgroundRunner : BackgroundService, IConfigurationChangePreviewBackgroundRunner
{
    /// <summary>
    /// How many previews may evaluate at once in this process. Small on purpose: JIM.Web's job is serving requests,
    /// and a preview that wants more concurrency than this is a preview that should have gone to the worker.
    /// </summary>
    private const int MaximumConcurrentPreviews = 2;

    private readonly IJimApplicationFactory _applicationFactory;
    private readonly ILogger<ConfigurationChangePreviewBackgroundRunner> _logger;
    private readonly Channel<QueuedPreview> _queue = Channel.CreateUnbounded<QueuedPreview>(
        new UnboundedChannelOptions { SingleReader = false, SingleWriter = false });

    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _running = new();

    public ConfigurationChangePreviewBackgroundRunner(IJimApplicationFactory applicationFactory,
        ILogger<ConfigurationChangePreviewBackgroundRunner> logger)
    {
        _applicationFactory = applicationFactory;
        _logger = logger;
    }

    public void Enqueue(Guid activityId, ConfigurationChangePreviewRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!_queue.Writer.TryWrite(new QueuedPreview(activityId, request)))
        {
            // An unbounded channel only refuses writes once it is completed, which happens at shutdown.
            throw new InvalidOperationException(
                "Configuration change previews are no longer being accepted in this process because it is shutting down.");
        }
    }

    public bool Cancel(Guid activityId)
    {
        if (!_running.TryGetValue(activityId, out var cancellation))
            return false;

        cancellation.Cancel();
        return true;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var workers = Enumerable.Range(0, MaximumConcurrentPreviews)
            .Select(_ => ConsumeAsync(stoppingToken))
            .ToArray();

        await Task.WhenAll(workers);
    }

    private async Task ConsumeAsync(CancellationToken stoppingToken)
    {
        await foreach (var queued in _queue.Reader.ReadAllAsync(stoppingToken))
            await RunAsync(queued, stoppingToken);
    }

    private async Task RunAsync(QueuedPreview queued, CancellationToken stoppingToken)
    {
        // Linked so both a shutdown and an administrator's cancellation stop the evaluation; the preview server
        // records the difference, because one is the host going away and the other is a decision.
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        _running[queued.ActivityId] = cancellation;

        // Its own JimApplication, and therefore its own DbContext: the request that started this preview has long
        // since finished, and with it the scope whose context was serving it.
        using var jim = _applicationFactory.Create();

        try
        {
            await jim.ConfigurationChangePreviews.RunPreviewAsync(queued.ActivityId, queued.Request, cancellation.Token);
        }
        catch (OperationCanceledException)
        {
            // A shutdown, or an administrator cancelling. The preview server records cancellation on the preview
            // and its Activity wherever it can still reach the database; reaching here means it could not, which
            // the stale-Activity recovery already covers. Logging it as an error would send somebody looking for
            // a fault that never happened.
            _logger.LogDebug("A configuration change preview for Activity {ActivityId} stopped because it was cancelled",
                queued.ActivityId);
        }
        catch (Exception ex)
        {
            // Sanctioned broad catch (see src/CLAUDE.md, Activity execution boundaries). The preview server records
            // its own stage failures; what reaches here is a failure to run at all, and letting it escape would
            // take down a channel consumer and silently halve this process's preview capacity.
            _logger.LogError(ex, "A configuration change preview failed to run in-process for Activity {ActivityId}", queued.ActivityId);
        }
        finally
        {
            _running.TryRemove(queued.ActivityId, out _);
        }
    }

    private record QueuedPreview(Guid ActivityId, ConfigurationChangePreviewRequest Request);
}
