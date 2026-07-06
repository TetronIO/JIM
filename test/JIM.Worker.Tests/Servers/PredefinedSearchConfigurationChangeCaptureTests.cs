// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application;
using JIM.Application.Interfaces;
using JIM.Data;
using JIM.Data.Repositories;
using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Search;
using JIM.Models.Security;
using Moq;
using NUnit.Framework;
using System.Text;

namespace JIM.Worker.Tests.Servers;

/// <summary>
/// Tests configuration change-history capture for Predefined Searches: the search's own definition, its criteria
/// groups (including nested groups) and its criteria all roll up into a single versioned, metadata-only snapshot
/// keyed by <see cref="Activity.PredefinedSearchId"/>, mirroring <c>RoleConfigurationChangeCaptureTests</c> and
/// <c>ApiKeyConfigurationChangeCaptureTests</c>. A criteria group/criterion mutation resolves its owning search via
/// <see cref="ISearchRepository.GetOwningPredefinedSearchIdForGroupAsync"/> /
/// <see cref="ISearchRepository.GetOwningPredefinedSearchIdForCriterionAsync"/>, both mocked directly here: the
/// repository's own parent-chain walk is exercised by its own (non-Worker-layer) tests, so these tests instead prove
/// that the Server layer correctly threads the resolved owning id onto the Activity.
/// </summary>
[TestFixture]
public class PredefinedSearchConfigurationChangeCaptureTests
{
    private const int SearchId = 42;
    private const int GroupId = 7;
    private const int CriterionId = 100;
    private const int MetaverseAttributeId = 3;

    private Mock<IRepository> _repo = null!;
    private Mock<IActivityRepository> _activityRepo = null!;
    private Mock<IServiceSettingsRepository> _settingsRepo = null!;
    private Mock<ISearchRepository> _searchRepo = null!;
    private FakeProtection _protection = null!;
    private JimApplication _jim = null!;
    private Activity? _completedActivity;
    private List<Activity> _createdActivities = null!;

    [SetUp]
    public void SetUp()
    {
        TestUtilities.SetEnvironmentVariables();

        _repo = new Mock<IRepository>();
        _activityRepo = new Mock<IActivityRepository>();
        _settingsRepo = new Mock<IServiceSettingsRepository>();
        _searchRepo = new Mock<ISearchRepository>();
        _repo.Setup(r => r.Activity).Returns(_activityRepo.Object);
        _repo.Setup(r => r.ServiceSettings).Returns(_settingsRepo.Object);
        _repo.Setup(r => r.Search).Returns(_searchRepo.Object);

        _createdActivities = new List<Activity>();
        _activityRepo.Setup(r => r.CreateActivityAsync(It.IsAny<Activity>()))
            .Callback<Activity>(a => _createdActivities.Add(a))
            .Returns(Task.CompletedTask);
        _activityRepo.Setup(r => r.UpdateActivityAsync(It.IsAny<Activity>()))
            .Callback<Activity>(a => _completedActivity = a)
            .Returns(Task.CompletedTask);

        _protection = new FakeProtection();
        _jim = new JimApplication(_repo.Object) { CredentialProtection = _protection };

        // NUnit reuses one fixture instance across every [Test] by default, so a value left over from a previous
        // test would otherwise leak in here; explicit reset keeps assertions reliable regardless of execution order.
        _completedActivity = null;

        SetupTrackingSetting(enabled: true);
        SetupHashKeySetting();
    }

    [TearDown]
    public void TearDown() => _jim?.Dispose();

    // -- UpdatePredefinedSearchAsync ---------------------------------------------------------------------------------

