// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application;
using JIM.Data;
using JIM.Data.Repositories;
using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Enums;
using JIM.Models.Logic;
using JIM.Models.Security;
using JIM.Models.Staging;
using Moq;
using NUnit.Framework;

namespace JIM.Worker.Tests.Servers;

/// <summary>
/// Manual removal of Accumulate values (#3059, Phase 3; PRD requirements 14 and 15). An administrator may remove a value
/// an Accumulate attribute has kept, but not one a live contributor still supplies, since the next synchronisation would
/// only add it back: that is refused, naming the contributor. A removal is recorded on the object's change history as the
/// person's, under an Activity, and goes through the same write path as any other edit outside synchronisation, so the
/// removal reaches the systems the object is exported to.
///
/// Topology: a Person with an Accumulate "Account Audit Log", fed by the HR Synchronisation Rule from HR's DisplayName.
/// The object holds "John Smith" (still HR's DisplayName) and "J Smith" (HR's DisplayName before a rename, kept by
/// Accumulate).
/// </summary>
[TestFixture]
public class MetaverseServerAccumulatedValueRemovalTests
{
    private const string CurrentName = "John Smith";
    private const string FormerName = "J Smith";

    private Mock<IRepository> _repository = null!;
    private Mock<IMetaverseRepository> _metaverseRepository = null!;
    private Mock<IConnectedSystemRepository> _connectedSystemRepository = null!;
    private Mock<IActivityRepository> _activityRepository = null!;
    private JIM.InMemoryData.SyncRepository _syncRepository = null!;
    private JimApplication _jim = null!;

    private MetaverseObjectType _personType = null!;
    private MetaverseAttribute _auditLog = null!;
    private MetaverseAttribute _department = null!;
    private ConnectedSystem _hr = null!;
    private ConnectedSystemObjectType _hrUserType = null!;
    private ConnectedSystemObjectTypeAttribute _hrDisplayName = null!;
    private SyncRule _hrImport = null!;
    private MetaverseObject _person = null!;
    private ConnectedSystemObject _hrAccount = null!;
    private MetaverseObject _administrator = null!;
    private readonly List<Activity> _activities = [];

