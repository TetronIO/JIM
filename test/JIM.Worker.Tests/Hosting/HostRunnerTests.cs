// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace JIM.Worker.Tests.Hosting;

/// <summary>
/// How a host's end reaches the process exit code. A background service that fails stops the host (the default
/// <see cref="BackgroundServiceExceptionBehavior.StopHost"/>), but the host itself returns normally, so without
/// <see cref="HostRunner"/> the process exited 0 and a container runtime, systemd or a monitoring system saw a clean
/// stop straight after the service had logged a fatal error.
/// </summary>
[TestFixture]
public class HostRunnerTests
{
    private static IHost BuildHost<TService>() where TService : class, IHostedService
    {
        var builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings());
        builder.Services.AddHostedService<TService>();
        return builder.Build();
    }

    [Test]
    public async Task RunAsync_BackgroundServiceFails_ReturnsNonZeroAsync()
    {
        using var host = BuildHost<FailingService>();

        var exitCode = await HostRunner.RunAsync(host);

        Assert.That(exitCode, Is.EqualTo(1));
    }

    [Test]
    public async Task RunAsync_BackgroundServiceStopsTheHostCleanly_ReturnsZeroAsync()
    {
        using var host = BuildHost<StoppingService>();

        var exitCode = await HostRunner.RunAsync(host);

        Assert.That(exitCode, Is.EqualTo(0));
    }

    [Test]
    public async Task RunAsync_ShutdownCancelsABackgroundService_ReturnsZeroAsync()
    {
        // An ordinary shutdown cancels every service's stopping token; a service ending on that is not a failure.
        using var host = BuildHost<WaitingService>();
        var lifetime = host.Services.GetRequiredService<IHostApplicationLifetime>();
        lifetime.ApplicationStarted.Register(lifetime.StopApplication);

        var exitCode = await HostRunner.RunAsync(host);

        Assert.That(exitCode, Is.EqualTo(0));
    }

    private sealed class FailingService : BackgroundService
    {
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            await Task.Yield();
            throw new InvalidOperationException("The service could not start.");
        }
    }

    private sealed class StoppingService(IHostApplicationLifetime lifetime) : BackgroundService
    {
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            await Task.Yield();
            lifetime.StopApplication();
        }
    }

    private sealed class WaitingService : BackgroundService
    {
        protected override Task ExecuteAsync(CancellationToken stoppingToken) => Task.Delay(Timeout.Infinite, stoppingToken);
    }
}
