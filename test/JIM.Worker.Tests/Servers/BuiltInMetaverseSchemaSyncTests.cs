// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System.Text;
using JIM.Application;
using JIM.Application.Interfaces;
using JIM.Data;
using JIM.Data.Repositories;
using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Security;
using Moq;
using NUnit.Framework;

namespace JIM.Worker.Tests.Servers;

/// <summary>
/// Tests the built-in Metaverse schema synchronisation pass (issue #1104). SeedAsync short-circuits once
/// ServiceSettings exists, so newly-introduced built-in Metaverse Attributes (and their advisory Standard
/// Mappings) would never reach an existing deployment without a startup pass that converges the database
/// towards <see cref="BuiltInMetaverseSchema"/>: creating missing built-in attributes (with a System-attributed
/// baseline under the System Initialisation Activity), adding missing Object Type bindings, and reconciling
/// Standard Mappings. The pass must be idempotent: a fully-converged database results in no writes and no
/// Activities.
/// </summary>
[TestFixture]
public class BuiltInMetaverseSchemaSyncTests
{
    private static readonly string[] GapAttributeNames =
    {
        Constants.BuiltInAttributes.Emails,
        Constants.BuiltInAttributes.AccountEnabled,
        Constants.BuiltInAttributes.Nickname,
        Constants.BuiltInAttributes.PreferredLanguage,
        Constants.BuiltInAttributes.Locale,
        Constants.BuiltInAttributes.TimeZone,
        Constants.BuiltInAttributes.MiddleName,
        Constants.BuiltInAttributes.HonorificPrefix,
        Constants.BuiltInAttributes.HonorificSuffix
    };

    private Mock<IRepository> _repo = null!;
    private Mock<IActivityRepository> _activityRepo = null!;
    private Mock<IServiceSettingsRepository> _settingsRepo = null!;
    private Mock<IMetaverseRepository> _metaverseRepo = null!;
    private Mock<ISeedingRepository> _seedingRepo = null!;
    private FakeProtection _protection = null!;
    private JimApplication _jim = null!;
    private List<Activity> _createdActivities = null!;
    private List<MetaverseAttribute>? _savedNewAttributes;
    private int _saveCallCount;
    private MetaverseObjectType _userObjectType = null!;
    private MetaverseObjectType _groupObjectType = null!;
    private List<MetaverseAttribute> _existingAttributes = null!;

    [SetUp]
    public void SetUp()
    {
        TestUtilities.SetEnvironmentVariables();

        _repo = new Mock<IRepository>();
        _activityRepo = new Mock<IActivityRepository>();
        _settingsRepo = new Mock<IServiceSettingsRepository>();
        _metaverseRepo = new Mock<IMetaverseRepository>();
        _seedingRepo = new Mock<ISeedingRepository>();
        _repo.Setup(r => r.Activity).Returns(_activityRepo.Object);
        _repo.Setup(r => r.ServiceSettings).Returns(_settingsRepo.Object);
        _repo.Setup(r => r.Metaverse).Returns(_metaverseRepo.Object);
        _repo.Setup(r => r.Seeding).Returns(_seedingRepo.Object);

        _createdActivities = new List<Activity>();
        _savedNewAttributes = null;
        _saveCallCount = 0;

        _activityRepo.Setup(r => r.CreateActivityAsync(It.IsAny<Activity>()))
            .Callback<Activity>(a => _createdActivities.Add(a))
            .Returns(Task.CompletedTask);
        _activityRepo.Setup(r => r.UpdateActivityAsync(It.IsAny<Activity>()))
            .Returns(Task.CompletedTask);
        _activityRepo.Setup(r => r.GetMaxConfigurationChangeVersionAsync(ActivityTargetType.MetaverseAttribute, It.IsAny<int>()))
            .ReturnsAsync(0);

        _seedingRepo.Setup(r => r.SaveBuiltInSchemaChangesAsync(It.IsAny<List<MetaverseAttribute>>()))
            .Callback<List<MetaverseAttribute>>(attributes =>
            {
                _savedNewAttributes = attributes;
                _saveCallCount++;
            })
            .Returns(Task.CompletedTask);

        _metaverseRepo.Setup(r => r.GetMetaverseAttributeWithObjectTypesAsync(It.IsAny<int>(), It.IsAny<bool>()))
            .ReturnsAsync((int id, bool _) => new MetaverseAttribute { Id = id, Name = "Baseline", BuiltIn = true });

        _protection = new FakeProtection();
        _jim = new JimApplication(_repo.Object) { CredentialProtection = _protection };

        SetupTrackingSetting(enabled: true);
        SetupHashKeySetting();
    }

