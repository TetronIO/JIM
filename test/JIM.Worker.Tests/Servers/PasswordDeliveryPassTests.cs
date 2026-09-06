// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using JIM.Application.Servers;
using JIM.Connectors.Mock;
using JIM.Data.Repositories;
using JIM.Models.Activities;
using JIM.Models.Interfaces;
using JIM.Models.Staging;
using JIM.Models.Transactional;
using JIM.Worker.Tests.Services;
using Moq;
using NUnit.Framework;

namespace JIM.Worker.Tests.Servers;

/// <summary>
/// The delivery pass the worker runs: which Connected Systems it visits, and what it does when one of them
/// cannot be delivered to (#1119).
/// <para>
/// Separate from <see cref="PasswordDeliveryTests"/>, which pins what happens to a single queued change. What is
/// pinned here is the layer above it: a pass must never abandon the systems it has not reached because one of
/// them is misconfigured, unreachable, or served by a Connector that has since been removed.
/// </para>
/// </summary>
[TestFixture]
public class PasswordDeliveryPassTests
{
    private const int FirstSystemId = 3;
    private const int SecondSystemId = 4;
    private const int UserObjectTypeId = 200;
    private const string ClaimedBy = "worker-test-1a2b3c4d";

    private JIM.InMemoryData.SyncRepository _syncRepository = null!;
    private Mock<IConnectedSystemRepository> _connectedSystemRepository = null!;
    private TestCredentialProtection _protection = null!;
    private PasswordSynchronisationServer _server = null!;
    private Dictionary<int, MockCallConnector> _connectors = null!;
    private List<int> _connectorRequests = null!;
    private Func<ConnectedSystem, IConnector> _createConnector = null!;

    [SetUp]
    public void SetUp()
    {
        _syncRepository = new JIM.InMemoryData.SyncRepository();
        _connectedSystemRepository = new Mock<IConnectedSystemRepository>();
        _protection = new TestCredentialProtection();
        _connectors = [];
        _connectorRequests = [];

        _createConnector = connectedSystem =>
        {
            _connectorRequests.Add(connectedSystem.Id);
            if (!_connectors.TryGetValue(connectedSystem.Id, out var connector))
                throw new NotSupportedException($"Connector definition for system {connectedSystem.Id} is not supported.");

            return connector;
        };

        _connectedSystemRepository
            .Setup(r => r.GetConnectedSystemObjectsByMetaverseObjectIdAsync(It.IsAny<Guid>()))
            .ReturnsAsync([]);

        _server = new PasswordSynchronisationServer(
            _syncRepository,
            () => _connectedSystemRepository.Object,
            () => new Mock<JIM.Data.Repositories.IActivityRepository>().Object,
            () => _protection,
            cs => _createConnector(cs),
            // Mirrors the real Activity server's refusal to record an Activity attributed to nobody. A fake that
            // accepted one is why #1529 hid here: delivery passed null for both initiators, the real server threw,
            // and every unit test passed regardless.
            (activity, initiatedBy, initiatedByApiKey) =>
            {
                if (initiatedBy == null && initiatedByApiKey == null)
                    throw new InvalidOperationException(
                        "Activity must be attributed to a security principal. InitiatedByType has not been set.");
                return Task.CompletedTask;
            },
            activity =>
            {
                activity.InitiatedByType = ActivityInitiatorType.System;
                return Task.CompletedTask;
            },
            _ => Task.CompletedTask,
            (_, _) => Task.CompletedTask);
    }

    /// <summary>
    /// Registers a Connected System that is configured and enabled for Password Synchronisation, with a Connector
    /// that can set passwords.
    /// </summary>
    private ConnectedSystem ArrangeSystem(int connectedSystemId, string name, bool enabled = true, bool withConnector = true)
    {
        var connectedSystem = new ConnectedSystem
        {
            Id = connectedSystemId,
            Name = name,
            SettingValues = [],
            PasswordSynchronisation = new ConnectedSystemPasswordSynchronisation
            {
                ConnectedSystemId = connectedSystemId,
                Enabled = enabled,
                TargetObjectTypeId = UserObjectTypeId,
                MaxRetries = 3,
                RetryBackoffBase = TimeSpan.FromMinutes(5)
            }
        };

        _connectedSystemRepository
            .Setup(r => r.GetConnectedSystemForPasswordDeliveryAsync(connectedSystemId))
            .ReturnsAsync(connectedSystem);

        if (withConnector)
            _connectors[connectedSystemId] = new MockCallConnector();

        return connectedSystem;
    }

