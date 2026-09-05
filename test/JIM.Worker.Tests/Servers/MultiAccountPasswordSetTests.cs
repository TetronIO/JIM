// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application;
using JIM.Connectors;
using System.Collections.Concurrent;
using JIM.Data;
using JIM.Data.Repositories;
using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Interfaces;
using JIM.Models.Staging;
using Moq;
using Serilog;

namespace JIM.Worker.Tests.Servers;

/// <summary>
/// Covers setting one password across several of a person's accounts (issue #1172).
/// <para>
/// TODO(#1635 layer 3, web agent): this fixture guards the interim <c>SetPasswordOnAccountsAsync</c> shim, which
/// the portal's Set Password dialog still calls; delete it with the shim once the dialog queues through
/// <c>PasswordSynchronisationServer.SetPasswordAsync</c>. Each intent here has its queue-side counterpart in
/// <c>SetPasswordRequestTests</c> and <c>PasswordDeliveryTests</c>: one refusal never stops the rest (each system
/// is its own lane), every account is reported by name (per-target outcomes), and the audit trail is one parent
/// Activity with a child per system. Progress narration is gone; outcomes come from the queue.
/// </para>
/// <para>
/// The behaviour worth guarding is what happens when the fan-out does not go cleanly, because it routinely does
/// not: three Connected Systems are three independent writes with no transaction between them. One account
/// refusing must not stop the others, every account must be reported by name, and the audit trail must let
/// somebody reconstruct which accounts actually changed.
/// </para>
/// </summary>
[TestFixture]
public class MultiAccountPasswordSetTests
{
    private const string Password = "Correct-Horse-42";

    private Mock<IActivityRepository> _activityRepository = null!;
    private Mock<IConnectedSystemRepository> _connectedSystemRepository = null!;
    private Dictionary<int, RecordingPasswordConnector> _connectors = null!;
    private List<MetaverseObjectAccount> _accounts = null!;
    private JimApplication _application = null!;
    private Guid _metaverseObjectId;

    [SetUp]
    public void Setup()
    {
        TestUtilities.SetEnvironmentVariables();

        _metaverseObjectId = Guid.NewGuid();
        _connectors = [];
        _accounts = [];

        _activityRepository = new Mock<IActivityRepository>();
        _connectedSystemRepository = new Mock<IConnectedSystemRepository>();

        foreach (var (connectedSystemId, name) in new[] { (1, "Contoso AD"), (2, "Fabrikam HR"), (3, "Research LDAP") })
        {
            _connectors[connectedSystemId] = new RecordingPasswordConnector();

            var connectedSystem = new ConnectedSystem
            {
                Id = connectedSystemId,
                Name = name,
                // A distinct Connector Definition per system, so the stub factory can hand back the right
                // Connector by name. Keying it off call order instead drifts the moment an account fails
                // before its Connector is created, and the drift reads as the product skipping an account.
                ConnectorDefinition = new ConnectorDefinition { Id = connectedSystemId, Name = $"Connector {connectedSystemId}" },
                ConnectorDefinitionId = connectedSystemId,
                SettingValues = [new ConnectedSystemSettingValue { Id = 1 }]
            };
            var csoId = Guid.NewGuid();
            var cso = new ConnectedSystemObject { Id = csoId, ConnectedSystemId = connectedSystemId, MetaverseObjectId = _metaverseObjectId };

            _connectedSystemRepository.Setup(r => r.GetConnectedSystemCoreAsync(connectedSystemId)).ReturnsAsync(connectedSystem);
            _connectedSystemRepository.Setup(r => r.GetConnectedSystemObjectAsync(connectedSystemId, csoId)).ReturnsAsync(cso);

            _accounts.Add(new MetaverseObjectAccount
            {
                ConnectedSystemObjectId = csoId,
                ConnectedSystemId = connectedSystemId,
                ConnectedSystemName = name,
                AccountIdentifier = $"uid=alovelace,{name}",
                ConnectorCanSetPasswords = true
            });
        }

        var repository = new Mock<IRepository>();
        repository.Setup(r => r.Activity).Returns(_activityRepository.Object);
        repository.Setup(r => r.ConnectedSystems).Returns(_connectedSystemRepository.Object);

        _application = new JimApplication(repository.Object, connectorFactory: new PerSystemConnectorFactory(_connectors));
    }