    [TearDown]
    public void TearDown() => _jim?.Dispose();

    [Test]
    public async Task SyncBuiltInMetaverseSchemaAsync_GapAttributesMissing_CreatesBindsAndBaselinesThemAsync()
    {
        SetupDatabaseState(includeGapAttributes: false, includeStandardMappings: true);

        await _jim.Seeding.SyncBuiltInMetaverseSchemaAsync();

        Assert.That(_savedNewAttributes, Is.Not.Null, "the schema changes must be persisted");
        Assert.That(_savedNewAttributes!.Select(a => a.Name), Is.EquivalentTo(GapAttributeNames),
            "exactly the missing catalogue attributes must be created");
        foreach (var attribute in _savedNewAttributes)
        {
            Assert.That(attribute.BuiltIn, Is.True, $"{attribute.Name} must be created as built-in");
            Assert.That(attribute.CreatedByType, Is.EqualTo(ActivityInitiatorType.System), $"{attribute.Name} must be attributed to System");
        }

        // the shapes must come from the catalogue
        var emails = _savedNewAttributes.Single(a => a.Name == Constants.BuiltInAttributes.Emails);
        Assert.That(emails.Type, Is.EqualTo(AttributeDataType.Text));
        Assert.That(emails.AttributePlurality, Is.EqualTo(AttributePlurality.MultiValued));
        var accountEnabled = _savedNewAttributes.Single(a => a.Name == Constants.BuiltInAttributes.AccountEnabled);
        Assert.That(accountEnabled.Type, Is.EqualTo(AttributeDataType.Boolean));

        // bindings: the new attributes must be added to the tracked built-in Metaverse Object Types per the catalogue
        Assert.That(_userObjectType.Attributes.Any(a => a.Name == Constants.BuiltInAttributes.Emails), Is.True,
            "Emails must be bound to the User Metaverse Object Type");
        Assert.That(_groupObjectType.Attributes.Any(a => a.Name == Constants.BuiltInAttributes.Emails), Is.True,
            "Emails must be bound to the Group Metaverse Object Type");
        Assert.That(_userObjectType.Attributes.Any(a => a.Name == Constants.BuiltInAttributes.AccountEnabled), Is.True,
            "Account Enabled must be bound to the User Metaverse Object Type");
        Assert.That(_groupObjectType.Attributes.Any(a => a.Name == Constants.BuiltInAttributes.AccountEnabled), Is.False,
            "Account Enabled must not be bound to the Group Metaverse Object Type");

        // advisory Standard Mappings must be seeded with the new attributes
        Assert.That(emails.StandardMappings.Any(m => m.Standard == AttributeStandard.Scim && m.CounterpartName == "emails"), Is.True);
        Assert.That(accountEnabled.StandardMappings.Any(m => m.Standard == AttributeStandard.Scim && m.CounterpartName == "active"), Is.True);

        // each created attribute must be baselined: a System-attributed Create Activity grouped under a single
        // System Initialisation parent, matching the batch-seed baseline pattern
        var parentActivity = _createdActivities.SingleOrDefault(a => a.TargetType == ActivityTargetType.SystemInitialisation);
        Assert.That(parentActivity, Is.Not.Null, "creating built-in attributes must record a System Initialisation parent Activity");
        var attributeActivities = _createdActivities.Where(a => a.TargetType == ActivityTargetType.MetaverseAttribute).ToList();
        Assert.That(attributeActivities.Select(a => a.TargetName), Is.EquivalentTo(GapAttributeNames));
        foreach (var activity in attributeActivities)
        {
            Assert.That(activity.TargetOperationType, Is.EqualTo(ActivityTargetOperationType.Create));
            Assert.That(activity.ParentActivityId, Is.EqualTo(parentActivity!.Id));
        }
    }