    [Test]
    public async Task UpdatePredefinedSearchAsync_UserInitiated_RecordsUpdateActivityWithVersionOneSnapshotAsync()
    {
        var search = BuildSearch(id: SearchId, name: "All Permanent Staff");
        _searchRepo.Setup(r => r.UpdatePredefinedSearchAsync(It.IsAny<PredefinedSearch>())).Returns(Task.CompletedTask);
        _searchRepo.Setup(r => r.GetPredefinedSearchAsync(SearchId)).ReturnsAsync(search);
        SetupMaxVersion(0);

        await _jim.Search.UpdatePredefinedSearchAsync(search, NewUser(), changeReason: "renamed search");

        Assert.That(_completedActivity, Is.Not.Null);
        Assert.That(_completedActivity!.TargetType, Is.EqualTo(ActivityTargetType.PredefinedSearch));
        Assert.That(_completedActivity!.TargetOperationType, Is.EqualTo(ActivityTargetOperationType.Update));
        Assert.That(_completedActivity!.InitiatedByType, Is.EqualTo(ActivityInitiatorType.User));
        Assert.That(_completedActivity!.PredefinedSearchId, Is.EqualTo(SearchId), "the activity must carry the search id so history is queryable");
        Assert.That(_completedActivity!.ConfigurationChangeVersion, Is.EqualTo(1));
        Assert.That(_completedActivity!.ChangeReason, Is.EqualTo("renamed search"));
        var snapshot = _completedActivity!.ConfigurationChangeSnapshot;
        Assert.That(snapshot, Is.Not.Null);
        Assert.That(snapshot, Does.Contain("\"objectType\":\"PredefinedSearch\""));
        Assert.That(snapshot, Does.Contain("All Permanent Staff"));
    }

    [Test]
    public async Task UpdatePredefinedSearchAsync_NoInitiator_RecordsSystemAttributedActivityAsync()
    {
        var search = BuildSearch(id: SearchId, name: "Built-in search");
        _searchRepo.Setup(r => r.UpdatePredefinedSearchAsync(It.IsAny<PredefinedSearch>())).Returns(Task.CompletedTask);
        _searchRepo.Setup(r => r.GetPredefinedSearchAsync(SearchId)).ReturnsAsync(search);
        SetupMaxVersion(0);

        await _jim.Search.UpdatePredefinedSearchAsync(search, changeReason: "seeded default");

        Assert.That(_completedActivity, Is.Not.Null);
        Assert.That(_completedActivity!.InitiatedByType, Is.EqualTo(ActivityInitiatorType.System));
        Assert.That(_completedActivity!.InitiatedByName, Is.EqualTo("System"));
        Assert.That(_completedActivity!.PredefinedSearchId, Is.EqualTo(SearchId));
        Assert.That(_completedActivity!.ConfigurationChangeVersion, Is.EqualTo(1));
    }

    [Test]
    public async Task UpdatePredefinedSearchAsync_ApiKeyInitiated_AttributesActivityToTheInitiatingKeyAsync()
    {
        var search = BuildSearch(id: SearchId, name: "Provisioned search");
        _searchRepo.Setup(r => r.UpdatePredefinedSearchAsync(It.IsAny<PredefinedSearch>())).Returns(Task.CompletedTask);
        _searchRepo.Setup(r => r.GetPredefinedSearchAsync(SearchId)).ReturnsAsync(search);
        SetupMaxVersion(0);

        var initiatingKey = new ApiKey { Id = Guid.NewGuid(), Name = "provisioning-key" };
        await _jim.Search.UpdatePredefinedSearchAsync(search, initiatingKey, changeReason: "provisioned via API");

        Assert.That(_completedActivity, Is.Not.Null);
        Assert.That(_completedActivity!.InitiatedByType, Is.EqualTo(ActivityInitiatorType.ApiKey));
        Assert.That(_completedActivity!.InitiatedById, Is.EqualTo(initiatingKey.Id));
        Assert.That(_completedActivity!.PredefinedSearchId, Is.EqualTo(SearchId));
    }

    [Test]
    public async Task UpdatePredefinedSearchAsync_WhenTrackingDisabled_RecordsActivityButNoSnapshotAsync()
    {
        SetupTrackingSetting(enabled: false);
        var search = BuildSearch(id: SearchId, name: "All Permanent Staff");
        _searchRepo.Setup(r => r.UpdatePredefinedSearchAsync(It.IsAny<PredefinedSearch>())).Returns(Task.CompletedTask);

        await _jim.Search.UpdatePredefinedSearchAsync(search, NewUser(), changeReason: "no tracking");

        Assert.That(_completedActivity, Is.Not.Null);
        Assert.That(_completedActivity!.TargetType, Is.EqualTo(ActivityTargetType.PredefinedSearch));
        Assert.That(_completedActivity!.ConfigurationChangeSnapshot, Is.Null);
        Assert.That(_completedActivity!.ConfigurationChangeVersion, Is.Null);
        Assert.That(_completedActivity!.ChangeReason, Is.EqualTo("no tracking"));
    }

