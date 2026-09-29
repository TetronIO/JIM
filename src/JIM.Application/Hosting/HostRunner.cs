// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace JIM.Application.Hosting;

/// <summary>
/// Runs a JIM host and reports how it ended as the process exit code. A background service that fails stops its
/// host (the default <see cref="BackgroundServiceExceptionBehavior.StopHost"/>), but the host itself returns
/// normally, so without this the process exits 0 straight after the service has logged a fatal error, and a
/// container runtime, systemd or a monitoring system sees a clean stop.
/// </summary>
public static class HostRunner
{
    /// <summary>
    /// Runs <paramref name="host"/> until it stops, and returns the exit code the process should end with: 1 when a
    /// background service failed, 0 otherwise. A service that ends because shutdown cancelled it has not failed.
    /// </summary>
    public static async Task<int> RunAsync(IHost host)
    {
        // Judged once the host has stopped: every service has ended by then, and the service provider (disposed as
        // the host finishes) can still be asked for them. Asking before the host starts would construct the hosted
        // services earlier than the host itself would.
        var failed = false;
        var lifetime = host.Services.GetRequiredService<IHostApplicationLifetime>();
        lifetime.ApplicationStopped.Register(() => failed = host.Services.GetServices<IHostedService>()
            .OfType<BackgroundService>()
            .Any(s => s.ExecuteTask is { IsFaulted: true }));

        await host.RunAsync();

        return failed ? 1 : 0;
    }
}
