// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application;
using JIM.Data;
using JIM.Data.Repositories;
using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Logic;
using JIM.Models.Staging;
using Moq;
using NUnit.Framework;

namespace JIM.Worker.Tests.Servers;

/// <summary>
/// Saving a Synchronisation Rule is what releases the initial passwords parked against it (#1221).
/// <para>
/// Parking a policy rejection deliberately stops the retry loop, on the understanding that an administrator
/// changing the settings the target objected to is what makes another attempt worth making. This is the wiring
/// that turns their save into that event. Without it parking is a one-way door: nothing else in JIM moves a
/// record out of Parked, and the delivery pass will not look at one.
/// </para>
/// </summary>
[TestFixture]
public class InitialPasswordReleaseOnSaveTests
{
    private const int SyncRuleId = 12;

    private Mock<IRepository> _mockRepository = null!;
    private Mock<IConnectedSystemRepository> _mockCsRepo = null!;
    private Mock<ISyncRepository> _mockSyncRepo = null!;
    private JimApplication _jim = null!;
    private MetaverseObject _initiatedBy = null!;

    [SetUp]
    public void SetUp()
    {
        TestUtilities.SetEnvironmentVariables();

        _mockRepository = new Mock<IRepository>();
        _mockCsRepo = new Mock<IConnectedSystemRepository>();
        _mockSyncRepo = new Mock<ISyncRepository>();
        var mockMvRepo = new Mock<IMetaverseRepository>();
        var mockActivityRepo = new Mock<IActivityRepository>();

        _mockRepository.Setup(r => r.ConnectedSystems).Returns(_mockCsRepo.Object);
        _mockRepository.Setup(r => r.Metaverse).Returns(mockMvRepo.Object);
        _mockRepository.Setup(r => r.Activity).Returns(mockActivityRepo.Object);
        _mockRepository.Setup(r => r.Sync).Returns(_mockSyncRepo.Object);

        mockActivityRepo.Setup(r => r.CreateActivityAsync(It.IsAny<Activity>())).Returns(Task.CompletedTask);
        mockActivityRepo.Setup(r => r.UpdateActivityAsync(It.IsAny<Activity>())).Returns(Task.CompletedTask);

        _mockCsRepo.Setup(r => r.UpdateSyncRuleAsync(It.IsAny<SyncRule>())).Returns(Task.CompletedTask);
        // Saving a Synchronisation Rule reconciles its target attributes' priority order (#1199), which reads the
        // mappings' persisted targets. A loose mock returns null for the dictionary and the reconcile throws;
        // production code is deliberately not null-guarded, because a null there would be a repository contract
        // violation that should fail loudly rather than be swallowed.
        _mockCsRepo.Setup(r => r.GetImportMappingTargetMetaverseAttributesAsync(It.IsAny<int>()))
            .ReturnsAsync(new Dictionary<int, int>());
        _mockSyncRepo.Setup(r => r.ReleaseParkedInitialPasswordsAsync(It.IsAny<int>())).ReturnsAsync(0);

        _initiatedBy = TestUtilities.GetInitiatedBy();
        // Passed explicitly because JimApplication takes the sync repository as its own argument rather than
        // reading IRepository.Sync, exactly as every host does (JIM.Web, JIM.Worker and JIM.Scheduler all pass
        // it). Omitting it here would leave the release path with a null repository and fail for a reason that
        // cannot happen in production.
        _jim = new JimApplication(_mockRepository.Object, syncRepository: _mockSyncRepo.Object);
    }

    [TearDown]
    public void TearDown() => _jim?.Dispose();