    [Test]
    public async Task UpdatePredefinedSearchAsync_WhenResaveIsUnchanged_SkipsVersionAndSnapshotAsync()
    {
        var search = BuildSearch(id: SearchId, name: "All Permanent Staff");
        _searchRepo.Setup(r => r.UpdatePredefinedSearchAsync(It.IsAny<PredefinedSearch>())).Returns(Task.CompletedTask);
        _searchRepo.Setup(r => r.GetPredefinedSearchAsync(SearchId)).ReturnsAsync(search);
        SetupMaxVersion(0);

        await _jim.Search.UpdatePredefinedSearchAsync(search, NewUser(), changeReason: "first save");
        var storedSnapshot = _completedActivity!.ConfigurationChangeSnapshot;
        Assert.That(storedSnapshot, Is.Not.Null);
        Assert.That(_completedActivity!.ConfigurationChangeVersion, Is.EqualTo(1));
        _activityRepo.Setup(r => r.GetLatestConfigurationChangeSnapshotAsync(ActivityTargetType.PredefinedSearch, SearchId))
            .ReturnsAsync(storedSnapshot);
        _completedActivity = null;

        await _jim.Search.UpdatePredefinedSearchAsync(search, NewUser(), changeReason: "resend, nothing changed");

        Assert.That(_completedActivity, Is.Not.Null);
        Assert.That(_completedActivity!.ConfigurationChangeVersion, Is.Null, "an unchanged save must not consume a version");
        Assert.That(_completedActivity!.ConfigurationChangeSnapshot, Is.Null);
        Assert.That(_completedActivity!.PredefinedSearchId, Is.EqualTo(SearchId), "the activity still deep-links to the search when the capture is skipped");
        Assert.That(_completedActivity!.ChangeReason, Is.EqualTo("resend, nothing changed"));
    }

    // -- CreatePredefinedSearchCriteriaGroupAsync (owning id passed directly) ---------------------------------------

    [Test]
    public async Task CreatePredefinedSearchCriteriaGroupAsync_TopLevelGroup_CapturesAgainstTheSuppliedSearchIdAsync()
    {
        var search = BuildSearch(id: SearchId, name: "All Permanent Staff");
        _searchRepo.Setup(r => r.GetPredefinedSearchCoreAsync(SearchId)).ReturnsAsync(search);
        _searchRepo.Setup(r => r.CreatePredefinedSearchCriteriaGroupAsync(SearchId, null, SearchGroupType.All, 0))
            .ReturnsAsync(new PredefinedSearchCriteriaGroup { Id = GroupId, Type = SearchGroupType.All, Position = 0, PredefinedSearchId = SearchId });
        _searchRepo.Setup(r => r.GetPredefinedSearchAsync(SearchId)).ReturnsAsync(search);
        SetupMaxVersion(0);

        var result = await _jim.Search.CreatePredefinedSearchCriteriaGroupAsync(SearchId, null, SearchGroupType.All, 0, NewUser(), changeReason: "added top-level group");

        Assert.That(result.Id, Is.EqualTo(GroupId));
        Assert.That(_completedActivity, Is.Not.Null);
        Assert.That(_completedActivity!.TargetType, Is.EqualTo(ActivityTargetType.PredefinedSearch));
        Assert.That(_completedActivity!.PredefinedSearchId, Is.EqualTo(SearchId));
        Assert.That(_completedActivity!.ConfigurationChangeVersion, Is.EqualTo(1));
        Assert.That(_completedActivity!.ChangeReason, Is.EqualTo("added top-level group"));
    }

    // -- CreatePredefinedSearchCriterionAsync (owning id resolved via group) ----------------------------------------

    [Test]
    public async Task CreatePredefinedSearchCriterionAsync_ResolvesOwningSearchViaGroupAndCapturesAsync()
    {
        var search = BuildSearch(id: SearchId, name: "All Permanent Staff");
        var criterion = BuildCriterion(id: 0, attributeId: MetaverseAttributeId);
        _searchRepo.Setup(r => r.GetOwningPredefinedSearchIdForGroupAsync(GroupId)).ReturnsAsync(SearchId);
        _searchRepo.Setup(r => r.GetPredefinedSearchCoreAsync(SearchId)).ReturnsAsync(search);
        _searchRepo.Setup(r => r.CreatePredefinedSearchCriterionAsync(GroupId, criterion))
            .ReturnsAsync(() => { criterion.Id = CriterionId; return criterion; });
        _searchRepo.Setup(r => r.GetPredefinedSearchAsync(SearchId)).ReturnsAsync(search);
        SetupMaxVersion(0);

        var result = await _jim.Search.CreatePredefinedSearchCriterionAsync(GroupId, criterion, NewUser(), changeReason: "added criterion");

        Assert.That(result, Is.Not.Null);
        Assert.That(_completedActivity, Is.Not.Null);
        Assert.That(_completedActivity!.PredefinedSearchId, Is.EqualTo(SearchId));
        Assert.That(_completedActivity!.ConfigurationChangeVersion, Is.EqualTo(1));
    }

