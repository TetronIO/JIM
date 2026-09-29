// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Net;
using System.Net.Sockets;
using JIM.PostgresData;
using Microsoft.EntityFrameworkCore;
using NUnit.Framework;

namespace JIM.Worker.Tests.Repositories;

/// <summary>
/// <see cref="PostgresDataRepository.TryConnectAsync"/> against a server that is not there. No PostgreSQL is needed,
/// so this runs in every build: a port nothing listens on refuses the connection at once.
/// </summary>
[TestFixture]
public class RepositoryTryConnectTests
{
    [Test]
    public async Task TryConnectAsync_NothingListening_IsNotConnectedAndNamesTheServerAsync()
    {
        var port = UnusedLocalPort();
        var options = new DbContextOptionsBuilder<JimDbContext>()
            .UseNpgsql($"Host=127.0.0.1;Port={port};Database=jim;Username=jim;Password=unused;Timeout=5")
            .Options;
        await using var context = new JimDbContext(options);
        using var repository = new PostgresDataRepository(context);

        var result = await repository.TryConnectAsync(CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.IsConnected, Is.False, "a refused connection is the database not being up yet, which is worth retrying");
            Assert.That(result.FailureReason, Does.StartWith($"127.0.0.1:{port}: "), "the log line must say which server it is waiting for");
            // The socket's own reason, not Npgsql's generic "Failed to connect to": refused, unresolvable and timed
            // out each point the administrator somewhere different.
            Assert.That(result.FailureReason, Does.Contain("refused").IgnoreCase);
        }
    }

    private static int UnusedLocalPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
