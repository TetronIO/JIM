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
using JIM.Models.Core;
using JIM.Models.Interfaces;
using JIM.Models.Security;
using JIM.Models.Staging;
using JIM.Models.Staging.DTOs;
using JIM.Models.Transactional;
using JIM.Models.Transactional.DTOs;
using JIM.Worker.Tests.Services;
using Moq;
using NUnit.Framework;

namespace JIM.Worker.Tests.Servers;

/// <summary>
/// The one password operation (#1635): Set Password aimed at accounts an administrator named, through the same
/// queue, Activity shape and coalescing as a propagated change.
/// <para>
/// What is pinned here is where the explicit mode differs from the propagated one and nowhere else: it needs no
/// Password Synchronisation configuration and is not held by a paused one (decision D1), it carries the account
/// and the enable decision the administrator chose, and it refuses a request it could not honour before anything
/// is recorded. Everything the two modes share (one row per system, the encrypted value, the parent Activity) is
/// asserted to be identical rather than merely similar.
/// </para>
/// </summary>
[TestFixture]
public class SetPasswordRequestTests
{
    private const int CorporateAdId = 3;
    private const int HrPortalId = 4;
    private const int UserObjectTypeId = 200;
    private const string Password = "Correct-Horse-42";

    private JIM.InMemoryData.SyncRepository _syncRepository = null!;
    private Mock<IConnectedSystemRepository> _connectedSystemRepository = null!;
    private TestCredentialProtection _protection = null!;
    private List<Activity> _createdActivities = null!;
    private Dictionary<int, ConnectedSystem> _systems = null!;
    private Dictionary<int, IConnector> _connectors = null!;
    private List<ConnectedSystemObject> _accounts = null!;
    private List<PasswordSynchronisationTarget> _targets = null!;
    private PasswordSynchronisationServer _server = null!;
    private Guid _metaverseObjectId;

    private static readonly MetaverseObject Administrator = new() { Id = Guid.NewGuid(), CachedDisplayName = "Grace Hopper" };

    [SetUp]
    public void SetUp()
    {
        _syncRepository = new JIM.InMemoryData.SyncRepository();
        _connectedSystemRepository = new Mock<IConnectedSystemRepository>();
        _protection = new TestCredentialProtection();
        _createdActivities = [];
        _systems = [];
        _connectors = [];
        _accounts = [];
        _targets = [];
        _metaverseObjectId = Guid.NewGuid();

        _connectedSystemRepository
            .Setup(r => r.GetConnectedSystemObjectsByMetaverseObjectIdAsync(_metaverseObjectId))
            .ReturnsAsync(() => _accounts.ToList());
        _connectedSystemRepository
            .Setup(r => r.GetConnectedSystemForPasswordDeliveryAsync(It.IsAny<int>()))
            .ReturnsAsync((int id) => _systems.TryGetValue(id, out var system) ? system : null);
        _connectedSystemRepository
            .Setup(r => r.GetPasswordSynchronisationTargetsAsync())
            .ReturnsAsync(() => _targets.ToList());

        _server = new PasswordSynchronisationServer(
            _syncRepository,
            () => _connectedSystemRepository.Object,
            () => new Mock<IActivityRepository>().Object,
            () => _protection,
            connectedSystem => _connectors.TryGetValue(connectedSystem.Id, out var connector)
                ? connector
                : throw new NotSupportedException($"No Connector for Connected System {connectedSystem.Id}."),
            (activity, initiatedBy, initiatedByApiKey) =>
            {
                // Mirrors the real Activity server: an Activity attributed to neither a person nor an API key is
                // refused.
                if (initiatedBy == null && initiatedByApiKey == null)
                    throw new InvalidOperationException("Activity must be attributed to a security principal.");
                // The database assigns the id in production; two Activities with the default id would be
                // indistinguishable here, and the tests below tell parent from child by id.
                activity.Id = Guid.NewGuid();
                activity.InitiatedByType = initiatedByApiKey != null ? ActivityInitiatorType.ApiKey : ActivityInitiatorType.User;
                activity.InitiatedById = initiatedByApiKey?.Id ?? initiatedBy!.Id;
                _createdActivities.Add(activity);
                return Task.CompletedTask;
            },
            activity =>
            {
                activity.Id = Guid.NewGuid();
                activity.InitiatedByType = ActivityInitiatorType.System;
                _createdActivities.Add(activity);
                return Task.CompletedTask;
            },
            activity =>
            {
                activity.Status = ActivityStatus.Complete;
                return Task.CompletedTask;
            },
            (activity, errorMessage) =>
            {
                activity.Status = ActivityStatus.FailedWithError;
                activity.ErrorMessage = errorMessage;
                return Task.CompletedTask;
            });
    }

