// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System;
using JIM.Models.Activities;
using NUnit.Framework;

namespace JIM.Models.Tests;

/// <summary>
/// Tests <see cref="Activity.SetConfigurationTargetId(ActivityTargetType,int)"/> and its Guid- and string-keyed
/// overloads: the single place that maps a configuration target type to the Activity column its change history is
/// keyed on. The mapping must mirror the repository's ConfigurationChangeQuery routing; a target type supported
/// here but not there (or vice versa) breaks history retrieval for that type.
/// </summary>
[TestFixture]
public class ActivityConfigurationTargetTests
{
    [Test]
    public void SetConfigurationTargetId_IntKeyedTypes_SetTheMatchingColumn()
    {
        foreach (var (targetType, getter) in new (ActivityTargetType TargetType, Func<Activity, int?> Getter)[]
        {
            (ActivityTargetType.ConnectedSystem, a => a.ConnectedSystemId),
            (ActivityTargetType.SyncRule, a => a.SyncRuleId),
            (ActivityTargetType.MetaverseAttribute, a => a.MetaverseAttributeId),
            (ActivityTargetType.MetaverseObjectType, a => a.MetaverseObjectTypeId),
            (ActivityTargetType.PredefinedSearch, a => a.PredefinedSearchId),
            (ActivityTargetType.Role, a => a.RoleId),
            (ActivityTargetType.ConnectorDefinition, a => a.ConnectorDefinitionId),
            (ActivityTargetType.ExampleDataTemplate, a => a.ExampleDataTemplateId),
            (ActivityTargetType.ExampleDataSet, a => a.ExampleDataSetId)
        })
        {
            var activity = new Activity();
            activity.SetConfigurationTargetId(targetType, 42);
            Assert.That(getter(activity), Is.EqualTo(42), $"'{targetType}' must set its own target column");
        }
    }

    [Test]
    public void SetConfigurationTargetId_GuidKeyedTypes_SetTheMatchingColumn()
    {
        var id = Guid.NewGuid();
        foreach (var (targetType, getter) in new (ActivityTargetType TargetType, Func<Activity, Guid?> Getter)[]
        {
            (ActivityTargetType.Schedule, a => a.ScheduleId),
            (ActivityTargetType.TrustedCertificate, a => a.TrustedCertificateId),
            (ActivityTargetType.ApiKey, a => a.ApiKeyId)
        })
        {
            var activity = new Activity();
            activity.SetConfigurationTargetId(targetType, id);
            Assert.That(getter(activity), Is.EqualTo(id), $"'{targetType}' must set its own target column");
        }
    }

    [Test]
    public void SetConfigurationTargetId_StringKeyedTypes_SetTheMatchingColumn()
    {
        var activity = new Activity();
        activity.SetConfigurationTargetId(ActivityTargetType.ServiceSetting, "History.RetentionPeriod");
        Assert.That(activity.ServiceSettingKey, Is.EqualTo("History.RetentionPeriod"));
    }

    [Test]
    public void SetConfigurationTargetId_TargetAlreadySet_PreservesTheExistingValue()
    {
        // Matches the established ??= capture semantics: an activity whose target column was set at creation time
        // (e.g. by a granular sub-entity endpoint) must not have it overwritten at capture time.
        var activity = new Activity { ConnectedSystemId = 7 };
        activity.SetConfigurationTargetId(ActivityTargetType.ConnectedSystem, 42);
        Assert.That(activity.ConnectedSystemId, Is.EqualTo(7));
    }

    [Test]
    public void SetConfigurationTargetId_OnlySetsTheTargetTypesOwnColumn()
    {
        var activity = new Activity();
        activity.SetConfigurationTargetId(ActivityTargetType.PredefinedSearch, 42);
        Assert.Multiple(() =>
        {
            Assert.That(activity.PredefinedSearchId, Is.EqualTo(42));
            Assert.That(activity.ConnectedSystemId, Is.Null);
            Assert.That(activity.SyncRuleId, Is.Null);
            Assert.That(activity.RoleId, Is.Null);
            Assert.That(activity.ServiceSettingKey, Is.Null);
        });
    }

    [Test]
    public void SetConfigurationTargetId_WrongKeyShape_Throws()
    {
        var activity = new Activity();
        Assert.Multiple(() =>
        {
            Assert.That(() => activity.SetConfigurationTargetId(ActivityTargetType.Schedule, 42),
                Throws.TypeOf<ArgumentOutOfRangeException>(), "Schedule is Guid-keyed, not integer-keyed");
            Assert.That(() => activity.SetConfigurationTargetId(ActivityTargetType.SyncRule, Guid.NewGuid()),
                Throws.TypeOf<ArgumentOutOfRangeException>(), "SyncRule is integer-keyed, not Guid-keyed");
            Assert.That(() => activity.SetConfigurationTargetId(ActivityTargetType.ConnectedSystem, "key"),
                Throws.TypeOf<ArgumentOutOfRangeException>(), "ConnectedSystem is integer-keyed, not string-keyed");
        });
    }
}