    [TearDown]
    public void TearDown()
    {
        _application?.Dispose();
    }

    private async Task<MultiAccountPasswordSetResult> SetOnAsync(
        IReadOnlyList<MetaverseObjectAccount>? accounts = null,
        IProgress<AccountPasswordSetOutcome>? progress = null,
        CancellationToken cancellationToken = default)
    {
        return await _application.ConnectedSystems.SetPasswordOnAccountsAsync(
            _metaverseObjectId, accounts ?? _accounts, Password, new PasswordSetOptions(),
            new MetaverseObject { Id = Guid.NewGuid(), CachedDisplayName = "Ada Lovelace" }, progress, cancellationToken);
    }

    private List<Activity> CreatedActivities()
    {
        var created = new List<Activity>();
        _activityRepository.Verify(r => r.CreateActivityAsync(Capture.In(created)), Times.AtLeastOnce);
        return created;
    }

    #region one refusal does not stop the rest

    /// <summary>
    /// The defining behaviour. A directory refusing the password says nothing about the other two, and stopping
    /// there would leave the administrator to repeat the whole exercise for accounts that would have worked.
    /// </summary>
    [Test]
    public async Task SetPasswordOnAccountsAsync_WhenOneSystemRefuses_StillAttemptsTheRestAsync()
    {
        _connectors[2].Result = PasswordSetResult.Failed(PasswordSetFailureReason.PolicyRejection, "Refused.");

        var result = await SetOnAsync();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Outcomes, Has.Count.EqualTo(3));
            Assert.That(_connectors[1].PasswordsSet, Is.EqualTo(new[] { Password }));
            Assert.That(_connectors[3].PasswordsSet, Is.EqualTo(new[] { Password }), "the system after the refusal must still be attempted");
            Assert.That(result.SucceededCount, Is.EqualTo(2));
            Assert.That(result.IsPartial, Is.True);
        }
    }

    /// <summary>
    /// A Connector that throws rather than classifying, or an account that has gone, is that account's problem
    /// and not the fan-out's.
    /// </summary>
    [Test]
    public async Task SetPasswordOnAccountsAsync_WhenOneAccountDoesNotExist_StillAttemptsTheRestAsync()
    {
        var accounts = _accounts.ToList();
        accounts[1] = new MetaverseObjectAccount
        {
            ConnectedSystemObjectId = Guid.NewGuid(),
            ConnectedSystemId = 2,
            ConnectedSystemName = "Fabrikam HR",
            AccountIdentifier = "gone",
            ConnectorCanSetPasswords = true
        };

        var result = await SetOnAsync(accounts);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Outcomes, Has.Count.EqualTo(3));
            Assert.That(result.Failed.Single().Result.FailureReason, Is.EqualTo(PasswordSetFailureReason.TargetObjectNotFound));
            Assert.That(_connectors[3].PasswordsSet, Is.EqualTo(new[] { Password }));
        }
    }

    [Test]
    public async Task SetPasswordOnAccountsAsync_ReportsEachAccountByNameAsync()
    {
        _connectors[2].Result = PasswordSetResult.Failed(PasswordSetFailureReason.PolicyRejection,
            "The password does not meet the requirements of the domain.");

        var result = await SetOnAsync();

        var refused = result.Failed.Single();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(refused.ConnectedSystemName, Is.EqualTo("Fabrikam HR"));
            Assert.That(refused.Result.ErrorMessage, Does.Contain("does not meet the requirements"));
        }
    }

    #endregion

    #region sequence and progress

    /// <summary>
    /// Sequential in the order given, which is what lets a caller narrate the fan-out at all, and what makes a
    /// target refusing everything visible on the first account rather than the fourth.
    /// </summary>
    [Test]
    public async Task SetPasswordOnAccountsAsync_AttemptsAccountsInTheOrderGivenAsync()
    {
        // Asserted on the returned record rather than on progress, because Progress<T> posts to the captured
        // context and its arrival order is not the attempt order. The returned list is built as each attempt
        // finishes, so it is the attempt order by construction.
        var result = await SetOnAsync();

        Assert.That(result.Outcomes.Select(o => o.ConnectedSystemName),
            Is.EqualTo(new[] { "Contoso AD", "Fabrikam HR", "Research LDAP" }));
    }

    [Test]
    public async Task SetPasswordOnAccountsAsync_ReportsEachOutcomeAsItLandsAsync()
    {
        // A concurrent collection rather than a List, because the callbacks are not serialised: NUnit's
        // SafeSynchronizationContext hands each Progress<T> callback to its own thread pool thread, so all three
        // can run at once even though the product reports the accounts strictly one at a time. Two concurrent
        // List<T>.Add calls can write the same index, and the outcome that loses is gone with no error anywhere;
        // that is what failed this test on main, reproducibly at roughly one run in thirteen.
        var reported = new ConcurrentQueue<AccountPasswordSetOutcome>();
        await SetOnAsync(progress: new Progress<AccountPasswordSetOutcome>(reported.Enqueue));

        // Progress<T> posts to the captured context, so the reports may arrive after the call returns.
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (reported.Count < 3 && DateTime.UtcNow < deadline)
            await Task.Delay(20);

        Assert.That(reported.Select(o => o.ConnectedSystemName), Is.EquivalentTo(new[] { "Contoso AD", "Fabrikam HR", "Research LDAP" }));
    }

    /// <summary>
    /// Cancelling prevents the accounts not yet reached, and cannot undo the ones already written. Their
    /// outcomes are still returned: a password that landed has landed, whatever the administrator did next, and
    /// dropping the record would leave them unable to find out.
    /// </summary>
    [Test]
    public async Task SetPasswordOnAccountsAsync_WhenCancelledPartWay_ReportsWhatWasAlreadyDoneAsync()
    {
        using var cancellation = new CancellationTokenSource();
        _connectors[1].OnSet = () => cancellation.Cancel();

        var result = await SetOnAsync(cancellationToken: cancellation.Token);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Outcomes, Has.Count.EqualTo(1), "the accounts after the cancellation are not attempted");
            Assert.That(result.Outcomes[0].ConnectedSystemName, Is.EqualTo("Contoso AD"));
            Assert.That(_connectors[2].PasswordsSet, Is.Empty);
        }
    }

    #endregion

    #region the audit trail

    /// <summary>
    /// One Activity per account, plus a parent tying them together. Each account is a separate change to a
    /// separate account and has to be auditable as one; the parent is what makes the fan-out findable as a
    /// single administrator action afterwards.
    /// </summary>
    [Test]
    public async Task SetPasswordOnAccountsAsync_WithSeveralAccounts_RecordsOneActivityEachUnderAParentAsync()
    {
        await SetOnAsync();

        var activities = CreatedActivities();
        var parent = activities.Single(a => a.TargetType == ActivityTargetType.MetaverseObject);
        var children = activities.Where(a => a.TargetType == ActivityTargetType.ConnectedSystemObject).ToList();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(children, Has.Count.EqualTo(3));
            Assert.That(children.Select(c => c.ParentActivityId), Is.All.EqualTo(parent.Id));
            Assert.That(parent.MetaverseObjectId, Is.EqualTo(_metaverseObjectId));
            Assert.That(parent.TargetOperationType, Is.EqualTo(ActivityTargetOperationType.SetPassword));
        }
    }

    /// <summary>
    /// A group of one is a row in the Activity list that says nothing and pushes down the row that does.
    /// </summary>
    [Test]
    public async Task SetPasswordOnAccountsAsync_WithOneAccount_RecordsNoParentActivityAsync()
    {
        await SetOnAsync([_accounts[0]]);

        var activities = CreatedActivities();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(activities, Has.Count.EqualTo(1));
            Assert.That(activities[0].TargetType, Is.EqualTo(ActivityTargetType.ConnectedSystemObject));
            Assert.That(activities[0].ParentActivityId, Is.Null);
        }
    }

    /// <summary>
    /// The parent must not read as a success when an account was left without the password the administrator
    /// asked for, and it has to name which.
    /// </summary>
    [Test]
    public async Task SetPasswordOnAccountsAsync_WhenAnAccountRefuses_FailsTheParentNamingItAsync()
    {
        _connectors[2].Result = PasswordSetResult.Failed(PasswordSetFailureReason.PolicyRejection, "Refused.");

        await SetOnAsync();

        var parent = CreatedActivities().Single(a => a.TargetType == ActivityTargetType.MetaverseObject);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(parent.Status, Is.EqualTo(ActivityStatus.FailedWithError));
            Assert.That(parent.ErrorMessage, Does.Contain("Fabrikam HR"));
            Assert.That(parent.ErrorMessage, Does.Contain("2 of 3"));
        }
    }

    [Test]
    public async Task SetPasswordOnAccountsAsync_WhenEveryAccountTakesIt_CompletesTheParentAsync()
    {
        await SetOnAsync();

        var parent = CreatedActivities().Single(a => a.TargetType == ActivityTargetType.MetaverseObject);
        Assert.That(parent.Status, Is.EqualTo(ActivityStatus.Complete));
    }

    /// <summary>
    /// The rule the whole feature is built around, asserted across the fan-out's own audit records as well as
    /// the per-account ones.
    /// </summary>
    [Test]
    public async Task SetPasswordOnAccountsAsync_RecordsNoPasswordValueOnAnyActivityAsync()
    {
        _connectors[2].Result = PasswordSetResult.Failed(PasswordSetFailureReason.PolicyRejection, "Refused.");

        await SetOnAsync();

        var text = CreatedActivities()
            .SelectMany(a => new[] { a.TargetName, a.Message, a.ErrorMessage, a.ErrorStackTrace })
            .Where(value => value != null)
            .Select(value => value!);
        Assert.That(text, Has.None.Contains(Password));
    }

    #endregion

    #region guards

    [Test]
    public void SetPasswordOnAccountsAsync_WithNoAccounts_Throws()
    {
        Assert.ThrowsAsync<ArgumentException>(async () => await SetOnAsync([]));
    }

    #endregion

    /// <summary>
    /// Hands each Connected System its own Connector, so a test can make one refuse and assert that the others
    /// were still asked. Keyed by the Connector Definition's name, which is the only thing the factory is told,
    /// and which each system in this fixture makes unique to itself.
    /// </summary>
    private sealed class PerSystemConnectorFactory(Dictionary<int, RecordingPasswordConnector> connectors) : IConnectorFactory
    {
        public IConnector Create(string connectorName, ICredentialProtection? credentialProtection = null, ICertificateProvider? certificateProvider = null) =>
            connectors[int.Parse(connectorName.Split(' ')[1])];
    }

    private sealed class RecordingPasswordConnector : IConnector, IConnectorPasswordManagement
    {
        public string Name => "Recording Password Connector";
        public string? Description => null;
        public string? Url => null;

        public List<string> PasswordsSet { get; } = [];
        public Action? OnSet { get; set; }
        public PasswordSetResult Result { get; set; } = PasswordSetResult.Succeeded(PasswordExpiryBehaviour.RequireChangeAtNextSignIn);

        public IReadOnlyCollection<PasswordExpiryBehaviour> SupportedExpiryBehaviours =>
            [PasswordExpiryBehaviour.RequireChangeAtNextSignIn];

        /// <summary>
        /// This double stands in for an ordinary, properly configured target, so its channel is secure.
        /// </summary>
        public bool IsPasswordChannelSecure => true;

        public void OpenPasswordConnection(IList<ConnectedSystemSettingValue> settings)
        {
        }

        public Task<PasswordSetResult> SetPasswordAsync(ConnectedSystemObject target, string password, PasswordSetOptions options, CancellationToken cancellationToken)
        {
            PasswordsSet.Add(password);
            OnSet?.Invoke();
            return Task.FromResult(Result);
        }

        public void ClosePasswordConnection()
        {
        }

        public Task<PasswordPreflightResult> RunPasswordPreflightAsync(List<ConnectedSystemSettingValue> settings, IReadOnlyList<string> containerExternalIds, ILogger logger, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