    // -- UpdatePredefinedSearchCriterionAsync (nested-group rollup) -----------------------------------------------

    [Test]
    public async Task UpdatePredefinedSearchCriterionAsync_OnNestedGroup_RollsUpToTheAncestorSearchIdAsync()
    {
        // The criterion lives several levels deep in the criteria group tree; the repository resolves the walk
        // (its own logic is covered elsewhere), and this test proves the Server layer threads that resolved id
        // straight onto the Activity rather than, say, the immediate (non-owning) group id.
        var search = BuildSearch(id: SearchId, name: "Deeply Nested Search");
        var criterion = BuildCriterion(id: CriterionId, attributeId: MetaverseAttributeId);
        _searchRepo.Setup(r => r.GetOwningPredefinedSearchIdForCriterionAsync(CriterionId)).ReturnsAsync(SearchId);
        _searchRepo.Setup(r => r.GetPredefinedSearchCoreAsync(SearchId)).ReturnsAsync(search);
        _searchRepo.Setup(r => r.UpdatePredefinedSearchCriterionAsync(criterion)).ReturnsAsync(criterion);
        _searchRepo.Setup(r => r.GetPredefinedSearchAsync(SearchId)).ReturnsAsync(search);
        SetupMaxVersion(0);

        var result = await _jim.Search.UpdatePredefinedSearchCriterionAsync(criterion, NewUser(), changeReason: "changed comparison value");

        Assert.That(result, Is.Not.Null);
        Assert.That(_completedActivity, Is.Not.Null);
        Assert.That(_completedActivity!.TargetType, Is.EqualTo(ActivityTargetType.PredefinedSearch));
        Assert.That(_completedActivity!.PredefinedSearchId, Is.EqualTo(SearchId), "the nested criterion's change must roll up to the ancestor search, not any intermediate group");
        Assert.That(_completedActivity!.ConfigurationChangeVersion, Is.EqualTo(1));
    }

    // -- DeletePredefinedSearchCriteriaGroupAsync -------------------------------------------------------------------

    [Test]
    public async Task DeletePredefinedSearchCriteriaGroupAsync_DeletesGroup_CapturesAgainstTheSurvivingOwningSearchAsync()
    {
        var search = BuildSearch(id: SearchId, name: "All Permanent Staff");
        _searchRepo.Setup(r => r.GetOwningPredefinedSearchIdForGroupAsync(GroupId)).ReturnsAsync(SearchId);
        _searchRepo.Setup(r => r.GetPredefinedSearchCoreAsync(SearchId)).ReturnsAsync(search);
        _searchRepo.Setup(r => r.DeletePredefinedSearchCriteriaGroupAsync(GroupId)).ReturnsAsync(true);
        _searchRepo.Setup(r => r.GetPredefinedSearchAsync(SearchId)).ReturnsAsync(search);
        SetupMaxVersion(0);

        var result = await _jim.Search.DeletePredefinedSearchCriteriaGroupAsync(GroupId, NewUser(), changeReason: "removing obsolete group");

        Assert.That(result, Is.True);
        Assert.That(_completedActivity, Is.Not.Null);
        Assert.That(_completedActivity!.TargetType, Is.EqualTo(ActivityTargetType.PredefinedSearch));
        Assert.That(_completedActivity!.PredefinedSearchId, Is.EqualTo(SearchId));
        Assert.That(_completedActivity!.ConfigurationChangeVersion, Is.EqualTo(1));
        Assert.That(_completedActivity!.ChangeReason, Is.EqualTo("removing obsolete group"));
    }

    // -- DeletePredefinedSearchCriterionAsync (not-found path) -------------------------------------------------------

    [Test]
    public async Task DeletePredefinedSearchCriterionAsync_CriterionNotFound_SkipsCaptureAndRecordsNoActivityAsync()
    {
        _searchRepo.Setup(r => r.GetOwningPredefinedSearchIdForCriterionAsync(CriterionId)).ReturnsAsync((int?)null);
        _searchRepo.Setup(r => r.DeletePredefinedSearchCriterionAsync(CriterionId)).ReturnsAsync(false);

        var result = await _jim.Search.DeletePredefinedSearchCriterionAsync(CriterionId, NewUser(), changeReason: "cleanup");

        Assert.That(result, Is.False);
        Assert.That(_completedActivity, Is.Null, "a not-found delete must not record a configuration-change activity");
        _activityRepo.Verify(r => r.CreateActivityAsync(It.IsAny<Activity>()), Times.Never);
    }

