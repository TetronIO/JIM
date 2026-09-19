// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Reflection;
using JIM.Application;
using JIM.Connectors;
using JIM.Data;
using JIM.Data.Repositories;
using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Interfaces;
using JIM.Models.Security;
using JIM.Models.Staging;
using Moq;
using NUnit.Framework;
using Serilog;

namespace JIM.Worker.Tests.Servers;

/// <summary>
/// How a discovered password policy is recorded against a Connected System during schema import, and how the
/// Set Password dialog's "can this Connector discover a policy?" answer is derived from it.
/// <para>
/// A Connector may now return a policy row that carries no constraints at all, purely to say why nothing was
/// read (the directory publishes nothing, or the service account cannot see the configuration). Such a row is
/// persisted, because the reason is the useful part, but must not be reported as a discovery.
/// </para>
/// </summary>
[TestFixture]
public class ConnectedSystemPasswordPolicyDiscoveryTests
{
    private const int ConnectedSystemId = 1;

    private Mock<IRepository> _repository = null!;
    private Mock<IActivityRepository> _activityRepository = null!;
    private Mock<IConnectedSystemRepository> _connectedSystemRepository = null!;
    private Mock<IConnector> _connector = null!;
    private Mock<IConnectorPasswordPolicyDiscovery> _policyDiscovery = null!;
    private Mock<IConnectorPasswordManagement> _passwordManagement = null!;
    private JimApplication _jim = null!;

    [SetUp]
    public void SetUp()
    {
        TestUtilities.SetEnvironmentVariables();

        _repository = new Mock<IRepository>();
        _activityRepository = new Mock<IActivityRepository>();
        _connectedSystemRepository = new Mock<IConnectedSystemRepository>();
        _repository.Setup(r => r.Activity).Returns(_activityRepository.Object);
        _repository.Setup(r => r.ConnectedSystems).Returns(_connectedSystemRepository.Object);
        _activityRepository.Setup(r => r.CreateActivityAsync(It.IsAny<Activity>())).Returns(Task.CompletedTask);
        _activityRepository.Setup(r => r.UpdateActivityAsync(It.IsAny<Activity>())).Returns(Task.CompletedTask);
        _connectedSystemRepository.Setup(r => r.UpdateConnectedSystemSchemaAsync(It.IsAny<ConnectedSystem>())).Returns(Task.CompletedTask);

        _connector = new Mock<IConnector>();
        _connector.Setup(c => c.Name).Returns("Stub Policy Connector");
        _connector.As<IConnectorSchema>()
            .Setup(c => c.GetSchemaAsync(It.IsAny<List<ConnectedSystemSettingValue>>(), It.IsAny<ILogger>()))
            .ReturnsAsync(CreateSchema());
        // Every facet is declared before the proxy is materialised; Moq refuses an As<T>() after .Object.
        _policyDiscovery = _connector.As<IConnectorPasswordPolicyDiscovery>();
        _passwordManagement = _connector.As<IConnectorPasswordManagement>();

        _jim = new JimApplication(_repository.Object, connectorFactory: new StubConnectorFactory(_connector.Object));
    }

    [TearDown]
    public void TearDown() => _jim?.Dispose();

    #region Schema import persistence

