// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Text;
using JIM.Application;
using JIM.Application.Interfaces;
using JIM.Application.Services;
using JIM.Data;
using JIM.Data.Repositories;
using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Logic;
using JIM.Models.Staging;
using JIM.Models.Staging.DTOs;
using Moq;
using NUnit.Framework;

namespace JIM.Worker.Tests.Servers;

/// <summary>
/// The preflight of an Attribute Priority order says, before the save, which Connected System Object Types the save will
/// record as awaiting synchronisation (#2650), so the portal's confirmation can state when the change takes effect as a
/// Synchronisation Rule save's does. Its answer must be the save's own: each case evaluates an order, saves the same order
/// against the same stored state, and requires the preflight's list to equal what the save recorded, entry for entry.
/// The repositories are a small stateful fake, so the preflight reads the order as stored and the save reads back what it
/// wrote, as they do against the database.
/// </summary>
[TestFixture]
public class AttributePriorityChangePreflightTests
{
    private const int MetaverseObjectTypeId = 1;
    private const int MetaverseAttributeId = 42;
    private static readonly byte[] HashKey = new byte[32];

    private Mock<IRepository> _repo = null!;
    private Mock<IActivityRepository> _activityRepo = null!;
    private Mock<IServiceSettingsRepository> _settingsRepo = null!;
    private Mock<IConnectedSystemRepository> _csRepo = null!;
    private FakeProtection _protection = null!;
    private JimApplication _jim = null!;

    // The contributors as stored, by mapping id; the save's writes update them.
    private Dictionary<int, Contributor> _stored = null!;
    private Dictionary<int, string> _baselines = null!;
    private List<ConfigurationChangeSynchronisation> _recorded = null!;
    private int _mappingWrites;
    private int _activitiesCreated;

    [SetUp]
    public void SetUp()
    {
        TestUtilities.SetEnvironmentVariables();

        _stored = [];
        _baselines = [];
        _recorded = [];
        _mappingWrites = 0;
        _activitiesCreated = 0;

        _repo = new Mock<IRepository>();
        _repo.Setup(r => r.ConfigurationTests).Returns(TestUtilities.CreateEmptyConfigurationTestRepository());
        _activityRepo = new Mock<IActivityRepository>();
        _settingsRepo = new Mock<IServiceSettingsRepository>();
        _csRepo = new Mock<IConnectedSystemRepository>();
        _repo.Setup(r => r.Activity).Returns(_activityRepo.Object);
        _repo.Setup(r => r.ServiceSettings).Returns(_settingsRepo.Object);
        _repo.Setup(r => r.ConnectedSystems).Returns(_csRepo.Object);
        _repo.Setup(r => r.Metaverse).Returns(new Mock<IMetaverseRepository>().Object);

        _activityRepo.Setup(r => r.CreateActivityAsync(It.IsAny<Activity>()))
            .Callback<Activity>(a =>
            {
                if (a.Id == Guid.Empty)
                    a.Id = Guid.NewGuid();
                _activitiesCreated++;
            })
            .Returns(Task.CompletedTask);
        _activityRepo.Setup(r => r.UpdateActivityAsync(It.IsAny<Activity>())).Returns(Task.CompletedTask);
        _activityRepo.Setup(r => r.GetMaxConfigurationChangeVersionAsync(It.IsAny<ActivityTargetType>(), It.IsAny<int>())).ReturnsAsync(6);
        _activityRepo.Setup(r => r.GetLatestConfigurationChangeSnapshotAsync(ActivityTargetType.SynchronisationRule, It.IsAny<int>()))
            .ReturnsAsync((ActivityTargetType _, int ruleId) => _baselines.GetValueOrDefault(ruleId));

        _csRepo.Setup(r => r.GetImportSyncRuleMappingsForMetaverseAttributeAsync(MetaverseObjectTypeId, MetaverseAttributeId))
            .ReturnsAsync(() => _stored.Values.OrderBy(c => c.Priority).ThenBy(c => c.MappingId).Select(c => BuildRule(c).AttributeFlowRules[0]).ToList());
        _csRepo.Setup(r => r.GetSyncRuleAsync(It.IsAny<int>()))
            .ReturnsAsync((int ruleId) => _stored.Values.Where(c => c.RuleId == ruleId).Select(BuildRule).SingleOrDefault());
        _csRepo.Setup(r => r.UpdateSyncRuleMappingsAsync(It.IsAny<IReadOnlyCollection<SyncRuleMapping>>()))
            .Callback<IReadOnlyCollection<SyncRuleMapping>>(written =>
            {
                _mappingWrites++;
                foreach (var mapping in written)
                    _stored[mapping.Id] = _stored[mapping.Id] with { Priority = mapping.Priority, NullIsValue = mapping.NullIsValue };
            })
            .Returns(Task.CompletedTask);
        _csRepo.Setup(r => r.CreateConfigurationChangeSynchronisationAsync(It.IsAny<ConfigurationChangeSynchronisation>()))
            .Callback<ConfigurationChangeSynchronisation>(c => _recorded.Add(c))
            .Returns(Task.CompletedTask);
        // Each type's population, distinct so an entry estimated for the wrong type shows.
        _csRepo.Setup(r => r.GetConnectedSystemObjectCountAsync(It.IsAny<int>(), It.IsAny<int?>(), null))
            .ReturnsAsync((int connectedSystemId, int? objectTypeId, int? _) => connectedSystemId * 1000 + (objectTypeId ?? 0));

        _protection = new FakeProtection();
        _jim = new JimApplication(_repo.Object, syncRepository: TestUtilities.QueuedChangeWithdrawalSyncRepository()) { CredentialProtection = _protection };
        SetupHashKeySetting();
    }

