// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Diagnostics;

namespace Tetron.PowerShellRemoting.Tests;

public class RemoteSessionFactoryTests
{
    [Test]
    public void OpenAsync_AlreadyCancelled_ThrowsWithoutContactingAnything()
    {
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        var options = new RemoteSessionOptions
        {
            ConnectionUri = new Uri("http://192.0.2.1:5985/wsman"),
            UserPrincipalName = "nobody@EXAMPLE.TEST",
            Password = "not-used"
        };

        var stopwatch = Stopwatch.StartNew();
        Assert.That(async () => await RemoteSessionFactory.OpenAsync(options, cancelled.Token), Throws.InstanceOf<OperationCanceledException>());
        Assert.That(stopwatch.Elapsed, Is.LessThan(TimeSpan.FromSeconds(2)));
    }
}