    #region arrangement

    /// <summary>
    /// A Connected System with a Connector that can set passwords, its Password Synchronisation in the given
    /// state: configured and enabled, configured and paused, or not configured at all.
    /// </summary>
    private ConnectedSystem ArrangeSystem(int id, string name, bool? enabled = true, TimeSpan? timeToLive = null, bool connectorCanSetPasswords = true)
    {
        var system = new ConnectedSystem
        {
            Id = id,
            Name = name,
            SettingValues = [],
            InitialPasswordTimeToLive = timeToLive,
            PasswordSynchronisation = enabled is { } isEnabled
                ? new ConnectedSystemPasswordSynchronisation { ConnectedSystemId = id, Enabled = isEnabled, TargetObjectTypeId = UserObjectTypeId }
                : null
        };
        _systems[id] = system;
        _connectors[id] = connectorCanSetPasswords ? new MockCallConnector() : new PasswordlessConnector();

        if (enabled is { } configured)
            _targets.Add(new PasswordSynchronisationTarget
            {
                ConnectedSystemId = id,
                ConnectedSystemName = name,
                TargetObjectTypeId = UserObjectTypeId,
                Enabled = configured,
                TimeToLive = system.EffectiveInitialPasswordTimeToLive
            });

        return system;
    }

    private ConnectedSystemObject ArrangeAccount(int connectedSystemId, int typeId = UserObjectTypeId)
    {
        var account = new ConnectedSystemObject { Id = Guid.NewGuid(), ConnectedSystemId = connectedSystemId, MetaverseObjectId = _metaverseObjectId, TypeId = typeId };
        _accounts.Add(account);
        return account;
    }

    private SetPasswordRequest Request(IReadOnlyList<Guid>? targets, bool? enableAccount = null, string password = Password, ApiKey? apiKey = null) => new()
    {
        MetaverseObjectId = _metaverseObjectId,
        DisplayName = "Ada Lovelace",
        Password = password,
        Targets = targets,
        ExpiryBehaviour = PasswordExpiryBehaviour.RequireChangeAtNextSignIn,
        EnableAccount = enableAccount,
        InitiatedBy = apiKey == null ? Administrator : null,
        InitiatedByApiKey = apiKey
    };

    private PendingPasswordChange QueuedRow() => _syncRepository.PendingPasswordChanges.Values.Single();

    #endregion

    #region an explicit set needs no configuration and is not held by a paused one (decision D1)