    [Test]
    public async Task SyncBuiltInMetaverseSchemaAsync_StandardMappingsMissing_AddsThemWithoutCreatingAttributesAsync()
    {
        SetupDatabaseState(includeGapAttributes: true, includeStandardMappings: false);

        await _jim.Seeding.SyncBuiltInMetaverseSchemaAsync();

        Assert.That(_saveCallCount, Is.EqualTo(1), "the mapping additions must be persisted");
        Assert.That(_savedNewAttributes, Is.Empty, "no attributes must be created when they all already exist");

        var firstName = _existingAttributes.Single(a => a.Name == Constants.BuiltInAttributes.FirstName);
        Assert.That(firstName.StandardMappings.Any(m => m.Standard == AttributeStandard.Scim && m.CounterpartName == "name.givenName"), Is.True,
            "First Name must gain its SCIM Standard Mapping");
        Assert.That(firstName.StandardMappings.Any(m => m.Standard == AttributeStandard.Ldap && m.CounterpartName == "givenName"), Is.True,
            "First Name must gain its LDAP Standard Mapping");

        Assert.That(_createdActivities, Is.Empty,
            "reconciling advisory Standard Mappings is repository-direct, matching the connector definition sync precedent");
    }

    [Test]
    public async Task SyncBuiltInMetaverseSchemaAsync_DatabaseFullyConverged_DoesNothingAsync()
    {
        SetupDatabaseState(includeGapAttributes: true, includeStandardMappings: true);

        await _jim.Seeding.SyncBuiltInMetaverseSchemaAsync();

        _seedingRepo.Verify(r => r.SaveBuiltInSchemaChangesAsync(It.IsAny<List<MetaverseAttribute>>()), Times.Never,
            "a converged database must result in no writes");
        Assert.That(_createdActivities, Is.Empty, "a converged database must record no Activities");
    }

    [Test]
    public async Task SyncBuiltInMetaverseSchemaAsync_StaleStandardMapping_RemovesItAsync()
    {
        SetupDatabaseState(includeGapAttributes: true, includeStandardMappings: true);
        var firstName = _existingAttributes.Single(a => a.Name == Constants.BuiltInAttributes.FirstName);
        firstName.StandardMappings.Add(new MetaverseAttributeStandardMapping
        {
            Standard = AttributeStandard.Ldap,
            CounterpartName = "obsoleteName"
        });

        await _jim.Seeding.SyncBuiltInMetaverseSchemaAsync();

        Assert.That(_saveCallCount, Is.EqualTo(1), "removing the stale mapping must be persisted");
        Assert.That(firstName.StandardMappings.Any(m => m.CounterpartName == "obsoleteName"), Is.False,
            "a Standard Mapping no longer in the catalogue must be removed from a built-in attribute");
        Assert.That(firstName.StandardMappings.Any(m => m.Standard == AttributeStandard.Ldap && m.CounterpartName == "givenName"), Is.True,
            "catalogue mappings must be retained");
    }

    [Test]
    public async Task SyncBuiltInMetaverseSchemaAsync_MissingBinding_AddsItWithoutCreatingAttributeAsync()
    {
        SetupDatabaseState(includeGapAttributes: true, includeStandardMappings: true);
        var emails = _existingAttributes.Single(a => a.Name == Constants.BuiltInAttributes.Emails);
        _groupObjectType.Attributes.Remove(emails);

        await _jim.Seeding.SyncBuiltInMetaverseSchemaAsync();

        Assert.That(_saveCallCount, Is.EqualTo(1), "the binding addition must be persisted");
        Assert.That(_savedNewAttributes, Is.Empty);
        Assert.That(_groupObjectType.Attributes.Any(a => a.Name == Constants.BuiltInAttributes.Emails), Is.True,
            "the missing Group binding for Emails must be restored");
    }