    /// <summary>
    /// Queues a change for a system, and gives the identity an account of the target type there so the pass has
    /// something it can actually deliver to.
    /// </summary>
    private async Task<PendingPasswordChange> QueueAsync(int connectedSystemId)
    {
        var now = DateTime.UtcNow;
        var change = new PendingPasswordChange
        {
            MetaverseObjectId = Guid.NewGuid(),
            ConnectedSystemId = connectedSystemId,
            ConnectedSystemObjectId = Guid.NewGuid(),
            EncryptedPassword = _protection.ProtectPassword("a-password")!,
            CreatedAt = now,
            ExpiresAt = now.AddDays(7),
            ActivityId = Guid.NewGuid()
        };

        await _syncRepository.QueuePasswordChangesAsync([change]);

        _connectedSystemRepository
            .Setup(r => r.GetConnectedSystemObjectsByMetaverseObjectIdAsync(change.MetaverseObjectId))
            .ReturnsAsync([
                new ConnectedSystemObject
                {
                    Id = change.ConnectedSystemObjectId!.Value,
                    ConnectedSystemId = connectedSystemId,
                    TypeId = UserObjectTypeId
                }
            ]);

        return change;
    }

    [Test]
    public async Task DeliverDueAsync_SystemNamed_VisitsOnlyThatSystemAsync()
    {
        ArrangeSystem(FirstSystemId, "Corporate AD");
        ArrangeSystem(SecondSystemId, "Partner Directory");
        await QueueAsync(FirstSystemId);
        await QueueAsync(SecondSystemId);

        var result = await _server.DeliverDueAsync(FirstSystemId, ClaimedBy, DateTime.UtcNow, CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_connectorRequests, Is.EqualTo(new[] { FirstSystemId }));
            Assert.That(result.ConnectedSystemsVisited, Is.EqualTo(1));
            Assert.That(result.DeliveredCount, Is.EqualTo(1));
        }
    }

    [Test]
    public async Task DeliverDueAsync_NoSystemNamed_VisitsEverySystemWithWorkDueAsync()
    {
        ArrangeSystem(FirstSystemId, "Corporate AD");
        ArrangeSystem(SecondSystemId, "Partner Directory");
        await QueueAsync(FirstSystemId);
        await QueueAsync(SecondSystemId);

        var result = await _server.DeliverDueAsync(null, ClaimedBy, DateTime.UtcNow, CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_connectorRequests.OrderBy(id => id), Is.EqualTo(new[] { FirstSystemId, SecondSystemId }));
            Assert.That(result.ConnectedSystemsVisited, Is.EqualTo(2));
            Assert.That(result.DeliveredCount, Is.EqualTo(2));
        }
    }

    [Test]
    public async Task DeliverDueAsync_NoSystemNamedAndNothingDue_VisitsNothingAsync()
    {
        ArrangeSystem(FirstSystemId, "Corporate AD");

        var result = await _server.DeliverDueAsync(null, ClaimedBy, DateTime.UtcNow, CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_connectorRequests, Is.Empty);
            Assert.That(result.ConnectedSystemsVisited, Is.Zero);
            Assert.That(result.HasSomethingToReport, Is.False);
        }
    }

    [Test]
    public async Task DeliverDueAsync_SystemDeletedSinceQueueing_IsSkippedAndTheRestStillRunAsync()
    {
        // No arrangement for the first system at all: the repository answers null, as it would for a Connected
        // System deleted between a change being queued and this pass reaching it.
        _connectedSystemRepository
            .Setup(r => r.GetConnectedSystemForPasswordDeliveryAsync(FirstSystemId))
            .ReturnsAsync((ConnectedSystem?)null);
        ArrangeSystem(SecondSystemId, "Partner Directory");
        await QueueAsync(FirstSystemId);
        await QueueAsync(SecondSystemId);

        var result = await _server.DeliverDueAsync(null, ClaimedBy, DateTime.UtcNow, CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.DeliveredCount, Is.EqualTo(1), "The second system must still be delivered to.");
            Assert.That(result.ConnectedSystemsVisited, Is.EqualTo(1));
        }
    }

    [Test]
    public async Task DeliverDueAsync_ConnectorNoLongerSupported_IsReportedNotThrownAsync()
    {
        ArrangeSystem(FirstSystemId, "Corporate AD", withConnector: false);
        ArrangeSystem(SecondSystemId, "Partner Directory");
        await QueueAsync(FirstSystemId);
        await QueueAsync(SecondSystemId);

        var result = await _server.DeliverDueAsync(null, ClaimedBy, DateTime.UtcNow, CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.DeliveredCount, Is.EqualTo(1), "The system whose Connector resolves must still be delivered to.");
            Assert.That(result.Problems, Has.Exactly(1).Contains("Corporate AD"));
        }
    }

    [Test]
    public async Task DeliverDueAsync_ConnectorNoLongerSupported_LeavesTheChangeQueuedAsync()
    {
        ArrangeSystem(FirstSystemId, "Corporate AD", withConnector: false);
        var change = await QueueAsync(FirstSystemId);

        await _server.DeliverDueAsync(FirstSystemId, ClaimedBy, DateTime.UtcNow, CancellationToken.None);

        var stored = await _syncRepository.GetDuePasswordChangesAsync(FirstSystemId, DateTime.UtcNow.AddMinutes(1), 10);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(stored, Has.Exactly(1).Items, "A Connector that cannot be resolved must not consume the change.");
            Assert.That(stored[0].Id, Is.EqualTo(change.Id));
            Assert.That(stored[0].AttemptCount, Is.Zero, "Nothing was attempted, so nothing may be counted against the change.");
        }
    }

    [Test]
    public async Task DeliverDueAsync_SystemDisabledSinceQueueing_IsNotVisitedAsync()
    {
        ArrangeSystem(FirstSystemId, "Corporate AD", enabled: false);
        await QueueAsync(FirstSystemId);

        var result = await _server.DeliverDueAsync(null, ClaimedBy, DateTime.UtcNow, CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_connectorRequests, Is.Empty, "A disabled system must not have its Connector resolved.");
            Assert.That(result.ConnectedSystemsVisited, Is.Zero);
        }
    }

    [Test]
    public async Task DeliverDueAsync_ConnectorCannotSetPasswords_IsReportedAsync()
    {
        ArrangeSystem(FirstSystemId, "Corporate AD", withConnector: false);
        // A Connector that resolves but has no password capability at all.
        _createConnector = _ => new PasswordlessConnector();
        await QueueAsync(FirstSystemId);

        var result = await _server.DeliverDueAsync(FirstSystemId, ClaimedBy, DateTime.UtcNow, CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.DeliveredCount, Is.Zero);
            Assert.That(result.Problems, Has.Exactly(1).Contains("Corporate AD"));
        }
    }

    [Test]
    public async Task DeliverDueAsync_Cancelled_StopsBeforeTheNextSystemAsync()
    {
        ArrangeSystem(FirstSystemId, "Corporate AD");
        ArrangeSystem(SecondSystemId, "Partner Directory");
        await QueueAsync(FirstSystemId);
        await QueueAsync(SecondSystemId);

        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        var result = await _server.DeliverDueAsync(null, ClaimedBy, DateTime.UtcNow, cancellation.Token);

        Assert.That(result.ConnectedSystemsVisited, Is.Zero);
    }

    [Test]
    public async Task DeliverDueAsync_NothingHappened_HasNothingToReportAsync()
    {
        ArrangeSystem(FirstSystemId, "Corporate AD");

        var result = await _server.DeliverDueAsync(FirstSystemId, ClaimedBy, DateTime.UtcNow, CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.HasSomethingToReport, Is.False);
            Assert.That(result.Describe(), Is.Null, "A pass that achieved nothing must leave the Activity message alone.");
        }
    }

    [Test]
    public async Task DeliverDueAsync_Delivered_DescribesTheOutcomeWithoutNamingAPasswordAsync()
    {
        ArrangeSystem(FirstSystemId, "Corporate AD");
        await QueueAsync(FirstSystemId);

        var result = await _server.DeliverDueAsync(FirstSystemId, ClaimedBy, DateTime.UtcNow, CancellationToken.None);

        var description = result.Describe();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(description, Is.Not.Null);
            Assert.That(description, Does.Contain("1"));
            Assert.That(description, Does.Not.Contain("a-password"));
        }
    }

    [Test]
    public async Task DeliverDueAsync_SecureTransportRequiredAndChannelIsNot_DeliversNothingAsync()
    {
        // The administrator has said passwords must not leave JIM in the clear for this system. Refusing is the
        // whole point of the setting: sending anyway, having been told not to, would be the worst outcome
        // available.
        var connectedSystem = ArrangeSystem(FirstSystemId, "Corporate AD");
        connectedSystem.RequireSecureTransport = true;
        _connectors[FirstSystemId].PasswordChannelSecure = false;
        var change = await QueueAsync(FirstSystemId);

        var result = await _server.DeliverDueAsync(FirstSystemId, ClaimedBy, DateTime.UtcNow, CancellationToken.None);

        var stored = await _syncRepository.GetDuePasswordChangesAsync(FirstSystemId, DateTime.UtcNow.AddMinutes(1), 10);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.DeliveredCount, Is.Zero);
            Assert.That(result.Problems, Has.Exactly(1).Contains("Corporate AD"));
            Assert.That(stored, Has.Exactly(1).Items, "The change waits for a secure channel rather than being consumed.");
            Assert.That(stored[0].Id, Is.EqualTo(change.Id));
            Assert.That(stored[0].AttemptCount, Is.Zero,
                "Nothing was sent, so nothing may be counted against the change.");
            Assert.That(stored[0].ClaimedBy, Is.Null, "The claim the pass took is given back with the change.");
        }
    }

    [Test]
    public async Task DeliverDueAsync_SecureTransportRequiredAndChannelIs_DeliversAsync()
    {
        var connectedSystem = ArrangeSystem(FirstSystemId, "Corporate AD");
        connectedSystem.RequireSecureTransport = true;
        _connectors[FirstSystemId].PasswordChannelSecure = true;
        await QueueAsync(FirstSystemId);

        var result = await _server.DeliverDueAsync(FirstSystemId, ClaimedBy, DateTime.UtcNow, CancellationToken.None);

        Assert.That(result.DeliveredCount, Is.EqualTo(1));
    }

    [Test]
    public async Task DeliverDueAsync_SecureTransportNotRequiredAndChannelIsNot_DeliversAsync()
    {
        // Some directories genuinely cannot offer TLS, and locking those sites out of Password Synchronisation
        // entirely helps nobody. The choice belongs to the administrator, who is warned either way.
        var connectedSystem = ArrangeSystem(FirstSystemId, "Corporate AD");
        connectedSystem.RequireSecureTransport = false;
        _connectors[FirstSystemId].PasswordChannelSecure = false;
        await QueueAsync(FirstSystemId);

        var result = await _server.DeliverDueAsync(FirstSystemId, ClaimedBy, DateTime.UtcNow, CancellationToken.None);

        Assert.That(result.DeliveredCount, Is.EqualTo(1));
    }

    [Test]
    public async Task DeliverDueAsync_SecureTransportRefused_ClosesTheChannelAsync()
    {
        var connectedSystem = ArrangeSystem(FirstSystemId, "Corporate AD");
        connectedSystem.RequireSecureTransport = true;
        _connectors[FirstSystemId].PasswordChannelSecure = false;
        await QueueAsync(FirstSystemId);

        await _server.DeliverDueAsync(FirstSystemId, ClaimedBy, DateTime.UtcNow, CancellationToken.None);

        Assert.That(_connectors[FirstSystemId].PasswordConnectionOpen, Is.False,
            "A refused pass must not leave the channel it opened to make the check hanging open.");
    }

    /// <summary>
    /// A Connector with no password capability, standing in for one whose capability was removed or never existed.
    /// </summary>
    private sealed class PasswordlessConnector : IConnector
    {
        public string Name => "Passwordless Connector";
        public string? Description => null;
        public string? Url => null;
    }
}