    [Test]
    public async Task ImportConnectedSystemSchemaAsync_PolicyWithConstraints_ReportsPolicyDiscoveredAsync()
    {
        DiscoverPolicy(new ConnectedSystemPasswordPolicy { MinimumLength = 12 });
        var connectedSystem = CreateConnectedSystem();

        var result = await _jim.ConnectedSystems.ImportConnectedSystemSchemaAsync(connectedSystem, NewInitiator());

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.PasswordPolicyDiscovered, Is.True);
            Assert.That(connectedSystem.PasswordPolicy?.MinimumLength, Is.EqualTo(12));
        }
    }

    /// <summary>
    /// A row with no constraints exists to carry its outcome, not to announce a discovery. Reporting it as one
    /// would tell the administrator JIM read a policy when it read nothing.
    /// </summary>
    [Test]
    public async Task ImportConnectedSystemSchemaAsync_PolicyWithNoConstraints_RecordsTheRowWithoutReportingADiscoveryAsync()
    {
        DiscoverPolicy(new ConnectedSystemPasswordPolicy
        {
            DiscoveryOutcome = PasswordPolicyDiscoveryOutcome.NotPublished,
            PolicyOverrideSignal = PolicyOverrideSignal.Absent
        });
        var connectedSystem = CreateConnectedSystem();

        var result = await _jim.ConnectedSystems.ImportConnectedSystemSchemaAsync(connectedSystem, NewInitiator());

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.PasswordPolicyDiscovered, Is.False);
            Assert.That(connectedSystem.PasswordPolicy, Is.Not.Null, "the outcome is the useful part and must be kept");
            Assert.That(connectedSystem.PasswordPolicy!.DiscoveryOutcome, Is.EqualTo(PasswordPolicyDiscoveryOutcome.NotPublished));
        }
    }

    [Test]
    public async Task ImportConnectedSystemSchemaAsync_ExistingRowAndNewOutcomeFields_CopiesThemOntoTheRowAsync()
    {
        DiscoverPolicy(new ConnectedSystemPasswordPolicy
        {
            MinimumLength = 10,
            PolicyOverrideSignal = PolicyOverrideSignal.Present,
            FurtherChecksApply = true,
            DiscoveryOutcome = PasswordPolicyDiscoveryOutcome.ConfigurationNotReadable
        });
        var connectedSystem = CreateConnectedSystem();
        var existing = new ConnectedSystemPasswordPolicy { Id = 5, ConnectedSystemId = ConnectedSystemId };
        connectedSystem.PasswordPolicy = existing;

        await _jim.ConnectedSystems.ImportConnectedSystemSchemaAsync(connectedSystem, NewInitiator());

        using (Assert.EnterMultipleScope())
        {
            Assert.That(connectedSystem.PasswordPolicy, Is.SameAs(existing), "the persisted row is updated in place, never replaced");
            Assert.That(existing.PolicyOverrideSignal, Is.EqualTo(PolicyOverrideSignal.Present));
            Assert.That(existing.FurtherChecksApply, Is.True);
            Assert.That(existing.DiscoveryOutcome, Is.EqualTo(PasswordPolicyDiscoveryOutcome.ConfigurationNotReadable));
        }
    }

    /// <summary>
    /// The guard that keeps the field-by-field copy in <c>DiscoverPasswordPolicyAsync</c> honest. It sets every
    /// settable property of the model to a value that differs from a fresh row's, runs the import against an
    /// existing row, and asserts each one arrived. Adding a property to the model without extending the copy
    /// therefore fails here naming the property, instead of silently keeping the stale value on every refresh.
    /// </summary>
    [Test]
    public async Task ImportConnectedSystemSchemaAsync_ExistingRow_CopiesEverySettablePropertyOfThePolicyAsync()
    {
        var copiedProperties = CopiedPolicyProperties();
        Assert.That(copiedProperties, Is.Not.Empty, "expected the model to declare copyable properties");

        var baseline = new ConnectedSystemPasswordPolicy();
        var discovered = new ConnectedSystemPasswordPolicy();
        for (var i = 0; i < copiedProperties.Count; i++)
            copiedProperties[i].SetValue(discovered, DistinctValueFor(copiedProperties[i], baseline, i));
        DiscoverPolicy(discovered);

        var connectedSystem = CreateConnectedSystem();
        var existing = new ConnectedSystemPasswordPolicy { Id = 5, ConnectedSystemId = ConnectedSystemId };
        connectedSystem.PasswordPolicy = existing;

        await _jim.ConnectedSystems.ImportConnectedSystemSchemaAsync(connectedSystem, NewInitiator());

        var notCopied = copiedProperties
            .Where(p => !Equals(p.GetValue(existing), p.GetValue(discovered)))
            .Select(p => p.Name)
            .ToList();

        Assert.That(notCopied, Is.Empty,
            "ConnectedSystemPasswordPolicy propert(ies) not copied onto an existing row by " +
            "ConnectedSystemServer.DiscoverPasswordPolicyAsync: " + string.Join(", ", notCopied) +
            ". Extend the copy block there, or exclude the property here with a reason.");
    }

    #endregion

    #region Accounts for a password set

    [Test]
    public async Task GetAccountsForPasswordSetAsync_PolicyOutcomeIsNotPublished_ReportsTheConnectorCannotDiscoverAPolicyAsync()
    {
        var account = await GetAccountAsync(new ConnectedSystemPasswordPolicy
        {
            ConnectedSystemId = ConnectedSystemId,
            DiscoveryOutcome = PasswordPolicyDiscoveryOutcome.NotPublished
        });

        Assert.That(account.ConnectorCanDiscoverPasswordPolicy, Is.False,
            "a directory that publishes nothing has no policy to refresh, and the dialog must not send the administrator to refresh the schema");
    }

    [Test]
    public async Task GetAccountsForPasswordSetAsync_PolicyOutcomeIsRead_ReportsTheConnectorCanDiscoverAPolicyAsync()
    {
        var account = await GetAccountAsync(new ConnectedSystemPasswordPolicy
        {
            ConnectedSystemId = ConnectedSystemId,
            MinimumLength = 8,
            DiscoveryOutcome = PasswordPolicyDiscoveryOutcome.Read
        });

        Assert.That(account.ConnectorCanDiscoverPasswordPolicy, Is.True);
    }

    /// <summary>
    /// No row yet means the schema has never been refreshed with this Connector, which is the "refresh the
    /// schema" case the dialog draws; the Connector's own capability is the only thing that can answer it.
    /// </summary>
    [Test]
    public async Task GetAccountsForPasswordSetAsync_NoPolicyRowAndConnectorCanDiscover_ReportsTheConnectorCanDiscoverAPolicyAsync()
    {
        var account = await GetAccountAsync(policy: null);

        Assert.That(account.ConnectorCanDiscoverPasswordPolicy, Is.True);
    }

    [Test]
    public async Task GetAccountsForPasswordSetAsync_ConnectorCannotDiscover_ReportsSoWhateverTheRowSaysAsync()
    {
        var account = await GetAccountAsync(
            new ConnectedSystemPasswordPolicy { ConnectedSystemId = ConnectedSystemId, MinimumLength = 8 },
            connectorSupportsDiscovery: false);

        Assert.That(account.ConnectorCanDiscoverPasswordPolicy, Is.False);
    }

    private async Task<MetaverseObjectAccount> GetAccountAsync(ConnectedSystemPasswordPolicy? policy, bool connectorSupportsDiscovery = true)
    {
        var metaverseObjectId = Guid.NewGuid();
        _connectedSystemRepository.Setup(r => r.GetConnectedSystemObjectsByMetaverseObjectIdAsync(metaverseObjectId))
            .ReturnsAsync([new ConnectedSystemObject { Id = Guid.NewGuid(), ConnectedSystemId = ConnectedSystemId }]);
        _connectedSystemRepository.Setup(r => r.GetConnectedSystemCoreAsync(ConnectedSystemId, false))
            .ReturnsAsync(new ConnectedSystem
            {
                Id = ConnectedSystemId,
                Name = "Yellowstone Directory",
                ConnectorDefinition = new ConnectorDefinition { Name = "Stub Policy Connector", SupportsPasswordPolicyDiscovery = connectorSupportsDiscovery }
            });
        _connectedSystemRepository.Setup(r => r.GetPasswordPolicyAsync(ConnectedSystemId)).ReturnsAsync(policy);
        _passwordManagement
            .Setup(c => c.SupportedExpiryBehaviours)
            .Returns([PasswordExpiryBehaviour.RequireChangeAtNextSignIn]);

        var accounts = await _jim.ConnectedSystems.GetAccountsForPasswordSetAsync(metaverseObjectId);

        Assert.That(accounts, Has.Count.EqualTo(1));
        return accounts[0];
    }

    #endregion

    #region Helpers

    private void DiscoverPolicy(ConnectedSystemPasswordPolicy? policy)
    {
        _policyDiscovery
            .Setup(c => c.GetPasswordPolicyAsync(It.IsAny<List<ConnectedSystemSettingValue>>(), It.IsAny<ILogger>()))
            .ReturnsAsync(policy);
    }

    /// <summary>
    /// Every public settable property the copy must carry: the key, the foreign key and the owning navigation
    /// identify the row and are the only things the copy is right to leave alone.
    /// </summary>
    private static List<PropertyInfo> CopiedPolicyProperties()
    {
        var identity = new[]
        {
            nameof(ConnectedSystemPasswordPolicy.Id),
            nameof(ConnectedSystemPasswordPolicy.ConnectedSystemId),
            nameof(ConnectedSystemPasswordPolicy.ConnectedSystem)
        };

        return typeof(ConnectedSystemPasswordPolicy)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanWrite && p.SetMethod?.IsPublic == true && !identity.Contains(p.Name))
            .OrderBy(p => p.Name, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// A value for the property that differs from what a fresh row holds, so a copy that never happened is
    /// visible as a difference. A property of a type this cannot produce fails the test naming it, which is the
    /// prompt to extend this helper alongside the model.
    /// </summary>
    private static object DistinctValueFor(PropertyInfo property, ConnectedSystemPasswordPolicy baseline, int seed)
    {
        var type = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;
        var current = property.GetValue(baseline);

        if (type == typeof(int))
            return ((int?)current ?? 0) + 7 + seed;
        if (type == typeof(bool))
            return !((bool?)current ?? false);
        if (type == typeof(TimeSpan))
            return ((TimeSpan?)current ?? TimeSpan.Zero) + TimeSpan.FromDays(3 + seed);
        if (type == typeof(DateTime))
            return new DateTime(2030, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddDays(seed);
        if (type.IsEnum)
            return Enum.GetValues(type).Cast<object>().First(v => !Equals(v, current));

        Assert.Fail($"No distinct value generator for {property.Name} of type {property.PropertyType.Name}; extend DistinctValueFor.");
        return null!;
    }

    private static MetaverseObject NewInitiator() => new()
    {
        Id = Guid.NewGuid(),
        CachedDisplayName = "Test Administrator"
    };

    private static ConnectorSchema CreateSchema()
    {
        var userName = new ConnectorSchemaAttribute("userName", AttributeDataType.Text, AttributePlurality.SingleValued);
        var objectType = new ConnectorSchemaObjectType("User") { RecommendedExternalIdAttribute = userName };
        objectType.Attributes.Add(userName);
        return new ConnectorSchema { ObjectTypes = [objectType] };
    }

    private static ConnectedSystem CreateConnectedSystem()
    {
        var connectorDefinition = new ConnectorDefinition { Name = "Stub Policy Connector", SupportsPasswordPolicyDiscovery = true };
        var setting = new ConnectorDefinitionSetting { Name = "Dummy Setting", Type = ConnectedSystemSettingType.Text };
        connectorDefinition.Settings.Add(setting);

        return new ConnectedSystem
        {
            Id = ConnectedSystemId,
            Name = "Yellowstone Directory",
            ConnectorDefinition = connectorDefinition,
            SettingValues = [new ConnectedSystemSettingValue { Setting = setting, StringValue = "value" }]
        };
    }

    /// <summary>
    /// Resolves every connector name to the supplied connector, so the server under test exercises its own
    /// policy handling rather than a real Connector's.
    /// </summary>
    private sealed class StubConnectorFactory(IConnector connector) : IConnectorFactory
    {
        public IConnector Create(string connectorName, ICredentialProtection? credentialProtection = null, ICertificateProvider? certificateProvider = null) => connector;
    }

    #endregion
}