    [Test]
    public async Task SyncBuiltInMetaverseSchemaAsync_CustomAttributeUsesBuiltInName_SkipsDefinitionWithoutMutatingItAsync()
    {
        // a customer created a custom attribute named like a newly-introduced built-in before upgrading.
        // the pass must not adopt it: no forced bindings, no mapping reconciliation, no shape changes.
        SetupDatabaseState(includeGapAttributes: true, includeStandardMappings: true);
        var nickname = _existingAttributes.Single(a => a.Name == Constants.BuiltInAttributes.Nickname);
        nickname.BuiltIn = false;
        nickname.Type = AttributeDataType.Number;
        _userObjectType.Attributes.Remove(nickname);
        var customMapping = new MetaverseAttributeStandardMapping { Standard = AttributeStandard.Ldap, CounterpartName = "customerCounterpart" };
        nickname.StandardMappings.Clear();
        nickname.StandardMappings.Add(customMapping);

        await _jim.Seeding.SyncBuiltInMetaverseSchemaAsync();

        _seedingRepo.Verify(r => r.SaveBuiltInSchemaChangesAsync(It.IsAny<List<MetaverseAttribute>>()), Times.Never,
            "the colliding definition must be skipped entirely, leaving nothing to persist");
        Assert.That(nickname.BuiltIn, Is.False, "the customer's attribute must not be converted to built-in");
        Assert.That(nickname.Type, Is.EqualTo(AttributeDataType.Number), "the customer's attribute shape must not be changed");
        Assert.That(_userObjectType.Attributes, Does.Not.Contain(nickname), "the customer's attribute must not be force-bound");
        Assert.That(nickname.StandardMappings, Is.EquivalentTo(new[] { customMapping }),
            "the customer's Standard Mappings must not be reconciled to the catalogue");
        Assert.That(_createdActivities, Is.Empty);
    }

    [Test]
    public async Task SyncBuiltInMetaverseSchemaAsync_CaseVariantDuplicateAttributeNames_DoesNotThrowAsync()
    {
        // attribute names are unique case-insensitively at the application layer, but no database constraint
        // enforces it; a pre-existing anomaly must not crash startup.
        SetupDatabaseState(includeGapAttributes: true, includeStandardMappings: true);
        _existingAttributes.Add(new MetaverseAttribute { Id = 9999, Name = "EMAILS", Type = AttributeDataType.Text, BuiltIn = false });

        await _jim.Seeding.SyncBuiltInMetaverseSchemaAsync();

        _seedingRepo.Verify(r => r.SaveBuiltInSchemaChangesAsync(It.IsAny<List<MetaverseAttribute>>()), Times.Never,
            "the converged built-in attribute must win the name and the anomaly must not trigger writes");
    }

    // -- helpers -------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Builds the tracked database state the sync pass reads, derived from the catalogue so the fixtures stay
    /// current as the catalogue evolves. Gap attributes and Standard Mappings can be withheld to simulate an
    /// existing deployment that predates them.
    /// </summary>
    private void SetupDatabaseState(bool includeGapAttributes, bool includeStandardMappings)
    {
        _userObjectType = new MetaverseObjectType { Id = 1, Name = Constants.BuiltInObjectTypes.User, PluralName = "Users", BuiltIn = true, Attributes = new List<MetaverseAttribute>() };
        _groupObjectType = new MetaverseObjectType { Id = 2, Name = Constants.BuiltInObjectTypes.Group, PluralName = "Groups", BuiltIn = true, Attributes = new List<MetaverseAttribute>() };
        _existingAttributes = new List<MetaverseAttribute>();

        var nextId = 1;
        foreach (var definition in BuiltInMetaverseSchema.Attributes)
        {
            if (!includeGapAttributes && GapAttributeNames.Contains(definition.Name))
                continue;

            var attribute = new MetaverseAttribute
            {
                Id = nextId++,
                Name = definition.Name,
                Type = definition.Type,
                AttributePlurality = definition.Plurality,
                RenderingHint = definition.RenderingHint,
                BuiltIn = true
            };

            if (includeStandardMappings)
            {
                foreach (var mapping in definition.StandardMappings)
                {
                    attribute.StandardMappings.Add(new MetaverseAttributeStandardMapping
                    {
                        MetaverseAttributeId = attribute.Id,
                        Standard = mapping.Standard,
                        CounterpartName = mapping.CounterpartName,
                        Notes = mapping.Notes
                    });
                }
            }

            _existingAttributes.Add(attribute);
            if (definition.ObjectTypeNames.Contains(Constants.BuiltInObjectTypes.User))
                _userObjectType.Attributes.Add(attribute);
            if (definition.ObjectTypeNames.Contains(Constants.BuiltInObjectTypes.Group))
                _groupObjectType.Attributes.Add(attribute);
        }

        _metaverseRepo.Setup(r => r.GetBuiltInMetaverseObjectTypesForSchemaSyncAsync())
            .ReturnsAsync(() => new List<MetaverseObjectType> { _userObjectType, _groupObjectType });
        _metaverseRepo.Setup(r => r.GetMetaverseAttributesForSchemaSyncAsync())
            .ReturnsAsync(() => _existingAttributes);
    }

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
                Value = _protection.Protect(Convert.ToBase64String(new byte[32]))
            });

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