    [Test]
    public async Task CreateOrUpdateSyncRuleAsync_ReEnablingARule_ClearsTheDisabledReasonAsync()
    {
        // The reason describes why the rule is off (#1485); re-enabled, it would be a stale claim about a
        // state that no longer holds, so every save of an enabled rule clears it.
        var rule = Rule(initialPassword: null, direction: SyncRuleDirection.Import, provisions: null);
        rule.Enabled = true;
        rule.DisabledReason = "Object Type 'computer' is no longer reported by the Connected System.";

        await _jim.ConnectedSystems.CreateOrUpdateSyncRuleAsync(rule, _initiatedBy);

        Assert.That(rule.DisabledReason, Is.Null);
    }

    private static SyncRuleInitialPassword Configuration(int length = 16) => new()
    {
        SyncRuleId = SyncRuleId,
        Enabled = true,
        Source = InitialPasswordSource.Custom,
        ExpiryBehaviour = PasswordExpiryBehaviour.RequireChangeAtNextSignIn,
        EnableAccount = true,
        CustomPolicy = new PasswordGenerationPolicy { Length = length }
    };

    private static SyncRule Rule(
        SyncRuleInitialPassword? initialPassword,
        SyncRuleDirection direction = SyncRuleDirection.Export,
        bool? provisions = true) => new()
    {
        Id = SyncRuleId,
        Name = "Provision Users",
        Direction = direction,
        ConnectedSystemId = 1,
        ConnectedSystem = new ConnectedSystem { Id = 1, Name = "Yellowstone Directory" },
        MetaverseObjectType = new MetaverseObjectType { Id = 1, Name = "person" },
        ConnectedSystemObjectType = new ConnectedSystemObjectType { Id = 1, Name = "user" },
        ProvisionToConnectedSystem = provisions,
        InitialPassword = initialPassword
    };

    private async Task SaveAsync(SyncRuleInitialPassword? stored, SyncRuleInitialPassword? saving)
    {
        _mockCsRepo.Setup(r => r.GetSyncRuleInitialPasswordAsync(SyncRuleId)).ReturnsAsync(stored);

        var saved = await _jim.ConnectedSystems.CreateOrUpdateSyncRuleAsync(Rule(saving), _initiatedBy);

        Assert.That(saved, Is.True, "precondition: the Synchronisation Rule must have saved");
    }

    /// <summary>
    /// The case the whole feature exists for. A target refused the generated password, the administrator
    /// lengthens it, and their fix has to reach the accounts that refusal parked.
    /// </summary>
    [Test]
    public async Task CreateOrUpdateSyncRuleAsync_WhenTheGeneratorSettingsChange_ReleasesTheParkedAccountsAsync()
    {
        await SaveAsync(stored: Configuration(), saving: Configuration(length: 24));

        _mockSyncRepo.Verify(r => r.ReleaseParkedInitialPasswordsAsync(SyncRuleId), Times.Once);
    }

    /// <summary>
    /// Editing something else on the rule must not set parked accounts retrying. The target has already given
    /// its answer on these settings, so the retry fails identically, and it inflates an attempt count that is
    /// meant to say how many distinct configurations have been tried.
    /// </summary>
    [Test]
    public async Task CreateOrUpdateSyncRuleAsync_WhenTheConfigurationIsUnchanged_LeavesTheParkedAccountsParkedAsync()
    {
        await SaveAsync(stored: Configuration(), saving: Configuration());

        _mockSyncRepo.Verify(r => r.ReleaseParkedInitialPasswordsAsync(It.IsAny<int>()), Times.Never);
    }

    /// <summary>
    /// Turning initial passwords on for a rule that previously set none is a change of delivery, and the rule
    /// may already have parked accounts from an earlier period when it was configured.
    /// </summary>
    [Test]
    public async Task CreateOrUpdateSyncRuleAsync_WhenInitialPasswordsAreConfiguredForTheFirstTime_ReleasesTheParkedAccountsAsync()
    {
        await SaveAsync(stored: null, saving: Configuration());

        _mockSyncRepo.Verify(r => r.ReleaseParkedInitialPasswordsAsync(SyncRuleId), Times.Once);
    }