    [TearDown]
    public void TearDown() => _jim?.Dispose();

    // -- the preflight agrees with the save ----------------------------------------------------------------------------

    private static IEnumerable<TestCaseData> AgreementCases()
    {
        yield return Case("an unchanged order records nothing",
            [Hr(priority: 1), Directory(priority: 2)],
            [(Hr().MappingId, false), (Directory().MappingId, false)],
            trackingEnabled: true,
            expectedObjectTypeIds: []);

        yield return Case("a swap of two import rules on different systems records both",
            [Hr(priority: 1), Directory(priority: 2)],
            [(Directory().MappingId, false), (Hr().MappingId, false)],
            trackingEnabled: true,
            expectedObjectTypeIds: [Directory().ObjectTypeId, Hr().ObjectTypeId]);

        yield return Case("a move that renumbers only some rules records only those",
            [Hr(priority: 1), Directory(priority: 2), Payroll(priority: 3)],
            [(Hr().MappingId, false), (Payroll().MappingId, false), (Directory().MappingId, false)],
            trackingEnabled: true,
            expectedObjectTypeIds: [Payroll().ObjectTypeId, Directory().ObjectTypeId]);

        yield return Case("a change to Null is a value alone records that rule",
            [Hr(priority: 1), Directory(priority: 2)],
            [(Hr().MappingId, true), (Directory().MappingId, false)],
            trackingEnabled: true,
            expectedObjectTypeIds: [Hr().ObjectTypeId]);

        yield return Case("a disabled rule is not recorded",
            [Hr(priority: 1), Directory(priority: 2, enabled: false)],
            [(Directory().MappingId, false), (Hr().MappingId, false)],
            trackingEnabled: true,
            expectedObjectTypeIds: [Hr().ObjectTypeId]);

        yield return Case("with change tracking off a disabled rule is recorded as well, since nothing was compared",
            [Hr(priority: 1), Directory(priority: 2, enabled: false)],
            [(Directory().MappingId, false), (Hr().MappingId, false)],
            trackingEnabled: false,
            expectedObjectTypeIds: [Directory().ObjectTypeId, Hr().ObjectTypeId]);

        yield return Case("an export rule among the contributors is not recorded",
            [Hr(priority: 1), ContosoExport(priority: 2)],
            [(ContosoExport().MappingId, false), (Hr().MappingId, false)],
            trackingEnabled: true,
            expectedObjectTypeIds: [Hr().ObjectTypeId]);
    }