    [Test]
    public async Task UpdatePredefinedSearchCriteriaGroupAsync_GroupNotFound_ReturnsNullAndRecordsNoActivityAsync()
    {
        _searchRepo.Setup(r => r.GetOwningPredefinedSearchIdForGroupAsync(GroupId)).ReturnsAsync((int?)null);
        _searchRepo.Setup(r => r.UpdatePredefinedSearchCriteriaGroupAsync(GroupId, SearchGroupType.Any, 1)).ReturnsAsync((PredefinedSearchCriteriaGroup?)null);

        var result = await _jim.Search.UpdatePredefinedSearchCriteriaGroupAsync(GroupId, SearchGroupType.Any, 1, NewUser());

        Assert.That(result, Is.Null);
        Assert.That(_completedActivity, Is.Null);
        _activityRepo.Verify(r => r.CreateActivityAsync(It.IsAny<Activity>()), Times.Never);
    }

    // -- helpers -------------------------------------------------------------------------------------------------------

    private static readonly byte[] HashKeyBytes = new byte[32];

    private void SetupTrackingSetting(bool enabled) =>
        _settingsRepo.Setup(r => r.GetSettingAsync(Constants.SettingKeys.ChangeTrackingConfigurationChangesEnabled))
            .ReturnsAsync(new ServiceSetting
            {
                Key = Constants.SettingKeys.ChangeTrackingConfigurationChangesEnabled,
                DisplayName = "Track configuration changes",
                ValueType = ServiceSettingValueType.Boolean,
                Value = enabled ? "true" : "false"
            });

    private void SetupHashKeySetting() =>
        _settingsRepo.Setup(r => r.GetSettingAsync(Constants.SettingKeys.ConfigurationChangeHashKey))
            .ReturnsAsync(new ServiceSetting
            {
                Key = Constants.SettingKeys.ConfigurationChangeHashKey,
                DisplayName = "Configuration change hash key",
                ValueType = ServiceSettingValueType.StringEncrypted,
                Value = _protection.Protect(Convert.ToBase64String(HashKeyBytes))
            });

    private void SetupMaxVersion(int max) =>
        _activityRepo.Setup(r => r.GetMaxConfigurationChangeVersionAsync(ActivityTargetType.PredefinedSearch, It.IsAny<int>()))
            .ReturnsAsync(max);

    private static PredefinedSearch BuildSearch(int id, string name) => new()
    {
        Id = id,
        Name = name,
        Uri = name.ToLowerInvariant().Replace(' ', '-'),
        BuiltIn = false,
        IsEnabled = true,
        MetaverseObjectType = new MetaverseObjectType { Id = 1, Name = "Person", PluralName = "People" },
        Attributes = [],
    };

    private static PredefinedSearchCriteria BuildCriterion(int id, int attributeId) => new()
    {
        Id = id,
        ComparisonType = SearchComparisonType.Equals,
        MetaverseAttributeId = attributeId,
        MetaverseAttribute = new MetaverseAttribute { Id = attributeId, Name = "JobTitle" },
        StringValue = "Engineer"
    };

    private static MetaverseObject NewUser() => new() { Id = Guid.NewGuid(), CachedDisplayName = "Admin User" };

    /// <summary>A round-trip credential-protection test double using a recognisable encrypted-value prefix.</summary>
    private sealed class FakeProtection : ICredentialProtectionService
    {
        private const string Prefix = "$JIM$v1$";

        public string? Protect(string? plainText) =>
            string.IsNullOrEmpty(plainText) ? plainText : Prefix + Convert.ToBase64String(Encoding.UTF8.GetBytes(plainText));

        public string? Unprotect(string? protectedData) =>
            string.IsNullOrEmpty(protectedData) || !IsProtected(protectedData)
                ? protectedData
                : Encoding.UTF8.GetString(Convert.FromBase64String(protectedData[Prefix.Length..]));

        public bool IsProtected(string? value) =>
            !string.IsNullOrEmpty(value) && value.StartsWith(Prefix, StringComparison.Ordinal);
    }
}