    /// <summary>
    /// A rule that sets no initial passwords, and still sets none, has nothing to release however often it is
    /// saved. Releasing here would cost a database write on every save of every rule in the system.
    /// </summary>
    [Test]
    public async Task CreateOrUpdateSyncRuleAsync_ForARuleThatSetsNoInitialPasswords_DoesNotReleaseAnythingAsync()
    {
        await SaveAsync(stored: null, saving: null);

        _mockSyncRepo.Verify(r => r.ReleaseParkedInitialPasswordsAsync(It.IsAny<int>()), Times.Never);
    }

    #region an initial password only survives on a rule that can deliver one

    /// <summary>
    /// Only a newly created account has never had a password, so an initial password is meaningless on a rule
    /// that creates none. Switching provisioning off therefore switches the initial password off with it,
    /// rather than leaving a setting that reads as configured and can never run.
    /// <para>
    /// This is the save path's half of the rule the REST API states by refusing an enabled initial-password
    /// configuration on such a Synchronisation Rule outright. It cannot refuse here: the administrator is
    /// saving a whole rule and has not asked about passwords at all, and the portal removes the tab the moment
    /// provisioning goes off, so there would be nothing on screen to correct.
    /// </para>
    /// </summary>
    [Test]
    public async Task CreateOrUpdateSyncRuleAsync_WhenTheRuleNoLongerProvisions_SwitchesTheInitialPasswordOffAsync()
    {
        var configuration = Configuration();
        var rule = Rule(configuration, provisions: false);

        var saved = await _jim.ConnectedSystems.CreateOrUpdateSyncRuleAsync(rule, _initiatedBy);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(saved, Is.True, "the rest of the rule is perfectly savable");
            Assert.That(configuration.Enabled, Is.False);
        }
    }

    /// <summary>
    /// The same for an Import rule, which provisions nothing into the Connected System by definition. An
    /// initial password could only ever have arrived here by the rule's direction being changed after it was
    /// configured.
    /// </summary>
    [Test]
    public async Task CreateOrUpdateSyncRuleAsync_WhenTheRuleImports_SwitchesTheInitialPasswordOffAsync()
    {
        var configuration = Configuration();
        var rule = Rule(configuration, direction: SyncRuleDirection.Import, provisions: null);

        await _jim.ConnectedSystems.CreateOrUpdateSyncRuleAsync(rule, _initiatedBy);

        Assert.That(configuration.Enabled, Is.False);
    }

    /// <summary>
    /// The guard against over-reach: the rule that can deliver an initial password keeps the one it was given.
    /// </summary>
    [Test]
    public async Task CreateOrUpdateSyncRuleAsync_WhenTheRuleProvisions_LeavesTheInitialPasswordOnAsync()
    {
        var configuration = Configuration();

        await _jim.ConnectedSystems.CreateOrUpdateSyncRuleAsync(Rule(configuration), _initiatedBy);

        Assert.That(configuration.Enabled, Is.True);
    }

    /// <summary>
    /// Switching it off is a change to what the rule delivers, so the accounts parked waiting on these settings
    /// stop waiting. The delivery pass finds the rule no longer asks for a password and retires their records,
    /// rather than leaving them holding a needs-attention marker over work nobody is going to do.
    /// </summary>
    [Test]
    public async Task CreateOrUpdateSyncRuleAsync_WhenTheRuleNoLongerProvisions_ReleasesTheParkedAccountsAsync()
    {
        _mockCsRepo.Setup(r => r.GetSyncRuleInitialPasswordAsync(SyncRuleId)).ReturnsAsync(Configuration());

        await _jim.ConnectedSystems.CreateOrUpdateSyncRuleAsync(Rule(Configuration(), provisions: false), _initiatedBy);

        _mockSyncRepo.Verify(r => r.ReleaseParkedInitialPasswordsAsync(SyncRuleId), Times.Once);
    }

    #endregion
}
