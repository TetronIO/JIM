// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using JIM.Application;
using JIM.Application.Interfaces;
using JIM.Data;
using JIM.Data.Repositories;
using JIM.Web.Authentication;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using NUnit.Framework;

namespace JIM.Web.Api.Tests;

/// <summary>
/// Tests for <see cref="FailedAuthenticationRecorder"/>, the one place a failed authentication is written to the
/// security audit log from the request path.
/// </summary>
[TestFixture]
public class FailedAuthenticationRecorderTests
{
    [Test]
    public async Task RecordAsync_WritesTheAttemptThroughTheSecurityAuditAsync()
    {
        var repository = NewRepository();
        var services = new ServiceCollection();
        services.AddTransient(_ => new JimApplication(repository.Object));
        services.AddSingleton<IFailedAuthenticationRecorder, FailedAuthenticationRecorder>();
        await using var root = services.BuildServiceProvider();

        await root.GetRequiredService<IFailedAuthenticationRecorder>()
            .RecordAsync(FailedAuthenticationEventNames.AccessToken, "Access token expired", null, "203.0.113.7");

        Mock.Get(repository.Object.Activity).Verify(a => a.IncrementAggregatedFailedAuthenticationAsync(
            string.Empty, "203.0.113.7", "Access token expired", It.IsAny<DateTime>(), It.IsAny<DateTime>()), Times.Once);
    }

    [Test]
    public async Task RecordAsync_ResolvedFromTheContainerAsTheHostsDo_DoesNotRetainTheApplicationItUsedAsync()
    {
        // A failed authentication is attacker-reachable, so the JimApplication each write uses must be released
        // afterwards. Resolving a disposable transient from the root provider (a singleton holding IServiceProvider)
        // keeps it in the root scope until the process exits, whether or not it was disposed.
        var created = new List<WeakReference>();
        var services = new ServiceCollection();
        services.AddTransient<IRepository>(_ => NewRepository().Object);
        services.AddTransient(sp =>
        {
            var jim = new JimApplication(sp.GetRequiredService<IRepository>());
            lock (created)
                created.Add(new WeakReference(jim));
            return jim;
        });
        services.AddSingleton<IJimApplicationFactory, JimApplicationFactory>();
        services.AddSingleton<IFailedAuthenticationRecorder, FailedAuthenticationRecorder>();
        await using var root = services.BuildServiceProvider();

        await root.GetRequiredService<IFailedAuthenticationRecorder>()
            .RecordAsync(FailedAuthenticationEventNames.AccessToken, "Access token malformed", null, "203.0.113.7");

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        Assert.That(created, Has.Count.EqualTo(1));
        Assert.That(created[0].IsAlive, Is.False, "the JimApplication used for the audit write is still referenced");
    }

    [Test]
    public async Task RecordAsync_ApplicationCannotBeCreated_CompletesWithoutFaultingAsync()
    {
        var services = new ServiceCollection();
        services.AddTransient<JimApplication>(_ => throw new InvalidOperationException("database unavailable"));
        services.AddSingleton<IFailedAuthenticationRecorder, FailedAuthenticationRecorder>();
        await using var root = services.BuildServiceProvider();
        var recorder = root.GetRequiredService<IFailedAuthenticationRecorder>();

        Assert.That(async () => await recorder.RecordAsync(
            FailedAuthenticationEventNames.ApiKey, "API key not found", "jim_ak_0000", null), Throws.Nothing);
    }

    private static Mock<IRepository> NewRepository()
    {
        var activities = new Mock<IActivityRepository>();
        activities.Setup(a => a.IncrementAggregatedFailedAuthenticationAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<DateTime>(), It.IsAny<DateTime>()))
            .ReturnsAsync(true);
        var repository = new Mock<IRepository>();
        repository.Setup(r => r.Activity).Returns(activities.Object);
        return repository;
    }
}