    [Test]
    public async Task SetPasswordAsync_ExplicitTargetOnAnUnconfiguredSystem_QueuesAnExplicitRowAsync()
    {
        ArrangeSystem(CorporateAdId, "Corporate AD", enabled: null, timeToLive: TimeSpan.FromDays(3));
        var account = ArrangeAccount(CorporateAdId);

        var result = await _server.SetPasswordAsync(Request([account.Id], enableAccount: true), CancellationToken.None);

        var row = QueuedRow();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Origin, Is.EqualTo(PendingPasswordChangeOrigin.Explicit));
            Assert.That(result.NoTargets, Is.False);
            Assert.That(result.Targets.Single().ConnectedSystemObjectId, Is.EqualTo(account.Id));
            Assert.That(row.Origin, Is.EqualTo(PendingPasswordChangeOrigin.Explicit));
            Assert.That(row.ConnectedSystemObjectId, Is.EqualTo(account.Id), "The row names the account the administrator chose.");
            Assert.That(row.EnableAccount, Is.True, "The enable decision travels with the row to delivery.");
            Assert.That(row.Status, Is.EqualTo(PendingPasswordChangeStatus.Pending));
            Assert.That(row.IsDue(DateTime.UtcNow), Is.True);
            Assert.That(row.ExpiresAt - row.CreatedAt, Is.EqualTo(TimeSpan.FromDays(3)), "The time to live is the Connected System's.");
            Assert.That(_protection.UnprotectPassword(row.EncryptedPassword), Is.EqualTo(Password));
        }
    }

    [Test]
    public async Task SetPasswordAsync_ExplicitTargetWithNoTimeToLiveSet_UsesJimsDefaultAsync()
    {
        ArrangeSystem(CorporateAdId, "Corporate AD", enabled: null);
        var account = ArrangeAccount(CorporateAdId);

        await _server.SetPasswordAsync(Request([account.Id]), CancellationToken.None);

        var row = QueuedRow();
        Assert.That(row.ExpiresAt - row.CreatedAt, Is.EqualTo(PendingInitialPassword.DefaultTimeToLive));
    }

    /// <summary>
    /// The unconfigured system queues a row and the lane delivers it: the end-to-end promise behind decision D1,
    /// with the enable decision reaching the Connector because the administrator made it.
    /// </summary>
    [Test]
    public async Task SetPasswordAsync_ExplicitTargetOnAnUnconfiguredSystem_IsDeliveredWithTheEnableDecisionAsync()
    {
        var system = ArrangeSystem(CorporateAdId, "Corporate AD", enabled: null);
        var account = ArrangeAccount(CorporateAdId);
        var connector = (MockCallConnector)_connectors[CorporateAdId];

        var result = await _server.SetPasswordAsync(Request([account.Id], enableAccount: true), CancellationToken.None);
        var delivery = await _server.DeliverDuePasswordChangesAsync(system, connector, "worker-test", DateTime.UtcNow, CancellationToken.None);

        var attempt = connector.PasswordSetAttempts.Single();
        var child = _createdActivities.Single(a => a.ParentActivityId == result.ActivityId);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(delivery.DeliveredCount, Is.EqualTo(1));
            Assert.That(attempt.ConnectedSystemObjectId, Is.EqualTo(account.Id));
            Assert.That(attempt.PasswordLength, Is.EqualTo(Password.Length));
            Assert.That(attempt.Options.EnableAccount, Is.True);
            Assert.That(attempt.Options.ExpiryBehaviour, Is.EqualTo(PasswordExpiryBehaviour.RequireChangeAtNextSignIn));
            Assert.That(_syncRepository.PendingPasswordChanges, Is.Empty, "Success deletes the row; the Activity is the history.");
            Assert.That(child.TargetType, Is.EqualTo(ActivityTargetType.PasswordSynchronisation));
            Assert.That(child.Status, Is.EqualTo(ActivityStatus.Complete));
        }
    }

    [Test]
    public async Task SetPasswordAsync_ExplicitTargetOnAPausedSystem_QueuesADueRowAndReportsThePauseAsync()
    {
        ArrangeSystem(CorporateAdId, "Corporate AD", enabled: false);
        var account = ArrangeAccount(CorporateAdId);

        var result = await _server.SetPasswordAsync(Request([account.Id]), CancellationToken.None);

        var row = QueuedRow();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Targets.Single().Enabled, Is.False, "The dialog says the system is paused for propagation.");
            Assert.That(row.Status, Is.EqualTo(PendingPasswordChangeStatus.Pending));
            Assert.That(row.IsDue(DateTime.UtcNow), Is.True, "Paused for propagation does not hold an explicit set.");
            Assert.That(_createdActivities.Single().Message, Does.Not.Contain("Held"));
        }
    }

    [Test]
    public async Task SetPasswordAsync_ExplicitTargets_KeepTheOrderGivenAsync()
    {
        ArrangeSystem(CorporateAdId, "Corporate AD");
        ArrangeSystem(HrPortalId, "HR Portal");
        var hr = ArrangeAccount(HrPortalId);
        var ad = ArrangeAccount(CorporateAdId);

        var result = await _server.SetPasswordAsync(Request([hr.Id, ad.Id]), CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Targets.Select(t => t.ConnectedSystemName), Is.EqualTo(new[] { "HR Portal", "Corporate AD" }));
            Assert.That(_syncRepository.PendingPasswordChanges, Has.Count.EqualTo(2));
        }
    }

    #endregion

    #region a request JIM could not honour is refused before anything is recorded

    [Test]
    public void SetPasswordAsync_EmptyTargetList_ThrowsAndRecordsNothing()
    {
        ArrangeSystem(CorporateAdId, "Corporate AD");
        ArrangeAccount(CorporateAdId);

        Assert.ThrowsAsync<ArgumentException>(() => _server.SetPasswordAsync(Request([]), CancellationToken.None));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_syncRepository.PendingPasswordChanges, Is.Empty);
            Assert.That(_createdActivities, Is.Empty, "A request that asked for nothing leaves no record of having asked.");
        }
    }

    [Test]
    public void SetPasswordAsync_TargetThatIsNotThePersonsAccount_ThrowsNamingIt()
    {
        ArrangeSystem(CorporateAdId, "Corporate AD");
        ArrangeAccount(CorporateAdId);
        var foreign = Guid.NewGuid();

        var ex = Assert.ThrowsAsync<ArgumentException>(() => _server.SetPasswordAsync(Request([foreign]), CancellationToken.None));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(ex!.Message, Does.Contain(foreign.ToString()));
            Assert.That(_syncRepository.PendingPasswordChanges, Is.Empty);
            Assert.That(_createdActivities, Is.Empty);
        }
    }

    [Test]
    public void SetPasswordAsync_TargetWhoseConnectorCannotSetPasswords_ThrowsNamingTheSystem()
    {
        ArrangeSystem(CorporateAdId, "Corporate AD", connectorCanSetPasswords: false);
        var account = ArrangeAccount(CorporateAdId);

        var ex = Assert.ThrowsAsync<ArgumentException>(() => _server.SetPasswordAsync(Request([account.Id]), CancellationToken.None));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(ex!.Message, Does.Contain(account.Id.ToString()).And.Contain("Corporate AD"));
            Assert.That(_syncRepository.PendingPasswordChanges, Is.Empty);
        }
    }

    /// <summary>
    /// The queue holds one change per person per system, so two accounts in one system would coalesce into one
    /// row and the first would silently never get the password. Refused, naming both, rather than guessed at.
    /// </summary>
    [Test]
    public void SetPasswordAsync_TwoTargetsInOneSystem_Throws()
    {
        ArrangeSystem(CorporateAdId, "Corporate AD");
        var first = ArrangeAccount(CorporateAdId);
        var second = ArrangeAccount(CorporateAdId);

        var ex = Assert.ThrowsAsync<ArgumentException>(() => _server.SetPasswordAsync(Request([first.Id, second.Id]), CancellationToken.None));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(ex!.Message, Does.Contain(first.Id.ToString()).And.Contain(second.Id.ToString()).And.Contain("Corporate AD"));
            Assert.That(_syncRepository.PendingPasswordChanges, Is.Empty);
        }
    }

    [Test]
    public void SetPasswordAsync_EmptyPassword_Throws()
    {
        ArrangeSystem(CorporateAdId, "Corporate AD");
        var account = ArrangeAccount(CorporateAdId);

        Assert.ThrowsAsync<ArgumentException>(() => _server.SetPasswordAsync(Request([account.Id], password: string.Empty), CancellationToken.None));
    }

    #endregion

    #region coalescing is by person and system, whatever the origin

    [Test]
    public async Task SetPasswordAsync_ExplicitAfterPropagated_SupersedesItAsAnExplicitSetAsync()
    {
        ArrangeSystem(CorporateAdId, "Corporate AD", enabled: false);
        var account = ArrangeAccount(CorporateAdId);

        await _server.SetPasswordAsync(Request(null, password: "first-password"), CancellationToken.None);
        var reset = await _server.SetPasswordAsync(Request([account.Id], enableAccount: true, password: "second-password"), CancellationToken.None);

        var row = QueuedRow();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(_protection.UnprotectPassword(row.EncryptedPassword), Is.EqualTo("second-password"));
            Assert.That(row.Origin, Is.EqualTo(PendingPasswordChangeOrigin.Explicit));
            Assert.That(row.EnableAccount, Is.True);
            Assert.That(row.ActivityId, Is.EqualTo(reset.ActivityId));
            Assert.That(row.IsDue(DateTime.UtcNow), Is.True, "A held propagated change replaced by a reset is delivered as a reset.");
        }
    }

    [Test]
    public async Task SetPasswordAsync_PropagatedAfterExplicit_SupersedesItWithNoEnableDecisionAsync()
    {
        ArrangeSystem(CorporateAdId, "Corporate AD");
        var account = ArrangeAccount(CorporateAdId);

        await _server.SetPasswordAsync(Request([account.Id], enableAccount: true, password: "first-password"), CancellationToken.None);
        await _server.SetPasswordAsync(Request(null, enableAccount: true, password: "second-password"), CancellationToken.None);

        var row = QueuedRow();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(_protection.UnprotectPassword(row.EncryptedPassword), Is.EqualTo("second-password"));
            Assert.That(row.Origin, Is.EqualTo(PendingPasswordChangeOrigin.Propagated));
            Assert.That(row.EnableAccount, Is.Null, "A propagated password never enables an account, whatever the request said.");
        }
    }

    #endregion

    #region the two origins share one Activity shape

    [Test]
    public async Task SetPasswordAsync_ExplicitAndPropagated_RecordTheSameParentActivityShapeAsync()
    {
        ArrangeSystem(CorporateAdId, "Corporate AD");
        var account = ArrangeAccount(CorporateAdId);

        var explicitResult = await _server.SetPasswordAsync(Request([account.Id]), CancellationToken.None);
        var propagatedResult = await _server.SetPasswordAsync(Request(null), CancellationToken.None);

        var explicitActivity = _createdActivities.Single(a => a.Id == explicitResult.ActivityId);
        var propagatedActivity = _createdActivities.Single(a => a.Id == propagatedResult.ActivityId);
        using (Assert.EnterMultipleScope())
        {
            foreach (var activity in new[] { explicitActivity, propagatedActivity })
            {
                Assert.That(activity.TargetType, Is.EqualTo(ActivityTargetType.PasswordSynchronisation));
                Assert.That(activity.TargetOperationType, Is.EqualTo(ActivityTargetOperationType.SetPassword));
                Assert.That(activity.MetaverseObjectId, Is.EqualTo(_metaverseObjectId));
                Assert.That(activity.TargetName, Is.EqualTo("Ada Lovelace"));
                Assert.That(activity.Status, Is.EqualTo(ActivityStatus.Complete));
                Assert.That(activity.InitiatedById, Is.EqualTo(Administrator.Id));
            }

            Assert.That(explicitActivity.Message, Does.StartWith("Password set requested for 1 account: Corporate AD."));
            Assert.That(propagatedActivity.Message, Does.StartWith("Password change queued for 1 Connected System: Corporate AD."));
        }
    }

    /// <summary>
    /// The Activity is the durable record of a change and the queue row is not, so the person's password history
    /// can only say "set" or "propagated" if the parent Activity carries the origin. TargetContext is the one
    /// free-text slot on an Activity that is context about the target rather than the target itself, and the
    /// enum's own name is written so the read side can parse it back without a mapping table.
    /// </summary>
    [Test]
    public async Task SetPasswordAsync_ExplicitAndPropagated_RecordTheOriginOnTheParentActivityAsync()
    {
        ArrangeSystem(CorporateAdId, "Corporate AD");
        var account = ArrangeAccount(CorporateAdId);

        var explicitResult = await _server.SetPasswordAsync(Request([account.Id]), CancellationToken.None);
        var propagatedResult = await _server.SetPasswordAsync(Request(null), CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_createdActivities.Single(a => a.Id == explicitResult.ActivityId).TargetContext,
                Is.EqualTo(nameof(PendingPasswordChangeOrigin.Explicit)));
            Assert.That(_createdActivities.Single(a => a.Id == propagatedResult.ActivityId).TargetContext,
                Is.EqualTo(nameof(PendingPasswordChangeOrigin.Propagated)));
        }
    }

    [Test]
    public async Task SetPasswordAsync_ExplicitAndPropagated_RecordOneChildActivityPerSystemOnDeliveryAsync()
    {
        var system = ArrangeSystem(CorporateAdId, "Corporate AD");
        var account = ArrangeAccount(CorporateAdId);
        var connector = (MockCallConnector)_connectors[CorporateAdId];

        var explicitResult = await _server.SetPasswordAsync(Request([account.Id]), CancellationToken.None);
        await _server.DeliverDuePasswordChangesAsync(system, connector, "worker-test", DateTime.UtcNow, CancellationToken.None);
        var propagatedResult = await _server.SetPasswordAsync(Request(null), CancellationToken.None);
        await _server.DeliverDuePasswordChangesAsync(system, connector, "worker-test", DateTime.UtcNow, CancellationToken.None);

        var explicitChild = _createdActivities.Single(a => a.ParentActivityId == explicitResult.ActivityId);
        var propagatedChild = _createdActivities.Single(a => a.ParentActivityId == propagatedResult.ActivityId);
        using (Assert.EnterMultipleScope())
        {
            foreach (var child in new[] { explicitChild, propagatedChild })
            {
                Assert.That(child.TargetType, Is.EqualTo(ActivityTargetType.PasswordSynchronisation));
                Assert.That(child.TargetOperationType, Is.EqualTo(ActivityTargetOperationType.SetPassword));
                Assert.That(child.ConnectedSystemId, Is.EqualTo(CorporateAdId));
                Assert.That(child.ConnectedSystemObjectId, Is.EqualTo(account.Id));
                Assert.That(child.MetaverseObjectId, Is.EqualTo(_metaverseObjectId));
                Assert.That(child.InitiatedByType, Is.EqualTo(ActivityInitiatorType.System));
                Assert.That(child.Message, Is.EqualTo("Password set on Corporate AD."));
            }
        }
    }

    [Test]
    public async Task SetPasswordAsync_Explicit_NeverRecordsThePasswordOnTheActivityAsync()
    {
        ArrangeSystem(CorporateAdId, "Corporate AD");
        var account = ArrangeAccount(CorporateAdId);

        await _server.SetPasswordAsync(Request([account.Id], password: "Correct-Horse-Battery-Staple"), CancellationToken.None);

        var activity = _createdActivities.Single();
        var text = $"{activity.TargetName} {activity.Message} {activity.TargetContext} {activity.ErrorMessage}";
        Assert.That(text, Does.Not.Contain("Correct-Horse").And.Not.Contain("Battery"));
    }

    [Test]
    public async Task SetPasswordAsync_InitiatedByAnApiKey_AttributesTheActivityToItAsync()
    {
        ArrangeSystem(CorporateAdId, "Corporate AD");
        var account = ArrangeAccount(CorporateAdId);
        var apiKey = new ApiKey { Id = Guid.NewGuid(), Name = "Service Desk automation" };

        await _server.SetPasswordAsync(Request([account.Id], apiKey: apiKey), CancellationToken.None);

        var activity = _createdActivities.Single();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(activity.InitiatedByType, Is.EqualTo(ActivityInitiatorType.ApiKey));
            Assert.That(activity.InitiatedById, Is.EqualTo(apiKey.Id));
        }
    }

    #endregion

    #region the propagated mode is exactly what it was

    [Test]
    public async Task SetPasswordAsync_NoTargets_QueuesForEveryConfiguredSystemAsPropagatedAsync()
    {
        ArrangeSystem(CorporateAdId, "Corporate AD");
        ArrangeSystem(HrPortalId, "HR Portal", enabled: false);
        ArrangeAccount(CorporateAdId);

        var result = await _server.SetPasswordAsync(Request(null, enableAccount: true), CancellationToken.None);

        var rows = _syncRepository.PendingPasswordChanges.Values.ToList();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Origin, Is.EqualTo(PendingPasswordChangeOrigin.Propagated));
            Assert.That(rows.Select(r => r.ConnectedSystemId), Is.EquivalentTo(new[] { CorporateAdId, HrPortalId }));
            Assert.That(rows.Select(r => r.Origin), Is.All.EqualTo(PendingPasswordChangeOrigin.Propagated));
            Assert.That(rows.Select(r => r.EnableAccount), Is.All.Null, "A propagated change never carries an enable decision.");
            Assert.That(rows.Single(r => r.ConnectedSystemId == HrPortalId).ConnectedSystemObjectId, Is.Null,
                "No account there yet; the change waits for provisioning.");
            Assert.That(result.Targets.Single(t => t.ConnectedSystemId == HrPortalId).Enabled, Is.False);
        }
    }

    [Test]
    public async Task SetPasswordAsync_NoTargetsAndNothingConfigured_RecordsAnExplicitNoOpAsync()
    {
        ArrangeSystem(CorporateAdId, "Corporate AD", enabled: null);
        ArrangeAccount(CorporateAdId);

        var result = await _server.SetPasswordAsync(Request(null), CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.NoTargets, Is.True);
            Assert.That(_syncRepository.PendingPasswordChanges, Is.Empty);
            Assert.That(_createdActivities.Single().Id, Is.EqualTo(result.ActivityId));
        }
    }

    #endregion

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