    [TestCaseSource(nameof(AgreementCases))]
    public async Task EvaluateAttributePriorityOrderAsync_StatesExactlyWhatTheSaveRecordsAsync(
        Contributor[] stored, (int MappingId, bool NullIsValue)[] proposed, bool trackingEnabled, int[] expectedObjectTypeIds)
    {
        Store(stored, trackingEnabled);

        var preflight = await _jim.ConnectedSystems.EvaluateAttributePriorityOrderAsync(MetaverseObjectTypeId, MetaverseAttributeId, proposed);
        var stated = preflight.Effect?.ObjectTypesProcessedInFull.ToList() ?? [];

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_recorded, Is.Empty, "the preflight records nothing");
            Assert.That(_mappingWrites, Is.Zero, "the preflight writes no mapping");
            Assert.That(_activitiesCreated, Is.Zero, "the preflight creates no Activity");
        }

        await _jim.ConnectedSystems.SetAttributePriorityOrderAsync(MetaverseObjectTypeId, MetaverseAttributeId, proposed, NewUser());
        var recorded = await _jim.ConnectedSystems.TakeRecordedAwaitingSynchronisationAsync();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(stated.Select(Describe), Is.EqualTo(recorded.Select(Describe)), "the preflight states what the save recorded, entry for entry");
            Assert.That(stated.Select(e => e.ConnectedSystemObjectTypeId), Is.EquivalentTo(expectedObjectTypeIds));
            Assert.That(_recorded.Select(c => c.ConnectedSystemObjectTypeId), Is.EquivalentTo(expectedObjectTypeIds), "and the rows the save wrote");
        }
    }

    // -- what the preflight describes ----------------------------------------------------------------------------------

    [Test]
    public async Task EvaluateAttributePriorityOrderAsync_Swap_ListsEachChangedPriorityWithBothValuesAndAsksForAcknowledgementAsync()
    {
        Store([Hr(priority: 1), Directory(priority: 2)], trackingEnabled: true);

        var preflight = await _jim.ConnectedSystems.EvaluateAttributePriorityOrderAsync(MetaverseObjectTypeId, MetaverseAttributeId,
            [(Directory().MappingId, false), (Hr().MappingId, false)]);

        var hr = preflight.Items.Single(i => i.Label.Contains("HR Import"));
        var directory = preflight.Items.Single(i => i.Label.Contains("Directory Import"));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(preflight.RequiresAcknowledgement, Is.True);
            Assert.That(preflight.Items, Has.Count.EqualTo(2), "one item for each changed priority, nothing for an unchanged flag");
            Assert.That((hr.Key, hr.OldDisplayValue, hr.NewDisplayValue), Is.EqualTo(("priority", "1", "2")));
            Assert.That((directory.Key, directory.OldDisplayValue, directory.NewDisplayValue), Is.EqualTo(("priority", "2", "1")));
        }
    }

    [Test]
    public async Task EvaluateAttributePriorityOrderAsync_NullIsValueChanged_ListsTheFlagAndAsksForAcknowledgementAsync()
    {
        Store([Hr(priority: 1), Directory(priority: 2)], trackingEnabled: true);

        var preflight = await _jim.ConnectedSystems.EvaluateAttributePriorityOrderAsync(MetaverseObjectTypeId, MetaverseAttributeId,
            [(Hr().MappingId, true), (Directory().MappingId, false)]);

        var item = preflight.Items.Single();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(preflight.RequiresAcknowledgement, Is.True);
            Assert.That((item.Key, item.OldDisplayValue, item.NewDisplayValue), Is.EqualTo(("nullIsValue", "No", "Yes")));
            Assert.That(item.Label, Does.Contain("HR Import"));
        }
    }

    [Test]
    public async Task EvaluateAttributePriorityOrderAsync_UnchangedOrder_AsksForNothingAsync()
    {
        Store([Hr(priority: 1), Directory(priority: 2)], trackingEnabled: true);

        var preflight = await _jim.ConnectedSystems.EvaluateAttributePriorityOrderAsync(MetaverseObjectTypeId, MetaverseAttributeId,
            [(Hr().MappingId, false), (Directory().MappingId, false)]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(preflight.RequiresAcknowledgement, Is.False);
            Assert.That(preflight.Items, Is.Empty);
        }
    }

    [Test]
    public void EvaluateAttributePriorityOrderAsync_OrderThatMissesAContributor_IsRefusedAsTheSaveRefusesItAsync()
    {
        Store([Hr(priority: 1), Directory(priority: 2)], trackingEnabled: true);

        Assert.ThrowsAsync<ArgumentException>(async () =>
            await _jim.ConnectedSystems.EvaluateAttributePriorityOrderAsync(MetaverseObjectTypeId, MetaverseAttributeId, [(Hr().MappingId, false)]));
    }

    // -- helpers -------------------------------------------------------------------------------------------------------

    /// <summary>One contributing Attribute Flow and the Synchronisation Rule it belongs to, as stored.</summary>
    public sealed record Contributor(
        int MappingId, int RuleId, string RuleName, int Priority, bool NullIsValue, bool Enabled, SyncRuleDirection Direction,
        int ConnectedSystemId, string ConnectedSystemName, int ObjectTypeId, string ObjectTypeName);

    private static Contributor Hr(int priority = 1, bool enabled = true) =>
        new(10, 1000, "HR Import", priority, false, enabled, SyncRuleDirection.Import, 30, "HR", 70, "person");

    private static Contributor Directory(int priority = 2, bool enabled = true) =>
        new(20, 2000, "Directory Import", priority, false, enabled, SyncRuleDirection.Import, 40, "Directory", 80, "user");

    private static Contributor Payroll(int priority = 3) =>
        new(30, 3000, "Payroll Import", priority, false, true, SyncRuleDirection.Import, 50, "Payroll", 90, "employee");

    // The repository lists import rules only; an export rule is put among the contributors here to prove the preflight
    // excludes it by the same rule the save does, not by the query.
    private static Contributor ContosoExport(int priority = 2) =>
        new(40, 4000, "Contoso AD Export", priority, false, true, SyncRuleDirection.Export, 60, "Contoso AD", 100, "user");

    private static TestCaseData Case(string name, Contributor[] stored, (int MappingId, bool NullIsValue)[] proposed,
        bool trackingEnabled, int[] expectedObjectTypeIds) =>
        new TestCaseData(stored, proposed, trackingEnabled, expectedObjectTypeIds).SetArgDisplayNames(name);

    private void Store(Contributor[] contributors, bool trackingEnabled)
    {
        foreach (var contributor in contributors)
        {
            _stored[contributor.MappingId] = contributor;
            // The rule's change history as it stands before the change, so the capture compares against it.
            _baselines[contributor.RuleId] = ConfigurationSnapshotService.Serialise(_jim.ConfigurationSnapshots.CreateSnapshot(BuildRule(contributor), HashKey));
        }

        SetupTrackingSetting(trackingEnabled);
    }

    private static SyncRule BuildRule(Contributor contributor)
    {
        var rule = new SyncRule
        {
            Id = contributor.RuleId,
            Name = contributor.RuleName,
            Direction = contributor.Direction,
            Enabled = contributor.Enabled,
            MetaverseObjectTypeId = MetaverseObjectTypeId,
            ConnectedSystemId = contributor.ConnectedSystemId,
            ConnectedSystem = new ConnectedSystem { Id = contributor.ConnectedSystemId, Name = contributor.ConnectedSystemName },
            ConnectedSystemObjectTypeId = contributor.ObjectTypeId,
            ConnectedSystemObjectType = new ConnectedSystemObjectType
            {
                Id = contributor.ObjectTypeId,
                Name = contributor.ObjectTypeName,
                ConnectedSystemId = contributor.ConnectedSystemId
            }
        };
        rule.AttributeFlowRules =
        [
            new SyncRuleMapping
            {
                Id = contributor.MappingId,
                Priority = contributor.Priority,
                NullIsValue = contributor.NullIsValue,
                SyncRuleId = contributor.RuleId,
                SyncRule = rule,
                TargetMetaverseAttributeId = MetaverseAttributeId,
                TargetMetaverseAttribute = new MetaverseAttribute { Id = MetaverseAttributeId, Name = "department", Type = AttributeDataType.Text }
            }
        ];
        return rule;
    }

    private static string Describe(AwaitingSynchronisationEstimate estimate) =>
        $"{estimate.ConnectedSystemId}:{estimate.ConnectedSystemName}/{estimate.ConnectedSystemObjectTypeId}:{estimate.ObjectTypeName} " +
        $"{estimate.ObjectCount} {estimate.EstimatedDuration}";

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
                Value = _protection.Protect(Convert.ToBase64String(HashKey))
            });

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
