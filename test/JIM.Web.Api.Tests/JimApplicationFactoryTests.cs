// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using JIM.Application;
using JIM.Application.Interfaces;
using JIM.Data;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using NUnit.Framework;

namespace JIM.Web.Api.Tests;

/// <summary>
/// Tests for <see cref="JimApplicationFactory"/>, registered as a singleton by JIM.Web, JIM.Worker and JIM.Scheduler.
/// The container here is built the way the hosts build theirs: a transient <see cref="IRepository"/> and a transient
/// <see cref="JimApplication"/>, both disposable, behind the singleton factory.
/// </summary>
[TestFixture]
public class JimApplicationFactoryTests
{
    [Test]
    public async Task Create_InstanceDisposedByTheCaller_IsNotRetainedByTheContainerAsync()
    {
        // A singleton holds the root provider, and the root scope keeps every disposable transient it resolves until
        // the process exits, disposed or not. Every portal page load, Worker task and Password Delivery batch goes
        // through this factory, so a retained instance is memory that grows with work done.
        var created = new List<WeakReference>();
        await using var root = BuildHostLikeProvider(created, out _);

        CreateAndDispose(root.GetRequiredService<IJimApplicationFactory>(), 100);

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        Assert.That(created, Has.Count.EqualTo(100));
        Assert.That(created.FindAll(w => w.IsAlive), Is.Empty,
            "JimApplication instances created and disposed through the factory are still referenced");
    }

    [Test]
    public async Task Create_InstanceDisposed_DisposesItsRepositoryAsync()
    {
        await using var root = BuildHostLikeProvider([], out var repositories);

        using (root.GetRequiredService<IJimApplicationFactory>().Create())
        {
            Assert.That(repositories, Has.Count.EqualTo(1));
            repositories[0].Verify(r => r.Dispose(), Times.Never);
        }

        repositories[0].Verify(r => r.Dispose(), Times.AtLeastOnce);
    }

    [Test]
    public async Task Create_CalledTwice_ReturnsSeparateInstancesWithSeparateRepositoriesAsync()
    {
        // Each caller must get its own DbContext; parallel Worker tasks and concurrent Blazor renders depend on it.
        await using var root = BuildHostLikeProvider([], out var repositories);
        var factory = root.GetRequiredService<IJimApplicationFactory>();

        using var first = factory.Create();
        using var second = factory.Create();

        Assert.That(second, Is.Not.SameAs(first));
        Assert.That(repositories, Has.Count.EqualTo(2));
    }

    [Test]
    public async Task Create_InstanceDisposedTwice_DoesNotThrowAsync()
    {
        await using var root = BuildHostLikeProvider([], out _);
        var jim = root.GetRequiredService<IJimApplicationFactory>().Create();

        jim.Dispose();

        Assert.That(jim.Dispose, Throws.Nothing);
    }

    [Test]
    public async Task Create_ResolutionFailsPartWay_DisposesWhatItHadResolvedAsync()
    {
        // The hosts' JimApplication registration resolves a repository (opening a DbContext) before it can fail, for
        // instance when the database is unreachable. What was resolved must not outlive the failed call.
        var repository = new Mock<IRepository>();
        var services = new ServiceCollection();
        services.AddTransient<IRepository>(_ => repository.Object);
        services.AddTransient<JimApplication>(sp =>
        {
            sp.GetRequiredService<IRepository>();
            throw new InvalidOperationException("database unavailable");
        });
        services.AddSingleton<IJimApplicationFactory, JimApplicationFactory>();
        await using var root = services.BuildServiceProvider();
        var factory = root.GetRequiredService<IJimApplicationFactory>();

        Assert.That(factory.Create, Throws.InvalidOperationException);
        repository.Verify(r => r.Dispose(), Times.Once);
    }

    // Kept out of the test methods so no local in them can keep an instance reachable while the GC runs.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void CreateAndDispose(IJimApplicationFactory factory, int count)
    {
        for (var i = 0; i < count; i++)
        {
            using var jim = factory.Create();
        }
    }

    private static ServiceProvider BuildHostLikeProvider(List<WeakReference> created, out List<Mock<IRepository>> repositories)
    {
        var repositoryMocks = new List<Mock<IRepository>>();
        repositories = repositoryMocks;
        var services = new ServiceCollection();
        services.AddTransient<IRepository>(_ =>
        {
            var repository = new Mock<IRepository>();
            lock (repositoryMocks)
                repositoryMocks.Add(repository);
            return repository.Object;
        });
        services.AddTransient(sp =>
        {
            var jim = new JimApplication(sp.GetRequiredService<IRepository>());
            lock (created)
                created.Add(new WeakReference(jim));
            return jim;
        });
        services.AddSingleton<IJimApplicationFactory, JimApplicationFactory>();
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
    }
}