    [SetUp]
    public void SetUp()
    {
        _repository = new Mock<IRepository>();
        _metaverseRepository = new Mock<IMetaverseRepository>();
        _connectedSystemRepository = new Mock<IConnectedSystemRepository>();
        _activityRepository = new Mock<IActivityRepository>();
        var serviceSettings = new Mock<IServiceSettingsRepository>();
        serviceSettings.Setup(r => r.GetSettingAsync(It.IsAny<string>())).ReturnsAsync((ServiceSetting?)null);

        _repository.Setup(r => r.Metaverse).Returns(_metaverseRepository.Object);
        _repository.Setup(r => r.ConnectedSystems).Returns(_connectedSystemRepository.Object);
        _repository.Setup(r => r.Activity).Returns(_activityRepository.Object);
        _repository.Setup(r => r.ServiceSettings).Returns(serviceSettings.Object);
        _activities.Clear();
        _activityRepository.Setup(r => r.CreateActivityAsync(It.IsAny<Activity>()))
            .Callback<Activity>(a =>
            {
                if (a.Id == Guid.Empty)
                    a.Id = Guid.NewGuid();
                _activities.Add(a);
            })
            .Returns(Task.CompletedTask);

        _syncRepository = new JIM.InMemoryData.SyncRepository();
        _jim = new JimApplication(_repository.Object, syncRepository: _syncRepository);

        _personType = new MetaverseObjectType { Id = 1, Name = "Person" };
        _auditLog = new MetaverseAttribute
        {
            Id = 10,
            Name = "Account Audit Log",
            Type = AttributeDataType.Text,
            AttributePlurality = AttributePlurality.MultiValued,
            ContributionMode = AttributeContributionMode.Accumulate
        };
        _department = new MetaverseAttribute
        {
            Id = 11,
            Name = "Department",
            Type = AttributeDataType.Text,
            AttributePlurality = AttributePlurality.SingleValued
        };
        _personType.Attributes.Add(_auditLog);
        _personType.Attributes.Add(_department);

        _hr = new ConnectedSystem { Id = 1, Name = "HR" };
        _hrDisplayName = new ConnectedSystemObjectTypeAttribute { Id = 1000, Name = "DisplayName", Type = AttributeDataType.Text };
        _hrUserType = new ConnectedSystemObjectType { Id = 100, Name = "HrUser", ConnectedSystemId = _hr.Id, Attributes = [_hrDisplayName] };
        _hrDisplayName.ConnectedSystemObjectType = _hrUserType;
        _hrImport = new SyncRule
        {
            Id = 50,
            Name = "HR Import",
            Direction = SyncRuleDirection.Import,
            ConnectedSystemId = _hr.Id,
            ConnectedSystem = _hr,
            ConnectedSystemObjectTypeId = _hrUserType.Id,
            ConnectedSystemObjectType = _hrUserType,
            MetaverseObjectTypeId = _personType.Id,
            MetaverseObjectType = _personType
        };
        _hrImport.AttributeFlowRules.Add(new SyncRuleMapping
        {
            Id = 500,
            SyncRule = _hrImport,
            SyncRuleId = _hrImport.Id,
            TargetMetaverseAttribute = _auditLog,
            TargetMetaverseAttributeId = _auditLog.Id,
            Sources = { new SyncRuleMappingSource { Order = 0, ConnectedSystemAttribute = _hrDisplayName, ConnectedSystemAttributeId = _hrDisplayName.Id } }
        });

        _person = new MetaverseObject { Id = Guid.NewGuid(), Type = _personType };
        _person.AttributeValues.Add(HrValue(CurrentName));
        _person.AttributeValues.Add(HrValue(FormerName));

        _hrAccount = new ConnectedSystemObject
        {
            Id = Guid.NewGuid(),
            ConnectedSystemId = _hr.Id,
            ConnectedSystem = _hr,
            TypeId = _hrUserType.Id,
            Type = _hrUserType,
            MetaverseObject = _person,
            MetaverseObjectId = _person.Id,
            Status = ConnectedSystemObjectStatus.Normal
        };
        _hrAccount.AttributeValues.Add(new ConnectedSystemObjectAttributeValue
        {
            Id = Guid.NewGuid(),
            Attribute = _hrDisplayName,
            AttributeId = _hrDisplayName.Id,
            StringValue = CurrentName,
            ConnectedSystemObject = _hrAccount
        });
        _syncRepository.SeedConnectedSystem(_hr);
        _syncRepository.SeedObjectType(_hrUserType);
        _syncRepository.SeedConnectedSystemObject(_hrAccount);

        _connectedSystemRepository.Setup(r => r.GetImportSyncRulesForMetaverseObjectTypeAsync(_personType.Id))
            .ReturnsAsync(() => [_hrImport]);
        _connectedSystemRepository.Setup(r => r.GetObjectTypesAsync(_hr.Id)).ReturnsAsync([_hrUserType]);
        _connectedSystemRepository.Setup(r => r.GetConnectedSystemCoreAsync(_hr.Id, It.IsAny<bool>())).ReturnsAsync(_hr);

        _administrator = new MetaverseObject { Id = Guid.NewGuid(), Type = _personType };
    }

    [TearDown]
    public void TearDown() => _jim?.Dispose();

    private MetaverseObjectAttributeValue HrValue(string value) => new()
    {
        Id = Guid.NewGuid(),
        Attribute = _auditLog,
        AttributeId = _auditLog.Id,
        MetaverseObject = _person,
        StringValue = value,
        ContributedBySyncRuleId = _hrImport.Id,
        ContributedBySystemId = _hr.Id,
        ContributedByKind = ValueContributorKind.SynchronisationRule
    };

    private Guid ValueIdOf(string value) => _person.AttributeValues.Single(av => av.StringValue == value).Id;

    private List<string?> AuditLog() => _person.AttributeValues.Where(av => av.AttributeId == _auditLog.Id).Select(av => av.StringValue).ToList();

    private void VerifyNothingWritten()
    {
        _metaverseRepository.Verify(r => r.RemoveMetaverseObjectAttributeValuesAsync(It.IsAny<MetaverseObject>(), It.IsAny<IReadOnlyCollection<Guid>>()), Times.Never);
        Assert.That(_activities, Is.Empty, "a refused removal records no Activity: nothing was done");
        Assert.That(_person.Changes, Is.Empty);
    }

    #region Removed

    /// <summary>
    /// PRD Scenario 6: the value HR no longer supplies is removed, recorded as the administrator's change under an
    /// Activity, and the value HR still supplies is left alone.
    /// </summary>
    [Test]
    public async Task RemoveAccumulatedValuesAsync_ValueNoContributorSupplies_RemovesAndRecordsThePersonsChangeAsync()
    {
        var removedId = ValueIdOf(FormerName);

        var result = await _jim.Metaverse.RemoveAccumulatedValuesAsync(_person, _auditLog.Id, [removedId], [], _administrator);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Outcome, Is.EqualTo(AttributeValueRemovalOutcome.Removed));
            Assert.That(AuditLog(), Is.EqualTo(new[] { CurrentName }));
            _metaverseRepository.Verify(r => r.RemoveMetaverseObjectAttributeValuesAsync(_person,
                It.Is<IReadOnlyCollection<Guid>>(ids => ids.SequenceEqual(new[] { removedId }))), Times.Once);

            var activity = _activities.Single();
            Assert.That(activity.TargetType, Is.EqualTo(ActivityTargetType.MetaverseObject));
            Assert.That(activity.TargetOperationType, Is.EqualTo(ActivityTargetOperationType.Update));
            Assert.That(activity.MetaverseObjectId, Is.EqualTo(_person.Id));
            Assert.That(activity.InitiatedByType, Is.EqualTo(ActivityInitiatorType.User));
            Assert.That(activity.InitiatedById, Is.EqualTo(_administrator.Id));
            Assert.That(activity.Status, Is.EqualTo(ActivityStatus.Complete));
            Assert.That(result.ActivityId, Is.EqualTo(activity.Id));

            var change = _person.Changes.Single();
            Assert.That(change.ActivityId, Is.EqualTo(activity.Id));
            Assert.That(change.InitiatedByType, Is.EqualTo(ActivityInitiatorType.User));
            Assert.That(change.InitiatedById, Is.EqualTo(_administrator.Id));
            Assert.That(change.ChangeInitiatorType, Is.EqualTo(MetaverseObjectChangeInitiatorType.User));
            var valueChange = change.AttributeChanges.Single().ValueChanges.Single();
            Assert.That(valueChange.ValueChangeType, Is.EqualTo(ValueChangeType.Remove));
            Assert.That(valueChange.StringValue, Is.EqualTo(FormerName));
        }
    }

    [Test]
    public async Task RemoveAccumulatedValuesAsync_ValueNamedByItsText_RemovesItAsync()
    {
        var result = await _jim.Metaverse.RemoveAccumulatedValuesAsync(_person, _auditLog.Id, [], [FormerName], _administrator);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Outcome, Is.EqualTo(AttributeValueRemovalOutcome.Removed));
            Assert.That(AuditLog(), Is.EqualTo(new[] { CurrentName }));
        }
    }

    [Test]
    public async Task RemoveAccumulatedValuesAsync_Removed_FlagsTheObjectSoTheRemovalReachesExportsAsync()
    {
        await _jim.Metaverse.RemoveAccumulatedValuesAsync(_person, _auditLog.Id, [ValueIdOf(FormerName)], [], _administrator);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_person.ScopeReviewPending, Is.True);
            Assert.That(_person.UnsynchronisedChangesJson, Does.Contain(FormerName));
        }
    }

    [Test]
    public async Task RemoveAccumulatedValuesAsync_ByApiKey_AttributesTheActivityAndChangeToTheKeyAsync()
    {
        var apiKey = new ApiKey { Id = Guid.NewGuid(), Name = "Clean-up automation" };

        await _jim.Metaverse.RemoveAccumulatedValuesAsync(_person, _auditLog.Id, [ValueIdOf(FormerName)], [], null, apiKey);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_activities.Single().InitiatedByType, Is.EqualTo(ActivityInitiatorType.ApiKey));
            Assert.That(_person.Changes.Single().InitiatedByType, Is.EqualTo(ActivityInitiatorType.ApiKey));
            Assert.That(_person.Changes.Single().InitiatedByName, Is.EqualTo("Clean-up automation"));
        }
    }

    [Test]
    public async Task RemoveAccumulatedValuesAsync_ContributorsRuleDisabled_RemovesTheValueItWouldSupplyAsync()
    {
        _hrImport.Enabled = false;

        var result = await _jim.Metaverse.RemoveAccumulatedValuesAsync(_person, _auditLog.Id, [ValueIdOf(CurrentName)], [], _administrator);

        Assert.That(result.Outcome, Is.EqualTo(AttributeValueRemovalOutcome.Removed), "a disabled rule adds nothing back");
    }

    [Test]
    public async Task RemoveAccumulatedValuesAsync_ContributorNoLongerJoined_RemovesTheValueItOnceSuppliedAsync()
    {
        _hrAccount.MetaverseObject = null;
        _hrAccount.MetaverseObjectId = null;
        _syncRepository = new JIM.InMemoryData.SyncRepository();
        _syncRepository.SeedConnectedSystem(_hr);
        _syncRepository.SeedObjectType(_hrUserType);
        _syncRepository.SeedConnectedSystemObject(_hrAccount);
        _jim.Dispose();
        _jim = new JimApplication(_repository.Object, syncRepository: _syncRepository);

        var result = await _jim.Metaverse.RemoveAccumulatedValuesAsync(_person, _auditLog.Id, [ValueIdOf(CurrentName)], [], _administrator);

        Assert.That(result.Outcome, Is.EqualTo(AttributeValueRemovalOutcome.Removed));
    }

    #endregion

    #region Refused

    /// <summary>
    /// PRD requirement 15: HR still supplies "John Smith", so removing it would only see the next synchronisation add it
    /// back. Refused, naming the rule and its Connected System, with nothing removed.
    /// </summary>
    [Test]
    public async Task RemoveAccumulatedValuesAsync_ValueALiveContributorSupplies_RefusesNamingTheContributorAsync()
    {
        var result = await _jim.Metaverse.RemoveAccumulatedValuesAsync(_person, _auditLog.Id,
            [ValueIdOf(CurrentName), ValueIdOf(FormerName)], [], _administrator);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Outcome, Is.EqualTo(AttributeValueRemovalOutcome.SuppliedByContributor));
            var supplier = result.Suppliers.Single();
            Assert.That(supplier.Value, Is.EqualTo(CurrentName));
            Assert.That(supplier.SyncRuleName, Is.EqualTo("HR Import"));
            Assert.That(supplier.ConnectedSystemName, Is.EqualTo("HR"));
            Assert.That(result.Refusal!.ToString(), Does.Contain("HR Import").And.Contain("HR").And.Contain(CurrentName));
            Assert.That(AuditLog(), Is.EquivalentTo(new[] { CurrentName, FormerName }), "all or nothing: the other value stays too");
        }
        VerifyNothingWritten();
    }

    [Test]
    public async Task RemoveAccumulatedValuesAsync_ContributorOutOfTheRulesScope_RemovesTheValueAsync()
    {
        var scopeFlag = new ConnectedSystemObjectTypeAttribute { Id = 1001, Name = "ScopeFlag", Type = AttributeDataType.Text, ConnectedSystemObjectType = _hrUserType };
        _hrUserType.Attributes.Add(scopeFlag);
        _hrImport.ObjectScopingCriteriaGroups.Add(new SyncRuleScopingCriteriaGroup
        {
            Type = JIM.Models.Search.SearchGroupType.All,
            Criteria =
            [
                new SyncRuleScopingCriteria
                {
                    ConnectedSystemAttribute = scopeFlag,
                    ComparisonType = JIM.Models.Search.SearchComparisonType.Equals,
                    StringValue = "InScope",
                    CaseSensitive = true
                }
            ]
        });

        var result = await _jim.Metaverse.RemoveAccumulatedValuesAsync(_person, _auditLog.Id, [ValueIdOf(CurrentName)], [], _administrator);

        Assert.That(result.Outcome, Is.EqualTo(AttributeValueRemovalOutcome.Removed), "an out-of-scope rule supplies nothing");
    }

    [Test]
    public async Task RemoveAccumulatedValuesAsync_PriorityAttribute_RefusesAsync()
    {
        _person.AttributeValues.Add(new MetaverseObjectAttributeValue
        {
            Id = Guid.NewGuid(), Attribute = _department, AttributeId = _department.Id, MetaverseObject = _person, StringValue = "Finance"
        });

        var result = await _jim.Metaverse.RemoveAccumulatedValuesAsync(_person, _department.Id, [], ["Finance"], _administrator);

        Assert.That(result.Outcome, Is.EqualTo(AttributeValueRemovalOutcome.NotAccumulate));
        VerifyNothingWritten();
    }

    [Test]
    public async Task RemoveAccumulatedValuesAsync_ValueNotHeld_RefusesAsync()
    {
        var result = await _jim.Metaverse.RemoveAccumulatedValuesAsync(_person, _auditLog.Id, [], ["Never held"], _administrator);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Outcome, Is.EqualTo(AttributeValueRemovalOutcome.ValueNotHeld));
            Assert.That(result.Refusal!.ToString(), Does.Contain("Never held"));
        }
        VerifyNothingWritten();
    }

    [Test]
    public async Task RemoveAccumulatedValuesAsync_NoValuesNamed_RefusesAsync()
    {
        var result = await _jim.Metaverse.RemoveAccumulatedValuesAsync(_person, _auditLog.Id, [], [], _administrator);

        Assert.That(result.Outcome, Is.EqualTo(AttributeValueRemovalOutcome.ValueNotHeld));
        VerifyNothingWritten();
    }

    [Test]
    public void RemoveAccumulatedValuesAsync_NoInitiator_Throws()
    {
        Assert.That(() => _jim.Metaverse.RemoveAccumulatedValuesAsync(_person, _auditLog.Id, [ValueIdOf(FormerName)], [], null),
            Throws.ArgumentException);
    }

    #endregion
}
